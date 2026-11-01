using System.Collections.Concurrent;
using System.Net;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

internal sealed record DavRequest(string Method, string Path, string? Destination, string? Overwrite);

/// <summary>In-memory WebDAV endpoint: records every request and answers like a cooperative server.</summary>
internal sealed class FakeWebDavServer
{
    public FakeWebDavServer()
    {
        // MoveUnsupported is remembered per server URL, so every fake server gets its own host.
        Url = $"https://dav-{Guid.NewGuid():N}.test/dav";
    }

    public string Url { get; }
    public ConcurrentQueue<DavRequest> Requests { get; } = new();

    /// <summary>Optional override; return null to fall back to the default answer.</summary>
    public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? Respond { get; set; }

    public IEnumerable<DavRequest> Puts => Requests.Where(r => r.Method == "PUT");

    public BackupService CreateBackupService(string? snapshotRoot = null) =>
        new((url, password) => new WebDavClient(url, "", password, messageHandler: new Handler(this)), snapshotRoot);

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        var destination = Header("Destination") is { } d ? Uri.UnescapeDataString(new Uri(d).AbsolutePath) : null;
        Requests.Enqueue(new DavRequest(request.Method.Method, Uri.UnescapeDataString(request.RequestUri!.AbsolutePath),
            destination, Header("Overwrite")));
        if (request.Content is not null)
            await request.Content.ReadAsByteArrayAsync(ct);
        if (Respond is not null && await Respond(request) is { } custom)
            return custom;
        return request.Method.Method switch
        {
            "PROPFIND" => new HttpResponseMessage((HttpStatusCode)207)
            {
                Content = new StringContent("<d:multistatus xmlns:d=\"DAV:\"></d:multistatus>")
            },
            "MKCOL" or "PUT" or "MOVE" => new HttpResponseMessage(HttpStatusCode.Created),
            "DELETE" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        };
    }

    private sealed class Handler(FakeWebDavServer server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            server.HandleAsync(request, ct);
    }
}

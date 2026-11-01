using System.Net;
using System.Text;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public sealed class WebDavClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_dav_{Guid.NewGuid():N}");

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Listing(string body) => new((HttpStatusCode)207)
    { Content = new StringContent($"<d:multistatus xmlns:d=\"DAV:\">{body}</d:multistatus>", Encoding.UTF8, "application/xml") };

    private static string Entry(string href, bool directory = false) => $"""
        <d:response><d:href>{href}</d:href><d:propstat><d:prop>
        <d:displayname>Friendly display name</d:displayname>
        <d:resourcetype>{(directory ? "<d:collection/>" : "")}</d:resourcetype>
        </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        """;

    [Fact]
    public async Task Download_EncodedSelfAndNestedCollectionWithoutSlash_UsesActualFilenames()
    {
        using var client = new WebDavClient("https://example.test/dav", "", "", messageHandler: new Handler(req =>
        {
            if (req.Method.Method == "GET")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("save data") };
            var path = req.RequestUri!.AbsolutePath;
            return path.EndsWith("/slot")
                ? Listing(Entry(path, true) + Entry(path + "/%E5%AD%98%E6%A1%A3.dat"))
                : Listing(Entry("https://example.test/dav/%E6%B8%B8%E6%88%8F", true) + Entry(path + "/slot", true));
        }));
        Assert.Equal(1, await client.DownloadDirAsync("游戏", _root));
        Assert.Equal("save data", await File.ReadAllTextAsync(Path.Combine(_root, "slot", "存档.dat")));
    }

    [Fact]
    public async Task Restore_PartialDownloadFailure_PreservesExistingSave()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "original.dat"), "original");
        var service = new BackupService((url, password) => new WebDavClient(url, "", password,
            messageHandler: new Handler(req => req.Method.Method == "PROPFIND"
                ? Listing(Entry(req.RequestUri!.AbsolutePath, true) + Entry(req.RequestUri.AbsolutePath + "/ok.dat") + Entry(req.RequestUri.AbsolutePath + "/failed.dat"))
                : req.RequestUri!.AbsolutePath.EndsWith("failed.dat")
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("new data") })));
        await Assert.ThrowsAnyAsync<IOException>(() => service.RestoreFromWebDavAsync(
            new GameConfig { Name = "game", Save = _root }, "save_test",
            new AppConfig { WebDavUrl = "https://example.test/dav" }, "", _ => { }));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_root, "original.dat")));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    public async Task Download_ListingFailure_IsReported(int status)
    {
        using var client = new WebDavClient("https://example.test/dav", "", "",
            messageHandler: new Handler(_ => new HttpResponseMessage((HttpStatusCode)status)));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadDirAsync("game", _root));
    }

    [Fact]
    public async Task Download_MalformedListing_IsReported()
    {
        using var client = new WebDavClient("https://example.test/dav", "", "",
            messageHandler: new Handler(_ => new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent("broken xml") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadDirAsync("game", _root));
    }

    [Fact]
    public async Task Download_UnreadableEntry_IsReportedInsteadOfOmitted()
    {
        using var client = new WebDavClient("https://example.test/dav", "", "",
            messageHandler: new Handler(_ => Listing(Entry("/dav/game/readable.dat") + """
                <d:response><d:href>/dav/game/missing.dat</d:href><d:propstat>
                <d:prop/><d:status>HTTP/1.1 403 Forbidden</d:status></d:propstat></d:response>
                """)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadDirAsync("game", _root));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task ListDir_EncodedBasePath_ExcludesItselfAndDecodesChildName()
    {
        using var client = new WebDavClient("https://example.test/%E4%BA%91%E7%9B%98", "", "",
            messageHandler: new Handler(_ => Listing(Entry("/%E4%BA%91%E7%9B%98/", true) + Entry("/%E4%BA%91%E7%9B%98/%E6%B8%B8%E6%88%8F", true))));
        var names = await client.ListDirAsync("");
        Assert.Equal(new[] { "游戏" }, names);
    }

    [Fact]
    public async Task Download_EncodedPathTraversal_IsRejected()
    {
        using var client = new WebDavClient("https://example.test/dav", "", "",
            messageHandler: new Handler(_ => Listing(Entry("/dav/game/..%5Coutside.dat"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadDirAsync("game", _root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

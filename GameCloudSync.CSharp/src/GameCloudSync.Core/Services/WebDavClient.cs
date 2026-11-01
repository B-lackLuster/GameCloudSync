using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Services;

/// <summary>
/// WebDAV client supporting PROPFIND / MKCOL / PUT / GET / DELETE with
/// parallel directory upload/download (mirrors the Python stdlib client).
/// </summary>
public sealed class WebDavClient : IDisposable
{
    private static readonly XNamespace DavNs = "DAV:";
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _urlPath;
    private readonly Action<string>? _log;
    private readonly Action<string>? _debug;

    public WebDavClient(string url, string username, string password,
        Action<string>? log = null, Action<string>? debug = null,
        int timeoutSeconds = 30, HttpMessageHandler? messageHandler = null)
    {
        _baseUrl = url.TrimEnd('/');
        _urlPath = Uri.UnescapeDataString(new Uri(_baseUrl).AbsolutePath).TrimEnd('/');
        _log = log;
        _debug = debug;

        var handler = messageHandler ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameCloudSync/2.0");
        if (!string.IsNullOrEmpty(username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    public string BaseUrl => _baseUrl;

    public void Dispose() => _http.Dispose();

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        var (status, _) = await RequestAsync("PROPFIND", "", extraHeaders: new Dictionary<string, string> { ["Depth"] = "0" }, ct: ct);
        return status is 207 or 200;
    }

    /// <summary>Creates a remote directory (recursively).</summary>
    public async Task<bool> MkColAsync(string path, CancellationToken ct = default)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current = current.Length == 0 ? part : $"{current}/{part}";
            var (status, _) = await RequestAsync("MKCOL", current, ct: ct);
            if (status is not (201 or 405 or 301))
                return false;
        }
        return true;
    }

    public async Task<List<(string Href, string Name)>> PropFindAsync(string path, string depth = "1", CancellationToken ct = default)
    {
        var (status, body) = await RequestAsync("PROPFIND", path,
            extraHeaders: new Dictionary<string, string> { ["Depth"] = depth }, ct: ct);
        if (status != 207)
        {
            _log?.Invoke(Localization.T("Log.WebDavPropfind", path, status));
            throw new HttpRequestException($"WebDAV PROPFIND '{path}' returned HTTP {status}.");
        }

        var results = new List<(string Href, string Name)>();
        try
        {
            var doc = XDocument.Parse(Encoding.UTF8.GetString(body));
            if (doc.Root?.Name != DavNs + "multistatus")
                throw new InvalidDataException("Expected a DAV multistatus response.");
            foreach (var resp in doc.Descendants(DavNs + "response"))
            {
                var hrefEl = resp.Element(DavNs + "href");
                var prop = resp.Elements(DavNs + "propstat")
                    .Where(ps => ps.Element(DavNs + "status")?.Value.Contains(" 200 ") == true)
                    .Select(ps => ps.Element(DavNs + "prop"))
                    .FirstOrDefault(p => p is not null);
                if (hrefEl is null || prop is null)
                    throw new InvalidDataException("A WebDAV entry is missing its path or readable properties.");
                var href = hrefEl.Value;
                var name = Uri.UnescapeDataString(new Uri(new Uri(_baseUrl + "/"), href)
                    .AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "");
                if (prop.Element(DavNs + "resourcetype")?.Element(DavNs + "collection") is not null)
                    href = href.TrimEnd('/') + "/";
                if (!string.IsNullOrEmpty(name))
                    results.Add((href, name));
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke(Localization.T("Log.WebDavXmlError", path, ex.Message));
            throw new InvalidDataException($"Invalid WebDAV directory listing for '{path}'.", ex);
        }
        _debug?.Invoke(Localization.T("Log.WebDavPropfindDbg", path, results.Count));
        return results;
    }

    public async Task<List<string>> ListDirAsync(string path, CancellationToken ct = default)
    {
        var items = await PropFindAsync(path, ct: ct);
        var serverPath = $"{_urlPath}/{path.TrimStart('/')}".TrimEnd('/');
        var names = new List<string>();
        foreach (var (href, name) in items)
        {
            var hrefPath = DecodeHrefPath(href);
            if (hrefPath == serverPath)
                continue;
            names.Add(name);
        }
        return names;
    }

    public async Task<bool> PutFileAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var content = new StreamContent(stream, 64 * 1024);
        var (status, _) = await RequestAsync("PUT", remotePath, content: content, ct: ct);
        return status is 200 or 201 or 204;
    }

    public async Task<bool> GetFileAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        var (status, body) = await RequestAsync("GET", remotePath, ct: ct);
        if (status != 200)
            return false;
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(localPath, body, ct);
        return true;
    }

    public async Task<bool> DeletePathAsync(string path, CancellationToken ct = default)
    {
        var (status, _) = await RequestAsync("DELETE", path, ct: ct);
        return status is 200 or 204;
    }

    /// <summary>Uploads a local directory to the server with parallel file PUTs.</summary>
    public async Task<bool> UploadDirAsync(string localDir, string remoteDir, CancellationToken ct = default)
    {
        if (!await MkColAsync(remoteDir, ct))
        {
            _log?.Invoke(Localization.T("Log.WebDavMkcolFail", remoteDir));
            return false;
        }

        var fileTasks = new List<(string Local, string Remote)>();
        foreach (var walkRoot in Directory.EnumerateDirectories(localDir, "*", SearchOption.AllDirectories)
                     .Prepend(localDir))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(localDir, walkRoot).Replace('\\', '/');
            var currentRemote = rel == "." ? remoteDir : $"{remoteDir}/{rel}";
            if (rel != "." && !await MkColAsync(currentRemote, ct))
            {
                    _log?.Invoke(Localization.T("Log.WebDavMkcolFail", currentRemote));
                return false;
            }
            foreach (var file in Directory.EnumerateFiles(walkRoot))
            {
                fileTasks.Add((file, $"{currentRemote}/{Path.GetFileName(file)}"));
            }
        }

        if (fileTasks.Count == 0)
            return true;

        var failed = false;
        await Parallel.ForEachAsync(fileTasks,
            new ParallelOptions { MaxDegreeOfParallelism = Constants.WebDavWorkers, CancellationToken = ct },
            async (task, token) =>
            {
                if (failed)
                    return;
                if (!await PutFileAsync(task.Local, task.Remote, token))
                {
                    failed = true;
                    _log?.Invoke(Localization.T("Log.WebDavUploadItemFail", task.Remote));
                }
            });
        return !failed;
    }

    /// <summary>Downloads a remote directory recursively; returns the number of files downloaded.</summary>
    public async Task<int> DownloadDirAsync(string remoteDir, string localDir, CancellationToken ct = default)
    {
        var allFiles = new List<(string Remote, string Local)>();
        await CollectFilesAsync(remoteDir, localDir, allFiles, ct);
        if (allFiles.Count == 0)
        {
            _log?.Invoke(Localization.T("Log.WebDavNoFiles", remoteDir));
            return 0;
        }

        foreach (var (_, local) in allFiles)
        {
            var dir = Path.GetDirectoryName(local);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        var downloaded = 0;
        await Parallel.ForEachAsync(allFiles,
            new ParallelOptions { MaxDegreeOfParallelism = Constants.WebDavWorkers, CancellationToken = ct },
            async (task, token) =>
            {
                if (!await GetFileAsync(task.Remote, task.Local, token))
                    throw new IOException($"WebDAV download failed: {task.Remote}");
                Interlocked.Increment(ref downloaded);
            });
        return downloaded;
    }

    private async Task CollectFilesAsync(string remoteDir, string localDir, List<(string, string)> result, CancellationToken ct)
    {
        Directory.CreateDirectory(localDir);
        var items = await PropFindAsync(remoteDir, ct: ct);
        var serverPath = $"{_urlPath}/{remoteDir.TrimStart('/')}".TrimEnd('/');
        foreach (var (href, name) in items)
        {
            ct.ThrowIfCancellationRequested();
            var hrefPath = DecodeHrefPath(href);
            if (hrefPath == serverPath)
                continue;
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"Invalid WebDAV filename: {name}");
            var localChild = Path.Combine(localDir, name);
            if (href.EndsWith('/'))
            {
                await CollectFilesAsync($"{remoteDir.TrimEnd('/')}/{name}", localChild, result, ct);
            }
            else
            {
                result.Add(($"{remoteDir}/{name}", localChild));
            }
        }
    }

    private async Task<(int Status, byte[] Body)> RequestAsync(
        string method,
        string path,
        HttpContent? content = null,
        Dictionary<string, string>? extraHeaders = null,
        CancellationToken ct = default)
    {
        var encoded = EncodePath(path);
        using var request = new HttpRequestMessage(new HttpMethod(method), $"{_baseUrl}/{encoded}")
        {
            Content = content
        };
        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
                request.Headers.TryAddWithoutValidation(key, value);
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        return ((int)response.StatusCode, body);
    }

    private static string EncodePath(string path)
    {
        var parts = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts.Select(Uri.EscapeDataString));
    }

    private string DecodeHrefPath(string href) =>
        Uri.UnescapeDataString(new Uri(new Uri(_baseUrl + "/"), href).AbsolutePath).TrimEnd('/');
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Xml.Linq;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Services;

/// <summary>A WebDAV request was answered with an error status; the message already says what it means.</summary>
public sealed class WebDavException(int statusCode, string message) : IOException(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Result of a quick reachability check; <see cref="Message"/> explains a failure.</summary>
public sealed record WebDavProbe(bool Ok, int Status, string Message);

/// <summary>
/// WebDAV client supporting PROPFIND / MKCOL / PUT / GET / DELETE with
/// parallel directory upload/download (mirrors the Python stdlib client).
/// Requests that fail for transient reasons (dropped connection, timeout, a busy server) are repeated.
/// </summary>
public sealed class WebDavClient : IDisposable
{
    private static readonly XNamespace DavNs = "DAV:";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _urlPath;
    private readonly Action<string>? _log;
    private readonly Action<string>? _debug;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _transferTimeout;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    // Once a request has used up all its attempts on connection trouble, the server is written off for a
    // while: the clean-up and the remaining files of the same run then fail at once instead of each
    // repeating the whole wait.
    private static readonly TimeSpan UnreachableMemory = TimeSpan.FromSeconds(30);
    private long _unreachableUntilTicks;
    private string _unreachableMessage = "";

    /// <param name="timeoutSeconds">Limit for listing, creating and deleting folders.</param>
    /// <param name="transferTimeout">Limit for moving one file's content; saves can be large and uplinks slow.</param>
    /// <param name="maxAttempts">Tries per request, including the first.</param>
    /// <param name="retryDelay">Wait before the first repeat; each further repeat waits three times longer.</param>
    public WebDavClient(string url, string username, string password,
        Action<string>? log = null, Action<string>? debug = null,
        int timeoutSeconds = 30, HttpMessageHandler? messageHandler = null,
        TimeSpan? transferTimeout = null, int maxAttempts = 3, TimeSpan? retryDelay = null)
    {
        var urlError = WebDavUrl.Normalize(url, out var normalized);
        if (normalized.Length == 0)
            throw new ArgumentException(Localization.T("Url.Empty"), nameof(url));
        if (urlError != WebDavUrlError.None)
            throw new ArgumentException(WebDavUrl.Describe(urlError, url), nameof(url));
        _baseUrl = normalized.TrimEnd('/');
        _urlPath = Uri.UnescapeDataString(new Uri(_baseUrl).AbsolutePath).TrimEnd('/');
        _log = log;
        _debug = debug;
        _requestTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        _transferTimeout = transferTimeout ?? TimeSpan.FromMinutes(10);
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);

        var handler = messageHandler ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        // Time limits are applied per request (see RequestAsync), since they differ by kind of request.
        _http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
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

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default) =>
        (await ProbeAsync(ct)).Ok;

    /// <summary>Asks the server about its root folder once, quickly, and says what is wrong if it can't answer.</summary>
    public async Task<WebDavProbe> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var (status, _) = await RequestAsync("PROPFIND", "",
                extraHeaders: new Dictionary<string, string> { ["Depth"] = "0" }, ct: ct,
                attempts: 1, timeout: _requestTimeout < ProbeTimeout ? _requestTimeout : ProbeTimeout);
            return status is 207 or 200
                ? new WebDavProbe(true, status, "")
                : new WebDavProbe(false, status, DescribeStatus(status));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new WebDavProbe(false, 0, DescribeException(ex));
        }
    }

    /// <summary>Plain-language meaning of an HTTP status returned for a failed request.</summary>
    public static string DescribeStatus(int status) => status switch
    {
        401 or 403 => Localization.T("Err.WebDavAuth", status),
        404 => Localization.T("Err.WebDavNotFound"),
        408 or 429 or 502 or 503 or 504 => Localization.T("Err.WebDavBusy", status),
        _ => Localization.T("Err.WebDavHttp", status)
    };

    /// <summary>Plain-language reason for a failure that surfaced as an exception.</summary>
    public static string DescribeException(Exception ex) => ex switch
    {
        WebDavException => ex.Message,
        HttpRequestException { StatusCode: { } code } => DescribeStatus((int)code),
        HttpRequestException http => Localization.T("Err.WebDavUnreachable", http.InnerException?.Message ?? http.Message),
        OperationCanceledException => Localization.T("Err.WebDavTimeout"),
        _ => ex.Message
    };

    /// <summary>Creates a remote directory (recursively).</summary>
    public async Task<bool> MkColAsync(string path, CancellationToken ct = default) =>
        await MkColCoreAsync(path, ct) == 0;

    /// <returns>0 when the directory exists afterwards, otherwise the status that stopped it.</returns>
    private async Task<int> MkColCoreAsync(string path, CancellationToken ct)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current = current.Length == 0 ? part : $"{current}/{part}";
            var (status, _) = await RequestAsync("MKCOL", current, ct: ct);
            if (status is not (201 or 405 or 301))
                return status;
        }
        return 0;
    }

    public async Task<List<(string Href, string Name)>> PropFindAsync(string path, string depth = "1", CancellationToken ct = default)
    {
        var (status, body) = await RequestAsync("PROPFIND", path,
            extraHeaders: new Dictionary<string, string> { ["Depth"] = depth }, ct: ct);
        if (status != 207)
        {
            _log?.Invoke(Localization.T("Log.WebDavPropfind", path, status));
            throw new HttpRequestException($"WebDAV PROPFIND '{path}' returned HTTP {status}.", null, (HttpStatusCode)status);
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

    public async Task<bool> PutFileAsync(string localPath, string remotePath, CancellationToken ct = default) =>
        IsPutOk(await PutCoreAsync(localPath, remotePath, ct));

    private static bool IsPutOk(int status) => status is 200 or 201 or 204;

    private async Task<int> PutCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        // Each attempt reads the file again, so a repeated request is never missing its content.
        var (status, _) = await RequestAsync("PUT", remotePath, contentFactory: () =>
            new StreamContent(new FileStream(localPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true), 64 * 1024), ct: ct);
        return status;
    }

    public async Task<bool> GetFileAsync(string remotePath, string localPath, CancellationToken ct = default) =>
        await GetCoreAsync(remotePath, localPath, ct) == 200;

    private async Task<int> GetCoreAsync(string remotePath, string localPath, CancellationToken ct)
    {
        var (status, body) = await RequestAsync("GET", remotePath, ct: ct);
        if (status != 200)
            return status;
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(localPath, body, ct);
        return status;
    }

    public async Task<bool> DeletePathAsync(string path, CancellationToken ct = default)
    {
        var (status, _) = await RequestAsync("DELETE", path, ct: ct);
        return status is 200 or 204;
    }

    public async Task<bool> DeleteCollectionAsync(string path, CancellationToken ct = default)
    {
        var (status, _) = await RequestAsync("DELETE", path, ct: ct, collection: true);
        return status is 200 or 204 or 404;
    }

    /// <summary>Renames a remote file or folder; returns the HTTP status (201/204 on success).</summary>
    public async Task<int> MoveAsync(string fromPath, string toPath, bool collection, bool overwrite,
        CancellationToken ct = default)
    {
        var slash = collection ? "/" : "";
        var (status, _) = await RequestAsync("MOVE", fromPath, extraHeaders: new Dictionary<string, string>
        {
            ["Destination"] = $"{_baseUrl}/{EncodePath(toPath)}{slash}",
            ["Overwrite"] = overwrite ? "T" : "F"
        }, ct: ct, collection: collection);
        return status;
    }

    /// <summary>
    /// Uploads a local directory to the server with parallel file PUTs.
    /// Throws <see cref="WebDavException"/> for the first folder or file the server refused.
    /// </summary>
    public async Task UploadDirAsync(string localDir, string remoteDir, CancellationToken ct = default)
    {
        await MakeDirectoryOrThrowAsync(remoteDir, ct);

        var fileTasks = new List<(string Local, string Remote)>();
        foreach (var walkRoot in Directory.EnumerateDirectories(localDir, "*", SearchOption.AllDirectories)
                     .Prepend(localDir))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(localDir, walkRoot).Replace('\\', '/');
            var currentRemote = rel == "." ? remoteDir : $"{remoteDir}/{rel}";
            if (rel != ".")
                await MakeDirectoryOrThrowAsync(currentRemote, ct);
            foreach (var file in Directory.EnumerateFiles(walkRoot))
            {
                fileTasks.Add((file, $"{currentRemote}/{Path.GetFileName(file)}"));
            }
        }

        if (fileTasks.Count == 0)
            return;

        var failedStatus = 0;
        string? failedRemote = null;
        await Parallel.ForEachAsync(fileTasks,
            new ParallelOptions { MaxDegreeOfParallelism = Constants.WebDavWorkers, CancellationToken = ct },
            async (task, token) =>
            {
                if (Volatile.Read(ref failedStatus) != 0)
                    return;
                var status = await PutCoreAsync(task.Local, task.Remote, token);
                if (IsPutOk(status))
                    return;
                if (Interlocked.CompareExchange(ref failedStatus, status, 0) == 0)
                {
                    failedRemote = task.Remote;
                    _log?.Invoke(Localization.T("Log.WebDavUploadItemFail", task.Remote));
                }
            });
        if (failedStatus != 0)
            throw new WebDavException(failedStatus,
                Localization.T("Err.WebDavUpload", failedRemote, DescribeStatus(failedStatus)));
    }

    private async Task MakeDirectoryOrThrowAsync(string remoteDir, CancellationToken ct)
    {
        var failedStatus = await MkColCoreAsync(remoteDir, ct);
        if (failedStatus == 0)
            return;
        _log?.Invoke(Localization.T("Log.WebDavMkcolFail", remoteDir));
        throw new WebDavException(failedStatus,
            Localization.T("Err.WebDavMkcol", remoteDir, DescribeStatus(failedStatus)));
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
                var status = await GetCoreAsync(task.Remote, task.Local, token);
                if (status != 200)
                    throw new WebDavException(status,
                        Localization.T("Err.WebDavDownload", task.Remote, DescribeStatus(status)));
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
        Func<HttpContent>? contentFactory = null,
        Dictionary<string, string>? extraHeaders = null,
        CancellationToken ct = default,
        bool collection = false,
        int? attempts = null,
        TimeSpan? timeout = null)
    {
        // Collections get a trailing slash so servers don't answer with a redirect (which drops credentials).
        var encoded = EncodePath(path) + (collection ? "/" : "");
        var url = $"{_baseUrl}/{encoded}";
        var limit = timeout ?? (contentFactory is not null || method == "GET" ? _transferTimeout : _requestTimeout);
        // A renamed folder can't be told apart from one that was never renamed once the answer is lost.
        var maxAttempts = method == "MOVE" ? 1 : attempts ?? _maxAttempts;
        if (Volatile.Read(ref _unreachableUntilTicks) > DateTime.UtcNow.Ticks)
            throw new WebDavException(0, _unreachableMessage);

        for (var attempt = 1; ; attempt++)
        {
            string reason;
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), url)
                {
                    Content = contentFactory?.Invoke()
                };
                if (extraHeaders is not null)
                {
                    foreach (var (key, value) in extraHeaders)
                        request.Headers.TryAddWithoutValidation(key, value);
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(limit);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token);
                var body = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                var status = (int)response.StatusCode;
                // The server answered, so it is reachable.
                Volatile.Write(ref _unreachableUntilTicks, 0);
                if (attempt >= maxAttempts || !IsRetryableStatus(status))
                    return (status, body);
                reason = $"HTTP {status}";
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                if (attempt >= maxAttempts)
                {
                    // A local file that was locked says nothing about the server; connection trouble does.
                    if (ex is OperationCanceledException or HttpRequestException)
                    {
                        _unreachableMessage = DescribeException(ex);
                        Volatile.Write(ref _unreachableUntilTicks, (DateTime.UtcNow + UnreachableMemory).Ticks);
                    }
                    throw;
                }
                reason = ex is OperationCanceledException ? Localization.T("Err.WebDavTimeout") : ex.Message;
            }
            _debug?.Invoke(Localization.T("Log.WebDavRetry", method, path, reason, attempt, maxAttempts));
            await Task.Delay(TimeSpan.FromTicks(_retryDelay.Ticks * (long)Math.Pow(3, attempt - 1)), ct);
        }
    }

    // The server asked to come back later (or a gateway in front of it gave up), so nothing was applied.
    private static bool IsRetryableStatus(int status) => status is 408 or 429 or 502 or 503 or 504;

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;
        return ex switch
        {
            // Not the caller's cancellation, so it is the per-request time limit running out.
            OperationCanceledException => true,
            // A bad certificate won't fix itself within a few seconds.
            HttpRequestException http => http.StatusCode is null && http.InnerException is not AuthenticationException,
            // A missing local file stays missing; a sharing violation may pass.
            IOException io => io is not (FileNotFoundException or DirectoryNotFoundException),
            _ => false
        };
    }

    private static string EncodePath(string path)
    {
        var parts = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts.Select(Uri.EscapeDataString));
    }

    private string DecodeHrefPath(string href) =>
        Uri.UnescapeDataString(new Uri(new Uri(_baseUrl + "/"), href).AbsolutePath).TrimEnd('/');
}

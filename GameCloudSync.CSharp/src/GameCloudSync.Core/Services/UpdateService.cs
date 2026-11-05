using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Services;

/// <summary>Checking for or downloading an update failed; the message says why, in the UI language.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The program file a release offers for download.</summary>
/// <param name="Sha256">The hex SHA-256 GitHub reports for the file, when it reports one.</param>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

/// <summary>A published release as GitHub describes it.</summary>
/// <param name="Asset">The exe to install, or null when the release doesn't carry one.</param>
public sealed record ReleaseInfo(
    Version Version, string Tag, string Notes, string PageUrl, DateTimeOffset? PublishedAt, ReleaseAsset? Asset)
{
    public string VersionText => UpdateService.Display(Version);
}

/// <summary>
/// Asks GitHub for the latest release and downloads its exe. A download only counts once it matches the
/// size and SHA-256 GitHub reports for it; anything else is deleted.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;
    private readonly string _latestReleaseUrl;
    private readonly TimeSpan _stallTimeout;

    /// <param name="stallTimeout">A download that receives nothing for this long is given up.</param>
    public UpdateService(Version currentVersion, HttpMessageHandler? messageHandler = null,
        string latestReleaseUrl = Constants.LatestReleaseApiUrl, TimeSpan? stallTimeout = null)
    {
        CurrentVersion = Normalize(currentVersion);
        _latestReleaseUrl = latestReleaseUrl;
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(30);
        // The system proxy applies, as it does in the browser.
        _http = new HttpClient(messageHandler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            // Limits are per request: a check must answer quickly, a download may take minutes.
            Timeout = Timeout.InfiniteTimeSpan
        };
        // GitHub's API turns away requests without a User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GameCloudSync/{Format(CurrentVersion)}");
    }

    public Version CurrentVersion { get; }

    public void Dispose() => _http.Dispose();

    /// <summary>The release GitHub marks as latest (never a draft or pre-release).</summary>
    public async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _latestReleaseUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UpdateException(Localization.T("Update.ErrNoRelease"));
        EnsureSuccess(response);
        return ParseRelease(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Reads GitHub's description of a release; one without a version number in its tag is refused.</summary>
    public static ReleaseInfo ParseRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = Text(root, "tag_name");
            var version = ParseVersion(tag) ?? throw new UpdateException(Localization.T("Update.ErrBadResponse"));
            DateTimeOffset? published = DateTimeOffset.TryParse(Text(root, "published_at"), out var when) ? when : null;
            var page = Text(root, "html_url");
            return new ReleaseInfo(version, tag, Text(root, "body"),
                page.Length > 0 ? page : Constants.LatestReleasePageUrl, published, FindAsset(root));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new UpdateException(Localization.T("Update.ErrBadResponse"), ex);
        }
    }

    // Only the exe under its usual name: anything else on the release (an installer, a zip) must never replace the program.
    private static ReleaseAsset? FindAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Text(asset, "name");
            if (!string.Equals(name, Constants.UpdateAssetName, StringComparison.OrdinalIgnoreCase))
                continue;
            var url = Text(asset, "browser_download_url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                continue;
            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number &&
                       sizeElement.TryGetInt64(out var bytes) ? bytes : 0;
            return new ReleaseAsset(name, url, size, Sha256Of(Text(asset, "digest")));
        }
        return null;
    }

    // GitHub writes the digest as "sha256:<64 hex digits>".
    private static string? Sha256Of(string digest) =>
        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 7 + 64
            ? digest[7..].ToLowerInvariant()
            : null;

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>
    /// Downloads the asset to <paramref name="destination"/>, reporting the bytes received so far. A download that
    /// stops early, comes out the wrong size or doesn't match its SHA-256 throws and leaves no file behind.
    /// </summary>
    public async Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            EnsureSuccess(response);
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long received = 0;
            await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                             buffer.Length, useAsync: true))
            {
                while (true)
                {
                    var read = await ReadAsync(source, buffer, ct);
                    if (read == 0)
                        break;
                    received += read;
                    if (asset.Size > 0 && received > asset.Size)
                        throw SizeMismatch(received, asset.Size);
                    sha256.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report(received);
                }
                await target.FlushAsync(ct);
                target.Flush(flushToDisk: true);
            }
            if (asset.Size > 0 && received != asset.Size)
                throw SizeMismatch(received, asset.Size);
            if (asset.Sha256 is { } expected &&
                !string.Equals(Convert.ToHexString(sha256.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(Localization.T("Update.ErrHash"));
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    private static UpdateException SizeMismatch(long received, long expected) =>
        new(Localization.T("Update.ErrSize", FileUtil.FormatSize(received), FileUtil.FormatSize(expected)));

    private async Task<int> ReadAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(_stallTimeout);
        try
        {
            return await source.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(Localization.T("Update.ErrStalled", (int)Math.Ceiling(_stallTimeout.TotalSeconds)));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            throw new UpdateException(Localization.T("Update.ErrDownload", ex.Message), ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await _http.SendAsync(request, completion, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(Localization.T("Update.ErrTimeout"));
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(Localization.T("Update.ErrNetwork", ex.Message), ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var code = (int)response.StatusCode;
        // Without signing in, GitHub answers 60 API requests an hour per address, then 403 (or 429) until the hour is up.
        var limited = code == 429 || (code == 403 &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0");
        throw new UpdateException(limited ? Localization.T("Update.ErrRateLimited") : Localization.T("Update.ErrHttp", code));
    }

    /// <summary>
    /// Makes sure a downloaded file is a Windows program carrying <paramref name="expected"/> as its version. An exe
    /// built with another version number than its release's tag would otherwise be offered again after every update.
    /// </summary>
    public static void VerifyProgram(string path, Version expected)
    {
        Version actual;
        try
        {
            if (!StartsLikeProgram(path))
                throw new UpdateException(Localization.T("Update.ErrNotProgram"));
            var info = FileVersionInfo.GetVersionInfo(path);
            actual = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(Localization.T("Update.ErrNotProgram"), ex);
        }
        if (Normalize(actual) != Normalize(expected))
            throw new UpdateException(Localization.T("Update.ErrVersion", Display(actual), Display(expected)));
    }

    // Every Windows program starts with "MZ".
    private static bool StartsLikeProgram(string path)
    {
        Span<byte> head = stackalloc byte[2];
        using var file = File.OpenRead(path);
        return file.Read(head) == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z';
    }

    /// <summary>The version in a release tag such as "v2.3.0"; anything after "-" or "+" is ignored.</summary>
    public static Version? ParseVersion(string? tag)
    {
        var text = tag?.Trim() ?? "";
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        var end = text.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
            text = text[..end];
        if (!text.Contains('.'))
            text += ".0";
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>Fills in missing parts with 0, so that 2.3 and 2.3.0.0 compare as equal.</summary>
    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary>"2.3.0"; the fourth part only when it isn't 0.</summary>
    public static string Format(Version version)
    {
        var normalized = Normalize(version);
        return normalized.ToString(normalized.Revision > 0 ? 4 : 3);
    }

    /// <summary>"v2.3.0", as the version is written on GitHub and in the app.</summary>
    public static string Display(Version version) => "v" + Format(version);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Removed at the next start.
        }
    }
}

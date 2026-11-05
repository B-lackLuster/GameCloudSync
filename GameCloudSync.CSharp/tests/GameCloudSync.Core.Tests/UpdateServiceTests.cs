using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

// The messages compared here are in the UI language, which LocalizationTests switches for a moment.
[Collection(nameof(Localization))]
public sealed class UpdateServiceTests : IDisposable
{
    private const string ApiUrl = "https://api.example.test/repos/owner/app/releases/latest";
    private const string ExeUrl = "https://example.test/download/v2.3.0/GameCloudSync.exe";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_update_{Guid.NewGuid():N}");

    public UpdateServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return respond(request, ct);
        }
    }

    private static UpdateService Service(Handler handler, TimeSpan? stallTimeout = null) =>
        new(new Version(2, 2, 2), handler, ApiUrl, stallTimeout);

    private static Handler Answer(HttpStatusCode status, string body = "") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReleaseJson(string tag = "v2.3.0", string assets = "") => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "GameCloudSync {{tag}}",
          "html_url": "https://example.test/releases/tag/{{tag}}",
          "published_at": "2026-11-04T15:34:14Z",
          "body": "## {{tag}}\n\n- Fixed things",
          "draft": false,
          "prerelease": false,
          "assets": [{{assets}}]
        }
        """;

    private static string Asset(string name, long size, string? digest, string url = ExeUrl) => $$"""
        {
          "name": "{{name}}",
          "size": {{size}},
          "state": "uploaded",
          "digest": {{(digest is null ? "null" : $"\"{digest}\"")}},
          "browser_download_url": "{{url}}"
        }
        """;

    // ---------------- Versions ----------------

    [Theory]
    [InlineData("v2.3.0", "2.3.0.0")]
    [InlineData("V2.3", "2.3.0.0")]
    [InlineData("2.3.0", "2.3.0.0")]
    [InlineData("v2.3.0-beta.1", "2.3.0.0")]
    [InlineData("v2.3.0+build.7", "2.3.0.0")]
    [InlineData("v3", "3.0.0.0")]
    [InlineData("v2.3.0.1", "2.3.0.1")]
    public void ParseVersion_ReadsReleaseTags(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("release-2026")]
    public void ParseVersion_RejectsTagsWithoutVersion(string? tag)
    {
        Assert.Null(UpdateService.ParseVersion(tag));
    }

    [Theory]
    [InlineData("2.3.0", "2.2.2", true)]
    [InlineData("2.2.10", "2.2.9", true)]
    [InlineData("2.2.2", "2.2.2.0", false)]
    [InlineData("2.2.2.0", "2.2.2", false)]
    [InlineData("2.2.1", "2.2.2", false)]
    public void IsNewer_ComparesAllParts(string candidate, string current, bool newer)
    {
        Assert.Equal(newer, UpdateService.IsNewer(Version.Parse(candidate), Version.Parse(current)));
    }

    [Fact]
    public void Display_ShowsThreePartsUnlessTheFourthIsSet()
    {
        Assert.Equal("v2.3.0", UpdateService.Display(new Version(2, 3, 0, 0)));
        Assert.Equal("v2.3.0", UpdateService.Display(new Version(2, 3)));
        Assert.Equal("v2.3.0.1", UpdateService.Display(new Version(2, 3, 0, 1)));
    }

    // ---------------- Latest release ----------------

    [Fact]
    public async Task GetLatestRelease_ReadsVersionNotesAndTheExe()
    {
        var digest = new string('A', 64);
        var handler = Answer(HttpStatusCode.OK, ReleaseJson(assets:
            Asset("GameCloudSync.zip", 10, null, "https://example.test/download/v2.3.0/GameCloudSync.zip") + "," +
            Asset("GameCloudSync.exe", 74_458_315, "sha256:" + digest)));
        using var service = Service(handler);

        var release = await service.GetLatestReleaseAsync();

        Assert.Equal(new Version(2, 3, 0, 0), release.Version);
        Assert.Equal("v2.3.0", release.Tag);
        Assert.Equal("v2.3.0", release.VersionText);
        Assert.Equal("https://example.test/releases/tag/v2.3.0", release.PageUrl);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 15, 34, 14, TimeSpan.Zero), release.PublishedAt);
        Assert.Contains("Fixed things", release.Notes);
        var asset = Assert.IsType<ReleaseAsset>(release.Asset);
        Assert.Equal("GameCloudSync.exe", asset.Name);
        Assert.Equal(ExeUrl, asset.DownloadUrl);
        Assert.Equal(74_458_315, asset.Size);
        Assert.Equal(digest.ToLowerInvariant(), asset.Sha256);

        // GitHub's API refuses requests without a User-Agent.
        var request = Assert.Single(handler.Requests);
        Assert.Equal(ApiUrl, request.RequestUri!.ToString());
        Assert.NotEmpty(request.Headers.UserAgent);
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
    }

    [Fact]
    public async Task GetLatestRelease_OnlyTheExeUnderItsOwnNameCounts()
    {
        // An installer or a zip must never take the program's place.
        using var service = Service(Answer(HttpStatusCode.OK, ReleaseJson(assets:
            Asset("GameCloudSync-Setup.exe", 10, null, "https://example.test/download/v2.3.0/GameCloudSync-Setup.exe") + "," +
            Asset("GameCloudSync.exe", 10, null, "http://example.test/download/v2.3.0/GameCloudSync.exe"))));

        var release = await service.GetLatestReleaseAsync();

        // The exe that would come over plain http doesn't count either.
        Assert.Null(release.Asset);
    }

    [Fact]
    public async Task GetLatestRelease_DigestIsOptional()
    {
        using var service = Service(Answer(HttpStatusCode.OK, ReleaseJson(assets: Asset("GameCloudSync.exe", 5, null))));

        var release = await service.GetLatestReleaseAsync();

        Assert.Null(release.Asset!.Sha256);
        Assert.Equal(5, release.Asset.Size);
    }

    [Fact]
    public async Task GetLatestRelease_NoReleaseYet_SaysSo()
    {
        using var service = Service(Answer(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.GetLatestReleaseAsync());

        Assert.Equal(Localization.T("Update.ErrNoRelease"), ex.Message);
    }

    [Fact]
    public async Task GetLatestRelease_RateLimited_SaysSo()
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        });
        using var service = Service(handler);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.GetLatestReleaseAsync());

        Assert.Equal(Localization.T("Update.ErrRateLimited"), ex.Message);
    }

    [Fact]
    public async Task GetLatestRelease_ServerError_GivesTheStatus()
    {
        using var service = Service(Answer(HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.GetLatestReleaseAsync());

        Assert.Equal(Localization.T("Update.ErrHttp", 502), ex.Message);
    }

    [Theory]
    [InlineData("<html>not json</html>")]
    [InlineData("{\"tag_name\": \"latest\"}")]
    [InlineData("[]")]
    public async Task GetLatestRelease_UnreadableAnswer_IsRefused(string body)
    {
        using var service = Service(Answer(HttpStatusCode.OK, body));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.GetLatestReleaseAsync());

        Assert.Equal(Localization.T("Update.ErrBadResponse"), ex.Message);
    }

    [Fact]
    public async Task GetLatestRelease_Offline_SaysGitHubIsOutOfReach()
    {
        using var service = Service(new Handler((_, _) => throw new HttpRequestException("No such host is known.")));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.GetLatestReleaseAsync());

        Assert.Equal(Localization.T("Update.ErrNetwork", "No such host is known."), ex.Message);
    }

    // ---------------- Download ----------------

    private static Handler Serve(byte[] content) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));

    [Fact]
    public async Task Download_MatchingFile_IsWrittenAndReported()
    {
        var content = RandomNumberGenerator.GetBytes(300_000);
        var handler = Serve(content);
        using var service = Service(handler);
        var target = Path.Combine(_root, "GameCloudSync.exe.download");
        var reported = new List<long>();

        await service.DownloadAsync(new ReleaseAsset("GameCloudSync.exe", ExeUrl, content.Length, Sha256Hex(content)),
            target, new SyncProgress(reported.Add));

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.Equal(content.Length, reported[^1]);
        Assert.Equal(reported.OrderBy(r => r), reported);
        Assert.Equal(ExeUrl, Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task Download_WithoutDigest_ChecksTheSize()
    {
        var content = Encoding.UTF8.GetBytes("program");
        using var service = Service(Serve(content));
        var target = Path.Combine(_root, "new.exe");

        await service.DownloadAsync(new ReleaseAsset("GameCloudSync.exe", ExeUrl, content.Length, null), target);

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task Download_DamagedFile_LeavesNothingBehind()
    {
        var content = Encoding.UTF8.GetBytes("program");
        using var service = Service(Serve(content));
        var target = Path.Combine(_root, "new.exe");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(
            new ReleaseAsset("GameCloudSync.exe", ExeUrl, content.Length, new string('0', 64)), target));

        Assert.Equal(Localization.T("Update.ErrHash"), ex.Message);
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData(10)] // the connection closed early
    [InlineData(3)] // more arrived than the release holds
    public async Task Download_WrongSize_LeavesNothingBehind(long announcedSize)
    {
        var content = Encoding.UTF8.GetBytes("12345");
        using var service = Service(Serve(content));
        var target = Path.Combine(_root, "new.exe");

        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(
            new ReleaseAsset("GameCloudSync.exe", ExeUrl, announcedSize, null), target));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Download_Cancelled_LeavesNothingBehind()
    {
        using var cts = new CancellationTokenSource();
        var stream = new EndlessStream(firstChunk: 1000, onFirstRead: cts.Cancel);
        using var service = Service(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        var target = Path.Combine(_root, "new.exe");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(
            new ReleaseAsset("GameCloudSync.exe", ExeUrl, 1_000_000, null), target, ct: cts.Token));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Download_Stalled_IsGivenUp()
    {
        var stream = new EndlessStream(firstChunk: 1000);
        using var service = Service(new Handler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })),
            stallTimeout: TimeSpan.FromMilliseconds(300));
        var target = Path.Combine(_root, "new.exe");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(
            new ReleaseAsset("GameCloudSync.exe", ExeUrl, 1_000_000, null), target));

        Assert.Equal(Localization.T("Update.ErrStalled", 1), ex.Message);
        Assert.False(File.Exists(target));
    }

    // ---------------- The downloaded program ----------------

    // Test projects carry the default version 1.0.0.0.
    private static readonly string VersionedProgram = typeof(UpdateServiceTests).Assembly.Location;

    [Fact]
    public void VerifyProgram_AcceptsTheReleasedVersion()
    {
        UpdateService.VerifyProgram(VersionedProgram, new Version(1, 0, 0));
    }

    [Fact]
    public void VerifyProgram_RefusesAnotherVersion()
    {
        // A release whose exe was built with a stale version would be offered again after every update.
        var ex = Assert.Throws<UpdateException>(() => UpdateService.VerifyProgram(VersionedProgram, new Version(2, 3, 0)));

        Assert.Equal(Localization.T("Update.ErrVersion", "v1.0.0", "v2.3.0"), ex.Message);
    }

    [Fact]
    public void VerifyProgram_RefusesWhatIsNoProgram()
    {
        var page = Path.Combine(_root, "error.html");
        File.WriteAllText(page, "<html>Service unavailable</html>");

        var ex = Assert.Throws<UpdateException>(() => UpdateService.VerifyProgram(page, new Version(1, 0, 0)));

        Assert.Equal(Localization.T("Update.ErrNotProgram"), ex.Message);
    }

    /// <summary>Reports on the calling thread, so the test sees every report before the download returns.</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>Hands out one chunk, then waits for more that never comes (until cancelled).</summary>
    private sealed class EndlessStream(int firstChunk, Action? onFirstRead = null) : Stream
    {
        private bool _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent)
            {
                _sent = true;
                onFirstRead?.Invoke();
                var count = Math.Min(firstChunk, buffer.Length);
                buffer.Span[..count].Fill(0x4D);
                return count;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

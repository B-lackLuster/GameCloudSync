using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public sealed class MonitoringRetryTests : IDisposable
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(200);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_retry_mon_{Guid.NewGuid():N}");
    private readonly string _save;
    private readonly string _cloud;
    private readonly ConcurrentQueue<BackupAttempt> _attempts = new();
    private readonly ConcurrentQueue<string> _log = new();
    private MonitoringService? _monitor;

    public MonitoringRetryTests()
    {
        _save = Path.Combine(_root, "save");
        _cloud = Path.Combine(_root, "cloud");
        Directory.CreateDirectory(_save);
        Directory.CreateDirectory(_cloud);
    }

    public void Dispose()
    {
        _monitor?.Dispose();
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private AppConfig Config(string[] methods, string webDavUrl = "") => new()
    {
        CloudRoot = _cloud,
        WebDavUrl = webDavUrl,
        Games = { new GameConfig { Name = "game", Save = _save, BackupMethods = methods.ToList() } }
    };

    private MonitoringService StartMonitor(BackupService service, AppConfig config, params TimeSpan[] retryDelays)
    {
        _monitor = new MonitoringService(service, () => config, () => "", _log.Enqueue,
            quietPeriod: Quick, maxDelay: TimeSpan.FromSeconds(10), retryDelays: retryDelays);
        _monitor.BackupAttempted += _attempts.Enqueue;
        Assert.True(_monitor.Start());
        return _monitor;
    }

    /// <summary>A server that refuses to create folders (as if the login had expired) while Failing is set.</summary>
    private sealed class FlakyServer
    {
        public FlakyServer(bool failing)
        {
            Failing = failing;
            Server = new FakeWebDavServer
            {
                Respond = req => Task.FromResult<HttpResponseMessage?>(
                    Failing && req.Method.Method == "MKCOL" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null)
            };
        }

        public volatile bool Failing;
        public FakeWebDavServer Server { get; }
    }

    private int OneDriveBackupCount() =>
        Directory.Exists(Path.Combine(_cloud, "game"))
            ? Directory.GetDirectories(Path.Combine(_cloud, "game"), "save_*").Length
            : 0;

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs)
                Assert.Fail("Timed out waiting for the expected backup attempt.");
            await Task.Delay(50);
        }
    }

    private Task SaveGameAsync(string content = "progress") =>
        File.WriteAllTextAsync(Path.Combine(_save, "slot.sav"), content);

    [Fact]
    public async Task SuccessfulBackup_IsReportedWithoutFailure()
    {
        StartMonitor(new BackupService(), Config(["onedrive"]), Quick);

        await SaveGameAsync();
        await WaitUntilAsync(() => !_attempts.IsEmpty);

        var attempt = _attempts.Single();
        Assert.False(attempt.Result.HasFailures);
        Assert.False(attempt.IsNewFailure);
        Assert.Null(attempt.RetryIn);
        Assert.Equal(new[] { "OneDrive" }, attempt.Result.Succeeded);
    }

    [Fact]
    public async Task FailedBackup_IsRepeatedByItself_UntilItSucceeds()
    {
        var flaky = new FlakyServer(failing: true);
        StartMonitor(flaky.Server.CreateBackupService(), Config(["webdav"], flaky.Server.Url), Quick, Quick, Quick, Quick);

        await SaveGameAsync();
        await WaitUntilAsync(() => !_attempts.IsEmpty);
        var first = _attempts.First();
        Assert.True(first.Result.HasFailures);
        Assert.True(first.IsNewFailure);
        Assert.Equal(Quick, first.RetryIn);
        Assert.Contains("401", first.Result.FailureSummary);

        // The password was fixed meanwhile; nobody touched the save again.
        flaky.Failing = false;
        await WaitUntilAsync(() => _attempts.Any(a => !a.Result.HasFailures));

        var success = _attempts.First(a => !a.Result.HasFailures);
        Assert.False(success.IsNewFailure);
        Assert.Equal(new[] { "WebDAV" }, success.Result.Succeeded);
        Assert.Contains(flaky.Server.Requests, r => r.Method == "MOVE");

        // Once it works, no further tries are scheduled.
        var attemptsSoFar = _attempts.Count;
        await Task.Delay(800);
        Assert.Equal(attemptsSoFar, _attempts.Count);
    }

    [Fact]
    public async Task ContinuingFailure_IsNewOnlyOnce_AndRetriesAreLimited()
    {
        var flaky = new FlakyServer(failing: true);
        StartMonitor(flaky.Server.CreateBackupService(), Config(["webdav"], flaky.Server.Url),
            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150));

        await SaveGameAsync();
        await WaitUntilAsync(() => _attempts.Count >= 3);
        await Task.Delay(700);

        var attempts = _attempts.ToArray();
        Assert.Equal(3, attempts.Length);
        Assert.Equal(new[] { true, false, false }, attempts.Select(a => a.IsNewFailure));
        Assert.NotNull(attempts[0].RetryIn);
        Assert.NotNull(attempts[1].RetryIn);
        Assert.Null(attempts[2].RetryIn);
    }

    [Fact]
    public async Task PartialFailure_RetriesOnlyTheTargetThatFailed()
    {
        var flaky = new FlakyServer(failing: true);
        StartMonitor(flaky.Server.CreateBackupService(), Config(["onedrive", "webdav"], flaky.Server.Url), Quick, Quick, Quick);

        await SaveGameAsync();
        await WaitUntilAsync(() => !_attempts.IsEmpty);
        var first = _attempts.First();
        Assert.Equal(new[] { "OneDrive" }, first.Result.Succeeded);
        Assert.Equal("WebDAV", Assert.Single(first.Result.Failures).Target);
        Assert.Equal(1, OneDriveBackupCount());

        flaky.Failing = false;
        await WaitUntilAsync(() => _attempts.Any(a => !a.Result.HasFailures));

        // The retry went to WebDAV alone: OneDrive's history isn't padded with an identical copy.
        Assert.Equal(new[] { "WebDAV" }, _attempts.First(a => !a.Result.HasFailures).Result.Succeeded);
        Assert.Equal(1, OneDriveBackupCount());
    }

    [Fact]
    public async Task StopAndStartAgain_RetriesAtOnce_EvenWhenTheNextRetryIsFarAway()
    {
        var flaky = new FlakyServer(failing: true);
        var monitor = StartMonitor(flaky.Server.CreateBackupService(), Config(["webdav"], flaky.Server.Url), TimeSpan.FromHours(1));

        await SaveGameAsync();
        await WaitUntilAsync(() => !_attempts.IsEmpty);
        Assert.Equal(TimeSpan.FromHours(1), _attempts.First().RetryIn);

        // The user fixes the password in the settings, which restarts monitoring.
        flaky.Failing = false;
        monitor.Stop();
        Assert.True(monitor.Start());

        await WaitUntilAsync(() => _attempts.Any(a => !a.Result.HasFailures));
        Assert.Equal(new[] { "WebDAV" }, _attempts.First(a => !a.Result.HasFailures).Result.Succeeded);
    }

    [Fact]
    public async Task SubscriberThatThrows_DoesNotStopOtherSubscribersOrTheWorker()
    {
        var monitor = StartMonitor(new BackupService(), Config(["onedrive"]), Quick);
        monitor.BackupAttempted += _ => throw new InvalidOperationException("UI object touched from a worker thread");

        await SaveGameAsync("v1");
        await WaitUntilAsync(() => _attempts.Count == 1);
        await Task.Delay(1100); // backups are named by the second
        await SaveGameAsync("v2");
        await WaitUntilAsync(() => _attempts.Count == 2);

        Assert.Equal(2, OneDriveBackupCount());
    }

    [Fact]
    public void SaveFolderFarTooBroad_IsNotMonitored()
    {
        var config = Config(["onedrive"]);
        config.Games[0].Save = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _monitor = new MonitoringService(new BackupService(), () => config, () => "", _log.Enqueue,
            quietPeriod: Quick, maxDelay: TimeSpan.FromSeconds(10));

        Assert.False(_monitor.Start());

        Assert.Equal(0, _monitor.MonitoredCount);
        Assert.Contains(_log, line => line.Contains("game"));
    }
}

using System.Diagnostics;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public sealed class MonitoringServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_mon_{Guid.NewGuid():N}");
    private readonly string _save;
    private readonly string _cloud;

    public MonitoringServiceTests()
    {
        _save = Path.Combine(_root, "save");
        _cloud = Path.Combine(_root, "cloud");
        Directory.CreateDirectory(_save);
        Directory.CreateDirectory(_cloud);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private AppConfig Config(string method, string webDavUrl = "") => new()
    {
        CloudRoot = _cloud,
        WebDavUrl = webDavUrl,
        Games = { new GameConfig { Name = "game", Save = _save, BackupMethods = new() { method } } }
    };

    private static MonitoringService Monitor(BackupService service, AppConfig config, int quietMs = 200) =>
        new(service, () => config, () => "", _ => { },
            quietPeriod: TimeSpan.FromMilliseconds(quietMs), maxDelay: TimeSpan.FromSeconds(10));

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
                Assert.Fail("Timed out waiting for the expected backup.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task ChangeWhileBackupIsRunning_IsBackedUpAfterwards()
    {
        var firstUpload = new TaskCompletionSource();
        var releaseUploads = new TaskCompletionSource();
        var server = new FakeWebDavServer
        {
            Respond = async req =>
            {
                if (req.Method.Method == "PUT")
                {
                    firstUpload.TrySetResult();
                    await releaseUploads.Task;
                }
                return null;
            }
        };
        using var monitor = Monitor(server.CreateBackupService(), Config(Constants.MethodWebDav, server.Url));
        Assert.True(monitor.Start());

        await File.WriteAllTextAsync(Path.Combine(_save, "slot1.sav"), "v1");
        await firstUpload.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The game saves again while the first backup is still uploading...
        await File.WriteAllTextAsync(Path.Combine(_save, "slot2.sav"), "v2");
        await Task.Delay(600);
        releaseUploads.SetResult();

        // ...and that save is backed up next instead of being dropped.
        await WaitUntilAsync(() => server.Puts.Any(p => p.Path.EndsWith("/slot2.sav")));
    }

    [Fact]
    public async Task BurstOfWrites_IsBackedUpOnceAfterItSettles()
    {
        using var monitor = Monitor(new BackupService(), Config(Constants.MethodOneDrive), quietMs: 400);
        Assert.True(monitor.Start());

        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_save, $"part{i}.sav"), $"chunk {i}");
            await Task.Delay(60);
        }

        await WaitUntilAsync(() => OneDriveBackupCount() == 1);
        await Task.Delay(1000);
        Assert.Equal(1, OneDriveBackupCount());
        var backup = Directory.GetDirectories(Path.Combine(_cloud, "game"), "save_*").Single();
        Assert.Equal(5, Directory.GetFiles(backup).Length);
    }

    [Fact]
    public async Task PendingChange_SurvivesMonitoringRestart()
    {
        using var monitor = Monitor(new BackupService(), Config(Constants.MethodOneDrive), quietMs: 1000);
        Assert.True(monitor.Start());

        await File.WriteAllTextAsync(Path.Combine(_save, "slot.sav"), "progress");
        await Task.Delay(300);
        // e.g. the user edits another game while this one is waiting for its quiet period.
        monitor.RestartIfActive();

        await WaitUntilAsync(() => OneDriveBackupCount() == 1);
    }

    [Fact]
    public void ThrowingStateSubscriber_DoesNotStopMonitoring()
    {
        using var monitor = Monitor(new BackupService(), Config(Constants.MethodOneDrive));
        monitor.StateChanged += () => throw new InvalidOperationException("UI object touched from a worker thread");

        Assert.True(monitor.Start());
        monitor.RestartIfActive();

        Assert.True(monitor.IsMonitoring);
        Assert.Equal(1, monitor.MonitoredCount);
    }

    [Fact]
    public async Task StartedWithNoGames_WatchesTheFirstGameAdded()
    {
        var config = Config(Constants.MethodOneDrive);
        var game = config.Games.Single();
        config.Games.Clear();
        using var monitor = Monitor(new BackupService(), config);

        // The app's first start: nothing to watch yet, but monitoring is on.
        Assert.False(monitor.Start());
        Assert.True(monitor.IsActive);
        Assert.False(monitor.IsMonitoring);

        // The user adds their first game.
        config.Games.Add(game);
        monitor.RestartIfActive();

        Assert.Equal(1, monitor.MonitoredCount);
        await File.WriteAllTextAsync(Path.Combine(_save, "slot.sav"), "progress");
        await WaitUntilAsync(() => OneDriveBackupCount() == 1);
    }

    [Fact]
    public void StoppedByUser_StaysOffWhenGamesAreAdded()
    {
        var config = Config(Constants.MethodOneDrive);
        var game = config.Games.Single();
        using var monitor = Monitor(new BackupService(), config);
        Assert.True(monitor.Start());

        // The user stops monitoring, deletes the game and adds it again.
        monitor.Stop();
        config.Games.Clear();
        monitor.RestartIfActive();
        config.Games.Add(game);
        monitor.RestartIfActive();

        Assert.False(monitor.IsActive);
        Assert.False(monitor.IsMonitoring);
    }

    [Fact]
    public void AllGamesPaused_ResumingOneStartsMonitoringAgain()
    {
        var config = Config(Constants.MethodOneDrive);
        using var monitor = Monitor(new BackupService(), config);
        Assert.True(monitor.Start());

        config.Games[0].Paused = true;
        monitor.RestartIfActive();
        Assert.False(monitor.IsMonitoring);

        config.Games[0].Paused = false;
        monitor.RestartIfActive();
        Assert.True(monitor.IsMonitoring);
    }

    [Fact]
    public void NothingToWatch_IsNotReportedAsStopped()
    {
        var log = new List<string>();
        var config = new AppConfig { CloudRoot = _cloud };
        using var monitor = new MonitoringService(new BackupService(), () => config, () => "", log.Add);

        // e.g. a restore pauses monitoring that is still waiting for its first game.
        monitor.Start();
        monitor.RestartIfActive();
        monitor.Stop();

        // "All monitoring stopped" would be news to a user who never saw it start.
        Assert.Empty(log);
    }
}

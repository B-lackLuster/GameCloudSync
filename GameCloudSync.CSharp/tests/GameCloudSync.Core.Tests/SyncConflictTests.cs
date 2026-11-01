using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

/// <summary>Backups and restores remember what this PC and the cloud share; a change on both sides since then is a conflict.</summary>
public sealed class SyncConflictTests : IDisposable
{
    private static readonly DateTime SyncedAt = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PlayedHereAt = SyncedAt.AddHours(2);
    private static readonly DateTime PlayedElsewhereAt = SyncedAt.AddHours(5);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_conflict_{Guid.NewGuid():N}");
    private readonly string _save;
    private readonly string _cloud;
    private readonly SyncBaselineStore _baselines;
    private readonly BackupService _service;
    private readonly AppConfig _config;
    private readonly GameConfig _game;

    public SyncConflictTests()
    {
        _save = Path.Combine(_root, "save");
        _cloud = Path.Combine(_root, "cloud");
        Directory.CreateDirectory(_save);
        Directory.CreateDirectory(_cloud);
        WriteFile(Path.Combine(_save, "slot.sav"), "progress", SyncedAt);
        _baselines = new SyncBaselineStore(Path.Combine(_root, "state"));
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        _service = new BackupService(baselines: _baselines);
        _config = new AppConfig { CloudRoot = _cloud };
        _game = new GameConfig { Name = "game", Save = _save };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private static void NoLog(string _) { }

    private static long Unix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    private static void WriteFile(string path, string content, DateTime modifiedUtc)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    /// <summary>A backup another PC made later, holding a save from <paramref name="savedAt"/>.</summary>
    private void BackupFromAnotherPc(DateTime savedAt)
    {
        var dir = Path.Combine(_cloud, "game", "save_20261001_170000");
        Directory.CreateDirectory(dir);
        WriteFile(Path.Combine(dir, "slot.sav"), "progress made elsewhere", savedAt);
        // Listings put the most recently written backup folder first.
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddHours(1));
    }

    [Fact]
    public async Task Backup_RemembersTheSaveTimeItPublished()
    {
        var result = await _service.RunBackupAsync(_game, _config, "", NoLog);

        Assert.False(result.HasFailures);
        Assert.Equal(Unix(SyncedAt), _baselines.Get("game"));
    }

    [Fact]
    public async Task FailedBackup_LeavesTheBaselineAlone()
    {
        _baselines.Set("game", 1000);

        await _service.RunBackupAsync(_game, new AppConfig { CloudRoot = Path.Combine(_root, "gone") }, "", NoLog);

        Assert.Equal(1000, _baselines.Get("game"));
    }

    [Fact]
    public async Task Restore_RemembersTheSaveItBroughtBack()
    {
        BackupFromAnotherPc(PlayedElsewhereAt);

        await _service.RestoreFromOneDriveAsync(_game, "save_20261001_170000", _config, NoLog);

        Assert.Equal(Unix(PlayedElsewhereAt), _baselines.Get("game"));
    }

    [Fact]
    public void ChangedOnBothSides_NeedsBothSidesNewerThanTheLastSync()
    {
        _baselines.Set("game", 1000);

        Assert.True(_service.ChangedOnBothSides(_game, 2000, 3000));
        Assert.False(_service.ChangedOnBothSides(_game, 1000, 3000));
        Assert.False(_service.ChangedOnBothSides(_game, 2000, 1000));
        // File times a moment apart (copies, file systems) still count as the same save.
        Assert.False(_service.ChangedOnBothSides(_game, 1000 + Constants.SyncThresholdSeconds, 3000));
    }

    [Fact]
    public void ChangedOnBothSides_CantBeToldWithoutAnEarlierSync()
    {
        Assert.False(_service.ChangedOnBothSides(_game, 2000, 3000));
        Assert.False(new BackupService().ChangedOnBothSides(_game, 2000, 3000));
    }

    [Fact]
    public async Task Status_BothSidesChangedSinceTheLastSync_IsAConflictNotNewerWins()
    {
        await _service.RunBackupAsync(_game, _config, "", NoLog);
        BackupFromAnotherPc(PlayedElsewhereAt);
        WriteFile(Path.Combine(_save, "slot.sav"), "progress made here", PlayedHereAt);

        var status = await _service.GetSyncStatusAsync(_game, _config, "", checkWebDav: false);

        Assert.Equal(SyncStatusKind.Conflict, status.Kind);
        Assert.Contains(RelativeTime.FormatUnix(Unix(SyncedAt)), status.Detail);
    }

    [Fact]
    public async Task Status_OnlyThisPcChanged_IsLocalNew()
    {
        await _service.RunBackupAsync(_game, _config, "", NoLog);
        WriteFile(Path.Combine(_save, "slot.sav"), "progress made here", PlayedHereAt);

        var status = await _service.GetSyncStatusAsync(_game, _config, "", checkWebDav: false);

        Assert.Equal(SyncStatusKind.LocalNew, status.Kind);
    }

    [Fact]
    public async Task Status_OnlyTheCloudChanged_IsCloudNew()
    {
        await _service.RunBackupAsync(_game, _config, "", NoLog);
        BackupFromAnotherPc(PlayedElsewhereAt);

        var status = await _service.GetSyncStatusAsync(_game, _config, "", checkWebDav: false);

        Assert.Equal(SyncStatusKind.CloudNew, status.Kind);
    }

    [Fact]
    public async Task Status_NotSetUp_NoSavesYet_AndNotCheckedYet_AreToldApart()
    {
        var notSetUp = await _service.GetSyncStatusAsync(_game, new AppConfig(), "", checkWebDav: false);
        Assert.Equal(SyncStatusKind.NotConfigured, notSetUp.Kind);

        var empty = new GameConfig { Name = "empty", Save = Path.Combine(_root, "empty-save") };
        Directory.CreateDirectory(empty.Save);
        var noSaves = await _service.GetSyncStatusAsync(empty, _config, "", checkWebDav: false);
        Assert.Equal(SyncStatusKind.NoSaves, noSaves.Kind);

        var webDavOnly = new GameConfig { Name = "game", Save = _save, BackupMethods = new List<string> { Constants.MethodWebDav } };
        var notChecked = await _service.GetSyncStatusAsync(webDavOnly,
            new AppConfig { WebDavUrl = "https://example.test/dav" }, "", checkWebDav: false);
        Assert.Equal(SyncStatusKind.Unknown, notChecked.Kind);
    }
}

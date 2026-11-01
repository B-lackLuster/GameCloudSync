using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public sealed class RestoreSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_snap_{Guid.NewGuid():N}");
    private readonly string _save;
    private readonly AppConfig _config;
    private readonly GameConfig _game;
    private readonly BackupService _service;

    public RestoreSnapshotTests()
    {
        _save = Path.Combine(_root, "save");
        Directory.CreateDirectory(_save);
        _config = new AppConfig { CloudRoot = Path.Combine(_root, "cloud") };
        _game = new GameConfig { Name = "game", Save = _save };
        _service = new BackupService(snapshotRoot: Path.Combine(_root, "snapshots"));
        var backup = Path.Combine(_config.CloudRoot, "game", "save_20260101_120000");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "slot.sav"), "cloud progress");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private string LocalSave() => File.ReadAllText(Path.Combine(_save, "slot.sav"));

    [Fact]
    public async Task Restore_KeepsOverwrittenLocalSave_AndItCanBeRestoredBack()
    {
        await File.WriteAllTextAsync(Path.Combine(_save, "slot.sav"), "local progress");

        await _service.RestoreFromOneDriveAsync(_game, "save_20260101_120000", _config, _ => { });

        Assert.Equal("cloud progress", LocalSave());
        var snapshot = Assert.Single(_service.ListRestoreSnapshots(_game));
        Assert.Equal("local progress", await File.ReadAllTextAsync(Path.Combine(snapshot.Path, "slot.sav")));

        await _service.RestoreFromSnapshotAsync(_game, snapshot.Path, _ => { });

        Assert.Equal("local progress", LocalSave());
        // Undoing is itself undoable: the cloud version became the newest snapshot.
        var snapshots = _service.ListRestoreSnapshots(_game);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal("cloud progress", await File.ReadAllTextAsync(Path.Combine(snapshots[0].Path, "slot.sav")));
    }

    [Fact]
    public async Task FailedRestore_LeavesSaveAndNoSnapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(_save, "slot.sav"), "local progress");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            _service.RestoreFromOneDriveAsync(_game, "save_missing", _config, _ => { }));

        Assert.Equal("local progress", LocalSave());
        Assert.Empty(_service.ListRestoreSnapshots(_game));
    }

    [Fact]
    public async Task Snapshots_KeepOnlyTheNewest()
    {
        for (var i = 0; i < Constants.MaxRestoreSnapshots + 2; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_save, "slot.sav"), $"local {i}");
            await _service.RestoreFromOneDriveAsync(_game, "save_20260101_120000", _config, _ => { });
        }

        var snapshots = _service.ListRestoreSnapshots(_game);
        Assert.Equal(Constants.MaxRestoreSnapshots, snapshots.Count);
        Assert.Equal($"local {Constants.MaxRestoreSnapshots + 1}",
            await File.ReadAllTextAsync(Path.Combine(snapshots[0].Path, "slot.sav")));
    }

    [Fact]
    public async Task Restore_WithNoLocalSave_TakesNoSnapshot()
    {
        await _service.RestoreFromOneDriveAsync(_game, "save_20260101_120000", _config, _ => { });

        Assert.Equal("cloud progress", LocalSave());
        Assert.Empty(_service.ListRestoreSnapshots(_game));
    }

    [Theory]
    [InlineData("save_20260512_143000_mtime1715500200", 1715500200)]
    [InlineData("save_20260512_143000", 0)]
    public void GetBackupMTime_WebDavName_UsesEncodedMTimeElseTimestamp(string name, long encoded)
    {
        var expected = encoded > 0
            ? encoded
            : new DateTimeOffset(new DateTime(2026, 5, 12, 14, 30, 0, DateTimeKind.Local)).ToUnixTimeSeconds();
        Assert.Equal(expected, BackupService.GetBackupMTime(new BackupSource("WebDAV", Constants.MethodWebDav, name)));
    }
}

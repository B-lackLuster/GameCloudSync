using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public class BackupServiceTests : IDisposable
{
    private readonly string _root;

    public BackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gcs_bk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public async Task RestoreFromOneDrive_MissingBackup_PreservesExistingSave()
    {
        var save = Path.Combine(_root, "save");
        Directory.CreateDirectory(save);
        await File.WriteAllTextAsync(Path.Combine(save, "progress.dat"), "original");
        var game = new GameConfig { Name = "game", Save = save };

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new BackupService()
            .RestoreFromOneDriveAsync(game, "save_missing", new AppConfig { CloudRoot = _root }, _ => { }));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(save, "progress.dat")));
        Assert.Empty(Directory.GetDirectories(_root, ".gcs-*"));
    }

    [Fact]
    public async Task RestoreDirectory_FailedPreparation_PreservesOriginalAndCleansStaging()
    {
        var save = Path.Combine(_root, "save");
        Directory.CreateDirectory(save);
        await File.WriteAllTextAsync(Path.Combine(save, "progress.dat"), "original");

        await Assert.ThrowsAsync<IOException>(() => FileUtil.RestoreDirectoryAsync(save, async staging =>
        {
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "partial.dat"), "partial");
            throw new IOException("Simulated read failure");
        }));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(save, "progress.dat")));
        Assert.Empty(Directory.GetDirectories(_root, ".gcs-*"));
    }

    [Fact]
    public async Task RestoreDirectory_CancelledBeforeCommit_PreservesOriginal()
    {
        var save = Path.Combine(_root, "save");
        Directory.CreateDirectory(save);
        await File.WriteAllTextAsync(Path.Combine(save, "progress.dat"), "original");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileUtil.RestoreDirectoryAsync(save, staging =>
        {
            Directory.CreateDirectory(staging);
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(save, "progress.dat")));
        Assert.Empty(Directory.GetDirectories(_root, ".gcs-*"));
    }

    [Fact]
    public async Task RestoreFromOneDrive_Success_ReplacesStaleFiles()
    {
        var save = Path.Combine(_root, "save");
        var backup = Path.Combine(_root, "cloud", "game", "save_test");
        Directory.CreateDirectory(save);
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(save, "stale.dat"), "old");
        await File.WriteAllTextAsync(Path.Combine(backup, "progress.dat"), "new");
        await new BackupService().RestoreFromOneDriveAsync(new GameConfig { Name = "game", Save = save },
            "save_test", new AppConfig { CloudRoot = Path.Combine(_root, "cloud") }, _ => { });
        Assert.False(File.Exists(Path.Combine(save, "stale.dat")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(save, "progress.dat")));
        Assert.Empty(Directory.GetDirectories(_root, ".gcs-*"));
    }

    [Fact]
    public async Task RestoreFromOneDrive_CreatesMissingSaveDirectoryChain()
    {
        // Cloud side: one backup with a file.
        var cloudRoot = Path.Combine(_root, "OneDrive", "GameCloud");
        var backupDir = Path.Combine(cloudRoot, "仁王3", "save_20260512_143000");
        Directory.CreateDirectory(backupDir);
        await File.WriteAllTextAsync(Path.Combine(backupDir, "save.dat"), "data");

        // Local side: the game was never launched, so the whole path is missing.
        var saveDir = Path.Combine(_root, "Local", "AppData", "KoeiTecmo", "NIOH3", "Savedata", "76561198325454168");
        var game = new GameConfig
        {
            Name = "仁王3",
            Save = saveDir,
            BackupMethods = new List<string> { Constants.MethodOneDrive }
        };
        var config = new AppConfig { CloudRoot = cloudRoot };
        var service = new BackupService();

        await service.RestoreFromOneDriveAsync(game, "save_20260512_143000", config, _ => { });

        Assert.True(Directory.Exists(saveDir));
        Assert.Equal("data", await File.ReadAllTextAsync(Path.Combine(saveDir, "save.dat")));
    }

    [Fact]
    public async Task GetSyncStatus_EmptyLocalSaveWithCloudBackup_ReportsCloudNew()
    {
        var cloudRoot = Path.Combine(_root, "OneDrive", "GameCloud");
        var backupDir = Path.Combine(cloudRoot, "战神5", "save_20260512_143000");
        Directory.CreateDirectory(backupDir);
        var file = Path.Combine(backupDir, "save.dat");
        await File.WriteAllTextAsync(file, "data");
        var backupTime = new DateTime(2026, 5, 12, 14, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, backupTime);

        // Local save directory exists but is empty (auto-created by monitoring).
        var saveDir = Path.Combine(_root, "Local", "EmptySave");
        Directory.CreateDirectory(saveDir);

        var game = new GameConfig
        {
            Name = "战神5",
            Save = saveDir,
            BackupMethods = new List<string> { Constants.MethodOneDrive }
        };
        var config = new AppConfig { CloudRoot = cloudRoot };
        var service = new BackupService();

        var status = await service.GetSyncStatusAsync(game, config, "", checkWebDav: false);

        Assert.Equal(SyncStatusKind.CloudNew, status.Kind);
        Assert.Contains("本地无档", status.Text);
    }
}

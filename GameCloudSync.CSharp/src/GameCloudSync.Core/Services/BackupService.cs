using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Services;

public sealed class BackupService
{
    private readonly Func<string, string, WebDavClient> _webDavFactory;

    public BackupService(Func<string, string, WebDavClient>? webDavFactory = null)
    {
        _webDavFactory = webDavFactory
            ?? ((url, password) => new WebDavClient(url, "", password));
    }

    // ---------------- Backup ----------------

    public async Task<List<string>> PerformBackupAsync(
        GameConfig game,
        AppConfig config,
        string webDavPassword,
        Action<string> log,
        CancellationToken ct = default)
    {
        var success = new List<string>();
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // OneDrive (a local cloud folder)
        if (game.HasMethod(Constants.MethodOneDrive) && !string.IsNullOrEmpty(config.CloudRoot))
        {
            try
            {
                var backupFolder = $"save_{timestamp}";
                var target = Path.Combine(config.CloudRoot, game.Name, backupFolder);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await FileUtil.CopyDirectoryAsync(game.Save, target, ct);
                await CleanupOneDriveBackupsAsync(game.Name, config.CloudRoot, game.MaxBackups, ct);
                success.Add("OneDrive");
            }
            catch (Exception ex)
            {
                log($"[{DateTime.Now:HH:mm:ss}] ✗ {game.Name}: OneDrive 备份失败 ({ex.Message})");
            }
        }

        // WebDAV
        if (game.HasMethod(Constants.MethodWebDav) && !string.IsNullOrEmpty(config.WebDavUrl))
        {
            try
            {
                var localMtime = FileUtil.GetLatestMTime(game.Save);
                var backupFolder = $"save_{timestamp}_mtime{localMtime}";
                var remoteDir = $"{game.Name}/{backupFolder}";
                using var wc = _webDavFactory(config.WebDavUrl, webDavPassword);
                if (await wc.UploadDirAsync(game.Save, remoteDir, ct))
                {
                    await CleanupWebDavBackupsAsync(wc, game.Name, game.MaxBackups, ct);
                    success.Add("WebDAV");
                }
                else
                {
                    log($"[{DateTime.Now:HH:mm:ss}] ✗ {game.Name}: WebDAV 备份上传失败");
                }
            }
            catch (Exception ex)
            {
                log($"[{DateTime.Now:HH:mm:ss}] ✗ {game.Name}: WebDAV 备份失败 ({ex.Message})");
            }
        }

        if (success.Count > 0)
            log($"[{DateTime.Now:HH:mm:ss}] ✓ {game.Name}: 自动备份成功 ({string.Join(", ", success)})");
        return success;
    }

    // ---------------- Restore ----------------

    public async Task RestoreFromOneDriveAsync(
        GameConfig game,
        string backupName,
        AppConfig config,
        Action<string> log,
        CancellationToken ct = default)
    {
        var backupPath = Path.Combine(config.CloudRoot, game.Name, backupName);
        FileUtil.DeleteDirectorySafe(game.Save);
        await FileUtil.CopyDirectoryAsync(backupPath, game.Save, ct);
        log($"⇩ ✓ {game.Name}: 从 OneDrive 备份 [{backupName}] 恢复");
    }

    public async Task RestoreFromWebDavAsync(
        GameConfig game,
        string backupName,
        AppConfig config,
        string webDavPassword,
        Action<string> log,
        CancellationToken ct = default)
    {
        using var wc = _webDavFactory(config.WebDavUrl, webDavPassword);
        var remoteDir = $"{game.Name}/{backupName}";
        log($"⇩ ⚡ WebDAV 下载: {remoteDir} ...");
        var tmpDir = game.Save + ".tmp";
        FileUtil.DeleteDirectorySafe(tmpDir);
        var count = await wc.DownloadDirAsync(remoteDir, tmpDir, ct);
        if (count == 0)
        {
            FileUtil.DeleteDirectorySafe(tmpDir);
            throw new InvalidOperationException(
                $"WebDAV 下载失败：'{remoteDir}' 未获取到任何文件（检查目录是否存在、文件名编码是否匹配）");
        }
        FileUtil.DeleteDirectorySafe(game.Save);
        Directory.Move(tmpDir, game.Save);
        FileUtil.ApplyBackupMTime(backupName, game.Save);
        log($"⇩ ⚡ WebDAV 下载完成: {count} 个文件");
        log($"⇩ ✓ {game.Name}: 从 WebDAV 备份 [{backupName}] 恢复");
    }

    // ---------------- Listing ----------------

    public Task<List<string>> ListOneDriveBackupsAsync(GameConfig game, AppConfig config, CancellationToken ct = default)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(config.CloudRoot))
            return Task.FromResult(result);
        var gameCloudDir = Path.Combine(config.CloudRoot, game.Name);
        if (!Directory.Exists(gameCloudDir))
            return Task.FromResult(result);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(gameCloudDir))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith("save_", StringComparison.Ordinal))
                    result.Add(name);
            }
        }
        catch
        {
            return Task.FromResult(new List<string>());
        }
        result.Sort((a, b) =>
        {
            var ma = SafeDirMTime(Path.Combine(gameCloudDir, a));
            var mb = SafeDirMTime(Path.Combine(gameCloudDir, b));
            return mb.CompareTo(ma);
        });
        return Task.FromResult(result);
    }

    public async Task<List<BackupEntry>> ListWebDavBackupsAsync(
        GameConfig game,
        AppConfig config,
        string webDavPassword,
        CancellationToken ct = default)
    {
        var result = new List<BackupEntry>();
        if (string.IsNullOrEmpty(config.WebDavUrl))
            return result;
        try
        {
            using var wc = _webDavFactory(config.WebDavUrl, webDavPassword);
            var items = await wc.PropFindAsync(game.Name, ct: ct);
            foreach (var (href, name) in items)
            {
                if (!name.StartsWith("save_", StringComparison.Ordinal))
                    continue;
                var (ts, _) = BackupName.Parse(name);
                var dateText = BackupName.ToDateTime(ts)?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知时间";
                result.Add(new BackupEntry(name, href, dateText, 0));
            }
            result.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
        }
        catch
        {
            // return what we have
        }
        return result;
    }

    // ---------------- Sync status ----------------

    public async Task<SyncStatus> GetSyncStatusAsync(
        GameConfig game,
        AppConfig config,
        string webDavPassword,
        bool checkWebDav,
        CancellationToken ct = default)
    {
        if (game.Paused)
            return new SyncStatus("⏸ 已暂停", SyncStatusKind.Paused);
        if (game.BackupMethods.Count == 0)
            return new SyncStatus("未设置备份方式", SyncStatusKind.None);

        var hasOneDrive = game.HasMethod(Constants.MethodOneDrive) && !string.IsNullOrEmpty(config.CloudRoot);
        var hasWebDav = game.HasMethod(Constants.MethodWebDav) && !string.IsNullOrEmpty(config.WebDavUrl);
        if (!hasOneDrive && !hasWebDav)
            return new SyncStatus("备份目标未配置", SyncStatusKind.None);

        var localTime = FileUtil.GetLatestMTime(game.Save);
        long odCloudTime = 0;
        if (hasOneDrive)
        {
            var odNames = await ListOneDriveBackupsAsync(game, config, ct);
            if (odNames.Count > 0)
            {
                var latestDir = Path.Combine(config.CloudRoot, game.Name, odNames[0]);
                odCloudTime = FileUtil.GetLatestMTime(latestDir);
            }
        }

        long wdBackupMTime = 0;
        long wdDirTs = 0;
        if (hasWebDav && checkWebDav)
        {
            var wdBackups = await ListWebDavBackupsAsync(game, config, webDavPassword, ct);
            if (wdBackups.Count > 0)
            {
                var (ts, bkMtime) = BackupName.Parse(wdBackups[0].Name);
                var dt = BackupName.ToDateTime(ts);
                if (bkMtime > 0)
                {
                    wdBackupMTime = bkMtime;
                }
                else if (dt is not null)
                {
                    wdDirTs = new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Local)).ToUnixTimeSeconds();
                }
            }
        }

        if (hasWebDav && !checkWebDav && odCloudTime == 0)
            return new SyncStatus("WebDAV 未检查（点刷新）", SyncStatusKind.None);

        long cloudTime;
        if (odCloudTime > 0)
            cloudTime = odCloudTime;
        else if (wdBackupMTime > 0)
            cloudTime = wdBackupMTime;
        else
            cloudTime = wdDirTs;

        if (localTime == 0 && cloudTime == 0)
            return new SyncStatus("无存档记录", SyncStatusKind.None);
        if (cloudTime == 0)
            return new SyncStatus("等待首次备份...", SyncStatusKind.LocalNew);
        if (localTime == 0)
            return new SyncStatus("☁ 本地无档 (可下载)", SyncStatusKind.CloudNew);
        if (localTime - cloudTime > Constants.SyncThresholdSeconds)
            return new SyncStatus("⚠ 本地较新 (未备份)", SyncStatusKind.LocalNew);
        if ((odCloudTime > 0 || wdBackupMTime > 0) &&
            cloudTime - localTime > Constants.SyncThresholdSeconds)
            return new SyncStatus("☁ 云端较新 (请下载)", SyncStatusKind.CloudNew);
        return new SyncStatus("✓ 已完全同步", SyncStatusKind.Synced);
    }

    public async Task<BackupSource?> FindLatestSourceAsync(
        GameConfig game,
        AppConfig config,
        string webDavPassword,
        CancellationToken ct = default)
    {
        var pref = config.DefaultDownloadSource;
        BackupSource? odBest = null;
        long odTs = 0;
        BackupSource? wdBest = null;
        long wdTs = 0;

        if (game.HasMethod(Constants.MethodOneDrive) && !string.IsNullOrEmpty(config.CloudRoot))
        {
            var names = await ListOneDriveBackupsAsync(game, config, ct);
            if (names.Count > 0)
            {
                var latest = Path.Combine(config.CloudRoot, game.Name, names[0]);
                odTs = FileUtil.GetLatestMTime(latest);
                odBest = new BackupSource("OneDrive", Constants.MethodOneDrive, latest);
            }
        }

        if (game.HasMethod(Constants.MethodWebDav) && !string.IsNullOrEmpty(config.WebDavUrl))
        {
            var backups = await ListWebDavBackupsAsync(game, config, webDavPassword, ct);
            if (backups.Count > 0)
            {
                var (ts, bkMtime) = BackupName.Parse(backups[0].Name);
                if (bkMtime > 0)
                {
                    wdTs = bkMtime;
                }
                else if (BackupName.ToDateTime(ts) is { } dt)
                {
                    wdTs = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)).ToUnixTimeSeconds();
                }
                wdBest = new BackupSource("WebDAV", Constants.MethodWebDav, backups[0].Name);
            }
        }

        if (pref == Constants.MethodOneDrive && odBest is not null)
            return odBest;
        if (pref == Constants.MethodWebDav && wdBest is not null)
            return wdBest;
        if (odTs > wdTs)
            return odBest;
        return wdBest ?? odBest;
    }

    // ---------------- Cleanup ----------------

    public Task CleanupOneDriveBackupsAsync(string gameName, string cloudRoot, int maxBackups, CancellationToken ct = default)
    {
        if (maxBackups <= 0)
            maxBackups = Constants.DefaultMaxBackups;
        var gameCloudDir = Path.Combine(cloudRoot, gameName);
        if (!Directory.Exists(gameCloudDir))
            return Task.CompletedTask;
        var backups = Directory.EnumerateDirectories(gameCloudDir)
            .Where(d => Path.GetFileName(d).StartsWith("save_", StringComparison.Ordinal))
            .OrderBy(SafeDirMTime)
            .ToList();
        var excess = backups.Count - maxBackups;
        for (var i = 0; i < excess; i++)
        {
            ct.ThrowIfCancellationRequested();
            FileUtil.DeleteDirectorySafe(backups[i]);
        }
        return Task.CompletedTask;
    }

    public async Task CleanupWebDavBackupsAsync(WebDavClient wc, string gameName, int maxBackups, CancellationToken ct = default)
    {
        if (maxBackups <= 0)
            maxBackups = Constants.DefaultMaxBackups;
        var items = await wc.PropFindAsync(gameName, ct: ct);
        var names = items.Where(i => i.Name.StartsWith("save_", StringComparison.Ordinal))
            .Select(i => i.Name)
            .OrderByDescending(n => n)
            .ToList();
        for (var i = names.Count - 1; i >= maxBackups; i--)
        {
            ct.ThrowIfCancellationRequested();
            await wc.DeletePathAsync($"{gameName}/{names[i]}", ct);
        }
    }

    private static long SafeDirMTime(string dir)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(dir)).ToUnixTimeSeconds();
        }
        catch
        {
            return 0;
        }
    }
}

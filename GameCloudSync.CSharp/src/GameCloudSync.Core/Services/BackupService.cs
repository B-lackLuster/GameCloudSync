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
                log($"[{DateTime.Now:HH:mm:ss}] {Localization.T("Log.BackupFailed", game.Name, "OneDrive", ex.Message)}");
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
                    log($"[{DateTime.Now:HH:mm:ss}] {Localization.T("Log.WebDavUploadFailed", game.Name)}");
                }
            }
            catch (Exception ex)
            {
                log($"[{DateTime.Now:HH:mm:ss}] {Localization.T("Log.BackupFailed", game.Name, "WebDAV", ex.Message)}");
            }
        }

        if (success.Count > 0)
            log($"[{DateTime.Now:HH:mm:ss}] {Localization.T("Log.BackupSucceeded", game.Name, string.Join(", ", success))}");
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
        // The game may never have been launched on this machine; create the
        // full save path (including missing parents) before copying.
        Directory.CreateDirectory(game.Save);
        await FileUtil.CopyDirectoryAsync(backupPath, game.Save, ct);
        log(Localization.T("Log.RestoreOdDone", game.Name, backupName));
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
        log(Localization.T("Log.RestoreWdStart", remoteDir));
        var tmpDir = game.Save + ".tmp";
        FileUtil.DeleteDirectorySafe(tmpDir);
        var count = await wc.DownloadDirAsync(remoteDir, tmpDir, ct);
        if (count == 0)
        {
            FileUtil.DeleteDirectorySafe(tmpDir);
            throw new InvalidOperationException(
                Localization.T("Log.RestoreWdEmpty", remoteDir));
        }
        FileUtil.DeleteDirectorySafe(game.Save);
        // Directory.Move requires the destination's parent to exist. On a new
        // machine the whole save path may be missing because the game was
        // never launched, so create the parent chain explicitly.
        var saveParent = Path.GetDirectoryName(game.Save);
        if (!string.IsNullOrEmpty(saveParent))
            Directory.CreateDirectory(saveParent);
        Directory.Move(tmpDir, game.Save);
        FileUtil.ApplyBackupMTime(backupName, game.Save);
        log(Localization.T("Log.RestoreWdDone", count));
        log(Localization.T("Log.RestoreWdDoneFull", game.Name, backupName));
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
                var dateText = BackupName.ToDateTime(ts)?.ToString("yyyy-MM-dd HH:mm:ss") ?? Localization.T("Common.UnknownTime");
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
            return new SyncStatus(Localization.T("Status.Paused"), SyncStatusKind.Paused);
        if (game.BackupMethods.Count == 0)
            return new SyncStatus(Localization.T("Status.NoMethods"), SyncStatusKind.None);

        var hasOneDrive = game.HasMethod(Constants.MethodOneDrive) && !string.IsNullOrEmpty(config.CloudRoot);
        var hasWebDav = game.HasMethod(Constants.MethodWebDav) && !string.IsNullOrEmpty(config.WebDavUrl);
        if (!hasOneDrive && !hasWebDav)
            return new SyncStatus(Localization.T("Status.NoTarget"), SyncStatusKind.None);

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
            return new SyncStatus(Localization.T("Status.WebDavUnchecked"), SyncStatusKind.None);

        long cloudTime;
        if (odCloudTime > 0)
            cloudTime = odCloudTime;
        else if (wdBackupMTime > 0)
            cloudTime = wdBackupMTime;
        else
            cloudTime = wdDirTs;

        if (localTime == 0 && cloudTime == 0)
            return new SyncStatus(Localization.T("Status.NoSaves"), SyncStatusKind.None);
        if (cloudTime == 0)
            return new SyncStatus(Localization.T("Status.WaitingFirst"), SyncStatusKind.LocalNew);
        if (localTime == 0)
            return new SyncStatus(Localization.T("Status.LocalEmpty"), SyncStatusKind.CloudNew);
        if (localTime - cloudTime > Constants.SyncThresholdSeconds)
            return new SyncStatus(Localization.T("Status.LocalNew"), SyncStatusKind.LocalNew);
        if ((odCloudTime > 0 || wdBackupMTime > 0) &&
            cloudTime - localTime > Constants.SyncThresholdSeconds)
            return new SyncStatus(Localization.T("Status.CloudNew"), SyncStatusKind.CloudNew);
        return new SyncStatus(Localization.T("Status.Synced"), SyncStatusKind.Synced);
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

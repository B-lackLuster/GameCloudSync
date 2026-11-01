using System.Collections.Concurrent;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Services;

public sealed class BackupService
{
    private const string SnapshotPrefix = "pre-restore_";
    private static readonly TimeSpan StaleIncompleteAge = TimeSpan.FromHours(24);

    // WebDAV servers that rejected MOVE; backups to them are uploaded in place.
    private static readonly ConcurrentDictionary<string, bool> MoveUnsupported = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, string, WebDavClient> _webDavFactory;
    private readonly string? _snapshotRoot;

    /// <param name="snapshotRoot">Where the local save is kept before a restore overwrites it; null disables snapshots.</param>
    public BackupService(Func<string, string, WebDavClient>? webDavFactory = null, string? snapshotRoot = null)
    {
        _webDavFactory = webDavFactory
            ?? ((url, password) => new WebDavClient(url, "", password));
        _snapshotRoot = snapshotRoot;
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
        var toOneDrive = game.HasMethod(Constants.MethodOneDrive) && !string.IsNullOrEmpty(config.CloudRoot);
        var toWebDav = game.HasMethod(Constants.MethodWebDav) && !string.IsNullOrEmpty(config.WebDavUrl);

        // An upload can take minutes, so WebDAV reads from a private copy: the game's own files
        // are only read for the length of one local copy and never held open during the upload.
        string? snapshot = null;
        try
        {
            if (toWebDav)
            {
                snapshot = Path.Combine(Path.GetTempPath(), $"gcs_snapshot_{Guid.NewGuid():N}");
                try
                {
                    if (!await FileUtil.CopyDirectoryConsistentAsync(game.Save, snapshot, ct))
                        log(Localization.T("Log.BackupUnstable", game.Name));
                }
                catch (Exception ex)
                {
                    log(Localization.T("Log.BackupFailed", game.Name, toOneDrive ? "OneDrive, WebDAV" : "WebDAV", ex.Message));
                    return success;
                }
            }

            if (toOneDrive)
            {
                try
                {
                    await PublishToOneDriveAsync(game, config.CloudRoot, snapshot, timestamp, log, ct);
                    success.Add("OneDrive");
                }
                catch (Exception ex)
                {
                    log(Localization.T("Log.BackupFailed", game.Name, "OneDrive", ex.Message));
                }
                if (success.Contains("OneDrive"))
                {
                    try
                    {
                        await CleanupOneDriveBackupsAsync(game.Name, config.CloudRoot, game.MaxBackups, ct);
                    }
                    catch (Exception ex)
                    {
                        log(Localization.T("Log.CleanupFailed", game.Name, "OneDrive", ex.Message));
                    }
                }
            }

            if (toWebDav)
            {
                using var wc = _webDavFactory(config.WebDavUrl, webDavPassword);
                try
                {
                    await PublishToWebDavAsync(wc, game.Name, config.WebDavUrl, snapshot!, timestamp, log, ct);
                    success.Add("WebDAV");
                }
                catch (Exception ex)
                {
                    log(Localization.T("Log.BackupFailed", game.Name, "WebDAV", ex.Message));
                }
                if (success.Contains("WebDAV"))
                {
                    try
                    {
                        await CleanupWebDavBackupsAsync(wc, game.Name, game.MaxBackups, ct);
                    }
                    catch (Exception ex)
                    {
                        log(Localization.T("Log.CleanupFailed", game.Name, "WebDAV", ex.Message));
                    }
                }
            }
        }
        finally
        {
            if (snapshot is not null)
            {
                try { FileUtil.DeleteDirectorySafe(snapshot); } catch { /* temp folder, best effort */ }
            }
        }

        if (success.Count > 0)
            log(Localization.T("Log.BackupSucceeded", game.Name, string.Join(", ", success)));
        return success;
    }

    private static async Task PublishToOneDriveAsync(GameConfig game, string cloudRoot, string? snapshot,
        string timestamp, Action<string> log, CancellationToken ct)
    {
        var gameCloudDir = Path.Combine(cloudRoot, game.Name);
        Directory.CreateDirectory(gameCloudDir);
        var finalName = $"save_{timestamp}";
        for (var n = 2; Directory.Exists(Path.Combine(gameCloudDir, finalName)); n++)
            finalName = $"save_{timestamp}_{n}";

        // Written under a name that listings ignore and renamed once complete, so a failed or
        // interrupted backup can never be mistaken for the newest save.
        var incomplete = Path.Combine(gameCloudDir, Constants.IncompleteBackupPrefix + finalName);
        try
        {
            if (snapshot is not null)
                await FileUtil.CopyDirectoryAsync(snapshot, incomplete, ct);
            else if (!await FileUtil.CopyDirectoryConsistentAsync(game.Save, incomplete, ct))
                log(Localization.T("Log.BackupUnstable", game.Name));
            Directory.Move(incomplete, Path.Combine(gameCloudDir, finalName));
        }
        catch
        {
            try { FileUtil.DeleteDirectorySafe(incomplete); } catch { /* stale leftovers are swept by cleanup */ }
            throw;
        }
    }

    private static async Task PublishToWebDavAsync(WebDavClient wc, string gameName, string serverUrl, string source,
        string timestamp, Action<string> log, CancellationToken ct)
    {
        var backupName = $"save_{timestamp}_mtime{FileUtil.GetLatestMTime(source)}";
        var finalDir = $"{gameName}/{backupName}";
        if (MoveUnsupported.ContainsKey(serverUrl))
        {
            await UploadInPlaceAsync(wc, source, finalDir, ct);
            return;
        }

        var incompleteDir = $"{gameName}/{Constants.IncompleteBackupPrefix}{backupName}";
        var moved = false;
        try
        {
            await UploadOrThrowAsync(wc, source, incompleteDir, ct);
            var status = await wc.MoveAsync(incompleteDir, finalDir, collection: true, overwrite: true, ct);
            if (status is 201 or 204)
            {
                moved = true;
                return;
            }
            // No server-side rename: fall back to uploading in place, as older versions did.
            if (status is not (429 or 503 or 504))
                MoveUnsupported[serverUrl] = true;
            log(Localization.T("Log.WebDavMoveFallback", status));
            await UploadInPlaceAsync(wc, source, finalDir, ct);
        }
        finally
        {
            if (!moved)
            {
                try { await wc.DeleteCollectionAsync(incompleteDir, CancellationToken.None); } catch { /* swept by cleanup */ }
            }
        }
    }

    private static async Task UploadInPlaceAsync(WebDavClient wc, string source, string finalDir, CancellationToken ct)
    {
        try
        {
            await UploadOrThrowAsync(wc, source, finalDir, ct);
        }
        catch
        {
            // Without MOVE the only protection is removing what was half uploaded.
            try { await wc.DeleteCollectionAsync(finalDir, CancellationToken.None); } catch { /* best effort */ }
            throw;
        }
    }

    private static async Task UploadOrThrowAsync(WebDavClient wc, string source, string remoteDir, CancellationToken ct)
    {
        if (!await wc.UploadDirAsync(source, remoteDir, ct))
            throw new IOException(Localization.T("Err.WebDavUploadIncomplete"));
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
        await RestoreWithSnapshotAsync(game, log, () => FileUtil.RestoreDirectoryAsync(game.Save,
            staging => FileUtil.CopyDirectoryAsync(backupPath, staging, ct), ct), ct);
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
        var count = 0;
        await RestoreWithSnapshotAsync(game, log, () => FileUtil.RestoreDirectoryAsync(game.Save, async staging =>
        {
            count = await wc.DownloadDirAsync(remoteDir, staging, ct);
            if (count == 0)
                throw new InvalidOperationException(Localization.T("Log.RestoreWdEmpty", remoteDir));
            FileUtil.ApplyBackupMTime(backupName, staging);
        }, ct), ct);
        log(Localization.T("Log.RestoreWdDone", count));
        log(Localization.T("Log.RestoreWdDoneFull", game.Name, backupName));
    }

    // ---------------- Pre-restore snapshots ----------------

    public IReadOnlyList<RestoreSnapshot> ListRestoreSnapshots(GameConfig game)
    {
        var dir = SnapshotDirFor(game);
        if (dir is null || !Directory.Exists(dir))
            return Array.Empty<RestoreSnapshot>();
        return Directory.EnumerateDirectories(dir, SnapshotPrefix + "*")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => new RestoreSnapshot(path, SnapshotTime(path), FileUtil.GetFolderSize(path)))
            .ToList();
    }

    public async Task RestoreFromSnapshotAsync(GameConfig game, string snapshotPath, Action<string> log,
        CancellationToken ct = default)
    {
        // The current save is snapshotted too, so undoing a restore can itself be undone.
        await RestoreWithSnapshotAsync(game, log, () => FileUtil.RestoreDirectoryAsync(game.Save,
            staging => FileUtil.CopyDirectoryAsync(snapshotPath, staging, ct), ct), ct);
        log(Localization.T("Log.SnapshotRestored", game.Name, Path.GetFileName(snapshotPath)));
    }

    private async Task RestoreWithSnapshotAsync(GameConfig game, Action<string> log, Func<Task> restore,
        CancellationToken ct)
    {
        var snapshot = await CreateRestoreSnapshotAsync(game, ct);
        try
        {
            await restore();
        }
        catch
        {
            // A failed restore leaves the save in place, so its snapshot is redundant — unless
            // the save folder is gone, in which case the snapshot is the only copy left.
            if (snapshot is not null && Directory.Exists(game.Save))
            {
                try { FileUtil.DeleteDirectorySafe(snapshot); } catch { /* best effort */ }
            }
            throw;
        }
        if (snapshot is null)
            return;
        log(Localization.T("Log.SnapshotSaved", game.Name, snapshot));
        PruneSnapshots(game);
    }

    private async Task<string?> CreateRestoreSnapshotAsync(GameConfig game, CancellationToken ct)
    {
        var dir = SnapshotDirFor(game);
        if (dir is null || !FileUtil.HasAnyFile(game.Save))
            return null;
        var path = Path.Combine(dir, $"{SnapshotPrefix}{DateTime.Now:yyyyMMdd_HHmmss_fff}");
        try
        {
            await FileUtil.CopyDirectoryAsync(game.Save, path, ct);
            return path;
        }
        catch (Exception ex)
        {
            try { FileUtil.DeleteDirectorySafe(path); } catch { /* best effort */ }
            throw new IOException(Localization.T("Err.SnapshotFailed", ex.Message), ex);
        }
    }

    private void PruneSnapshots(GameConfig game)
    {
        foreach (var old in ListRestoreSnapshots(game).Skip(Constants.MaxRestoreSnapshots))
        {
            try { FileUtil.DeleteDirectorySafe(old.Path); } catch { /* retried on the next restore */ }
        }
    }

    private string? SnapshotDirFor(GameConfig game) =>
        _snapshotRoot is null ? null : Path.Combine(_snapshotRoot, SafeFolderName(game.Name));

    private static DateTime SnapshotTime(string path)
    {
        var stamp = Path.GetFileName(path)[SnapshotPrefix.Length..];
        return DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var created)
            ? created
            : Directory.GetCreationTime(path);
    }

    private static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return safe.Length == 0 ? "_" : safe;
    }

    // ---------------- Rename ----------------

    /// <summary>
    /// Moves a game's backup folders after the game was renamed, because backups are stored
    /// under the game name. Returns a description of every target that could not be renamed.
    /// </summary>
    public async Task<List<string>> RenameGameStorageAsync(
        string oldName,
        string newName,
        AppConfig config,
        string webDavPassword,
        CancellationToken ct = default)
    {
        var failures = new List<string>();
        // Windows folders ignore case, so a case-only rename needs no move on OneDrive.
        if (!string.IsNullOrEmpty(config.CloudRoot) && !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            var from = Path.Combine(config.CloudRoot, oldName);
            var to = Path.Combine(config.CloudRoot, newName);
            if (Directory.Exists(from))
            {
                if (Directory.Exists(to))
                {
                    failures.Add(Localization.T("Rename.TargetExists", "OneDrive", to));
                }
                else
                {
                    try
                    {
                        Directory.Move(from, to);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(Localization.T("Rename.Failed", "OneDrive", ex.Message));
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(config.WebDavUrl) && !string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            try
            {
                using var wc = _webDavFactory(config.WebDavUrl, webDavPassword);
                var status = await wc.MoveAsync(oldName, newName, collection: true, overwrite: false, ct);
                if (status == 412)
                    failures.Add(Localization.T("Rename.TargetExists", "WebDAV", newName));
                else if (status is not (201 or 204 or 404))
                    failures.Add(Localization.T("Rename.Failed", "WebDAV", $"HTTP {status}"));
            }
            catch (Exception ex)
            {
                failures.Add(Localization.T("Rename.Failed", "WebDAV", ex.Message));
            }
        }
        return failures;
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

    /// <param name="preference">Overrides <see cref="AppConfig.DefaultDownloadSource"/> for this lookup.</param>
    public async Task<BackupSource?> FindLatestSourceAsync(
        GameConfig game,
        AppConfig config,
        string webDavPassword,
        string? preference = null,
        CancellationToken ct = default)
    {
        var pref = preference ?? config.DefaultDownloadSource;
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
                wdBest = new BackupSource("WebDAV", Constants.MethodWebDav, backups[0].Name);
                wdTs = GetBackupMTime(wdBest);
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

    /// <summary>The newest save time a backup holds: its files' mtime, else the time encoded in its name.</summary>
    public static long GetBackupMTime(BackupSource source)
    {
        if (source.Method == Constants.MethodOneDrive)
            return FileUtil.GetLatestMTime(source.Path);
        var (ts, mtime) = BackupName.Parse(source.Path);
        if (mtime > 0)
            return mtime;
        return BackupName.ToDateTime(ts) is { } dt
            ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)).ToUnixTimeSeconds()
            : 0;
    }

    // ---------------- Cleanup ----------------

    public Task CleanupOneDriveBackupsAsync(string gameName, string cloudRoot, int maxBackups, CancellationToken ct = default)
    {
        if (maxBackups <= 0)
            maxBackups = Constants.DefaultMaxBackups;
        var gameCloudDir = Path.Combine(cloudRoot, gameName);
        if (!Directory.Exists(gameCloudDir))
            return Task.CompletedTask;

        // Leftovers of backups that were interrupted (crash, power loss) long ago.
        foreach (var stale in Directory.EnumerateDirectories(gameCloudDir, Constants.IncompleteBackupPrefix + "*")
                     .Where(d => DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > StaleIncompleteAge)
                     .ToList())
        {
            try { FileUtil.DeleteDirectorySafe(stale); } catch { /* retried next time */ }
        }

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
        foreach (var stale in items.Select(i => i.Name).Where(IsStaleIncomplete).ToList())
        {
            try { await wc.DeleteCollectionAsync($"{gameName}/{stale}", ct); } catch { /* retried next time */ }
        }

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

    private static bool IsStaleIncomplete(string name)
    {
        if (!name.StartsWith(Constants.IncompleteBackupPrefix, StringComparison.Ordinal))
            return false;
        var (ts, _) = BackupName.Parse(name[Constants.IncompleteBackupPrefix.Length..]);
        return BackupName.ToDateTime(ts) is { } started && DateTime.Now - started > StaleIncompleteAge;
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

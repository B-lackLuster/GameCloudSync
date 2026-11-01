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
    private readonly SyncBaselineStore? _baselines;

    /// <param name="snapshotRoot">Where the local save is kept before a restore overwrites it; null disables snapshots.</param>
    /// <param name="baselines">Where each backup or restore notes what this PC and the cloud now share; null disables conflict detection.</param>
    public BackupService(Func<string, string, WebDavClient>? webDavFactory = null, string? snapshotRoot = null,
        SyncBaselineStore? baselines = null)
    {
        _webDavFactory = webDavFactory
            ?? ((url, password) => new WebDavClient(url, "", password));
        _snapshotRoot = snapshotRoot;
        _baselines = baselines;
    }

    /// <summary>What this PC last had in common with the cloud, per game; null when that isn't tracked.</summary>
    public SyncBaselineStore? Baselines => _baselines;

    /// <summary>
    /// True when the local save and the cloud's newest backup both changed since this PC last synced the game,
    /// so neither may simply replace the other. False when that can't be told (the game was never synced here).
    /// </summary>
    public bool ChangedOnBothSides(GameConfig game, long localMTime, long cloudMTime)
    {
        var baseline = _baselines?.Get(game.Name) ?? 0;
        return baseline > 0
               && localMTime - baseline > Constants.SyncThresholdSeconds
               && cloudMTime - baseline > Constants.SyncThresholdSeconds;
    }

    /// <summary>Notes that the local save and the cloud hold the same save, whose newest file is from <paramref name="mtime"/>.</summary>
    public void RememberSynced(GameConfig game, long mtime) => _baselines?.Set(game.Name, mtime);

    // ---------------- Backup ----------------

    /// <summary>The backup targets a game is set up for and that are configured, in the order they run.</summary>
    public static IReadOnlyList<string> TargetsFor(GameConfig game, AppConfig config)
    {
        var targets = new List<string>();
        if (game.HasMethod(Constants.MethodOneDrive) && !string.IsNullOrEmpty(config.CloudRoot))
            targets.Add(Constants.TargetOneDrive);
        if (game.HasMethod(Constants.MethodWebDav) && !string.IsNullOrEmpty(config.WebDavUrl))
            targets.Add(Constants.TargetWebDav);
        return targets;
    }

    /// <returns>The targets that now hold the save. Use <see cref="RunBackupAsync"/> to learn why the others failed.</returns>
    public async Task<List<string>> PerformBackupAsync(
        GameConfig game,
        AppConfig config,
        string webDavPassword,
        Action<string> log,
        CancellationToken ct = default) =>
        (await RunBackupAsync(game, config, webDavPassword, log, ct: ct)).Succeeded;

    /// <param name="onlyTargets">Restricts the run to these targets (see <see cref="TargetsFor"/>); null means all.</param>
    public async Task<BackupResult> RunBackupAsync(
        GameConfig game,
        AppConfig config,
        string webDavPassword,
        Action<string> log,
        IReadOnlyCollection<string>? onlyTargets = null,
        CancellationToken ct = default)
    {
        var result = new BackupResult();
        var targets = TargetsFor(game, config).Where(t => onlyTargets is null || onlyTargets.Contains(t)).ToList();
        if (targets.Count == 0)
            return result;
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var toOneDrive = targets.Contains(Constants.TargetOneDrive);
        var toWebDav = targets.Contains(Constants.TargetWebDav);

        void Fail(string target, string message)
        {
            result.Failures.Add(new BackupFailure(target, message));
            log(Localization.T("Log.BackupFailed", game.Name, target, message));
        }
        // A problem with the save itself stops every target at once.
        void FailAll(string message) => Fail(string.Join(", ", targets), message);

        var pathError = SavePathPolicy.Validate(game.Save, game.Name, toOneDrive ? config.CloudRoot : "");
        if (pathError != SavePathError.None)
        {
            FailAll(SavePathPolicy.Describe(pathError));
            return result;
        }
        if (!Directory.Exists(game.Save))
        {
            FailAll(Localization.T("Err.SaveDirMissing", game.Save));
            return result;
        }
        // Creating a missing root would "back up" into a plain local folder that OneDrive never syncs.
        if (toOneDrive && !Directory.Exists(config.CloudRoot))
        {
            Fail(Constants.TargetOneDrive, Localization.T("Err.OneDriveRootMissing", config.CloudRoot));
            toOneDrive = false;
        }
        if (!toOneDrive && !toWebDav)
            return result;

        // An upload can take minutes, so WebDAV reads from a private copy: the game's own files
        // are only read for the length of one local copy and never held open during the upload.
        string? snapshot = null;
        // The newest file time in what was published, which this PC and the cloud now have in common.
        long publishedMTime = 0;
        try
        {
            if (toWebDav)
            {
                snapshot = Path.Combine(Path.GetTempPath(), $"gcs_snapshot_{Guid.NewGuid():N}");
                try
                {
                    if (!await FileUtil.CopyDirectoryConsistentAsync(game.Save, snapshot, ct))
                        log(Localization.T("Log.BackupUnstable", game.Name));
                    publishedMTime = FileUtil.GetLatestMTime(snapshot);
                }
                catch (Exception ex) when (!IsCancelled(ex, ct))
                {
                    // OneDrive reads from the same copy, so it can't run either.
                    var attempted = new List<string>();
                    if (toOneDrive)
                        attempted.Add(Constants.TargetOneDrive);
                    attempted.Add(Constants.TargetWebDav);
                    Fail(string.Join(", ", attempted), ex.Message);
                    return result;
                }
            }

            if (toOneDrive)
            {
                try
                {
                    var published = await PublishToOneDriveAsync(game, config.CloudRoot, snapshot, timestamp, log, ct);
                    if (publishedMTime == 0)
                        publishedMTime = FileUtil.GetLatestMTime(published);
                    result.Succeeded.Add(Constants.TargetOneDrive);
                }
                catch (Exception ex) when (!IsCancelled(ex, ct))
                {
                    Fail(Constants.TargetOneDrive, ex.Message);
                }
                if (result.Succeeded.Contains(Constants.TargetOneDrive))
                {
                    try
                    {
                        await CleanupOneDriveBackupsAsync(game.Name, config.CloudRoot, game.MaxBackups, ct);
                    }
                    catch (Exception ex) when (!IsCancelled(ex, ct))
                    {
                        log(Localization.T("Log.CleanupFailed", game.Name, Constants.TargetOneDrive, ex.Message));
                    }
                }
            }

            if (toWebDav)
            {
                WebDavClient? wc = null;
                try
                {
                    try
                    {
                        // Building the client can fail too (a malformed address), so it belongs inside the try.
                        wc = _webDavFactory(config.WebDavUrl, webDavPassword);
                        await PublishToWebDavAsync(wc, game.Name, config.WebDavUrl, snapshot!, timestamp, log, ct);
                        result.Succeeded.Add(Constants.TargetWebDav);
                    }
                    catch (Exception ex) when (!IsCancelled(ex, ct))
                    {
                        Fail(Constants.TargetWebDav, WebDavClient.DescribeException(ex));
                    }
                    if (wc is not null && result.Succeeded.Contains(Constants.TargetWebDav))
                    {
                        try
                        {
                            await CleanupWebDavBackupsAsync(wc, game.Name, game.MaxBackups, ct);
                        }
                        catch (Exception ex) when (!IsCancelled(ex, ct))
                        {
                            log(Localization.T("Log.CleanupFailed", game.Name, Constants.TargetWebDav, ex.Message));
                        }
                    }
                }
                finally
                {
                    wc?.Dispose();
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

        if (result.Succeeded.Count > 0)
        {
            RememberSynced(game, publishedMTime);
            log(Localization.T("Log.BackupSucceeded", game.Name, string.Join(", ", result.Succeeded)));
        }
        return result;
    }

    // Shutting down is not a failure; running out of the per-request time limit is.
    private static bool IsCancelled(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    /// <returns>The folder holding the published backup.</returns>
    private static async Task<string> PublishToOneDriveAsync(GameConfig game, string cloudRoot, string? snapshot,
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
            var published = Path.Combine(gameCloudDir, finalName);
            Directory.Move(incomplete, published);
            return published;
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

    private static Task UploadOrThrowAsync(WebDavClient wc, string source, string remoteDir, CancellationToken ct) =>
        wc.UploadDirAsync(source, remoteDir, ct);

    // ---------------- Restore ----------------

    public async Task RestoreFromOneDriveAsync(
        GameConfig game,
        string backupName,
        AppConfig config,
        Action<string> log,
        CancellationToken ct = default)
    {
        var backupPath = Path.Combine(config.CloudRoot, game.Name, backupName);
        await RestoreWithSnapshotAsync(game, config.CloudRoot, log, () => FileUtil.RestoreDirectoryAsync(game.Save,
            staging => FileUtil.CopyDirectoryAsync(backupPath, staging, ct), ct), ct);
        RememberRestored(game);
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
        await RestoreWithSnapshotAsync(game, config.CloudRoot, log, () => FileUtil.RestoreDirectoryAsync(game.Save, async staging =>
        {
            count = await wc.DownloadDirAsync(remoteDir, staging, ct);
            if (count == 0)
                throw new InvalidOperationException(Localization.T("Log.RestoreWdEmpty", remoteDir));
            FileUtil.ApplyBackupMTime(backupName, staging);
        }, ct), ct);
        RememberRestored(game);
        log(Localization.T("Log.RestoreWdDone", count));
        log(Localization.T("Log.RestoreWdDoneFull", game.Name, backupName));
    }

    // The save now is that backup (copies keep file times), so both sides are in sync again.
    private void RememberRestored(GameConfig game) => RememberSynced(game, FileUtil.GetLatestMTime(game.Save));

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
        await RestoreWithSnapshotAsync(game, "", log, () => FileUtil.RestoreDirectoryAsync(game.Save,
            staging => FileUtil.CopyDirectoryAsync(snapshotPath, staging, ct), ct), ct);
        log(Localization.T("Log.SnapshotRestored", game.Name, Path.GetFileName(snapshotPath)));
    }

    private async Task RestoreWithSnapshotAsync(GameConfig game, string cloudRoot, Action<string> log,
        Func<Task> restore, CancellationToken ct)
    {
        // A restore replaces the whole folder, so refuse one that is far bigger than a game's save.
        var pathError = SavePathPolicy.Validate(game.Save, game.Name, cloudRoot);
        if (pathError != SavePathError.None)
            throw new InvalidOperationException(SavePathPolicy.Describe(pathError));
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
            // Cancelled by the user: nothing failed, and the caller has to be able to tell.
            if (IsCancelled(ex, ct))
                throw;
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
        try
        {
            return await ListWebDavBackupsOrThrowAsync(game, config, webDavPassword, ct);
        }
        catch
        {
            // return what we have
            return new List<BackupEntry>();
        }
    }

    /// <summary>
    /// Like <see cref="ListWebDavBackupsAsync"/>, but a server that can't be asked is an error instead of
    /// "no backups". A game folder that doesn't exist yet (HTTP 404) simply has no backups.
    /// </summary>
    public async Task<List<BackupEntry>> ListWebDavBackupsOrThrowAsync(
        GameConfig game,
        AppConfig config,
        string webDavPassword,
        CancellationToken ct = default)
    {
        var result = new List<BackupEntry>();
        if (string.IsNullOrEmpty(config.WebDavUrl))
            return result;
        using var wc = _webDavFactory(config.WebDavUrl, webDavPassword);
        List<(string Href, string Name)> items;
        try
        {
            items = await wc.PropFindAsync(game.Name, ct: ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return result;
        }
        foreach (var (href, name) in items)
        {
            if (!name.StartsWith("save_", StringComparison.Ordinal))
                continue;
            var (ts, _) = BackupName.Parse(name);
            var dateText = BackupName.ToDateTime(ts)?.ToString("yyyy-MM-dd HH:mm:ss") ?? Localization.T("Common.UnknownTime");
            result.Add(new BackupEntry(name, href, dateText, 0));
        }
        result.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
        return result;
    }

    /// <summary>Asks the WebDAV server whether it can be reached with the configured address and login.</summary>
    public async Task<WebDavProbe> ProbeWebDavAsync(AppConfig config, string webDavPassword,
        CancellationToken ct = default)
    {
        try
        {
            using var wc = _webDavFactory(config.WebDavUrl, webDavPassword);
            return await wc.ProbeAsync(ct);
        }
        catch (Exception ex) when (!IsCancelled(ex, ct))
        {
            return new WebDavProbe(false, 0, WebDavClient.DescribeException(ex));
        }
    }

    // ---------------- Sync status ----------------

    /// <param name="webDavProbe">
    /// The outcome of <see cref="ProbeWebDavAsync"/> when the caller already has it, so checking many games
    /// asks the server once; without it a WebDAV check probes by itself.
    /// </param>
    public async Task<SyncStatus> GetSyncStatusAsync(
        GameConfig game,
        AppConfig config,
        string webDavPassword,
        bool checkWebDav,
        CancellationToken ct = default,
        WebDavProbe? webDavProbe = null)
    {
        if (game.Paused)
            return new SyncStatus(Localization.T("Status.Paused"), SyncStatusKind.Paused);
        if (game.BackupMethods.Count == 0)
            return new SyncStatus(Localization.T("Status.NoMethods"), SyncStatusKind.NotConfigured);

        var hasOneDrive = game.HasMethod(Constants.MethodOneDrive) && !string.IsNullOrEmpty(config.CloudRoot);
        var hasWebDav = game.HasMethod(Constants.MethodWebDav) && !string.IsNullOrEmpty(config.WebDavUrl);
        if (!hasOneDrive && !hasWebDav)
            return new SyncStatus(Localization.T("Status.NoTarget"), SyncStatusKind.NotConfigured);

        // Whatever makes every backup fail comes first: nothing else about the game means much until it is fixed.
        var pathError = SavePathPolicy.Validate(game.Save, game.Name, hasOneDrive ? config.CloudRoot : "");
        if (pathError != SavePathError.None)
            return Problem(Localization.T("Status.PathInvalid"), SavePathPolicy.Describe(pathError));
        if (hasOneDrive && !Directory.Exists(config.CloudRoot))
            return Problem(Localization.T("Status.OneDriveMissing"),
                Localization.T("Err.OneDriveRootMissing", config.CloudRoot));
        if (hasWebDav)
        {
            var urlError = WebDavUrl.Normalize(config.WebDavUrl, out _);
            if (urlError != WebDavUrlError.None)
                return Problem(Localization.T("Status.WebDavUrlInvalid"), WebDavUrl.Describe(urlError, config.WebDavUrl));
            if (checkWebDav)
            {
                var probe = webDavProbe ?? await ProbeWebDavAsync(config, webDavPassword, ct);
                if (!probe.Ok)
                    return Problem(WebDavProblemText(probe.Status), probe.Message);
            }
        }

        var localTime = FileUtil.GetLatestMTime(game.Save);
        DateTime? lastBackup = null;
        long odCloudTime = 0;
        if (hasOneDrive)
        {
            var odNames = await ListOneDriveBackupsAsync(game, config, ct);
            if (odNames.Count > 0)
            {
                var latestDir = Path.Combine(config.CloudRoot, game.Name, odNames[0]);
                odCloudTime = FileUtil.GetLatestMTime(latestDir);
                lastBackup = Newer(lastBackup, BackupTimeOf(odNames[0]));
            }
        }

        long wdBackupMTime = 0;
        long wdDirTs = 0;
        if (hasWebDav && checkWebDav)
        {
            List<BackupEntry> wdBackups;
            try
            {
                wdBackups = await ListWebDavBackupsOrThrowAsync(game, config, webDavPassword, ct);
            }
            catch (Exception ex) when (!IsCancelled(ex, ct))
            {
                return Problem(WebDavProblemText(WebDavStatusOf(ex)), WebDavClient.DescribeException(ex));
            }
            if (wdBackups.Count > 0)
            {
                lastBackup = Newer(lastBackup, BackupTimeOf(wdBackups[0].Name));
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
            return new SyncStatus(Localization.T("Status.WebDavUnchecked"), SyncStatusKind.Unknown, lastBackup);

        long cloudTime;
        if (odCloudTime > 0)
            cloudTime = odCloudTime;
        else if (wdBackupMTime > 0)
            cloudTime = wdBackupMTime;
        else
            cloudTime = wdDirTs;

        if (localTime == 0 && cloudTime == 0)
            return new SyncStatus(Localization.T("Status.NoSaves"), SyncStatusKind.NoSaves, lastBackup);
        if (cloudTime == 0)
            return new SyncStatus(Localization.T("Status.WaitingFirst"), SyncStatusKind.LocalNew, lastBackup);
        if (localTime == 0)
            return new SyncStatus(Localization.T("Status.LocalEmpty"), SyncStatusKind.CloudNew, lastBackup);
        // Only a real save time can tell that the cloud changed; the time a backup was uploaded can't.
        var cloudSaveTime = odCloudTime > 0 ? odCloudTime : wdBackupMTime;
        if (cloudSaveTime > 0 && ChangedOnBothSides(game, localTime, cloudSaveTime))
            return new SyncStatus(Localization.T("Status.Conflict"), SyncStatusKind.Conflict, lastBackup,
                Localization.T("Status.ConflictDetail", RelativeTime.FormatUnix(_baselines!.Get(game.Name))));
        if (localTime - cloudTime > Constants.SyncThresholdSeconds)
            return new SyncStatus(Localization.T("Status.LocalNew"), SyncStatusKind.LocalNew, lastBackup);
        if ((odCloudTime > 0 || wdBackupMTime > 0) &&
            cloudTime - localTime > Constants.SyncThresholdSeconds)
            return new SyncStatus(Localization.T("Status.CloudNew"), SyncStatusKind.CloudNew, lastBackup);
        return new SyncStatus(Localization.T("Status.Synced"), SyncStatusKind.Synced, lastBackup);
    }

    private static SyncStatus Problem(string text, string detail) =>
        new(text, SyncStatusKind.Error, Detail: detail);

    /// <summary>Short label for a WebDAV problem; 0 means the server couldn't be reached at all.</summary>
    private static string WebDavProblemText(int status) => status switch
    {
        401 or 403 => Localization.T("Status.WebDavAuth"),
        404 => Localization.T("Status.WebDavNotFound"),
        0 => Localization.T("Status.WebDavUnreachable"),
        _ => Localization.T("Status.WebDavError", status)
    };

    private static int WebDavStatusOf(Exception ex) => ex switch
    {
        WebDavException webDav => webDav.StatusCode,
        HttpRequestException { StatusCode: { } code } => (int)code,
        _ => 0
    };

    private static DateTime? BackupTimeOf(string backupName) =>
        BackupName.ToDateTime(BackupName.Parse(backupName).Timestamp);

    private static DateTime? Newer(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

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

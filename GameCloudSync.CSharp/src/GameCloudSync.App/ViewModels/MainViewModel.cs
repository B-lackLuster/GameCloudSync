using System.Collections.ObjectModel;
using System.Collections;
using System.IO;
using GameCloudSync.Core;
using System.Windows.Media;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.ViewModels;

public enum ConflictChoice { Skip, KeepLocal, UseCloud }

/// <summary>A save that changed on this PC and in the cloud since the two were last in sync here.</summary>
public sealed record SyncConflict(GameConfig Game, long LocalMTime, long CloudMTime, long BaselineMTime, BackupSource Source);

public sealed class MainViewModel : ObservableObject
{
    private readonly ConfigStore _configStore;
    private readonly SecretStore _secretStore;
    private readonly BackupService _backupService;
    private readonly CloudConfigService _cloudConfigService;
    private readonly MonitoringService _monitoring;
    private readonly SynchronizationContext _ui;
    private readonly FileLog? _fileLog;

    private AppConfig _config;
    private string _webDavPassword = "";
    private List<GameItemViewModel> _selectedGames = new();
    private bool _isBusy;
    private int _busyDepth;
    private string _busyText = "";
    private CancellationTokenSource? _busyCts;
    private int _refreshGeneration;
    private bool _hasPendingCloudUpdate;
    private List<GameConfig> _pendingCloudGames = new();
    private string _pendingCloudSource = "";
    private bool _isMonitoring;
    private int _monitoredCount;
    // Startup sync and a new game's first backup, which run without the busy bar.
    private int _backgroundJobs;

    public MainViewModel(
        ConfigStore configStore,
        SecretStore secretStore,
        BackupService backupService,
        CloudConfigService cloudConfigService,
        MonitoringService monitoring,
        UpdateService updates,
        SynchronizationContext ui,
        FileLog? fileLog = null)
    {
        _configStore = configStore;
        _secretStore = secretStore;
        _backupService = backupService;
        _cloudConfigService = cloudConfigService;
        _monitoring = monitoring;
        _ui = ui;
        _fileLog = fileLog;
        _config = configStore.Load();
        Dashboard = new DashboardViewModel(this);
        Update = new UpdateViewModel(this, updates, Environment.ProcessPath);
        Games.CollectionChanged += (_, _) => OnPropertyChanged(nameof(GameCountText));
        _webDavPassword = _secretStore.LoadPassword() ?? "";

        _monitoring.StateChanged += () => RunOnUi(() =>
        {
            IsMonitoring = _monitoring.IsMonitoring;
            _monitoredCount = _monitoring.MonitoredCount;
            OnPropertyChanged(nameof(MonitorStateText));
        });
        _monitoring.BackupFinished += () => RunOnUi(() => _ = RefreshGamesAsync());
        _monitoring.BackupAttempted += attempt => RunOnUi(() => OnBackupAttempted(attempt));
        Localization.Instance.LanguageChanged += () => RunOnUi(() =>
        {
            OnPropertyChanged(nameof(CloudStatusText));
            OnPropertyChanged(nameof(GameCountText));
            OnPropertyChanged(nameof(MonitorButtonText));
            OnPropertyChanged(nameof(MonitorStateText));
            OnPropertyChanged(nameof(UpdateConfigButtonText));
            _ = RefreshGamesAsync();
        });
    }

    public AppConfig Config => _config;
    public DashboardViewModel Dashboard { get; }
    public UpdateViewModel Update { get; }
    public string WebDavPassword => _webDavPassword;
    public ConfigStore ConfigStore => _configStore;
    public SecretStore SecretStore => _secretStore;
    public BackupService BackupService => _backupService;
    public CloudConfigService CloudConfigService => _cloudConfigService;
    public MonitoringService Monitoring => _monitoring;

    /// <summary>The folder holding the log files, for the "open log folder" button.</summary>
    public string LogFolder => _fileLog?.Directory ?? Path.Combine(_configStore.AppDir, Constants.LogDirName);

    public ObservableCollection<GameItemViewModel> Games { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanAct));
                OnPropertyChanged(nameof(CanUseSelection));
            }
        }
    }

    public bool CanAct => !IsBusy;

    /// <summary>
    /// A backup or restore is running or about to start, so quitting now would cut it off. An update waits for this
    /// before it restarts the app.
    /// </summary>
    public bool HasBackgroundWork => IsBusy || Volatile.Read(ref _backgroundJobs) > 0 || _monitoring.HasPendingWork;

    /// <summary>What the running job is doing right now, shown beside its progress bar.</summary>
    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public bool CanCancelBusy => _busyCts is { IsCancellationRequested: false };

    /// <summary>
    /// Stops the running job at the next safe point: a backup leaves no half-written copy behind, and a
    /// restore that hasn't replaced the save yet leaves it untouched.
    /// </summary>
    public void CancelBusy()
    {
        if (_busyCts is not { IsCancellationRequested: false } cts)
            return;
        cts.Cancel();
        BusyText = Localization.T("Busy.Cancelling");
        OnPropertyChanged(nameof(CanCancelBusy));
    }

    /// <summary>Runs a job the user started; the window shows <paramref name="text"/> and a cancel button until it ends.</summary>
    public async Task RunBusyAsync(string text, Func<CancellationToken, Task> work)
    {
        var ct = BeginBusy(text);
        try
        {
            await work(ct);
        }
        finally
        {
            EndBusy();
        }
    }

    // Jobs may overlap (the history window works beside the main window); the bar stays until the last one ends.
    private CancellationToken BeginBusy(string text)
    {
        if (_busyDepth++ == 0)
        {
            _busyCts = new CancellationTokenSource();
            IsBusy = true;
        }
        BusyText = text;
        OnPropertyChanged(nameof(CanCancelBusy));
        return _busyCts!.Token;
    }

    private void SetBusyProgress(string text, CancellationToken ct)
    {
        // "Cancelling…" stays up once asked for.
        if (!ct.IsCancellationRequested)
            BusyText = text;
    }

    private void EndBusy()
    {
        if (--_busyDepth > 0)
            return;
        IsBusy = false;
        BusyText = "";
        _busyCts?.Dispose();
        _busyCts = null;
        OnPropertyChanged(nameof(CanCancelBusy));
    }

    public string GameCountText => Localization.T("Main.GameCount", Games.Count);

    public bool HasPendingCloudUpdate
    {
        get => _hasPendingCloudUpdate;
        private set
        {
            if (SetProperty(ref _hasPendingCloudUpdate, value))
                OnPropertyChanged(nameof(UpdateConfigButtonText));
        }
    }

    public string UpdateConfigButtonText => HasPendingCloudUpdate
        ? Localization.T("Main.UpdateConfigN", _pendingCloudGames.Count)
        : Localization.T("Main.UpdateConfig");

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
            {
                OnPropertyChanged(nameof(MonitorButtonText));
                OnPropertyChanged(nameof(MonitorStateBrush));
                OnPropertyChanged(nameof(MonitorStateText));
            }
        }
    }

    public string MonitorButtonText => IsMonitoring
        ? Localization.T("Btn.StopMonitor")
        : Localization.T("Btn.StartMonitor");

    public string MonitorStateText => IsMonitoring
        ? Localization.T("Main.MonitorStateOn", _monitoredCount)
        : Localization.T("Main.MonitorStateOff");

    public string CloudStatusText
    {
        get
        {
            var parts = new List<string>();
            parts.Add(string.IsNullOrEmpty(_config.CloudRoot)
                ? Localization.T("Cloud.OdUnset")
                : Localization.T("Cloud.OdSet", _config.CloudRoot));
            parts.Add(string.IsNullOrEmpty(_config.WebDavUrl)
                ? Localization.T("Cloud.WdUnset")
                : Localization.T("Cloud.WdSet", _config.WebDavUrl));
            return string.Join("  |  ", parts);
        }
    }

    public Brush CloudStatusBrush =>
        Brush(string.IsNullOrEmpty(_config.CloudRoot) && string.IsNullOrEmpty(_config.WebDavUrl)
            ? "#EF4444" : "#10B981");

    public Brush MonitorStateBrush => IsMonitoring ? Brush("#10B981") : Brush("#94A3B8");

    public IReadOnlyList<GameItemViewModel> SelectedGames => _selectedGames;

    public bool HasSelection => _selectedGames.Count > 0;

    /// <summary>Upload and download need a selected game and no other job running.</summary>
    public bool CanUseSelection => CanAct && HasSelection;

    public void SetSelection(IList selected)
    {
        _selectedGames = selected.OfType<GameItemViewModel>().ToList();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanUseSelection));
    }

    public void SetWebDavPassword(string password)
    {
        _webDavPassword = password;
    }

    // ---------------- Startup ----------------

    public void Load()
    {
        foreach (var game in _config.Games)
            Games.Add(new GameItemViewModel(game));
        AppendLog(Localization.T("Log.Welcome", Games.Count));
        // The local side shows at once; the WebDAV side follows when the server has answered.
        _ = RefreshGamesAsync(checkWebDav: false);
        if (_config.WebDavUrl.Length > 0)
            _ = Task.Run(CheckWebDavAsync);
    }

    public void AutoStart()
    {
        // Also with no games yet: monitoring then starts watching the first game as soon as it is added.
        if (!IsMonitoring)
            _monitoring.Start();
        if (_config.Games.Count == 0)
        {
            AppendLog(Localization.T("Log.EmptyList"));
            return;
        }
        if (_config.AutoSyncOnStartup)
        {
            AppendLog(Localization.T("Log.CheckingSync"));
            RunInBackground(AutoSyncAllGamesAsync);
        }
        if (!string.IsNullOrEmpty(_config.CloudRoot) || !string.IsNullOrEmpty(_config.WebDavUrl))
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                await CheckCloudConfigAsync();
            });
    }

    public void CheckWakeupFile()
    {
        var wakeup = Path.Combine(_configStore.AppDir, Constants.WakeupFileName);
        if (!File.Exists(wakeup))
            return;
        try
        {
            File.Delete(wakeup);
            ShowWindowRequested?.Invoke();
        }
        catch
        {
            // ignore
        }
    }

    public event Action? ShowWindowRequested;

    /// <summary>Asks the server once at startup, says in the log what is wrong if it can't be used, then shows each game's real status.</summary>
    private async Task CheckWebDavAsync()
    {
        var probe = await _backupService.ProbeWebDavAsync(_config, _webDavPassword);
        AppendLog(probe.Ok
            ? Localization.T("Log.WebDavOk", _config.WebDavUrl)
            : Localization.T("Log.WebDavProbeFail", _config.WebDavUrl, probe.Message));
        RunOnUi(() => _ = RefreshGamesAsync(checkWebDav: true, probe));
    }

    private WebDavClient CreateWebDavClient()
    {
        return new WebDavClient(_config.WebDavUrl, _config.WebDavUsername, _webDavPassword,
            log: AppendLog,
            debug: _config.DebugWebDavLog ? AppendLog : null);
    }

    // ---------------- List & status ----------------

    private bool HasWebDavGames =>
        !string.IsNullOrEmpty(_config.WebDavUrl) && Games.Any(g => g.IsWebDav && !g.IsPaused);

    /// <param name="checkWebDav">
    /// Whether to ask the WebDAV server too. By default it is asked whenever a game uses it, so a game
    /// backed up only to WebDAV never falls back to "not checked" after an ordinary refresh.
    /// </param>
    /// <param name="probe">The answer of an earlier <see cref="BackupService.ProbeWebDavAsync"/>, so the server isn't asked again.</param>
    public async Task RefreshGamesAsync(bool? checkWebDav = null, WebDavProbe? probe = null)
    {
        // A later refresh can overtake this one while the server is slow; then its newer answer must stand.
        var generation = ++_refreshGeneration;
        var withWebDav = checkWebDav ?? HasWebDavGames;
        // One question to the server covers every game; asking per game would repeat the same failure N times.
        if (withWebDav && probe is null && HasWebDavGames)
            probe = await _backupService.ProbeWebDavAsync(_config, _webDavPassword);
        foreach (var item in Games.ToList())
        {
            var status = await _backupService.GetSyncStatusAsync(
                item.Game, _config, _webDavPassword, withWebDav, webDavProbe: probe);
            if (generation != _refreshGeneration)
                return;
            item.Status = status;
        }
    }

    /// <summary>Brings the "5 min ago" texts on the game cards up to date.</summary>
    public void RefreshTimes()
    {
        foreach (var item in Games)
            item.RefreshDetail();
    }

    // ---------------- Backup / restore ----------------

    /// <returns>
    /// One line per game that could not be backed up, with the reason (empty when all went well), and
    /// whether the user cancelled before every game was done.
    /// </returns>
    public async Task<(List<string> Failures, bool Cancelled)> ForceBackupAsync(IReadOnlyList<GameItemViewModel> items)
    {
        var failures = new List<string>();
        var cancelled = false;
        var selected = items.Where(g => g.Game.BackupMethods.Count > 0).ToList();
        if (selected.Count == 0)
            return (failures, cancelled);
        var ct = BeginBusy(Localization.T("Main.Busy"));
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                var item = selected[i];
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                SetBusyProgress(Localization.T("Busy.Uploading", item.Name, i + 1, selected.Count), ct);
                try
                {
                    // Copying and fingerprinting a big save takes a while; the window keeps drawing meanwhile.
                    var result = await Task.Run(() =>
                        _backupService.RunBackupAsync(item.Game, _config, _webDavPassword, AppendLog, ct: ct), ct);
                    item.SetLastFailure(result.HasFailures ? result.FailureSummary : null);
                    if (result.HasFailures)
                        failures.Add($"{item.Name}: {result.FailureSummary}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    AppendLog(Localization.T("Log.UploadCancelled", item.Name));
                    break;
                }
            }
        }
        finally
        {
            EndBusy();
            await RefreshGamesAsync();
        }
        return (failures, cancelled);
    }

    /// <summary>Backs up a game that was just added, without holding anything up; failures are shown like any other.</summary>
    public async Task BackupNewGameAsync(GameConfig game)
    {
        Interlocked.Increment(ref _backgroundJobs);
        try
        {
            var result = await Task.Run(() => _backupService.RunBackupAsync(game, _config, _webDavPassword, AppendLog));
            RunOnUi(() =>
            {
                var item = Games.FirstOrDefault(g => ReferenceEquals(g.Game, game));
                item?.SetLastFailure(result.HasFailures ? result.FailureSummary : null);
                if (result.HasFailures)
                    RaiseFailureNotice(game.Name, result.FailureSummary);
                _ = RefreshGamesAsync();
            });
        }
        finally
        {
            Interlocked.Decrement(ref _backgroundJobs);
        }
    }

    /// <summary>Raised with a title and text when a game's backups start failing and the user should hear about it.</summary>
    public event Action<string, string>? FailureNotice;

    private void OnBackupAttempted(BackupAttempt attempt)
    {
        // The monitor works on its own copy of the config, so games are matched by name.
        var item = Games.FirstOrDefault(g => string.Equals(g.Name, attempt.Game.Name, StringComparison.OrdinalIgnoreCase));
        item?.SetLastFailure(attempt.Result.HasFailures ? attempt.Result.FailureSummary : null);
        if (attempt.Result.HasFailures && attempt.IsNewFailure)
            RaiseFailureNotice(attempt.Game.Name, attempt.Result.FailureSummary);
    }

    private void RaiseFailureNotice(string gameName, string reason) =>
        FailureNotice?.Invoke(Localization.T("Notify.BackupFailedTitle"),
            Localization.T("Notify.BackupFailedBody", gameName, reason));

    public async Task<List<BackupSource>> CollectRestoreSourcesAsync(GameItemViewModel item)
    {
        var sources = new List<BackupSource>();
        if (item.Game.HasMethod(Constants.MethodOneDrive) && !string.IsNullOrEmpty(_config.CloudRoot))
        {
            var names = await _backupService.ListOneDriveBackupsAsync(item.Game, _config);
            if (names.Count > 0)
                sources.Add(new BackupSource("OneDrive", Constants.MethodOneDrive,
                    Path.Combine(_config.CloudRoot, item.Game.Name, names[0])));
        }
        if (item.Game.HasMethod(Constants.MethodWebDav) && !string.IsNullOrEmpty(_config.WebDavUrl))
        {
            var backups = await _backupService.ListWebDavBackupsAsync(item.Game, _config, _webDavPassword);
            if (backups.Count > 0)
                sources.Add(new BackupSource("WebDAV", Constants.MethodWebDav, backups[0].Name));
        }
        return sources;
    }

    /// <summary>The question asked before a backup overwrites the local save: both times, and which side is newer.</summary>
    public string RestoreConfirmMessage(GameItemViewModel item, BackupSource source)
    {
        var localTs = FileUtil.GetLatestMTime(item.Game.Save);
        var cloudTs = BackupService.GetBackupMTime(source);
        var localStr = localTs > 0 ? RelativeTime.FormatUnix(localTs) : Localization.T("Msg.RestoreNoLocal");
        var cloudStr = cloudTs > 0 ? RelativeTime.FormatUnix(cloudTs) : Localization.T("Msg.RestoreUnknown");
        var headline = localTs == 0
            ? Localization.T("Msg.RestoreConfirmLocalEmpty")
            : _backupService.ChangedOnBothSides(item.Game, localTs, cloudTs)
                ? Localization.T("Msg.RestoreConfirmConflict")
                : cloudTs > localTs
                    ? Localization.T("Msg.RestoreConfirmCloudNew")
                    : Localization.T("Msg.RestoreConfirmLocalNew");
        return $"{headline}\n{Localization.T("Msg.RestoreSource", source.Label)}\n" +
               $"{Localization.T("Msg.RestoreCloudTime", cloudStr)}\n" +
               $"{Localization.T("Msg.RestoreLocalTime", localStr)}\n\n" +
               Localization.T("Msg.RestoreAsk");
    }

    private Task RestoreCoreAsync(GameConfig game, BackupSource source, CancellationToken ct) =>
        source.Method == Constants.MethodOneDrive
            ? _backupService.RestoreFromOneDriveAsync(game, Path.GetFileName(source.Path), _config, AppendLog, ct)
            : _backupService.RestoreFromWebDavAsync(game, source.Path, _config, _webDavPassword, AppendLog, ct);

    /// <returns>False when the user cancelled before the save was replaced.</returns>
    public Task<bool> RestoreFromSourceAsync(GameItemViewModel item, BackupSource source) =>
        RunRestoreAsync(item, ct => RestoreCoreAsync(item.Game, source, ct));

    /// <returns>False when the user cancelled before the save was replaced.</returns>
    public Task<bool> RestoreFromHistoryAsync(GameItemViewModel item, string backupName, bool webDav) =>
        RunRestoreAsync(item, ct => webDav
            ? _backupService.RestoreFromWebDavAsync(item.Game, backupName, _config, _webDavPassword, AppendLog, ct)
            : _backupService.RestoreFromOneDriveAsync(item.Game, backupName, _config, AppendLog, ct));

    /// <returns>False when the user cancelled before the save was replaced.</returns>
    public Task<bool> RestoreFromSnapshotAsync(GameItemViewModel item, string snapshotPath) =>
        RunRestoreAsync(item, ct => _backupService.RestoreFromSnapshotAsync(item.Game, snapshotPath, AppendLog, ct));

    private async Task<bool> RunRestoreAsync(GameItemViewModel item, Func<CancellationToken, Task> restore)
    {
        var ct = BeginBusy(Localization.T("Busy.Restoring", item.Name));
        try
        {
            await WithMonitoringPausedAsync(() => Task.Run(() => restore(ct), ct));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AppendLog(Localization.T("Log.RestoreCancelled", item.Name));
            return false;
        }
        finally
        {
            EndBusy();
            await RefreshGamesAsync();
        }
    }

    /// <summary>Restores several games one after another; one failure doesn't stop the rest, a cancel does.</summary>
    public async Task<(int Restored, List<string> Failed, bool Cancelled)> RestoreManyAsync(
        IReadOnlyList<(GameItemViewModel Item, BackupSource Source)> plan)
    {
        var restored = 0;
        var failed = new List<string>();
        var cancelled = false;
        var ct = BeginBusy(Localization.T("Main.Busy"));
        try
        {
            await WithMonitoringPausedAsync(async () =>
            {
                for (var i = 0; i < plan.Count; i++)
                {
                    var (item, source) = plan[i];
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        return;
                    }
                    SetBusyProgress(Localization.T("Busy.RestoringN", item.Name, i + 1, plan.Count), ct);
                    try
                    {
                        await Task.Run(() => RestoreCoreAsync(item.Game, source, ct), ct);
                        restored++;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        AppendLog(Localization.T("Log.RestoreCancelled", item.Name));
                        return;
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{item.Name}: {ex.Message}");
                        AppendLog(Localization.T("Msg.RestoreFailed", $"{item.Name}: {ex.Message}"));
                    }
                }
            });
        }
        finally
        {
            EndBusy();
            await RefreshGamesAsync();
        }
        return (restored, failed, cancelled);
    }

    /// <summary>
    /// Restores replace the save folder, so no watcher may stay attached to the old one;
    /// watchers are recreated on the new folder afterwards (pending changes carry over).
    /// </summary>
    private async Task WithMonitoringPausedAsync(Func<Task> restore)
    {
        // Also when it has nothing to watch: a game added meanwhile would start it on the folder being replaced.
        var wasActive = _monitoring.IsActive;
        if (wasActive)
            _monitoring.Stop();
        try
        {
            await restore();
        }
        finally
        {
            if (wasActive)
                _monitoring.Start();
        }
    }

    public Task<List<BackupEntry>> GetOneDriveHistoryAsync(GameItemViewModel item)
    {
        var names = _backupService.ListOneDriveBackupsAsync(item.Game, _config).Result;
        var result = new List<BackupEntry>();
        foreach (var name in names)
        {
            var (ts, _) = BackupName.Parse(name);
            var dateText = BackupName.ToDateTime(ts)?.ToString("yyyy-MM-dd HH:mm:ss") ?? Localization.T("Common.UnknownTime");
            var dir = Path.Combine(_config.CloudRoot, item.Game.Name, name);
            result.Add(new BackupEntry(name, "", dateText, FileUtil.GetFolderSize(dir)));
        }
        return Task.FromResult(result);
    }

    public async Task<List<BackupEntry>> GetWebDavHistoryAsync(GameItemViewModel item)
    {
        return await _backupService.ListWebDavBackupsAsync(item.Game, _config, _webDavPassword);
    }

    public Task DeleteOneDriveBackupAsync(GameItemViewModel item, string backupName)
    {
        var dir = Path.Combine(_config.CloudRoot, item.Game.Name, backupName);
        FileUtil.DeleteDirectorySafe(dir);
        AppendLog(Localization.T("Msg.DeleteBackupLog", item.Name, backupName));
        return Task.CompletedTask;
    }

    public async Task DeleteWebDavBackupAsync(GameItemViewModel item, string backupName)
    {
        using var wc = CreateWebDavClient();
        await wc.DeletePathAsync($"{item.Game.Name}/{backupName}");
        AppendLog(Localization.T("Msg.DeleteBackupLog", item.Name, backupName));
    }

    public Task DeleteAllOneDriveBackupsAsync(GameItemViewModel item)
    {
        var dir = Path.Combine(_config.CloudRoot, item.Game.Name);
        FileUtil.DeleteDirectorySafe(dir);
        AppendLog(Localization.T("Msg.DeleteAllBackupsLog", item.Name));
        return Task.CompletedTask;
    }

    public Task DownloadOneDriveBackupAsync(GameItemViewModel item, string backupName, string destDir)
    {
        var src = Path.Combine(_config.CloudRoot, item.Game.Name, backupName);
        var dest = Path.Combine(destDir, backupName);
        FileUtil.DeleteDirectorySafe(dest);
        return FileUtil.CopyDirectoryAsync(src, dest);
    }

    public async Task DownloadWebDavBackupAsync(GameItemViewModel item, string backupName, string destDir)
    {
        using var wc = CreateWebDavClient();
        var dest = Path.Combine(destDir, backupName);
        FileUtil.DeleteDirectorySafe(dest);
        var count = await wc.DownloadDirAsync($"{item.Game.Name}/{backupName}", dest);
        AppendLog(Localization.T("Msg.DownloadWebDavLog", item.Name, backupName, count));
    }

    // ---------------- Config updates ----------------

    public void ApplyConfigChange(Action<AppConfig> change)
    {
        change(_config);
        _configStore.Save(_config);
        OnPropertyChanged(nameof(CloudStatusText));
        OnPropertyChanged(nameof(CloudStatusBrush));
    }

    public void RestartMonitoringIfActive() => _monitoring.RestartIfActive();

    /// <summary>Keeps what this PC remembers about a game's last sync in step with an edit of the game.</summary>
    public void OnGameEdited(string oldName, string oldSave, GameConfig game)
    {
        if (_backupService.Baselines is not { } baselines)
            return;
        // Another folder: what it was in sync with says nothing about the new one.
        if (!string.Equals(Path.TrimEndingDirectorySeparator(oldSave), Path.TrimEndingDirectorySeparator(game.Save),
                StringComparison.OrdinalIgnoreCase))
            baselines.Remove(oldName);
        else if (oldName != game.Name)
            baselines.Rename(oldName, game.Name);
    }

    /// <summary>Forgets a game's last sync, for a game that was deleted or replaced.</summary>
    public void ForgetSync(string gameName) => _backupService.Baselines?.Remove(gameName);

    public async Task BackupConfigToCloudAsync()
    {
        await _cloudConfigService.BackupConfigToCloudAsync(_config, _webDavPassword, AppendLog);
    }

    public async Task<List<CloudConfigSummary>> ListConfigSourcesAsync()
    {
        return await _cloudConfigService.ListLatestSourcesAsync(_config, _webDavPassword);
    }

    public async Task CheckCloudConfigAsync()
    {
        var summaries = await _cloudConfigService.ListLatestSourcesAsync(_config, _webDavPassword);
        var cloudConfigs = summaries
            .Where(s => !string.IsNullOrEmpty(s.Filename) && s.Games.Count > 0)
            .ToList();
        if (cloudConfigs.Count == 0)
        {
            AppendLog(Localization.T("Log.CfgDetectNone"));
            HasPendingCloudUpdate = false;
            return;
        }

        var selected = SelectCloudConfig(cloudConfigs);
        if (selected is null)
        {
            AppendLog(Localization.T("Log.CfgDetectUnavailable"));
            HasPendingCloudUpdate = false;
            return;
        }

        var (source, _, games) = selected.Value;
        var newGames = NewCloudGames(games, logInvalid: false);
        AppendLog(Localization.T("Log.CfgDetectSummary", source, games.Count, _config.Games.Count));

        if (newGames.Count == 0)
        {
            foreach (var alt in cloudConfigs.Where(c => c.Source != source))
            {
                var altNew = NewCloudGames(alt.Games, logInvalid: false);
                if (altNew.Count > 0)
                {
                    _pendingCloudGames = altNew;
                    _pendingCloudSource = alt.Source;
                    HasPendingCloudUpdate = true;
                    AppendLog(Localization.T("Log.CfgDetectAlt", source, alt.Source, altNew.Count));
                    AppendLog(Localization.T("Log.CfgDetectUpdateBtn"));
                    return;
                }
            }
            AppendLog(Localization.T("Log.CfgDetectSynced"));
            HasPendingCloudUpdate = false;
            return;
        }

        _pendingCloudGames = newGames;
        _pendingCloudSource = source;
        HasPendingCloudUpdate = true;
        AppendLog(Localization.T("Log.CfgDetectNew", newGames.Count, string.Join(", ", newGames.Select(g => g.Name))));
        AppendLog(Localization.T("Log.CfgDetectUpdateBtn"));
    }

    /// <summary>
    /// Cloud games this device doesn't have yet. Names are compared like the folders they map to
    /// (case-insensitively), and names that can't be a folder name are skipped.
    /// </summary>
    private List<GameConfig> NewCloudGames(IEnumerable<GameConfig> cloudGames, bool logInvalid)
    {
        var known = _config.Games.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<GameConfig>();
        foreach (var game in cloudGames)
        {
            var nameError = GameNames.Validate(game.Name, Array.Empty<GameConfig>());
            if (nameError != GameNameError.None)
            {
                if (logInvalid)
                    AppendLog(Localization.T("Log.GameSkippedInvalid", game.Name, GameNames.Describe(nameError, game.Name)));
                continue;
            }
            if (SavePathProblem(game) is { } pathProblem)
            {
                if (logInvalid)
                    AppendLog(Localization.T("Log.GameSkippedInvalid", game.Name, pathProblem));
                continue;
            }
            if (known.Add(game.Name))
                result.Add(game);
        }
        return result;
    }

    /// <summary>
    /// Why a game coming from another PC's config can't be taken over (judged on the path as it will be
    /// used here), or null when its save path is fine.
    /// </summary>
    public string? SavePathProblem(GameConfig game)
    {
        var toOneDrive = game.BackupMethods?.Contains(Constants.MethodOneDrive) == true;
        var error = SavePathPolicy.Validate(FileUtil.FixSavePath(game.Save ?? ""), game.Name,
            toOneDrive ? _config.CloudRoot : "");
        return error == SavePathError.None ? null : SavePathPolicy.Describe(error);
    }

    public async Task<int> ApplyPendingCloudUpdateAsync()
    {
        if (_pendingCloudGames.Count == 0)
            return 0;
        var imported = 0;
        foreach (var ng in NewCloudGames(_pendingCloudGames, logInvalid: true))
        {
            ng.Save = FileUtil.FixSavePath(ng.Save);
            _config.Games.Add(ng);
            Games.Add(new GameItemViewModel(ng));
            imported++;
        }
        if (imported > 0)
        {
            _configStore.Save(_config);
            _monitoring.RestartIfActive();
            AppendLog(Localization.T("Log.CfgUpdated", _pendingCloudSource, imported));
        }
        _pendingCloudGames = new List<GameConfig>();
        _pendingCloudSource = "";
        HasPendingCloudUpdate = false;
        await RefreshGamesAsync();
        return imported;
    }

    public async Task<int> MergeCloudConfigAsync(string sourceLabel, string filename, IReadOnlyList<GameConfig> cloudGames)
    {
        var imported = 0;
        foreach (var ng in NewCloudGames(cloudGames, logInvalid: true))
        {
            ng.Save = FileUtil.FixSavePath(ng.Save);
            _config.Games.Add(ng);
            Games.Add(new GameItemViewModel(ng));
            imported++;
        }
        if (imported > 0)
        {
            _configStore.Save(_config);
            _monitoring.RestartIfActive();
            AppendLog(Localization.T("Log.CfgMerged", sourceLabel, imported));
        }
        await RefreshGamesAsync();
        return imported;
    }

    private (string Source, string Filename, IReadOnlyList<GameConfig> Games)? SelectCloudConfig(
        List<CloudConfigSummary> cloudConfigs)
    {
        var od = cloudConfigs.FirstOrDefault(c => c.Source == "OneDrive");
        var wd = cloudConfigs.FirstOrDefault(c => c.Source == "WebDAV");
        CloudConfigSummary? chosen = _config.DefaultConfigSource switch
        {
            Constants.MethodOneDrive => (CloudConfigSummary?)od ?? wd,
            Constants.MethodWebDav => (CloudConfigSummary?)wd ?? od,
            _ => NewestOf(od, wd)
        };
        return chosen is null ? null : (chosen.Source, chosen.Filename, chosen.Games);
    }

    private static CloudConfigSummary? NewestOf(CloudConfigSummary? a, CloudConfigSummary? b)
    {
        if (a is null)
            return b;
        if (b is null)
            return a;
        return string.CompareOrdinal(a.Filename, b.Filename) >= 0 ? a : b;
    }

    // ---------------- Auto sync on startup ----------------

    /// <summary>
    /// Asks the user which side of a save conflict to keep; set by the main window. Without it a
    /// conflict is left alone, which loses nothing.
    /// </summary>
    public Func<SyncConflict, Task<ConflictChoice>>? ResolveConflict { get; set; }

    private Task<ConflictChoice> AskConflictAsync(SyncConflict conflict)
    {
        if (ResolveConflict is not { } resolve)
            return Task.FromResult(ConflictChoice.Skip);
        var answer = new TaskCompletionSource<ConflictChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUi(async () =>
        {
            try
            {
                answer.SetResult(await resolve(conflict));
            }
            catch (Exception ex)
            {
                AppendLog(Localization.T("Log.TaskError", ex.Message));
                answer.SetResult(ConflictChoice.Skip);
            }
        });
        return answer.Task;
    }

    private async Task AutoSyncAllGamesAsync()
    {
        foreach (var game in _config.Games.ToList())
        {
            // Without a target that is set up there is nothing to compare with (and "backing up" would do nothing).
            if (game.Paused || BackupService.TargetsFor(game, _config).Count == 0 || !Directory.Exists(game.Save))
                continue;
            try
            {
                await AutoSyncGameAsync(game);
            }
            catch (Exception ex)
            {
                // One unreachable or broken game must not stop the others from syncing.
                AppendLog(Localization.T("Log.AutoSyncGameFailed", game.Name, ex.Message));
            }
        }
        RunOnUi(() => _ = RefreshGamesAsync());
    }

    private async Task AutoSyncGameAsync(GameConfig game)
    {
        var localTime = FileUtil.GetLatestMTime(game.Save);
        if (localTime == 0)
            return;
        var source = await _backupService.FindLatestSourceAsync(game, _config, _webDavPassword);
        long cloudTime = 0;
        if (source is not null)
        {
            cloudTime = source.Method == Constants.MethodOneDrive
                ? FileUtil.GetLatestMTime(source.Path)
                : BackupName.Parse(source.Path).MTime;
        }
        // Both sides moved on since the last sync here, so the newer one isn't necessarily the one to keep.
        if (source is not null && _backupService.ChangedOnBothSides(game, localTime, cloudTime))
        {
            AppendLog(Localization.T("Log.AutoSyncConflict", game.Name));
            var baseline = _backupService.Baselines?.Get(game.Name) ?? 0;
            switch (await AskConflictAsync(new SyncConflict(game, localTime, cloudTime, baseline, source)))
            {
                case ConflictChoice.KeepLocal:
                    AppendLog(Localization.T("Log.ConflictKeepLocal", game.Name));
                    await AutoUploadAsync(game);
                    break;
                case ConflictChoice.UseCloud:
                    AppendLog(Localization.T("Log.ConflictUseCloud", game.Name));
                    await AutoDownloadAsync(game, source);
                    break;
                default:
                    AppendLog(Localization.T("Log.AutoSyncConflictSkipped", game.Name));
                    break;
            }
            return;
        }
        if (localTime - cloudTime > Constants.SyncThresholdSeconds)
        {
            AppendLog(Localization.T("Log.AutoSyncUpload", game.Name));
            await AutoUploadAsync(game);
        }
        else if (cloudTime - localTime > Constants.SyncThresholdSeconds && source is not null)
        {
            AppendLog(Localization.T("Log.AutoSyncDownload", game.Name));
            await AutoDownloadAsync(game, source);
        }
        else
        {
            // Both sides agree: from here on, a change on either side is measured against this.
            _backupService.RememberSynced(game, localTime);
            AppendLog(Localization.T("Log.AutoSyncSynced", game.Name));
        }
    }

    private async Task AutoUploadAsync(GameConfig game)
    {
        var result = await _backupService.RunBackupAsync(game, _config, _webDavPassword, AppendLog);
        RunOnUi(() =>
        {
            var item = Games.FirstOrDefault(g => ReferenceEquals(g.Game, game));
            item?.SetLastFailure(result.HasFailures ? result.FailureSummary : null);
            if (result.HasFailures)
                RaiseFailureNotice(game.Name, result.FailureSummary);
        });
        await Task.Delay(1000);
    }

    private async Task AutoDownloadAsync(GameConfig game, BackupSource source)
    {
        await WithMonitoringPausedAsync(() => RestoreCoreAsync(game, source, CancellationToken.None));
        await Task.Delay(1000);
    }

    /// <summary>Runs work off the UI thread, counted in <see cref="HasBackgroundWork"/> until it ends.</summary>
    private void RunInBackground(Func<Task> work)
    {
        Interlocked.Increment(ref _backgroundJobs);
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            finally
            {
                Interlocked.Decrement(ref _backgroundJobs);
            }
        });
    }

    // ---------------- Logging / UI marshalling ----------------

    /// <summary>Writes the message to the log file and to the activity list.</summary>
    public void AppendLog(string message)
    {
        _fileLog?.Write(message);
        ShowLog(message);
    }

    /// <summary>Writes the message to the log file only, for what the activity list doesn't need to show.</summary>
    public void WriteFileLog(string message) => _fileLog?.Write(message);

    /// <summary>Adds a message to the activity list only, for callers that have already written it to the log file.</summary>
    public void ShowLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        RunOnUi(() =>
        {
            Logs.Add(line);
            while (Logs.Count > 400)
                Logs.RemoveAt(0);
        });
    }

    private void RunOnUi(Action action)
    {
        if (SynchronizationContext.Current == _ui)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    private static Brush Brush(string hex)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}

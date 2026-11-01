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
    private bool _hasPendingCloudUpdate;
    private List<GameConfig> _pendingCloudGames = new();
    private string _pendingCloudSource = "";
    private bool _isMonitoring;
    private int _monitoredCount;

    public MainViewModel(
        ConfigStore configStore,
        SecretStore secretStore,
        BackupService backupService,
        CloudConfigService cloudConfigService,
        MonitoringService monitoring,
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
        Games.CollectionChanged += (_, _) => OnPropertyChanged(nameof(GameCountText));
        _webDavPassword = _secretStore.LoadPassword() ?? "";

        _monitoring.StateChanged += () => RunOnUi(() =>
        {
            IsMonitoring = _monitoring.IsMonitoring;
            _monitoredCount = _monitoring.MonitoredCount;
            OnPropertyChanged(nameof(MonitorStateText));
        });
        _monitoring.BackupFinished += () => RunOnUi(() => _ = RefreshGamesAsync(checkWebDav: HasWebDavGames));
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
                OnPropertyChanged(nameof(CanAct));
        }
    }

    public bool CanAct => !IsBusy;

    public async Task RunBusyAsync(Func<Task> work)
    {
        IsBusy = true;
        try
        {
            await work();
        }
        finally
        {
            IsBusy = false;
        }
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

    public void SetSelection(IList selected)
    {
        _selectedGames = selected.OfType<GameItemViewModel>().ToList();
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
        _ = RefreshGamesAsync();
        if (_config.WebDavUrl.Length > 0)
            _ = Task.Run(CheckWebDavAsync);
    }

    public void AutoStart()
    {
        if (_config.Games.Count == 0)
        {
            AppendLog(Localization.T("Log.EmptyList"));
            return;
        }
        if (!IsMonitoring)
            _monitoring.Start();
        if (_config.AutoSyncOnStartup)
        {
            AppendLog(Localization.T("Log.CheckingSync"));
            _ = Task.Run(AutoSyncAllGamesAsync);
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

    /// <param name="probe">The answer of an earlier <see cref="BackupService.ProbeWebDavAsync"/>, so the server isn't asked again.</param>
    public async Task RefreshGamesAsync(bool checkWebDav = false, WebDavProbe? probe = null)
    {
        // One question to the server covers every game; asking per game would repeat the same failure N times.
        if (checkWebDav && probe is null && HasWebDavGames)
            probe = await _backupService.ProbeWebDavAsync(_config, _webDavPassword);
        foreach (var item in Games.ToList())
        {
            item.Status = await _backupService.GetSyncStatusAsync(
                item.Game, _config, _webDavPassword, checkWebDav, webDavProbe: probe);
        }
    }

    /// <summary>Brings the "5 min ago" texts on the game cards up to date.</summary>
    public void RefreshTimes()
    {
        foreach (var item in Games)
            item.RefreshDetail();
    }

    // ---------------- Backup / restore ----------------

    /// <returns>One line per game that could not be backed up, with the reason; empty when all went well.</returns>
    public async Task<List<string>> ForceBackupAsync()
    {
        var failures = new List<string>();
        var selected = _selectedGames
            .Where(g => g.Game.BackupMethods.Count > 0 && Directory.Exists(g.Game.Save))
            .ToList();
        if (selected.Count == 0)
            return failures;
        IsBusy = true;
        try
        {
            foreach (var item in selected)
            {
                var result = await _backupService.RunBackupAsync(item.Game, _config, _webDavPassword, AppendLog);
                item.SetLastFailure(result.HasFailures ? result.FailureSummary : null);
                if (result.HasFailures)
                    failures.Add($"{item.Name}: {result.FailureSummary}");
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshGamesAsync(checkWebDav: HasWebDavGames);
        }
        return failures;
    }

    /// <summary>Backs up a game that was just added, without holding anything up; failures are shown like any other.</summary>
    public async Task BackupNewGameAsync(GameConfig game)
    {
        var result = await Task.Run(() => _backupService.RunBackupAsync(game, _config, _webDavPassword, AppendLog));
        RunOnUi(() =>
        {
            var item = Games.FirstOrDefault(g => ReferenceEquals(g.Game, game));
            item?.SetLastFailure(result.HasFailures ? result.FailureSummary : null);
            if (result.HasFailures)
                RaiseFailureNotice(game.Name, result.FailureSummary);
            _ = RefreshGamesAsync(checkWebDav: HasWebDavGames);
        });
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

    public async Task RestoreFromSourceAsync(GameItemViewModel item, BackupSource source)
    {
        IsBusy = true;
        try
        {
            await WithMonitoringPausedAsync(() => source.Method == Constants.MethodOneDrive
                ? _backupService.RestoreFromOneDriveAsync(item.Game, Path.GetFileName(source.Path), _config, AppendLog)
                : _backupService.RestoreFromWebDavAsync(item.Game, source.Path, _config, _webDavPassword, AppendLog));
        }
        finally
        {
            IsBusy = false;
            await RefreshGamesAsync();
        }
    }

    public async Task RestoreFromHistoryAsync(GameItemViewModel item, string backupName, bool webDav)
    {
        IsBusy = true;
        try
        {
            await WithMonitoringPausedAsync(() => webDav
                ? _backupService.RestoreFromWebDavAsync(item.Game, backupName, _config, _webDavPassword, AppendLog)
                : _backupService.RestoreFromOneDriveAsync(item.Game, backupName, _config, AppendLog));
        }
        finally
        {
            IsBusy = false;
            await RefreshGamesAsync();
        }
    }

    public async Task RestoreFromSnapshotAsync(GameItemViewModel item, string snapshotPath)
    {
        IsBusy = true;
        try
        {
            await WithMonitoringPausedAsync(() =>
                _backupService.RestoreFromSnapshotAsync(item.Game, snapshotPath, AppendLog));
        }
        finally
        {
            IsBusy = false;
            await RefreshGamesAsync();
        }
    }

    /// <summary>
    /// Restores replace the save folder, so no watcher may stay attached to the old one;
    /// watchers are recreated on the new folder afterwards (pending changes carry over).
    /// </summary>
    private async Task WithMonitoringPausedAsync(Func<Task> restore)
    {
        var wasMonitoring = _monitoring.IsMonitoring;
        if (wasMonitoring)
            _monitoring.Stop();
        try
        {
            await restore();
        }
        finally
        {
            if (wasMonitoring)
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

    private async Task AutoSyncAllGamesAsync()
    {
        foreach (var game in _config.Games.ToList())
        {
            if (game.Paused || game.BackupMethods.Count == 0 || !Directory.Exists(game.Save))
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
        if (localTime - cloudTime > Constants.SyncThresholdSeconds)
        {
            AppendLog(Localization.T("Log.AutoSyncUpload", game.Name));
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
        else if (cloudTime - localTime > Constants.SyncThresholdSeconds && source is not null)
        {
            AppendLog(Localization.T("Log.AutoSyncDownload", game.Name));
            await WithMonitoringPausedAsync(() => source.Method == Constants.MethodOneDrive
                ? _backupService.RestoreFromOneDriveAsync(game, Path.GetFileName(source.Path), _config, AppendLog)
                : _backupService.RestoreFromWebDavAsync(game, source.Path, _config, _webDavPassword, AppendLog));
            await Task.Delay(1000);
        }
        else
        {
            AppendLog(Localization.T("Log.AutoSyncSynced", game.Name));
        }
    }

    // ---------------- Logging / UI marshalling ----------------

    /// <summary>Writes the message to the log file and to the activity list.</summary>
    public void AppendLog(string message)
    {
        _fileLog?.Write(message);
        ShowLog(message);
    }

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

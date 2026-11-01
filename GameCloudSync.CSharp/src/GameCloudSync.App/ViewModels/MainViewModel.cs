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
        SynchronizationContext ui)
    {
        _configStore = configStore;
        _secretStore = secretStore;
        _backupService = backupService;
        _cloudConfigService = cloudConfigService;
        _monitoring = monitoring;
        _ui = ui;
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
        _monitoring.BackupFinished += () => RunOnUi(() => _ = RefreshGamesAsync());
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
            _ = Task.Run(TestWebDavAsync);
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

    private async Task TestWebDavAsync()
    {
        try
        {
            using var wc = CreateWebDavClient();
            if (await wc.TestConnectionAsync())
                AppendLog(Localization.T("Log.WebDavOk", _config.WebDavUrl));
            else
                AppendLog(Localization.T("Log.WebDavFail", _config.WebDavUrl));
        }
        catch (Exception ex)
        {
            AppendLog(Localization.T("Log.WebDavError", ex.Message));
        }
    }

    private WebDavClient CreateWebDavClient()
    {
        return new WebDavClient(_config.WebDavUrl, _config.WebDavUsername, _webDavPassword,
            log: AppendLog,
            debug: _config.DebugWebDavLog ? AppendLog : null);
    }

    // ---------------- List & status ----------------

    public async Task RefreshGamesAsync(bool checkWebDav = false)
    {
        foreach (var item in Games.ToList())
        {
            item.Status = await _backupService.GetSyncStatusAsync(
                item.Game, _config, _webDavPassword, checkWebDav);
        }
    }

    // ---------------- Backup / restore ----------------

    public async Task ForceBackupAsync()
    {
        var selected = _selectedGames
            .Where(g => g.Game.BackupMethods.Count > 0 && Directory.Exists(g.Game.Save))
            .ToList();
        if (selected.Count == 0)
            return;
        IsBusy = true;
        try
        {
            foreach (var item in selected)
            {
                await _backupService.PerformBackupAsync(
                    item.Game, _config, _webDavPassword, AppendLog);
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshGamesAsync();
        }
    }

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
            if (source.Method == Constants.MethodOneDrive)
                await _backupService.RestoreFromOneDriveAsync(item.Game, Path.GetFileName(source.Path), _config, AppendLog);
            else
                await _backupService.RestoreFromWebDavAsync(item.Game, source.Path, _config, _webDavPassword, AppendLog);
        }
        finally
        {
            // Replacing a save folder leaves FileSystemWatcher attached to the old directory.
            _monitoring.RestartIfActive();
            IsBusy = false;
            await RefreshGamesAsync();
        }
    }

    public async Task RestoreFromHistoryAsync(GameItemViewModel item, string backupName, bool webDav)
    {
        IsBusy = true;
        try
        {
            if (webDav)
                await _backupService.RestoreFromWebDavAsync(item.Game, backupName, _config, _webDavPassword, AppendLog);
            else
                await _backupService.RestoreFromOneDriveAsync(item.Game, backupName, _config, AppendLog);
        }
        finally
        {
            _monitoring.RestartIfActive();
            IsBusy = false;
            await RefreshGamesAsync();
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
        var localNames = _config.Games.Select(g => g.Name).ToHashSet();
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
        var cloudNames = games.Select(g => g.Name).ToHashSet();
        var newNames = cloudNames.Except(localNames).ToList();
        AppendLog(Localization.T("Log.CfgDetectSummary", source, games.Count, localNames.Count));

        if (newNames.Count == 0)
        {
            foreach (var alt in cloudConfigs.Where(c => c.Source != source))
            {
                var altNew = alt.Games.Select(g => g.Name).Except(localNames).ToList();
                if (altNew.Count > 0)
                {
                    _pendingCloudGames = alt.Games.Where(g => altNew.Contains(g.Name)).ToList();
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

        _pendingCloudGames = games.Where(g => newNames.Contains(g.Name)).ToList();
        _pendingCloudSource = source;
        HasPendingCloudUpdate = true;
        AppendLog(Localization.T("Log.CfgDetectNew", _pendingCloudGames.Count, string.Join(", ", newNames)));
        AppendLog(Localization.T("Log.CfgDetectUpdateBtn"));
    }

    public async Task<int> ApplyPendingCloudUpdateAsync()
    {
        if (_pendingCloudGames.Count == 0)
            return 0;
        var localNames = _config.Games.Select(g => g.Name).ToHashSet();
        var imported = 0;
        foreach (var ng in _pendingCloudGames)
        {
            if (localNames.Contains(ng.Name))
                continue;
            ng.Save = FileUtil.FixSavePath(ng.Save);
            _config.Games.Add(ng);
            Games.Add(new GameItemViewModel(ng));
            localNames.Add(ng.Name);
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
        var localNames = _config.Games.Select(g => g.Name).ToHashSet();
        var imported = 0;
        foreach (var ng in cloudGames)
        {
            if (string.IsNullOrEmpty(ng.Name) || localNames.Contains(ng.Name))
                continue;
            ng.Save = FileUtil.FixSavePath(ng.Save);
            _config.Games.Add(ng);
            Games.Add(new GameItemViewModel(ng));
            localNames.Add(ng.Name);
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
        foreach (var game in _config.Games)
        {
            if (game.Paused || game.BackupMethods.Count == 0 || !Directory.Exists(game.Save))
                continue;
            var localTime = FileUtil.GetLatestMTime(game.Save);
            if (localTime == 0)
                continue;
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
                await _backupService.PerformBackupAsync(game, _config, _webDavPassword, AppendLog);
                await Task.Delay(1000);
            }
            else if (cloudTime - localTime > Constants.SyncThresholdSeconds && source is not null)
            {
                AppendLog(Localization.T("Log.AutoSyncDownload", game.Name));
                if (source.Method == Constants.MethodOneDrive)
                    await _backupService.RestoreFromOneDriveAsync(game, Path.GetFileName(source.Path), _config, AppendLog);
                else
                    await _backupService.RestoreFromWebDavAsync(game, source.Path, _config, _webDavPassword, AppendLog);
                _monitoring.RestartIfActive();
                await Task.Delay(1000);
            }
            else
            {
                AppendLog(Localization.T("Log.AutoSyncSynced", game.Name));
            }
        }
        RunOnUi(() => _ = RefreshGamesAsync());
    }

    // ---------------- Logging / UI marshalling ----------------

    public void AppendLog(string message)
    {
        RunOnUi(() =>
        {
            Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
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

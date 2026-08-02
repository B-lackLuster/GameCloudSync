using System.Collections.ObjectModel;
using System.Collections;
using System.IO;
using GameCloudSync.Core;
using System.Windows.Media;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;

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
        _webDavPassword = _secretStore.LoadPassword() ?? "";

        _monitoring.StateChanged += () => RunOnUi(() =>
        {
            IsMonitoring = _monitoring.IsMonitoring;
            _monitoredCount = _monitoring.MonitoredCount;
            OnPropertyChanged(nameof(MonitorStateText));
        });
        _monitoring.BackupFinished += () => RunOnUi(() => _ = RefreshGamesAsync());
    }

    public AppConfig Config => _config;
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
        ? $"⬆ 更新配置 ({_pendingCloudGames.Count} 个新游戏)"
        : "⬆ 更新配置";

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
                OnPropertyChanged(nameof(MonitorButtonText));
        }
    }

    public string MonitorButtonText => IsMonitoring ? "停止监控" : "启动监控";

    public string MonitorStateText => IsMonitoring
        ? $"● 监控中 ({_monitoredCount} 个游戏)"
        : "○ 未监控";

    public string CloudStatusText
    {
        get
        {
            var parts = new List<string>();
            parts.Add(string.IsNullOrEmpty(_config.CloudRoot) ? "OneDrive: 未设置" : $"OneDrive: {_config.CloudRoot}");
            parts.Add(string.IsNullOrEmpty(_config.WebDavUrl) ? "WebDAV: 未设置" : $"WebDAV: {_config.WebDavUrl}");
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
        AppendLog($"欢迎！共 {Games.Count} 个受监控游戏。");
        if (_config.WebDavUrl.Length > 0)
            _ = Task.Run(TestWebDavAsync);
    }

    public void AutoStart()
    {
        if (_config.Games.Count == 0)
        {
            AppendLog("💡 欢迎！列表为空，请先添加游戏或导入配置。");
            return;
        }
        if (!IsMonitoring)
            _monitoring.Start();
        if (_config.AutoSyncOnStartup)
        {
            AppendLog("🔄 正在检查各游戏存档同步状态...");
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
                AppendLog($"☆ WebDAV 连接成功: {_config.WebDavUrl}");
            else
                AppendLog($"☆ WebDAV 连接失败，请检查地址和账号密码: {_config.WebDavUrl}");
        }
        catch (Exception ex)
        {
            AppendLog($"☆ WebDAV 连接异常: {ex.Message}");
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
        foreach (var item in Games)
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
            var dateText = BackupName.ToDateTime(ts)?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知时间";
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
        AppendLog($"🗑 {item.Name}: 已删除备份 {backupName}");
        return Task.CompletedTask;
    }

    public async Task DeleteWebDavBackupAsync(GameItemViewModel item, string backupName)
    {
        using var wc = CreateWebDavClient();
        await wc.DeletePathAsync($"{item.Game.Name}/{backupName}");
        AppendLog($"🗑 {item.Name}: 已删除 WebDAV 备份 {backupName}");
    }

    public Task DeleteAllOneDriveBackupsAsync(GameItemViewModel item)
    {
        var dir = Path.Combine(_config.CloudRoot, item.Game.Name);
        FileUtil.DeleteDirectorySafe(dir);
        AppendLog($"🗑 {item.Name}: 已清空所有 OneDrive 云端备份");
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
        AppendLog($"⇩ {item.Name}: 已下载 WebDAV 备份 {backupName} ({count} 个文件)");
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
            AppendLog("☆ [配置检测] 云端暂无游戏配置备份，跳过检查。");
            HasPendingCloudUpdate = false;
            return;
        }

        var selected = SelectCloudConfig(cloudConfigs);
        if (selected is null)
        {
            AppendLog("☆ [配置检测] 未找到可用的云端配置。");
            HasPendingCloudUpdate = false;
            return;
        }

        var (source, _, games) = selected.Value;
        var cloudNames = games.Select(g => g.Name).ToHashSet();
        var newNames = cloudNames.Except(localNames).ToList();
        AppendLog($"☆ [配置检测] 云端 ({source}) 共 {games.Count} 个游戏，本地 {localNames.Count} 个。");

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
                    AppendLog($"☆ [配置检测] 偏好源 ({source}) 无新游戏，但 {alt.Source} 有 {altNew.Count} 个新游戏。");
                    AppendLog("📥 有新游戏，按「更新配置」按钮立即更新");
                    return;
                }
            }
            AppendLog("☆ [配置检测] 云端游戏配置与本地一致，无需更新。");
            HasPendingCloudUpdate = false;
            return;
        }

        _pendingCloudGames = games.Where(g => newNames.Contains(g.Name)).ToList();
        _pendingCloudSource = source;
        HasPendingCloudUpdate = true;
        AppendLog($"☆ [配置检测] 云端有 {_pendingCloudGames.Count} 个新游戏: {string.Join(", ", newNames)}");
        AppendLog("📥 有新游戏，按「更新配置」按钮立即更新");
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
            AppendLog($"☆ 更新配置：从 {_pendingCloudSource} 导入 {imported} 个新游戏。");
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
            AppendLog($"☆ 从云端配置 ({sourceLabel}) 导入 {imported} 个新游戏。");
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
                AppendLog($"📤 启动同步: {game.Name} 本地较新，自动备份..");
                await _backupService.PerformBackupAsync(game, _config, _webDavPassword, AppendLog);
                await Task.Delay(1000);
            }
            else if (cloudTime - localTime > Constants.SyncThresholdSeconds && source is not null)
            {
                AppendLog($"📥 启动同步: {game.Name} 云端较新，自动下载..");
                if (source.Method == Constants.MethodOneDrive)
                    await _backupService.RestoreFromOneDriveAsync(game, Path.GetFileName(source.Path), _config, AppendLog);
                else
                    await _backupService.RestoreFromWebDavAsync(game, source.Path, _config, _webDavPassword, AppendLog);
                await Task.Delay(1000);
            }
            else
            {
                AppendLog($"✓ 启动同步: {game.Name} 已完全同步，跳过");
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

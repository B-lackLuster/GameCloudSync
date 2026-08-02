using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameCloudSync.App.Services;
using GameCloudSync.App.ViewModels;
using GameCloudSync.Core;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;
using Microsoft.Win32;

namespace GameCloudSync.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly TrayIconService _tray;
    private bool _allowClose;

    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public MainWindow(MainViewModel viewModel, TrayIconService tray)
    {
        InitializeComponent();
        _vm = viewModel;
        _tray = tray;
        DataContext = viewModel;
        viewModel.Logs.CollectionChanged += OnLogsChanged;
        Closing += OnWindowClosing;
        var wakeupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        wakeupTimer.Tick += (_, _) => viewModel.CheckWakeupFile();
        wakeupTimer.Start();
    }

    public MainViewModel ViewModel => _vm;

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ScrollIntoView inside the CollectionChanged callback triggers an
        // immediate layout pass while the generator is mid-change, which
        // crashes WPF with "ItemsControl is inconsistent with its item source".
        // Defer the scroll until layout is idle.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_vm.Logs.Count > 0)
                LogList.ScrollIntoView(_vm.Logs[_vm.Logs.Count - 1]);
        });
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || !_vm.Config.BackgroundMode)
            return;
        e.Cancel = true;
        Hide();
        _vm.AppendLog("面板已隐藏，程序在托盘继续运行...");
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SetSelection(GameList.SelectedItems);
    }

    private void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(GameList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is null)
            return;
        if (!item.IsSelected)
        {
            GameList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        _vm.SetSelection(GameList.SelectedItems);

        var menu = (ContextMenu)FindResource("GameContextMenu");
        if (_vm.SelectedGames.Count == 1)
        {
            var game = _vm.SelectedGames[0].Game;
            foreach (var menuItem in menu.Items.OfType<MenuItem>())
            {
                if ((string)menuItem.Header == "备份到 OneDrive")
                    menuItem.IsChecked = game.HasMethod(Constants.MethodOneDrive);
                else if ((string)menuItem.Header == "备份到 WebDAV")
                    menuItem.IsChecked = game.HasMethod(Constants.MethodWebDav);
            }
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---------------- Toolbar ----------------

    private void OnAddGame(object sender, RoutedEventArgs e)
    {
        var dialog = new AddGameDialog(_vm.Config)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;
        var game = dialog.Result!;
        _vm.Config.Games.Add(game);
        _vm.ConfigStore.Save(_vm.Config);
        _vm.Games.Add(new GameItemViewModel(game));
        if (_vm.Config.AutoBackupConfigToCloud)
            _ = _vm.BackupConfigToCloudAsync();
        _vm.RestartMonitoringIfActive();
        _vm.AppendLog($"＋ 已添加游戏「{game.Name}」，备份方式: {string.Join(", ", game.BackupMethods)}");
        if (_vm.Monitoring.IsMonitoring && Directory.Exists(game.Save) && Directory.EnumerateFileSystemEntries(game.Save).Any())
            _ = Task.Run(() => _vm.BackupService.PerformBackupAsync(game, _vm.Config, _vm.WebDavPassword, _vm.AppendLog));
        _ = _vm.RefreshGamesAsync();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_vm)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _vm.RestartMonitoringIfActive();
            _ = _vm.RefreshGamesAsync();
        }
    }

    private void OnToggleMonitoring(object sender, RoutedEventArgs e)
    {
        if (_vm.IsMonitoring)
            _vm.Monitoring.Stop();
        else
            _vm.Monitoring.Start();
        _ = _vm.RefreshGamesAsync();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _ = _vm.RefreshGamesAsync(checkWebDav: true);
    }

    private async void OnForceBackup(object sender, RoutedEventArgs e)
    {
        var selected = _vm.SelectedGames.Where(g => Directory.Exists(g.Game.Save)).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先选中需要上传的游戏。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (selected.Count > 1 &&
            MessageBox.Show(this, $"确定为选中的 {selected.Count} 个游戏执行手动上传吗？", "批量上传",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            await _vm.ForceBackupAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"上传失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        var selected = _vm.SelectedGames.ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先选中需要下载覆盖的游戏。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (selected.Count > 1)
            {
                var pref = _vm.Config.DefaultDownloadSource;
                if (pref == Constants.SourceAsk)
                {
                    var picker = new SourcePickerDialog(null)
                    {
                        Owner = this
                    };
                    if (picker.ShowDialog() != true)
                        return;
                    pref = picker.Result == "OneDrive 优先"
                        ? Constants.MethodOneDrive
                        : Constants.MethodWebDav;
                }
                if (MessageBox.Show(this, $"确定要用云端最新备份覆盖选中的 {selected.Count} 个游戏的本地存档吗？",
                        "批量覆盖下载", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }
                var oldPref = _vm.Config.DefaultDownloadSource;
                _vm.Config.DefaultDownloadSource = pref;
                try
                {
                    foreach (var item in selected)
                    {
                        var source = await _vm.BackupService.FindLatestSourceAsync(
                            item.Game, _vm.Config, _vm.WebDavPassword);
                        if (source is null)
                        {
                            _vm.AppendLog($"⇩ ✗ {item.Name}: 未找到任何云端备份，跳过");
                            continue;
                        }
                        await _vm.RestoreFromSourceAsync(item, source);
                    }
                }
                finally
                {
                    _vm.Config.DefaultDownloadSource = oldPref;
                }
            }
            else
            {
                var item = selected[0];
                var sources = await _vm.CollectRestoreSourcesAsync(item);
                if (sources.Count == 0)
                {
                    MessageBox.Show(this, "未找到任何云端备份！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                BackupSource? source;
                if (sources.Count == 1)
                {
                    source = sources[0];
                }
                else if (_vm.Config.DefaultDownloadSource == Constants.SourceAsk)
                {
                    var picker = new SourcePickerDialog(sources.Select(s => $"{s.Label}: {s.Path}").ToList())
                    {
                        Owner = this
                    };
                    if (picker.ShowDialog() != true)
                        return;
                    source = sources[picker.SelectedIndex];
                }
                else
                {
                    source = await _vm.BackupService.FindLatestSourceAsync(
                        item.Game, _vm.Config, _vm.WebDavPassword);
                }
                if (source is null)
                    return;

                var confirm = BuildRestoreConfirmMessage(item, source);
                if (MessageBox.Show(this, confirm, "覆盖确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                await _vm.RestoreFromSourceAsync(item, source);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"恢复失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string BuildRestoreConfirmMessage(GameItemViewModel item, BackupSource source)
    {
        var localTs = FileUtil.GetLatestMTime(item.Game.Save);
        long cloudTs;
        if (source.Method == Constants.MethodOneDrive)
        {
            cloudTs = FileUtil.GetLatestMTime(source.Path);
        }
        else
        {
            var (ts, mtime) = BackupName.Parse(source.Path);
            cloudTs = mtime > 0 ? mtime :
                BackupName.ToDateTime(ts) is { } dt
                    ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)).ToUnixTimeSeconds()
                    : 0;
        }
        var localStr = localTs > 0
            ? DateTimeOffset.FromUnixTimeSeconds(localTs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "无本地";
        var cloudStr = cloudTs > 0
            ? DateTimeOffset.FromUnixTimeSeconds(cloudTs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "未知";
        var headline = localTs == 0
            ? "本地无存档。\n"
            : cloudTs > localTs
                ? "☁ 云端较新\n"
                : "⚠ 本地较新或相同！\n";
        return $"{headline}\n来源: {source.Label}\n[云端]: {cloudStr}\n[本地]: {localStr}\n\n确定覆盖？";
    }

    private void OnOpenLocalFolder(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedGames.FirstOrDefault();
        if (item is null)
        {
            MessageBox.Show(this, "请先选中一个游戏！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Directory.Exists(item.Game.Save))
            FileUtil.TryOpenInExplorer(item.Game.Save);
        else
            MessageBox.Show(this, $"本地存档路径不存在：\n{item.Game.Save}\n\n可能尚未创建或已被删除。", "路径不存在",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnOpenCloudFolder(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedGames.FirstOrDefault();
        if (item is null)
        {
            MessageBox.Show(this, "请先选中一个游戏！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (item.Game.HasMethod(Constants.MethodOneDrive) && !string.IsNullOrEmpty(_vm.Config.CloudRoot))
        {
            var dir = Path.Combine(_vm.Config.CloudRoot, item.Game.Name);
            Directory.CreateDirectory(dir);
            FileUtil.TryOpenInExplorer(dir);
        }
        else if (item.Game.HasMethod(Constants.MethodWebDav) && !string.IsNullOrEmpty(_vm.Config.WebDavUrl))
        {
            var url = _vm.Config.WebDavUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(item.Game.Name);
            FileUtil.TryOpenBrowser(url);
        }
        else
        {
            MessageBox.Show(this, "该游戏未配置可用的云端目录目标。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        var selected = _vm.SelectedGames.ToList();
        if (selected.Count == 0)
            return;
        var newPaused = !selected[0].Game.Paused;
        foreach (var item in selected)
        {
            item.Game.Paused = newPaused;
            item.Refresh();
            _vm.AppendLog($"⏸ 游戏「{item.Name}」已{(newPaused ? "暂停" : "恢复")}同步。");
        }
        _vm.ConfigStore.Save(_vm.Config);
        _vm.RestartMonitoringIfActive();
        _ = _vm.RefreshGamesAsync();
    }

    private void OnToggleOneDriveMethod(object sender, RoutedEventArgs e)
    {
        ToggleBackupMethod(Constants.MethodOneDrive);
    }

    private void OnToggleWebDavMethod(object sender, RoutedEventArgs e)
    {
        ToggleBackupMethod(Constants.MethodWebDav);
    }

    private void ToggleBackupMethod(string method)
    {
        var item = _vm.SelectedGames.FirstOrDefault();
        if (item is null)
            return;
        var game = item.Game;
        if (game.HasMethod(method))
        {
            game.BackupMethods.Remove(method);
        }
        else
        {
            if (method == Constants.MethodOneDrive && string.IsNullOrEmpty(_vm.Config.CloudRoot))
            {
                MessageBox.Show(this, "OneDrive 云盘目录未设置，请先在全局设置中配置。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (method == Constants.MethodWebDav && string.IsNullOrEmpty(_vm.Config.WebDavUrl))
            {
                MessageBox.Show(this, "WebDAV 未配置，请先在全局设置中配置。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            game.BackupMethods.Add(method);
        }
        if (game.BackupMethods.Count == 0)
            game.BackupMethods.Add(Constants.MethodOneDrive);
        item.Refresh();
        _vm.ConfigStore.Save(_vm.Config);
        _vm.RestartMonitoringIfActive();
        _vm.AppendLog($"⚙ 游戏「{game.Name}」备份方式已更新: {string.Join(", ", game.BackupMethods)}");
        _ = _vm.RefreshGamesAsync();
    }

    private void OnShowHistory(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedGames.FirstOrDefault();
        if (item is null)
        {
            MessageBox.Show(this, "请先选中一个游戏！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new HistoryWindow(_vm, item)
        {
            Owner = this
        };
        window.Show();
    }

    private void OnEditGame(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedGames.FirstOrDefault();
        if (item is null)
        {
            MessageBox.Show(this, "请先选中一个游戏！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new EditGameDialog(_vm, item.Game)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            item.Refresh();
            _vm.ConfigStore.Save(_vm.Config);
            if (_vm.Config.AutoBackupConfigToCloud)
                _ = _vm.BackupConfigToCloudAsync();
            _vm.RestartMonitoringIfActive();
            _ = _vm.RefreshGamesAsync();
        }
    }

    private void OnDeleteGame(object sender, RoutedEventArgs e)
    {
        var selected = _vm.SelectedGames.ToList();
        if (selected.Count == 0)
            return;
        if (MessageBox.Show(this, "确定删除所选游戏配置吗？", "确认", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        foreach (var item in selected)
        {
            _vm.Config.Games.Remove(item.Game);
            _vm.Games.Remove(item);
        }
        _vm.ConfigStore.Save(_vm.Config);
        if (_vm.Config.AutoBackupConfigToCloud)
            _ = _vm.BackupConfigToCloudAsync();
        _vm.RestartMonitoringIfActive();
    }

    private void OnExportSelected(object sender, RoutedEventArgs e)
    {
        var selected = _vm.SelectedGames.ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先选中要导出的游戏。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var initial = selected.Count == 1
            ? $"GameCloudSync_{selected[0].Game.Name}.json"
            : "GameCloudSync_Selected.json";
        var path = SaveJsonDialog(initial, "导出所选游戏配置");
        if (path is null)
            return;
        var payload = selected.Select(g => new
        {
            g.Game.Name,
            g.Game.Save,
            g.Game.Paused,
            backup_methods = g.Game.BackupMethods,
            g.Game.MaxBackups
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(payload, ConfigJsonOptions), new UTF8Encoding(false));
        _vm.AppendLog($"📤 已合并导出 {selected.Count} 个游戏配置到: {path}");
        MessageBox.Show(this, $"已导出 {selected.Count} 个游戏配置。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExportConfig(object sender, RoutedEventArgs e)
    {
        if (_vm.Config.Games.Count == 0)
        {
            MessageBox.Show(this, "当前列表为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var path = SaveJsonDialog("GameCloudSync_Config.json", "导出配置文件");
        if (path is null)
            return;
        var payload = _vm.Config.Games.Select(g => new
        {
            g.Name,
            g.Save,
            g.Paused,
            backup_methods = g.BackupMethods,
            g.MaxBackups
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(payload, ConfigJsonOptions), new UTF8Encoding(false));
        _vm.AppendLog($"📤 全部配置已导出至: {path}");
        MessageBox.Show(this, "配置导出成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnImportConfig(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog
        {
            Filter = "JSON Config (*.json)|*.json",
            Title = "选择要导入的配置文件"
        };
        if (open.ShowDialog(this) != true)
            return;
        try
        {
            var text = await File.ReadAllTextAsync(open.FileName);
            var root = JsonNode.Parse(text);
            List<GameConfig> importedGames;
            if (root is JsonArray)
            {
                importedGames = JsonSerializer.Deserialize<List<GameConfig>>(text, ConfigJsonOptions) ?? new();
            }
            else if (root is JsonObject obj)
            {
                if (obj["webdav_url"]?.GetValue<string>() is { Length: > 0 } url &&
                    string.IsNullOrEmpty(_vm.Config.WebDavUrl) &&
                    MessageBox.Show(this, "配置文件中包含 WebDAV 设置，是否一同导入？", "导入 WebDAV 配置",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _vm.Config.WebDavUrl = url;
                    _vm.Config.WebDavUsername = obj["webdav_username"]?.GetValue<string>() ?? "";
                    var importedPw = obj["webdav_password"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(importedPw))
                    {
                        _vm.SecretStore.SavePassword(importedPw);
                        _vm.SetWebDavPassword(importedPw);
                    }
                    else
                    {
                        _vm.SecretStore.ClearPassword();
                    }
                }
                if (obj["default_max_backups"] is { } mb && mb.GetValue<int>() > 0)
                    _vm.Config.DefaultMaxBackups = mb.GetValue<int>();
                if (obj["default_download_source"] is { } dl && dl.GetValue<string>() is { Length: > 0 } dlSrc)
                    _vm.Config.DefaultDownloadSource = dlSrc;
                importedGames = obj["games"] is JsonArray ga
                    ? JsonSerializer.Deserialize<List<GameConfig>>(ga.ToJsonString(), ConfigJsonOptions) ?? new()
                    : new List<GameConfig>();
            }
            else
            {
                MessageBox.Show(this, "配置文件格式不正确！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (importedGames.Count == 0)
            {
                MessageBox.Show(this, "配置文件中没有游戏。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            foreach (var ig in importedGames)
                NormalizeImportedGame(ig);

            // OneDrive root confirmation
            if (string.IsNullOrEmpty(_vm.Config.CloudRoot))
            {
                var folder = PickFolder("选择 OneDrive 云盘总目录");
                if (folder is not null)
                    _vm.Config.CloudRoot = folder;
            }
            else if (MessageBox.Show(this, $"当前 OneDrive 云盘根目录为：\n{_vm.Config.CloudRoot}\n\n是否替换为新目录？",
                         "更新云盘目录", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var folder = PickFolder("选择新的 OneDrive 云盘总目录");
                if (folder is not null)
                    _vm.Config.CloudRoot = folder;
            }

            var importedCount = 0;
            var fixedPaths = 0;
            foreach (var ig in importedGames)
            {
                if (string.IsNullOrEmpty(ig.Name) || string.IsNullOrEmpty(ig.Save))
                    continue;
                var fixedSave = FileUtil.FixSavePath(ig.Save);
                if (fixedSave != ig.Save)
                    fixedPaths++;
                ig.Save = fixedSave;
                if (_vm.Config.Games.Any(g => g.Name == ig.Name))
                {
                    if (MessageBox.Show(this, $"游戏「{ig.Name}」已存在，是否覆盖？", "重复游戏",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        continue;
                    }
                    _vm.Config.Games.RemoveAll(g => g.Name == ig.Name);
                    var existing = _vm.Games.FirstOrDefault(g => g.Name == ig.Name);
                    if (existing is not null)
                        _vm.Games.Remove(existing);
                }
                _vm.Config.Games.Add(ig);
                _vm.Games.Add(new GameItemViewModel(ig));
                importedCount++;
            }

            _vm.ConfigStore.Save(_vm.Config);
            if (_vm.Config.AutoBackupConfigToCloud)
                await _vm.BackupConfigToCloudAsync();
            _vm.RestartMonitoringIfActive();
            if (fixedPaths > 0)
                _vm.AppendLog($"📥 已自动适配 {fixedPaths} 个路径中的用户名，共导入 {importedCount} 个游戏配置。");
            else
                _vm.AppendLog($"📥 成功导入 {importedCount} 个游戏配置。");
            MessageBox.Show(this, $"已导入 {importedCount} 个游戏。", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = _vm.RefreshGamesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnApplyCloudUpdate(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasPendingCloudUpdate)
        {
            MessageBox.Show(this, "当前没有待同步的云端新游戏。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var count = await _vm.ApplyPendingCloudUpdateAsync();
        if (count > 0)
            MessageBox.Show(this, $"已从云端配置导入 {count} 个新游戏。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnRestoreConfigFromCloud(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfigRestoreDialog(_vm)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;
        var (source, filename, games) = dialog.Result!.Value;
        if (games.Count == 0)
        {
            MessageBox.Show(this, $"{source} 当前没有可恢复的游戏配置。", "不可恢复",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var localNames = _vm.Config.Games.Select(g => g.Name).ToHashSet();
        var newGames = games.Where(g => !localNames.Contains(g.Name)).ToList();
        if (newGames.Count == 0)
        {
            MessageBox.Show(this, $"{source} 最新配置中没有新游戏。\n\n云端共 {games.Count} 个游戏，本地已全部匹配。", "无需下载",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var msg = $"来源: {source}   ←  {filename}\n\n" +
                  $"☁ 云端配置中共有 {games.Count} 个游戏\n" +
                  $"⇩ 将新增 {newGames.Count} 个游戏\n" +
                  string.Concat(newGames.Select(g => $"    ＋ {g.Name}\n")) +
                  $"\n✓ 本地已有 {games.Count - newGames.Count} 个匹配 (保持不变)\n\n" +
                  "⚠ 本次只添加新游戏，不会修改任何已有游戏配置。\n是否下载？";
        if (MessageBox.Show(this, msg, "云端配置对比", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var imported = await _vm.MergeCloudConfigAsync(source, filename, games);
        if (imported > 0)
            MessageBox.Show(this, $"已从云端配置导入 {imported} 个新游戏。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnBackupConfigToCloud(object sender, RoutedEventArgs e)
    {
        await _vm.BackupConfigToCloudAsync();
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        _vm.Logs.Clear();
    }

    private void OnOpenMoreMenu(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("MoreMenu");
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------------- Helpers ----------------

    private string? SaveJsonDialog(string initialFile, string title)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON Config (*.json)|*.json",
            Title = title,
            FileName = initialFile,
            DefaultExt = ".json",
            AddExtension = true
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private static void NormalizeImportedGame(GameConfig game)
    {
        game.Name ??= "";
        game.Save ??= "";
        game.BackupMethods ??= new List<string>();
        if (game.BackupMethods.Count == 0)
            game.BackupMethods.Add(Constants.MethodOneDrive);
        if (game.MaxBackups <= 0)
            game.MaxBackups = Constants.DefaultMaxBackups;
    }
}

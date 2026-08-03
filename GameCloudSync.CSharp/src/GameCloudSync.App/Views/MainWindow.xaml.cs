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
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;
using Microsoft.Win32;
using Localization = GameCloudSync.Core.Services.Localization;

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
        _vm.AppendLog(Localization.T("Main.HiddenToTray"));
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
                if ((string)menuItem.Header == Localization.T("Menu.BackupToOneDrive"))
                    menuItem.IsChecked = game.HasMethod(Constants.MethodOneDrive);
                else if ((string)menuItem.Header == Localization.T("Menu.BackupToWebDav"))
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
        _vm.AppendLog(Localization.T("Msg.AddedGameLog", game.Name, string.Join(", ", game.BackupMethods)));
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
            MessageBox.Show(this, Localization.T("Msg.SelectUploadGames"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (selected.Count > 1 &&
            MessageBox.Show(this, Localization.T("Msg.BatchUploadConfirm", selected.Count), Localization.T("Msg.BatchUploadTitle"),
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
            MessageBox.Show(this, Localization.T("Msg.UploadFailed", ex.Message), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        var selected = _vm.SelectedGames.ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, Localization.T("Msg.SelectDownloadGames"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
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
                    pref = picker.Result == Localization.T("Pick.OneDriveFirst")
                        ? Constants.MethodOneDrive
                        : Constants.MethodWebDav;
                }
                if (MessageBox.Show(this, Localization.T("Msg.BatchDownloadConfirm", selected.Count),
                        Localization.T("Msg.BatchDownloadTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
                            _vm.AppendLog(Localization.T("Msg.SkipNoBackupLog", item.Name));
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
                    MessageBox.Show(this, Localization.T("Msg.NoCloudBackup"), Localization.T("Common.Info"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
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
                if (MessageBox.Show(this, confirm, Localization.T("Msg.RestoreConfirmTitle"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                await _vm.RestoreFromSourceAsync(item, source);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localization.T("Msg.RestoreFailed", ex.Message), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
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
            : Localization.T("Msg.RestoreNoLocal");
        var cloudStr = cloudTs > 0
            ? DateTimeOffset.FromUnixTimeSeconds(cloudTs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : Localization.T("Msg.RestoreUnknown");
        var headline = localTs == 0
            ? Localization.T("Msg.RestoreConfirmLocalEmpty")
            : cloudTs > localTs
                ? Localization.T("Msg.RestoreConfirmCloudNew")
                : Localization.T("Msg.RestoreConfirmLocalNew");
        return $"{headline}\n{Localization.T("Msg.RestoreSource", source.Label)}\n" +
               $"{Localization.T("Msg.RestoreCloudTime", cloudStr)}\n" +
               $"{Localization.T("Msg.RestoreLocalTime", localStr)}\n\n" +
               Localization.T("Msg.RestoreAsk");
    }

    private void OnOpenLocalFolder(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedGames.FirstOrDefault();
        if (item is null)
        {
            MessageBox.Show(this, Localization.T("Msg.SelectGameFirst"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Directory.Exists(item.Game.Save))
            FileUtil.TryOpenInExplorer(item.Game.Save);
        else
            MessageBox.Show(this, Localization.T("Msg.PathMissing", item.Game.Save), Localization.T("Msg.PathMissingTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnOpenCloudFolder(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedGames.FirstOrDefault();
        if (item is null)
        {
            MessageBox.Show(this, Localization.T("Msg.SelectGameFirst"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(this, Localization.T("Msg.NoCloudTarget"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
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
            _vm.AppendLog(Localization.T("Msg.PauseLog", item.Name,
                Localization.T(newPaused ? "Msg.Paused" : "Msg.Resumed")));
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
                MessageBox.Show(this, Localization.T("Add.OdNotConfigured"), Localization.T("Common.Info"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (method == Constants.MethodWebDav && string.IsNullOrEmpty(_vm.Config.WebDavUrl))
            {
                MessageBox.Show(this, Localization.T("Add.WdNotConfigured"), Localization.T("Common.Info"),
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
        _vm.AppendLog(Localization.T("Msg.MethodUpdatedLog", game.Name, string.Join(", ", game.BackupMethods)));
        _ = _vm.RefreshGamesAsync();
    }

    private void OnShowHistory(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedGames.FirstOrDefault();
        if (item is null)
        {
            MessageBox.Show(this, Localization.T("Msg.SelectGameFirst"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(this, Localization.T("Msg.SelectGameFirst"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (MessageBox.Show(this, Localization.T("Msg.DeleteGamesConfirm"), Localization.T("Common.Confirm"), MessageBoxButton.YesNo,
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
            MessageBox.Show(this, Localization.T("Msg.NoSelectionExport"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var initial = selected.Count == 1
            ? $"GameCloudSync_{selected[0].Game.Name}.json"
            : "GameCloudSync_Selected.json";
        var path = SaveJsonDialog(initial, Localization.T("Menu.ExportSelected"));
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
        _vm.AppendLog(Localization.T("Msg.ExportSelectedLog", selected.Count, path));
        MessageBox.Show(this, Localization.T("Msg.ExportSelectedDone", selected.Count), Localization.T("Common.Done"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExportConfig(object sender, RoutedEventArgs e)
    {
        if (_vm.Config.Games.Count == 0)
        {
            MessageBox.Show(this, Localization.T("Msg.EmptyListExport"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var path = SaveJsonDialog("GameCloudSync_Config.json", Localization.T("Menu.ExportAll"));
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
        _vm.AppendLog(Localization.T("Msg.ExportAllLog", path));
        MessageBox.Show(this, Localization.T("Msg.ExportOk"), Localization.T("Common.Success"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnImportConfig(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog
        {
            Filter = "JSON Config (*.json)|*.json",
            Title = Localization.T("Msg.ImportPickTitle")
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
                    MessageBox.Show(this, Localization.T("Msg.ImportWebDavAsk"), Localization.T("Msg.ImportWebDavTitle"),
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
                MessageBox.Show(this, Localization.T("Msg.ImportBadFormat"), Localization.T("Common.Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (importedGames.Count == 0)
            {
                MessageBox.Show(this, Localization.T("Msg.ImportNoGames"), Localization.T("Common.Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            foreach (var ig in importedGames)
                NormalizeImportedGame(ig);

            // OneDrive root confirmation
            if (string.IsNullOrEmpty(_vm.Config.CloudRoot))
            {
                var folder = PickFolder(Localization.T("Msg.ImportPickCloudRoot"));
                if (folder is not null)
                    _vm.Config.CloudRoot = folder;
            }
            else if (MessageBox.Show(this, Localization.T("Msg.ImportCloudRootAsk", _vm.Config.CloudRoot),
                         Localization.T("Msg.ImportCloudRootTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var folder = PickFolder(Localization.T("Msg.ImportPickNewCloudRoot"));
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
                    if (MessageBox.Show(this, Localization.T("Msg.ImportDuplicateAsk", ig.Name), Localization.T("Msg.ImportDuplicateTitle"),
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
                _vm.AppendLog(Localization.T("Msg.ImportFixedPaths", fixedPaths, importedCount));
            else
                _vm.AppendLog(Localization.T("Msg.ImportPlain", importedCount));
            MessageBox.Show(this, Localization.T("Msg.ImportDone", importedCount), Localization.T("Msg.ImportDoneTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            _ = _vm.RefreshGamesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localization.T("Msg.ImportFailed", ex.Message), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnApplyCloudUpdate(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasPendingCloudUpdate)
        {
            MessageBox.Show(this, Localization.T("Msg.UpdateNoPending"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var count = await _vm.ApplyPendingCloudUpdateAsync();
        if (count > 0)
            MessageBox.Show(this, Localization.T("Msg.UpdateDone", count), Localization.T("Common.Done"),
                MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(this, Localization.T("Msg.NoGamesRestore", source), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var localNames = _vm.Config.Games.Select(g => g.Name).ToHashSet();
        var newGames = games.Where(g => !localNames.Contains(g.Name)).ToList();
        if (newGames.Count == 0)
        {
            MessageBox.Show(this, Localization.T("CfgRestore.NoNew", source, games.Count), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var msg = Localization.T("CfgRestore.Header", source, filename, games.Count, newGames.Count) +
                  string.Concat(newGames.Select(g => $"    ＋ {g.Name}\n")) +
                  Localization.T("CfgRestore.Existing", games.Count - newGames.Count);
        if (MessageBox.Show(this, msg, Localization.T("CfgRestore.CompareTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var imported = await _vm.MergeCloudConfigAsync(source, filename, games);
        if (imported > 0)
            MessageBox.Show(this, Localization.T("CfgRestore.Done", imported), Localization.T("Common.Done"),
                MessageBoxButton.OK, MessageBoxImage.Information);
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

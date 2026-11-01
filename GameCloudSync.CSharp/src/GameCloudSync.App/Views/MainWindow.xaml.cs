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
        FitToWorkArea();
        CustomFrame.Attach(this);
        _vm = viewModel;
        _tray = tray;
        DataContext = viewModel;
        viewModel.Logs.CollectionChanged += OnLogsChanged;
        viewModel.ResolveConflict = ResolveConflictAsync;
        Closing += OnWindowClosing;
        KeyDown += OnWindowKeyDown;
        var wakeupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        wakeupTimer.Tick += (_, _) => viewModel.CheckWakeupFile();
        wakeupTimer.Start();
        // "Last backup: 5 min ago" would otherwise stay frozen until the next refresh.
        var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        clockTimer.Tick += (_, _) => viewModel.RefreshTimes();
        clockTimer.Start();
    }

    public MainViewModel ViewModel => _vm;

    /// <summary>
    /// The window is designed larger than a 1366x768 screen, or a 1080p one at 150% scaling, can show; it
    /// is shrunk to what the screen offers (and may be made smaller still) instead of starting cut off.
    /// </summary>
    private void FitToWorkArea()
    {
        const double margin = 16;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - margin));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - margin));
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
    }

    // Four overview cards in a row need about 800 DIPs; below that they go two by two.
    private void OnOverviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        OverviewCards.Columns = e.NewSize.Width >= 800 ? 4 : 2;
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        FileUtil.TryOpenInExplorer(_vm.LogFolder);
    }

    private void OnNavigateOverview(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Dashboard.Page = WorkspacePage.Overview;
    }

    private void OnNavigateLibrary(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Dashboard.Page = WorkspacePage.Library;
    }

    private void OnNavigateActivity(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Dashboard.Page = WorkspacePage.Activity;
    }

    private void OnOpenLibrary(object sender, RoutedEventArgs e) => _vm.Dashboard.Page = WorkspacePage.Library;
    private void OnOpenActivity(object sender, RoutedEventArgs e) => _vm.Dashboard.Page = WorkspacePage.Activity;

    private void OnPreviewHistory(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameItemViewModel game }) return;
        _vm.Dashboard.Page = WorkspacePage.Library;
        GameList.SelectedItem = game;
        _vm.SetSelection(GameList.SelectedItems);
        OnShowHistory(sender, e);
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximizeWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ScrollIntoView inside the CollectionChanged callback triggers an
        // immediate layout pass while the generator is mid-change, which
        // crashes WPF with "ItemsControl is inconsistent with its item source".
        // Defer the scroll until layout is idle.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_vm.Dashboard.IsActivity && _vm.Logs.Count > 0)
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
        // Said once: otherwise closing the window looks like quitting, while backups carry on in the tray.
        if (!_vm.Config.TrayHintShown)
        {
            _tray.ShowNotification(Localization.T("Tray.HiddenTitle"), Localization.T("Tray.HiddenBody"), isError: false);
            try
            {
                _vm.ApplyConfigChange(config => config.TrayHintShown = true);
            }
            catch (Exception ex)
            {
                _vm.AppendLog(Localization.T("Log.TaskError", ex.Message));
            }
        }
    }

    // F5 refreshes, Ctrl+N adds a game, Del deletes the selected games (only in the library, where they are in view).
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OnRefresh(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnAddGame(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None
                 && _vm.Dashboard.IsLibrary && _vm.CanUseSelection)
        {
            OnDeleteGame(sender, e);
            e.Handled = true;
        }
    }

    private void OnCancelBusy(object sender, RoutedEventArgs e) => _vm.CancelBusy();

    /// <summary>Startup sync found a save that changed on both sides; the user decides which one stays.</summary>
    private Task<ConflictChoice> ResolveConflictAsync(SyncConflict conflict)
    {
        // A login start keeps the window in the tray, but this question needs an answer.
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        var picker = new SourcePickerDialog(
            new List<string> { Localization.T("Conflict.KeepLocal"), Localization.T("Conflict.UseCloud") },
            Localization.T("Conflict.Prompt", conflict.Game.Name, RelativeTime.FormatUnix(conflict.BaselineMTime),
                RelativeTime.FormatUnix(conflict.LocalMTime), conflict.Source.Label, RelativeTime.FormatUnix(conflict.CloudMTime)),
            Localization.T("Conflict.Title"), Localization.T("Conflict.Skip"))
        {
            Owner = this
        };
        var choice = picker.ShowDialog() != true
            ? ConflictChoice.Skip
            : picker.SelectedIndex switch
            {
                0 => ConflictChoice.KeepLocal,
                1 => ConflictChoice.UseCloud,
                _ => ConflictChoice.Skip
            };
        return Task.FromResult(choice);
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
        OpenGameMenu(item);
        e.Handled = true;
    }

    private void OnGameActions(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not GameItemViewModel game)
            return;
        if (!GameList.SelectedItems.Contains(game))
            GameList.SelectedItem = game;
        _vm.SetSelection(GameList.SelectedItems);
        OpenGameMenu(button);
        e.Handled = true;
    }

    private void OpenGameMenu(FrameworkElement target)
    {
        var menu = (ContextMenu)FindResource("GameContextMenu");
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsEnabled = _vm.CanAct;
        foreach (var menuItem in menu.Items.OfType<MenuItem>())
        {
            var header = menuItem.Header as string;
            if (header == Localization.T("Menu.BackupToOneDrive") ||
                header == Localization.T("Menu.BackupToWebDav"))
            {
                menuItem.IsEnabled = _vm.SelectedGames.Count == 1;
                menuItem.IsChecked = false;
            }
            if (header == Localization.T("Menu.PauseSync") || header == Localization.T("Menu.ResumeSync"))
                menuItem.SetBinding(HeaderedItemsControl.HeaderProperty, new System.Windows.Data.Binding(
                    _vm.SelectedGames.FirstOrDefault()?.IsPaused == true ? "[Menu.ResumeSync]" : "[Menu.PauseSync]")
                { Source = Localization.Instance });
        }
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
            _ = _vm.BackupNewGameAsync(game);
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
        // A restore pauses monitoring while it replaces the save; starting it in between would watch the old folder.
        if (!_vm.CanAct)
            return;
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
        if (!_vm.CanAct)
            return;
        var selected = _vm.SelectedGames.ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, Localization.T("Msg.SelectUploadGames"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // A selected game whose folder is gone gets that said, not "select a game".
        var missing = selected.Where(g => !Directory.Exists(g.Game.Save)).ToList();
        var ready = selected.Except(missing).ToList();
        var missingText = string.Join("\n", missing.Select(g => $"{g.Name}: {g.Game.Save}"));
        if (ready.Count == 0)
        {
            MessageBox.Show(this, Localization.T("Msg.UploadSaveMissing", missingText), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if ((ready.Count > 1 || missing.Count > 0) &&
            MessageBox.Show(this, Localization.T("Msg.BatchUploadConfirm", ready.Count)
                                  + (missing.Count > 0 ? Localization.T("Msg.UploadSkipMissing", missingText) : ""),
                Localization.T("Msg.BatchUploadTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            var (failures, _) = await _vm.ForceBackupAsync(ready);
            // Failures used to land in the log only, so an upload that did nothing looked like one that worked.
            if (failures.Count > 0)
                MessageBox.Show(this, Localization.T("Msg.UploadSomeFailed", string.Join("\n", failures)),
                    Localization.T("Common.Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localization.T("Msg.UploadFailed", ex.Message), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanAct)
            return;
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
                await RestoreBatchAsync(selected, pref);
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

                var confirm = _vm.RestoreConfirmMessage(item, source);
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

    /// <summary>
    /// Restores several games at once. Games whose local save is newer than their latest backup are
    /// left alone (restore those one by one to override), and one failure doesn't stop the rest.
    /// </summary>
    private async Task RestoreBatchAsync(List<GameItemViewModel> selected, string preference)
    {
        var plan = new List<(GameItemViewModel Item, BackupSource Source)>();
        var localNewer = new List<string>();
        var noBackup = new List<string>();
        try
        {
            await _vm.RunBusyAsync(Localization.T("Busy.FindingBackups"), async ct =>
            {
                foreach (var item in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = await _vm.BackupService.FindLatestSourceAsync(
                        item.Game, _vm.Config, _vm.WebDavPassword, preference, ct);
                    if (source is null)
                    {
                        noBackup.Add(item.Name);
                        _vm.AppendLog(Localization.T("Msg.SkipNoBackupLog", item.Name));
                    }
                    else if (FileUtil.GetLatestMTime(item.Game.Save) - BackupService.GetBackupMTime(source)
                             > Constants.SyncThresholdSeconds)
                    {
                        localNewer.Add(item.Name);
                    }
                    else
                    {
                        plan.Add((item, source));
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Nothing was restored yet.
            return;
        }

        var skipped = (localNewer.Count > 0 ? Localization.T("Msg.BatchSkipLocalNewer", JoinNames(localNewer)) : "")
                      + (noBackup.Count > 0 ? Localization.T("Msg.BatchSkipNoBackup", JoinNames(noBackup)) : "");
        if (plan.Count == 0)
        {
            MessageBox.Show(this, Localization.T("Msg.BatchNothing") + skipped, Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = Localization.T("Msg.BatchDownloadConfirm", plan.Count, JoinNames(plan.Select(p => p.Item.Name)))
                      + skipped + Localization.T("Msg.BatchContinue");
        if (MessageBox.Show(this, confirm, Localization.T("Msg.BatchDownloadTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var (restored, failed, cancelled) = await _vm.RestoreManyAsync(plan);
        var summary = Localization.T("Msg.BatchDownloadDone", restored)
                      + (failed.Count > 0 ? Localization.T("Msg.BatchDownloadFailed", failed.Count, string.Join("\n", failed)) : "")
                      + (cancelled ? Localization.T("Msg.BatchDownloadCancelled") : "");
        MessageBox.Show(this, summary, Localization.T("Common.Done"), MessageBoxButton.OK,
            failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static string JoinNames(IEnumerable<string> names) =>
        string.Join(Localization.Instance.IsEnglish ? ", " : "、", names);

    private async void OnRestoreSnapshot(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedGames.FirstOrDefault();
        if (item is null)
        {
            MessageBox.Show(this, Localization.T("Msg.SelectGameFirst"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var snapshots = _vm.BackupService.ListRestoreSnapshots(item.Game);
        if (snapshots.Count == 0)
        {
            MessageBox.Show(this, Localization.T("Snap.None", item.Name), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var picker = new SourcePickerDialog(
            snapshots.Select(s => $"{s.Created:yyyy-MM-dd HH:mm:ss}  ·  {FileUtil.FormatSize(s.SizeBytes)}").ToList(),
            Localization.T("Snap.Prompt", item.Name, Constants.MaxRestoreSnapshots))
        {
            Owner = this
        };
        if (picker.ShowDialog() != true || picker.SelectedIndex < 0)
            return;
        var snapshot = snapshots[picker.SelectedIndex];
        if (MessageBox.Show(this, Localization.T("Snap.Confirm", item.Name, $"{snapshot.Created:yyyy-MM-dd HH:mm:ss}"),
                Localization.T("Msg.RestoreConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            if (await _vm.RestoreFromSnapshotAsync(item, snapshot.Path))
                MessageBox.Show(this, Localization.T("Snap.Done", item.Name), Localization.T("Common.Done"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localization.T("Msg.RestoreFailed", ex.Message), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            // Unticking the last one used to bring OneDrive back unasked, even with OneDrive not set up.
            if (game.BackupMethods.Count == 1)
            {
                MessageBox.Show(this, Localization.T("Msg.LastMethod"), Localization.T("Common.Info"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
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

    private async void OnEditGame(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedGames.FirstOrDefault();
        if (item is null)
        {
            MessageBox.Show(this, Localization.T("Msg.SelectGameFirst"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var oldName = item.Game.Name;
        var oldSave = item.Game.Save;
        var dialog = new EditGameDialog(_vm, item.Game)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        _vm.OnGameEdited(oldName, oldSave, item.Game);
        var renamed = oldName != item.Game.Name;
        // No backup may land in either cloud folder while it is being renamed.
        var pauseMonitoring = renamed && _vm.Monitoring.IsMonitoring;
        if (pauseMonitoring)
            _vm.Monitoring.Stop();
        try
        {
            if (renamed)
                await RenameCloudBackupsAsync(oldName, item.Game.Name);
        }
        finally
        {
            item.Refresh();
            _vm.ConfigStore.Save(_vm.Config);
            if (_vm.Config.AutoBackupConfigToCloud)
                _ = _vm.BackupConfigToCloudAsync();
            if (pauseMonitoring)
                _vm.Monitoring.Start();
            else
                _vm.RestartMonitoringIfActive();
            _ = _vm.RefreshGamesAsync();
        }
    }

    /// <summary>Backups are stored in folders named after the game, so a rename has to move them too.</summary>
    private async Task RenameCloudBackupsAsync(string oldName, string newName)
    {
        if (string.IsNullOrEmpty(_vm.Config.CloudRoot) && string.IsNullOrEmpty(_vm.Config.WebDavUrl))
            return;
        if (MessageBox.Show(this, Localization.T("Rename.Ask", oldName, newName), Localization.T("Rename.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            _vm.AppendLog(Localization.T("Log.RenameSkipped", oldName, newName));
            return;
        }
        var failures = await _vm.BackupService.RenameGameStorageAsync(oldName, newName, _vm.Config, _vm.WebDavPassword);
        if (failures.Count == 0)
        {
            _vm.AppendLog(Localization.T("Log.RenameDone", oldName, newName));
            return;
        }
        foreach (var failure in failures)
            _vm.AppendLog(failure);
        MessageBox.Show(this, Localization.T("Rename.SomeFailed", string.Join("\n", failures), oldName),
            Localization.T("Common.Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnDeleteGame(object sender, RoutedEventArgs e)
    {
        var selected = _vm.SelectedGames.ToList();
        if (selected.Count == 0 || !_vm.CanAct)
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
            _vm.ForgetSync(item.Game.Name);
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
                var nameError = GameNames.Validate(ig.Name, Array.Empty<GameConfig>());
                if (nameError != GameNameError.None)
                {
                    _vm.AppendLog(Localization.T("Log.GameSkippedInvalid", ig.Name, GameNames.Describe(nameError, ig.Name)));
                    continue;
                }
                if (_vm.SavePathProblem(ig) is { } pathProblem)
                {
                    _vm.AppendLog(Localization.T("Log.GameSkippedInvalid", ig.Name, pathProblem));
                    continue;
                }
                var fixedSave = FileUtil.FixSavePath(ig.Save);
                if (fixedSave != ig.Save)
                    fixedPaths++;
                ig.Save = fixedSave;
                // Names are compared like folder names: case-insensitively.
                bool SameName(GameConfig g) => string.Equals(g.Name, ig.Name, StringComparison.OrdinalIgnoreCase);
                if (_vm.Config.Games.Any(SameName))
                {
                    if (MessageBox.Show(this, Localization.T("Msg.ImportDuplicateAsk", ig.Name), Localization.T("Msg.ImportDuplicateTitle"),
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        continue;
                    }
                    _vm.Config.Games.RemoveAll(SameName);
                    foreach (var existing in _vm.Games.Where(g => SameName(g.Game)).ToList())
                        _vm.Games.Remove(existing);
                    _vm.ForgetSync(ig.Name);
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
        var localNames = _vm.Config.Games.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newGames = games.Where(g => !localNames.Contains(g.Name)
            && GameNames.Validate(g.Name, Array.Empty<GameConfig>()) == GameNameError.None
            && _vm.SavePathProblem(g) is null).ToList();
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

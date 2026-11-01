using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GameCloudSync.App.ViewModels;
using GameCloudSync.Core;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;
using Microsoft.Win32;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.Views;

public partial class HistoryWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly GameItemViewModel _game;
    private List<HistoryItem> _oneDrive = new();
    private List<HistoryItem> _webDav = new();

    public HistoryWindow(MainViewModel vm, GameItemViewModel game)
    {
        InitializeComponent();
        _vm = vm;
        _game = game;
        DataContext = this;
        HeaderText = Localization.T("Hist.Title", game.Name);
        // Restore and delete work on one source; a selection left behind on the hidden tab used to win silently.
        OneDriveList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count > 0)
                WebDavList.UnselectAll();
        };
        WebDavList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count > 0)
                OneDriveList.UnselectAll();
        };
        Loaded += async (_, _) => await LoadAsync();
    }

    public string HeaderText { get; }
    public int OneDriveCount => _oneDrive.Count;
    public int WebDavCount => _webDav.Count;

    private async Task LoadAsync()
    {
        if (_game.IsOneDrive && !string.IsNullOrEmpty(_vm.Config.CloudRoot))
        {
            var entries = await _vm.GetOneDriveHistoryAsync(_game);
            _oneDrive = entries.Select(e => new HistoryItem(e.Name, e.DateText, FileUtil.FormatSize(e.SizeBytes))).ToList();
        }
        if (_game.IsWebDav && !string.IsNullOrEmpty(_vm.Config.WebDavUrl))
        {
            var entries = await _vm.GetWebDavHistoryAsync(_game);
            _webDav = entries.Select(e => new HistoryItem(e.Name, e.DateText, "")).ToList();
        }
        OneDriveList.ItemsSource = _oneDrive;
        WebDavList.ItemsSource = _webDav;
        OnPropertyChanged(nameof(OneDriveCount));
        OnPropertyChanged(nameof(WebDavCount));
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        // This window stays open beside the main one; two restores of one save must never run at once.
        if (!_vm.CanAct)
        {
            MessageBox.Show(this, Localization.T("Main.Busy"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var webDav = WebDavList.SelectedItems.Count > 0;
        if ((webDav ? WebDavList : OneDriveList).SelectedItem is not HistoryItem item)
        {
            MessageBox.Show(this, Localization.T("Hist.SelectFirst"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // The same question as a download from the main window: both times, and which one is newer.
        var source = webDav
            ? new BackupSource($"WebDAV · {item.Name}", Constants.MethodWebDav, item.Name)
            : new BackupSource($"OneDrive · {item.Name}", Constants.MethodOneDrive,
                Path.Combine(_vm.Config.CloudRoot, _game.Game.Name, item.Name));
        if (MessageBox.Show(this, _vm.RestoreConfirmMessage(_game, source), Localization.T("Msg.RestoreConfirmTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            if (!await _vm.RestoreFromHistoryAsync(_game, item.Name, webDav))
                return;
            MessageBox.Show(this, Localization.T("Hist.RestoreSuccess", _game.Name, item.Name), Localization.T("Common.Success"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Localization.T("Msg.RestoreFailed", ex.Message), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (OneDriveList.SelectedItems.Count > 0)
        {
            var selected = OneDriveList.SelectedItems.OfType<HistoryItem>().ToList();
            if (MessageBox.Show(this, Localization.T("Hist.DeleteConfirmOd", selected.Count), Localization.T("Common.Confirm"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
            foreach (var item in selected)
                await _vm.DeleteOneDriveBackupAsync(_game, item.Name);
        }
        else if (WebDavList.SelectedItems.Count > 0)
        {
            var selected = WebDavList.SelectedItems.OfType<HistoryItem>().ToList();
            if (MessageBox.Show(this, Localization.T("Hist.DeleteConfirmWd", selected.Count), Localization.T("Common.Confirm"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
            foreach (var item in selected)
                await _vm.DeleteWebDavBackupAsync(_game, item.Name);
        }
        else
        {
            MessageBox.Show(this, Localization.T("Hist.SelectDelete"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await LoadAsync();
        // The newest backup may be gone, which changes the game's status in the main window.
        _ = _vm.RefreshGamesAsync();
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Localization.T("Hist.PickDownloadDir") };
        if (dialog.ShowDialog(this) != true)
            return;
        var destDir = dialog.FolderName;
        var count = 0;
        foreach (var item in OneDriveList.SelectedItems.OfType<HistoryItem>())
        {
            await _vm.DownloadOneDriveBackupAsync(_game, item.Name, destDir);
            count++;
        }
        foreach (var item in WebDavList.SelectedItems.OfType<HistoryItem>())
        {
            await _vm.DownloadWebDavBackupAsync(_game, item.Name, destDir);
            count++;
        }
        if (count == 0)
        {
            MessageBox.Show(this, Localization.T("Hist.SelectDownload"), Localization.T("Common.Info"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MessageBox.Show(this, Localization.T("Hist.DownloadDone", count, destDir), Localization.T("Common.Done"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class HistoryItem
{
    public HistoryItem(string name, string dateText, string sizeText)
    {
        Name = name;
        DateText = dateText;
        SizeText = sizeText;
    }

    public string Name { get; }
    public string DateText { get; }
    public string SizeText { get; }
}

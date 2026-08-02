using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GameCloudSync.App.ViewModels;
using GameCloudSync.Core;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;
using Microsoft.Win32;

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
        HeaderText = $"历史备份 - {game.Name}";
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

    private List<HistoryItem> CurrentSelection()
    {
        return OneDriveList.SelectedItems.OfType<HistoryItem>().ToList();
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        var odSelected = OneDriveList.SelectedItems.OfType<HistoryItem>().ToList();
        var wdSelected = WebDavList.SelectedItems.OfType<HistoryItem>().ToList();
        var selected = odSelected.Count > 0 ? odSelected : wdSelected;
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先选中要恢复的备份。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var item = selected[0];
        if (MessageBox.Show(this, $"确定要用备份「{item.Name}」覆盖本地存档吗？\n当前本地存档将被删除！",
                "确认恢复", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            await _vm.RestoreFromHistoryAsync(_game, item.Name, webDav: wdSelected.Count > 0);
            MessageBox.Show(this, $"游戏「{_game.Name}」已恢复到备份版本 {item.Name}", "恢复成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"恢复失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (OneDriveList.SelectedItems.Count > 0)
        {
            var selected = OneDriveList.SelectedItems.OfType<HistoryItem>().ToList();
            if (MessageBox.Show(this, $"确定要删除选中的 {selected.Count} 个 OneDrive 备份吗？", "确认删除",
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
            if (MessageBox.Show(this, $"确定要删除选中的 {selected.Count} 个 WebDAV 备份吗？", "确认删除",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
            foreach (var item in selected)
                await _vm.DeleteWebDavBackupAsync(_game, item.Name);
        }
        else
        {
            MessageBox.Show(this, "请先选中要删除的备份。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await LoadAsync();
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择下载保存目录" };
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
            MessageBox.Show(this, "请先选中要下载的备份（可多选）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MessageBox.Show(this, $"已下载 {count} 个备份到 {destDir}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
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

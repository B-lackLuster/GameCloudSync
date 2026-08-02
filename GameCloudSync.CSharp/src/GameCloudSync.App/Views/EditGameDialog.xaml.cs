using System.Windows;
using GameCloudSync.App.ViewModels;
using GameCloudSync.Core;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;
using Microsoft.Win32;

namespace GameCloudSync.App.Views;

public partial class EditGameDialog : Window
{
    private readonly GameConfig _game;

    public EditGameDialog(MainViewModel vm, GameConfig game)
    {
        InitializeComponent();
        _game = game;
        NameBox.Text = game.Name;
        SaveBox.Text = game.Save;
        MaxBox.Text = game.MaxBackups.ToString();
        OneDriveCheck.IsChecked = game.HasMethod(Constants.MethodOneDrive);
        WebDavCheck.IsChecked = game.HasMethod(Constants.MethodWebDav);
        _ = vm; // validation against live config is done by MainWindow
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择本地存档目录" };
        if (dialog.ShowDialog(this) == true)
            SaveBox.Text = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var save = SaveBox.Text.Trim();
        if (name.Length == 0 || save.Length == 0)
        {
            MessageBox.Show(this, "请填写完整！", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var methods = new List<string>();
        if (OneDriveCheck.IsChecked == true)
            methods.Add(Constants.MethodOneDrive);
        if (WebDavCheck.IsChecked == true)
            methods.Add(Constants.MethodWebDav);
        if (methods.Count == 0)
        {
            MessageBox.Show(this, "请至少选择一种备份方式！", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(MaxBox.Text, out var max) || max < 1 || max > 99)
        {
            MessageBox.Show(this, "备份保留数需为 1-99 之间的整数。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _game.Name = name;
        _game.Save = save;
        _game.BackupMethods = methods;
        _game.MaxBackups = max;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

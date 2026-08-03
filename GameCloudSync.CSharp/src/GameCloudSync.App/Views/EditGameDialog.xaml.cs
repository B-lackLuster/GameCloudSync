using System.Windows;
using GameCloudSync.App.ViewModels;
using GameCloudSync.Core;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;
using Microsoft.Win32;
using Localization = GameCloudSync.Core.Services.Localization;

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
        var dialog = new OpenFolderDialog { Title = Localization.T("Set.PickFolder") };
        if (dialog.ShowDialog(this) == true)
            SaveBox.Text = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var save = SaveBox.Text.Trim();
        if (name.Length == 0 || save.Length == 0)
        {
            MessageBox.Show(this, Localization.T("Edit.FillRequired"), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var methods = new List<string>();
        if (OneDriveCheck.IsChecked == true)
            methods.Add(Constants.MethodOneDrive);
        if (WebDavCheck.IsChecked == true)
            methods.Add(Constants.MethodWebDav);
        if (methods.Count == 0)
        {
            MessageBox.Show(this, Localization.T("Add.NoMethod"), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(MaxBox.Text, out var max) || max < 1 || max > 99)
        {
            MessageBox.Show(this, Localization.T("Edit.InvalidMax"), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
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

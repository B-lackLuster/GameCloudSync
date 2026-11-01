using System.Windows;
using GameCloudSync.Core;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;
using Microsoft.Win32;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.Views;

public partial class AddGameDialog : Window
{
    private readonly AppConfig _config;

    public AddGameDialog(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        OneDriveCheck.IsChecked = !string.IsNullOrEmpty(config.CloudRoot);
        WebDavCheck.IsChecked = !string.IsNullOrEmpty(config.WebDavUrl);
        UpdateHint();
        OneDriveCheck.Checked += (_, _) => UpdateHint();
        OneDriveCheck.Unchecked += (_, _) => UpdateHint();
        WebDavCheck.Checked += (_, _) => UpdateHint();
        WebDavCheck.Unchecked += (_, _) => UpdateHint();
        Loaded += (_, _) => NameBox.Focus();
    }

    public GameConfig? Result { get; private set; }

    private void UpdateHint()
    {
        var hint = "";
        if (!string.IsNullOrEmpty(_config.CloudRoot) && !string.IsNullOrEmpty(_config.WebDavUrl))
        {
            hint = "";
        }
        else if (string.IsNullOrEmpty(_config.CloudRoot) && string.IsNullOrEmpty(_config.WebDavUrl))
        {
            hint = Localization.T("Add.HintNone");
        }
        else if (string.IsNullOrEmpty(_config.CloudRoot))
        {
            hint = Localization.T("Add.HintNoOd");
        }
        else
        {
            hint = Localization.T("Add.HintNoWd");
        }
        HintText.Text = hint;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Localization.T("Set.PickFolder") };
        if (dialog.ShowDialog(this) == true)
            SaveBox.Text = dialog.FolderName;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var save = SaveBox.Text.Trim();
        if (name.Length == 0 || save.Length == 0)
        {
            MessageBox.Show(this, Localization.T("Add.FillRequired"), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var nameError = GameNames.Validate(name, _config.Games);
        if (nameError != GameNameError.None)
        {
            MessageBox.Show(this, GameNames.Describe(nameError, name), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            NameBox.SelectAll();
            return;
        }
        // Backups and restores act on the whole folder, so it has to be a game's own save folder.
        var pathError = SavePathPolicy.Validate(save, name, OneDriveCheck.IsChecked == true ? _config.CloudRoot : "");
        if (pathError != SavePathError.None)
        {
            MessageBox.Show(this, SavePathPolicy.Describe(pathError), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SaveBox.Focus();
            SaveBox.SelectAll();
            return;
        }
        var methods = new List<string>();
        if (OneDriveCheck.IsChecked == true)
        {
            if (string.IsNullOrEmpty(_config.CloudRoot))
            {
                MessageBox.Show(this, Localization.T("Add.OdNotConfigured"), Localization.T("Common.Info"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            methods.Add(Constants.MethodOneDrive);
        }
        if (WebDavCheck.IsChecked == true)
        {
            if (string.IsNullOrEmpty(_config.WebDavUrl))
            {
                MessageBox.Show(this, Localization.T("Add.WdNotConfigured"), Localization.T("Common.Info"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            methods.Add(Constants.MethodWebDav);
        }
        if (methods.Count == 0)
        {
            MessageBox.Show(this, Localization.T("Add.NoMethod"), Localization.T("Common.Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = new GameConfig
        {
            Name = name,
            Save = save,
            Paused = false,
            BackupMethods = methods,
            MaxBackups = _config.DefaultMaxBackups > 0 ? _config.DefaultMaxBackups : Constants.DefaultMaxBackups
        };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

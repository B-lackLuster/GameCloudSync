using System.Windows;
using GameCloudSync.Core;
using GameCloudSync.Core.Models;
using Microsoft.Win32;

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
            hint = "提示：请先在「全局设置」中配置 OneDrive 或 WebDAV";
        }
        else if (string.IsNullOrEmpty(_config.CloudRoot))
        {
            hint = "提示：OneDrive 未配置，请先在全局设置中配置";
        }
        else
        {
            hint = "提示：WebDAV 未配置，请先在全局设置中配置";
        }
        HintText.Text = hint;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择本地存档目录" };
        if (dialog.ShowDialog(this) == true)
            SaveBox.Text = dialog.FolderName;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var save = SaveBox.Text.Trim();
        if (name.Length == 0 || save.Length == 0)
        {
            MessageBox.Show(this, "请填写游戏名称和本地存档路径。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var methods = new List<string>();
        if (OneDriveCheck.IsChecked == true)
        {
            if (string.IsNullOrEmpty(_config.CloudRoot))
            {
                MessageBox.Show(this, "OneDrive 云盘目录未设置，请先在全局设置中配置。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            methods.Add(Constants.MethodOneDrive);
        }
        if (WebDavCheck.IsChecked == true)
        {
            if (string.IsNullOrEmpty(_config.WebDavUrl))
            {
                MessageBox.Show(this, "WebDAV 未配置，请先在全局设置中配置。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            methods.Add(Constants.MethodWebDav);
        }
        if (methods.Count == 0)
        {
            MessageBox.Show(this, "请至少选择一种备份方式！", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
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

using System.Windows;
using GameCloudSync.App.Services;
using GameCloudSync.App.ViewModels;
using GameCloudSync.Core;
using GameCloudSync.Core.Services;
using Microsoft.Win32;

namespace GameCloudSync.App.Views;

public partial class SettingsDialog : Window
{
    private readonly MainViewModel _vm;
    private readonly string[] _sourceLabels = { "OneDrive 优先", "WebDAV 优先", "每次询问我" };
    private readonly string[] _sourceKeys = { Constants.MethodOneDrive, Constants.MethodWebDav, Constants.SourceAsk };

    public SettingsDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        CloudRootBox.Text = vm.Config.CloudRoot;
        UrlBox.Text = vm.Config.WebDavUrl;
        UserBox.Text = vm.Config.WebDavUsername;
        PassBox.Password = vm.WebDavPassword;
        MaxBox.Text = vm.Config.DefaultMaxBackups.ToString();

        DownloadSourceBox.ItemsSource = _sourceLabels;
        DownloadSourceBox.SelectedIndex = Math.Max(0, Array.IndexOf(_sourceKeys, vm.Config.DefaultDownloadSource));
        ConfigSourceBox.ItemsSource = _sourceLabels;
        ConfigSourceBox.SelectedIndex = Math.Max(0, Array.IndexOf(_sourceKeys, vm.Config.DefaultConfigSource));

        AutostartCheck.IsChecked = AutoStartService.IsEnabled();
        BackgroundCheck.IsChecked = vm.Config.BackgroundMode;
        AutoSyncCheck.IsChecked = vm.Config.AutoSyncOnStartup;
        AutoConfigCheck.IsChecked = vm.Config.AutoBackupConfigToCloud;
        DebugCheck.IsChecked = vm.Config.DebugWebDavLog;
    }

    private void OnBrowseCloudRoot(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 OneDrive 云盘根目录" };
        if (dialog.ShowDialog(this) == true)
            CloudRootBox.Text = dialog.FolderName;
    }

    private async void OnTestWebDav(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            TestResult.Text = "请先输入服务器地址";
            return;
        }
        TestButton.IsEnabled = false;
        TestResult.Text = "测试中...";
        try
        {
            using var wc = new WebDavClient(url, UserBox.Text.Trim(), PassBox.Password);
            var ok = await wc.TestConnectionAsync();
            TestResult.Text = ok ? "✓ 连接成功" : "✗ 连接失败，请检查地址和账号密码";
            TestResult.Foreground = ok
                ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
                : (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
        catch (Exception ex)
        {
            TestResult.Text = $"✗ 连接失败: {ex.Message}";
            TestResult.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var newCloud = CloudRootBox.Text.Trim();
        var newUrl = UrlBox.Text.Trim();
        var newUser = UserBox.Text.Trim();
        var newPass = PassBox.Password;
        if (!int.TryParse(MaxBox.Text, out var max) || max < 1 || max > 99)
        {
            MessageBox.Show(this, "默认备份数量需为 1-99 之间的整数。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (newCloud != _vm.Config.CloudRoot)
        {
            _vm.Config.CloudRoot = newCloud;
        }
        if (newUrl != _vm.Config.WebDavUrl || newUser != _vm.Config.WebDavUsername || newPass != _vm.WebDavPassword)
        {
            _vm.Config.WebDavUrl = newUrl;
            _vm.Config.WebDavUsername = newUser;
            if (newUrl.Length > 0 && newUser.Length > 0 && newPass.Length > 0)
            {
                if (!_vm.SecretStore.SavePassword(newPass))
                    _vm.AppendLog("⚠ WebDAV 密码无法写入系统加密存储，将仅保存在内存（重启后丢失）。");
            }
            else
            {
                _vm.SecretStore.ClearPassword();
            }
            _vm.SetWebDavPassword(newPass);
        }

        _vm.Config.DefaultMaxBackups = max;
        _vm.Config.DefaultDownloadSource = _sourceKeys[Math.Max(0, DownloadSourceBox.SelectedIndex)];
        _vm.Config.DefaultConfigSource = _sourceKeys[Math.Max(0, ConfigSourceBox.SelectedIndex)];
        _vm.Config.BackgroundMode = BackgroundCheck.IsChecked == true;
        _vm.Config.AutoSyncOnStartup = AutoSyncCheck.IsChecked == true;
        _vm.Config.AutoBackupConfigToCloud = AutoConfigCheck.IsChecked == true;
        _vm.Config.DebugWebDavLog = DebugCheck.IsChecked == true;

        var autostart = AutostartCheck.IsChecked == true;
        if (autostart != AutoStartService.IsEnabled())
        {
            try
            {
                AutoStartService.SetEnabled(autostart, Environment.ProcessPath ?? AppContext.BaseDirectory);
                _vm.AppendLog(autostart ? "已添加开机自启动" : "已取消开机自启动");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"无法修改注册表：{ex.Message}", "自启动设置失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        _vm.ApplyConfigChange(_ => { });
        _vm.AppendLog("⚙ 全局设置已保存。");
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.IO;
using GameCloudSync.Core;
using GameCloudSync.App.Services;
using GameCloudSync.App.ViewModels;
using GameCloudSync.App.Views;
using GameCloudSync.Core.Services;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App;

public partial class App : Application
{
    private bool _shuttingDown;
    private SingleInstance? _singleInstance;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private MonitoringService? _monitoring;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("Dispatcher", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("Task", args.Exception);
            args.SetObserved();
        };

        // WPF's font cache requires the windir environment variable; some
        // shells (and CI/sandbox sessions) launch processes without it, which
        // makes WPF throw UriFormatException during font initialization.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("windir")) &&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SystemRoot")))
        {
            Environment.SetEnvironmentVariable("windir", Environment.GetEnvironmentVariable("SystemRoot"));
        }

        var appDir = AppContext.BaseDirectory;
        _singleInstance = new SingleInstance();
        if (!_singleInstance.TryAcquire(appDir, Constants.WakeupFileName))
        {
            Shutdown();
            return;
        }

        // Services
        var configStore = new ConfigStore(appDir);
        Localization.Instance.Language = configStore.Load().Language;
        var secretStore = new SecretStore(appDir);
        MigrateLegacyPassword(appDir, configStore, secretStore);

        var ui = SynchronizationContext.Current!;
        var backupService = new BackupService((url, password) =>
        {
            var cfg = configStore.Load();
            var vm = _mainWindow?.ViewModel;
            return new WebDavClient(url, cfg.WebDavUsername, password,
                log: vm is null ? null : m => vm.AppendLog(m),
                debug: cfg.DebugWebDavLog && vm is not null ? m => vm.AppendLog(m) : null);
        });
        var cloudConfigService = new CloudConfigService((url, password) =>
        {
            var cfg = configStore.Load();
            var vm = _mainWindow?.ViewModel;
            return new WebDavClient(url, cfg.WebDavUsername, password,
                log: vm is null ? null : m => vm.AppendLog(m),
                debug: cfg.DebugWebDavLog && vm is not null ? m => vm.AppendLog(m) : null);
        });
        _monitoring = new MonitoringService(
            backupService,
            () => configStore.Load(),
            () => secretStore.LoadPassword() ?? "",
            m => ui.Post(_ => _mainWindow?.ViewModel.AppendLog(m), null));

        var viewModel = new MainViewModel(
            configStore, secretStore, backupService, cloudConfigService, _monitoring, ui);

        // Tray
        _tray = new TrayIconService();
        _tray.ShowWindowRequested += () => ShowMainWindow();
        _tray.StartMonitoringRequested += () =>
        {
            _monitoring.Start();
            UpdateTray();
        };
        _tray.StopMonitoringRequested += () =>
        {
            _monitoring.Stop();
            UpdateTray();
        };
        _tray.QuitRequested += () => ShutdownApp();

        viewModel.ShowWindowRequested += () => ShowMainWindow();
        _monitoring.StateChanged += UpdateTray;

        _mainWindow = new MainWindow(viewModel, _tray);
        MainWindow = _mainWindow;
        _mainWindow.Closed += (_, _) => ShutdownApp();
        _mainWindow.Show();

        viewModel.Load();
        viewModel.AutoStart();
        viewModel.CheckWakeupFile();
        UpdateTray();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;
        Dispatcher.Invoke(() =>
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        });
    }

    private void UpdateTray()
    {
        if (_tray is null || _monitoring is null)
            return;
        _tray.SetMonitoring(_monitoring.IsMonitoring, _monitoring.MonitoredCount);
    }

    private void ShutdownApp()
    {
        if (_shuttingDown)
            return;
        _shuttingDown = true;
        try
        {
            _monitoring?.Dispose();
        }
        catch
        {
            // ignore
        }
        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // ignore
        }
        _mainWindow?.ForceClose();
        _singleInstance?.Dispose();
        Shutdown();
    }

    /// <summary>
    /// One-time migration: a plain-text webdav_password in config.json is moved
    /// into the DPAPI secret store and removed from the file (like the Python app).
    /// </summary>
    private static void MigrateLegacyPassword(string appDir, ConfigStore configStore, SecretStore secretStore)
    {
        var configPath = configStore.ConfigPath;
        if (!File.Exists(configPath) || secretStore.HasStoredPassword())
            return;
        try
        {
            var text = File.ReadAllText(configPath);
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null || node["webdav_password"] is not { } pwNode)
                return;
            var legacy = pwNode.GetValue<string>();
            if (!string.IsNullOrEmpty(legacy))
                secretStore.SavePassword(legacy);
            node.Remove("webdav_password");
            File.WriteAllText(configPath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), new UTF8Encoding(false));
        }
        catch
        {
            // Non-fatal; the user can re-enter the password.
        }
    }

    private void WriteCrashLog(string source, Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n",
                Encoding.UTF8);
        }
        catch
        {
            // last-resort: never throw from an exception handler
        }
    }
}

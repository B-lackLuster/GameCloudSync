using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.IO;
using GameCloudSync.Core;
using GameCloudSync.App.Services;
using GameCloudSync.App.ViewModels;
using GameCloudSync.App.Views;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App;

public partial class App : Application
{
    private bool _shuttingDown;
    private SingleInstance? _singleInstance;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private MonitoringService? _monitoring;
    private FileLog? _fileLog;
    private string? _dataDir;

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

        // Portable by default; when the program folder can't be written (Program Files) everything goes to %APPDATA%.
        var exeDir = AppContext.BaseDirectory;
        var appDir = DataDirectory.Resolve(exeDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Constants.AppFolderName));
        _dataDir = appDir;
        var afterUpdate = e.Args.Any(arg => string.Equals(arg, Constants.UpdatedArg, StringComparison.OrdinalIgnoreCase));
        _singleInstance = new SingleInstance();
        // After an update the previous version may still be closing; it has let go of the lock by then, normally.
        if (!_singleInstance.TryAcquire(appDir, Constants.WakeupFileName,
                afterUpdate ? TimeSpan.FromSeconds(15) : TimeSpan.Zero))
        {
            Shutdown();
            return;
        }

        // Only the instance that keeps running writes the log and looks after the autostart entry.
        var startMinimized = AutostartCommand.StartsMinimized(e.Args);
        _fileLog = new FileLog(Path.Combine(appDir, Constants.LogDirName, Constants.LogFileName));
        _fileLog.Write($"GameCloudSync started ({(startMinimized ? "minimized to the tray" : "main window")})");
        var movedToAppData = !string.Equals(appDir, exeDir, StringComparison.OrdinalIgnoreCase);
        if (movedToAppData)
            _fileLog.Write($"The program folder {exeDir} can't be written; using {appDir}");
        if (Environment.ProcessPath is { } exePath)
            AutoStartService.UpgradeLegacyEntry(exePath);

        // Services
        var configStore = new ConfigStore(appDir);
        try
        {
            Localization.Instance.Language = configStore.Load().Language;
        }
        catch (Exception ex)
        {
            // Carrying on with an empty config would overwrite the real one on the next save.
            _fileLog.Write($"Config could not be read: {ex.Message}");
            MessageBox.Show(Localization.T("Cfg.ReadFailed", configStore.ConfigPath, ex.Message), "GameCloudSync",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        var secretStore = new SecretStore(appDir);
        MigrateLegacyPassword(appDir, configStore, secretStore);

        var ui = SynchronizationContext.Current!;
        // Background work logs from any thread: the file gets the line at once, the activity list on the UI thread.
        void Log(string message)
        {
            _fileLog?.Write(message);
            ui.Post(_ => _mainWindow?.ViewModel.ShowLog(message), null);
        }
        var backupService = new BackupService((url, password) =>
        {
            var cfg = configStore.Load();
            var vm = _mainWindow?.ViewModel;
            return new WebDavClient(url, cfg.WebDavUsername, password,
                log: vm is null ? null : m => vm.AppendLog(m),
                debug: cfg.DebugWebDavLog && vm is not null ? m => vm.AppendLog(m) : null);
        }, snapshotRoot: Path.Combine(appDir, Constants.RestoreSnapshotDir), baselines: new SyncBaselineStore(appDir));
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
            Log);

        var updateService = new UpdateService(typeof(App).Assembly.GetName().Version ?? new Version(0, 0));
        var viewModel = new MainViewModel(
            configStore, secretStore, backupService, cloudConfigService, _monitoring, updateService, ui, _fileLog);

        // Tray
        _tray = new TrayIconService();
        _tray.ShowWindowRequested += () => ShowMainWindow();
        _tray.ToggleMonitoringRequested += () =>
        {
            // A restore pauses monitoring while it replaces the save, and starts it again itself.
            if (_mainWindow?.ViewModel.CanAct == false)
            {
                _tray.ShowNotification("GameCloudSync", Localization.T("Main.Busy"), isError: false);
                return;
            }
            if (_monitoring.IsMonitoring)
                _monitoring.Stop();
            else
                _monitoring.Start();
            UpdateTray();
        };
        _tray.QuitRequested += () => ShutdownApp();

        viewModel.ShowWindowRequested += () => ShowMainWindow();
        // A backup that fails in the background is otherwise invisible while the window is hidden in the tray.
        viewModel.FailureNotice += (title, message) => _tray?.ShowNotification(title, message, isError: true);
        viewModel.Dashboard.ProblemCountChanged += UpdateTray;
        // Monitoring can be restarted from background threads (startup auto sync); the tray icon is UI.
        _monitoring.StateChanged += () => Dispatcher.InvokeAsync(UpdateTray);
        // With the window open the sidebar shows the new version; in the tray only a notification can.
        viewModel.Update.UpdateFound += release =>
        {
            if (_mainWindow?.IsVisible != true)
                _tray?.ShowNotification(Localization.T("Notify.UpdateTitle", release.VersionText),
                    Localization.T("Notify.UpdateBody"), isError: false, onClick: () =>
                    {
                        ShowMainWindow();
                        _mainWindow?.ShowUpdateDialog();
                    });
        };
        // Once the update dialog has closed.
        viewModel.Update.RestartRequested += exePath => Dispatcher.BeginInvoke(() => RestartIntoUpdate(exePath));

        _mainWindow = new MainWindow(viewModel, _tray);
        MainWindow = _mainWindow;
        _mainWindow.Closed += (_, _) => ShutdownApp();
        // A login start goes straight to the tray: nothing pops up until the user opens the window.
        if (!startMinimized)
            _mainWindow.Show();

        viewModel.Load();
        if (movedToAppData)
            viewModel.ShowLog(Localization.T("Log.DataDirFallback", exeDir, appDir));
        if (configStore.CorruptConfigPath is { } corruptPath)
        {
            var notice = Localization.T(configStore.RecoveredFromBackup ? "Cfg.RecoveredFromBak" : "Cfg.CorruptReset",
                corruptPath);
            viewModel.AppendLog(notice);
            // This one the user has to see, even after a minimized start.
            if (startMinimized)
                ShowMainWindow();
            MessageBox.Show(_mainWindow, notice, Localization.T("Common.Warning"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        viewModel.AutoStart();
        viewModel.CheckWakeupFile();
        UpdateTray();
        if (afterUpdate)
        {
            viewModel.AppendLog(Localization.T("Log.Updated", viewModel.Update.CurrentVersionText));
            _tray.ShowNotification(Localization.T("Notify.UpdatedTitle", viewModel.Update.CurrentVersionText),
                Localization.T("Notify.UpdatedBody"), isError: false);
        }
        if (Environment.ProcessPath is { } runningExe)
            _ = CleanUpAfterUpdateAsync(runningExe);
        _ = viewModel.Update.RunAutomaticChecksAsync();
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
        _tray.SetState(_monitoring.IsMonitoring, _monitoring.MonitoredCount,
            _mainWindow?.ViewModel.Dashboard.ProblemCount ?? 0);
    }

    private void ShutdownApp()
    {
        if (CloseDown())
            Shutdown();
    }

    /// <summary>Stops monitoring, removes the tray icon, closes the window and lets go of the single-instance lock.</summary>
    /// <returns>False when the app is already shutting down.</returns>
    private bool CloseDown()
    {
        if (_shuttingDown)
            return false;
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
        _fileLog?.Write("GameCloudSync exited");
        _fileLog?.Dispose();
        return true;
    }

    /// <summary>The new version has taken the exe's place: close down as when quitting, then start it.</summary>
    private void RestartIntoUpdate(string exePath)
    {
        _fileLog?.Write($"Restarting into the new version at {exePath}");
        // Closing down releases the single-instance lock, which the new version takes over as it starts.
        if (!CloseDown())
            return;
        try
        {
            StartProgram(exePath, Constants.UpdatedArg);
        }
        catch (Exception ex)
        {
            // Without a running copy nothing would be backed up: put the previous version back and start that.
            var restarted = SelfUpdate.Revert(exePath) && TryStartProgram(exePath);
            _fileLog?.Write($"The new version could not be started ({ex.Message}); " +
                            (restarted ? "the previous version was put back" : "nothing could be started"));
            MessageBox.Show(Localization.T(restarted ? "Update.RestartFailed" : "Update.RestartFailedNoRevert", ex.Message),
                "GameCloudSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Shutdown();
    }

    private static void StartProgram(string exePath, params string[] args)
    {
        var start = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var _ = Process.Start(start);
    }

    private static bool TryStartProgram(string exePath)
    {
        try
        {
            StartProgram(exePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the version an update replaced (it may take a moment to exit after starting this one) and any
    /// download an earlier run didn't finish.
    /// </summary>
    private async Task CleanUpAfterUpdateAsync(string exePath)
    {
        try
        {
            var old = SelfUpdate.OldPath(exePath);
            if (await SelfUpdate.CleanUpAsync(exePath, TimeSpan.FromMinutes(1)))
                _fileLog?.Write($"Deleted the previous version {old}");
            else if (File.Exists(old))
                _fileLog?.Write($"The previous version {old} could not be deleted yet; the next start tries again");
        }
        catch (Exception ex)
        {
            _fileLog?.Write($"Cleaning up after an update failed: {ex.Message}");
        }
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
        _fileLog?.Write($"[{source}] unhandled exception: {ex}");
        try
        {
            var path = Path.Combine(_dataDir ?? AppContext.BaseDirectory, "crash.log");
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

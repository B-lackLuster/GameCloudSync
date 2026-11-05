using System.IO;
using GameCloudSync.Core;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.ViewModels;

public enum UpdateStage { Downloading, Verifying, WaitingForBackups, Installing }

/// <summary>How far an update has got; while downloading, <see cref="Received"/> of <see cref="Total"/> bytes.</summary>
public sealed record UpdateProgress(UpdateStage Stage, long Received = 0, long Total = 0);

/// <summary>
/// Looks for a newer release on GitHub (shortly after startup, then every few hours) and installs one when the user
/// asks: download and check it, wait for running backups, put it in place of the exe and restart.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(Constants.UpdateCheckIntervalHours);
    // How often the automatic check looks at the clock: the PC may sleep through a whole interval.
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromMinutes(30);

    private readonly MainViewModel _main;
    private readonly UpdateService _service;
    private readonly HashSet<Version> _announced = new();
    private ReleaseInfo? _available;
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private bool _checking;

    public UpdateViewModel(MainViewModel main, UpdateService service, string? exePath)
    {
        _main = main;
        _service = service;
        ExePath = exePath;
        Localization.Instance.LanguageChanged += () => OnPropertyChanged(nameof(SidebarText));
    }

    /// <summary>The running exe, which an update replaces.</summary>
    public string? ExePath { get; }

    public string CurrentVersionText => UpdateService.Display(_service.CurrentVersion);

    /// <summary>The newer release the last check found, skipped or not.</summary>
    public ReleaseInfo? Available
    {
        get => _available;
        private set
        {
            if (SetProperty(ref _available, value))
            {
                OnPropertyChanged(nameof(HasUpdate));
                OnPropertyChanged(nameof(SidebarText));
            }
        }
    }

    /// <summary>A newer release the user hasn't skipped; the sidebar offers it.</summary>
    public bool HasUpdate => Available is { } release && !IsSkipped(release);

    public string SidebarText => Available is { } release ? Localization.T("Update.SidebarButton", release.VersionText) : "";

    /// <summary>Raised when an automatic check finds a release the user hasn't been told about yet.</summary>
    public event Action<ReleaseInfo>? UpdateFound;

    /// <summary>Raised with the exe path once the new version is in its place; the app then restarts into it.</summary>
    public event Action<string>? RestartRequested;

    /// <summary>Asks GitHub for the latest release; returns it when it is newer than this version, otherwise null.</summary>
    /// <exception cref="UpdateException">GitHub couldn't be reached, or its answer couldn't be read.</exception>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        _checking = true;
        try
        {
            var latest = await _service.GetLatestReleaseAsync(ct);
            _lastCheckUtc = DateTime.UtcNow;
            Available = UpdateService.IsNewer(latest.Version, _service.CurrentVersion) ? latest : null;
            return Available;
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Checks shortly after startup and then every few hours, for as long as the settings allow it.</summary>
    public async Task RunAutomaticChecksAsync()
    {
        await Task.Delay(FirstCheckDelay);
        while (true)
        {
            if (_main.Config.AutoCheckUpdates && !_checking && DateTime.UtcNow - _lastCheckUtc >= CheckInterval)
                await CheckQuietlyAsync();
            await Task.Delay(RecheckDelay);
        }
    }

    private async Task CheckQuietlyAsync()
    {
        try
        {
            if (await CheckAsync() is { } release && !IsSkipped(release) && _announced.Add(release.Version))
            {
                _main.AppendLog(Localization.T("Log.UpdateAvailable", release.VersionText, CurrentVersionText));
                UpdateFound?.Invoke(release);
            }
        }
        catch (Exception ex)
        {
            // Offline, GitHub out of reach or rate limited: nothing the user has to act on, and the next round
            // (half an hour on, as no check has succeeded) tries again.
            _main.WriteFileLog(Localization.T("Log.UpdateCheckFailed", ex.Message));
        }
    }

    /// <summary>Stops offering this release; a newer one is offered again.</summary>
    public void Skip(ReleaseInfo release)
    {
        try
        {
            _main.ApplyConfigChange(config => config.SkippedUpdateVersion = UpdateService.Format(release.Version));
            _main.AppendLog(Localization.T("Log.UpdateSkipped", release.VersionText));
        }
        catch (Exception ex)
        {
            _main.AppendLog(Localization.T("Log.TaskError", ex.Message));
        }
        OnPropertyChanged(nameof(HasUpdate));
    }

    private bool IsSkipped(ReleaseInfo release) =>
        UpdateService.ParseVersion(_main.Config.SkippedUpdateVersion) is { } skipped && skipped == release.Version;

    /// <summary>Why this copy can't replace itself with the release (then it is downloaded by hand), or null.</summary>
    public string? CannotInstallReason(ReleaseInfo release)
    {
        if (release.Asset is null)
            return Localization.T("Update.NoAsset", Constants.UpdateAssetName);
        if (ExePath is null || !IsSingleFileBuild)
            return Localization.T("Update.NotPortable");
        var folder = Path.GetDirectoryName(ExePath) ?? "";
        return DataDirectory.IsWritable(folder) ? null : Localization.T("Update.FolderReadOnly", folder);
    }

    // A release is one exe; built from source, the program is a .dll beside a small launcher exe.
    private static bool IsSingleFileBuild =>
        !File.Exists(Path.Combine(AppContext.BaseDirectory, typeof(UpdateViewModel).Assembly.GetName().Name + ".dll"));

    /// <summary>
    /// Downloads and checks the release, waits for backups and restores that are under way, puts the new exe in place
    /// of the running one and asks for the restart. Cancelling works until the exe is replaced; on failure or cancel
    /// the program is left as it was.
    /// </summary>
    public async Task InstallAsync(ReleaseInfo release, IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        if (CannotInstallReason(release) is { } reason)
            throw new UpdateException(reason);
        var exe = ExePath!;
        var asset = release.Asset!;
        var download = SelfUpdate.DownloadPath(exe);
        try
        {
            progress.Report(new UpdateProgress(UpdateStage.Downloading, 0, asset.Size));
            // Hashing and writing 70 MB stays off the UI thread.
            var bytes = new Forward<long>(received =>
                progress.Report(new UpdateProgress(UpdateStage.Downloading, received, asset.Size)));
            await Task.Run(() => _service.DownloadAsync(asset, download, bytes, ct), ct);
            progress.Report(new UpdateProgress(UpdateStage.Verifying));
            await Task.Run(() => UpdateService.VerifyProgram(download, release.Version), ct);
            if (_main.HasBackgroundWork)
            {
                progress.Report(new UpdateProgress(UpdateStage.WaitingForBackups));
                await WaitForBackgroundWorkAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            progress.Report(new UpdateProgress(UpdateStage.Installing));
            try
            {
                await Task.Run(() => SelfUpdate.Install(exe, download));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException(Localization.T("Update.ErrReplace", ex.Message), ex);
            }
        }
        catch
        {
            TryDelete(download);
            throw;
        }
        _main.AppendLog(Localization.T("Log.UpdateInstalling", release.VersionText));
        RestartRequested?.Invoke(exe);
    }

    // Quitting in the middle of a backup or restore would leave the newest save without a backup, or half restored.
    private async Task WaitForBackgroundWorkAsync(CancellationToken ct)
    {
        // Idle twice in a row: a backup whose quiet period just ended is between its timer and the queue for a moment.
        for (var idle = 0; idle < 2;)
        {
            await Task.Delay(500, ct);
            idle = _main.HasBackgroundWork ? 0 : idle + 1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Removed at the next start.
        }
    }

    // Unlike Progress<T>, reports at once on the calling thread; the dialog's own Progress<T> brings them to the UI.
    private sealed class Forward<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

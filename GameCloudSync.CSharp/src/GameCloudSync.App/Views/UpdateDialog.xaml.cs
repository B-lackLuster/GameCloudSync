using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using GameCloudSync.App.ViewModels;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.Views;

/// <summary>A newer release with its notes; installs it in one go, or skips it.</summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateViewModel _updates;
    private readonly ReleaseInfo _release;
    private readonly string? _cannotInstall;
    private CancellationTokenSource? _work;
    private bool _closed;

    public UpdateDialog(UpdateViewModel updates, ReleaseInfo release)
    {
        InitializeComponent();
        _updates = updates;
        _release = release;

        HeadlineText.Text = Localization.T("Update.Headline", release.VersionText);
        var published = release.PublishedAt?.LocalDateTime.ToString("yyyy-MM-dd") ?? "-";
        DetailsText.Text = release.Asset is { Size: > 0 } asset
            ? Localization.T("Update.Details", updates.CurrentVersionText, published, FileUtil.FormatSize(asset.Size))
            : Localization.T("Update.DetailsNoSize", updates.CurrentVersionText, published);
        var notes = ReleaseNotes.Parse(release.Notes);
        NotesList.ItemsSource = notes.Count > 0 ? notes : [new NoteLine(NoteKind.Text, Localization.T("Update.NoNotes"))];

        _cannotInstall = updates.CannotInstallReason(release);
        if (_cannotInstall is null)
        {
            NoteText.Text = Localization.T("Update.RestartNote");
        }
        else
        {
            // The new version is still a click away, only by hand.
            NoteText.Text = _cannotInstall;
            NoteText.Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xCB, 0x8D));
            UpdateButton.Content = Localization.T("Btn.OpenDownloadPage");
        }

        // Closing the dialog gives up a running download; once the exe has been replaced the restart goes ahead.
        Closing += (_, _) => _work?.Cancel();
        Closed += (_, _) => _closed = true;
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_cannotInstall is not null)
        {
            FileUtil.TryOpenBrowser(_release.PageUrl);
            return;
        }
        if (_work is not null)
            return;
        _work = new CancellationTokenSource();
        SetWorking(true);
        var restarting = false;
        try
        {
            await _updates.InstallAsync(_release, new Progress<UpdateProgress>(ShowProgress), _work.Token);
            restarting = true;
            if (!_closed)
                DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            ShowOutcome(Localization.T("Update.Cancelled"), isError: false);
        }
        catch (Exception ex)
        {
            ShowOutcome(Localization.T("Update.Failed", ex.Message), isError: true);
        }
        finally
        {
            _work.Dispose();
            _work = null;
            if (!restarting)
                SetWorking(false);
        }
    }

    private void SetWorking(bool working)
    {
        UpdateButton.IsEnabled = !working;
        SkipButton.IsEnabled = !working;
        LaterButton.Content = Localization.T(working ? "Common.Cancel" : "Btn.UpdateLater");
        if (!working)
            return;
        ProgressPanel.Visibility = Visibility.Visible;
        DownloadBar.Visibility = Visibility.Visible;
        DownloadBar.Value = 0;
        ProgressText.Text = "";
        ProgressText.Foreground = (Brush)FindResource("TextSecBrush");
    }

    private void ShowProgress(UpdateProgress progress)
    {
        if (_work is null)
            return;
        switch (progress.Stage)
        {
            case UpdateStage.Downloading:
                DownloadBar.Value = progress.Total > 0 ? progress.Received * 100.0 / progress.Total : 0;
                ProgressText.Text = progress.Total > 0
                    ? Localization.T("Update.Downloading", FileUtil.FormatSize(progress.Received), FileUtil.FormatSize(progress.Total))
                    : Localization.T("Update.DownloadingNoSize", FileUtil.FormatSize(progress.Received));
                break;
            case UpdateStage.Verifying:
                DownloadBar.Value = 100;
                ProgressText.Text = Localization.T("Update.Verifying");
                break;
            case UpdateStage.WaitingForBackups:
                ProgressText.Text = Localization.T("Update.WaitingForBackups");
                break;
            case UpdateStage.Installing:
                ProgressText.Text = Localization.T("Update.Installing");
                break;
        }
    }

    private void ShowOutcome(string text, bool isError)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        DownloadBar.Visibility = Visibility.Collapsed;
        ProgressText.Text = text;
        ProgressText.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "TextSecBrush");
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _updates.Skip(_release);
        DialogResult = false;
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnViewOnGitHub(object sender, RoutedEventArgs e)
    {
        FileUtil.TryOpenBrowser(_release.PageUrl);
    }
}

using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.Services;

public sealed class TrayIconService : IDisposable
{
    private enum TrayState { Idle, Monitoring, Problem }

    // One icon per state, created on first use. Building a new one at every change would leak a GDI handle each time.
    private static readonly Dictionary<TrayState, Icon> Icons = new();

    // The balloon's own limits: 63 characters for the title, 255 for the text.
    private const int MaxBalloonTitle = 63;
    private const int MaxBalloonText = 250;

    private readonly TaskbarIcon _tray;
    private MenuItem? _monitorItem;
    private bool _monitoring;
    // What clicking the notification on show does; each notification brings its own.
    private Action? _notificationClick;

    public event Action? ShowWindowRequested;
    /// <summary>The monitoring switch was clicked: start when stopped, stop when running.</summary>
    public event Action? ToggleMonitoringRequested;
    public event Action? QuitRequested;

    public TrayIconService()
    {
        _tray = new TaskbarIcon();
        _tray.Icon = IconFor(TrayState.Idle);
        _tray.ToolTipText = Localization.T("Tray.TooltipIdle");
        _tray.TrayMouseDoubleClick += (_, _) => ShowWindowRequested?.Invoke();
        _tray.TrayBalloonTipClicked += (_, _) =>
        {
            var click = _notificationClick;
            _notificationClick = null;
            click?.Invoke();
        };
        RebuildMenu();
        Localization.Instance.LanguageChanged += RebuildMenu;
    }

    private void RebuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(Localization.T("Tray.ShowMain"), () => ShowWindowRequested?.Invoke()));
        // One switch whose tick shows the state, instead of "start" and "stop" side by side with one of them doing nothing.
        _monitorItem = MenuItem(Localization.T("Tray.Monitor"), () => ToggleMonitoringRequested?.Invoke());
        _monitorItem.IsChecked = _monitoring;
        menu.Items.Add(_monitorItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Localization.T("Tray.Quit"), () => QuitRequested?.Invoke()));
        _tray.ContextMenu = menu;
    }

    /// <param name="problems">Games whose backups need attention; they turn the icon red.</param>
    public void SetState(bool monitoring, int count, int problems)
    {
        _monitoring = monitoring;
        if (_monitorItem is not null)
            _monitorItem.IsChecked = monitoring;
        var state = problems > 0 ? TrayState.Problem : monitoring ? TrayState.Monitoring : TrayState.Idle;
        _tray.Icon = IconFor(state);
        _tray.ToolTipText = problems > 0
            ? Localization.T("Tray.TooltipProblems", problems)
            : monitoring
                ? Localization.T("Tray.TooltipMonitoring", count)
                : Localization.T("Tray.TooltipIdle");
    }

    /// <summary>Pops a Windows notification from the tray icon; <paramref name="onClick"/> runs when it is clicked.</summary>
    public void ShowNotification(string title, string message, bool isError, Action? onClick = null)
    {
        _notificationClick = onClick;
        _tray.ShowBalloonTip(Truncate(title, MaxBalloonTitle), Truncate(message, MaxBalloonText),
            isError ? BalloonIcon.Error : BalloonIcon.Info);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static Icon IconFor(TrayState state)
    {
        if (!Icons.TryGetValue(state, out var icon))
            Icons[state] = icon = DrawIcon(state switch
            {
                TrayState.Monitoring => Color.FromArgb(50, 205, 50),
                TrayState.Problem => Color.FromArgb(239, 83, 80),
                _ => Color.FromArgb(169, 169, 169)
            });
        return icon;
    }

    private static Icon DrawIcon(Color color)
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            using var pen = new Pen(Color.Black, 2f);
            g.FillEllipse(brush, 8, 8, 48, 48);
            g.DrawEllipse(pen, 8, 8, 48, 48);
            using var shine = new SolidBrush(Color.FromArgb(80, 255, 255, 255));
            g.FillEllipse(shine, 20, 15, 24, 15);
        }
        var handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _tray.Dispose();
    }
}

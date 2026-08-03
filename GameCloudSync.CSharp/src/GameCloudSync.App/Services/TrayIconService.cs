using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Localization = GameCloudSync.Core.Services.Localization;

namespace GameCloudSync.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _tray;

    public event Action? ShowWindowRequested;
    public event Action? StartMonitoringRequested;
    public event Action? StopMonitoringRequested;
    public event Action? QuitRequested;

    public TrayIconService()
    {
        _tray = new TaskbarIcon();
        _tray.Icon = LoadIcon(active: false);
        _tray.ToolTipText = Localization.T("Tray.TooltipIdle");
        _tray.TrayMouseDoubleClick += (_, _) => ShowWindowRequested?.Invoke();
        RebuildMenu();
        Localization.Instance.LanguageChanged += RebuildMenu;
    }

    private void RebuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(Localization.T("Tray.ShowMain"), () => ShowWindowRequested?.Invoke()));
        menu.Items.Add(MenuItem(Localization.T("Tray.StartMonitor"), () => StartMonitoringRequested?.Invoke()));
        menu.Items.Add(MenuItem(Localization.T("Tray.StopMonitor"), () => StopMonitoringRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Localization.T("Tray.Quit"), () => QuitRequested?.Invoke()));
        _tray.ContextMenu = menu;
    }

    public void SetMonitoring(bool monitoring, int count)
    {
        _tray.Icon = LoadIcon(monitoring);
        _tray.ToolTipText = monitoring
            ? Localization.T("Tray.TooltipMonitoring", count)
            : Localization.T("Tray.TooltipIdle");
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static Icon LoadIcon(bool active)
    {
        var color = active ? Color.FromArgb(50, 205, 50) : Color.FromArgb(169, 169, 169);
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

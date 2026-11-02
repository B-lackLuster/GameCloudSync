using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace GameCloudSync.App.Services;

/// <summary>
/// Keeps a window that draws its own title bar (WindowChrome) within the work area of its monitor when it is
/// maximized, like a normal window, instead of reaching past the edges of the screen.
/// </summary>
public static class CustomFrame
{
    private const int WmWindowPosChanged = 0x0047;
    private const int MonitorDefaultToNearest = 2;

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is not HwndSource source)
                return;
            var chrome = WindowChrome.GetWindowChrome(window);
            var captionHeight = chrome?.CaptionHeight ?? 0;
            source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                // Whatever moved or sized the window, its state is final here; a minimized one keeps what it had.
                if (msg == WmWindowPosChanged && !IsIconic(hwnd))
                    FitToWorkArea(window, hwnd, chrome, captionHeight);
                return IntPtr.Zero;
            });
        };
    }

    // Windows makes a maximized window with a system title bar larger than the work area by its resize frame on
    // every side, so that the frame hangs over the screen edges (and asking for less in WM_GETMINMAXINFO doesn't
    // change that), while WindowChrome lets the content fill the frame as well. The content is moved in by as much
    // as the window reaches past the work area: the window's border, which nothing draws.
    private static void FitToWorkArea(Window window, IntPtr hwnd, WindowChrome? chrome, double captionHeight)
    {
        var inset = new Thickness();
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (IsZoomed(hwnd) && GetWindowRect(hwnd, out var rect) && monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            inset = new Thickness(
                Math.Max(0, info.Work.Left - rect.Left) / dpi.DpiScaleX,
                Math.Max(0, info.Work.Top - rect.Top) / dpi.DpiScaleY,
                Math.Max(0, rect.Right - info.Work.Right) / dpi.DpiScaleX,
                Math.Max(0, rect.Bottom - info.Work.Bottom) / dpi.DpiScaleY);
        }
        if (window.BorderThickness != inset)
            window.BorderThickness = inset;
        // The title bar moves down with the content; the strip that drags the window has to reach as far.
        if (chrome is not null && chrome.CaptionHeight != captionHeight + inset.Top)
            chrome.CaptionHeight = captionHeight + inset.Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

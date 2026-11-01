using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameCloudSync.App.Services;

/// <summary>
/// Makes a window that draws its own title bar (WindowChrome with WindowStyle None) maximize like a normal
/// window: into the work area of its monitor, instead of over the whole monitor and the taskbar.
/// </summary>
public static class CustomFrame
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 2;

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook(Hook);
        };
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
            FitMaximizedToWorkArea(hwnd, lParam);
        return IntPtr.Zero;
    }

    // Without a system title bar Windows maximizes a window over the whole monitor, taskbar included.
    private static void FitMaximizedToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return;
        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // Relative to the monitor; Windows carries the values over when the window is maximized on another one.
        minMax.MaxPosition = new Point32(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        minMax.MaxSize = new Point32(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        Marshal.StructureToPtr(minMax, lParam, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;

        public Point32(int x, int y)
        {
            X = x;
            Y = y;
        }
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
    private struct MinMaxInfo
    {
        public Point32 Reserved;
        public Point32 MaxSize;
        public Point32 MaxPosition;
        public Point32 MinTrackSize;
        public Point32 MaxTrackSize;
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
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

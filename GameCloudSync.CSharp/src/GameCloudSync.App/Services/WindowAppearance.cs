using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameCloudSync.App.Services;

public static class WindowAppearance
{
    public static readonly DependencyProperty UseDarkTitleBarProperty = DependencyProperty.RegisterAttached(
        "UseDarkTitleBar", typeof(bool), typeof(WindowAppearance), new PropertyMetadata(false, OnChanged));

    public static bool GetUseDarkTitleBar(DependencyObject target) => (bool)target.GetValue(UseDarkTitleBarProperty);
    public static void SetUseDarkTitleBar(DependencyObject target, bool value) => target.SetValue(UseDarkTitleBarProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Window window) return;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
        else window.SourceInitialized += (_, _) => Apply(window);
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = GetUseDarkTitleBar(window) ? 1 : 0;
        // Older Windows builds may ignore these optional appearance hints.
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        // Windows 11 rounds windows with a system title bar by itself; the main window draws its own, so it has to ask.
        var round = DwmCornerRound;
        DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

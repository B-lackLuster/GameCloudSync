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

    private static void Apply(Window window)
    {
        var enabled = GetUseDarkTitleBar(window) ? 1 : 0;
        // Older Windows builds may ignore this optional appearance hint.
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

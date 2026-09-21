using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Compositor.Windows;

internal static class WindowAppearance
{
    static bool registered;
    public static void Register()
    {
        if (registered) return;
        registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender)));
    }
    public static void Apply(Window window)
    {
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || SystemParameters.HighContrast) return;
        int enabled = 1, caption = 0x001E1713, text = 0x00F9F4F0, border = 0x003D322C;
        // Unsupported attributes simply return HRESULT on older Windows versions.
        DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }
    internal static (int Width, int Height) ScreenSize(Window? owner)
    {
        var handle = owner == null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info)
            ? (info.Right - info.Left, info.Bottom - info.Top) : (1920, 1080);
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo { public int Size, Left, Top, Right, Bottom, WorkLeft, WorkTop, WorkRight, WorkBottom, Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace CodexUsageWidget;
internal sealed record BackdropResult(bool GlassActive, int RequestedType, int HResult);
internal static class WindowBackdrop
{
    internal static int TypeFor(bool glass, bool image) => glass && !image ? 3 : 1;
    internal static BackdropResult Apply(Window window, bool glass, bool image, bool light)
    {
        int type = TypeFor(glass, image);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return new(false, type, unchecked((int)0x80004005));
        int dark = light ? 0 : 1, corners = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4); DwmSetWindowAttribute(hwnd, 33, ref corners, 4);
        // Chrome must be configured before setting the composition target and extending the frame.
        var chrome = WindowChrome.GetWindowChrome(window);
        if (chrome != null) chrome.GlassFrameThickness = type == 3 ? new Thickness(-1) : new Thickness(0);
        var solid = light ? Color.FromRgb(239, 245, 252) : Color.FromRgb(20, 30, 46);
        window.Background = type == 3 ? Brushes.Transparent : new SolidColorBrush(solid);
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = type == 3 ? Colors.Transparent : solid;
        var margins = new Margins { Left = type == 3 ? -1 : 0 };
        int frame = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int result = DwmSetWindowAttribute(hwnd, 38, ref type, 4);
        bool active = type == 3 && result == 0 && frame == 0;
        if (!active)
        {
            // A real opaque fallback, not a translucent overlay on an undefined black surface.
            window.Background = new SolidColorBrush(solid);
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } fallback) fallback.BackgroundColor = solid;
        }
        return new(active, type, result != 0 ? result : frame);
    }
    internal static int? ReadType(Window window)
    { return DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 38, out int value, 4) == 0 ? value : null; }
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}

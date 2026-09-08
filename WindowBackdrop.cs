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
    internal static BackdropResult Apply(Window window, bool glass, bool image, bool light, bool noShadow = false)
    {
        int type = TypeFor(glass, image);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return new(false, type, unchecked((int)0x80004005));
        int dark = light ? 0 : 1, corners = noShadow ? 1 : 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4); DwmSetWindowAttribute(hwnd, 33, ref corners, 4);
        // Chrome must be configured before setting the composition target and extending the frame.
        var chrome = WindowChrome.GetWindowChrome(window);
        if (chrome != null) chrome.GlassFrameThickness = type == 3 && !noShadow ? new Thickness(-1) : new Thickness(0);
        var solid = light ? Color.FromRgb(239, 245, 252) : Color.FromRgb(20, 30, 46);
        window.Background = type == 3 ? Brushes.Transparent : new SolidColorBrush(solid);
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = type == 3 ? Colors.Transparent : solid;
        var margins = new Margins { Left = type == 3 && !noShadow ? -1 : 0 };
        int frame = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int result = DwmSetWindowAttribute(hwnd, 38, ref type, 4);
        bool active = type == 3 && result == 0 && frame == 0;
        // Disable native non-client decoration and bound the window region in follow mode.
        // Reapply after WindowChrome, which can otherwise bring the DWM frame back.
        int policy = noShadow ? 1 : 2;
        DwmSetWindowAttribute(hwnd, 2, ref policy, 4);
        if (noShadow)
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var region = CreateRoundRectRgn(0, 0, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX) + 1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY) + 1, (int)(30 * dpi.DpiScaleX), (int)(30 * dpi.DpiScaleY));
            if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
        }
        else SetWindowRgn(hwnd, IntPtr.Zero, true);
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
    internal static bool? NonClientEnabled(Window window) => DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 1, out int value, 4) == 0 ? value != 0 : null;
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr region);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}

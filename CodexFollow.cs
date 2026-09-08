using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUsageWidget;

internal sealed record FollowTarget(IntPtr Handle, Rect Client, double Scale);
internal interface IFollowSource { FollowTarget? Read(IntPtr widget); }

// Only reads window geometry and executable identity. Never reads titles, UI content or credentials.
internal sealed class CodexFollowSource : IFollowSource
{
    IntPtr lastHost, checkedWindow;
    uint checkedPid;
    bool checkedResult;
    DateTime checkedAt;
    DateTime searchedAt;
    internal static bool IsCodexExecutable(string path) =>
        (Path.GetFileName(path).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)) &&
        (path.Contains(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ||
         path.Contains(@"\OpenAI\Codex\", StringComparison.OrdinalIgnoreCase));
    bool IsHost(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        if (window == checkedWindow && pid == checkedPid && DateTime.UtcNow - checkedAt < TimeSpan.FromSeconds(2)) return checkedResult;
        checkedWindow = window; checkedPid = pid; checkedAt = DateTime.UtcNow; checkedResult = false;
        var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
        if (name.ToString() != "Chrome_WidgetWin_1") return false;
        try { using var process = Process.GetProcessById((int)pid); checkedResult = IsCodexExecutable(process.MainModule?.FileName ?? ""); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { }
        return checkedResult;
    }
    public FollowTarget? Read(IntPtr widget)
    {
        var foreground = GetForegroundWindow();
        var root = GetAncestor(foreground, 2);
        if (IsHost(root)) lastHost = root;
        // A visible background host is still a valid target. Discover it on startup too.
        if ((lastHost == IntPtr.Zero || !IsWindowVisible(lastHost) || !IsHost(lastHost)) && DateTime.UtcNow - searchedAt > TimeSpan.FromSeconds(2))
        {
            searchedAt = DateTime.UtcNow; lastHost = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                if (window != widget && IsWindowVisible(window) && !IsIconic(window) && IsHost(window)) { lastHost = window; return false; }
                return true;
            }, IntPtr.Zero);
        }
        if (lastHost == IntPtr.Zero || !IsWindowVisible(lastHost) || IsIconic(lastHost) || !IsHost(lastHost)) return null;
        if (DwmGetWindowAttribute(lastHost, 14, out int cloaked, 4) == 0 && cloaked != 0) return null;
        if (!GetClientRect(lastHost, out var rect)) return null;
        var origin = new NativePoint(); if (!ClientToScreen(lastHost, ref origin)) return null;
        var info = new WorkInfo { Size = Marshal.SizeOf<WorkInfo>() };
        var bounds = new Rect(origin.X, origin.Y, Math.Max(0, rect.Right), Math.Max(0, rect.Bottom));
        if (GetMonitorInfo(MonitorFromWindow(lastHost, 2), ref info))
            bounds.Intersect(new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
        return bounds.IsEmpty ? null : new(lastHost, bounds, Math.Max(96, GetDpiForWindow(lastHost)) / 96.0);
    }
    internal static Point Anchor(Rect client, Size widget, double scale) => new(
        client.Left + 8 * scale,
        Math.Max(client.Top + 48 * scale, client.Bottom - 48 * scale - widget.Height));
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct WorkInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    delegate bool WindowVisitor(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowVisitor visitor, IntPtr parameter);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr monitor, ref WorkInfo info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}

public sealed partial class WidgetWindow
{
    readonly DispatcherTimer followTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    internal IFollowSource FollowSource = new CodexFollowSource();
    bool followHiddenByUser;
    void CompactLayout()
    {
        bool compact = Preferences.FollowCodex && Width < 260;
        layout.Margin = new Thickness(compact ? 12 : 20, 14, compact ? 12 : 20, 14);
        brandMark.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        pin.Visibility = !compact && (Preferences.FollowCodex || Preferences.AlwaysOnTop) ? Visibility.Visible : Visibility.Collapsed;
        serverHeader.TextWrapping = TextWrapping.Wrap;
        serverHeader.MaxWidth = Math.Max(120, Width - (compact ? 56 : 72));
    }
    internal void InitializeFollow()
    {
        Toggle("Follow", "Codexに追従（左下・影なし）", () => Preferences.FollowCodex, SetFollowMode);
        followTimer.Tick += (_, _) => UpdateFollow();
    }
    internal void SetFollowMode(bool enabled)
    {
        if (Preferences.FollowCodex == enabled) return;
        Persist(); Preferences.FollowCodex = enabled; followHiddenByUser = false;
        Toggles["Follow"].IsChecked = enabled;
        MaxWidth = 800; MaxHeight = 900;
        MinWidth = enabled ? 200 : 280;
        Width = enabled ? Preferences.FollowWidth : Preferences.Width;
        Height = enabled ? Preferences.FollowHeight : Preferences.Height;
        Topmost = !enabled && Preferences.AlwaysOnTop;
        ShowActivated = !enabled && !testing;
        if (!enabled)
        {
            followTimer.Stop(); Left = Preferences.Left; Top = Preferences.Top;
            Show(); KeepOnScreen();
        }
        else if (!testing) followTimer.Start();
        ApplyBackground(); UpdateFollow(); Persist();
    }
    internal void StartFollow()
    {
        if (!Preferences.FollowCodex) return;
        ShowActivated = false;
        if (!testing) followTimer.Start();
        UpdateFollow();
    }
    internal void UpdateFollow()
    {
        if (closed || !ready || !Preferences.FollowCodex) return;
        // Keep menus/dialogs usable when opened from the tray, even if Codex is not foreground.
        if (ContextMenu.IsOpen || trayMenu?.IsOpen == true || OwnedWindows.Count > 0) return;
        var target = FollowSource.Read(new WindowInteropHelper(this).Handle);
        if (followHiddenByUser || target == null || target.Client.Width < MinWidth * target.Scale + 16 * target.Scale || target.Client.Height < (MinHeight + 96) * target.Scale)
        {
            Topmost = false; if (IsVisible) Hide(); return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        MaxWidth = Math.Max(MinWidth, Math.Min(800, (target.Client.Width - 16 * target.Scale) / dpi.DpiScaleX));
        MaxHeight = Math.Max(MinHeight, Math.Min(900, (target.Client.Height - 96 * target.Scale) / dpi.DpiScaleY));
        var size = new Size(Math.Min(Width, MaxWidth) * dpi.DpiScaleX, Math.Min(Height, MaxHeight) * dpi.DpiScaleY);
        var point = CodexFollowSource.Anchor(target.Client, size, target.Scale);
        Topmost = false;
        // Move our window only. No SetParent, injection or modification of the host app.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!IsVisible) { ShowActivated = false; Show(); QueueBackdrop(); }
        SetWindowPos(hwnd, AboveHost(target.Handle, hwnd), (int)Math.Round(point.X), (int)Math.Round(point.Y), 0, 0, 0x0010 | 0x0001);
    }
    internal static IntPtr AboveHost(IntPtr host, IntPtr widget)
    {
        // Insert immediately above Codex, below any other app covering Codex.
        var previous = GetWindow(host, 3); // GW_HWNDPREV
        if (previous == widget) previous = GetWindow(widget, 3);
        // Inserting after a topmost window would promote us to the topmost band.
        return previous != IntPtr.Zero && (GetWindowLongPtr(previous, -20).ToInt64() & 8) != 0 ? IntPtr.Zero : previous;
    }
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

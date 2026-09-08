using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CodexUsageWidget;

// Windows Shell notification-area API, with no installer, COM registration or network dependency.
internal sealed class TrayIcon : IDisposable
{
    const uint Callback = 0x8000 + 71;
    readonly HwndSource source;
    readonly Action restore;
    readonly Action menu;
    readonly uint taskbarCreated;
    NotifyIconData data;
    bool disposed;
    internal bool Registered { get; private set; }

    internal TrayIcon(HwndSource source, Action restore, Action menu)
    {
        this.source = source; this.restore = restore; this.menu = menu;
        data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = source.Handle, Id = 1,
            CallbackMessage = Callback, Tip = "Codex Usage Widget", Info = "", InfoTitle = "" };
        // The executable icon is owned by this instance and remains alive until NIM_DELETE.
        ExtractIconEx(Environment.ProcessPath!, 0, out var large, out var small, 1);
        data.Icon = small != IntPtr.Zero ? small : large;
        if (small != IntPtr.Zero && large != IntPtr.Zero) DestroyIcon(large);
        if (data.Icon == IntPtr.Zero) throw new InvalidOperationException("アプリアイコンを読み込めません。");
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        source.AddHook(Hook);
        Add();
    }
    void Add()
    {
        data.Flags = 1 | 2 | 4 | 0x80; // MESSAGE | ICON | TIP | SHOWTIP
        Registered = Shell_NotifyIcon(0, ref data);
        if (Registered) { data.Version = 4; Shell_NotifyIcon(4, ref data); }
    }
    IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (disposed) return IntPtr.Zero;
        if ((uint)message == taskbarCreated && taskbarCreated != 0) { Add(); return IntPtr.Zero; }
        if ((uint)message != Callback) return IntPtr.Zero;
        var code = unchecked((uint)lParam.ToInt64()) & 0xffff;
        if (code is 0x400 or 0x401 or 0x405) { restore(); handled = true; }
        else if (code is 0x7b or 0x205) { SetForegroundWindow(hwnd); menu(); handled = true; }
        return IntPtr.Zero;
    }
    internal bool Notify(string title, string message)
    {
        if (disposed) return false;
        if (!Registered) Add();
        if (!Registered) return false;
        var notice = data;
        notice.Flags = 0x10; notice.InfoTitle = title; notice.Info = message.Length <= 255 ? message : message[..255];
        notice.InfoFlags = 1 | 0x80; // NIIF_INFO | NIIF_RESPECT_QUIET_TIME
        return Shell_NotifyIcon(1, ref notice);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Shell_NotifyIcon(2, ref data); Registered = false;
        source.RemoveHook(Hook); DestroyIcon(data.Icon);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
}

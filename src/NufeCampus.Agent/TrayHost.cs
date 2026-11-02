using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Diagnostics;

internal sealed class TrayHost : IDisposable
{
    private const uint TrayMessage = 0x8001, NotifyMessage = 0x8002;
    private readonly WindowProc procedure;
    private readonly Action wake;
    private readonly CancellationTokenSource stop;
    private readonly string? testRoot;
    private readonly ConcurrentQueue<string> notifications = new();
    private nint window, power, trayIcon;
    private uint taskbarCreated;
    private IconData icon;
    public Action<string>? Command { get; set; }
    public TrayHost(Action wake, CancellationTokenSource stop, string? testRoot = null)
    {
        this.wake = wake; this.stop = stop; this.testRoot = testRoot; procedure = Handle;
        var cls = new WindowClass { Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = GetModuleHandle(null), Name = "NufeCampus.Agent." + Guid.NewGuid().ToString("N") };
        if (RegisterClass(ref cls) == 0) throw new IOException("Tray class unavailable");
        window = CreateWindow(0, cls.Name, "NUFE Campus Agent", 0, 0, 0, 0, 0, 0, 0, cls.Instance, 0);
        if (window == 0) throw new IOException("Tray window unavailable");
        ExtractIconEx(Environment.ProcessPath!, 0, out var largeIcon, out var smallIcon, 1);
        trayIcon = smallIcon != 0 ? smallIcon : largeIcon;
        if (largeIcon != 0 && largeIcon != trayIcon) DestroyIcon(largeIcon);
        icon = new IconData
        {
            Size = (uint)Marshal.SizeOf<IconData>(),
            Window = window,
            Id = 1,
            Flags = 7,
            Callback = TrayMessage,
            Icon = trayIcon != 0 ? trayIcon : LoadIcon(0, 32512),
            Tip = "NUFE · 校园网保活",
            Info = "",
            Title = ""
        };
        ShellNotify(0, ref icon); taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        power = RegisterSuspendResumeNotification(window, 0);
        stop.Token.Register(Stop);
    }
    public void Notify(string text) { if (text.Length == 0) return; notifications.Enqueue(text); PostMessage(window, NotifyMessage, 0, 0); }
    public void Stop() { if (window != 0) PostMessage(window, 0x10, 0, 0); }
    public void Run() { while (GetMessage(out var msg, 0, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); } }
    private nint Handle(nint hwnd, uint message, nint wparam, nint lparam)
    {
        if (message == taskbarCreated && taskbarCreated != 0) { icon.Flags = 7; ShellNotify(0, ref icon); return 0; }
        if (message == 0x218 && wparam is 7 or 18) { wake(); return 1; }
        if (message == NotifyMessage)
        {
            while (notifications.TryDequeue(out var text)) { icon.Flags = 0x10; icon.Info = text.Length > 250 ? text[..250] : text; icon.Title = "NUFE 校园网需要处理"; icon.InfoFlags = 2 | 0x10; ShellNotify(1, ref icon); icon.Info = ""; }
            return 0;
        }
        if (message == TrayMessage)
        {
            uint action = (uint)(long)lparam;
            if (action == 0x203 || action == 0x405) OpenApp();
            if (action == 0x205)
            {
                var menu = CreatePopupMenu();
                try
                {
                    AppendMenu(menu, 0, 1, "打开配置界面"); AppendMenu(menu, 0, 2, "暂停保活"); AppendMenu(menu, 0, 3, "恢复保活"); AppendMenu(menu, 0, 4, "退出本次运行");
                    GetCursorPos(out var point); SetForegroundWindow(hwnd);
                    uint selected = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, hwnd, 0);
                    if (selected == 1) OpenApp(); else if (selected != 0) Command?.Invoke(selected == 2 ? "pause" : selected == 3 ? "resume" : "stop");
                }
                finally { DestroyMenu(menu); }
            }
            return 0;
        }
        if (message == 0x10) { ShellNotify(2, ref icon); DestroyWindow(hwnd); return 0; }
        if (message == 2) { window = 0; stop.Cancel(); PostQuitMessage(0); return 0; }
        return DefWindowProc(hwnd, message, wparam, lparam);
    }
    private void OpenApp()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "NufeCampus.App.exe");
        if (File.Exists(path))
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false };
            if (testRoot != null) { start.ArgumentList.Add("--test-root"); start.ArgumentList.Add(testRoot); }
            Process.Start(start);
        }
    }
    public void Dispose() { ShellNotify(2, ref icon); if (power != 0) UnregisterSuspendResumeNotification(power); if (window != 0) DestroyWindow(window); if (trayIcon != 0) { DestroyIcon(trayIcon); trayIcon = 0; } GC.KeepAlive(procedure); }
    private delegate nint WindowProc(nint window, uint message, nint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu; public string Name; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct IconData { public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip; public uint State, StateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title; public uint InfoFlags; public Guid Guid; public nint BalloonIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint Wparam; public nint Lparam; public uint Time; public Point Point; public uint Private; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassW")] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")] private static extern nint CreateWindow(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadIcon(nint instance, nint resource);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")] private static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] private static extern bool ShellNotify(uint action, ref IconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint RegisterSuspendResumeNotification(nint recipient, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterSuspendResumeNotification(nint handle);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);
}

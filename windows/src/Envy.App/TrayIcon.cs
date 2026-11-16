using System.Runtime.InteropServices;

namespace Envy.App;

/// <summary>An entry in the tray icon's menu.</summary>
public sealed record TrayMenuItem(string Text, Action? Invoke = null, bool Checked = false, bool Enabled = true)
{
    public static readonly TrayMenuItem Separator = new("-");
    public bool IsSeparator => Text == "-";
}

/// <summary>
/// The notification area icon: a click opens Envy, a right-click shows quick switches. Built on Shell_NotifyIcon with a
/// native menu, so it looks like every other tray menu and works before any window exists.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    const uint CallbackMessage = 0x8000 + 17; // WM_APP + 17
    const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    const int WmContextMenu = 0x007B, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, NinSelect = 0x0400, NinKeySelect = 0x0401;

    readonly WndProc procedure;
    readonly IntPtr window;
    readonly IntPtr icon;
    readonly uint taskbarCreated;
    readonly string tooltip;
    bool added;

    public event Action? Clicked;
    public Func<IReadOnlyList<TrayMenuItem>>? Menu { get; set; }

    public TrayIcon(string iconPath, string tooltip)
    {
        this.tooltip = tooltip;
        procedure = WindowProcedure;
        var className = "EnvyTrayWindow" + Environment.ProcessId;
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClassEx(ref windowClass);
        window = CreateWindowEx(0, className, "Envy", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        icon = LoadImage(IntPtr.Zero, iconPath, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
        Add();
    }

    void Add()
    {
        var data = Data();
        added = ShellNotifyIcon(NimAdd, ref data);
        data.uVersion = 4;
        ShellNotifyIcon(NimSetVersion, ref data);
    }

    NotifyIconData Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = window,
        uID = 1,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    public void SetTooltip(string text)
    {
        var data = Data();
        data.szTip = text.Length > 127 ? text[..127] : text;
        ShellNotifyIcon(NimModify, ref data);
    }

    IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WmLButtonUp or NinSelect or NinKeySelect:
                    Clicked?.Invoke();
                    break;
                case WmContextMenu or WmRButtonUp:
                    ShowMenu();
                    break;
            }
            return IntPtr.Zero;
        }
        if (message == taskbarCreated)
        {
            // Explorer restarted and forgot every tray icon.
            Add();
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    void ShowMenu()
    {
        if (Menu?.Invoke() is not { Count: > 0 } items) return;
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.IsSeparator) { AppendMenu(menu, 0x800, UIntPtr.Zero, null); continue; }
                uint flags = 0;
                if (item.Checked) flags |= 0x8;
                if (!item.Enabled || item.Invoke is null) flags |= 0x1;
                AppendMenu(menu, flags, (UIntPtr)(uint)(i + 1), item.Text.Replace("&", "&&"));
            }
            GetCursorPos(out var point);
            // Without this the menu doesn't close when you click elsewhere.
            SetForegroundWindow(window);
            var chosen = TrackPopupMenuEx(menu, 0x0100 | 0x0002 | 0x0080, point.X, point.Y, window, IntPtr.Zero);
            PostMessage(window, 0, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= items.Count) items[chosen - 1].Invoke?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data();
            ShellNotifyIcon(NimDelete, ref data);
            added = false;
        }
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        if (window != IntPtr.Zero) DestroyWindow(window);
    }

    delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X; public int Y; }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string message);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint load);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr parameters);

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}

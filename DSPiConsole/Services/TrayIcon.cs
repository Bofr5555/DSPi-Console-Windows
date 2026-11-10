using System.Runtime.InteropServices;
using DSPiConsole.Core.Background;
using Microsoft.Win32;

namespace DSPiConsole.Services;

/// <summary>One entry of the notification icon's menu.</summary>
internal sealed record TrayMenuItem(string Text, Action? Invoke = null, bool Enabled = true, bool Checked = false, bool IsDefault = false)
{
    public static readonly TrayMenuItem Separator = new("-");
    public bool IsSeparator => Text == "-";
}

/// <summary>
/// The notification-area icon (Shell_NotifyIcon), owned by a hidden window on
/// the UI thread: a left click or Enter opens, the context menu is a native
/// popup menu built fresh each time it opens. The glyph is drawn at the size
/// and in the colour the taskbar wants, and redrawn when either changes; the
/// icon is added again when Explorer restarts. UI thread only.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const uint CallbackMessage = 0x8000 + 1;   // WM_APP + 1

    private readonly WndProc _wndProc;   // kept alive: the window calls it
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private string _tooltip = "DSPi Console";
    private bool _filled;
    private bool _added;
    private bool _disposed;

    /// <summary>A left click, or Enter on the focused icon.</summary>
    public event Action? Opened;
    /// <summary>The menu's entries, asked for each time it opens.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? Menu { get; set; }

    private static int _instances;
    private readonly string _className;

    public TrayIcon()
    {
        _wndProc = WindowProc;
        // A class per instance (the setting can be switched off and on): a
        // reused class would still call the first instance's delegate.
        var className = _className = $"DSPiConsole.TrayIcon.{Environment.ProcessId}.{Interlocked.Increment(ref _instances)}";
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);
        // A real top-level window, never shown: a message-only window doesn't
        // hear the broadcasts this needs (TaskbarCreated, theme changes).
        _hwnd = CreateWindowEx(0, className, "DSPi Console", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        DarkMenus.AllowFor(_hwnd);
        Add();
    }

    /// <summary>Tooltip and glyph (filled while sharing).</summary>
    public void Update(string tooltip, bool filled)
    {
        if (_disposed) return;
        bool redraw = filled != _filled;
        _tooltip = tooltip;
        _filled = filled;
        if (redraw) RenderIcon();
        Modify();
    }

    // ── Shell_NotifyIcon ──

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>Adds the icon, or takes over one the shell already has. A busy
    /// Explorer (at login, mostly) can report a timeout for a call that
    /// worked, so a failed add is tried as a modify, and the version is
    /// always set: without version 4 the icon would ignore clicks.</summary>
    private void Add()
    {
        RenderIcon();
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data) || Shell_NotifyIcon(NIM_MODIFY, ref data);
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private void Modify()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (Shell_NotifyIcon(NIM_MODIFY, ref data)) { _added = true; return; }
        Add();
    }

    /// <summary>A notification from the icon (Windows shows it as a toast).</summary>
    public void Notify(string title, string text)
    {
        if (_disposed) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = 0x1;   // NIIF_INFO
        if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
        {
            Add();
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    // ── The glyph ──

    private void RenderIcon()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96;
        int size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi);
        if (size <= 0) size = 16;
        // White on a dark taskbar, near-black on a light one.
        byte shade = TaskbarIsLight() ? (byte)0x1C : (byte)0xFF;
        var pixels = TrayGlyph.Render(size, _filled, shade, shade, shade);
        var next = CreateIcon(size, pixels);
        if (next == IntPtr.Zero) return;
        var old = _icon;
        _icon = next;
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    private static IntPtr CreateIcon(int size, uint[] pixels)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,   // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        // An all-clear AND mask: the colour bitmap's alpha does the shaping.
        // Rows of a 1-bit bitmap are padded to 16 bits.
        var maskBits = new byte[(size + 15) / 16 * 2 * size];
        var pin = GCHandle.Alloc(maskBits, GCHandleType.Pinned);
        IntPtr mask;
        try { mask = CreateBitmap(size, size, 1, 1, pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
        try
        {
            Marshal.Copy((int[])(object)pixels, 0, bits, pixels.Length);
            var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            if (mask != IntPtr.Zero) DeleteObject(mask);
        }
    }

    // ── Messages ──

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == CallbackMessage)
            {
                int evt = (int)((long)lParam & 0xFFFF);
                switch (evt)
                {
                    case NIN_SELECT:
                    case NIN_KEYSELECT:
                        Opened?.Invoke();
                        break;
                    case WM_CONTEXTMENU:
                        ShowMenu((short)((long)wParam & 0xFFFF), (short)(((long)wParam >> 16) & 0xFFFF));
                        break;
                }
                return IntPtr.Zero;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                // Explorer restarted: the icon went with the old taskbar.
                _added = false;
                Add();
                return IntPtr.Zero;
            }
            if (msg == WM_SETTINGCHANGE || msg == WM_DPICHANGED || msg == WM_DISPLAYCHANGE)
            {
                // Light/dark taskbar ("ImmersiveColorSet") or a new scale.
                if (msg == WM_SETTINGCHANGE) DarkMenus.Refresh();
                RenderIcon();
                Modify();
            }
        }
        catch (Exception ex)
        {
            Settings.SettingsWindow.WriteCrashLog("TrayIcon.WindowProc", ex);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var items = Menu?.Invoke();
        if (items is not { Count: > 0 }) return;
        var menu = CreatePopupMenu();
        try
        {
            uint id = 1;
            var actions = new Dictionary<uint, Action>();
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                    continue;
                }
                uint flags = MF_STRING;
                if (!item.Enabled || item.Invoke == null) flags |= MF_GRAYED;
                if (item.Checked) flags |= MF_CHECKED;
                // "&" would be read as an access-key marker.
                AppendMenu(menu, flags, id, item.Text.Replace("&", "&&"));
                if (item.IsDefault) SetMenuDefaultItem(menu, id, 0);
                if (item.Invoke != null) actions[id] = item.Invoke;
                id++;
            }
            // The documented dance: without the foreground, the menu doesn't
            // close when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);
            uint align = GetSystemMetrics(40 /* SM_MENUDROPALIGNMENT */) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            uint chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | align, x, y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            if (chosen != 0 && actions.TryGetValue(chosen, out var action)) action();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Always: a call that "failed" may still have added it, and deleting
        // an icon that isn't there is harmless. A leftover would sit in the
        // notification area until the pointer passed over it.
        var data = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        DestroyWindow(_hwnd);
        UnregisterClass(_className, GetModuleHandle(null));
    }

    /// <summary>Lets the popup menu follow a dark system theme, as Explorer's
    /// own do. The calls are uxtheme's unnamed exports (ordinals 133, 135,
    /// 136), present since Windows 10 1903; anything missing leaves the
    /// menu light, never broken.</summary>
    private static class DarkMenus
    {
        [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#133")] private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
        [DllImport("uxtheme.dll", EntryPoint = "#104")] private static extern void RefreshImmersiveColorPolicyState();

        /// <summary>After a theme change, so the next menu follows it.</summary>
        public static void Refresh()
        {
            if (Environment.OSVersion.Version.Build < 18362) return;
            try
            {
                RefreshImmersiveColorPolicyState();
                FlushMenuThemes();
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
        }

        public static void AllowFor(IntPtr hwnd)
        {
            if (Environment.OSVersion.Version.Build < 18362) return;
            try
            {
                SetPreferredAppMode(1);   // AllowDark: follow the system
                AllowDarkModeForWindow(hwnd, true);
                FlushMenuThemes();
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
        }
    }

    // ── Win32 ──

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, WM_CONTEXTMENU = 0x7B;
    private const uint WM_SETTINGCHANGE = 0x1A, WM_DPICHANGED = 0x2E0, WM_DISPLAYCHANGE = 0x7E;
    private const uint MF_STRING = 0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_LEFTALIGN = 0, TPM_RIGHTALIGN = 0x8, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bitCount, IntPtr bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}

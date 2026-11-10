using System.ComponentModel;
using System.Runtime.InteropServices;
using DSPiConsole.Core.Background;
using DSPiConsole.Core.Link;
using DSPiConsole.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.Win32;
using WinRT.Interop;

namespace DSPiConsole.Services;

/// <summary>
/// Running in the background (L5), the Windows counterpart of the macOS
/// Console's menu bar mode: an icon in the notification area showing the
/// hub's state, closing the window to it, starting hidden, opening at login,
/// and keeping the hub's timers (and, if asked, the PC) awake while sharing.
/// Hiding is presentation only: the device, the hub and its clients carry on.
/// UI thread, except where noted.
/// </summary>
public sealed class BackgroundMode
{
    public static BackgroundMode Shared { get; } = new();

    private MainWindow? _window;
    private DispatcherQueue? _dispatcher;
    private TrayIcon? _tray;
    private IntPtr _powerRequest;
    private bool _systemRequired;
    private bool _throttlingOff;
    private bool _awakeRefusalLogged;

    /// <summary>True while the window is hidden in the notification area.</summary>
    public bool IsMinimised { get; private set; }

    /// <summary>The window came back, or the settings changed.</summary>
    public event EventHandler? Changed;

    private BackgroundMode() { }

    private static AppSettings Settings => AppSettings.Instance;

    // ── Settings ──

    /// <summary>"Show in the notification area". Turning it off while hidden
    /// brings the window back: there'd be no other way to it.</summary>
    public bool ShowInNotificationArea
    {
        get => Settings.ShowInNotificationArea;
        set
        {
            if (Settings.ShowInNotificationArea == value) return;
            Settings.ShowInNotificationArea = value;
            Settings.Save();
            if (!value && IsMinimised) Restore();
            SyncTray();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>"Start in the notification area only".</summary>
    public bool StartInNotificationArea
    {
        get => Settings.StartInNotificationArea;
        set
        {
            if (Settings.StartInNotificationArea == value) return;
            Settings.StartInNotificationArea = value;
            Settings.Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>"Keep this PC awake while sharing".</summary>
    public bool KeepAwakeWhileSharing
    {
        get => Settings.KeepAwakeWhileSharing;
        set
        {
            if (Settings.KeepAwakeWhileSharing == value) return;
            Settings.KeepAwakeWhileSharing = value;
            Settings.Save();
            ApplyPower();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The argument the login entry passes, so a start at login can
    /// be told from a double-click or an update's relaunch.</summary>
    public const string LoginArgument = LoginItem.Argument;

    /// <summary>Whether this launch should leave the window hidden: only a
    /// start at login, with both settings on. Opening the app by hand, or
    /// relaunching after an update, always shows the window: on Windows a new
    /// notification icon is tucked in the overflow, so a hidden start there
    /// would look like nothing happened. (The Mac hides at every launch; its
    /// menu bar item is always in view.)</summary>
    public static bool StartsHidden =>
        Settings.ShowInNotificationArea && Settings.StartInNotificationArea
        && Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, LoginArgument, StringComparison.OrdinalIgnoreCase));

    // ── Attaching ──

    /// <summary>Called once, from the main window's constructor.</summary>
    public void Attach(MainWindow window)
    {
        _window = window;
        _dispatcher = window.DispatcherQueue;
        var link = LinkNetwork.Shared;
        link.Sharing.Changed += (_, _) => Post(() => { ApplyPower(); RefreshTray(); });
        link.Hub.Changed += (_, _) => Post(RefreshTray);
        link.Auth.Changed += (_, _) => Post(RefreshTray);
        window.ViewModel.PropertyChanged += OnViewModelChanged;
        RefreshLoginItem();
        SyncTray();
        ApplyPower();
        // A second launch that came before the window existed.
        if (Interlocked.Exchange(ref _restorePending, 0) == 1) _dispatcher.TryEnqueue(Restore);
    }

    private int _restorePending;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.IsDeviceConnected)) Post(RefreshTray);
    }

    private void Post(Action action) => _dispatcher?.TryEnqueue(() =>
    {
        try { action(); }
        catch (Exception ex) { DSPiConsole.Settings.SettingsWindow.WriteCrashLog("BackgroundMode", ex); }
    });

    /// <summary>The window is going for good: the icon and the power requests go with it.</summary>
    public void Detach()
    {
        _tray?.Dispose();
        _tray = null;
        ClearPower();
        if (_window != null) _window.ViewModel.PropertyChanged -= OnViewModelChanged;
        _window = null;
    }

    // ── Hiding and showing ──

    private AppWindow? AppWindowOf(MainWindow window) =>
        AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(window)));

    /// <summary>Hide the window to the notification area (turning the icon on,
    /// as the Mac's Minimise to Menu Bar does). Nothing else changes.</summary>
    public void Minimise()
    {
        if (_window is not { } window || IsMinimised) return;
        if (!Settings.ShowInNotificationArea)
        {
            Settings.ShowInNotificationArea = true;
            Settings.Save();
            SyncTray();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        // Never hidden with no icon to come back by.
        if (_tray == null) return;
        AppWindowOf(window)?.Hide();
        IsMinimised = true;
        LinkLog.Write("background: window hidden to the notification area");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Launch with the window hidden: it is created and laid out (so
    /// everything that needs it works) while cloaked, then hidden unseen.</summary>
    public void StartHidden(MainWindow window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        _cloaked = hwnd;
        SetCloak(hwnd, true);
        window.Activate();
        window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            // A Restore (the icon, a second launch) got here first: stay shown.
            if (_cloaked != hwnd) return;
            // No icon to come back by: show the window after all.
            if (_tray != null) AppWindowOf(window)?.Hide();
            Uncloak();
            if (_tray == null) return;
            IsMinimised = true;
            LinkLog.Write("background: started in the notification area");
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>The window cloaked by <see cref="StartHidden"/>, until it is
    /// hidden or restored.</summary>
    private IntPtr _cloaked;

    private static void SetCloak(IntPtr hwnd, bool on)
    {
        int value = on ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }

    private void Uncloak()
    {
        if (_cloaked == IntPtr.Zero) return;
        SetCloak(_cloaked, false);
        _cloaked = IntPtr.Zero;
    }

    /// <summary>A second launch: <see cref="Restore"/> on the UI thread, or as
    /// soon as the window exists. Any thread.</summary>
    public void RestoreFromAnyThread()
    {
        if (_dispatcher is { } dispatcher) dispatcher.TryEnqueue(Restore);
        else Interlocked.Exchange(ref _restorePending, 1);
    }

    /// <summary>Bring the window back, in front. Also what a second launch does.</summary>
    public void Restore()
    {
        if (_window is not { } window) return;
        Uncloak();
        var appWindow = AppWindowOf(window);
        if (appWindow != null)
        {
            appWindow.Show(true);
            if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        }
        window.Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(window));
        bool was = IsMinimised;
        IsMinimised = false;
        if (was)
        {
            LinkLog.Write("background: window restored");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── The icon ──

    private void SyncTray()
    {
        if (_window == null) return;
        if (Settings.ShowInNotificationArea && _tray == null)
        {
            try
            {
                _tray = new TrayIcon { Menu = BuildMenu };
                _tray.Opened += Restore;
            }
            catch (Exception ex)
            {
                DSPiConsole.Settings.SettingsWindow.WriteCrashLog("BackgroundMode.SyncTray", ex);
                _tray = null;
            }
            RefreshTray();
        }
        else if (!Settings.ShowInNotificationArea && _tray != null)
        {
            _tray.Dispose();
            _tray = null;
        }
    }

    private string StatusLine()
    {
        var sharing = LinkNetwork.Shared.Sharing;
        return TrayStatus.Line(_window?.ViewModel.IsDeviceConnected == true, sharing.IsRunning, sharing.ClientCount);
    }

    private void RefreshTray()
    {
        if (_tray == null) return;
        var line = StatusLine();
        if (LinkNetwork.Shared.Auth.ActivePin is { } pin) line += $"\nPairing code {pin}";
        _tray.Update(TrayStatus.Tooltip(line), LinkNetwork.Shared.Sharing.IsRunning);
    }

    /// <summary>A notification from the icon, when it's shown.</summary>
    public void Notify(string title, string text) => _tray?.Notify(title, text);

    /// <summary>"Allow a New Client..." from the icon: the code also goes up as
    /// a notification, since the menu that offered it has closed.</summary>
    private void BeginPairingFromTray()
    {
        var pin = LinkNetwork.Shared.Auth.BeginPairing();
        RefreshTray();
        _tray?.Notify("Pairing code " + pin, "Enter this code on the device you're adding. It works for 2 minutes.");
    }

    /// <summary>The menu, built as it opens: what the hub is doing, a way back
    /// to the window, sharing on or off, pairing, quit (the Mac's menu).</summary>
    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        var link = LinkNetwork.Shared;
        var sharing = link.Sharing;
        bool running = sharing.IsRunning;
        var items = new List<TrayMenuItem> { new(StatusLine(), Enabled: false) };
        if (running && LinkNetwork.LanAddress() is { } ip && sharing.BoundPort is { } port)
            items.Add(new($"{ip}:{port}", Enabled: false));
        if (link.Auth.ActivePin is { } pin)
            items.Add(new($"Pairing code {pin}", Enabled: false));
        items.Add(TrayMenuItem.Separator);
        items.Add(new(IsMinimised ? "Open DSPi Console" : "Show DSPi Console", Restore, IsDefault: true));
        items.Add(new("Share on This Network", () => SetSharing(!running), Checked: running));
        if (link.Auth.AuthMode == LinkAuthMode.Pin)
        {
            bool pairing = link.Auth.ActivePin != null;
            items.Add(new(pairing ? "Cancel Pairing" : "Allow a New Client...",
                () => { if (pairing) { link.Auth.CancelPairing(); RefreshTray(); } else BeginPairingFromTray(); },
                Enabled: running));
        }
        items.Add(TrayMenuItem.Separator);
        items.Add(new("Quit DSPi Console", () => _window?.QuitFromNotificationArea()));
        return items;
    }

    private void SetSharing(bool on) => Task.Run(() =>
    {
        try { LinkNetwork.Shared.SetSharing(on); }
        catch (Exception ex) { LinkLog.Write($"background: sharing {(on ? "on" : "off")} threw {ex.GetType().Name}"); }
    });

    // ── Open at login ──

    private static string LaunchPath =>
        LoginItem.LaunchPath(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DSPiConsole.exe"), File.Exists);

    /// <summary>On as the user sees it (Task Manager's switch included).</summary>
    public bool StartsAtLogin
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(LoginItem.RunKey);
                using var approved = Registry.CurrentUser.OpenSubKey(LoginItem.ApprovedKey);
                return LoginItem.IsOn(run?.GetValue(LoginItem.ValueName) as string,
                    approved?.GetValue(LoginItem.ValueName) as byte[], LaunchPath);
            }
            catch { return false; }
        }
    }

    /// <summary>Why the last change didn't take, or null.</summary>
    public string? LoginItemError { get; private set; }

    public void SetStartsAtLogin(bool on)
    {
        LoginItemError = null;
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(LoginItem.RunKey, writable: true);
            if (on) run.SetValue(LoginItem.ValueName, LoginItem.Command(LaunchPath), RegistryValueKind.String);
            else run.DeleteValue(LoginItem.ValueName, throwOnMissingValue: false);
            // Turning it on here also clears a "Disabled" set in Task Manager,
            // or the switch would stay off.
            using var approved = Registry.CurrentUser.OpenSubKey(LoginItem.ApprovedKey, writable: true);
            approved?.DeleteValue(LoginItem.ValueName, throwOnMissingValue: false);
            LinkLog.Write($"background: open at login {(on ? "on" : "off")}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            LoginItemError = $"Windows didn't allow the change: {ex.Message}";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>An entry naming a copy that's gone (moved or deleted) is
    /// pointed here, so login still starts the app.</summary>
    private static void RefreshLoginItem()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(LoginItem.RunKey, writable: true);
            if (run?.GetValue(LoginItem.ValueName) is string value
                && (LoginItem.IsStale(value, LaunchPath, File.Exists) || LoginItem.IsOutdated(value, LaunchPath)))
            {
                run.SetValue(LoginItem.ValueName, LoginItem.Command(LaunchPath), RegistryValueKind.String);
                LinkLog.Write("background: open at login now starts this copy");
            }
        }
        catch { }
    }

    // ── Power ──

    /// <summary>While sharing: Windows mustn't throttle the hub's timers when
    /// the window is hidden (EcoQoS, timer coalescing), and, if asked, mustn't
    /// idle-sleep the PC (the Mac's App Nap and sleep assertions).</summary>
    private void ApplyPower()
    {
        bool sharing = LinkNetwork.Shared.Sharing.IsRunning;
        SetThrottling(off: sharing);
        bool wantAwake = sharing && Settings.KeepAwakeWhileSharing;
        if (wantAwake == _systemRequired) return;
        if (wantAwake)
        {
            if (_powerRequest == IntPtr.Zero)
            {
                var context = new REASON_CONTEXT
                {
                    Version = 0,
                    Flags = 1,   // POWER_REQUEST_CONTEXT_SIMPLE_STRING
                    Reason = Marshal.StringToHGlobalUni("Sharing DSPi devices on the local network"),
                };
                _powerRequest = PowerCreateRequest(ref context);
                if (_powerRequest == new IntPtr(-1)) _powerRequest = IntPtr.Zero;
                // The reason string is copied by the call.
                Marshal.FreeHGlobal(context.Reason);
            }
            _systemRequired = _powerRequest != IntPtr.Zero && PowerSetRequest(_powerRequest, 1 /* PowerRequestSystemRequired */);
            // A refusal is retried on each sharing change, but logged once.
            if (_systemRequired) LinkLog.Write("background: keeping the PC awake while sharing");
            else if (!_awakeRefusalLogged) LinkLog.Write("background: couldn't hold off sleep");
            _awakeRefusalLogged = !_systemRequired;
        }
        else
        {
            if (_powerRequest != IntPtr.Zero) PowerClearRequest(_powerRequest, 1);
            _systemRequired = false;
            LinkLog.Write("background: the PC may sleep again");
        }
    }

    private void ClearPower()
    {
        if (_powerRequest != IntPtr.Zero)
        {
            if (_systemRequired) PowerClearRequest(_powerRequest, 1);
            CloseHandle(_powerRequest);
            _powerRequest = IntPtr.Zero;
        }
        _systemRequired = false;
        SetThrottling(off: false);
    }

    private void SetThrottling(bool off)
    {
        if (off == _throttlingOff) return;
        // Off: execution speed and timer resolution are ours to keep (the
        // control bits set, the state bits clear). Back on: hand both back to
        // the system (neither controlled).
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = off ? 0x1u /* EXECUTION_SPEED */ | 0x4u /* IGNORE_TIMER_RESOLUTION */ : 0,
            StateMask = 0,
        };
        if (SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
            _throttlingOff = off;
    }

    // ── Win32 ──

    private const int DWMWA_CLOAK = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct REASON_CONTEXT
    {
        public uint Version, Flags;
        public IntPtr Reason;
        // The rest of the union (the detailed form), unused.
        public IntPtr Pad0, Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(IntPtr request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(IntPtr request, int type);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}

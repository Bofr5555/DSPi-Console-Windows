using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Usb.Transport;

/// <summary>
/// The local UI's link to the board this PC has open: USB for the connection,
/// the device list and the notification stream, but every command through
/// the hub's router as session 1, the same path a remote client's takes. So
/// ordering, attribution and locks are the same for everyone, whether or not
/// sharing is on (spec 5, 8.2). Port of the macOS Console's HubTransport.
///
/// "Connected" means the hub has attached the board (its identity read and
/// its router built), not merely that USB opened it, so the view model never
/// sees a device it can't yet send to.
/// </summary>
public sealed class HubLocalTransport : IDspTransport
{
    private readonly IDspTransport _usb;
    private readonly LinkHub _hub;
    private readonly object _lock = new();
    private ulong _attachedGeneration;
    private bool _connected;
    private ulong _probeGeneration;
    /// <summary>Connected and Disconnected are raised under this, and only in
    /// turn, so a probe finishing just as USB drops can't tell the facade
    /// "connected" after it heard "disconnected".</summary>
    private readonly object _raiseLock = new();
    private bool _raisedConnected;

    /// <summary>Longest a command waits for the router. The router bounds each
    /// command itself (2 s, bulk 5 s) and answers every one it is handed, so
    /// this only guards a wedged queue.</summary>
    public TimeSpan SyncWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Pauses before each identity probe, the first immediate: about
    /// 2.5 s in all, the patience the connect sequence has for a board still
    /// booting after re-enumeration.</summary>
    public IReadOnlyList<TimeSpan> IdentityRetryDelays { get; init; } =
        new[] { 0, 100, 200, 400, 800, 1000 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray();

    /// <summary>Builds the hub's device for the current USB open; null means
    /// it didn't answer yet. Tests replace it.</summary>
    public Func<IDspTransport, IHubDevice?> DeviceFactory { get; init; } = usb =>
        usb.SelectedDevice is { } d && UsbHubDevice.ReadInfo(usb, d.Serial) is { } info ? new UsbHubDevice(usb, info) : null;

    public HubLocalTransport(IDspTransport usb, LinkHub hub)
    {
        _usb = usb;
        _hub = hub;
        // The local UI is always session 1, an admin, never capped.
        _hub.OpenSession(LinkRole.Admin, LinkSessionIds.Local);
        _usb.Connected += OnUsbConnected;
        _usb.Disconnected += OnUsbDisconnected;
        _usb.AvailableDevicesChanged += (s, e) => AvailableDevicesChanged?.Invoke(this, e);
        _usb.ErrorMessageChanged += (s, e) => ErrorMessageChanged?.Invoke(this, e);
        _usb.SelectedDeviceChanged += (s, e) => SelectedDeviceChanged?.Invoke(this, e);
        _usb.NotificationReceived += OnUsbNotification;
    }

    public ushort Session => LinkSessionIds.Local;
    public ulong Generation => _usb.Generation;
    public bool IsConnected
    {
        get { lock (_lock) return _connected && _attachedGeneration == _usb.Generation && _usb.IsConnected; }
    }
    public string? ErrorMessage => _usb.ErrorMessage;
    public DSPiDeviceInfo? SelectedDevice => _usb.SelectedDevice;
    public IReadOnlyList<DSPiDeviceInfo> AvailableDevices => _usb.AvailableDevices;

    public event EventHandler<DSPiDeviceInfo>? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler? AvailableDevicesChanged;
    public event EventHandler? ErrorMessageChanged;
    public event EventHandler? SelectedDeviceChanged;
    public event EventHandler<LinkNotification>? NotificationReceived;

    // ── Connection ──

    /// <summary>A USB open (a switch between boards is a new generation with
    /// no disconnect in between): read the board's identity, retrying while
    /// it boots, then attach it and only then tell the facade.</summary>
    private void OnUsbConnected(object? sender, DSPiDeviceInfo device)
    {
        ulong generation = _usb.Generation;
        bool wasConnected;
        lock (_lock)
        {
            if (_probeGeneration == generation) return;   // same open
            _probeGeneration = generation;
            wasConnected = _connected;
            _connected = false;
        }
        if (wasConnected) _hub.DetachDevice();
        RaiseDisconnected();
        _ = Task.Run(() => ProbeAndAttach(generation, device));
    }

    private void ProbeAndAttach(ulong generation, DSPiDeviceInfo device)
    {
        IHubDevice? hubDevice = null;
        foreach (var delay in IdentityRetryDelays)
        {
            if (delay > TimeSpan.Zero) Thread.Sleep(delay);
            if (!StillCurrent(generation)) return;
            try { hubDevice = DeviceFactory(_usb); }
            catch (Exception ex) { LinkLog.Write($"hub: identity read threw {ex.GetType().Name}"); }
            if (hubDevice != null) break;
        }
        // A board that never answered its platform request is still the
        // local UI's to use; it is shared with what little is known.
        hubDevice ??= new UsbHubDevice(_usb, new HubDeviceInfo(device.Serial, 0, "0.0.0", 0));

        lock (_lock)
        {
            if (_probeGeneration != generation || !_usb.IsConnected) return;
            _hub.AttachDevice(hubDevice);
            _attachedGeneration = generation;
            _connected = true;
        }
        lock (_raiseLock)
        {
            // Still the open this probe was for, now that it's our turn.
            lock (_lock)
                if (!_connected || _attachedGeneration != generation || _usb.Generation != generation || !_usb.IsConnected) return;
            if (_raisedConnected) return;
            _raisedConnected = true;
            Connected?.Invoke(this, device);
        }
    }

    private void RaiseDisconnected()
    {
        lock (_raiseLock)
        {
            if (!_raisedConnected) return;
            _raisedConnected = false;
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool StillCurrent(ulong generation)
    {
        lock (_lock) return _probeGeneration == generation && _usb.Generation == generation && _usb.IsConnected;
    }

    private void OnUsbDisconnected(object? sender, EventArgs e)
    {
        bool wasConnected;
        lock (_lock)
        {
            wasConnected = _connected;
            _connected = false;
            _probeGeneration = 0;
        }
        _hub.DetachDevice();
        RaiseDisconnected();
    }

    /// <summary>Every packet the endpoint yields still reaches the local UI
    /// (the Interrupt Monitor shows them all), attributed by the hub, which
    /// also relays the v2 ones to its remote sessions.</summary>
    private void OnUsbNotification(object? sender, LinkNotification note)
    {
        ushort origin = _hub.Ingest(note);
        NotificationReceived?.Invoke(this, note with { Origin = origin });
    }

    public void Start() => _usb.Start();
    public void Stop() => _usb.Stop();
    public void SelectDevice(DSPiDeviceInfo device) => _usb.SelectDevice(device);
    public void Reconnect() => _usb.Reconnect();
    public void Disconnect() => _usb.Disconnect();
    public void MarkDisconnected() => _usb.MarkDisconnected();

    // ── Transfers, through the hub's router ──

    public LinkStatus ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data) =>
        Route(new LinkCmdRequest(0, _hub.CurrentHandle, LinkDirection.Set, request, value, index, payload: data.ToArray()), out _);

    public LinkStatus ControlIn(byte request, ushort value, ushort index, int length, out byte[] data) =>
        Route(new LinkCmdRequest(0, _hub.CurrentHandle, LinkDirection.Get, request, value, index, (ushort)Math.Clamp(length, 0, ushort.MaxValue)), out data);

    private LinkStatus Route(LinkCmdRequest request, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (!IsConnected) return LinkStatus.NoDevice;
        var done = new TaskCompletionSource<LinkCmdResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hub.Submit(request, LinkSessionIds.Local, r => done.TrySetResult(r));
        if (!done.Task.Wait(SyncWait)) return LinkStatus.Timeout;
        var response = done.Task.Result;
        if (response.Status == LinkStatus.Locked) LinkLockNotice.Post();
        if (response.Status != LinkStatus.Ok) return response.Status;
        data = response.Payload;
        return LinkStatus.Ok;
    }

    /// <summary>Each command already takes its turn in the router; the one
    /// sequence that must not be split (the chunked bulk read) runs whole
    /// inside the device's own call.</summary>
    public T Exclusive<T>(Func<T> body) => body();

    /// <summary>One REQ_GET_ALL_PARAMS through the router, which the USB
    /// device serves as a chunked read held for its whole length.</summary>
    public byte[]? ReadBulkParams() =>
        Route(new LinkCmdRequest(0, _hub.CurrentHandle, LinkDirection.Get, VendorCommands.GetAllParams, 0, 2, ushort.MaxValue), out var blob) == LinkStatus.Ok
        && BulkRead.IsComplete(blob)
            ? blob
            : null;

    // ── Lock (spec 7.7) ──

    public bool LastLockRefused { get; private set; }

    /// <summary>With no board attached there's no router to lock; the flow
    /// then fails on its own commands as NO_DEVICE, the truer message.</summary>
    public bool AcquireDeviceLock(string reason, TimeSpan timeout)
    {
        LastLockRefused = false;
        if (!IsConnected) return true;
        if (_hub.AcquireLock(LinkSessionIds.Local, timeout < LinkLockNotice.MaxTimeout ? timeout : LinkLockNotice.MaxTimeout)) return true;
        LastLockRefused = true;
        LinkLockNotice.Post();
        return false;
    }

    public void ReleaseDeviceLock() => _hub.ReleaseLock(LinkSessionIds.Local);

    public void Dispose()
    {
        _usb.Connected -= OnUsbConnected;
        _usb.Disconnected -= OnUsbDisconnected;
        _usb.NotificationReceived -= OnUsbNotification;
        _hub.DetachDevice();
        _usb.Dispose();
    }
}

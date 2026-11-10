using DSPiConsole.Core.Link;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace DSPiConsole.Usb.Transport;

/// <summary>
/// The DSPi over USB, through LibUsbDotNet on the WinUSB-bound vendor
/// interface 2: device scanning and selection, the open handle, the vendor
/// control transfers, and the notification endpoint (bulk IN EP 0x83). Moved
/// out of <see cref="DspDevice"/> unchanged in behaviour; the only host on
/// this link is us, so every host-sourced notification is attributed to our
/// own session.
/// </summary>
public sealed class UsbTransport : IDspTransport
{
    // Device identification
    private const int VendorId = 0x2E8B;
    private const int ProductId = 0xFEAA;

    // Interface 2 is the vendor-specific control interface
    private const int VendorInterfaceNumber = 2;

    // 0x41 = Host-to-Device | Vendor | Interface; 0xC1 = Device-to-Host | Vendor | Interface
    private const byte RequestTypeOut = 0x41;
    private const byte RequestTypeIn = 0xC1;

    /// <summary>The local UI's session, as on a hub (the Mac's LinkHub.localSessionID).</summary>
    public const ushort LocalSession = 1;

    private readonly UsbContext _context = new();
    private IUsbDevice? _device;
    private bool _interfaceClaimed;
    private byte _openBusNumber;
    private byte _openAddress;
    private readonly object _lock = new();
    private readonly System.Timers.Timer _pollTimer;
    private volatile bool _disposed;

    // Multi-device tracking
    private List<DSPiDeviceInfo> _availableDevices = new();
    private DSPiDeviceInfo? _selectedDeviceInfo;
    private string? _lastSelectedSerial;
    private string? _openDeviceSerial; // serial of the currently open _device handle
    private bool _isConnected;
    private string? _errorMessage;
    private ulong _generation;

    // Notification endpoint state (bulk IN EP 0x83, V7+ firmware).
    private UsbEndpointReader? _notifyReader;
    private Thread? _notifyThread;
    private volatile bool _notifyStop;
    private const int NotifyPacketSize = 64;
    private const int NotifyFailuresBeforeLost = 20; // ~1 s of failed reads

    public UsbTransport()
    {
        // Poll for devices every 500ms
        _pollTimer = new System.Timers.Timer(500);
        _pollTimer.Elapsed += (_, _) => ScanDevices();
        _pollTimer.AutoReset = true;
    }

    public ushort Session => LocalSession;
    public ulong Generation => Interlocked.Read(ref _generation);
    public bool IsConnected => _isConnected;
    public string? ErrorMessage => _errorMessage;
    public DSPiDeviceInfo? SelectedDevice => _selectedDeviceInfo;
    public IReadOnlyList<DSPiDeviceInfo> AvailableDevices => _availableDevices;

    public event EventHandler<DSPiDeviceInfo>? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler? AvailableDevicesChanged;
    public event EventHandler? ErrorMessageChanged;
    public event EventHandler? SelectedDeviceChanged;
    public event EventHandler<LinkNotification>? NotificationReceived;

    private void SetError(string? message)
    {
        if (_errorMessage == message) return;
        _errorMessage = message;
        ErrorMessageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Start()
    {
        _pollTimer.Start();
        ScanDevices();
    }

    public void Stop() => _pollTimer.Stop();

    /// <summary>
    /// Read the serial number from a temporarily opened USB device using the
    /// vendor GET_SERIAL control request. The device must already be open and
    /// have its interface claimed.
    /// </summary>
    private static string? ReadSerialFromDevice(IUsbDevice tempDevice)
    {
        var setupPacket = new UsbSetupPacket(RequestTypeIn, VendorCommands.GetSerial, 0, VendorInterfaceNumber, 16);
        var buffer = new byte[16];
        int transferred;
        try
        {
            transferred = tempDevice.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
        }
        catch
        {
            // Hot-plug races and brief WinUSB stalls can throw here during
            // enumeration / open-by-serial. Treat them as "no serial" so the
            // poll loop just skips this device and retries on the next tick.
            return null;
        }
        if (transferred > 0)
            return System.Text.Encoding.ASCII.GetString(buffer, 0, transferred).TrimEnd('\0');
        return null;
    }

    /// <summary>
    /// Configure (idempotent on Windows/WinUSB) and claim the vendor interface
    /// on a freshly opened device. Returns true on success.
    /// </summary>
    private static bool ConfigureAndClaim(IUsbDevice dev)
    {
        try { dev.SetConfiguration(1); }
        catch { /* already configured by Windows; libusb winusb backend tolerates this */ }
        return dev.ClaimInterface(VendorInterfaceNumber);
    }

    /// <summary>
    /// Cached serials keyed by (bus, address). Avoids opening matching devices
    /// repeatedly on every scan tick — a duplicate open of the currently-active
    /// device disturbs the in-flight control transfer queue on the original
    /// handle (WinUSB lets the second open through, then SetConfiguration /
    /// ClaimInterface on the duplicate handle stomps the original's state and
    /// every subsequent control GET silently returns 0 bytes).
    /// </summary>
    private readonly Dictionary<(byte bus, byte addr), string> _serialCache = new();

    private static (byte bus, byte addr) GetBusAddr(IUsbDevice dev)
    {
        // BusNumber/Address live on the concrete UsbDevice — IUsbDevice doesn't
        // expose them. Fall back to (0,0) if the cast fails (won't happen with
        // the libusb-1.0 backend, which always returns UsbDevice instances).
        if (dev is UsbDevice ud) return (ud.BusNumber, ud.Address);
        return (0, 0);
    }

    // Guards ScanDevices against re-entrancy. The scan runs on a 500 ms
    // auto-reset timer, but enumerating and opening a device (list + clone +
    // open + claim + read serial) can take longer than that interval. Without
    // this guard the timer re-enters ScanDevices on another thread-pool thread
    // while a previous scan is still mid-open; the overlapping scans each call
    // OpenDevice, which tears down any existing connection at entry, so they
    // repeatedly disconnect the device the previous scan just opened — leaving
    // it perpetually reconnecting and leaking scan threads.
    private int _scanActive;

    // Set when a transfer finds the open handle dead (the device is gone, or
    // keeps failing with I/O errors) while enumeration still lists it: a quick
    // reseat into the same port, or a sleep/resume that cut USB power, gives
    // the device back at the same bus address, so the scan would keep the stale
    // handle forever with every transfer failing. The next scan drops it and
    // reopens the device. Cleared by a successful open.
    private volatile bool _deviceLost;
    private int _consecutiveIoErrors;
    private const int IoErrorsBeforeLost = 5;

    /// <summary>Notes a failed transfer's error: the device gone at once, or
    /// several I/O errors in a row. A STALL (unsupported request) or a timeout
    /// is not a sign of a dead handle and doesn't count.</summary>
    private void NoteTransferError(Error err)
    {
        if (err is Error.NoDevice or Error.NotFound)
            _deviceLost = true;
        else if (err == Error.Io && ++_consecutiveIoErrors >= IoErrorsBeforeLost)
            _deviceLost = true;
    }

    /// <summary>
    /// Scan for all connected DSPi devices, update the available list,
    /// and auto-select/reconnect as needed.
    /// </summary>
    private void ScanDevices()
    {
        if (_disposed) return;

        // Only one scan at a time (see _scanActive).
        if (Interlocked.CompareExchange(ref _scanActive, 1, 0) != 0)
            return;

        try
        {
            // A handle found dead by a transfer: drop it so the code below sees no
            // device open and reopens the selected one (see _deviceLost).
            if (_deviceLost)
            {
                _deviceLost = false;
                lock (_lock) { if (_device != null) HandleDisconnect(); }
            }

            using var allDevicesList = _context.List();
            var matching = allDevicesList
                .Where(d => d.VendorId == VendorId && d.ProductId == ProductId)
                .ToList();

            // Drop cache entries for devices that have unplugged.
            var liveKeys = matching.Select(GetBusAddr).ToHashSet();
            foreach (var stale in _serialCache.Keys.Where(k => !liveKeys.Contains(k)).ToList())
                _serialCache.Remove(stale);

            // Build the current device list. For the device we already have open,
            // skip the open/claim/read cycle entirely — we know its serial.
            var currentDevices = new List<DSPiDeviceInfo>();
            (byte bus, byte addr) openKey = (_openBusNumber, _openAddress);
            bool weHaveOpen = _device != null && _isConnected && _selectedDeviceInfo != null;

            foreach (var dev in matching)
            {
                var key = GetBusAddr(dev);

                if (weHaveOpen && key.bus == openKey.bus && key.addr == openKey.addr)
                {
                    currentDevices.Add(_selectedDeviceInfo!);
                    continue;
                }

                if (_serialCache.TryGetValue(key, out var cachedSerial))
                {
                    currentDevices.Add(new DSPiDeviceInfo(cachedSerial,
                        $"vid_{VendorId:X4}&pid_{ProductId:X4}#{cachedSerial}"));
                    continue;
                }

                // Unknown device — open briefly to read serial, then close.
                try
                {
                    if (!dev.TryOpen()) continue;
                    try
                    {
                        if (!ConfigureAndClaim(dev)) continue;
                        var serial = ReadSerialFromDevice(dev);
                        if (!string.IsNullOrEmpty(serial))
                        {
                            _serialCache[key] = serial!;
                            currentDevices.Add(new DSPiDeviceInfo(serial!,
                                $"vid_{VendorId:X4}&pid_{ProductId:X4}#{serial}"));
                        }
                    }
                    finally
                    {
                        try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                        try { dev.Close(); } catch { }
                    }
                }
                catch { }
            }

            var oldSerials = _availableDevices.Select(d => d.Serial).ToHashSet();
            var newSerials = currentDevices.Select(d => d.Serial).ToHashSet();
            if (!oldSerials.SetEquals(newSerials))
            {
                _availableDevices = currentDevices;
                AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
            }

            if (_selectedDeviceInfo != null && !newSerials.Contains(_selectedDeviceInfo.Serial))
            {
                lock (_lock) { HandleDisconnect(); }
                if (currentDevices.Count == 0)
                {
                    _selectedDeviceInfo = null;
                    SelectedDeviceChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            if (_device != null && _isConnected)
            {
                if (matching.Count == 0)
                    lock (_lock) { HandleDisconnect(); }
                else
                    ProbeIfIdle();
                return;
            }

            if (_device == null && currentDevices.Count > 0)
            {
                var reconnectTarget = _lastSelectedSerial != null
                    ? currentDevices.FirstOrDefault(d => d.Serial == _lastSelectedSerial)
                    : null;
                OpenDevice(reconnectTarget ?? currentDevices[0]);
            }
            else if (currentDevices.Count == 0)
            {
                if (_errorMessage == null || _errorMessage == "Disconnected")
                {
                    using var anyDevicesList = _context.List();
                    if (anyDevicesList.Count == 0)
                        SetError("No USB devices visible to libusb-1.0. Install the WinUSB driver for the DSPi vendor interface.");
                    else
                        SetError("Disconnected");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScanDevices error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _scanActive, 0);
        }
    }

    /// <summary>When the last transfer was attempted, for <see cref="ProbeIfIdle"/>.</summary>
    private long _lastTransferTicks = Environment.TickCount64;
    private static readonly TimeSpan IdleProbeAfter = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A dead handle is only noticed by a transfer failing on it. Normally the
    /// view model's 60 ms poll provides those, but it stops polling when it
    /// decides the device isn't answering, and then a stale handle (a quick
    /// reseat, a resume that cut USB power) would be kept forever. So when
    /// nothing has used the link for a while, read the serial once: a failure
    /// counts toward <see cref="_deviceLost"/> like any other.
    /// </summary>
    private void ProbeIfIdle()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _lastTransferTicks) < IdleProbeAfter.TotalMilliseconds) return;
        ControlIn(VendorCommands.GetSerial, 0, VendorInterfaceNumber, 16, out _);
    }

    /// <summary>
    /// Open and connect to a specific device by its info. The opened device is
    /// retained outside the listing collection so it survives the collection's
    /// disposal (libusb-1.0 holds an internal ref while the handle is open).
    /// </summary>
    private void OpenDevice(DSPiDeviceInfo deviceInfo)
    {
        lock (_lock)
        {
            try
            {
                if (_device != null)
                {
                    HandleDisconnect();
                }

                // libusb-1.0 wrapper detail: UsbDeviceCollection.Dispose() also
                // disposes every IUsbDevice instance it contains. Retaining one
                // past the listing's `using` block leaves us with a disposed
                // wrapper — every subsequent ControlTransfer raises
                // ObjectDisposedException, which the viewmodels' broad try/catch
                // swallows silently (so the device looks "connected" but no GETs
                // ever return data). Clone() the candidates first so they
                // outlive the collection.
                List<IUsbDevice> candidates = new();
                using (var devices = _context.List())
                {
                    foreach (var d in devices.Where(d => d.VendorId == VendorId && d.ProductId == ProductId))
                        candidates.Add(d.Clone());
                }

                IUsbDevice? opened = null;
                bool openedClaimed = false;
                (byte bus, byte addr) openedKey = (0, 0);

                foreach (var dev in candidates)
                {
                    if (opened != null)
                    {
                        // Already found our match — discard remaining clones.
                        try { dev.Close(); } catch { }
                        continue;
                    }

                    if (!dev.TryOpen())
                    {
                        try { dev.Close(); } catch { }
                        continue;
                    }

                    bool claimed = false;
                    try
                    {
                        if (!ConfigureAndClaim(dev))
                        {
                            try { dev.Close(); } catch { }
                            continue;
                        }
                        claimed = true;

                        var serial = ReadSerialFromDevice(dev);
                        if (serial == deviceInfo.Serial)
                        {
                            opened = dev;
                            openedClaimed = true;
                            openedKey = GetBusAddr(dev);
                            // Don't release/close — keep this clone alive.
                        }
                        else
                        {
                            try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                            try { dev.Close(); } catch { }
                        }
                    }
                    catch
                    {
                        if (claimed)
                            try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                        try { dev.Close(); } catch { }
                    }
                }

                if (opened == null)
                {
                    SetError("Failed to open device");
                    return;
                }

                _device = opened;
                _interfaceClaimed = openedClaimed;
                _openBusNumber = openedKey.bus;
                _openAddress = openedKey.addr;
                _openDeviceSerial = deviceInfo.Serial;
                _selectedDeviceInfo = deviceInfo;
                _lastSelectedSerial = deviceInfo.Serial;

                _deviceLost = false;
                _consecutiveIoErrors = 0;
                Interlocked.Increment(ref _generation);
                _isConnected = true;
                _errorMessage = null; // reported with Connected

                StartNotifyListener(opened);
                Connected?.Invoke(this, deviceInfo);
            }
            catch (Exception ex)
            {
                SetError($"Error: {ex.Message}");
                if (_device != null)
                {
                    if (_interfaceClaimed)
                        try { _device.ReleaseInterface(VendorInterfaceNumber); } catch { }
                    try { _device.Close(); } catch { }
                }
                _device = null;
                _interfaceClaimed = false;
                _openBusNumber = 0;
                _openAddress = 0;
                _openDeviceSerial = null;
            }
        }
    }

    /// <summary>
    /// Switch to a different connected device. Called from ViewModel after unsaved changes check.
    /// </summary>
    public void SelectDevice(DSPiDeviceInfo device)
    {
        if (device.Serial == _openDeviceSerial && _isConnected) return;
        // Hold the scan guard for the switch. OpenDevice drops the old device
        // first and takes tens of ms to probe the candidates; a timer scan in
        // that window saw no device open and reopened the last selected one,
        // the old one, undoing the switch (and its brief probe-opens could
        // collide with ours). Called off the UI thread, so the wait is fine.
        while (Interlocked.CompareExchange(ref _scanActive, 1, 0) != 0)
        {
            if (_disposed) return;
            Thread.Sleep(10);
        }
        try
        {
            _lastSelectedSerial = device.Serial;
            OpenDevice(device);
        }
        finally
        {
            Interlocked.Exchange(ref _scanActive, 0);
        }
    }

    private void HandleDisconnect()
    {
        StopNotifyListener();

        var wasConnected = _isConnected;

        if (_device != null)
        {
            if (_interfaceClaimed)
                try { _device.ReleaseInterface(VendorInterfaceNumber); } catch { }
            try { _device.Close(); } catch { }
        }
        _device = null;
        _interfaceClaimed = false;
        _openBusNumber = 0;
        _openAddress = 0;
        _openDeviceSerial = null;

        _isConnected = false;

        if (wasConnected)
        {
            _errorMessage = "Disconnected"; // reported with Disconnected
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            HandleDisconnect();
        }
    }

    public void Reconnect()
    {
        lock (_lock)
        {
            HandleDisconnect();
        }
        ScanDevices();
    }

    // ── Transfers ──

    public LinkStatus ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_device == null) return LinkStatus.NoDevice;
            Interlocked.Exchange(ref _lastTransferTicks, Environment.TickCount64);

            var buffer = data.ToArray();
            var setupPacket = new UsbSetupPacket(RequestTypeOut, request, value, index, buffer.Length);

            try
            {
                int transferred = _device.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
                if (transferred < 0) return Fail((Error)transferred);
                _consecutiveIoErrors = 0;
                return LinkStatus.Ok;
            }
            catch (UsbException ex)
            {
                return Fail(ex.ErrorCode);
            }
            catch
            {
                // libusb / LibUsbDotNet throws on stall, device disappearance
                // mid-transfer, NAK timeout, etc. Surface those as a failed
                // transfer rather than unwinding through an async void handler
                // and killing the process.
                return LinkStatus.Error;
            }
        }
    }

    public LinkStatus ControlIn(byte request, ushort value, ushort index, int length, out byte[] data)
    {
        data = Array.Empty<byte>();
        lock (_lock)
        {
            if (_device == null) return LinkStatus.NoDevice;
            Interlocked.Exchange(ref _lastTransferTicks, Environment.TickCount64);

            var setupPacket = new UsbSetupPacket(RequestTypeIn, request, value, index, length);
            var buffer = new byte[length];
            int transferred;
            try
            {
                transferred = _device.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
            }
            catch (UsbException ex)
            {
                return Fail(ex.ErrorCode);
            }
            catch
            {
                // See ControlOut for the rationale.
                return LinkStatus.Error;
            }
            if (transferred < 0) return Fail((Error)transferred);
            _consecutiveIoErrors = 0;

            if (transferred < length) Array.Resize(ref buffer, transferred);
            data = buffer;
            return LinkStatus.Ok;
        }
    }

    /// <summary>Notes the error and maps it onto the Link status table (spec
    /// 9.1): a stall is ERROR, a timeout TIMEOUT, a vanished device NO_DEVICE.</summary>
    private LinkStatus Fail(Error err)
    {
        NoteTransferError(err);
        return err switch
        {
            Error.Timeout => LinkStatus.Timeout,
            Error.NoDevice or Error.NotFound => LinkStatus.NoDevice,
            _ => LinkStatus.Error,
        };
    }

    /// <summary>The transfer lock is reentrant, so the body's own transfers
    /// run inside it while every other thread's wait.</summary>
    public T Exclusive<T>(Func<T> body)
    {
        lock (_lock) return body();
    }

    // ── Notification endpoint (bulk IN EP 0x83, V7+ firmware) ──

    /// <summary>
    /// Open EP 0x83 (bulk IN, 64-byte packets) and start a background reader.
    /// The firmware always keeps this endpoint armed with a 1-byte IDLE
    /// keep-alive when nothing is pending, so reads return promptly.
    /// </summary>
    private void StartNotifyListener(IUsbDevice dev)
    {
        try
        {
            _notifyReader = dev.OpenEndpointReader(ReadEndpointID.Ep03, NotifyPacketSize, EndpointType.Bulk);
        }
        catch
        {
            _notifyReader = null;
            return;
        }

        _notifyStop = false;
        _notifyThread = new Thread(NotifyReadLoop)
        {
            IsBackground = true,
            Name = "DSPi notify"
        };
        _notifyThread.Start();
    }

    private void StopNotifyListener()
    {
        _notifyStop = true;
        // Closing the device handle (in HandleDisconnect, right after this call)
        // will cause any pending Read() to error out and the loop to exit. We
        // join with a short timeout so a stuck thread doesn't block disconnect.
        var thread = _notifyThread;
        _notifyThread = null;
        _notifyReader = null;
        if (thread != null && thread.IsAlive)
        {
            try { thread.Join(500); } catch { }
        }
    }

    private void NotifyReadLoop()
    {
        var buf = new byte[NotifyPacketSize];
        int failures = 0;
        while (!_notifyStop)
        {
            UsbEndpointReader? reader = _notifyReader;
            if (reader == null) break;

            int len;
            Error err;
            try
            {
                err = reader.Read(buf, 1000, out len);
            }
            catch
            {
                // Closed underneath us: by us (a stop), or the handle died.
                if (!_notifyStop) _deviceLost = true;
                break;
            }

            if (_notifyStop) break;

            if (err == Error.Timeout || err == Error.Interrupted)
                continue;
            if (err == Error.NoDevice || err == Error.NotFound)
            {
                _deviceLost = true;   // the next scan reopens it (see _deviceLost)
                break;
            }
            if (err != Error.Success)
            {
                // A dead handle can fail every read at once (Io, Pipe,
                // Overflow): back off rather than spin, and after a run of
                // them treat the handle as lost.
                if (++failures >= NotifyFailuresBeforeLost)
                {
                    _deviceLost = true;
                    break;
                }
                Thread.Sleep(50);
                continue;
            }
            failures = 0;
            if (len <= 0)
                continue;

            // Every packet, IDLE keep-alives and v1 included (the Interrupt
            // Monitor shows them). We are the only host on this link, so a
            // host-sourced change is our own write.
            var packet = buf.AsSpan(0, len).ToArray();
            var origin = LinkNotifyAttribution.IsHostSourced(packet) ? LocalSession : LinkNotification.Unattributed;
            try { NotificationReceived?.Invoke(this, new LinkNotification(packet, origin, DateTime.Now)); }
            catch { /* a misbehaving subscriber must not break the reader */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Dispose();
        Disconnect();
        _context.Dispose();
    }
}

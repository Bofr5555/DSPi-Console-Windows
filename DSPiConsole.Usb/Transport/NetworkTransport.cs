using System.Threading.Channels;
using DSPiConsole.Core.Link;

namespace DSPiConsole.Usb.Transport;

/// <summary>What the pairing prompt is told: which hub, and how it words the
/// request (spec 6.2: from pin_source).</summary>
public sealed record LinkPairingRequest(string HubId, string HubName, string PinSource, bool RetryAfterWrongPin);

/// <summary>
/// An <see cref="IDspTransport"/> over a DSPi Link client connection: the view
/// model drives a device on another machine's hub exactly as it drives a local
/// one. Bound to one remote device by serial (its handle is resolved from the
/// hub's inventory); commands tunnel as CMD frames, notifications arrive with
/// the hub's session attribution, a resync or a gap becomes the bulk re-read
/// the view model already knows, and the bulk read uses the hub's snapshot
/// when offered. Reconnects with backoff after a failure.
///
/// Sends are paced to the hub's max_inflight and retried on RATE_LIMITED or
/// BUSY (plan N7, which the Mac lacks). A GET asked on the UI thread never
/// waits long: with no ready session it fails at once, otherwise it waits at
/// most a second (the Mac's 0adb7f1). Port of the macOS Console's
/// NetworkTransport, to spec 1.2.
/// </summary>
public sealed class NetworkTransport : IDspTransport
{
    /// <summary>The handle shown while the bound device isn't in the hub's
    /// inventory (the hub unreachable, the session restarting).</summary>
    public const byte PlaceholderHandle = 255;

    private readonly LinkClient _client;
    private readonly LinkTokenStore _tokens;
    private readonly string _clientName;
    private readonly Func<bool> _isUiThread;
    private readonly object _lock = new();

    public string HubId { get; }
    public string HubName { get; private set; }

    private LinkEndpoint? _endpoint;
    private string? _boundSerial;
    private byte? _boundHandle;
    private byte? BoundHandle { get { lock (_lock) return _boundHandle; } }
    private string? _boundName;
    private bool _wantConnection;
    private int _reconnectAttempt;
    private CancellationTokenSource? _reconnectDelay;
    private bool _connected;
    private string? _errorMessage;
    private ulong _generation;
    private IReadOnlyList<DSPiDeviceInfo> _devices = Array.Empty<DSPiDeviceInfo>();
    private DSPiDeviceInfo? _selected;

    private bool _pairingPromptShowing;
    private string? _pendingPin;
    private ushort? _lastNotifyTag;
    private byte? _lastFirmwareSeq;

    /// <summary>Longest a UI-thread GET is held (the Mac's mainThreadWait).</summary>
    public TimeSpan UiThreadWait { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Longest any GET waits; the client times commands out sooner.</summary>
    public TimeSpan BackgroundWait { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Retries of a command the hub answered RATE_LIMITED or BUSY.</summary>
    public int MaxRetries { get; init; } = 5;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(30);

    /// <summary>Asks the user for the hub's PIN; null to give up. One prompt at
    /// a time. Unset, a hub that wants pairing reports it as an error.</summary>
    public Func<LinkPairingRequest, Task<string?>>? PairingPrompt { get; set; }

    /// <summary>The connection failed: the owner may ask discovery whether the
    /// hub has moved.</summary>
    public event EventHandler? ConnectionFailed;

    public NetworkTransport(LinkClient client, string hubId, string hubName, LinkTokenStore tokens,
        string clientName, Func<bool>? isUiThread = null)
    {
        _client = client;
        HubId = hubId.ToLowerInvariant();
        HubName = hubName;
        _tokens = tokens;
        _clientName = clientName;
        _isUiThread = isUiThread ?? (() => false);
        _pump = Task.Run(PumpAsync);

        _client.StateChanged += (_, s) => OnClientState(s);
        _client.DevicesChanged += (_, d) => OnRemoteDevices(d);
        _client.Notified += (_, f) => OnNotify(f);
        _client.Resynced += (_, f) =>
        {
            // "Dropped notifications" is about this session, whichever handle
            // the hub stamps; the others are about one device.
            if (f.EffectiveReason == LinkResyncReason.NotificationsDropped || f.Handle == BoundHandle) PublishInvalidate();
        };
    }

    public LinkClient Client => _client;

    // ── Connection ──

    /// <summary>Opens (or re-opens) the connection to the hub at <paramref name="endpoint"/>.</summary>
    public void Connect(LinkEndpoint endpoint)
    {
        lock (_lock)
        {
            _endpoint = endpoint;
            _wantConnection = true;
            _reconnectAttempt = 0;
            CancelReconnectLocked();
        }
        Dial(endpoint);
    }

    private void Dial(LinkEndpoint endpoint) =>
        _ = _client.ConnectAsync(endpoint, _clientName, _tokens.Token(HubId));

    /// <summary>
    /// Discovery listed the hub at <paramref name="endpoint"/>; <paramref name="reappeared"/>
    /// says it was absent before. A session that is up is left alone (a
    /// dual-stack hub resolves to another family from one probe to the next).
    /// A session that is down is dialled now only on news: a new address, or
    /// the hub back while a backoff wait is pending. The backoff isn't reset,
    /// so browse traffic can't hammer a hub that keeps refusing.
    /// </summary>
    public void HubSeen(LinkEndpoint endpoint, bool reappeared, string? hubName = null)
    {
        LinkEndpoint? dial = null;
        lock (_lock)
        {
            if (hubName is { Length: > 0 }) HubName = hubName;
            bool moved = _endpoint != endpoint;
            _endpoint = endpoint;
            if (!_wantConnection || _client.State.Phase == LinkClientPhase.Ready) return;
            if (!(moved || (reappeared && _reconnectDelay != null))) return;
            CancelReconnectLocked();
            dial = endpoint;
        }
        Dial(dial);
    }

    /// <summary>True when no session exists or the last one failed.</summary>
    public bool IsSessionDown => _client.State.Phase is LinkClientPhase.Disconnected or LinkClientPhase.Failed;

    /// <summary>Binds to one of the hub's devices by serial; the handle is
    /// resolved when the inventory has it. Selecting again after the user
    /// cancelled pairing (which gave the connection up) retries.</summary>
    public void Bind(string serial)
    {
        LinkEndpoint? dial = null;
        lock (_lock)
        {
            if (serial != _boundSerial) _boundName = null;
            _boundSerial = serial;
            ResolveBindingLocked();
            if (!_wantConnection && _endpoint != null)
            {
                _wantConnection = true;
                _reconnectAttempt = 0;
                dial = _endpoint;
            }
        }
        SelectedDeviceChanged?.Invoke(this, EventArgs.Empty);
        UpdateConnected();
        if (dial != null) Dial(dial);
    }

    /// <summary>The bound device stays selected while the inventory lacks it:
    /// a placeholder keeps the picker on the device the user chose.</summary>
    private void ResolveBindingLocked()
    {
        if (_boundSerial == null)
        {
            _boundHandle = null;
            _selected = null;
            return;
        }
        var info = _client.Devices.FirstOrDefault(d => d.Serial == _boundSerial);
        if (info == null)
        {
            _boundHandle = null;
            _selected = Device(_boundSerial, PlaceholderHandle, _boundName);
            return;
        }
        _boundHandle = (byte)info.Handle;
        _boundName = info.Name;
        _selected = Device(info.Serial, (byte)info.Handle, info.Name);
    }

    /// <summary>A remote device as the picker lists it.</summary>
    private DSPiDeviceInfo Device(string serial, byte handle, string? name) =>
        new(serial, $"dspilink://{HubId}/{handle}")
        {
            Kind = DeviceLinkKind.Remote,
            HubId = HubId,
            HubName = HubName,
            RemoteName = name,
        };

    private void OnClientState(LinkClientState state)
    {
        switch (state.Phase)
        {
            case LinkClientPhase.Ready:
                lock (_lock)
                {
                    _reconnectAttempt = 0;
                    _generation++;
                    _lastNotifyTag = null;
                    _lastFirmwareSeq = null;
                    _pendingPin = null; // nothing left to pair
                }
                SetError(null);
                break;

            case LinkClientPhase.AwaitingAuth when state.NeedsPairing:
                if (state.TokenRefused)
                {
                    // Revoked, or from before the hub was reset: resending it
                    // would only count toward the hub's failure limit.
                    _tokens.RemoveToken(HubId);
                    LinkLog.Write($"remote {HubName}: stored token refused, will pair again");
                }
                string? pin;
                lock (_lock) { pin = _pendingPin; _pendingPin = null; }
                if (pin != null) _ = SendPinAsync(pin);
                else _ = PairAsync(retry: false);
                break;

            case LinkClientPhase.Failed:
                SetError(state.Reason);
                switch (state.CloseCode)
                {
                    case LinkCloseCode.ProtocolError:
                        // The same client would fail the same way: stop.
                        lock (_lock) _wantConnection = false;
                        break;
                    case LinkCloseCode.TokenRevoked:
                        // Pair again on the next connection.
                        _tokens.RemoveToken(HubId);
                        LinkLog.Write($"remote {HubName}: token revoked, will pair again");
                        ScheduleReconnect();
                        break;
                    default:
                        ScheduleReconnect();
                        break;
                }
                UpdateConnected();
                ConnectionFailed?.Invoke(this, EventArgs.Empty);
                break;
        }
        UpdateConnected();
    }

    private async Task PairAsync(bool retry)
    {
        var prompt = PairingPrompt;
        if (prompt == null)
        {
            SetError("This hub needs pairing.");
            return;
        }
        lock (_lock)
        {
            if (_pairingPromptShowing) return;
            _pairingPromptShowing = true;
        }
        string? pin;
        try
        {
            pin = await prompt(new LinkPairingRequest(HubId, HubName,
                _client.Hello?.PinSource ?? LinkPinSource.Window, retry)).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _pairingPromptShowing = false;
        }

        if (pin == null)
        {
            LinkLog.Write($"remote {HubName}: pairing cancelled");
            // Cancelled: give the connection up; selecting the device again retries.
            lock (_lock)
            {
                _wantConnection = false;
                CancelReconnectLocked();
            }
            _client.Disconnect();
            return;
        }
        // The hub allows 30 s to authenticate; a slower answer finds the socket
        // closed and a reconnect under way. Hold the PIN for it, unless the
        // session came up without one meanwhile (nothing to pair).
        var phase = _client.State.Phase;
        if (phase == LinkClientPhase.Ready) return;
        if (phase != LinkClientPhase.AwaitingAuth)
        {
            lock (_lock) _pendingPin = pin;
            return;
        }
        await SendPinAsync(pin).ConfigureAwait(false);
    }

    private async Task SendPinAsync(string pin)
    {
        try
        {
            var token = await _client.PairAsync(pin, _clientName, LinkRole.Control).ConfigureAwait(false);
            // An open hub grants a session without a token.
            if (token.Length > 0) _tokens.SetToken(HubId, token);
            LinkLog.Write($"remote {HubName}: paired");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Paired, but the token couldn't be stored: next time asks again.
            LinkLog.Write($"remote {HubName}: paired, token not stored ({e.Message})");
        }
        catch (LinkRequestException e)
        {
            SetError("Pairing failed: " + (e.Message is { Length: > 0 } m ? m : e.Code));
            LinkLog.Write($"remote {HubName}: pairing refused ({e.Code})");
            // A wrong PIN leaves the hub waiting: ask again.
            if (e.Code is not (LinkRequestException.NotConnected or LinkRequestException.Timeout))
                await PairAsync(retry: true).ConfigureAwait(false);
        }
    }

    /// <summary>1 s, 2 s, 4 s … capped at 30 s (spec 5).</summary>
    private void ScheduleReconnect()
    {
        LinkEndpoint endpoint;
        TimeSpan delay;
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (!_wantConnection || _endpoint == null) return;
            endpoint = _endpoint;
            delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _reconnectAttempt)));
            LinkLog.Write($"remote {HubName}: reconnecting in {delay.TotalSeconds:0} s");
            _reconnectAttempt++;
            CancelReconnectLocked();
            cts = _reconnectDelay = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            LinkEndpoint? dial;
            lock (_lock)
            {
                if (!_wantConnection || _reconnectDelay != cts) return;
                _reconnectDelay = null;
                dial = _endpoint;
            }
            if (dial != null) Dial(dial);
        });
    }

    private void CancelReconnectLocked()
    {
        _reconnectDelay?.Cancel();
        _reconnectDelay = null;
    }

    private void OnRemoteDevices(IReadOnlyList<LinkDeviceInfo> infos)
    {
        lock (_lock)
        {
            _devices = infos.Select(i => Device(i.Serial, (byte)i.Handle, i.Name)).ToArray();
            ResolveBindingLocked();
        }
        AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
        UpdateConnected();
    }

    /// <summary>Connected: a ready session and the bound device online on the hub.</summary>
    /// <summary>Held while a connection change is worked out and raised, so
    /// two threads can't deliver Connected and Disconnected out of order.</summary>
    private readonly object _connectedRaise = new();

    private void UpdateConnected()
    {
        lock (_connectedRaise)
        {
            bool now;
            DSPiDeviceInfo? device;
            lock (_lock)
            {
                var info = _boundHandle is { } h ? _client.Devices.FirstOrDefault(d => d.Handle == h) : null;
                now = _client.State.Phase == LinkClientPhase.Ready && info != null
                      && info.State is not (LinkDeviceState.Offline or LinkDeviceState.Unknown);
                if (now == _connected) return;
                _connected = now;
                device = _selected;
            }
            if (now && device != null) Connected?.Invoke(this, device);
            else if (!now) Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetError(string? message)
    {
        lock (_lock)
        {
            if (_errorMessage == message) return;
            _errorMessage = message;
        }
        ErrorMessageChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Notifications ──

    private void OnNotify(LinkNotifyFrame frame)
    {
        bool gap, ours;
        lock (_lock)
        {
            // The Link tag counts this session's notifications for every device
            // on the hub (spec 8.3); the packet's own seq (byte 3) counts one
            // device's. A jump in either means something was lost, and the
            // bound device's state must be re-read.
            ours = frame.Handle == _boundHandle;
            gap = _lastNotifyTag is { } t && frame.Tag != (ushort)(t + 1);
            _lastNotifyTag = frame.Tag;
            if (ours)
            {
                gap |= frame.Packet.Length > 3 && _lastFirmwareSeq is { } s && frame.Packet[3] != (byte)(s + 1);
                if (frame.Packet.Length > 3) _lastFirmwareSeq = frame.Packet[3];
            }
        }
        if (!ours)
        {
            if (gap) PublishInvalidate();
            return;
        }
        NotificationReceived?.Invoke(this, new LinkNotification(frame.Packet, frame.Origin, DateTime.Now));
        if (gap) PublishInvalidate();
    }

    /// <summary>v2 BULK_INVALIDATED with source UNKNOWN: the view model re-reads
    /// everything on it, which is the whole recovery from a resync or a gap.</summary>
    private void PublishInvalidate() =>
        NotificationReceived?.Invoke(this, new LinkNotification([0x02, 0x03, 0, 0, 0, 0, 0, 0], LinkNotification.Unattributed, DateTime.Now));

    // ── IDspTransport ──

    public ushort Session => _client.SessionId;
    public ulong Generation { get { lock (_lock) return _generation; } }
    public bool IsConnected { get { lock (_lock) return _connected; } }
    public string? ErrorMessage { get { lock (_lock) return _errorMessage; } }
    public DSPiDeviceInfo? SelectedDevice { get { lock (_lock) return _selected; } }
    public IReadOnlyList<DSPiDeviceInfo> AvailableDevices { get { lock (_lock) return _devices; } }

    public event EventHandler<DSPiDeviceInfo>? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler? AvailableDevicesChanged;
    public event EventHandler? ErrorMessageChanged;
    public event EventHandler? SelectedDeviceChanged;
    public event EventHandler<LinkNotification>? NotificationReceived;

    public void Start() { }
    public void Stop() => Disconnect();

    public void SelectDevice(DSPiDeviceInfo device) => Bind(device.Serial);

    public void Reconnect()
    {
        LinkEndpoint? endpoint;
        lock (_lock) endpoint = _endpoint;
        if (endpoint != null) Connect(endpoint);
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _wantConnection = false;
            _pendingPin = null;
            CancelReconnectLocked();
        }
        _client.Disconnect();
    }

    public void MarkDisconnected()
    {
        lock (_lock)
        {
            if (!_wantConnection) return;
        }
        _client.Disconnect();
        UpdateConnected();
        ScheduleReconnect();
    }

    /// <summary>A SET goes out in order and isn't waited for (as on the Mac):
    /// the view model has already applied the value. A refusal shows only as
    /// the lock notice; the hub's notifications keep the UI honest.</summary>
    public LinkStatus ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data)
    {
        if (BoundHandle is not { } handle || _client.State.Phase != LinkClientPhase.Ready) return LinkStatus.NoDevice;
        var req = new LinkCmdRequest(0, handle, LinkDirection.Set, request, value, index, payload: data.ToArray());
        _ = Submit(req);
        return LinkStatus.Ok;
    }

    public LinkStatus ControlIn(byte request, ushort value, ushort index, int length, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (BoundHandle is not { } handle) return LinkStatus.NoDevice;
        bool ui = _isUiThread();
        // A UI-thread caller can't be left waiting on the network.
        if (ui && _client.State.Phase != LinkClientPhase.Ready) return LinkStatus.NoDevice;

        var req = new LinkCmdRequest(0, handle, LinkDirection.Get, request, value, index, (ushort)Math.Clamp(length, 0, ushort.MaxValue));
        var task = Submit(req);
        if (!task.Wait(ui ? UiThreadWait : BackgroundWait)) return LinkStatus.Timeout;
        var response = task.Result;
        if (response.Status != LinkStatus.Ok) return response.Status;
        data = response.Payload;
        return LinkStatus.Ok;
    }

    /// <summary>No chunked sessions over Link: the snapshot when the hub keeps
    /// one, else one REQ_GET_ALL_PARAMS (0xA0) for the whole blob.</summary>
    public byte[]? ReadBulkParams()
    {
        if (BoundHandle is not { } handle) return null;
        // Never a long wait on the UI thread (the sync runs off it anyway).
        bool ui = _isUiThread();
        if (ui && _client.State.Phase != LinkClientPhase.Ready) return null;
        if (_client.Supports(LinkCapability.Snapshot))
        {
            try
            {
                var snapshot = _client.SnapshotAsync(handle);
                if (snapshot.Wait(ui ? UiThreadWait : BackgroundWait) && BulkRead.IsComplete(snapshot.Result.Bulk))
                    return snapshot.Result.Bulk;
            }
            catch (AggregateException) { /* fall through to a direct read */ }
            catch (System.Text.Json.JsonException) { }
        }
        return ControlIn(VendorCommands.GetAllParams, 0, 2, BulkRead.SingleShotLength, out var blob) == LinkStatus.Ok
               && BulkRead.IsComplete(blob)
            ? blob
            : null;
    }

    public T Exclusive<T>(Func<T> body) => body();

    /// <summary>Skipped when the hub has no lock capability (it serialises
    /// commands itself, spec 7.7). Any other failure fails the flow rather
    /// than letting it run unlocked.</summary>
    public bool AcquireDeviceLock(string reason, TimeSpan timeout)
    {
        LastLockRefused = false;
        if (!_client.Supports(LinkCapability.Lock)) return true;
        if (BoundHandle is not { } handle) return false;
        // A release still waiting to go out must go first, or the hub would
        // take back the lock about to be granted.
        Task? release;
        lock (_lock) release = _pendingRelease;
        release?.Wait(BackgroundWait);

        var t = _client.AcquireLockAsync(handle, reason, timeout < LinkLockNotice.MaxTimeout ? timeout : LinkLockNotice.MaxTimeout);
        try
        {
            if (t.Wait(BackgroundWait)) return true;
            // Gave up waiting: a grant that lands later is handed straight back.
            t.ContinueWith(done => { if (done.IsCompletedSuccessfully) _client.ReleaseLock(handle); }, TaskScheduler.Default);
        }
        catch (AggregateException e) when (e.InnerException is LinkRequestException { Code: LinkErrorCode.Locked })
        {
            // Another console holds the device: the one refusal the notice is for.
            LastLockRefused = true;
            LinkLockNotice.Post();
        }
        catch (AggregateException) { }
        return false;
    }

    /// <summary>The last failed acquire was because another console holds the
    /// device (not a dropped link or a viewer's role).</summary>
    public bool LastLockRefused { get; private set; }

    private Task? _pendingRelease;

    /// <summary>Doesn't wait: the release goes out once the flow's queued
    /// commands have (so it can't overtake them), and a lost one expires on
    /// the hub. The next acquire waits for it.</summary>
    public void ReleaseDeviceLock()
    {
        if (!_client.Supports(LinkCapability.Lock) || BoundHandle is not { } handle) return;
        var release = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + BackgroundWait;
            while (Volatile.Read(ref _unfinished) > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10).ConfigureAwait(false);
            _client.ReleaseLock(handle);
        });
        lock (_lock) _pendingRelease = release;
    }

    /// <summary>Commands queued or in flight.</summary>
    private int _unfinished;

    // ── Pacing ──

    private sealed record Pending(LinkCmdRequest Request, TaskCompletionSource<LinkCmdResponse> Done, long Seq);

    /// <summary>The newest SET submitted per (bRequest, wValue, wIndex).</summary>
    private readonly Dictionary<(byte, ushort, ushort), long> _newestSet = new();
    private long _seq;
    /// <summary>Retries waiting to go out again: the pump sends nothing new
    /// meanwhile, so later commands don't overtake them.</summary>
    private int _retrying;

    private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private readonly object _pumpLock = new();
    private int _inflight;
    private TaskCompletionSource _slotFreed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task<LinkCmdResponse> Submit(LinkCmdRequest request)
    {
        var done = new TaskCompletionSource<LinkCmdResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _unfinished);
        done.Task.ContinueWith(_ => Interlocked.Decrement(ref _unfinished), TaskScheduler.Default);
        long seq = Interlocked.Increment(ref _seq);
        if (request.Direction == LinkDirection.Set)
            lock (_pumpLock) _newestSet[(request.BRequest, request.WValue, request.WIndex)] = seq;
        if (!_queue.Writer.TryWrite(new Pending(request, done, seq)))
            done.TrySetResult(new LinkCmdResponse(0, LinkStatus.NoDevice));
        return done.Task;
    }

    /// <summary>Sends queued commands in order, never more at once than the
    /// hub's max_inflight; one the hub refuses as RATE_LIMITED or BUSY is
    /// retried after a short wait.</summary>
    private async Task PumpAsync()
    {
        await foreach (var pending in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            int limit = Math.Max(1, _client.Limits?.MaxInflight ?? 8);
            while (true)
            {
                Task freed;
                lock (_pumpLock)
                {
                    if (_inflight < limit && _retrying == 0)
                    {
                        _inflight++;
                        break;
                    }
                    freed = _slotFreed.Task;
                }
                await freed.ConfigureAwait(false);
            }
            _ = RunAsync(pending);
        }
    }

    private async Task RunAsync(Pending pending)
    {
        try
        {
            LinkCmdResponse response = new(0, LinkStatus.NoDevice);
            bool held = false;
            try
            {
                for (int attempt = 0; attempt <= MaxRetries; attempt++)
                {
                    response = await _client.CommandAsync(pending.Request).ConfigureAwait(false);
                    if (response.Status is not (LinkStatus.RateLimited or LinkStatus.Busy)) break;
                    if (pending.Request.Direction == LinkDirection.Set && Superseded(pending))
                    {
                        // A newer value for the same parameter is already on its
                        // way: resending this one would only land after it.
                        response = new LinkCmdResponse(pending.Request.Tag, LinkStatus.Ok);
                        break;
                    }
                    if (!held)
                    {
                        lock (_pumpLock) _retrying++;
                        held = true;
                    }
                    await Task.Delay(RetryDelay * (attempt + 1)).ConfigureAwait(false);
                }
            }
            finally
            {
                if (held)
                {
                    TaskCompletionSource resume;
                    lock (_pumpLock)
                    {
                        _retrying--;
                        resume = _slotFreed;
                        _slotFreed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    resume.TrySetResult();
                }
            }
            if (response.Status == LinkStatus.Locked) LinkLockNotice.Post();
            pending.Done.TrySetResult(response);
        }
        catch (Exception)
        {
            pending.Done.TrySetResult(new LinkCmdResponse(0, LinkStatus.NoDevice));
        }
        finally
        {
            TaskCompletionSource freed;
            lock (_pumpLock)
            {
                _inflight--;
                freed = _slotFreed;
                _slotFreed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            freed.TrySetResult();
        }
    }

    private bool Superseded(Pending pending)
    {
        lock (_pumpLock)
            return _newestSet.TryGetValue((pending.Request.BRequest, pending.Request.WValue, pending.Request.WIndex), out var newest)
                   && newest != pending.Seq;
    }

    public void Dispose()
    {
        Disconnect();
        _queue.Writer.TryComplete();
        _client.Dispose();
    }
}

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DSPiConsole.Core.Link;

public enum LinkClientPhase
{
    Disconnected,
    Connecting,
    /// <summary>The hub answered hello and wants authentication.</summary>
    AwaitingAuth,
    Ready,
    /// <summary>The hub closed the session, or the socket failed.</summary>
    Failed,
}

/// <summary>Where a client connection stands.</summary>
/// <param name="TokenRefused">With NeedsPairing: the hub refused the stored
/// token (revoked, or from before a reset), so it should be forgotten.</param>
public sealed record LinkClientState(LinkClientPhase Phase, bool NeedsPairing = false, string? Reason = null, int? CloseCode = null,
    bool TokenRefused = false)
{
    public static LinkClientState Disconnected { get; } = new(LinkClientPhase.Disconnected);
    public static LinkClientState Connecting { get; } = new(LinkClientPhase.Connecting);
    public static LinkClientState Ready { get; } = new(LinkClientPhase.Ready);
}

/// <summary>A JSON request the hub refused, timed out, or couldn't be sent.</summary>
public sealed class LinkRequestException(string code, string? message) : Exception(message ?? code)
{
    /// <summary>The err code (spec 7.1), or "not_connected" / "timeout" /
    /// "protocol" for failures on this side.</summary>
    public string Code { get; } = code;

    public const string NotConnected = "not_connected";
    public const string Timeout = "timeout";
    public const string Protocol = "protocol";
}

/// <summary>Where to reach a hub. <see cref="Address"/>, when known, is dialled
/// directly (a link-local IPv6 address keeps its scope id); otherwise
/// <see cref="Host"/> is resolved by the OS.</summary>
public sealed record LinkEndpoint(string Host, int Port = LinkService.DefaultPort, string Path = LinkService.DefaultPath,
    bool Tls = false, IPAddress? Address = null)
{
    public Uri WebSocketUri => new($"{(Tls ? "wss" : "ws")}://{UriHost}:{Port}{(Path.StartsWith('/') ? Path : "/" + Path)}");

    /// <summary>The host as a URI writes it: an IPv6 literal in brackets and
    /// without its zone (a zone in a URI is mangled; the scoped address is
    /// dialled through <see cref="Address"/> instead).</summary>
    private string UriHost => IPAddress.TryParse(Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
        ? $"[{new IPAddress(ip.GetAddressBytes())}]"
        : Host;
}

/// <summary>
/// The client half of DSPi Link: one WebSocket to one hub, the session
/// lifecycle of spec 5, the control plane of 7 and the data plane of 8. It
/// owns no reconnect policy (the transport above decides when to connect
/// again) but is reusable: a connect after a failure starts a clean session.
///
/// Requests may be made from any thread. Events are raised on a thread-pool
/// thread, never under the client's lock; subscribers marshal as they need.
/// A receive is always pending while connected, which is what lets .NET answer
/// the hub's pings (and time out a silent hub with the keepalive below).
/// Port of the macOS Console's LinkClient, to spec 1.2.
/// </summary>
public sealed class LinkClient : IDisposable
{
    /// <summary>PIN sent by the courtesy pair in open mode; the hub ignores it.</summary>
    private const string OpenAccessPin = "000000";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Bulk transfers: the hub waits 5 s for the device, and the blob
    /// is thousands of bytes behind that.</summary>
    public TimeSpan BulkTimeout { get; init; } = TimeSpan.FromSeconds(15);
    private static readonly HashSet<byte> BulkOpcodes = [0xA0, 0xA1, 0xA2, 0xA3];

    /// <summary>The largest message accepted from a hub (its own max_frame is
    /// at least 16384; a snapshot carries the base64 bulk blob).</summary>
    private const int MaxInbound = 1 << 20;

    private readonly Func<LinkEndpoint, CancellationToken, Task<WebSocket>> _open;
    private readonly object _lock = new();
    private readonly string _appVersion;

    private int _generation;
    /// <summary>The generation already shut down: the receive loop's own
    /// failure after a disconnect must not publish over it.</summary>
    private int _closedGeneration;

    /// <summary>The session is the current one and hasn't been shut down. Lock held.</summary>
    private bool IsLiveLocked(int generation) => generation == _generation && generation != _closedGeneration;
    private WebSocket? _socket;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private LinkClientState _state = LinkClientState.Disconnected;

    private string _clientName = "DSPi Console";
    private string? _pendingToken;
    private bool _ready;

    private LinkHelloHub? _hello;
    private ushort _session;
    private LinkRole? _role;
    private List<LinkDeviceInfo> _inventory = new();

    private long _nextRequestId = 1;
    private ushort _nextTag = 1;
    private readonly Dictionary<long, TaskCompletionSource<LinkOk>> _pendingJson = new();
    private readonly Dictionary<ushort, TaskCompletionSource<LinkCmdResponse>> _pendingCommands = new();

    /// <param name="open">Opens the WebSocket (with the dspi-link-1
    /// sub-protocol). Tests pass an in-memory hub; null uses
    /// <see cref="OpenClientWebSocketAsync"/>.</param>
    public LinkClient(Func<LinkEndpoint, CancellationToken, Task<WebSocket>>? open = null, string? appVersion = null)
    {
        _open = open ?? OpenClientWebSocketAsync;
        _appVersion = appVersion ?? "0";
    }

    // ── State ──

    public LinkClientState State { get { lock (_lock) return _state; } }
    public LinkHelloHub? Hello { get { lock (_lock) return _hello; } }
    public LinkHubInfo? Hub => Hello?.Hub;
    public IReadOnlyList<string> Capabilities => Hello?.Caps ?? Array.Empty<string>();
    public LinkLimits? Limits => Hello?.Limits;
    public bool Supports(string capability) => Hello?.Supports(capability) ?? false;

    /// <summary>This connection's session id, 0 until known: in open mode a
    /// hub that refused the courtesy pair never tells us (spec 6.1), and our
    /// own echoes can't then be told apart.</summary>
    public ushort SessionId { get { lock (_lock) return _session; } }
    public LinkRole? Role { get { lock (_lock) return _role; } }

    /// <summary>The hub's inventory, kept current from the device.* events.</summary>
    public IReadOnlyList<LinkDeviceInfo> Devices { get { lock (_lock) return _inventory.ToArray(); } }

    public event EventHandler<LinkClientState>? StateChanged;
    public event EventHandler<IReadOnlyList<LinkDeviceInfo>>? DevicesChanged;
    public event EventHandler<LinkNotifyFrame>? Notified;
    public event EventHandler<LinkPollFrame>? Polled;
    public event EventHandler<LinkResyncFrame>? Resynced;

    // ── Connect and disconnect ──

    /// <summary>
    /// Opens the socket and says hello; returns once the socket is open (or
    /// failed). Authentication then runs as the hub answers: with
    /// <paramref name="token"/> when the hub wants a PIN, else the state
    /// settles on AwaitingAuth with NeedsPairing and the caller pairs.
    /// </summary>
    public async Task ConnectAsync(LinkEndpoint endpoint, string clientName, string? token)
    {
        int generation;
        CancellationTokenSource cts;
        lock (_lock)
        {
            ResetLocked();
            generation = ++_generation;
            _clientName = clientName;
            _pendingToken = token;
            cts = _cts = new CancellationTokenSource();
        }
        LinkLog.Write($"client: dialling {endpoint.WebSocketUri}{(endpoint.Address is { } a ? $" at {a}" : "")}");
        Publish(LinkClientState.Connecting, generation);
        ReplaceDevices(new List<LinkDeviceInfo>(), generation);

        var cancel = cts.Token;
        WebSocket socket;
        try
        {
            socket = await _open(endpoint, cancel).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Shutdown(generation, new LinkClientState(LinkClientPhase.Failed, Reason: Describe(e)), closeNormally: false);
            return;
        }

        lock (_lock)
        {
            // Disconnected (or reconnected) while the socket was opening.
            if (!IsLiveLocked(generation)) { socket.Abort(); socket.Dispose(); return; }
            _socket = socket;
        }
        _ = Task.Run(() => ReceiveLoopAsync(socket, generation, cancel));
        await SendAsync(new LinkHelloClient(new LinkClientInfo(clientName, "DSPi Console", _appVersion)), generation)
            .ConfigureAwait(false);
    }

    /// <summary>Closes the session normally.</summary>
    public void Disconnect()
    {
        int generation;
        lock (_lock) generation = _generation;
        Shutdown(generation, LinkClientState.Disconnected, closeNormally: true);
    }

    // ── Pairing ──

    /// <summary>Pairs with the PIN the hub shows (or its fixed PIN). Returns the
    /// token to store; empty when the hub issued none.</summary>
    public async Task<string> PairAsync(string pin, string clientName, LinkRole role)
    {
        int generation;
        lock (_lock) generation = _generation;
        var ok = await RequestAsync(id => new LinkAuthPair(id, pin, clientName, role), requiresReady: false).ConfigureAwait(false);
        var body = ok.DecodeBody<LinkAuthOkBody>();
        BecameReady(body.Session, body.Role, generation);
        return body.Token ?? "";
    }

    // ── Commands ──

    /// <summary>
    /// Tunnels one vendor command; the client assigns the tag. Completes with
    /// the hub's response, TIMEOUT when none comes in time, or NO_DEVICE when
    /// there is no ready session (the status the hub itself uses when it can't
    /// reach a device, spec 8.1).
    /// </summary>
    public Task<LinkCmdResponse> CommandAsync(LinkCmdRequest request)
    {
        TaskCompletionSource<LinkCmdResponse> tcs;
        ushort tag;
        int generation;
        lock (_lock)
        {
            if (!_ready || _socket == null)
                return Task.FromResult(new LinkCmdResponse(request.Tag, LinkStatus.NoDevice));
            tag = NextTagLocked();
            generation = _generation;
            tcs = new TaskCompletionSource<LinkCmdResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCommands[tag] = tcs;
        }

        var timeout = BulkOpcodes.Contains(request.BRequest) ? BulkTimeout : RequestTimeout;
        _ = TimeOutCommandAsync(tag, tcs, timeout);
        _ = SendFrameAsync(request with { Tag = tag }, generation);
        return tcs.Task;
    }

    private async Task TimeOutCommandAsync(ushort tag, TaskCompletionSource<LinkCmdResponse> tcs, TimeSpan timeout)
    {
        if (await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false) == tcs.Task) return;
        lock (_lock)
        {
            if (!_pendingCommands.TryGetValue(tag, out var current) || current != tcs) return;
            _pendingCommands.Remove(tag);
        }
        tcs.TrySetResult(new LinkCmdResponse(tag, LinkStatus.Timeout));
    }

    /// <summary>1..65535, skipping 0 and any tag still awaiting its answer.</summary>
    private ushort NextTagLocked()
    {
        for (int i = 0; i < ushort.MaxValue; i++)
        {
            var tag = _nextTag;
            _nextTag = _nextTag == ushort.MaxValue ? (ushort)1 : (ushort)(_nextTag + 1);
            if (!_pendingCommands.ContainsKey(tag)) return tag;
        }
        throw new InvalidOperationException("Every Link tag is in use.");
    }

    // ── Snapshot, polls, locks ──

    public async Task<LinkSnapshotBody> SnapshotAsync(byte handle) =>
        (await RequestAsync(id => new LinkDeviceSnapshotRequest(id, handle)).ConfigureAwait(false)).DecodeBody<LinkSnapshotBody>();

    public async Task<IReadOnlyList<LinkPollGrant>> SubscribePollsAsync(byte handle, IReadOnlyList<LinkPollSpec> polls) =>
        (await RequestAsync(id => new LinkPollSubscribe(id, handle, polls)).ConfigureAwait(false)).DecodeBody<LinkPollSubscribeBody>().Granted;

    public void UnsubscribePolls(byte handle, IReadOnlyList<int> slots) =>
        _ = RequestAsync(id => new LinkPollUnsubscribe(id, handle, slots)).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    /// <summary>Takes the device lock (spec 7.7). Throws LinkRequestException
    /// ("locked" when another session holds it). Returns the granted timeout
    /// when the hub reports one.</summary>
    public async Task<TimeSpan?> AcquireLockAsync(byte handle, string? reason, TimeSpan? timeout)
    {
        var ok = await RequestAsync(id => new LinkLockAcquire(id, handle, reason,
            timeout is { } t ? (int)Math.Min(t.TotalMilliseconds, 60000) : null)).ConfigureAwait(false);
        return ok.DecodeBody<LinkLockOkBody>().TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
    }

    public void ReleaseLock(byte handle) =>
        _ = RequestAsync(id => new LinkLockRelease(id, handle)).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    // ── Request plumbing ──

    /// <summary>One JSON request, its reply routed back by id. Throws
    /// LinkRequestException on err, timeout, or no session.</summary>
    public async Task<LinkOk> RequestAsync(Func<long, LinkMessage> make, bool requiresReady = true)
    {
        TaskCompletionSource<LinkOk> tcs;
        LinkMessage message;
        int generation;
        long id;
        lock (_lock)
        {
            bool usable = requiresReady
                ? _ready
                : _state.Phase is LinkClientPhase.Connecting or LinkClientPhase.AwaitingAuth or LinkClientPhase.Ready;
            if (!usable || _socket == null) throw new LinkRequestException(LinkRequestException.NotConnected, "Not connected to the hub.");
            id = _nextRequestId++;
            generation = _generation;
            message = make(id);
            tcs = new TaskCompletionSource<LinkOk>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingJson[id] = tcs;
        }

        await SendAsync(message, generation).ConfigureAwait(false);
        if (await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout)).ConfigureAwait(false) != tcs.Task)
        {
            lock (_lock) _pendingJson.Remove(id);
            tcs.TrySetException(new LinkRequestException(LinkRequestException.Timeout, "The hub didn't answer in time."));
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task SendAsync(LinkMessage message, int generation)
    {
        var bytes = message.EncodeUtf8();
        await SendRawAsync(bytes, WebSocketMessageType.Text, generation).ConfigureAwait(false);
    }

    private Task SendFrameAsync(LinkFrame frame, int generation) =>
        SendRawAsync(frame.Encode(), WebSocketMessageType.Binary, generation);

    /// <summary>One message at a time: a WebSocket allows a single send in flight.</summary>
    private async Task SendRawAsync(byte[] bytes, WebSocketMessageType type, int generation)
    {
        WebSocket? socket;
        CancellationToken token;
        lock (_lock)
        {
            if (generation != _generation) return;
            socket = _socket;
            token = _cts?.Token ?? CancellationToken.None;
        }
        if (socket == null) return;
        try
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try { await socket.SendAsync(bytes, type, endOfMessage: true, token).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            SocketFailed(socket, generation, e);
        }
    }

    // ── Receive loop ──

    private async Task ReceiveLoopAsync(WebSocket socket, int generation, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Finish the close handshake, then report why.
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                    SocketFailed(socket, generation, null);
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxInbound)
                {
                    SocketFailed(socket, generation, new WebSocketException("The hub sent a message larger than 1 MiB."));
                    return;
                }
                if (!result.EndOfMessage) continue;

                var bytes = message.ToArray();
                message.SetLength(0);
                lock (_lock) if (!IsLiveLocked(generation)) return;
                if (result.MessageType == WebSocketMessageType.Text) HandleText(bytes, generation);
                else HandleBinary(bytes, generation);
            }
        }
        catch (Exception e)
        {
            SocketFailed(socket, generation, e);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>One control-plane message from the hub. Internal so a test can
    /// feed events without a socket.</summary>
    internal void HandleText(byte[] utf8, int? generation = null)
    {
        int gen;
        lock (_lock) gen = generation ?? _generation;
        LinkMessage message;
        try { message = LinkMessage.Decode(utf8); }
        catch (JsonException) { return; } // a malformed event is dropped; a reply's request times out

        switch (message)
        {
            case LinkHelloHub hello:
                HandleHubHello(hello, gen);
                break;
            case LinkOk ok when ok.Id is { } okId:
                if (TakePending(okId) is { } okTcs) okTcs.TrySetResult(ok);
                break;
            case LinkErr err when err.Id is { } errId:
                if (TakePending(errId) is { } errTcs) errTcs.TrySetException(new LinkRequestException(err.Code, err.Msg));
                break;
            case LinkDeviceAdded added:
                UpsertDevice(added.Device, gen);
                break;
            case LinkDeviceChanged changed:
                UpsertDevice(changed.Device, gen);
                break;
            case LinkDeviceRemoved removed:
                RemoveDevice(removed.Handle, gen);
                break;
            // Anything else is an event this build doesn't act on (spec 7.1).
        }
    }

    private TaskCompletionSource<LinkOk>? TakePending(long id)
    {
        lock (_lock)
        {
            if (!_pendingJson.Remove(id, out var tcs)) return null;
            return tcs;
        }
    }

    private void HandleBinary(byte[] bytes, int generation)
    {
        LinkFrame frame;
        try { frame = LinkFrame.Decode(bytes); }
        catch (LinkFrameException) { return; } // an unknown or malformed frame is dropped (spec 10.4)

        switch (frame)
        {
            case LinkCmdResponse response:
                TaskCompletionSource<LinkCmdResponse>? tcs;
                lock (_lock) _pendingCommands.Remove(response.Tag, out tcs);
                tcs?.TrySetResult(response);
                break;
            case LinkNotifyFrame notify:
                Raise(Notified, notify);
                break;
            case LinkPollFrame poll:
                Raise(Polled, poll);
                break;
            case LinkResyncFrame resync:
                Raise(Resynced, resync);
                break;
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T value)
    {
        if (handler == null) return;
        try { handler(this, value); }
        catch { /* a subscriber must not break the receive loop */ }
    }

    // ── Session lifecycle ──

    private void HandleHubHello(LinkHelloHub hello, int generation)
    {
        string? token;
        string name;
        lock (_lock)
        {
            if (!IsLiveLocked(generation)) return;
            _hello = hello;
            LinkLog.Write($"client: hello from {hello.Hub.Name} ({hello.Hub.Kind} {hello.Hub.Version}, proto {hello.Version.Major}.{hello.Version.Minor}, auth {hello.Auth}"
                          + $"{(hello.Auth == LinkAuthMode.Pin ? " " + hello.PinSource : "")}, caps {string.Join(",", hello.Caps)}, max_inflight {hello.Limits?.MaxInflight})");
            token = _pendingToken;
            _pendingToken = null;
            name = _clientName;
        }

        if (hello.Auth == LinkAuthMode.None)
        {
            // Open access: the session is open and admin from now on (spec
            // 6.1). The courtesy pair only learns our session id, for echo
            // suppression; if the hub refuses it we carry on without one.
            _ = Task.Run(async () =>
            {
                ushort session = 0;
                LinkRole role = LinkRole.Admin;
                try
                {
                    var ok = await RequestAsync(id => new LinkAuthPair(id, OpenAccessPin, name), requiresReady: false).ConfigureAwait(false);
                    var body = ok.DecodeBody<LinkAuthOkBody>();
                    session = (ushort)Math.Clamp(body.Session, 0, ushort.MaxValue);
                    role = body.Role;
                }
                catch (Exception e) when (e is LinkRequestException or JsonException) { }
                BecameReady(session, role, generation);
            });
            return;
        }

        if (token == null)
        {
            Publish(new LinkClientState(LinkClientPhase.AwaitingAuth, NeedsPairing: true), generation);
            return;
        }
        Publish(new LinkClientState(LinkClientPhase.AwaitingAuth), generation);
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await RequestAsync(id => new LinkAuthToken(id, token), requiresReady: false).ConfigureAwait(false);
                var body = ok.DecodeBody<LinkAuthOkBody>();
                BecameReady(body.Session, body.Role, generation);
            }
            catch (LinkRequestException e) when (e.Code == LinkRequestException.NotConnected)
            {
                // The session closed meanwhile; its own state says why.
            }
            catch (LinkRequestException e) when (e.Code == LinkRequestException.Timeout)
            {
                // A slow hub, not a refusal: try again later rather than ask
                // for a PIN the token doesn't need.
                Shutdown(generation, new LinkClientState(LinkClientPhase.Failed, Reason: "The hub didn't answer in time."), closeNormally: true);
            }
            catch (Exception e) when (e is LinkRequestException or JsonException)
            {
                // The hub refused the token: pair again.
                Publish(new LinkClientState(LinkClientPhase.AwaitingAuth, NeedsPairing: true, TokenRefused: true), generation);
            }
        });
    }

    private void BecameReady(int session, LinkRole role, int? generation = null)
    {
        int gen;
        lock (_lock)
        {
            gen = generation ?? _generation;
            if (!IsLiveLocked(gen) || _ready) return;
            if (_state.Phase is not (LinkClientPhase.Connecting or LinkClientPhase.AwaitingAuth)) return;
            _session = (ushort)Math.Clamp(session, 0, ushort.MaxValue);
            _role = role;
            _ready = true;
        }
        Publish(LinkClientState.Ready, gen);
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await RequestAsync(id => new LinkDeviceListRequest(id)).ConfigureAwait(false);
                ReplaceDevices(ok.DecodeBody<LinkDeviceListBody>().Devices.ToList(), gen);
            }
            catch (Exception e) when (e is LinkRequestException or JsonException) { }
        });
    }

    // ── Inventory ──

    private void ReplaceDevices(List<LinkDeviceInfo> list, int generation)
    {
        IReadOnlyList<LinkDeviceInfo> snapshot;
        lock (_lock)
        {
            if (generation != _generation) return;
            _inventory = list;
            snapshot = _inventory.ToArray();
        }
        Raise(DevicesChanged, snapshot);
    }

    private void UpsertDevice(LinkDeviceInfo device, int generation)
    {
        IReadOnlyList<LinkDeviceInfo> snapshot;
        lock (_lock)
        {
            if (generation != _generation) return;
            int i = _inventory.FindIndex(d => d.Handle == device.Handle);
            if (i >= 0) _inventory[i] = device;
            else _inventory.Add(device);
            snapshot = _inventory.ToArray();
        }
        Raise(DevicesChanged, snapshot);
    }

    private void RemoveDevice(int handle, int generation)
    {
        IReadOnlyList<LinkDeviceInfo> snapshot;
        lock (_lock)
        {
            if (generation != _generation) return;
            _inventory.RemoveAll(d => d.Handle == handle);
            snapshot = _inventory.ToArray();
        }
        Raise(DevicesChanged, snapshot);
    }

    // ── Failure and teardown ──

    /// <summary>The socket failed or the hub closed it; the close code, when
    /// the hub sent one, names the reason.</summary>
    private void SocketFailed(WebSocket socket, int generation, Exception? error)
    {
        int? code = null;
        string? description = null;
        try
        {
            code = socket.CloseStatus is { } status ? (int)status : null;
            description = socket.CloseStatusDescription;
        }
        catch (ObjectDisposedException) { }
        var reason = code is { } c ? ReasonForCloseCode(c, description) : error != null ? Describe(error) : "The connection was lost.";
        Shutdown(generation, new LinkClientState(LinkClientPhase.Failed, Reason: reason, CloseCode: code), closeNormally: false);
    }

    public static string ReasonForCloseCode(int code, string? description = null) => code switch
    {
        LinkCloseCode.NormalClosure => "The hub closed the connection.",
        LinkCloseCode.KeepaliveFailure => "The hub stopped hearing from this console.",
        LinkCloseCode.StoppedReading => "The connection stalled.",
        LinkCloseCode.TooFast => "The hub couldn't keep up.",
        LinkCloseCode.ProtocolError => "The hub refused the connection (protocol error).",
        LinkCloseCode.AuthFailed => "Pairing timed out or failed too many times.",
        LinkCloseCode.TokenRevoked => "The hub revoked this console's access.",
        LinkCloseCode.ShuttingDown => "The hub is restarting.",
        _ => string.IsNullOrEmpty(description) ? $"The connection closed ({code})." : $"The connection closed ({code}: {description}).",
    };

    private static string Describe(Exception e) => e switch
    {
        WebSocketException { InnerException: { } inner } => Describe(inner),
        HttpRequestException { InnerException: SocketException s } => s.Message,
        SocketException s => s.Message,
        OperationCanceledException => "The connection timed out.",
        _ => e.Message,
    };

    private void Shutdown(int generation, LinkClientState newState, bool closeNormally)
    {
        List<TaskCompletionSource<LinkOk>> json;
        List<KeyValuePair<ushort, TaskCompletionSource<LinkCmdResponse>>> commands;
        WebSocket? socket;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (generation != _generation || generation == _closedGeneration) return;
            _closedGeneration = generation;
            socket = _socket;
            cts = _cts;
            _socket = null;
            _cts = null;
            _ready = false;
            _session = 0;
            _role = null;
            _pendingToken = null;
            json = _pendingJson.Values.ToList();
            commands = _pendingCommands.ToList();
            _pendingJson.Clear();
            _pendingCommands.Clear();
        }

        if (socket != null)
        {
            if (closeNormally && socket.State == WebSocketState.Open)
            {
                _ = socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .ContinueWith(_ => { socket.Dispose(); cts?.Cancel(); cts?.Dispose(); }, TaskScheduler.Default);
            }
            else
            {
                cts?.Cancel();
                socket.Abort();
                socket.Dispose();
                cts?.Dispose();
            }
        }
        else
        {
            cts?.Cancel();
            cts?.Dispose();
        }

        foreach (var tcs in json)
            tcs.TrySetException(new LinkRequestException(LinkRequestException.NotConnected, "The connection closed."));
        // The data plane has no "not connected": NO_DEVICE is the status for a
        // command the hub couldn't put on a device (spec 8.1).
        foreach (var (tag, tcs) in commands)
            tcs.TrySetResult(new LinkCmdResponse(tag, LinkStatus.NoDevice));
        Publish(newState, generation, final: true);
    }

    /// <summary>Drops whatever a previous connection left. Lock held.</summary>
    private void ResetLocked()
    {
        var socket = _socket;
        var cts = _cts;
        var json = _pendingJson.Values.ToList();
        var commands = _pendingCommands.ToList();
        _pendingJson.Clear();
        _pendingCommands.Clear();
        _socket = null;
        _cts = null;
        _hello = null;
        _session = 0;
        _role = null;
        _ready = false;
        _pendingToken = null;
        _state = LinkClientState.Disconnected;

        cts?.Cancel();
        socket?.Abort();
        socket?.Dispose();
        cts?.Dispose();
        foreach (var tcs in json)
            tcs.TrySetException(new LinkRequestException(LinkRequestException.NotConnected, "The connection closed."));
        foreach (var (tag, tcs) in commands)
            tcs.TrySetResult(new LinkCmdResponse(tag, LinkStatus.NoDevice));
    }

    /// <param name="final">The shutdown's own state, the last a session publishes.</param>
    private void Publish(LinkClientState state, int generation, bool final = false)
    {
        lock (_lock)
        {
            if (generation != _generation || _state == state) return;
            // After a shutdown nothing late (a failed auth, a hello) may
            // reopen the state: it would prompt for a PIN on a dead socket.
            if (generation == _closedGeneration && !final) return;
            _state = state;
        }
        LinkLog.Write($"client: {state.Phase}{(state.NeedsPairing ? " (needs pairing)" : "")}"
                      + $"{(state.CloseCode is { } c ? $" close {c}" : "")}{(state.Reason is { } r ? $": {r}" : "")}"
                      + (state.Phase == LinkClientPhase.Ready ? $" session {SessionId} role {Role}" : ""));
        Raise(StateChanged, state);
    }

    public void Dispose()
    {
        Disconnect();
        _sendLock.Dispose();
    }

    // ── The real socket ──

    /// <summary>Keepalive: ping every 10 s and give up on a hub silent for
    /// another 20 s; the hub pings us too (spec 4).</summary>
    public static TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(10);
    public static TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// A ClientWebSocket asking for dspi-link-1. A known address is dialled
    /// directly through a ConnectCallback, so a link-local IPv6 address keeps
    /// its scope id (a zone in the URI would be mangled, L0 spike c).
    /// </summary>
    public static async Task<WebSocket> OpenClientWebSocketAsync(LinkEndpoint endpoint, CancellationToken token)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(LinkService.SubProtocol);
        ws.Options.KeepAliveInterval = KeepAliveInterval;
        ws.Options.KeepAliveTimeout = KeepAliveTimeout;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            if (endpoint.Address is { } address)
            {
                var handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (ctx, ct) =>
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(address, ctx.DnsEndPoint.Port), ct).ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    },
                };
                await ws.ConnectAsync(endpoint.WebSocketUri, new HttpMessageInvoker(handler), timeout.Token).ConfigureAwait(false);
            }
            else
            {
                await ws.ConnectAsync(endpoint.WebSocketUri, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            ws.Dispose();
            throw;
        }
        if (ws.SubProtocol != LinkService.SubProtocol)
        {
            ws.Abort();
            ws.Dispose();
            throw new WebSocketException("The hub didn't accept the dspi-link-1 protocol.");
        }
        return ws;
    }
}

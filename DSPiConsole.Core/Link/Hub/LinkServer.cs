using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DSPiConsole.Core.Link.Hub;

/// <summary>
/// Per-connection limits and deadlines. The defaults are the spec's (4, 5);
/// tests shorten them.
/// </summary>
public sealed record LinkServerTuning
{
    /// <summary>The hub pings this often (spec 4).</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>A connection that has sent nothing, pongs included, for this
    /// long is closed with 1001 (spec 4, 5).</summary>
    public TimeSpan IdleDeadline { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>A connection still unauthenticated after this long is closed
    /// with 4001 (spec 5).</summary>
    public TimeSpan AuthDeadline { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>A send that has not completed for this long means the client
    /// stopped reading: the connection is dropped (1011, spec 5).</summary>
    public TimeSpan StallDeadline { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>How long a close handshake may take before the socket is
    /// simply dropped.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>How long a client may take to send its HTTP request head, so
    /// a half-open connection can't hold a slot.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>The largest HTTP request head accepted (431 above it).</summary>
    public int MaxRequestBytes { get; init; } = 8192;
    /// <summary>The largest reassembled WebSocket message; above it the
    /// connection is closed with 4000. The hello's <c>limits.max_frame</c>.</summary>
    public int MaxMessage { get; init; } = 65536;
    /// <summary>Queued outbound bytes above which the client counts as
    /// lagging: POLL frames are shed and NOTIFY/RESYNC folded into owed
    /// RESYNCs until the queue drains to <see cref="OutboundLowWater"/>.</summary>
    public int OutboundHighWater { get; init; } = 256 * 1024;
    public int OutboundLowWater { get; init; } = 64 * 1024;
    /// <summary>A hard cap on queued outbound bytes (text and CMD responses
    /// are never shed); past it the connection is dropped as stalled.</summary>
    public int OutboundLimit { get; init; } = 8 * 1024 * 1024;
    /// <summary>Received bytes waiting for the handler; past it the client is
    /// sending faster than the hub can take and is closed with 1013.</summary>
    public int InboundLimit { get; init; } = 1024 * 1024;
    /// <summary>Open sockets (HTTP and WebSocket together) beyond which new
    /// connections are refused at accept.</summary>
    public int MaxConnections { get; init; } = 64;
    /// <summary>Open sockets one LAN address may hold, so one host can't take
    /// every slot (loopback is exempt: it's this PC).</summary>
    public int MaxConnectionsPerPeer { get; init; } = 8;
    /// <summary>Tests turn this off to exercise the per-address cap over loopback.</summary>
    internal bool LoopbackUncapped { get; init; } = true;
    /// <summary>SO_SNDBUF for accepted sockets; null keeps the OS default.
    /// Tests make it small so backpressure shows quickly.</summary>
    public int? SocketSendBuffer { get; init; }

    public static LinkServerTuning Default { get; } = new();
}

/// <summary>One interface address and its prefix: a subnet this machine is on.</summary>
public readonly record struct LinkSubnet(IPAddress Address, int PrefixLength);

/// <summary>The listener couldn't start. <see cref="PortInUse"/> says whether
/// the port was the problem, so the UI can suggest another.</summary>
public sealed class LinkServerStartException : Exception
{
    public int Port { get; }
    public bool PortInUse { get; }

    public LinkServerStartException(int port, bool portInUse, string message, Exception? inner = null)
        : base(message, inner)
    {
        Port = port;
        PortInUse = portInUse;
    }
}

/// <summary>
/// The hub's network front end (L3, decision N2): one TCP port serving the
/// DSPi Link WebSocket, <c>GET /dspi/v1/info</c> and the browser UI (spec 4.2).
/// A <see cref="TcpListener"/> and a small HTTP/1.1 parser rather than Kestrel
/// or HttpListener: the app is framework-dependent, so Kestrel would make
/// every user install the ASP.NET Core runtime, and HttpListener needs a URL
/// ACL for a wildcard bind.
/// <para>It owns no protocol logic: each upgraded connection gets one
/// <see cref="ILinkConnection"/> from the factory, and this class only moves
/// whole messages between it and the socket, enforcing the spec's keepalive,
/// auth deadline, size limit and backpressure rules (spec 4, 5).</para>
/// <para>HTTP is one request per connection: every plain response carries
/// <c>Connection: close</c>. That keeps the parser free of pipelining and body
/// draining, and costs a handshake per file of a three-file bundle.</para>
/// </summary>
public sealed class LinkServer : IDisposable
{
    private readonly object _lock = new();
    private readonly LinkConnectionFactory _factory;
    private readonly Func<string> _infoJson;
    private readonly string? _webRoot;
    private readonly LinkServerTuning _tuning;
    private readonly Func<IReadOnlyCollection<string>> _ownIdentities;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private readonly HashSet<LinkWebSocketSession> _sessions = new();
    private int _openSockets;
    private long _framesShed;

    /// <summary>The number of open WebSocket connections changed. Raised on a
    /// pool thread.</summary>
    public event EventHandler? ConnectionCountChanged;

    /// <param name="factory">Makes the protocol handler for each upgraded connection.</param>
    /// <param name="infoJson">The body of <c>GET /dspi/v1/info</c> (spec 3.2),
    /// built fresh per request.</param>
    /// <param name="webRoot">The browser UI's folder, or null for none. Served
    /// at <c>/</c> only when it holds an index.html.</param>
    /// <param name="tuning">Deadlines and limits; the spec's by default.</param>
    /// <param name="ownIdentities">The names and addresses that count as this
    /// hub in a WebSocket <c>Origin</c>; <see cref="CurrentIdentities"/> by
    /// default. Loopback and <c>localhost</c> always count.</param>
    public LinkServer(LinkConnectionFactory factory, Func<string> infoJson, string? webRoot,
        LinkServerTuning? tuning = null, Func<IReadOnlyCollection<string>>? ownIdentities = null)
    {
        _factory = factory;
        _infoJson = infoJson;
        _webRoot = string.IsNullOrEmpty(webRoot) ? null : Path.GetFullPath(webRoot);
        _tuning = tuning ?? LinkServerTuning.Default;
        _ownIdentities = ownIdentities ?? CurrentIdentities;
    }

    public bool IsRunning { get { lock (_lock) return _listener != null; } }

    /// <summary>The port actually bound (useful after <c>Start(0)</c>), or null
    /// when stopped.</summary>
    public int? BoundPort
    {
        get { lock (_lock) return (_listener?.LocalEndpoint as IPEndPoint)?.Port; }
    }

    /// <summary>Open WebSocket connections (plain HTTP requests not counted).</summary>
    public int ConnectionCount { get { lock (_lock) return _sessions.Count; } }

    /// <summary>POLL, NOTIFY and RESYNC frames shed for lagging clients since
    /// construction; a diagnostic.</summary>
    public long FramesShed => Interlocked.Read(ref _framesShed);

    /// <summary>Whether the web root holds a browser UI (TXT <c>web=1</c>).</summary>
    public bool ServesWeb => _webRoot != null && LinkWebRoot.IsPresent(_webRoot);

    // ── Lifecycle ──

    /// <summary>
    /// Listens on every interface, dual-stack (<c>[::]</c> with IPv4 arriving
    /// as <c>::ffff:a.b.c.d</c>), or IPv4 only on a host without IPv6. Does
    /// nothing when already running. Port 0 picks a free port.
    /// </summary>
    /// <exception cref="LinkServerStartException">The port is taken or reserved.</exception>
    public void Start(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        lock (_lock)
        {
            if (_listener != null) return;
            var listener = Listen(port);
            var cts = new CancellationTokenSource();
            _listener = listener;
            _cts = cts;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, cts));
        }
    }

    private static TcpListener Listen(int port)
    {
        // No SO_EXCLUSIVEADDRUSE: with it, Windows refuses to rebind while
        // accepted connections from the last run sit in TIME_WAIT, so turning
        // sharing off and on again would fail for minutes. Windows' default
        // already stops another user's socket taking the port over.
        var listener = Socket.OSSupportsIPv6
            ? new TcpListener(IPAddress.IPv6Any, port)
            : new TcpListener(IPAddress.Any, port);
        if (Socket.OSSupportsIPv6) listener.Server.DualMode = true;
        try
        {
            listener.Start(128);
            return listener;
        }
        catch (SocketException e)
        {
            listener.Server.Dispose();
            throw e.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => new LinkServerStartException(port, true,
                    $"Port {port} is already in use by another program.", e),
                SocketError.AccessDenied => new LinkServerStartException(port, false,
                    $"Windows won't let this app use port {port}: it may be reserved (netsh interface ipv4 show excludedportrange protocol=tcp) or held exclusively by another program.", e),
                _ => new LinkServerStartException(port, false, $"Couldn't listen on port {port}: {e.Message}", e),
            };
        }
    }

    /// <summary>Stops listening and closes every WebSocket with
    /// <paramref name="closeCode"/> (4003, hub shutting down, by default; spec
    /// 5). Waits for each connection's close handshake, at most about twice
    /// <see cref="LinkServerTuning.CloseTimeout"/>.</summary>
    public void Stop(int closeCode = LinkCloseCode.ShuttingDown) =>
        StopAsync(closeCode).GetAwaiter().GetResult();

    public async Task StopAsync(int closeCode = LinkCloseCode.ShuttingDown)
    {
        TcpListener listener;
        CancellationTokenSource cts;
        Task? accept;
        LinkWebSocketSession[] sessions;
        lock (_lock)
        {
            if (_listener == null) return;
            listener = _listener;
            cts = _cts!;
            accept = _acceptLoop;
            sessions = _sessions.ToArray();
            _listener = null;
            _cts = null;
            _acceptLoop = null;
        }
        cts.Cancel();
        try { listener.Stop(); } catch (SocketException) { }
        if (accept != null) { try { await accept.ConfigureAwait(false); } catch { } }
        await Task.WhenAll(sessions.Select(s => s.ShutdownAsync(closeCode))).ConfigureAwait(false);
        cts.Dispose();
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationTokenSource cts)
    {
        var token = cts.Token;
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) when (!token.IsCancellationRequested)
            {
                continue; // a connection reset before accept; keep listening
            }
            catch (SocketException) { return; }
            catch (InvalidOperationException) { return; } // stopped

            if (Interlocked.Increment(ref _openSockets) > _tuning.MaxConnections)
            {
                Interlocked.Decrement(ref _openSockets);
                Drop(socket);
                continue;
            }
            if (!TakePeerSlot(socket))
            {
                Interlocked.Decrement(ref _openSockets);
                Drop(socket);
                continue;
            }
            _ = Task.Run(() => HandleConnectionAsync(socket, cts));
        }
    }

    private readonly Dictionary<IPAddress, int> _perPeer = new();

    private bool TakePeerSlot(Socket socket)
    {
        if ((socket.RemoteEndPoint as IPEndPoint)?.Address is not { } raw) return true;
        var peer = Normalize(raw);
        if (IPAddress.IsLoopback(peer) && _tuning.LoopbackUncapped) return true;
        lock (_perPeer)
        {
            int n = _perPeer.GetValueOrDefault(peer);
            if (n >= _tuning.MaxConnectionsPerPeer) return false;
            _perPeer[peer] = n + 1;
            return true;
        }
    }

    private void ReleasePeerSlot(IPEndPoint? remote)
    {
        if (remote == null) return;
        var peer = Normalize(remote.Address);
        if (IPAddress.IsLoopback(peer) && _tuning.LoopbackUncapped) return;
        lock (_perPeer)
        {
            if (!_perPeer.TryGetValue(peer, out var n)) return;
            if (n <= 1) _perPeer.Remove(peer);
            else _perPeer[peer] = n - 1;
        }
    }

    private static void Drop(Socket socket)
    {
        try { socket.LingerState = new LingerOption(true, 0); } catch { }
        socket.Dispose();
    }

    // ── One connection ──

    private async Task HandleConnectionAsync(Socket socket, CancellationTokenSource serverCts)
    {
        IPEndPoint? remote = null;
        try
        {
            // The hub is LAN-only (spec 6.4): refuse anything else before a
            // single byte of HTTP is read.
            remote = socket.RemoteEndPoint as IPEndPoint;
            if (remote == null || !IsLocalPeer(remote.Address))
            {
                LinkLog.Write($"Link server: refused non-local peer {remote}");
                Drop(socket);
                return;
            }
            socket.NoDelay = true;
            if (_tuning.SocketSendBuffer is { } sndbuf) socket.SendBufferSize = sndbuf;

            var stream = new NetworkStream(socket, ownsSocket: false);
            HttpRequestHead? head;
            byte[] leftover;
            int error;
            using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(serverCts.Token))
            {
                headCts.CancelAfter(_tuning.RequestTimeout);
                try
                {
                    (head, leftover, error) = await ReadRequestHeadAsync(stream, _tuning.MaxRequestBytes, headCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { socket.Dispose(); return; }
            }
            if (head == null)
            {
                if (error != 0) await RespondAndCloseAsync(socket, stream, error, PlainText(StatusText(error))).ConfigureAwait(false);
                else socket.Dispose();
                return;
            }

            var response = Route(head, out var upgrade);
            if (upgrade == null)
            {
                // Refusals say why a client can't connect; the info probe and
                // the web page's files would only be noise.
                if (response!.Status is not (0 or 200))
                    LinkLog.Write($"Link server: {head.Method} {head.Target} from {remote.Address} answered {response.Status}");
                await RespondAndCloseAsync(socket, stream, response).ConfigureAwait(false);
                return;
            }
            LinkLog.Write($"Link server: {remote.Address} connected");

            await RunWebSocketAsync(socket, stream, leftover, upgrade, PeerName(remote.Address), serverCts).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (e is not (IOException or SocketException or ObjectDisposedException or OperationCanceledException))
                LinkLog.Write($"Link server: connection from {remote} failed: {e}");
            socket.Dispose();
        }
        finally
        {
            Interlocked.Decrement(ref _openSockets);
            ReleasePeerSlot(remote);
        }
    }

    /// <summary>The remote IP as the factory gets it: no port, and an
    /// IPv4-mapped address as plain IPv4.</summary>
    private static string PeerName(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    private async Task RunWebSocketAsync(Socket socket, NetworkStream net, byte[] leftover, string acceptKey,
        string peer, CancellationTokenSource serverCts)
    {
        var accept = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
            $"Sec-WebSocket-Protocol: {LinkService.SubProtocol}\r\n\r\n");
        await net.WriteAsync(accept, serverCts.Token).ConfigureAwait(false);

        var session = new LinkWebSocketSession(socket, new ActivityStream(net, leftover), _tuning, peer,
            () => Interlocked.Increment(ref _framesShed));
        bool added;
        lock (_lock)
        {
            // Stop may have run between the accept and here.
            added = _cts == serverCts && !serverCts.IsCancellationRequested && _sessions.Add(session);
        }
        if (!added)
        {
            session.Abort("server stopping");
            await session.RunAsync(null).ConfigureAwait(false);
            return;
        }
        ConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await session.RunAsync(_factory).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _sessions.Remove(session);
            ConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── HTTP ──

    private sealed record HttpRequestHead(string Method, string Target, string Version, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        /// <summary>The target without query or fragment.</summary>
        public string Path
        {
            get
            {
                int cut = Target.IndexOfAny(['?', '#']);
                return cut < 0 ? Target : Target[..cut];
            }
        }
    }

    private sealed record HttpResponse(int Status, byte[] Body, IReadOnlyList<(string Name, string Value)> Headers);

    private static HttpResponse PlainText(string text, params (string, string)[] headers) =>
        new(0, Encoding.UTF8.GetBytes(text + "\n"), [("Content-Type", "text/plain; charset=utf-8"), .. headers]);

    /// <summary>
    /// Reads up to the blank line ending the request head. Returns the head and
    /// any bytes read past it (a client may start its WebSocket frames before
    /// reading the 101), or null with an HTTP status (400, 431) for a bad
    /// request, or null and 0 when the peer closed.
    /// </summary>
    private static async Task<(HttpRequestHead? Head, byte[] Leftover, int Error)> ReadRequestHeadAsync(
        Stream stream, int max, CancellationToken token)
    {
        var buffer = new byte[max];
        int length = 0;
        while (true)
        {
            if (length == max) return (null, [], 431);
            int n = await stream.ReadAsync(buffer.AsMemory(length, max - length), token).ConfigureAwait(false);
            if (n == 0) return (null, [], 0);
            int from = Math.Max(0, length - 3);
            length += n;
            int end = buffer.AsSpan(from, length - from).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            end += from;
            var head = ParseHead(Encoding.Latin1.GetString(buffer, 0, end));
            return head == null ? (null, [], 400) : (head, buffer[(end + 4)..length], 0);
        }
    }

    private static HttpRequestHead? ParseHead(string text)
    {
        var lines = text.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || !parts[1].StartsWith('/')
            || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            // No obsolete line folding, no header without a name.
            if (line.Length == 0 || line[0] is ' ' or '\t') return null;
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            var name = line[..colon];
            if (name.Any(c => c <= ' ' || c >= 0x7F)) return null;
            var value = line[(colon + 1)..].Trim(' ', '\t');
            // Repeated headers combine as a list (RFC 9110 5.3).
            headers[name] = headers.TryGetValue(name, out var prior) ? prior + ", " + value : value;
        }
        return new HttpRequestHead(parts[0], parts[1], parts[2], headers);
    }

    /// <summary>
    /// Picks the response. For an acceptable WebSocket upgrade it returns null
    /// and sets <paramref name="upgradeAccept"/> to the Sec-WebSocket-Accept
    /// value.
    /// </summary>
    private HttpResponse? Route(HttpRequestHead head, out string? upgradeAccept)
    {
        upgradeAccept = null;
        if (head.Method != "GET")
            return PlainText("Method not allowed.", ("Allow", "GET")) with { Status = 405 };

        var path = head.Path;
        if (path == LinkService.InfoPath)
        {
            string json;
            try { json = _infoJson(); }
            catch (Exception e)
            {
                LinkLog.Write($"Link server: info endpoint failed: {e.Message}");
                return PlainText("Info unavailable.") with { Status = 500 };
            }
            // CORS on this route only (spec 3.2), so a page served by another
            // hub can probe this one but can't read anything else here.
            return new HttpResponse(200, Encoding.UTF8.GetBytes(json),
                [("Content-Type", "application/json"), ("Access-Control-Allow-Origin", "*"), ("Cache-Control", "no-cache")]);
        }
        if (path == LinkService.DefaultPath)
            return CheckUpgrade(head, out upgradeAccept);

        if (_webRoot != null && LinkWebRoot.TryResolve(_webRoot, path, out var file, out var type))
        {
            try
            {
                return new HttpResponse(200, File.ReadAllBytes(file),
                    [("Content-Type", type), ("Cache-Control", "no-cache"), ("X-Content-Type-Options", "nosniff")]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return PlainText("Not found.") with { Status = 404 };
    }

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>
    /// RFC 6455 4.2.1 plus the spec's rules (4): the <c>dspi-link-1</c>
    /// sub-protocol must be requested, and a browser's <c>Origin</c> must name
    /// this hub.
    /// </summary>
    private HttpResponse? CheckUpgrade(HttpRequestHead head, out string? accept)
    {
        accept = null;
        if (head.Version == "HTTP/1.0"
            || !HasToken(head.Header("Upgrade"), "websocket")
            || !HasToken(head.Header("Connection"), "upgrade"))
            return PlainText("This is the DSPi Link WebSocket; connect with a WebSocket client.") with { Status = 400 };
        if (head.Header("Sec-WebSocket-Version")?.Trim() != "13")
            return PlainText("Unsupported WebSocket version.", ("Sec-WebSocket-Version", "13")) with { Status = 426 };
        var key = head.Header("Sec-WebSocket-Key")?.Trim();
        if (key == null || !IsValidKey(key))
            return PlainText("Missing or malformed Sec-WebSocket-Key.") with { Status = 400 };
        if (!HasToken(head.Header("Sec-WebSocket-Protocol"), LinkService.SubProtocol))
            return PlainText($"Request the {LinkService.SubProtocol} sub-protocol.") with { Status = 400 };
        if (!IsAllowedOrigin(head.Header("Origin"), _ownIdentities()))
        {
            LinkLog.Write($"Link server: refused an upgrade from origin {head.Header("Origin")}");
            return PlainText("This page may not open a session with this hub.") with { Status = 403 };
        }
        accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        return null;
    }

    private static bool IsValidKey(string key)
    {
        Span<byte> bytes = stackalloc byte[32];
        return Convert.TryFromBase64String(key, bytes, out int n) && n == 16;
    }

    /// <summary>Whether a comma-separated header holds this token (case-insensitive).</summary>
    private static bool HasToken(string? header, string token) =>
        header != null && header.Split(',').Any(t => t.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));

    private static string StatusText(int status) => status switch
    {
        101 => "Switching Protocols",
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        426 => "Upgrade Required",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        _ => "Error",
    };

    private Task RespondAndCloseAsync(Socket socket, Stream stream, int status, HttpResponse response) =>
        RespondAndCloseAsync(socket, stream, response with { Status = status });

    private async Task RespondAndCloseAsync(Socket socket, Stream stream, HttpResponse response)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {StatusText(response.Status)}\r\n");
            foreach (var (name, value) in response.Headers) sb.Append(name).Append(": ").Append(value).Append("\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {response.Body.Length}\r\n");
            sb.Append("Connection: close\r\n\r\n");
            using var cts = new CancellationTokenSource(_tuning.RequestTimeout);
            await stream.WriteAsync(Encoding.Latin1.GetBytes(sb.ToString()), cts.Token).ConfigureAwait(false);
            if (response.Body.Length > 0) await stream.WriteAsync(response.Body, cts.Token).ConfigureAwait(false);

            // Half-close and drain briefly before closing: closing a socket
            // with unread input (a request body, say) sends a reset, which
            // can destroy the response before the client reads it.
            socket.Shutdown(SocketShutdown.Send);
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var sink = new byte[4096];
            for (int total = 0; total < 65536;)
            {
                int n = await stream.ReadAsync(sink, drain.Token).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            socket.Dispose();
        }
    }

    // ── Peer gate (spec 6.4) ──

    /// <summary>
    /// Whether a peer may connect: loopback, RFC 1918, IPv4 link-local
    /// (169.254/16), CGNAT (100.64/10, where Tailscale peers arrive), IPv6
    /// link-local (fe80::/10) and ULA (fc00::/7), IPv4-mapped forms of any of
    /// those, or any address on one of <paramref name="subnets"/> (a LAN with
    /// ISP IPv6 gives every machine a public address).
    /// </summary>
    public static bool IsLocalPeer(IPAddress address, IEnumerable<LinkSubnet> subnets) =>
        IsLocalAddress(address) || IsOnLink(address, subnets);

    /// <summary>The peer gate against this machine's subnets, read only when
    /// the fixed ranges don't already decide.</summary>
    public static bool IsLocalPeer(IPAddress address) =>
        IsLocalAddress(address) || IsOnLink(address, LocalSubnets());

    /// <summary>The fixed ranges of <see cref="IsLocalPeer(IPAddress, IEnumerable{LinkSubnet})"/>.</summary>
    public static bool IsLocalAddress(IPAddress address)
    {
        address = Normalize(address);
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 127                                  // 127/8 loopback
                || b[0] == 10                                   // 10/8
                || (b[0] == 172 && b[1] is >= 16 and <= 31)     // 172.16/12
                || (b[0] == 192 && b[1] == 168)                 // 192.168/16
                || (b[0] == 169 && b[1] == 254)                 // 169.254/16 link-local
                || (b[0] == 100 && b[1] is >= 64 and <= 127);   // 100.64/10 CGNAT
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IPv6Loopback.Equals(new IPAddress(b))  // ::1
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)          // fe80::/10
                || (b[0] & 0xFE) == 0xFC;                           // fc00::/7 ULA
        }
        return false;
    }

    /// <summary>Whether the address is inside one of the subnets. A zero
    /// prefix (a default route) matches nothing.</summary>
    public static bool IsOnLink(IPAddress address, IEnumerable<LinkSubnet> subnets)
    {
        var peer = Normalize(address).GetAddressBytes();
        foreach (var net in subnets)
        {
            var bytes = Normalize(net.Address).GetAddressBytes();
            if (bytes.Length != peer.Length || net.PrefixLength <= 0) continue;
            int prefix = Math.Min(net.PrefixLength, bytes.Length * 8);
            if (PrefixMatches(peer, bytes, prefix)) return true;
        }
        return false;
    }

    private static bool PrefixMatches(byte[] a, byte[] b, int bits)
    {
        int whole = bits / 8;
        for (int i = 0; i < whole; i++) if (a[i] != b[i]) return false;
        int rest = bits % 8;
        if (rest == 0) return true;
        int mask = 0xFF << (8 - rest) & 0xFF;
        return (a[whole] & mask) == (b[whole] & mask);
    }

    /// <summary>IPv4-mapped IPv6 as plain IPv4, and no IPv6 zone.</summary>
    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            return new IPAddress(address.GetAddressBytes());
        return address;
    }

    /// <summary>The subnets of every interface that is up, read fresh per
    /// connection so a DHCP or router-advertisement change is picked up.</summary>
    public static IReadOnlyList<LinkSubnet> LocalSubnets()
    {
        var list = new List<LinkSubnet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    list.Add(new LinkSubnet(ua.Address, ua.PrefixLength));
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    // ── Origin check (spec 4, 1.2) ──

    /// <summary>
    /// Whether a WebSocket upgrade's <c>Origin</c> may open a session. None
    /// (a native client) may. Otherwise its host must be loopback,
    /// <c>localhost</c>, or one of <paramref name="ownIdentities"/> (names
    /// compared case-insensitively, addresses by value). Never compared with
    /// the request's <c>Host</c>, which a hostile page controls through DNS
    /// rebinding. The port is not compared.
    /// </summary>
    public static bool IsAllowedOrigin(string? origin, IReadOnlyCollection<string> ownIdentities)
    {
        if (origin == null) return true;
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) return false;

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(uri.DnsSafeHost, out var ip)) return false;
            ip = Normalize(ip);
            if (IPAddress.IsLoopback(ip)) return true;
            foreach (var id in ownIdentities)
                if (IPAddress.TryParse(id, out var own) && Normalize(own).Equals(ip)) return true;
            return false;
        }
        if (uri.HostNameType != UriHostNameType.Dns) return false;
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var id in ownIdentities)
        {
            if (IPAddress.TryParse(id, out _)) continue;
            if (id.TrimEnd('.').Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>This machine's names and addresses: the host name, the NetBIOS
    /// name, each with <c>.local</c> (Windows answers mDNS for it), and every
    /// unicast address of every interface that is up.</summary>
    public static IReadOnlyCollection<string> CurrentIdentities()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            ids.Add(name);
            ids.Add(name + ".local");
        }
        try { AddName(Dns.GetHostName()); } catch (SocketException) { }
        AddName(Environment.MachineName);
        // The names the page may have been opened by besides .local: the
        // primary DNS suffix and each interface's (router DNS "pc.lan",
        // Tailscale MagicDNS "pc.tailnet.ts.net", a domain).
        try
        {
            var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var host = props.HostName;
            if (!string.IsNullOrEmpty(props.DomainName)) ids.Add($"{host}.{props.DomainName}");
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var suffix = nic.GetIPProperties().DnsSuffix;
                if (!string.IsNullOrEmpty(suffix)) ids.Add($"{host}.{suffix}");
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        foreach (var net in LocalSubnets()) ids.Add(Normalize(net.Address).ToString());
        return ids;
    }
}

/// <summary>
/// The browser UI's files (spec 4.2, <c>web=1</c>): looked up inside the web
/// root only, and only for the types a static site is made of, so nothing
/// else on the disk is reachable over HTTP. Mirrors the Mac's LinkWebBundle.
/// </summary>
public static class LinkWebRoot
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["html"] = "text/html; charset=utf-8",
        ["js"] = "text/javascript; charset=utf-8",
        ["css"] = "text/css; charset=utf-8",
        ["json"] = "application/json",
        ["svg"] = "image/svg+xml",
        ["png"] = "image/png",
        ["ico"] = "image/x-icon",
        ["woff2"] = "font/woff2",
        ["map"] = "application/json",
        ["webmanifest"] = "application/manifest+json",
    };

    /// <summary>The content type served for a file extension (no dot), or
    /// null when the type isn't served.</summary>
    public static string? ContentType(string extension) =>
        Types.TryGetValue(extension.ToLowerInvariant(), out var t) ? t : null;

    public static bool IsPresent(string webRoot) => File.Exists(Path.Combine(webRoot, "index.html"));

    /// <summary>
    /// Maps a request path (query already removed) to a file under
    /// <paramref name="webRoot"/>. <c>/</c>, and any path whose last segment
    /// has no extension outside <c>/dspi/</c>, serve index.html, as the app
    /// routes on the client (Mac parity). Refused: <c>..</c> and <c>.</c>
    /// segments, dot-files, empty segments (so no rooted or UNC path),
    /// backslashes, colons (drive letters, NTFS streams), control
    /// characters, and any type outside the allow-list.
    /// </summary>
    public static bool TryResolve(string webRoot, string path, out string file, out string contentType)
    {
        file = "";
        contentType = "";
        if (!path.StartsWith('/')) return false;
        string decoded;
        try { decoded = Uri.UnescapeDataString(path); }
        catch (UriFormatException) { return false; }
        if (decoded.Any(c => c < 0x20 || c == 0x7F || c == '\\' || c == ':')) return false;

        var name = decoded[1..];
        var segments = name.Split('/');
        if (!segments[^1].Contains('.'))
        {
            // The root or a client-side route; /dspi/ belongs to the protocol.
            if (segments[0].Equals("dspi", StringComparison.OrdinalIgnoreCase)) return false;
            name = "index.html";
            segments = [name];
        }
        foreach (var s in segments)
        {
            // Windows drops trailing dots and spaces, so "x.js." would open x.js.
            if (s.Length == 0 || s.StartsWith('.') || s.EndsWith('.') || s.EndsWith(' ')) return false;
        }
        var type = ContentType(Path.GetExtension(segments[^1]).TrimStart('.'));
        if (type == null) return false;

        var root = Path.GetFullPath(webRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return false;
        file = full;
        contentType = type;
        return true;
    }
}

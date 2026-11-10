using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>
/// The hub's server over real loopback sockets (port 0): HTTP routes, the
/// upgrade rules, and the per-connection deadlines and backpressure with
/// shortened tunings. The handler is a fake that records what it is given.
/// </summary>
public sealed class LinkServerTests : IDisposable
{
    private readonly List<IDisposable> _cleanup = new();

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_cleanup)) d.Dispose();
    }

    // ── Fixture ──

    private sealed class FakeConnection : ILinkConnection
    {
        public required string Peer { get; init; }
        public required Action<LinkOutbound> Emit { get; init; }
        public volatile bool Authenticated = true;
        public readonly ConcurrentQueue<string> Texts = new();
        public readonly ConcurrentQueue<byte[]> Binaries = new();
        public int ClosedCount;
        private int _inside;
        public int MaxInside;
        public Action<FakeConnection, string>? OnText;
        public TimeSpan Work = TimeSpan.Zero;

        private void Enter()
        {
            int n = Interlocked.Increment(ref _inside);
            int max;
            while ((max = MaxInside) < n && Interlocked.CompareExchange(ref MaxInside, n, max) != max) { }
            if (Work > TimeSpan.Zero) Thread.Sleep(Work);
        }

        private void Leave() => Interlocked.Decrement(ref _inside);

        public bool IsAuthenticated
        {
            get { Enter(); try { return Authenticated; } finally { Leave(); } }
        }

        public void ReceiveText(string json)
        {
            Enter();
            try
            {
                Texts.Enqueue(json);
                OnText?.Invoke(this, json);
            }
            finally { Leave(); }
        }

        public void ReceiveBinary(byte[] data)
        {
            Enter();
            try { Binaries.Enqueue(data); } finally { Leave(); }
        }

        public void ConnectionClosed()
        {
            Enter();
            try { Interlocked.Increment(ref ClosedCount); } finally { Leave(); }
        }
    }

    private readonly ConcurrentQueue<FakeConnection> _connections = new();
    private Action<FakeConnection>? _configure;

    private const string InfoJson = """{"proto":{"major":1,"minor":2},"hub":{"id":"h","name":"Test","kind":"console"},"auth":"none","ws":"/dspi/v1"}""";

    private (LinkServer Server, int Port) StartServer(LinkServerTuning? tuning = null, string? webRoot = null,
        IReadOnlyCollection<string>? identities = null)
    {
        var server = new LinkServer((peer, emit) =>
            {
                var c = new FakeConnection { Peer = peer, Emit = emit };
                _configure?.Invoke(c);
                _connections.Enqueue(c);
                return c;
            },
            () => InfoJson, webRoot, tuning,
            identities == null ? null : () => identities);
        server.Start(0);
        _cleanup.Add(server);
        return (server, server.BoundPort!.Value);
    }

    private static async Task<ClientWebSocket> ConnectAsync(int port, bool subProtocol = true, string? origin = null,
        int? receiveBuffer = null, string host = "127.0.0.1")
    {
        var ws = new ClientWebSocket();
        if (subProtocol) ws.Options.AddSubProtocol(LinkService.SubProtocol);
        if (origin != null) ws.Options.SetRequestHeader("Origin", origin);
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var uri = new Uri($"ws://{host}:{port}/dspi/v1");
        if (receiveBuffer is { } size)
        {
            // A small receive window so a client that stops reading backs the
            // server up quickly.
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, token) =>
                {
                    var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = size, NoDelay = true };
                    await s.ConnectAsync(ctx.DnsEndPoint, token);
                    return new NetworkStream(s, ownsSocket: true);
                },
            };
            await ws.ConnectAsync(uri, new HttpMessageInvoker(handler), cts.Token);
        }
        else
        {
            await ws.ConnectAsync(uri, cts.Token);
        }
        return ws;
    }

    private async Task<FakeConnection> NextConnectionAsync(int index = 0)
    {
        await WaitUntilAsync(() => _connections.Count > index);
        return _connections.ElementAt(index);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>Reads until the server's close; returns its code (null if the
    /// socket died without one).</summary>
    private static async Task<int?> ReadUntilCloseAsync(WebSocket ws, int timeoutMs = 10000, List<byte[]>? frames = null)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[70000];
        var message = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
                    return (int?)r.CloseStatus;
                }
                message.Write(buffer, 0, r.Count);
                if (r.EndOfMessage)
                {
                    frames?.Add(message.ToArray());
                    message.SetLength(0);
                }
            }
        }
        catch (WebSocketException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    private static HttpClient Http()
    {
        return new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>A raw request, so paths reach the server unnormalised.</summary>
    private static async Task<(int Status, string Head, byte[] Body)> RawAsync(int port, string requestHead)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(requestHead));
        var all = new MemoryStream();
        using var cts = new CancellationTokenSource(10000);
        var buf = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
        {
            all.Write(buf, 0, n);
            if (requestHead.Contains("Upgrade:") && Encoding.ASCII.GetString(all.ToArray()).Contains("\r\n\r\n")) break;
        }
        var bytes = all.ToArray();
        var text = Encoding.ASCII.GetString(bytes);
        int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var body = split < 0 ? [] : bytes[(split + 4)..];
        int status = int.Parse(head.Split(' ')[1]);
        return (status, head, body);
    }

    private static string Get(string path) => $"GET {path} HTTP/1.1\r\nHost: test\r\n\r\n";

    private static string Upgrade(string? protocol = LinkService.SubProtocol, string? origin = null) =>
        "GET /dspi/v1 HTTP/1.1\r\nHost: test\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
        "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n" +
        (protocol == null ? "" : $"Sec-WebSocket-Protocol: {protocol}\r\n") +
        (origin == null ? "" : $"Origin: {origin}\r\n") + "\r\n";

    private string MakeWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "linkweb-" + Guid.NewGuid().ToString("N"));
        var web = Path.Combine(root, "Web");
        Directory.CreateDirectory(Path.Combine(web, "assets"));
        File.WriteAllText(Path.Combine(web, "index.html"), "<html>hi</html>");
        File.WriteAllText(Path.Combine(web, "assets", "app-abc123.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(web, "assets", "style--x.css"), "body{}");
        File.WriteAllText(Path.Combine(web, "keys.txt"), "k");
        File.WriteAllText(Path.Combine(web, ".hidden.js"), "h");
        File.WriteAllText(Path.Combine(root, "secret.js"), "secret");
        _cleanup.Add(new Deleter(root));
        return web;
    }

    private sealed class Deleter(string path) : IDisposable
    {
        public void Dispose() { try { Directory.Delete(path, true); } catch { } }
    }

    // ── Lifecycle ──

    [Fact]
    public async Task StartBindsAPortAndStopReleasesIt()
    {
        var (server, port) = StartServer();
        Assert.True(server.IsRunning);
        Assert.True(port > 0);
        server.Start(port); // idempotent
        server.Stop();
        Assert.False(server.IsRunning);
        Assert.Null(server.BoundPort);
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public void APortInUseIsAClearError()
    {
        var (_, port) = StartServer();
        var second = new LinkServer((_, _) => throw new InvalidOperationException(), () => "{}", null);
        var e = Assert.Throws<LinkServerStartException>(() => second.Start(port));
        Assert.True(e.PortInUse);
        Assert.Equal(port, e.Port);
        Assert.Contains(port.ToString(), e.Message);
        Assert.False(second.IsRunning);
    }

    [Fact]
    public async Task ReachableOverIPv4AndIPv6()
    {
        var (_, port) = StartServer();
        using var http = Http();
        foreach (var host in new[] { "127.0.0.1", "[::1]", "localhost" })
        {
            var r = await http.GetAsync($"http://{host}:{port}{LinkService.InfoPath}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
    }

    // ── HTTP ──

    [Fact]
    public async Task InfoServesTheOwnersJsonWithCorsOnlyThere()
    {
        var (_, port) = StartServer(webRoot: MakeWebRoot());
        using var http = Http();
        var info = await http.GetAsync($"http://127.0.0.1:{port}{LinkService.InfoPath}?x=1");
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.Equal("application/json", info.Content.Headers.ContentType?.MediaType);
        Assert.Equal("*", info.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal(InfoJson, await info.Content.ReadAsStringAsync());
        Assert.Equal("Test", LinkInfo.Decode(InfoJson).Hub.Name);

        var page = await http.GetAsync($"http://127.0.0.1:{port}/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.False(page.Headers.Contains("Access-Control-Allow-Origin"));
        var missing = await http.GetAsync($"http://127.0.0.1:{port}/nope.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.False(missing.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task InfoThatThrowsIs500()
    {
        var server = new LinkServer((_, _) => throw new InvalidOperationException(), () => throw new InvalidOperationException("x"), null);
        server.Start(0);
        _cleanup.Add(server);
        var r = await RawAsync(server.BoundPort!.Value, Get(LinkService.InfoPath));
        Assert.Equal(500, r.Status);
    }

    [Fact]
    public async Task StaticFilesAreServedWithTheirTypeAndNoCache()
    {
        var (server, port) = StartServer(webRoot: MakeWebRoot());
        Assert.True(server.ServesWeb);
        using var http = Http();
        var index = await http.GetAsync($"http://127.0.0.1:{port}/");
        Assert.Equal("<html>hi</html>", await index.Content.ReadAsStringAsync());
        Assert.Equal("text/html; charset=utf-8", index.Content.Headers.ContentType?.ToString());
        Assert.True(index.Headers.CacheControl?.NoCache);
        Assert.Equal("close", index.Headers.Connection.Single());

        var js = await http.GetAsync($"http://127.0.0.1:{port}/assets/app-abc123.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", js.Content.Headers.ContentType?.ToString());
        Assert.Equal("console.log(1)", await js.Content.ReadAsStringAsync());

        var route = await http.GetAsync($"http://127.0.0.1:{port}/devices/3");
        Assert.Equal("<html>hi</html>", await route.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/../secret.js")]
    [InlineData("/assets/../../secret.js")]
    [InlineData("/%2e%2e/secret.js")]
    [InlineData("/%2E%2E%2Fsecret.js")]
    [InlineData("/..%5csecret.js")]
    [InlineData("/assets\\..\\..\\secret.js")]
    [InlineData("//localhost/c$/secret.js")]
    [InlineData("/C:/Windows/win.ini")]
    [InlineData("/index.html::$DATA")]
    [InlineData("/keys.txt")]
    [InlineData("/.hidden.js")]
    [InlineData("/missing.js")]
    [InlineData("/index.html.")]
    [InlineData("/dspi/v2")]
    [InlineData("/dspi/v1/other")]
    [InlineData("/assets/app-abc123.js%00.html")]
    public async Task TraversalDisallowedTypesAndUnknownPathsAre404(string path)
    {
        var (_, port) = StartServer(webRoot: MakeWebRoot());
        var r = await RawAsync(port, Get(path));
        Assert.Equal(404, r.Status);
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(r.Body));
    }

    [Fact]
    public async Task WithoutAWebRootEverythingButTheProtocolIs404()
    {
        var (server, port) = StartServer();
        Assert.False(server.ServesWeb);
        Assert.Equal(404, (await RawAsync(port, Get("/"))).Status);
        Assert.Equal(404, (await RawAsync(port, Get("/index.html"))).Status);
        Assert.Equal(200, (await RawAsync(port, Get(LinkService.InfoPath))).Status);
    }

    [Fact]
    public async Task OtherMethodsAndBadRequestsAreRefused()
    {
        var (_, port) = StartServer();
        Assert.Equal(405, (await RawAsync(port, $"POST {LinkService.InfoPath} HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\n\r\nabc")).Status);
        Assert.Equal(400, (await RawAsync(port, "NONSENSE\r\n\r\n")).Status);
        Assert.Equal(400, (await RawAsync(port, Get("/dspi/v1"))).Status);
        var huge = $"GET / HTTP/1.1\r\nX-Big: {new string('a', 9000)}\r\n\r\n";
        Assert.Equal(431, (await RawAsync(port, huge)).Status);
    }

    [Fact]
    public void EveryShippedWebFilePassesTheAllowList()
    {
        // The app's frozen bundle (DSPiConsole/Web), found from the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DSPiConsole", "Web"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var web = Path.Combine(dir!.FullName, "DSPiConsole", "Web");
        Assert.True(LinkWebRoot.IsPresent(web));
        var files = Directory.GetFiles(web, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var path = "/" + Path.GetRelativePath(web, file).Replace('\\', '/');
            Assert.True(LinkWebRoot.TryResolve(web, path, out var resolved, out _), path);
            Assert.Equal(Path.GetFullPath(file), resolved, ignoreCase: true);
        }
    }

    // ── Upgrade ──

    [Fact]
    public async Task UpgradeNeedsTheSubProtocolAndEchoesIt()
    {
        var (_, port) = StartServer();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(port, subProtocol: false));
        Assert.Equal(400, (await RawAsync(port, Upgrade(protocol: null))).Status);
        Assert.Equal(400, (await RawAsync(port, Upgrade(protocol: "other-1"))).Status);

        var ok = await RawAsync(port, Upgrade(protocol: "other-1, dspi-link-1"));
        Assert.Equal(101, ok.Status);
        Assert.Contains("Sec-WebSocket-Protocol: dspi-link-1", ok.Head);
        Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", ok.Head); // RFC 6455's example

        using var ws = await ConnectAsync(port);
        Assert.Equal(LinkService.SubProtocol, ws.SubProtocol);
    }

    [Fact]
    public async Task OriginMustNameThisHub()
    {
        var (_, port) = StartServer(identities: ["studio-pc", "studio-pc.local", "192.168.1.20"]);
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(port, origin: "http://evil.example"));
        Assert.Equal(403, (await RawAsync(port, Upgrade(origin: "http://evil.example"))).Status);
        Assert.Equal(403, (await RawAsync(port, Upgrade(origin: "null"))).Status);
        Assert.Equal(101, (await RawAsync(port, Upgrade(origin: "http://studio-pc.local:11915"))).Status);
        Assert.Equal(101, (await RawAsync(port, Upgrade(origin: "http://192.168.1.20:11915"))).Status);
        using var loopback = await ConnectAsync(port, origin: $"http://127.0.0.1:{port}");
        using var native = await ConnectAsync(port);
        Assert.Equal(WebSocketState.Open, loopback.State);
        Assert.Equal(WebSocketState.Open, native.State);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://studio-pc.local:11915", true)]
    [InlineData("https://STUDIO-PC.local", true)]
    [InlineData("http://studio-pc.local.", true)]
    [InlineData("http://studio-pc", true)]
    [InlineData("http://192.168.1.20:11915", true)]
    [InlineData("http://[::ffff:192.168.1.20]:11915", true)]
    [InlineData("http://[fd00::20]", true)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("http://127.0.0.1:11915", true)]
    [InlineData("http://127.9.9.9", true)]
    [InlineData("http://[::1]:11915", true)]
    [InlineData("http://evil.example", false)]
    [InlineData("http://studio-pc.local.evil.example", false)]
    [InlineData("http://evil-studio-pc.local", false)]
    [InlineData("http://192.168.1.21", false)]
    [InlineData("http://[fd00::21]", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    [InlineData("file://studio-pc/x", false)]
    [InlineData("ftp://studio-pc.local", false)]
    [InlineData("http://user@studio-pc.local", false)]
    [InlineData("not a url", false)]
    public void OriginCheck(string? origin, bool allowed) =>
        Assert.Equal(allowed, LinkServer.IsAllowedOrigin(origin, ["studio-pc", "studio-pc.local", "192.168.1.20", "fd00::20"]));

    [Fact]
    public void CurrentIdentitiesHoldTheHostNameAndLocalForm()
    {
        var ids = LinkServer.CurrentIdentities();
        Assert.Contains(Dns.GetHostName(), ids, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Dns.GetHostName() + ".local", ids, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1", ids);
    }

    // ── Peer gate ──

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%12")]
    [InlineData("febf::1")]
    [InlineData("fd00::1")]
    [InlineData("fc00::1")]
    [InlineData("fd7a:115c:a1e0::1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:100.100.1.1")]
    public void PeerGateAcceptsLocalRanges(string ip) =>
        Assert.True(LinkServer.IsLocalPeer(IPAddress.Parse(ip), []), ip);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("1.1.1.1")]
    [InlineData("192.169.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("fec0::1")]
    [InlineData("fe00::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("::")]
    public void PeerGateRejectsPublicAddresses(string ip) =>
        Assert.False(LinkServer.IsLocalPeer(IPAddress.Parse(ip), []), ip);

    [Fact]
    public void PeerGateAcceptsOnLinkPublicAddresses()
    {
        LinkSubnet[] nets =
        [
            new(IPAddress.Parse("2001:db8:1:2::10"), 64),
            new(IPAddress.Parse("203.0.113.5"), 24),
            new(IPAddress.Parse("198.51.100.7"), 23),
            new(IPAddress.Parse("::"), 0),      // a default route matches nothing
            new(IPAddress.Parse("0.0.0.0"), 0),
        ];
        Assert.True(LinkServer.IsLocalPeer(IPAddress.Parse("2001:db8:1:2:abcd::1"), nets));
        Assert.True(LinkServer.IsLocalPeer(IPAddress.Parse("203.0.113.200"), nets));
        Assert.True(LinkServer.IsLocalPeer(IPAddress.Parse("::ffff:203.0.113.9"), nets));
        Assert.True(LinkServer.IsLocalPeer(IPAddress.Parse("198.51.101.1"), nets));
        Assert.False(LinkServer.IsLocalPeer(IPAddress.Parse("198.51.102.1"), nets));
        Assert.False(LinkServer.IsLocalPeer(IPAddress.Parse("2001:db8:1:3::1"), nets));
        Assert.False(LinkServer.IsLocalPeer(IPAddress.Parse("203.0.114.1"), nets));
        Assert.False(LinkServer.IsLocalPeer(IPAddress.Parse("8.8.8.8"), nets));
        Assert.False(LinkServer.IsLocalPeer(IPAddress.Parse("2600::1"), nets));
        Assert.NotEmpty(LinkServer.LocalSubnets()); // loopback at least is up
    }

    // ── Messages ──

    [Fact]
    public async Task TextAndBinaryReachTheHandlerAndItsOutputReachesTheClient()
    {
        _configure = c => c.OnText = (conn, json) => conn.Emit(new LinkOutbound.Text("echo:" + json));
        var (server, port) = StartServer();
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        Assert.Equal("127.0.0.1", conn.Peer);
        Assert.True(await WaitUntilAsync(() => server.ConnectionCount == 1));

        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"t":"hello"}"""), WebSocketMessageType.Text, true, default);
        await ws.SendAsync(new byte[] { 1, 0, 7, 0 }, WebSocketMessageType.Binary, true, default);
        Assert.True(await WaitUntilAsync(() => conn.Texts.Count == 1 && conn.Binaries.Count == 1));
        Assert.Equal("""{"t":"hello"}""", conn.Texts.Single());
        Assert.Equal(new byte[] { 1, 0, 7, 0 }, conn.Binaries.Single());

        var buffer = new byte[1024];
        var r = await ws.ReceiveAsync(buffer, new CancellationTokenSource(5000).Token);
        Assert.Equal(WebSocketMessageType.Text, r.MessageType);
        Assert.Equal("""echo:{"t":"hello"}""", Encoding.UTF8.GetString(buffer, 0, r.Count));

        conn.Emit(new LinkOutbound.Binary([2, 0, 1, 0, 9]));
        r = await ws.ReceiveAsync(buffer, new CancellationTokenSource(5000).Token);
        Assert.Equal(WebSocketMessageType.Binary, r.MessageType);
        Assert.Equal(new byte[] { 2, 0, 1, 0, 9 }, buffer[..r.Count]);
    }

    [Fact]
    public async Task CallsIntoOneConnectionAreNeverConcurrent()
    {
        _configure = c => c.Work = TimeSpan.FromMilliseconds(3);
        var (_, port) = StartServer(new LinkServerTuning { AuthDeadline = TimeSpan.FromMilliseconds(50) });
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        for (int i = 0; i < 30; i++)
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes($$"""{"n":{{i}}}"""), WebSocketMessageType.Text, true, default);
            await ws.SendAsync(new byte[] { 1, 0, (byte)i, 0 }, WebSocketMessageType.Binary, true, default);
        }
        Assert.True(await WaitUntilAsync(() => conn.Texts.Count == 30 && conn.Binaries.Count == 30));
        Assert.Equal(Enumerable.Range(0, 30).Select(i => $$"""{"n":{{i}}}"""), conn.Texts);
        Assert.Equal(1, conn.MaxInside);
    }

    [Fact]
    public async Task FragmentedMessagesAreReassembled()
    {
        var (_, port) = StartServer();
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        var payload = Enumerable.Range(0, 40000).Select(i => (byte)i).ToArray();
        for (int at = 0; at < payload.Length; at += 10000)
            await ws.SendAsync(payload.AsMemory(at, 10000), WebSocketMessageType.Binary, at + 10000 >= payload.Length, default);
        Assert.True(await WaitUntilAsync(() => conn.Binaries.Count == 1));
        Assert.Equal(payload, conn.Binaries.Single());
    }

    [Fact]
    public async Task AMessageOverTheLimitClosesWith4000()
    {
        var (_, port) = StartServer();
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        var chunk = new byte[16384];
        var reader = ReadUntilCloseAsync(ws);
        for (int i = 0; i < 5; i++) // 81920 bytes, fragmented
            await ws.SendAsync(chunk, WebSocketMessageType.Binary, i == 4, default);
        Assert.Equal(LinkCloseCode.ProtocolError, await reader);
        Assert.Empty(conn.Binaries);
        Assert.True(await WaitUntilAsync(() => conn.ClosedCount == 1));
    }

    [Fact]
    public async Task ASingleFrameOverTheLimitClosesWith4000()
    {
        var (_, port) = StartServer();
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        var reader = ReadUntilCloseAsync(ws);
        await ws.SendAsync(new byte[LinkServerTuning.Default.MaxMessage + 1], WebSocketMessageType.Text, true, default);
        Assert.Equal(LinkCloseCode.ProtocolError, await reader);
        Assert.Empty(conn.Texts);
    }

    [Fact]
    public async Task AMessageAtTheLimitIsDelivered()
    {
        var (_, port) = StartServer();
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        await ws.SendAsync(new byte[LinkServerTuning.Default.MaxMessage], WebSocketMessageType.Binary, true, default);
        Assert.True(await WaitUntilAsync(() => conn.Binaries.Count == 1));
        Assert.Equal(65536, conn.Binaries.Single().Length);
    }

    // ── Closing ──

    [Fact]
    public async Task CloseFromTheHandlerReachesTheClientWithItsCode()
    {
        _configure = c => c.OnText = (conn, json) =>
        {
            conn.Emit(new LinkOutbound.Text("bye"));
            conn.Emit(new LinkOutbound.Close(LinkCloseCode.TokenRevoked));
            conn.Emit(new LinkOutbound.Text("after close")); // dropped
        };
        var (server, port) = StartServer();
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        var frames = new List<byte[]>();
        var reader = ReadUntilCloseAsync(ws, frames: frames);
        await ws.SendAsync("x"u8.ToArray(), WebSocketMessageType.Text, true, default);
        Assert.Equal(LinkCloseCode.TokenRevoked, await reader);
        Assert.Equal(["bye"], frames.Select(f => Encoding.UTF8.GetString(f)));
        Assert.True(await WaitUntilAsync(() => conn.ClosedCount == 1 && server.ConnectionCount == 0));
        await Task.Delay(200);
        Assert.Equal(1, conn.ClosedCount);
    }

    [Fact]
    public async Task ConnectionClosedRunsExactlyOnceWhateverEndsTheConnection()
    {
        var (server, port) = StartServer();
        int changes = 0;
        server.ConnectionCountChanged += (_, _) => Interlocked.Increment(ref changes);

        // The client closes normally.
        var a = await ConnectAsync(port);
        var ca = await NextConnectionAsync(0);
        await a.CloseAsync(WebSocketCloseStatus.NormalClosure, null, new CancellationTokenSource(5000).Token);
        Assert.Equal(WebSocketState.Closed, a.State);

        // The client vanishes without a close.
        var b = await ConnectAsync(port);
        var cb = await NextConnectionAsync(1);
        b.Abort();

        // The server stops.
        var c = await ConnectAsync(port);
        var cc = await NextConnectionAsync(2);
        var reader = ReadUntilCloseAsync(c);
        Assert.True(await WaitUntilAsync(() => ca.ClosedCount == 1 && cb.ClosedCount == 1 && server.ConnectionCount == 1));
        await server.StopAsync();
        Assert.Equal(LinkCloseCode.ShuttingDown, await reader);
        Assert.Equal(1, cc.ClosedCount); // StopAsync waited for it

        await Task.Delay(300);
        Assert.Equal([1, 1, 1], new[] { ca.ClosedCount, cb.ClosedCount, cc.ClosedCount });
        Assert.Equal(0, server.ConnectionCount);
        Assert.Equal(6, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task StopClosesWithTheGivenCode()
    {
        var (server, port) = StartServer();
        using var ws = await ConnectAsync(port);
        var reader = ReadUntilCloseAsync(ws);
        await NextConnectionAsync();
        server.Stop(LinkCloseCode.KeepaliveFailure);
        Assert.Equal(LinkCloseCode.KeepaliveFailure, await reader);
    }

    // ── Deadlines ──

    [Fact]
    public async Task UnauthenticatedAfterTheDeadlineClosesWith4001()
    {
        _configure = c => c.Authenticated = false;
        var (_, port) = StartServer(new LinkServerTuning { AuthDeadline = TimeSpan.FromMilliseconds(300) });
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        var started = DateTime.UtcNow;
        Assert.Equal(LinkCloseCode.AuthFailed, await ReadUntilCloseAsync(ws));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(200));
        Assert.True(await WaitUntilAsync(() => conn.ClosedCount == 1));
    }

    [Fact]
    public async Task AuthenticatedBeforeTheDeadlineStaysOpen()
    {
        var (_, port) = StartServer(new LinkServerTuning { AuthDeadline = TimeSpan.FromMilliseconds(200) });
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        var reader = ReadUntilCloseAsync(ws);
        await Task.Delay(1000);
        Assert.False(reader.IsCompleted);
        Assert.Equal(0, conn.ClosedCount);
    }

    [Fact]
    public async Task AClientThatSendsNothingIsClosedWith1001()
    {
        // A raw client: it completes the upgrade, then never sends a byte, so
        // never answers a ping.
        var (_, port) = StartServer(new LinkServerTuning
        {
            PingInterval = TimeSpan.FromMilliseconds(200),
            IdleDeadline = TimeSpan.FromMilliseconds(600),
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(Upgrade()));
        var raw = new MemoryStream();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(10000);
        var started = DateTime.UtcNow;
        int? code = null;
        int pings = 0;
        // Read only until the close frame: the server then drops a peer that
        // never answers it, which may reset the connection.
        while (code == null)
        {
            int n = await stream.ReadAsync(buf, cts.Token);
            Assert.True(n > 0, "the connection ended before a close frame");
            raw.Write(buf, 0, n);
            var bytes = raw.ToArray();
            int body = Encoding.ASCII.GetString(bytes).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (body < 0) continue;
            // Server frames are unmasked: [opcode][len][payload]. Pings, then the close.
            pings = 0;
            for (int at = body + 4; at + 2 <= bytes.Length;)
            {
                int opcode = bytes[at] & 0x0F, len = bytes[at + 1] & 0x7F;
                if (at + 2 + len > bytes.Length) break;
                if (opcode == 0x9) pings++;
                if (opcode == 0x8 && len >= 2) code = bytes[at + 2] << 8 | bytes[at + 3];
                at += 2 + len;
            }
        }
        Assert.Equal(LinkCloseCode.KeepaliveFailure, code);
        Assert.True(pings >= 1, "the hub pinged first");
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task AClientThatAnswersPingsStaysConnected()
    {
        var (server, port) = StartServer(new LinkServerTuning
        {
            PingInterval = TimeSpan.FromMilliseconds(200),
            IdleDeadline = TimeSpan.FromMilliseconds(600),
        });
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        // ClientWebSocket only pongs while a receive is pending.
        var reader = ReadUntilCloseAsync(ws);
        await Task.Delay(2000);
        Assert.False(reader.IsCompleted);
        Assert.Equal(0, conn.ClosedCount);
        Assert.Equal(1, server.ConnectionCount);
    }

    // ── Backpressure ──

    private static byte[] Notify(byte handle, int size = 64)
    {
        var f = new byte[size];
        f[0] = (byte)LinkFrameType.Notify;
        f[4] = handle;
        return f;
    }

    private static byte[] Poll(byte handle, int size = 64)
    {
        var f = new byte[size];
        f[0] = (byte)LinkFrameType.Poll;
        f[4] = handle;
        return f;
    }

    [Fact]
    public async Task ALaggingClientLosesPollsAndIsOwedAResync()
    {
        var (server, port) = StartServer(new LinkServerTuning
        {
            OutboundHighWater = 4096,
            OutboundLowWater = 1024,
            SocketSendBuffer = 4096,
        });
        using var ws = await ConnectAsync(port, receiveBuffer: 4096);
        var conn = await NextConnectionAsync();

        // Not reading: fill everything until the server starts shedding.
        int notifies = 0, polls = 0;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (server.FramesShed < 50 && DateTime.UtcNow < until)
        {
            conn.Emit(new LinkOutbound.Binary(Notify(3, 512)));
            conn.Emit(new LinkOutbound.Binary(Poll(3, 512)));
            notifies++;
            polls++;
            if (notifies % 64 == 0) await Task.Delay(1);
        }
        Assert.True(server.FramesShed >= 50, "the server shed frames for a client that stopped reading");
        conn.Emit(new LinkOutbound.Text("after the lag")); // text is never shed

        // Read again: the queue drains, then the owed RESYNC arrives.
        var frames = new List<byte[]>();
        var buffer = new byte[70000];
        using var cts = new CancellationTokenSource(10000);
        bool sawResync = false, sawText = false;
        while (!(sawResync && sawText))
        {
            var r = await ws.ReceiveAsync(buffer, cts.Token);
            var f = buffer[..r.Count];
            if (r.MessageType == WebSocketMessageType.Text) sawText = true;
            else
            {
                frames.Add(f);
                if (f[0] == (byte)LinkFrameType.Resync)
                {
                    var resync = Assert.IsType<LinkResyncFrame>(LinkFrame.Decode(f));
                    Assert.Equal(3, resync.Handle);
                    Assert.Equal(LinkResyncReason.NotificationsDropped, resync.KnownReason);
                    sawResync = true;
                }
            }
        }
        int gotNotifies = frames.Count(f => f[0] == (byte)LinkFrameType.Notify);
        int gotPolls = frames.Count(f => f[0] == (byte)LinkFrameType.Poll);
        Assert.True(gotNotifies < notifies, $"{gotNotifies} of {notifies} notifies");
        Assert.True(gotPolls < polls, $"{gotPolls} of {polls} polls");
        Assert.Equal(1, frames.Count(f => f[0] == (byte)LinkFrameType.Resync)); // folded into one
        Assert.Equal(0, conn.ClosedCount);
    }

    [Fact]
    public async Task OneResyncIsOwedPerDeviceThatLostNotifies()
    {
        var (server, port) = StartServer(new LinkServerTuning
        {
            OutboundHighWater = 4096,
            OutboundLowWater = 1024,
            SocketSendBuffer = 4096,
        });
        using var ws = await ConnectAsync(port, receiveBuffer: 4096);
        var conn = await NextConnectionAsync();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (server.FramesShed < 20 && DateTime.UtcNow < until)
            conn.Emit(new LinkOutbound.Binary(Notify(1, 512)));
        conn.Emit(new LinkOutbound.Binary(Notify(2, 512)));
        conn.Emit(new LinkOutbound.Binary(Notify(1, 512)));
        conn.Emit(new LinkOutbound.Text("end"));

        var resyncs = new List<byte>();
        var buffer = new byte[70000];
        using var cts = new CancellationTokenSource(10000);
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, cts.Token);
            if (r.MessageType == WebSocketMessageType.Text) { if (resyncs.Count == 2) break; continue; }
            if (buffer[0] == (byte)LinkFrameType.Resync) resyncs.Add(buffer[4]);
            if (resyncs.Count == 2) break;
        }
        Assert.Equal(new byte[] { 1, 2 }, resyncs.Order());
    }

    [Fact]
    public async Task AClientThatStopsReadingIsDroppedAtTheStallDeadline()
    {
        var (server, port) = StartServer(new LinkServerTuning
        {
            StallDeadline = TimeSpan.FromMilliseconds(300),
            OutboundHighWater = 4096,
            OutboundLowWater = 1024,
            SocketSendBuffer = 4096,
        });
        using var ws = await ConnectAsync(port, receiveBuffer: 4096);
        var conn = await NextConnectionAsync();
        var text = new string('x', 1024);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (conn.ClosedCount == 0 && DateTime.UtcNow < until)
        {
            for (int i = 0; i < 32; i++) conn.Emit(new LinkOutbound.Text(text));
            await Task.Delay(5);
        }
        Assert.Equal(1, conn.ClosedCount);
        Assert.True(await WaitUntilAsync(() => server.ConnectionCount == 0));
    }

    [Fact]
    public async Task AClientThatOutrunsTheHandlerIsClosedWith1013()
    {
        _configure = c => c.Work = TimeSpan.FromMilliseconds(50);
        var (_, port) = StartServer(new LinkServerTuning { InboundLimit = 8192 });
        using var ws = await ConnectAsync(port);
        var conn = await NextConnectionAsync();
        var reader = ReadUntilCloseAsync(ws);
        try
        {
            for (int i = 0; i < 64; i++)
                await ws.SendAsync(new byte[1024], WebSocketMessageType.Binary, true, default);
        }
        catch (WebSocketException) { }
        Assert.Equal(LinkCloseCode.TooFast, await reader);
        Assert.True(conn.Binaries.Count < 64);
    }
}

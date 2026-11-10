using DSPiConsole.Core.Link;
using DSPiConsole.Core.Tests.Link;
using DSPiConsole.Usb;
using DSPiConsole.Usb.Transport;

namespace DSPiConsole.Core.Tests.Transport;

/// <summary>A device on a remote hub, driven through <see cref="NetworkTransport"/>
/// against a scripted hub.</summary>
public class NetworkTransportTests : IDisposable
{
    private const string HubId = "6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b";
    private const string Serial = "E46058388B1A2E2C";
    private static readonly LinkEndpoint Endpoint = new("studio.local");
    private readonly string _tokenPath = Path.Combine(Path.GetTempPath(), $"dspi-nt-{Guid.NewGuid():N}.dat");

    public void Dispose()
    {
        try { File.Delete(_tokenPath); } catch { }
    }

    private (NetworkTransport Transport, LinkTokenStore Tokens) Make(FakeLinkHub hub, Func<bool>? ui = null)
    {
        var tokens = new LinkTokenStore(_tokenPath);
        var client = new LinkClient(hub.OpenAsync, "1.1.6") { RequestTimeout = TimeSpan.FromSeconds(2), BulkTimeout = TimeSpan.FromSeconds(2) };
        var t = new NetworkTransport(client, HubId, "Studio Mac", tokens, "Studio PC", ui)
        {
            BackgroundWait = TimeSpan.FromSeconds(3),
            RetryDelay = TimeSpan.FromMilliseconds(5),
        };
        return (t, tokens);
    }

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting.");
            await Task.Delay(10);
        }
    }

    private static LinkHelloHub Hello(int maxInflight = 8, params string[] caps) =>
        FakeLinkHub.Hello(LinkAuthMode.None, null, caps) with { Limits = new LinkLimits(65536, 8192, maxInflight, 20, 200000) };

    /// <summary>Open-mode handshake with a chosen hello.</summary>
    private static async Task Accept(FakeLinkHub hub, LinkHelloHub hello, ushort session = 12)
    {
        await hub.ExpectAsync<LinkHelloClient>();
        await hub.SendAsync(hello);
        var pair = await hub.ExpectAsync<LinkAuthPair>();
        await hub.SendAsync(LinkOk.WithBody(pair.Id, new LinkAuthOkBody(session, LinkRole.Admin)));
        var list = await hub.ExpectAsync<LinkDeviceListRequest>();
        await hub.SendAsync(LinkOk.WithBody(list.Id, new LinkDeviceListBody([FakeLinkHub.Board(3, Serial)])));
    }

    private async Task<NetworkTransport> Connected(FakeLinkHub hub, LinkHelloHub? hello = null)
    {
        var (t, _) = Make(hub);
        t.Bind(Serial);
        t.Connect(Endpoint);
        await Accept(hub, hello ?? Hello());
        await Until(() => t.IsConnected);
        return t;
    }

    // ── Binding ──

    [Fact]
    public async Task ABoundDeviceIsAPlaceholderUntilTheHubListsIt()
    {
        await using var hub = new FakeLinkHub();
        var (t, _) = Make(hub);
        using var _t = t;
        var connected = new List<DSPiDeviceInfo>();
        t.Connected += (_, d) => { lock (connected) connected.Add(d); };

        t.Bind(Serial);
        Assert.Equal(Serial, t.SelectedDevice?.Serial);
        Assert.Equal(DeviceLinkKind.Remote, t.SelectedDevice?.Kind);
        Assert.EndsWith("/255", t.SelectedDevice?.DevicePath);
        Assert.False(t.IsConnected);

        t.Connect(Endpoint);
        await Accept(hub, Hello());
        // The flag is set a moment before the event is raised.
        await Until(() => { lock (connected) return connected.Count == 1; });
        Assert.True(t.IsConnected);
        Assert.EndsWith("/3", t.SelectedDevice?.DevicePath);
        Assert.Equal("Living room via Studio Mac", t.SelectedDevice?.DisplayName);
        Assert.Equal(12, t.Session);
        Assert.Equal(1UL, t.Generation);
        Assert.Single(t.AvailableDevices);
    }

    [Fact]
    public async Task AnOfflineDeviceIsNotConnected()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub);
        await hub.SendAsync(new LinkDeviceChanged(FakeLinkHub.Board(3, Serial) with { State = LinkDeviceState.Offline }));
        await Until(() => !t.IsConnected);
        await hub.SendAsync(new LinkDeviceChanged(FakeLinkHub.Board(3, Serial)));
        await Until(() => t.IsConnected);
    }

    // ── Commands ──

    [Fact]
    public async Task AGetTunnelsToTheBoundHandle()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub);
        var reply = Task.Run(() => (t.ControlIn(0x7F, 0, 2, 7, out var data), data));
        var req = await hub.ExpectAsync<LinkCmdRequest>();
        Assert.Equal(3, req.Handle);
        Assert.Equal(LinkDirection.Get, req.Direction);
        Assert.Equal(7, req.WLength);
        await hub.SendAsync(new LinkCmdResponse(req.Tag, LinkStatus.Ok, [1, 1, 0x16, 9, 1, 6, 0]));
        var (status, data) = await reply;
        Assert.Equal(LinkStatus.Ok, status);
        Assert.Equal(7, data.Length);
    }

    [Fact]
    public async Task ASetGoesOutWithoutWaiting()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub);
        Assert.Equal(LinkStatus.Ok, t.ControlOut(0xD2, 0, 2, [0, 0, 0xA0, 0xC1]));
        var req = await hub.ExpectAsync<LinkCmdRequest>();
        Assert.Equal(LinkDirection.Set, req.Direction);
        Assert.Equal(new byte[] { 0, 0, 0xA0, 0xC1 }, req.Payload);
    }

    /// <summary>Plan N7: never more in flight than the hub allows; sent in order.</summary>
    [Fact]
    public async Task SendsArePacedToMaxInflight()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub, Hello(maxInflight: 2));
        for (byte i = 0; i < 5; i++) t.ControlOut(0xD2, i, 2, [i]);

        var first = await hub.ExpectAsync<LinkCmdRequest>();
        var second = await hub.ExpectAsync<LinkCmdRequest>();
        Assert.True(await hub.QuietAsync(150)); // the third waits for an answer
        Assert.Equal(0, first.WValue);
        Assert.Equal(1, second.WValue);

        await hub.SendAsync(new LinkCmdResponse(first.Tag, LinkStatus.Ok));
        var third = await hub.ExpectAsync<LinkCmdRequest>();
        Assert.Equal(2, third.WValue);
    }

    [Fact]
    public async Task RateLimitedIsRetried()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub);
        var reply = Task.Run(() => t.ControlIn(0x50, 9, 2, 41, out _));
        var a = await hub.ExpectAsync<LinkCmdRequest>();
        await hub.SendAsync(new LinkCmdResponse(a.Tag, LinkStatus.RateLimited));
        var b = await hub.ExpectAsync<LinkCmdRequest>();
        Assert.Equal(a.BRequest, b.BRequest);
        await hub.SendAsync(new LinkCmdResponse(b.Tag, LinkStatus.Ok, new byte[41]));
        Assert.Equal(LinkStatus.Ok, await reply);
    }

    /// <summary>The Mac's 0adb7f1: a UI-thread GET with no session fails at once.</summary>
    [Fact]
    public async Task AUiThreadGetWithNoSessionFailsAtOnce()
    {
        await using var hub = new FakeLinkHub();
        var (t, _) = Make(hub, ui: () => true);
        using var _t = t;
        t.Bind(Serial);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(LinkStatus.NoDevice, t.ControlIn(0x50, 9, 2, 41, out _));
        Assert.True(watch.ElapsedMilliseconds < 200);
    }

    // ── The bulk read ──

    [Fact]
    public async Task TheBulkReadUsesTheSnapshotWhenOffered()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub, Hello(8, "cmd", "notify", "poll", "snapshot"));
        var blob = new byte[6136];
        BitConverter.GetBytes((ushort)6136).CopyTo(blob, 6);
        var read = Task.Run(t.ReadBulkParams);
        var req = await hub.ExpectAsync<LinkDeviceSnapshotRequest>();
        Assert.Equal(3, req.Handle);
        await hub.SendAsync(LinkOk.WithBody(req.Id, new LinkSnapshotBody(3, 32, 10, Convert.ToBase64String(blob))));
        Assert.Equal(6136, (await read)!.Length);
    }

    /// <summary>Without a snapshot: one 0xA0 for the whole blob, never the
    /// chunked 0xA2 (a hub can't keep its session).</summary>
    [Fact]
    public async Task WithoutASnapshotTheBulkReadIsOneGet()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub, Hello(8, "cmd", "notify", "poll"));
        var read = Task.Run(t.ReadBulkParams);
        var req = await hub.ExpectAsync<LinkCmdRequest>();
        Assert.Equal(VendorCommands.GetAllParams, req.BRequest);
        Assert.Equal(BulkRead.SingleShotLength, req.WLength);
        var blob = new byte[6136];
        BitConverter.GetBytes((ushort)6136).CopyTo(blob, 6);
        await hub.SendAsync(new LinkCmdResponse(req.Tag, LinkStatus.Ok, blob));
        Assert.Equal(6136, (await read)!.Length);
    }

    [Fact]
    public async Task AShortBulkReplyIsNoBlob()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub, Hello(8, "cmd"));
        var read = Task.Run(t.ReadBulkParams);
        var req = await hub.ExpectAsync<LinkCmdRequest>();
        var blob = new byte[132]; // a bridge's truncated GET
        BitConverter.GetBytes((ushort)6136).CopyTo(blob, 6);
        await hub.SendAsync(new LinkCmdResponse(req.Tag, LinkStatus.Ok, blob));
        Assert.Null(await read);
    }

    // ── Notifications ──

    [Fact]
    public async Task NotificationsForTheBoundDeviceCarryTheirOrigin()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub);
        var seen = new List<LinkNotification>();
        t.NotificationReceived += (_, n) => { lock (seen) seen.Add(n); };

        await hub.SendAsync(new LinkNotifyFrame(1, 3, 12, [2, 2, 0, 1, 0, 0, 0, 0, 1]));
        await hub.SendAsync(new LinkNotifyFrame(2, 9, 5, [2, 2, 0, 2, 0, 0, 0, 0, 1])); // another device
        await Until(() => { lock (seen) return seen.Count >= 1; });
        await Task.Delay(100);
        lock (seen)
        {
            var n = Assert.Single(seen);
            Assert.Equal(12, n.Origin);
        }
    }

    /// <summary>A RESYNC, or a gap in the Link tag or the firmware seq, means
    /// events were lost: the view model's bulk re-read is the recovery.</summary>
    [Fact]
    public async Task AResyncOrAGapAsksForAReRead()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub);
        var invalidations = 0;
        t.NotificationReceived += (_, n) => { if (n.EventId == 0x03) Interlocked.Increment(ref invalidations); };

        await hub.SendAsync(new LinkResyncFrame(3, 1));
        await Until(() => Volatile.Read(ref invalidations) == 1);

        await hub.SendAsync(new LinkNotifyFrame(10, 3, 0, [2, 2, 0, 50, 0, 0, 0, 0, 0]));
        await hub.SendAsync(new LinkNotifyFrame(11, 3, 0, [2, 2, 0, 51, 0, 0, 0, 0, 0])); // in sequence
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref invalidations));
        await hub.SendAsync(new LinkNotifyFrame(13, 3, 0, [2, 2, 0, 52, 0, 0, 0, 0, 0])); // tag 12 lost
        await Until(() => Volatile.Read(ref invalidations) == 2);
        await hub.SendAsync(new LinkNotifyFrame(14, 3, 0, [2, 2, 0, 60, 0, 0, 0, 0, 0])); // device seq jumped
        await Until(() => Volatile.Read(ref invalidations) == 3);
    }

    // ── Failures and reconnects ──

    [Fact]
    public async Task ARevokedTokenIsForgotten()
    {
        await using var hub = new FakeLinkHub();
        var (t, tokens) = Make(hub);
        using var _t = t;
        tokens.SetToken(HubId, "old");
        t.Bind(Serial);
        t.Connect(Endpoint);
        await Accept(hub, Hello());
        await Until(() => t.IsConnected);
        await hub.CloseAsync(LinkCloseCode.TokenRevoked);
        await Until(() => !t.IsConnected);
        await Until(() => tokens.Token(HubId) == null);
    }

    [Fact]
    public async Task AProtocolErrorIsNotRetried()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub);
        await hub.CloseAsync(LinkCloseCode.ProtocolError);
        await Until(() => !t.IsConnected);
        await Task.Delay(1500);
        Assert.Equal(1, hub.Connections);
    }

    [Fact]
    public async Task AHubRestartIsReconnectedWithBackoff()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub);
        await hub.CloseAsync(LinkCloseCode.ShuttingDown);
        await Until(() => !t.IsConnected);
        await hub.ExpectAsync<FakeLinkHub.Closed>();
        await Accept(hub, Hello(), session: 13); // after the 1 s backoff
        await Until(() => t.IsConnected, 4000);
        Assert.Equal(2, hub.Connections);
        Assert.Equal(13, t.Session);
        Assert.Equal(2UL, t.Generation);
    }

    // ── Pairing ──

    [Fact]
    public async Task PairingAsksOnceAndStoresTheToken()
    {
        await using var hub = new FakeLinkHub();
        var (t, tokens) = Make(hub);
        using var _t = t;
        var asked = new List<LinkPairingRequest>();
        t.PairingPrompt = r => { lock (asked) asked.Add(r); return Task.FromResult<string?>("482913"); };
        t.Bind(Serial);
        t.Connect(Endpoint);

        await hub.ExpectAsync<LinkHelloClient>();
        await hub.SendAsync(FakeLinkHub.Hello(LinkAuthMode.Pin, LinkPinSource.Fixed));
        var pair = await hub.ExpectAsync<LinkAuthPair>();
        Assert.Equal("482913", pair.Pin);
        Assert.Equal(LinkRole.Control, pair.Role);
        await hub.SendAsync(LinkOk.WithBody(pair.Id, new LinkAuthOkBody(7, LinkRole.Control, "tok")));
        var list = await hub.ExpectAsync<LinkDeviceListRequest>();
        await hub.SendAsync(LinkOk.WithBody(list.Id, new LinkDeviceListBody([FakeLinkHub.Board(3, Serial)])));

        await Until(() => t.IsConnected);
        Assert.Equal("tok", tokens.Token(HubId));
        var request = Assert.Single(asked);
        Assert.Equal(LinkPinSource.Fixed, request.PinSource);
        Assert.Equal("Studio Mac", request.HubName);
        Assert.False(request.RetryAfterWrongPin);
    }

    [Fact]
    public async Task AWrongPinAsksAgain()
    {
        await using var hub = new FakeLinkHub();
        var (t, _) = Make(hub);
        using var _t = t;
        var asked = new List<LinkPairingRequest>();
        t.PairingPrompt = r => { lock (asked) asked.Add(r); return Task.FromResult<string?>(asked.Count == 1 ? "000000" : "482913"); };
        t.Bind(Serial);
        t.Connect(Endpoint);
        await hub.ExpectAsync<LinkHelloClient>();
        await hub.SendAsync(FakeLinkHub.Hello(LinkAuthMode.Pin));
        var wrong = await hub.ExpectAsync<LinkAuthPair>();
        await hub.SendAsync(new LinkErr(LinkErrorCode.Denied, wrong.Id, "wrong PIN"));
        var right = await hub.ExpectAsync<LinkAuthPair>();
        Assert.Equal("482913", right.Pin);
        lock (asked) Assert.True(asked[1].RetryAfterWrongPin);
    }

    [Fact]
    public async Task CancellingThePromptGivesTheConnectionUp()
    {
        await using var hub = new FakeLinkHub();
        var (t, _) = Make(hub);
        using var _t = t;
        t.PairingPrompt = _ => Task.FromResult<string?>(null);
        t.Bind(Serial);
        t.Connect(Endpoint);
        await hub.ExpectAsync<LinkHelloClient>();
        await hub.SendAsync(FakeLinkHub.Hello(LinkAuthMode.Pin));
        Assert.Equal(1000, Assert.IsType<FakeLinkHub.Closed>(await hub.NextAsync()).Code);
        await Task.Delay(1500);
        Assert.Equal(1, hub.Connections); // no reconnect until the user picks the device again
    }

    // ── Locks ──

    [Fact]
    public async Task ALockIsSkippedWhenTheHubHasNone()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub, Hello(8, "cmd", "notify"));
        Assert.True(t.AcquireDeviceLock("Saving preset", TimeSpan.FromSeconds(5)));
        Assert.True(await hub.QuietAsync());
    }

    [Fact]
    public async Task ARefusedLockFailsTheFlowAndPostsTheNotice()
    {
        await using var hub = new FakeLinkHub();
        using var t = await Connected(hub);
        int posted = 0;
        EventHandler onPosted = (_, _) => Interlocked.Increment(ref posted);
        LinkLockNotice.Posted += onPosted;
        try
        {
            var acquire = Task.Run(() => t.AcquireDeviceLock("Saving preset", TimeSpan.FromSeconds(5)));
            var req = await hub.ExpectAsync<LinkLockAcquire>();
            Assert.Equal(3, req.Handle);
            Assert.Equal("Saving preset", req.Reason);
            await hub.SendAsync(new LinkErr(LinkErrorCode.Locked, req.Id));
            Assert.False(await acquire);
            Assert.True(Volatile.Read(ref posted) >= 1);
        }
        finally { LinkLockNotice.Posted -= onPosted; }
    }
}

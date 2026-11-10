using DSPiConsole.Core.Link;

namespace DSPiConsole.Core.Tests.Link;

/// <summary>The client half of DSPi Link against a scripted hub: the session
/// lifecycle (spec 5, 6), requests, the data plane, and teardown.</summary>
public class LinkClientTests
{
    private static readonly LinkEndpoint Endpoint = new("studio.local");

    private static LinkClient Client(FakeLinkHub hub, int requestMs = 2000) =>
        new(hub.OpenAsync, "1.1.6") { RequestTimeout = TimeSpan.FromMilliseconds(requestMs), BulkTimeout = TimeSpan.FromMilliseconds(requestMs) };

    /// <summary>Waits for the client to reach <paramref name="phase"/>.</summary>
    private static async Task<LinkClientState> Reach(LinkClient client, LinkClientPhase phase, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (client.State.Phase == phase) return client.State;
            await Task.Delay(10);
        }
        Assert.Fail($"Expected {phase}, still {client.State}");
        return client.State;
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

    // ── Hello and open access ──

    [Fact]
    public async Task HelloNamesTheConsoleAndSpeaksOnePointTwo()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "Studio PC", null);
        var hello = await hub.ExpectAsync<LinkHelloClient>();
        Assert.Equal("Studio PC", hello.Client.Name);
        Assert.Equal("DSPi Console", hello.Client.App);
        Assert.Equal("1.1.6", hello.Client.Version);
        Assert.Equal(new LinkProtoVersion(1, 2), hello.Proto);
    }

    /// <summary>Open mode: the courtesy pair learns our session id (spec 6.1),
    /// then the inventory is read.</summary>
    [Fact]
    public async Task OpenAccessLearnsTheSessionAndReadsTheInventory()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync(12, FakeLinkHub.Board(0), FakeLinkHub.Board(1, "0123456789ABCDEF"));

        await Reach(client, LinkClientPhase.Ready);
        Assert.Equal(12, client.SessionId);
        Assert.Equal(LinkRole.Admin, client.Role);
        await Until(() => client.Devices.Count == 2);
        Assert.Equal("Studio Mac", client.Hub?.Name);
        Assert.True(client.Supports(LinkCapability.Snapshot));
    }

    /// <summary>Spec 1.2: a refused courtesy pair leaves the session open and
    /// admin, just without an id (the Mac gives up on it; the spec says carry on).</summary>
    [Fact]
    public async Task ARefusedCourtesyPairStillOpensTheSession()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.ExpectAsync<LinkHelloClient>();
        await hub.SendAsync(FakeLinkHub.Hello(LinkAuthMode.None));
        var pair = await hub.ExpectAsync<LinkAuthPair>();
        await hub.SendAsync(new LinkErr(LinkErrorCode.BadRequest, pair.Id));

        await Reach(client, LinkClientPhase.Ready);
        Assert.Equal(0, client.SessionId);
        Assert.Equal(LinkRole.Admin, client.Role);
        await hub.ExpectAsync<LinkDeviceListRequest>();
    }

    // ── PIN mode ──

    [Fact]
    public async Task WithNoTokenTheClientWaitsToPair()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.ExpectAsync<LinkHelloClient>();
        await hub.SendAsync(FakeLinkHub.Hello(LinkAuthMode.Pin, LinkPinSource.Fixed));

        var state = await Reach(client, LinkClientPhase.AwaitingAuth);
        Assert.True(state.NeedsPairing);
        Assert.Equal(LinkPinSource.Fixed, client.Hello?.PinSource);
        Assert.True(await hub.QuietAsync()); // nothing sent until the user pairs

        var pairing = client.PairAsync("482913", "PC", LinkRole.Control);
        var pair = await hub.ExpectAsync<LinkAuthPair>();
        Assert.Equal("482913", pair.Pin);
        Assert.Equal(LinkRole.Control, pair.Role);
        await hub.SendAsync(LinkOk.WithBody(pair.Id, new LinkAuthOkBody(7, LinkRole.Control, "tok")));

        Assert.Equal("tok", await pairing);
        await Reach(client, LinkClientPhase.Ready);
        Assert.Equal(7, client.SessionId);
    }

    [Fact]
    public async Task AWrongPinIsTheHubsError()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.ExpectAsync<LinkHelloClient>();
        await hub.SendAsync(FakeLinkHub.Hello(LinkAuthMode.Pin));
        await Reach(client, LinkClientPhase.AwaitingAuth);

        var pairing = client.PairAsync("000000", "PC", LinkRole.Control);
        var pair = await hub.ExpectAsync<LinkAuthPair>();
        await hub.SendAsync(new LinkErr(LinkErrorCode.Denied, pair.Id, "wrong PIN"));
        var e = await Assert.ThrowsAsync<LinkRequestException>(() => pairing);
        Assert.Equal(LinkErrorCode.Denied, e.Code);
        Assert.Equal(LinkClientPhase.AwaitingAuth, client.State.Phase); // ask again
    }

    [Fact]
    public async Task AStoredTokenIsOffered()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", "tok");
        await hub.ExpectAsync<LinkHelloClient>();
        await hub.SendAsync(FakeLinkHub.Hello(LinkAuthMode.Pin));
        var auth = await hub.ExpectAsync<LinkAuthToken>();
        Assert.Equal("tok", auth.Token);
        await hub.SendAsync(LinkOk.WithBody(auth.Id, new LinkAuthOkBody(3, LinkRole.Viewer)));
        await Reach(client, LinkClientPhase.Ready);
        Assert.Equal(LinkRole.Viewer, client.Role);
    }

    [Fact]
    public async Task ARefusedTokenMeansPairAgain()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", "stale");
        await hub.ExpectAsync<LinkHelloClient>();
        await hub.SendAsync(FakeLinkHub.Hello(LinkAuthMode.Pin));
        var auth = await hub.ExpectAsync<LinkAuthToken>();
        await hub.SendAsync(new LinkErr(LinkErrorCode.Unauthenticated, auth.Id));
        var state = await Reach(client, LinkClientPhase.AwaitingAuth);
        await Until(() => client.State.NeedsPairing);
    }

    // ── Commands ──

    [Fact]
    public async Task ACommandBeforeTheSessionIsReadyIsNoDevice()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        var r = await client.CommandAsync(new LinkCmdRequest(0, 0, LinkDirection.Get, 0x50, wLength: 41));
        Assert.Equal(LinkStatus.NoDevice, r.Status);
    }

    [Fact]
    public async Task CommandsGetTheirOwnTagsAndTheirOwnAnswers()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync();
        await Reach(client, LinkClientPhase.Ready);

        var first = client.CommandAsync(new LinkCmdRequest(99, 0, LinkDirection.Get, 0x7F, wIndex: 2, wLength: 7));
        var second = client.CommandAsync(new LinkCmdRequest(99, 0, LinkDirection.Set, 0xD2, wIndex: 2, payload: [0, 0, 0xA0, 0xC1]));
        var a = await hub.ExpectAsync<LinkCmdRequest>();
        var b = await hub.ExpectAsync<LinkCmdRequest>();
        Assert.NotEqual(0, a.Tag);
        Assert.NotEqual(a.Tag, b.Tag);
        Assert.Equal(0x7F, a.BRequest);

        // Answered out of order.
        await hub.SendAsync(new LinkCmdResponse(b.Tag, LinkStatus.Ok));
        await hub.SendAsync(new LinkCmdResponse(a.Tag, LinkStatus.Ok, [1, 1, 0x16, 9, 1, 6, 0]));
        Assert.Equal(7, (await first).Payload.Length);
        Assert.Equal(LinkStatus.Ok, (await second).Status);
    }

    [Fact]
    public async Task AnUnansweredCommandTimesOut()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub, requestMs: 300);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync();
        await Reach(client, LinkClientPhase.Ready);
        var r = await client.CommandAsync(new LinkCmdRequest(0, 0, LinkDirection.Get, 0x50, wLength: 41));
        Assert.Equal(LinkStatus.Timeout, r.Status);
    }

    // ── Events ──

    [Fact]
    public async Task TheInventoryFollowsDeviceEvents()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync(12, FakeLinkHub.Board(0));
        await Until(() => client.Devices.Count == 1);

        await hub.SendAsync(new LinkDeviceAdded(FakeLinkHub.Board(1, "0123456789ABCDEF")));
        await Until(() => client.Devices.Count == 2);
        await hub.SendAsync(new LinkDeviceChanged(FakeLinkHub.Board(1, "0123456789ABCDEF") with { LockedBy = 5 }));
        await Until(() => client.Devices.Any(d => d.LockedBy == 5));
        await hub.SendAsync(new LinkDeviceRemoved(0));
        await Until(() => client.Devices.Count == 1 && client.Devices[0].Handle == 1);
    }

    [Fact]
    public async Task NotificationsPollsAndResyncsAreRaised()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        LinkNotifyFrame? notify = null;
        LinkPollFrame? poll = null;
        LinkResyncFrame? resync = null;
        client.Notified += (_, n) => notify = n;
        client.Polled += (_, p) => poll = p;
        client.Resynced += (_, r) => resync = r;
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync();

        await hub.SendAsync(new LinkNotifyFrame(1, 0, 12, [2, 2, 0, 1]));
        await hub.SendAsync(new LinkPollFrame(2, 0, 3, new byte[41]));
        await hub.SendAsync(new LinkResyncFrame(0, 1));
        await Until(() => notify != null && poll != null && resync != null);
        Assert.Equal(12, notify!.Origin);
        Assert.Equal(3, poll!.Slot);
        Assert.Equal(LinkResyncReason.DeviceReattached, resync!.KnownReason);
    }

    [Fact]
    public async Task GarbageFromTheHubIsIgnored()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync();
        await Reach(client, LinkClientPhase.Ready);
        await hub.SendTextAsync("""{"t":"ok","t":"err"}""");
        await hub.SendTextAsync("not json");
        await hub.SendAsync(new LinkUnknownMessage("hub.aurora", null));
        await hub.SendTextAsync("""{"t":"device.added","device":{"handle":1}}""");
        await Task.Delay(100);
        Assert.Equal(LinkClientPhase.Ready, client.State.Phase);
    }

    // ── Requests ──

    [Fact]
    public async Task ALockRefusalIsTheHubsError()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync();
        await Reach(client, LinkClientPhase.Ready);

        var acquiring = client.AcquireLockAsync(0, "Saving preset", TimeSpan.FromSeconds(5));
        var req = await hub.ExpectAsync<LinkLockAcquire>();
        Assert.Equal("Saving preset", req.Reason);
        Assert.Equal(5000, req.TimeoutMs);
        await hub.SendAsync(new LinkErr(LinkErrorCode.Locked, req.Id));
        Assert.Equal(LinkErrorCode.Locked, (await Assert.ThrowsAsync<LinkRequestException>(() => acquiring)).Code);

        var again = client.AcquireLockAsync(0, "Applying configuration", TimeSpan.FromSeconds(90));
        var second = await hub.ExpectAsync<LinkLockAcquire>();
        Assert.Equal(60000, second.TimeoutMs); // capped at 60 s (spec 7.7)
        await hub.SendTextAsync($$"""{"t":"ok","id":{{second.Id}},"timeout_ms":60000}""");
        Assert.Equal(TimeSpan.FromSeconds(60), await again);
    }

    [Fact]
    public async Task ASnapshotDecodes()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync();
        await Reach(client, LinkClientPhase.Ready);
        var snap = client.SnapshotAsync(0);
        var req = await hub.ExpectAsync<LinkDeviceSnapshotRequest>();
        await hub.SendAsync(LinkOk.WithBody(req.Id, new LinkSnapshotBody(0, 32, 120, Convert.ToBase64String(new byte[] { 1, 2, 3 }))));
        Assert.Equal(new byte[] { 1, 2, 3 }, (await snap).Bulk);
    }

    // ── Teardown ──

    [Fact]
    public async Task ARevokedTokenCloseIsReported()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync();
        await Reach(client, LinkClientPhase.Ready);
        await hub.CloseAsync(LinkCloseCode.TokenRevoked);
        var state = await Reach(client, LinkClientPhase.Failed);
        Assert.Equal(LinkCloseCode.TokenRevoked, state.CloseCode);
        Assert.Contains("revoked", state.Reason);
        Assert.Equal(0, client.SessionId);
    }

    [Fact]
    public async Task ADisconnectFailsWhatWasPending()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync();
        await Reach(client, LinkClientPhase.Ready);

        var command = client.CommandAsync(new LinkCmdRequest(0, 0, LinkDirection.Get, 0x50, wLength: 41));
        var request = client.SnapshotAsync(0);
        await hub.ExpectAsync<LinkCmdRequest>();
        await hub.ExpectAsync<LinkDeviceSnapshotRequest>();
        client.Disconnect();

        Assert.Equal(LinkStatus.NoDevice, (await command).Status);
        Assert.Equal(LinkRequestException.NotConnected, (await Assert.ThrowsAsync<LinkRequestException>(() => request)).Code);
        Assert.Equal(LinkClientPhase.Disconnected, client.State.Phase);
        Assert.Equal(1000, Assert.IsType<FakeLinkHub.Closed>(await hub.NextAsync()).Code);
        await Task.Delay(100);
        Assert.Equal(LinkClientPhase.Disconnected, client.State.Phase); // not overwritten by the loop's own failure
    }

    [Fact]
    public async Task ARefusedConnectionFails()
    {
        await using var hub = new FakeLinkHub { RefuseNextWith = new System.Net.Sockets.SocketException(10061) };
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        var state = await Reach(client, LinkClientPhase.Failed);
        Assert.Null(state.CloseCode);
        Assert.False(string.IsNullOrEmpty(state.Reason));
    }

    /// <summary>The client is reusable: a connect after a failure is a fresh session.</summary>
    [Fact]
    public async Task AReconnectIsAFreshSession()
    {
        await using var hub = new FakeLinkHub();
        using var client = Client(hub);
        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync(12);
        await Reach(client, LinkClientPhase.Ready);
        await hub.CloseAsync(LinkCloseCode.ShuttingDown);
        await Reach(client, LinkClientPhase.Failed);
        await hub.ExpectAsync<FakeLinkHub.Closed>(); // the client completes the close handshake

        await client.ConnectAsync(Endpoint, "PC", null);
        await hub.AcceptOpenAsync(13);
        await Reach(client, LinkClientPhase.Ready);
        Assert.Equal(13, client.SessionId);
        Assert.Equal(2, hub.Connections);
    }
}

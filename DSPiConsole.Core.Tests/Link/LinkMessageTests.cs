using System.Text.Json;
using System.Text.Json.Nodes;
using DSPiConsole.Core.Link;

namespace DSPiConsole.Core.Tests.Link;

/// <summary>The DSPi Link JSON control plane (spec section 7). The spelling
/// tests matter most: every other implementation keys off these exact
/// strings. Ported from the macOS Console's LinkMessageTests.</summary>
public class LinkMessageTests
{
    /// <summary>Encodes, decodes and encodes again: the same type and the same
    /// JSON both times. (Records compare lists by reference, so the JSON is
    /// the comparison.)</summary>
    internal static void AssertRoundTrips(LinkMessage message)
    {
        var text = message.Encode();
        var back = LinkMessage.Decode(text);
        Assert.Equal(message.GetType(), back.GetType());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(back.Encode())), text);
    }

    private static HashSet<string> WireKeys(LinkMessage message) =>
        JsonNode.Parse(message.Encode())!.AsObject().Select(p => p.Key).ToHashSet();

    private static JsonObject Object(LinkMessage message) => JsonNode.Parse(message.Encode())!.AsObject();

    private static T Decode<T>(string json) where T : LinkMessage => Assert.IsType<T>(LinkMessage.Decode(json));

    // ── Envelope ──

    [Fact]
    public void TypeNames()
    {
        (LinkMessage, string)[] expected =
        [
            (new LinkHelloClient(new LinkClientInfo("a")), "hello"),
            (new LinkHelloHub(new LinkHubInfo("u", "h", LinkHubKind.Console), LinkAuthMode.Pin, []), "hello"),
            (new LinkAuthToken(1, "t"), "auth.token"),
            (new LinkAuthPair(1, "482913", "n"), "auth.pair"),
            (new LinkAuthList(2), "auth.list"),
            (new LinkAuthRevoke(3, 3), "auth.revoke"),
            (new LinkAuthSetRole(4, 3, LinkRole.Viewer), "auth.set_role"),
            (new LinkDeviceListRequest(5), "device.list"),
            (new LinkDeviceAdded(new LinkDeviceInfo(0, "S")), "device.added"),
            (new LinkDeviceRemoved(0, "S"), "device.removed"),
            (new LinkDeviceChanged(new LinkDeviceInfo(0, "S")), "device.changed"),
            (new LinkDeviceRename(6, 0, "Living room"), "device.rename"),
            (new LinkDeviceSnapshotRequest(6, 0), "device.snapshot"),
            (new LinkPollSubscribe(8, 0, []), "poll.subscribe"),
            (new LinkPollUnsubscribe(9, 0, [1]), "poll.unsubscribe"),
            (new LinkPollError(0), "poll.error"),
            (new LinkLockAcquire(10, 0), "lock.acquire"),
            (new LinkLockRelease(11, 0), "lock.release"),
            (new LinkFwInstall(12, 0, 1, "ab"), "fw.install"),
            (new LinkFwProgress(0, LinkFwPhase.Uploading), "fw.progress"),
            (new LinkFwDone(0, true), "fw.done"),
            (new LinkHubStatsRequest(13), "hub.stats"),
            (new LinkHubRename(14, "Studio Mac"), "hub.rename"),
            (new LinkOk(1), "ok"),
            (new LinkErr(LinkErrorCode.Denied, 1), "err"),
        ];
        foreach (var (message, name) in expected)
        {
            Assert.Equal(name, message.TypeName);
            Assert.Equal(name, Object(message)["t"]!.GetValue<string>());
        }
    }

    [Fact]
    public void EveryMessageTypeRoundTrips()
    {
        var device = new LinkDeviceInfo(0, "E46058388B1A2E2C", "Living room", Platform: 1, Fw: "1.1.7",
            Outputs: 9, Inputs: 8, WireVersion: 30, State: LinkDeviceState.Online, Link: LinkDeviceLinkKind.Usb);
        LinkMessage[] messages =
        [
            new LinkHelloClient(new LinkClientInfo("Troy's iPhone", "DSPi Mobile", "0.1"), new LinkProtoVersion()),
            new LinkHelloHub(new LinkHubInfo("9d1b", "Studio Mac", LinkHubKind.Console, "1.1.7"), LinkAuthMode.Pin,
                ["cmd", "notify", "poll", "snapshot", "lock", "fw_install", "web", "rename"], new LinkProtoVersion(),
                new LinkLimits(65536, 8192, 8, 20, 200000)),
            new LinkAuthToken(1, "dG9rZW4"),
            new LinkAuthPair(1, "482913", "Troy's iPhone", LinkRole.Control),
            new LinkAuthList(2),
            new LinkAuthRevoke(3, 3),
            new LinkAuthSetRole(4, 3, LinkRole.Viewer),
            new LinkDeviceListRequest(5),
            new LinkDeviceAdded(device),
            new LinkDeviceChanged(device),
            new LinkDeviceRemoved(0, "E46058388B1A2E2C"),
            new LinkDeviceRename(6, 0, "Living room"),
            new LinkDeviceSnapshotRequest(6, 0),
            new LinkPollSubscribe(8, 0, [new LinkPollSpec(0, 80, 9, 2, 27, 10), new LinkPollSpec(1, 11, 3, 2, 80, 15)]),
            new LinkPollUnsubscribe(9, 0, [1]),
            new LinkPollError(0, 1, LinkErrorCode.Busy, "device busy"),
            new LinkLockAcquire(10, 0, "Applying configuration", 10000),
            new LinkLockRelease(11, 0),
            new LinkFwInstall(12, 0, 393216, "deadbeef", "1.1.8"),
            new LinkFwProgress(0, LinkFwPhase.Verifying, 42),
            new LinkFwDone(0, true, "1.1.8"),
            new LinkHubStatsRequest(13),
            new LinkHubRename(14, "Studio Mac"),
            new LinkErr(LinkErrorCode.Denied, 7, "admin role required"),
            new LinkUnknownMessage("future.thing", 42),
        ];
        Assert.Equal(25, messages.Length);
        foreach (var message in messages) AssertRoundTrips(message);
    }

    [Fact]
    public void OkRepliesRoundTrip()
    {
        LinkMessage[] bodies =
        [
            LinkOk.WithBody(1, new LinkAuthOkBody(12, LinkRole.Control, "dG9rZW4",
                new LinkPolicyDescriptor([[240, 1], [83, 1]]))),
            LinkOk.WithBody(5, new LinkDeviceListBody([new LinkDeviceInfo(0, "E46058388B1A2E2C", "Living room",
                Platform: 1, Fw: "1.1.7", Outputs: 9, WireVersion: 30, State: LinkDeviceState.Online, Link: LinkDeviceLinkKind.Usb)])),
            LinkOk.WithBody(6, new LinkSnapshotBody(0, 30, 120, "AAEC", "AwQF")),
            LinkOk.WithBody(8, new LinkPollSubscribeBody([new LinkPollGrant(0, 10), new LinkPollGrant(1, 12)])),
            LinkOk.WithBody(12, new LinkFwInstallBody(3)),
            LinkOk.WithBody(13, new LinkHubStatsBody(3, 8812, [new LinkHubDeviceStats(0, 18233, 2, 1.4, 0)])),
            new LinkOk(10),
        ];
        foreach (var message in bodies) AssertRoundTrips(message);
    }

    // ── Wire spellings ──

    [Fact]
    public void SnakeCaseKeySpellings()
    {
        var hello = new LinkHelloHub(new LinkHubInfo("u", "h", LinkHubKind.Console, "1.1.7"), LinkAuthMode.Pin, ["cmd"],
            Limits: new LinkLimits(65536, 8192, 8, 20, 200000));
        Assert.Equal(["max_frame", "max_payload", "max_inflight", "poll_max_hz", "poll_budget_bps"],
            Object(hello)["limits"]!.AsObject().Select(p => p.Key).ToHashSet());

        var snapshot = LinkOk.WithBody(6, new LinkSnapshotBody(0, 30, 120, "AAEC", "AwQF"));
        Assert.Equal(["t", "id", "handle", "wire_version", "age_ms", "bulk_b64", "status_b64"], WireKeys(snapshot));

        var stats = LinkOk.WithBody(13, new LinkHubStatsBody(3, 8812, [new LinkHubDeviceStats(0, 1, 0, 1.4, 0)]));
        Assert.Equal(["t", "id", "sessions", "uptime_s", "devices"], WireKeys(stats));
        Assert.Equal(["handle", "cmds", "errors", "avg_rtt_ms", "notify_dropped"],
            Object(stats)["devices"]![0]!.AsObject().Select(p => p.Key).ToHashSet());

        Assert.Equal(["t", "id", "handle", "reason", "timeout_ms"], WireKeys(new LinkLockAcquire(10, 0, "r", 10000)));
        Assert.Equal(["t", "id", "handle", "size", "sha256", "version"], WireKeys(new LinkFwInstall(12, 0, 1, "abc", "1.1.8")));

        var device = new LinkDeviceAdded(new LinkDeviceInfo(0, "S", "n", 1, "1.1.7", 0, 9, 8, 30,
            LinkDeviceState.Online, LinkDeviceLinkKind.Usb, 12));
        Assert.Equal(["handle", "serial", "name", "platform", "fw", "fw_beta", "outputs", "inputs", "wire_version",
                "state", "link", "locked_by"],
            Object(device)["device"]!.AsObject().Select(p => p.Key).ToHashSet());

        var clients = LinkOk.WithBody(2, new LinkAuthListBody([new LinkAuthClient(3, "Troy's iPhone", LinkRole.Control,
            DateTimeOffset.FromUnixTimeSeconds(0), DateTimeOffset.FromUnixTimeSeconds(60), true)]));
        var first = Object(clients)["clients"]![0]!.AsObject();
        Assert.Equal(["cid", "name", "role", "created", "last_seen", "online"], first.Select(p => p.Key).ToHashSet());
        Assert.Equal("1970-01-01T00:00:00Z", first["created"]!.GetValue<string>());
        Assert.Equal("control", first["role"]!.GetValue<string>());
    }

    /// <summary>The exact spellings must also decode.</summary>
    [Fact]
    public void SnakeCaseKeysDecode()
    {
        var ok = Decode<LinkOk>("""
            {"t":"ok","id":6,"handle":0,"wire_version":30,"age_ms":120,"bulk_b64":"AAEC","status_b64":"AwQF"}
            """);
        var body = ok.DecodeBody<LinkSnapshotBody>();
        Assert.Equal(0, body.Handle);
        Assert.Equal(30, body.WireVersion);
        Assert.Equal(120, body.AgeMs);
        Assert.Equal(new byte[] { 0, 1, 2 }, body.Bulk);
        Assert.Equal(new byte[] { 3, 4, 5 }, body.Status);

        // The body keeps the wire spellings, so a caller that doesn't know the
        // request can still read a field by name.
        Assert.Equal(30, ok.Body["wire_version"]!.GetValue<int>());
        Assert.Equal("AAEC", ok.Body["bulk_b64"]!.GetValue<string>());
        Assert.False(ok.Body.ContainsKey("t"));
        Assert.False(ok.Body.ContainsKey("id"));
        Assert.Equal(6, ok.Id);
    }

    // ── Spec examples ──

    [Fact]
    public void SpecExampleHelloExchange()
    {
        var c = Decode<LinkHelloClient>("""
            {"t": "hello", "proto": {"major": 1, "minor": 2},
             "client": {"name": "Living Room iPad", "app": "DSPi Mobile", "version": "0.1"}}
            """);
        Assert.Equal(new LinkProtoVersion(1, 2), c.Version);
        Assert.Equal("Living Room iPad", c.Client.Name);

        var h = Decode<LinkHelloHub>("""
            {"t": "hello", "proto": {"major": 1, "minor": 2},
             "hub": {"id": "9d1b", "name": "Studio Mac", "kind": "console", "version": "1.1.7"},
             "auth": "pin", "pin_source": "window",
             "caps": ["cmd", "notify", "poll", "snapshot", "lock", "fw_install", "web", "rename"],
             "limits": {"max_frame": 65536, "max_payload": 8192, "max_inflight": 8,
                        "poll_max_hz": 20, "poll_budget_bps": 200000}}
            """);
        Assert.Equal(LinkHubKind.Console, h.Hub.Kind);
        Assert.Equal(LinkAuthMode.Pin, h.Auth);
        Assert.True(h.Supports(LinkCapability.FwInstall));
        Assert.True(h.Supports(LinkCapability.Cmd));
        Assert.Equal(65536, h.Limits?.MaxFrame);
        Assert.Equal(200000, h.Limits?.PollBudgetBps);
        // Spec 1.2: no pin_source means a pairing window.
        Assert.Equal(LinkPinSource.Window, h.PinSource);
    }

    [Fact]
    public void ABridgeSaysItsPinIsFixed()
    {
        var h = Decode<LinkHelloHub>("""
            {"t":"hello","proto":{"major":1,"minor":2},"hub":{"id":"b","name":"DSPi Bridge","kind":"bridge"},
             "auth":"pin","pin_source":"fixed","caps":["cmd"]}
            """);
        Assert.Equal(LinkHubKind.Bridge, h.Hub.Kind);
        Assert.Equal(LinkPinSource.Fixed, h.PinSource);
        Assert.Equal(new LinkProtoVersion(1, 2), h.Version);
    }

    [Fact]
    public void SpecExampleAuthReply()
    {
        var ok = Decode<LinkOk>("""{"t": "ok", "id": 1, "session": 12, "role": "control", "policy": {"denied": [[240, 1], [83, 1]]}}""");
        Assert.Equal(1, ok.Id);
        var body = ok.DecodeBody<LinkAuthOkBody>();
        Assert.Equal(12, body.Session);
        Assert.Equal(LinkRole.Control, body.Role);
        Assert.Null(body.Token);
        var denied = body.Policy?.DeniedCommands ?? [];
        Assert.Equal(2, denied.Count);
        Assert.Equal(0xF0, denied[0].BRequest);
        Assert.Equal(LinkDirection.Get, denied[0].Direction);
    }

    [Fact]
    public void SpecExampleDeviceList()
    {
        var ok = Decode<LinkOk>("""
            {"t": "ok", "id": 5, "devices": [
              {"handle": 0, "serial": "E46058388B1A2E2C", "name": "Living room",
               "platform": 1, "fw": "1.1.7", "fw_beta": 0, "outputs": 9, "inputs": 8,
               "wire_version": 32, "state": "online", "link": "usb",
               "locked_by": null}]}
            """);
        var device = Assert.Single(ok.DecodeBody<LinkDeviceListBody>().Devices);
        Assert.Equal("E46058388B1A2E2C", device.Serial);
        Assert.Equal(32, device.WireVersion);
        Assert.Equal(0, device.FwBeta);
        Assert.Equal(LinkDeviceState.Online, device.State);
        Assert.Equal(LinkDeviceLinkKind.Usb, device.Link);
        Assert.Null(device.LockedBy); // an explicit null means unlocked
    }

    /// <summary>A 1.0 hub sends no fw_beta: that reads as unknown, never final.</summary>
    [Fact]
    public void AMissingFwBetaIsUnknown() =>
        Assert.Null(Decode<LinkDeviceAdded>("""{"t":"device.added","device":{"handle":0,"serial":"S","fw":"1.1.6"}}""").Device.FwBeta);

    [Fact]
    public void SpecExamplePollSubscribe()
    {
        var m = Decode<LinkPollSubscribe>("""
            {"t": "poll.subscribe", "id": 8, "handle": 0, "polls": [
              {"slot": 0, "req": 80,  "val": 9, "idx": 2, "len": 41, "hz": 10},
              {"slot": 1, "req": 11,  "val": 3, "idx": 2, "len": 82, "hz": 15}]}
            """);
        Assert.Equal(2, m.Polls.Count);
        Assert.Equal(new LinkPollSpec(0, 80, 9, 2, 41, 10), m.Polls[0]);
        Assert.Equal(82, m.Polls[1].Len);

        var ok = Decode<LinkOk>("""{"t":"ok","id":8,"granted":[{"slot":0,"hz":10},{"slot":1,"hz":12}]}""");
        Assert.Equal([new LinkPollGrant(0, 10), new LinkPollGrant(1, 12)], ok.DecodeBody<LinkPollSubscribeBody>().Granted);
    }

    [Fact]
    public void SpecExampleFirmwareInstall()
    {
        var m = Decode<LinkFwInstall>("""{"t":"fw.install","id":12,"handle":0,"size":393216,"sha256":"abc","version":"1.1.8"}""");
        Assert.Equal(393216, m.Size);
        Assert.Equal("abc", m.Sha256);
        Assert.Equal(3, Decode<LinkOk>("""{"t":"ok","id":12,"xfer":3}""").DecodeBody<LinkFwInstallBody>().Xfer);
        var p = Decode<LinkFwProgress>("""{"t":"fw.progress","handle":0,"phase":"copying","pct":42}""");
        Assert.Equal(LinkFwPhase.Copying, p.Phase);
        Assert.Equal(42, p.Pct);
        var d = Decode<LinkFwDone>("""{"t":"fw.done","handle":0,"ok":true,"fw":"1.1.8","fw_beta":0}""");
        Assert.True(d.Ok);
        Assert.Equal(0, d.FwBeta);
    }

    [Fact]
    public void SpecExampleHubStats()
    {
        var body = Decode<LinkOk>("""
            {"t": "ok", "id": 13, "sessions": 3, "uptime_s": 8812,
             "devices": [{"handle": 0, "cmds": 18233, "errors": 2, "avg_rtt_ms": 1.4, "notify_dropped": 0}]}
            """).DecodeBody<LinkHubStatsBody>();
        Assert.Equal(8812, body.UptimeS);
        Assert.Equal(1.4, body.Devices![0].AvgRttMs);
        Assert.Equal(0, body.Devices[0].NotifyDropped);
    }

    // ── Forward compatibility ──

    [Fact]
    public void AnUnknownTypeDecodesToUnknown()
    {
        var message = Decode<LinkUnknownMessage>("""{"t":"device.teleport","id":9,"handle":0}""");
        Assert.Equal(new LinkUnknownMessage("device.teleport", 9), message);
        Assert.Equal(9, message.RequestId);
        Assert.Equal(new LinkUnknownMessage("hub.aurora", null), LinkMessage.Decode("""{"t":"hub.aurora","colour":"green"}"""));
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var m = Decode<LinkDeviceRename>("""
            {"t":"device.rename","id":6,"handle":0,"name":"Living room",
             "colour":"green","nested":{"a":[1,2,{"b":null}]},"future_flag":true}
            """);
        Assert.Equal(new LinkDeviceRename(6, 0, "Living room"), m);

        // Inside nested objects of a known type too.
        var h = Decode<LinkHelloHub>("""
            {"t":"hello","proto":{"major":1,"minor":0,"patch":7},
             "hub":{"id":"u","name":"h","kind":"console","serial":"x"},
             "auth":"pin","caps":["cmd","time_travel"],
             "limits":{"max_frame":16384,"max_frames_per_fortnight":3}}
            """);
        Assert.Equal(16384, h.Limits?.MaxFrame);
        Assert.Equal(["cmd", "time_travel"], h.Caps);
        Assert.Equal(new LinkProtoVersion(1, 0), h.Version);
    }

    [Fact]
    public void AMissingTypeOrFieldIsADecodeFailure()
    {
        Assert.ThrowsAny<JsonException>(() => LinkMessage.Decode("""{"id":1,"handle":0}"""));
        Assert.ThrowsAny<JsonException>(() => LinkMessage.Decode("""{"t":"device.rename","id":6,"handle":0}"""));
        Assert.ThrowsAny<JsonException>(() => LinkMessage.Decode("""{"t":"auth.token","id":1,"token":null}"""));
        Assert.ThrowsAny<JsonException>(() => LinkMessage.Decode("[1,2]"));
        Assert.ThrowsAny<JsonException>(() => LinkMessage.Decode("not json"));
    }

    // ── err ──

    [Fact]
    public void ErrDecodesCodeAndOptionalMessage()
    {
        var e = Decode<LinkErr>("""{"t":"err","id":7,"code":"denied","msg":"admin role required"}""");
        Assert.Equal(7, e.Id);
        Assert.Equal(LinkErrorCode.Denied, e.Code);
        Assert.Equal("admin role required", e.Msg);

        var bare = Decode<LinkErr>("""{"t":"err","code":"unknown_type"}""");
        Assert.Null(bare.Id); // an err about an unparseable frame has no id to echo
        Assert.Equal(LinkErrorCode.UnknownType, bare.Code);
        Assert.Null(bare.Msg);
        Assert.Equal(["t", "code"], WireKeys(bare));
    }

    [Fact]
    public void Vocabularies()
    {
        Assert.Equal("unauthenticated", LinkErrorCode.Unauthenticated);
        Assert.Equal("bad_request", LinkErrorCode.BadRequest);
        Assert.Equal("unknown_type", LinkErrorCode.UnknownType);
        Assert.Equal("no_device", LinkErrorCode.NoDevice);
        Assert.Equal("rate_limited", LinkErrorCode.RateLimited);
        Assert.Equal("internal", LinkErrorCode.Internal);
        Assert.Equal("fw_install", LinkCapability.FwInstall);

        string Wire<T>(T value) => JsonSerializer.Serialize(value, LinkJson.Options).Trim('"');
        Assert.Equal("viewer", Wire(LinkRole.Viewer));
        Assert.Equal("admin", Wire(LinkRole.Admin));
        Assert.Equal("none", Wire(LinkAuthMode.None));
        Assert.Equal("bridge", Wire(LinkHubKind.Bridge));
        Assert.Equal("updating", Wire(LinkDeviceState.Updating));
        Assert.Equal("uart", Wire(LinkDeviceLinkKind.Uart));
        Assert.Equal("rebooting", Wire(LinkFwPhase.Rebooting));
        Assert.Equal("config", Wire(LinkCommandClass.Config));
    }

    // ── Spec 1.2 ──

    [Fact]
    public void ThisBuildSpeaksOnePointTwo()
    {
        var hello = new LinkHelloClient(new LinkClientInfo("PC", "DSPi Console", "1.1.6"), new LinkProtoVersion());
        var proto = Object(hello)["proto"]!;
        Assert.Equal(1, proto["major"]!.GetValue<int>());
        Assert.Equal(2, proto["minor"]!.GetValue<int>());
        // Built without one, a hello still says which version it speaks.
        Assert.True(Object(new LinkHelloHub(new LinkHubInfo("u", "h", LinkHubKind.Console), LinkAuthMode.None, ["cmd"])).ContainsKey("proto"));
        Assert.True(Object(new LinkHelloClient(new LinkClientInfo("x"))).ContainsKey("proto"));
        // A received hello must carry proto, with both fields: a missing minor
        // must not read as the newest version (spec 7.2, 10.2).
        Assert.ThrowsAny<JsonException>(() => LinkMessage.Decode("""{"t":"hello","client":{"name":"x"}}"""));
        Assert.ThrowsAny<JsonException>(() => LinkMessage.Decode("""{"t":"hello","proto":{"major":1},"client":{"name":"x"}}"""));
        Assert.ThrowsAny<JsonException>(() => LinkMessage.Decode("""{"t":"hello","proto":{},"client":{"name":"x"}}"""));
    }

    [Fact]
    public void HubAdministrationTypeNames()
    {
        (LinkMessage, string)[] expected =
        [
            (new LinkHubConfigGet(20), "hub.config.get"),
            (new LinkHubConfigSet(21, new JsonObject { ["name"] = "Bridge" }), "hub.config.set"),
            (new LinkHubConfigChanged(new JsonObject()), "hub.config.changed"),
            (new LinkHubWifiScan(22), "hub.wifi.scan"),
            (new LinkHubWifiJoin(23, "Studio", ""), "hub.wifi.join"),
            (new LinkHubAuthSet(24, LinkAuthMode.Pin, "482913"), "hub.auth.set"),
            (new LinkHubRestart(26), "hub.restart"),
            (new LinkHubReset(27, LinkResetLevel.Network), "hub.reset"),
            (new LinkHubUpdate(28, 1376256, "ab", "1.0.1"), "hub.update"),
            (new LinkHubUpdateDone(false, Msg: "image is for another chip"), "hub.update.done"),
        ];
        foreach (var (message, name) in expected)
        {
            Assert.Equal(name, message.TypeName);
            AssertRoundTrips(message);
        }
    }

    /// <summary>Config keys are data defined by the hub kind: nested objects,
    /// arrays and explicit nulls all survive a round trip.</summary>
    [Fact]
    public void HubConfigKeepsArbitraryKeys()
    {
        var m = Decode<LinkHubConfigChanged>("""
            {"t":"hub.config.changed","config":{"name":"B","led":{"pin":null,"type":"none"},"future":[1,{"x":true}]}}
            """);
        Assert.Null(m.Config["led"]!["pin"]);
        Assert.True(m.Config["led"]!.AsObject().ContainsKey("pin"));
        AssertRoundTrips(m);
    }

    [Fact]
    public void ABadRequestNamesTheOffendingKey()
    {
        var e = Decode<LinkErr>("""{"t":"err","id":21,"code":"bad_request","field":"uart.tx_pin","msg":"pin 3 cannot be used"}""");
        Assert.Equal("uart.tx_pin", e.Field);
        Assert.Equal(["t", "id", "code", "msg", "field"], WireKeys(e));
    }

    [Fact]
    public void ALockReplyCarriesTheGrantedTimeout()
    {
        Assert.Equal(60000, Decode<LinkOk>("""{"t":"ok","id":10,"timeout_ms":60000}""").DecodeBody<LinkLockOkBody>().TimeoutMs);
        Assert.Null(Decode<LinkOk>("""{"t":"ok","id":10}""").DecodeBody<LinkLockOkBody>().TimeoutMs); // a 1.0/1.1 hub
    }

    [Fact]
    public void CloseCodes()
    {
        Assert.True(LinkCloseCode.Reconnects(LinkCloseCode.KeepaliveFailure));
        Assert.True(LinkCloseCode.Reconnects(LinkCloseCode.StoppedReading));
        Assert.True(LinkCloseCode.Reconnects(LinkCloseCode.TooFast));
        Assert.True(LinkCloseCode.Reconnects(LinkCloseCode.ShuttingDown));
        Assert.True(LinkCloseCode.Reconnects(LinkCloseCode.NormalClosure));
        Assert.False(LinkCloseCode.Reconnects(LinkCloseCode.ProtocolError));
        Assert.False(LinkCloseCode.Reconnects(LinkCloseCode.AuthFailed));
        Assert.False(LinkCloseCode.Reconnects(LinkCloseCode.TokenRevoked));
    }

    [Fact]
    public void NewCapabilitySpellings()
    {
        Assert.Equal("hub_config", LinkCapability.HubConfig);
        Assert.Equal("wifi", LinkCapability.Wifi);
        Assert.Equal("auth_config", LinkCapability.AuthConfig);
        Assert.Equal("reset", LinkCapability.Reset);
        Assert.Equal("hub_update", LinkCapability.HubUpdate);
        Assert.Equal("window", LinkPinSource.Window);
        Assert.Equal("fixed", LinkPinSource.Fixed);
    }

    /// <summary>Whole numbers in an ok body must not gain a fractional part.</summary>
    [Fact]
    public void WholeNumbersStayWhole() =>
        Assert.Contains("\"session\":12", LinkOk.WithBody(1, new LinkAuthOkBody(12, LinkRole.Admin)).Encode());
}

using System.Text.Json.Nodes;
using DSPiConsole.Core.Link;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.Core.Tests.Link;

/// <summary>
/// The shared dspi-link fixtures (third_party/dspi-link/fixtures), which every
/// Link implementation is checked against: each binary frame decodes to what
/// frames.json says and encodes back to the same bytes; each message decodes
/// and re-encodes to the same JSON; and the captured device replies go through
/// the app's own parsers.
/// </summary>
public class LinkFixtureTests
{
    private static string Dir(string sub) => Path.Combine(AppContext.BaseDirectory, "LinkFixtures", sub);

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    public static TheoryData<string> FrameNames()
    {
        var data = new TheoryData<string>();
        foreach (var f in JsonNode.Parse(File.ReadAllText(Path.Combine(Dir("frames"), "frames.json")))!["frames"]!.AsArray())
            data.Add(f!["file"]!.GetValue<string>());
        return data;
    }

    [Theory]
    [MemberData(nameof(FrameNames))]
    public void EachFrameDecodesAsDescribedAndEncodesBack(string file)
    {
        var spec = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir("frames"), "frames.json")))!["frames"]!.AsArray()
            .Single(f => f!["file"]!.GetValue<string>() == file)!.AsObject();
        var wire = File.ReadAllBytes(Path.Combine(Dir("frames"), file));
        Assert.Equal(FromHex(spec["hex"]!.GetValue<string>()), wire);

        var frame = LinkFrame.Decode(wire);
        Assert.Equal(wire, frame.Encode());

        int Int(string key) => spec[key]!.GetValue<int>();
        byte[] Hex(string key) => FromHex(spec[key]!.GetValue<string>());

        switch (spec["type"]!.GetValue<string>())
        {
            case "cmd_request":
                var req = Assert.IsType<LinkCmdRequest>(frame);
                Assert.Equal(Int("tag"), req.Tag);
                Assert.Equal(Int("handle"), req.Handle);
                Assert.Equal(spec["direction"]!.GetValue<string>(), LinkPolicy.WireName(req.Direction));
                Assert.Equal(Int("bRequest"), req.BRequest);
                Assert.Equal(Int("wValue"), req.WValue);
                Assert.Equal(Int("wIndex"), req.WIndex);
                Assert.Equal(Int("wLength"), req.WLength);
                if (spec.ContainsKey("payload_hex")) Assert.Equal(Hex("payload_hex"), req.Payload);
                break;
            case "cmd_response":
                var resp = Assert.IsType<LinkCmdResponse>(frame);
                Assert.Equal(Int("tag"), resp.Tag);
                Assert.Equal(Int("status"), (int)resp.Status);
                if (spec.ContainsKey("payload_hex")) Assert.Equal(Hex("payload_hex"), resp.Payload);
                break;
            case "notify":
                var notify = Assert.IsType<LinkNotifyFrame>(frame);
                Assert.Equal(Int("tag"), notify.Tag);
                Assert.Equal(Int("handle"), notify.Handle);
                Assert.Equal(Int("origin"), notify.Origin);
                Assert.Equal(spec["packet"]!["version"]!.GetValue<int>(), notify.Packet[0]);
                break;
            case "poll":
                var poll = Assert.IsType<LinkPollFrame>(frame);
                Assert.Equal(Int("tag"), poll.Tag);
                Assert.Equal(Int("slot"), poll.Slot);
                Assert.Equal(Int("payload_length"), poll.Payload.Length);
                break;
            case "fwdata":
                var chunk = Assert.IsType<LinkFwDataFrame>(frame);
                Assert.Equal(Int("tag"), chunk.Tag);
                Assert.Equal((uint)Int("offset"), chunk.Offset);
                Assert.Equal(Hex("data_hex"), chunk.Data);
                break;
            case "fwdata_ack":
                var ack = Assert.IsType<LinkFwDataAck>(frame);
                Assert.Equal(Int("tag"), ack.Tag);
                Assert.Equal((uint)Int("offset"), ack.Offset);
                Assert.Equal(Int("status"), (int)ack.Status);
                break;
            case "resync":
                var resync = Assert.IsType<LinkResyncFrame>(frame);
                Assert.Equal(Int("handle"), resync.Handle);
                Assert.Equal(Int("reason"), resync.Reason);
                break;
            default:
                Assert.Fail($"frames.json names a type this test doesn't know: {spec["type"]}");
                break;
        }
    }

    [Fact]
    public void EveryFrameFileIsDescribed()
    {
        var described = FrameNames().Select(r => (string)r[0]).ToHashSet();
        var files = Directory.GetFiles(Dir("frames"), "*.bin").Select(f => Path.GetFileName(f)).ToHashSet();
        Assert.Equal(files, described);
    }

    public static TheoryData<string> MessageNames()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Dir("messages"), "*.json").Order()) data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(MessageNames))]
    public void EachMessageDecodesAndEncodesBack(string file)
    {
        var text = File.ReadAllText(Path.Combine(Dir("messages"), file));
        var original = JsonNode.Parse(text)!.AsObject();
        var type = original["t"]!.GetValue<string>();
        var message = LinkMessage.Decode(text);

        // Every message of the current spec is known to this build.
        Assert.False(message is LinkUnknownMessage, $"{file}: \"{type}\" should be a known message type");
        Assert.Equal(type, message.TypeName);
        Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(message.Encode())),
            $"{file} re-encodes differently:\n{message.Encode()}");
    }

    /// <summary>The ok replies' bodies read as the typed bodies they are.</summary>
    [Fact]
    public void OkBodiesDecode()
    {
        LinkOk Ok(string file) => Assert.IsType<LinkOk>(LinkMessage.Decode(File.ReadAllText(Path.Combine(Dir("messages"), file))));

        var auth = Ok("auth_ok.json").DecodeBody<LinkAuthOkBody>();
        Assert.Equal(12, auth.Session);
        Assert.Equal(LinkRole.Control, auth.Role);
        Assert.Equal((0xF0, LinkDirection.Get), auth.Policy!.DeniedCommands[0]);

        var devices = Ok("device_list_ok.json").DecodeBody<LinkDeviceListBody>().Devices;
        Assert.Equal("E46058388B1A2E2C", devices[0].Serial);
        Assert.Equal(32, devices[0].WireVersion);

        Assert.NotEmpty(Ok("auth_list_ok.json").DecodeBody<LinkAuthListBody>().Clients);
        Assert.NotEmpty(Ok("poll_subscribe_ok.json").DecodeBody<LinkPollSubscribeBody>().Granted);

        // Hub administration (1.2).
        var config = Ok("hub_config_get_ok.json").DecodeBody<LinkHubConfigBody>();
        Assert.Equal("DSPi Bridge 3F2A", config.Name);
        Assert.Equal(1000000, config.Config["uart"]!["baud"]!.GetValue<int>());
        Assert.Equal(5, config.Options!["uart"]!["baud"]!.AsArray().Count);
        Assert.Null(config.Config["led"]!["pin"]); // an explicit null survives

        var wifi = Ok("hub_wifi_scan_ok.json").DecodeBody<LinkWifiScanBody>();
        Assert.Equal("Home", wifi.Current?.Ssid);
        Assert.Equal(-50, wifi.Current?.Rssi);
        Assert.Equal(3, wifi.Networks.Count);
        Assert.False(wifi.Networks[2].Secure);

        Assert.NotNull(Ok("hub_auth_set_ok.json").DecodeBody<LinkHubAuthSetBody>().Token);
    }

    [Fact]
    public void HubAdministrationRequestsDecode()
    {
        T Msg<T>(string file) where T : LinkMessage => Assert.IsType<T>(LinkMessage.Decode(File.ReadAllText(Path.Combine(Dir("messages"), file))));

        var pin = Msg<LinkHubAuthSet>("hub_auth_set_pin.json");
        Assert.Equal(LinkAuthMode.Pin, pin.Mode);
        Assert.Equal("482913", pin.Pin);
        Assert.DoesNotContain("482913", pin.ToString()); // secrets stay out of logs
        Assert.Null(Msg<LinkHubAuthSet>("hub_auth_set_none.json").Pin);

        var set = Msg<LinkHubConfigSet>("hub_config_set.json");
        Assert.Equal(921600, set.Config["uart"]!["baud"]!.GetValue<int>());
        Assert.Equal(921600, Msg<LinkHubConfigChanged>("hub_config_changed.json").Config["uart"]!["baud"]!.GetValue<int>());

        var join = Msg<LinkHubWifiJoin>("hub_wifi_join.json");
        Assert.Equal("Studio", join.Ssid);
        Assert.DoesNotContain(join.Password, join.ToString());

        Assert.Equal(LinkResetLevel.Factory, Msg<LinkHubReset>("hub_reset.json").Level);
        Assert.Equal(26, Msg<LinkHubRestart>("hub_restart.json").RequestId);
        var update = Msg<LinkHubUpdate>("hub_update.json");
        Assert.Equal(1376256, update.Size);
        Assert.Equal("1.0.1", update.Version);
        var done = Msg<LinkHubUpdateDone>("hub_update_done.json");
        Assert.True(done.Ok);
        Assert.Null(done.RequestId); // an event, not a reply
    }

    [Fact]
    public void TheBridgeHelloSaysItsPinIsFixed()
    {
        var hello = Assert.IsType<LinkHelloHub>(LinkMessage.Decode(File.ReadAllText(Path.Combine(Dir("messages"), "hello_hub_bridge.json"))));
        Assert.Equal(LinkHubKind.Bridge, hello.Hub.Kind);
        Assert.Equal(LinkPinSource.Fixed, hello.PinSource);
        Assert.Equal(4, hello.Limits?.MaxInflight);
    }

    // ── Captured device replies, through the app's own parsers ──

    private static JsonObject Capture => JsonNode.Parse(File.ReadAllText(Path.Combine(Dir("device"), "capture.json")))!.AsObject();

    [Fact]
    public void ThePlatformReplyDecodes()
    {
        var expect = Capture["platform"]!;
        var reply = File.ReadAllBytes(Path.Combine(Dir("device"), "platform.bin"));
        Assert.Equal(FromHex(expect["raw_hex"]!.GetValue<string>()), reply);
        var (platform, version) = FirmwareVersion.FromPlatformReply(reply)!.Value;
        Assert.Equal(expect["platform"]!.GetValue<int>(), platform);
        Assert.Equal(FirmwareVersion.Parse(expect["fw"]!.GetValue<string>())!.Value with { Beta = expect["fw_beta"]!.GetValue<int>() }, version);
    }

    [Fact]
    public void TheBulkCaptureParses()
    {
        var expect = Capture["bulk"]!;
        var bulk = File.ReadAllBytes(Path.Combine(Dir("device"), "bulk_v32.bin"));
        Assert.Equal(expect["size"]!.GetValue<int>(), bulk.Length);
        var p = BulkParamsParser.Parse(bulk);
        Assert.NotNull(p);
        Assert.Equal(expect["wire_version"]!.GetValue<int>(), p!.FormatVersion);
        Assert.Equal(expect["num_channels"]!.GetValue<int>(), p.NumChannels);
        Assert.Equal(expect["num_output_channels"]!.GetValue<int>(), p.NumOutputChannels);
        Assert.Equal(expect["num_input_channels"]!.GetValue<int>(), p.NumInputChannels);
        Assert.Equal(expect["master_volume_db"]!.GetValue<double>(), p.MasterVolumeDb, 3);
        Assert.Equal(expect["global_preamp_db"]!.GetValue<double>(), p.PreampGainDb, 3);
    }

    [Fact]
    public void TheStatusCaptureHasTheDocumentedLayout()
    {
        var expect = Capture["status"]!;
        var status = File.ReadAllBytes(Path.Combine(Dir("device"), "status_wvalue9.bin"));
        Assert.Equal(expect["size"]!.GetValue<int>(), status.Length);
        // peaks[17] u16, cpu0 u8, cpu1 u8, clip_flags u32, active_input_channels u8.
        int channels = Capture["bulk"]!["num_channels"]!.GetValue<int>();
        Assert.Equal(channels * 2 + 1 + 1 + 4 + 1, status.Length);
        Assert.Equal(expect["cpu0"]!.GetValue<int>(), status[channels * 2]);
        Assert.Equal(expect["cpu1"]!.GetValue<int>(), status[channels * 2 + 1]);
        Assert.Equal((uint)expect["clip_flags"]!.GetValue<int>(), BitConverter.ToUInt32(status, channels * 2 + 2));
        Assert.Equal(expect["active_input_channels"]!.GetValue<int>(), status[channels * 2 + 6]);
    }
}

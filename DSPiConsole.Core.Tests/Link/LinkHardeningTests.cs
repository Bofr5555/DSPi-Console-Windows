using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DSPiConsole.Core.Link;

namespace DSPiConsole.Core.Tests.Link;

/// <summary>
/// Hostile and newer-than-us input. A hub decodes what any LAN client or
/// browser sends, before authentication, and a client decodes what any hub
/// sends: every failure must come out as JsonException (the one type a
/// session loop catches), never later when a kept value is read, and nothing
/// may decode into a value the code would then trust.
/// </summary>
public class LinkHardeningTests
{
    // A JSON \u escape, built here so no tool rewrites it into the character.
    private static readonly string U = "\\" + "u";

    private static JsonException Rejects(string json) => Assert.ThrowsAny<JsonException>(() => LinkMessage.Decode(json));

    // ── Malformed JSON ──

    [Theory]
    [InlineData("""{"t":"ok","t":"err"}""")]
    [InlineData("""{"t":"device.list","id":1,"id":2}""")]
    [InlineData("""{"t":"zz","a":1,"a":2}""")]
    [InlineData("""{"t":"hub.config.set","id":1,"config":{"name":"a","name":"b"}}""")]
    [InlineData("""{"t":"ok","id":1,"config":{"name":"a","name":"b"}}""")]
    [InlineData("""{"t":"ok","id":1,"a":{"b":1,"b":2}}""")]
    public void DuplicateKeysAreRefusedAtDecode(string json) => Rejects(json);

    [Fact]
    public void LoneSurrogatesAreRefusedAtDecode()
    {
        Rejects("{\"t\":\"" + U + "D800\",\"id\":1}");
        Rejects("{\"t\":\"hub.rename\",\"id\":1,\"name\":\"" + U + "D800\"}");
        Rejects("{\"t\":\"hello\",\"proto\":{\"major\":1,\"minor\":2},\"client\":{\"name\":\"" + U + "DC00x\"}}");
        Rejects("{\"t\":\"err\",\"id\":1,\"code\":\"x\",\"msg\":\"" + U + "D800\"}");
        Rejects("{\"t\":\"ok\",\"id\":1,\"token\":\"" + U + "D800\"}");
        Rejects("{\"t\":\"hub.config.set\",\"id\":1,\"config\":{\"name\":\"" + U + "D800\"}}");
        Rejects("{\"t\":\"ok\",\"id\":1,\"" + U + "D800\":1}");
    }

    [Fact]
    public void InvalidUtf8IsRefused()
    {
        var head = Encoding.ASCII.GetBytes("{\"t\":\"");
        var tail = Encoding.ASCII.GetBytes("\",\"id\":1}");
        foreach (var bad in new[] { new byte[] { 0xC0, 0xAE }, new byte[] { 0xFF }, new byte[] { 0xED, 0xA0, 0x80 } })
            Assert.ThrowsAny<JsonException>(() => LinkMessage.Decode(head.Concat(bad).Concat(tail).ToArray()));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"hello\"")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{\"t\":\"ok\"} trailing")]
    [InlineData("{\"t\":5}")]
    [InlineData("{\"t\":null}")]
    [InlineData("{\"t\":{\"a\":1}}")]
    public void NotAMessageIsRefused(string json) => Rejects(json);

    [Fact]
    public void DeepNestingIsRefused() =>
        Rejects("{\"t\":\"ok\",\"a\":" + new string('[', 200) + new string(']', 200) + "}");

    // ── Enumerations ──

    /// <summary>A role is one of three spellings: never a number, a comma list
    /// or padded text, so no value outside the enum reaches a permission check.</summary>
    [Theory]
    [InlineData("99")]
    [InlineData("\"99\"")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("\"viewer, admin\"")]
    [InlineData("\" admin\"")]
    [InlineData("\"Admin\"")]
    [InlineData("\"superuser\"")]
    public void ARoleIsOnlyOneOfItsSpellings(string role)
    {
        Rejects($$"""{"t":"auth.pair","id":1,"pin":"1","name":"n","role":{{role}}}""");
        Rejects($$"""{"t":"auth.set_role","id":1,"cid":3,"role":{{role}}}""");
        Assert.ThrowsAny<JsonException>(() =>
            LinkMessage.Decode($$"""{"t":"ok","id":1,"session":3,"role":{{role}}}""").Should<LinkOk>().DecodeBody<LinkAuthOkBody>());
    }

    [Fact]
    public void AnAuthModeIsOnlyOneOfItsSpellings()
    {
        Rejects("""{"t":"hub.auth.set","id":1,"mode":5}""");
        Rejects("""{"t":"hub.auth.set","id":1,"mode":"oauth"}""");
    }

    /// <summary>Whatever slips through, a value outside the roles permits nothing.</summary>
    [Fact]
    public void AnUndefinedRolePermitsNothing()
    {
        Assert.False(((LinkRole)99).Permits(LinkCommandClass.Read));
        Assert.False(LinkPolicy.Embedded!.IsAllowed((LinkRole)99, 0x50, LinkDirection.Get));
        Assert.Throws<ArgumentOutOfRangeException>(() => ((LinkRole)99).WireName());
    }

    /// <summary>Open vocabularies: a newer peer's new value reads as Unknown
    /// instead of failing the hello or the whole device list.</summary>
    [Fact]
    public void ANewerPeersNewValuesReadAsUnknown()
    {
        var hello = Assert.IsType<LinkHelloHub>(LinkMessage.Decode("""
            {"t":"hello","proto":{"major":1,"minor":3},"hub":{"id":"u","name":"h","kind":"relay"},"auth":"none","caps":["cmd"]}
            """));
        Assert.Equal(LinkHubKind.Unknown, hello.Hub.Kind);

        var list = Assert.IsType<LinkOk>(LinkMessage.Decode("""
            {"t":"ok","id":5,"devices":[
              {"handle":0,"serial":"A","state":"online","link":"usb"},
              {"handle":1,"serial":"B","state":"sleeping","link":"ble"}]}
            """)).DecodeBody<LinkDeviceListBody>();
        Assert.Equal(2, list.Devices.Count);
        Assert.Equal(LinkDeviceState.Online, list.Devices[0].State);
        Assert.Equal(LinkDeviceState.Unknown, list.Devices[1].State);
        Assert.Equal(LinkDeviceLinkKind.Unknown, list.Devices[1].Link);

        Assert.Equal(LinkFwPhase.Unknown, Assert.IsType<LinkFwProgress>(
            LinkMessage.Decode("""{"t":"fw.progress","handle":0,"phase":"erasing"}""")).Phase);

        // But a number is still not a spelling.
        Rejects("""{"t":"fw.progress","handle":0,"phase":3}""");
        // And Unknown is never sent.
        Assert.ThrowsAny<JsonException>(() => new LinkFwProgress(0, LinkFwPhase.Unknown).Encode());
    }

    [Fact]
    public void AnUnknownPinSourceMeansAWindow()
    {
        var h = Assert.IsType<LinkHelloHub>(LinkMessage.Decode("""
            {"t":"hello","proto":{"major":1,"minor":3},"hub":{"id":"u","name":"h","kind":"bridge"},"auth":"pin","pin_source":"qr","caps":["cmd"]}
            """));
        Assert.Equal(LinkPinSource.Window, h.PinSource);
    }

    // ── Values ──

    [Theory]
    [InlineData("\"garbage\"")]
    [InlineData("\"\"")]
    [InlineData("\"9999-12-31T23:59:59-01:00\"")]
    [InlineData("\"0001-01-01T00:00:00+01:00\"")]
    [InlineData("\"2026-01-01T00:00:00+15:00\"")]
    [InlineData("\"Tue, 01 Jan 2026 00:00:00 GMT\"")]
    [InlineData("\"10/18/2026 22:35\"")]
    [InlineData("\"2026-01-01\"")]
    [InlineData("\"12:00\"")]
    [InlineData("12")]
    public void ABadTimestampIsAJsonError(string created) =>
        Assert.ThrowsAny<JsonException>(() => Assert.IsType<LinkOk>(LinkMessage.Decode(
            $$"""{"t":"ok","id":2,"clients":[{"cid":3,"name":"n","role":"control","created":{{created}}}]}""")).DecodeBody<LinkAuthListBody>());

    [Theory]
    [InlineData("2026-09-08T10:12:00Z", "2026-09-08T10:12:00Z")]
    [InlineData("2026-09-08T10:12:00.5Z", "2026-09-08T10:12:00Z")]
    [InlineData("2026-09-08T12:12:00+02:00", "2026-09-08T10:12:00Z")]
    public void IsoTimestampsRead(string text, string written)
    {
        var body = Assert.IsType<LinkOk>(LinkMessage.Decode(
            $$"""{"t":"ok","id":2,"clients":[{"cid":3,"name":"n","role":"control","created":"{{text}}"}]}""")).DecodeBody<LinkAuthListBody>();
        Assert.Contains($"\"created\":\"{written}\"", LinkOk.WithBody(2, body).Encode());
    }

    [Theory]
    [InlineData("""{"t":"poll.subscribe","id":1,"handle":0,"polls":[null]}""")]
    [InlineData("""{"t":"hello","proto":{"major":1,"minor":2},"hub":{"id":"u","name":"h","kind":"console"},"auth":"none","caps":[null]}""")]
    [InlineData("""{"t":"poll.unsubscribe","id":1,"handle":0,"slots":null}""")]
    public void ANullInAListIsRefused(string json) => Rejects(json);

    [Theory]
    [InlineData("""{"t":"ok","id":5,"devices":[null]}""", typeof(LinkDeviceListBody))]
    [InlineData("""{"t":"ok","id":2,"clients":[null]}""", typeof(LinkAuthListBody))]
    [InlineData("""{"t":"ok","id":8,"granted":[null]}""", typeof(LinkPollSubscribeBody))]
    [InlineData("""{"t":"ok","id":22,"networks":[null]}""", typeof(LinkWifiScanBody))]
    [InlineData("""{"t":"ok","id":1,"session":3,"role":"viewer","policy":{"denied":[null]}}""", typeof(LinkAuthOkBody))]
    public void ANullInAReplyListIsRefused(string json, Type body)
    {
        var ok = Assert.IsType<LinkOk>(LinkMessage.Decode(json));
        var decode = typeof(LinkOk).GetMethod(nameof(LinkOk.DecodeBody))!.MakeGenericMethod(body);
        var e = Assert.Throws<System.Reflection.TargetInvocationException>(() => decode.Invoke(ok, null));
        Assert.IsAssignableFrom<JsonException>(e.InnerException);
    }

    [Fact]
    public void ANonFiniteNumberIsRefusedAndNeverWritten()
    {
        var ok = Assert.IsType<LinkOk>(LinkMessage.Decode("""{"t":"ok","id":13,"devices":[{"handle":0,"avg_rtt_ms":1e400}]}"""));
        Assert.ThrowsAny<JsonException>(() => ok.DecodeBody<LinkHubStatsBody>());
        // A hub's 0/0 average goes out as 0 rather than failing the reply.
        var stats = LinkOk.WithBody(13, new LinkHubStatsBody(1, 2, [new LinkHubDeviceStats(0, 0, 0, double.NaN)]));
        Assert.Contains("\"avg_rtt_ms\":0", stats.Encode());
    }

    [Fact]
    public void MalformedBase64IsNotTheSameAsAbsent()
    {
        var body = new LinkSnapshotBody(0, BulkB64: "not base64!");
        Assert.ThrowsAny<JsonException>(() => body.Bulk);
        Assert.Null(new LinkSnapshotBody(0).Bulk);
    }

    // ── Request ids ──

    [Fact]
    public void LargeIdsSurvive()
    {
        const long big = 1730000000000;
        Assert.Equal(big, LinkMessage.Decode("""{"t":"device.list","id":1730000000000}""").RequestId);
        Assert.Equal(big, LinkMessage.Decode("""{"t":"err","id":1730000000000,"code":"denied"}""").RequestId);
        Assert.Equal(big, LinkMessage.Decode("""{"t":"ok","id":1730000000000}""").RequestId);
        Assert.Contains("\"id\":1730000000000", new LinkDeviceListRequest(big).Encode());
    }

    /// <summary>ok and err read an id the same way: a fraction is not an id.</summary>
    [Fact]
    public void OkAndErrAgreeOnWhatAnIdIs()
    {
        Assert.Null(LinkMessage.Decode("""{"t":"ok","id":7.5}""").RequestId);
        Assert.Null(LinkMessage.Decode("""{"t":"ok","id":"7"}""").RequestId);
        Rejects("""{"t":"err","id":7.5,"code":"denied"}""");
    }

    /// <summary>A known request with a bad field keeps its type and id, so the
    /// hub can answer bad_request with the id (spec 7.1).</summary>
    [Fact]
    public void AMalformedRequestKeepsItsId()
    {
        var e = Assert.IsType<LinkMessageException>(Rejects("""{"t":"device.rename","id":6,"handle":0}"""));
        Assert.Equal("device.rename", e.Type);
        Assert.Equal(6, e.RequestId);
        var noId = Assert.IsType<LinkMessageException>(Rejects("""{"t":"device.rename","handle":0,"name":"x"}"""));
        Assert.Null(noId.RequestId);
    }

    // ── Secrets ──

    [Fact]
    public void SecretsStayOutOfToString()
    {
        Assert.DoesNotContain("482913", new LinkAuthPair(1, "482913", "Phone").ToString());
        Assert.DoesNotContain("s3cret", new LinkAuthToken(1, "s3cret").ToString());
        Assert.DoesNotContain("s3cret", new LinkAuthOkBody(3, LinkRole.Control, "s3cret").ToString());
        Assert.DoesNotContain("s3cret", new LinkHubAuthSetBody("s3cret").ToString());
        Assert.DoesNotContain("s3cret", LinkOk.WithBody(1, new LinkAuthOkBody(3, LinkRole.Control, "s3cret")).ToString());
        Assert.DoesNotContain("482913", new LinkHubAuthSet(1, LinkAuthMode.Pin, "482913").ToString());
        Assert.DoesNotContain("hunter2", new LinkHubWifiJoin(1, "Home", "hunter2").ToString());
    }

    // ── The policy table ──

    [Theory]
    [InlineData("""{"spec_version":"1","generated":"x"}""")]
    [InlineData("""{"spec_version":"1","generated":"x","commands":null}""")]
    [InlineData("""{"spec_version":"1","generated":"x","commands":[null]}""")]
    [InlineData("""{"spec_version":"1","generated":"x","commands":[{"code":1,"name":"A","dirs":null,"class":"read"}]}""")]
    [InlineData("""{"spec_version":"1","generated":"x","commands":[{"code":1,"name":"A","dirs":["get"],"class":0}]}""")]
    [InlineData("""{"spec_version":"1","generated":"x","commands":[{"code":1,"name":"A","dirs":["get"],"class":"read"},{"code":1,"name":"B","dirs":["set"],"class":"control"}]}""")]
    public void AMalformedTableIsAJsonError(string json) =>
        Assert.ThrowsAny<JsonException>(() => LinkPolicy.Parse(Encoding.UTF8.GetBytes(json)));
}

internal static class LinkTestExtensions
{
    public static T Should<T>(this LinkMessage message) where T : LinkMessage => Assert.IsType<T>(message);
}

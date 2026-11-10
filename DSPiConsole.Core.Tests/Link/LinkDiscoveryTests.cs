using System.Text.Json;
using System.Text.Json.Nodes;
using DSPiConsole.Core.Link;

namespace DSPiConsole.Core.Tests.Link;

/// <summary>Discovery: the DNS-SD TXT record (spec 3.1) and the HTTP info
/// endpoint (spec 3.2).</summary>
public class LinkDiscoveryTests
{
    [Fact]
    public void Defaults()
    {
        Assert.Equal("_dspi._tcp", LinkService.ServiceType);
        Assert.Equal(11915, LinkService.DefaultPort);
        Assert.Equal(0x2E8B, LinkService.DefaultPort);
        Assert.Equal("/dspi/v1", LinkService.DefaultPath);
        Assert.Equal("/dspi/v1/info", LinkService.InfoPath);
        Assert.Equal("dspi-link-1", LinkService.SubProtocol);
    }

    [Fact]
    public void ATxtRecordReadsEveryKey()
    {
        var txt = LinkTxtRecord.TryParse(new Dictionary<string, string>
        {
            ["v"] = "1", ["hid"] = "6F1A2B3C-4D5E-4F60-8A9B-0C1D2E3F4A5B", ["kind"] = "bridge", ["auth"] = "pin",
            ["n"] = "2", ["d"] = "E46058388B1A2E2C,0123456789ABCDEF", ["tls"] = "1", ["path"] = "/x", ["web"] = "1",
            ["future"] = "ignored",
        })!;
        Assert.Equal(1, txt.Version);
        Assert.Equal("6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b", txt.HubId); // hid is a lowercase UUID
        Assert.Equal(LinkHubKind.Bridge, txt.Kind);
        Assert.Equal(LinkAuthMode.Pin, txt.Auth);
        Assert.Equal(2, txt.DeviceCount);
        Assert.Equal(["E46058388B1A2E2C", "0123456789ABCDEF"], txt.Serials);
        Assert.True(txt.Tls);
        Assert.True(txt.Web);
        Assert.Equal("/x", txt.WebSocketPath);
    }

    [Fact]
    public void OptionalTxtKeysDefault()
    {
        var txt = LinkTxtRecord.TryParse(new Dictionary<string, string>
            { ["v"] = "1", ["hid"] = "h", ["kind"] = "console", ["auth"] = "none", ["n"] = "0" })!;
        Assert.Null(txt.Serials);
        Assert.False(txt.Tls);
        Assert.False(txt.Web);
        Assert.Equal("/dspi/v1", txt.WebSocketPath);
        // A newer kind still lists the hub.
        Assert.Equal(LinkHubKind.Unknown, LinkTxtRecord.TryParse(new Dictionary<string, string>
            { ["v"] = "1", ["hid"] = "h", ["kind"] = "phone", ["auth"] = "none", ["n"] = "0" })!.Kind);
    }

    [Theory]
    [InlineData("v")]
    [InlineData("hid")]
    [InlineData("kind")]
    [InlineData("auth")]
    [InlineData("n")]
    public void ATxtRecordMissingARequiredKeyIsRefused(string missing)
    {
        var keys = new Dictionary<string, string> { ["v"] = "1", ["hid"] = "h", ["kind"] = "console", ["auth"] = "none", ["n"] = "0" };
        keys.Remove(missing);
        Assert.Null(LinkTxtRecord.TryParse(keys));
    }

    [Fact]
    public void ABadTxtValueIsRefused()
    {
        Assert.Null(LinkTxtRecord.TryParse(new Dictionary<string, string> { ["v"] = "one", ["hid"] = "h", ["kind"] = "console", ["auth"] = "none", ["n"] = "0" }));
        Assert.Null(LinkTxtRecord.TryParse(new Dictionary<string, string> { ["v"] = "1", ["hid"] = "h", ["kind"] = "console", ["auth"] = "oauth", ["n"] = "0" }));
        Assert.Null(LinkTxtRecord.TryParse(new Dictionary<string, string> { ["v"] = "1", ["hid"] = "h", ["kind"] = "console", ["auth"] = "none", ["n"] = "-1" }));
    }

    [Fact]
    public void ATxtRecordWritesRequiredKeysAndDropsDevicesPastTheBudget()
    {
        var few = new LinkTxtRecord(1, "h", LinkHubKind.Console, LinkAuthMode.None, 1, ["E46058388B1A2E2C"]).ToDictionary();
        Assert.Equal(["auth", "d", "hid", "kind", "n", "v"], few.Keys.Order());
        Assert.Equal("E46058388B1A2E2C", few["d"]);

        var many = Enumerable.Range(0, 40).Select(i => $"SERIAL{i:D10}").ToList();
        var full = new LinkTxtRecord(1, "h", LinkHubKind.Console, LinkAuthMode.None, many.Count, many).ToDictionary();
        Assert.False(full.ContainsKey("d"));
        Assert.Equal("40", full["n"]);
    }

    [Fact]
    public void TheInfoReplyRoundTrips()
    {
        var info = LinkInfo.Decode("""
            {"proto":{"major":1,"minor":2},
             "hub":{"id":"u","name":"Studio Mac","kind":"console","version":"1.1.7"},
             "auth":"none","tls":false,"ws":"/dspi/v1",
             "devices":[{"serial":"E46058388B1A2E2C","name":"Living room"}]}
            """);
        Assert.Equal(new LinkProtoVersion(1, 2), info.Proto);
        Assert.Equal("Studio Mac", info.Hub.Name);
        Assert.Equal(LinkAuthMode.None, info.Auth);
        Assert.Equal("/dspi/v1", info.Ws);
        Assert.Equal("Living room", info.Devices![0].Name);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(info.Encode()), JsonNode.Parse(LinkInfo.Decode(info.Encode()).Encode())));
    }

    [Theory]
    [InlineData("""{"hub":{"id":"u","name":"h","kind":"console"},"auth":"none","ws":"/dspi/v1"}""")]
    [InlineData("""{"proto":{"major":1,"minor":2},"hub":{"id":"u","name":"h","kind":"console"},"auth":"none"}""")]
    [InlineData("""{"proto":{"major":1,"minor":2},"proto":{"major":1,"minor":2},"hub":{"id":"u","name":"h","kind":"console"},"auth":"none","ws":"/"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void AMalformedInfoReplyIsAJsonError(string json) => Assert.ThrowsAny<JsonException>(() => LinkInfo.Decode(json));
}

/// <summary>Typed hub addresses (spec 3.3). Ported from the macOS Console's HubAddressTests.</summary>
public class LinkHubAddressTests
{
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", 11915)]
    [InlineData("192.168.1.20:12000", "192.168.1.20", 12000)]
    [InlineData("studio.local", "studio.local", 11915)]
    [InlineData("[fe80::1%12]:12000", "fe80::1%12", 12000)]
    [InlineData("[fe80::1%12]", "fe80::1%12", 11915)]
    [InlineData("fe80::1", "fe80::1", 11915)] // a bare literal's last group is not a port
    [InlineData("  10.0.0.2 ", "10.0.0.2", 11915)]
    public void Parses(string text, string host, int port)
    {
        Assert.True(LinkHubAddress.TryParse(text, out var h, out var p), text);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("host:abc")]
    [InlineData("[fe80::1")]
    [InlineData("[fe80::1]x")]
    [InlineData(":12000")]
    [InlineData("[]:12000")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:-1")]
    public void RefusesNonsense(string? text) => Assert.False(LinkHubAddress.TryParse(text, out _, out _));

    [Fact]
    public void AnAddressKeepsItsScopeForTheDialButNotInTheUri()
    {
        var e = LinkHubAddress.Endpoint("fe80::1c2b:3d4e:5f60:7182%12", 11915);
        Assert.NotNull(e.Address);
        Assert.Equal(12, e.Address!.ScopeId);
        Assert.Equal("ws://[fe80::1c2b:3d4e:5f60:7182]:11915/dspi/v1", e.WebSocketUri.ToString());
        Assert.Equal("[fe80::1c2b:3d4e:5f60:7182%12]:11915", LinkHubAddress.Display("fe80::1c2b:3d4e:5f60:7182%12", 11915));
    }

    [Fact]
    public void NamesAndIPv4()
    {
        Assert.Null(LinkHubAddress.Endpoint("studio.local", 12000).Address);
        Assert.Equal("ws://studio.local:12000/dspi/v1", LinkHubAddress.Endpoint("studio.local", 12000).WebSocketUri.ToString());
        Assert.Equal("ws://192.168.1.20:11915/dspi/v1", LinkHubAddress.Endpoint("192.168.1.20", 11915).WebSocketUri.ToString());
        Assert.Equal("ws://[2001:db8::5]:11915/dspi/v1", LinkHubAddress.Endpoint("2001:db8::5", 11915).WebSocketUri.ToString());
        Assert.Equal("studio.local:12000", LinkHubAddress.Display("studio.local", 12000));
    }
}

public class LinkTokenStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dspi-tokens-{Guid.NewGuid():N}.dat");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void TokensAreKeptPerHubIdWhateverItsCase()
    {
        var store = new LinkTokenStore(_path);
        Assert.Null(store.Token("6F1A2B3C-4D5E-4F60-8A9B-0C1D2E3F4A5B"));
        store.SetToken("6F1A2B3C-4D5E-4F60-8A9B-0C1D2E3F4A5B", "tok-a");
        store.SetToken("0d6c41e2-93b7-4f0a-b5a8-3c2e7f19a4d0", "tok-b");
        var again = new LinkTokenStore(_path);
        Assert.Equal("tok-a", again.Token("6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b"));
        Assert.Equal("tok-b", again.Token("0D6C41E2-93B7-4F0A-B5A8-3C2E7F19A4D0"));
        again.RemoveToken("6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b");
        Assert.Null(new LinkTokenStore(_path).Token("6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b"));
    }

    [Fact]
    public void TheFileIsNotPlainText()
    {
        var store = new LinkTokenStore(_path);
        store.SetToken("hub", "secret-token-value");
        Assert.DoesNotContain("secret-token-value", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(_path)));
    }

    [Fact]
    public void ADamagedFileReadsAsNoTokens()
    {
        File.WriteAllBytes(_path, [1, 2, 3, 4]);
        var store = new LinkTokenStore(_path);
        Assert.Null(store.Token("hub"));
        store.SetToken("hub", "t");
        Assert.Equal("t", store.Token("hub"));
    }
}

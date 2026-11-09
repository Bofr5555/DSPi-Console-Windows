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

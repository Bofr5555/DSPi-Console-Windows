using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DSPiConsole.Core.Link;

/// <summary>DNS-SD service, port and path defaults (spec 3.1, 4).</summary>
public static class LinkService
{
    public const string ServiceType = "_dspi._tcp";
    /// <summary>The decimal of the USB VID 0x2E8B.</summary>
    public const int DefaultPort = 11915;
    public const string DefaultPath = "/dspi/v1";
    public const string InfoPath = "/dspi/v1/info";
    public const string SubProtocol = "dspi-link-1";
    /// <summary>The <c>d</c> key is left out when it would push the TXT
    /// record past this many bytes.</summary>
    public const int TxtBudget = 400;
}

/// <summary>
/// A hub's DNS-SD TXT record (spec 3.1). Values are ASCII; unknown keys are
/// ignored. <see cref="TryParse"/> refuses a record missing a required key or
/// with a malformed one, since such a hub can't be told apart or reached.
/// </summary>
public sealed record LinkTxtRecord(
    int Version,
    string HubId,
    LinkHubKind Kind,
    LinkAuthMode Auth,
    int DeviceCount,
    IReadOnlyList<string>? Serials = null,
    bool Tls = false,
    string? Path = null,
    bool Web = false)
{
    /// <summary>Where the WebSocket is: <see cref="Path"/>, or the default.</summary>
    public string WebSocketPath => string.IsNullOrEmpty(Path) ? LinkService.DefaultPath : Path;

    public static LinkTxtRecord? TryParse(IReadOnlyDictionary<string, string> txt)
    {
        string? Get(string key) => txt.TryGetValue(key, out var v) ? v : null;
        if (!int.TryParse(Get("v"), NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v < 1) return null;
        if (Get("hid") is not { Length: > 0 } hid) return null;
        var kind = Get("kind") switch { "console" => LinkHubKind.Console, "bridge" => LinkHubKind.Bridge, null => (LinkHubKind?)null, _ => LinkHubKind.Unknown };
        var auth = Get("auth") switch { "none" => LinkAuthMode.None, "pin" => LinkAuthMode.Pin, _ => (LinkAuthMode?)null };
        if (kind == null || auth == null) return null;
        if (!int.TryParse(Get("n"), NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return null;
        var serials = Get("d") is { Length: > 0 } d
            ? d.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;
        return new LinkTxtRecord(v, hid.ToLowerInvariant(), kind.Value, auth.Value, n, serials,
            Get("tls") == "1", Get("path"), Get("web") == "1");
    }

    /// <summary>The keys to advertise. <c>d</c> is dropped when the record
    /// would pass the 400-byte budget.</summary>
    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var txt = new Dictionary<string, string>
        {
            ["v"] = Version.ToString(CultureInfo.InvariantCulture),
            ["hid"] = HubId,
            ["kind"] = Kind switch { LinkHubKind.Console => "console", LinkHubKind.Bridge => "bridge", _ => throw new InvalidOperationException("No wire spelling for an unknown hub kind.") },
            ["auth"] = Auth == LinkAuthMode.Pin ? "pin" : "none",
            ["n"] = DeviceCount.ToString(CultureInfo.InvariantCulture),
        };
        if (Tls) txt["tls"] = "1";
        if (!string.IsNullOrEmpty(Path) && Path != LinkService.DefaultPath) txt["path"] = Path;
        if (Web) txt["web"] = "1";
        if (Serials is { Count: > 0 })
        {
            var d = string.Join(",", Serials);
            if (Size(txt) + Entry("d", d) <= LinkService.TxtBudget) txt["d"] = d;
        }
        return txt;
    }

    // Each TXT string is a length byte then "key=value".
    private static int Entry(string key, string value) => 1 + Encoding.ASCII.GetByteCount(key) + 1 + Encoding.ASCII.GetByteCount(value);
    private static int Size(Dictionary<string, string> txt) => txt.Sum(p => Entry(p.Key, p.Value));
}

/// <summary>A device as the info endpoint lists it.</summary>
public sealed record LinkInfoDevice(
    [property: JsonPropertyName("serial")] string Serial,
    [property: JsonPropertyName("name")] string? Name = null);

/// <summary>
/// GET /dspi/v1/info (spec 3.2): what a manual-IP entry or a script reads
/// before opening the WebSocket. Served with Access-Control-Allow-Origin: *.
/// </summary>
public sealed record LinkInfo(
    [property: JsonPropertyName("proto")] LinkProtoVersion Proto,
    [property: JsonPropertyName("hub")] LinkHubInfo Hub,
    [property: JsonPropertyName("auth")] LinkAuthMode Auth,
    [property: JsonPropertyName("ws")] string Ws,
    [property: JsonPropertyName("tls")] bool Tls = false,
    [property: JsonPropertyName("devices")] IReadOnlyList<LinkInfoDevice>? Devices = null)
{
    public string Encode() => JsonSerializer.Serialize(this, LinkJson.Options);

    /// <summary>Throws only JsonException.</summary>
    public static LinkInfo Decode(string json)
    {
        try
        {
            // Validated the same way as a message: duplicate keys, bad strings.
            var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("The info reply is a JSON object.");
            LinkMessage.ValidateTree(node);
            return node.Deserialize<LinkInfo>(LinkJson.Options) ?? throw new JsonException("Empty info reply.");
        }
        catch (Exception e) when (e is not JsonException)
        {
            throw new JsonException("Malformed info reply.", e);
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DSPiConsole.Core.Link;

// JSON control plane of the DSPi Link protocol, spec section 7. One text frame
// is one LinkMessage. Unknown message types decode to LinkUnknownMessage and
// unknown fields are ignored, which is what makes minor versions additive.
// Every wire key is spelled out with JsonPropertyName (snake_case, as the spec
// writes it); no naming policy, so keys with digits ("bulk_b64", "sha256")
// can't be mangled. Port of the macOS Console's LinkMessages.swift.

/// <summary>Shared JSON settings for the control plane.</summary>
public static class LinkJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        // Optional fields are absent rather than null, as on the Mac.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A required field that is missing or null fails the decode.
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        WriteIndented = false,
        Converters =
        {
            new LinkTimestampConverter(),
            new LinkFiniteDoubleConverter(),
            new LinkNonNullListConverterFactory(),
        },
    };
}

// ── Small vocabularies ──
// Closed ones (auth mode, role, command class) accept only their spellings:
// they gate permissions. Open ones (hub kind, device state, link kind,
// firmware phase) read an unknown spelling from a newer peer as Unknown, so
// one new value can't sink a whole hello or device list.

[JsonConverter(typeof(LinkStrictEnumConverter<LinkAuthMode>))]
public enum LinkAuthMode
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("pin")] Pin,
}

[JsonConverter(typeof(LinkOpenEnumConverter<LinkHubKind>))]
public enum LinkHubKind
{
    /// <summary>A kind this build doesn't know. Go by caps and limits.</summary>
    Unknown = -1,
    [JsonStringEnumMemberName("console")] Console,
    [JsonStringEnumMemberName("bridge")] Bridge,
}

[JsonConverter(typeof(LinkOpenEnumConverter<LinkDeviceState>))]
public enum LinkDeviceState
{
    /// <summary>A state this build doesn't know; treat the device as not usable.</summary>
    Unknown = -1,
    [JsonStringEnumMemberName("online")] Online,
    [JsonStringEnumMemberName("offline")] Offline,
    [JsonStringEnumMemberName("updating")] Updating,
}

/// <summary>How the hub reaches the device: what to expect for latency and
/// bulk timing.</summary>
[JsonConverter(typeof(LinkOpenEnumConverter<LinkDeviceLinkKind>))]
public enum LinkDeviceLinkKind
{
    Unknown = -1,
    [JsonStringEnumMemberName("usb")] Usb,
    [JsonStringEnumMemberName("uart")] Uart,
}

[JsonConverter(typeof(LinkOpenEnumConverter<LinkFwPhase>))]
public enum LinkFwPhase
{
    Unknown = -1,
    [JsonStringEnumMemberName("uploading")] Uploading,
    [JsonStringEnumMemberName("rebooting")] Rebooting,
    [JsonStringEnumMemberName("copying")] Copying,
    [JsonStringEnumMemberName("verifying")] Verifying,
}

/// <summary>Hub capabilities from hello.caps. The wire field stays a string
/// list so a hub may advertise ones this build never heard of.</summary>
public static class LinkCapability
{
    public const string Cmd = "cmd";
    public const string Notify = "notify";
    public const string Poll = "poll";
    public const string Snapshot = "snapshot";
    public const string Lock = "lock";
    public const string FwInstall = "fw_install";
    public const string Web = "web";
    public const string Rename = "rename";
    /// <summary>hub.config.get / set / changed (spec 7.10, 1.2).</summary>
    public const string HubConfig = "hub_config";
    /// <summary>hub.wifi.scan / join (spec 7.11, 1.2).</summary>
    public const string Wifi = "wifi";
    /// <summary>hub.auth.set (spec 7.12, 1.2).</summary>
    public const string AuthConfig = "auth_config";
    /// <summary>hub.restart / hub.reset (spec 7.13, 1.2).</summary>
    public const string Reset = "reset";
    /// <summary>hub.update / hub.update.done (spec 7.14, 1.2).</summary>
    public const string HubUpdate = "hub_update";
}

/// <summary>WebSocket close codes a hub uses (spec 5).</summary>
public static class LinkCloseCode
{
    /// <summary>A normal close. The spec gives no reconnect rule for it; this
    /// build reconnects, as for any close it isn't told to stay away after.</summary>
    public const int NormalClosure = 1000;
    /// <summary>Keepalive failure: no inbound frame for 30 s.</summary>
    public const int KeepaliveFailure = 1001;
    /// <summary>The client stopped reading: unwritable for 10 s.</summary>
    public const int StoppedReading = 1011;
    /// <summary>The client sent faster than the hub can take.</summary>
    public const int TooFast = 1013;
    /// <summary>Protocol error. The same client would fail the same way, so
    /// it does not reconnect.</summary>
    public const int ProtocolError = 4000;
    /// <summary>Authentication timeout, or the failure limit reached.</summary>
    public const int AuthFailed = 4001;
    /// <summary>Token revoked (also every other session when a PIN is set).</summary>
    public const int TokenRevoked = 4002;
    /// <summary>Hub shutting down, restarting, rejoining Wi-Fi or updating.</summary>
    public const int ShuttingDown = 4003;

    /// <summary>Whether a client reconnects (with backoff) after this close.
    /// 4001 and 4002 need the user (pair again), and 4000 would only repeat.</summary>
    public static bool Reconnects(int code) => code is not (ProtocolError or AuthFailed or TokenRevoked);
}

/// <summary>hub.reset levels (spec 7.13).</summary>
public static class LinkResetLevel
{
    /// <summary>The saved network connection only (hubs with wifi).</summary>
    public const string Network = "network";
    /// <summary>All hub state; the hub gets a new hid.</summary>
    public const string Factory = "factory";
}

/// <summary>Error codes from spec 7.1, kept as strings on the wire so a new
/// code is not a decode failure.</summary>
public static class LinkErrorCode
{
    public const string Unauthenticated = "unauthenticated";
    public const string Denied = "denied";
    public const string BadRequest = "bad_request";
    public const string UnknownType = "unknown_type";
    public const string NoDevice = "no_device";
    public const string Locked = "locked";
    public const string Busy = "busy";
    public const string RateLimited = "rate_limited";
    public const string Unsupported = "unsupported";
    public const string Internal = "internal";
}

/// <summary>Where a hub's PIN comes from (spec 6.2, added in 1.2).</summary>
public static class LinkPinSource
{
    /// <summary>The user opens a pairing window and reads a fresh PIN off the
    /// hub's screen. The default when the field is absent.</summary>
    public const string Window = "window";
    /// <summary>A fixed PIN, set on the hub (the ESP32 bridge).</summary>
    public const string Fixed = "fixed";
}

// ── Shared objects ──

/// <summary>Protocol version. Defaults to 1.2, the version this build speaks.
/// A peer that omits it is read as 1.0.</summary>
public sealed record LinkProtoVersion
{
    public const int CurrentMajor = 1;
    public const int CurrentMinor = 2;

    /// <summary>This build's version.</summary>
    public LinkProtoVersion() : this(CurrentMajor, CurrentMinor) { }

    /// <summary>Both fields are required on the wire: a missing minor must not
    /// read as the newest one (spec 10.2: use only what the hub says exists).</summary>
    [JsonConstructor]
    public LinkProtoVersion(int major, int minor)
    {
        Major = major;
        Minor = minor;
    }

    [JsonPropertyName("major")] public int Major { get; init; }
    [JsonPropertyName("minor")] public int Minor { get; init; }
}

/// <summary>A message whose type is known but which could not be decoded:
/// a missing or bad field. Carries the type and the request id when there
/// was one, so a hub can answer err bad_request with the same id.</summary>
public sealed class LinkMessageException(string type, long? requestId, Exception inner)
    : JsonException($"Malformed {type}: {inner.Message}", inner)
{
    public string Type { get; } = type;
    public long? RequestId { get; } = requestId;
}

public sealed record LinkClientInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("app")] string? App = null,
    [property: JsonPropertyName("version")] string? Version = null);

public sealed record LinkHubInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] LinkHubKind Kind,
    [property: JsonPropertyName("version")] string? Version = null);

public sealed record LinkLimits(
    [property: JsonPropertyName("max_frame")] int? MaxFrame = null,
    [property: JsonPropertyName("max_payload")] int? MaxPayload = null,
    [property: JsonPropertyName("max_inflight")] int? MaxInflight = null,
    [property: JsonPropertyName("poll_max_hz")] int? PollMaxHz = null,
    [property: JsonPropertyName("poll_budget_bps")] int? PollBudgetBps = null);

/// <summary>The session's effective denial list: [bRequest, dir] pairs, so a
/// client can grey out controls instead of guessing the hub's table.</summary>
public sealed record LinkPolicyDescriptor(
    [property: JsonPropertyName("denied")] IReadOnlyList<IReadOnlyList<int>>? Denied = null)
{
    [JsonIgnore]
    public IReadOnlyList<(byte BRequest, LinkDirection Direction)> DeniedCommands =>
        (Denied ?? Array.Empty<IReadOnlyList<int>>())
            .Where(p => p != null && p.Count >= 2 && p[0] is >= 0 and <= 255 && p[1] is 0 or 1)
            .Select(p => ((byte)p[0], (LinkDirection)p[1]))
            .ToList();

    public static LinkPolicyDescriptor From(IEnumerable<(byte Code, LinkDirection Direction)> denied) =>
        new(denied.Select(d => (IReadOnlyList<int>)new[] { (int)d.Code, (int)d.Direction }).ToList());
}

public sealed record LinkDeviceInfo(
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("serial")] string Serial,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("platform")] int? Platform = null,
    [property: JsonPropertyName("fw")] string? Fw = null,
    /// <summary>Beta number, 0 for final; absent means unknown, not final (spec 7.4, 1.1).</summary>
    [property: JsonPropertyName("fw_beta")] int? FwBeta = null,
    [property: JsonPropertyName("outputs")] int? Outputs = null,
    [property: JsonPropertyName("inputs")] int? Inputs = null,
    [property: JsonPropertyName("wire_version")] int? WireVersion = null,
    [property: JsonPropertyName("state")] LinkDeviceState? State = null,
    [property: JsonPropertyName("link")] LinkDeviceLinkKind? Link = null,
    /// <summary>Session holding the exclusive lock; absent or null when free.</summary>
    [property: JsonPropertyName("locked_by")] int? LockedBy = null);

/// <summary>One entry of a poll.subscribe request: the GET to run and how often.</summary>
public sealed record LinkPollSpec(
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("req")] int Req,
    [property: JsonPropertyName("val")] int Val,
    [property: JsonPropertyName("idx")] int Idx,
    [property: JsonPropertyName("len")] int Len,
    [property: JsonPropertyName("hz")] int Hz);

public sealed record LinkPollGrant(
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("hz")] int Hz);

public sealed record LinkAuthClient(
    [property: JsonPropertyName("cid")] int Cid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] LinkRole Role,
    [property: JsonPropertyName("created")] DateTimeOffset? Created = null,
    [property: JsonPropertyName("last_seen")] DateTimeOffset? LastSeen = null,
    [property: JsonPropertyName("online")] bool? Online = null);

public sealed record LinkHubDeviceStats(
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("cmds")] int? Cmds = null,
    [property: JsonPropertyName("errors")] int? Errors = null,
    [property: JsonPropertyName("avg_rtt_ms")] double? AvgRttMs = null,
    [property: JsonPropertyName("notify_dropped")] int? NotifyDropped = null);

// ── Messages ──

/// <summary>
/// One control-plane message. <see cref="Decode(string)"/> picks the type from
/// "t"; <see cref="Encode"/> writes the body and stamps "t" into it.
/// </summary>
public abstract record LinkMessage
{
    /// <summary>The "t" value.</summary>
    [JsonIgnore] public string TypeName => TypeNameCore;

    /// <summary>The request id, for matching a reply; null for events.</summary>
    [JsonIgnore] public long? RequestId => RequestIdCore;

    // Protected, so the serializer never writes them into a message's body.
    protected abstract string TypeNameCore { get; }
    protected virtual long? RequestIdCore => null;

    private static readonly Dictionary<string, Type> Types = new()
    {
        ["auth.token"] = typeof(LinkAuthToken),
        ["auth.pair"] = typeof(LinkAuthPair),
        ["auth.list"] = typeof(LinkAuthList),
        ["auth.revoke"] = typeof(LinkAuthRevoke),
        ["auth.set_role"] = typeof(LinkAuthSetRole),
        ["device.list"] = typeof(LinkDeviceListRequest),
        ["device.added"] = typeof(LinkDeviceAdded),
        ["device.removed"] = typeof(LinkDeviceRemoved),
        ["device.changed"] = typeof(LinkDeviceChanged),
        ["device.rename"] = typeof(LinkDeviceRename),
        ["device.snapshot"] = typeof(LinkDeviceSnapshotRequest),
        ["poll.subscribe"] = typeof(LinkPollSubscribe),
        ["poll.unsubscribe"] = typeof(LinkPollUnsubscribe),
        ["poll.error"] = typeof(LinkPollError),
        ["lock.acquire"] = typeof(LinkLockAcquire),
        ["lock.release"] = typeof(LinkLockRelease),
        ["fw.install"] = typeof(LinkFwInstall),
        ["fw.progress"] = typeof(LinkFwProgress),
        ["fw.done"] = typeof(LinkFwDone),
        ["hub.stats"] = typeof(LinkHubStatsRequest),
        ["hub.rename"] = typeof(LinkHubRename),
        ["hub.config.get"] = typeof(LinkHubConfigGet),
        ["hub.config.set"] = typeof(LinkHubConfigSet),
        ["hub.config.changed"] = typeof(LinkHubConfigChanged),
        ["hub.wifi.scan"] = typeof(LinkHubWifiScan),
        ["hub.wifi.join"] = typeof(LinkHubWifiJoin),
        ["hub.auth.set"] = typeof(LinkHubAuthSet),
        ["hub.restart"] = typeof(LinkHubRestart),
        ["hub.reset"] = typeof(LinkHubReset),
        ["hub.update"] = typeof(LinkHubUpdate),
        ["hub.update.done"] = typeof(LinkHubUpdateDone),
        ["err"] = typeof(LinkErr),
    };

    // Every way a message can fail to decode comes out as a JsonException
    // (a LinkMessageException once "t" is known): a session loop catches one
    // type. Hostile JSON otherwise surfaces as ArgumentException (duplicate
    // keys), InvalidOperationException (lone surrogates, invalid UTF-8) and
    // others, some only when a kept JsonObject is read later.

    public static LinkMessage Decode(ReadOnlySpan<byte> utf8)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(utf8); }
        catch (Exception e) when (e is not JsonException) { throw new JsonException("Not valid JSON text.", e); }
        return Decode(node as JsonObject ?? throw new JsonException("A Link message is a JSON object."));
    }

    public static LinkMessage Decode(string text)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (Exception e) when (e is not JsonException) { throw new JsonException("Not valid JSON text.", e); }
        return Decode(node as JsonObject ?? throw new JsonException("A Link message is a JSON object."));
    }

    /// <summary>Decodes one message. Throws <see cref="JsonException"/> when it
    /// is not valid JSON or has no string "t", and
    /// <see cref="LinkMessageException"/> (a JsonException carrying the type
    /// and id) when a known type is missing a field or has a bad one, so a hub
    /// can still answer bad_request with the id.</summary>
    public static LinkMessage Decode(JsonObject obj)
    {
        Validate(obj, 0);
        if (obj["t"] is not JsonValue tv || tv.GetValueKind() != JsonValueKind.String)
            throw new JsonException("A Link message needs a string \"t\".");
        string t = tv.GetValue<string>();
        long? id = ReadId(obj["id"]);

        try
        {
            if (t == "hello")
            {
                // hub and auth appear only in the hub's half of the exchange.
                var helloType = obj.ContainsKey("hub") || obj.ContainsKey("auth") ? typeof(LinkHelloHub) : typeof(LinkHelloClient);
                if (obj["proto"] is not JsonObject) throw new JsonException("hello needs proto.");
                return (LinkMessage)Body(obj).Deserialize(helloType, LinkJson.Options)!;
            }
            if (t == "ok") return LinkOk.FromObject(obj);
            if (Types.TryGetValue(t, out var type))
                return (LinkMessage)(Body(obj).Deserialize(type, LinkJson.Options)
                                     ?? throw new JsonException($"Empty {t}."));
            return new LinkUnknownMessage(t, id);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException
                                      or ArgumentException or NotSupportedException or OverflowException)
        {
            throw new LinkMessageException(t, id, e);
        }
    }

    /// <summary>Walks the whole tree once: building each object's key table
    /// finds duplicate keys, and reading each string finds lone surrogates, so
    /// nothing kept (an ok body, a hub config) can throw later when read.</summary>
    internal static void ValidateTree(JsonNode? node) => Validate(node, 0);

    private static void Validate(JsonNode? node, int depth)
    {
        try
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (_, child) in o) Validate(child, depth + 1);
                    break;
                case JsonArray a:
                    foreach (var child in a) Validate(child, depth + 1);
                    break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    v.GetValue<string>();
                    break;
            }
        }
        catch (Exception e) when (e is not JsonException)
        {
            throw new JsonException("Malformed JSON (a duplicate key or an invalid string).", e);
        }
    }

    /// <summary>A request id: any integer JSON can carry exactly (|id| below
    /// 2^53, as JavaScript clients count); null when absent or not one.</summary>
    internal static long? ReadId(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out long id)
        && Math.Abs(id) <= MaxSafeId ? id : null;

    private const long MaxSafeId = (1L << 53) - 1;

    /// <summary>The object without "t", which no body declares.</summary>
    private static JsonObject Body(JsonObject obj)
    {
        var copy = (JsonObject)obj.DeepClone();
        copy.Remove("t");
        return copy;
    }

    /// <summary>The message as one JSON text frame.</summary>
    public string Encode() => ToJsonObject().ToJsonString(LinkJson.Options);

    public byte[] EncodeUtf8() => System.Text.Encoding.UTF8.GetBytes(Encode());

    public virtual JsonObject ToJsonObject()
    {
        var obj = JsonSerializer.SerializeToNode(this, GetType(), LinkJson.Options)?.AsObject() ?? new JsonObject();
        obj["t"] = TypeName;
        return obj;
    }
}

/// <summary>The client's hello. <see cref="Proto"/> is this build's version
/// unless given; a received hello must carry one (spec 7.2).</summary>
public sealed record LinkHelloClient(
    [property: JsonPropertyName("client")] LinkClientInfo Client,
    LinkProtoVersion? Proto = null) : LinkMessage
{
    protected override string TypeNameCore => "hello";
    [JsonPropertyName("proto")] public LinkProtoVersion Proto { get; init; } = Proto ?? new LinkProtoVersion();
    [JsonIgnore] public LinkProtoVersion Version => Proto;
}

/// <summary>The hub's hello. <see cref="Proto"/> is this build's version
/// unless given; a received hello must carry one (spec 7.2).</summary>
public sealed record LinkHelloHub(
    [property: JsonPropertyName("hub")] LinkHubInfo Hub,
    [property: JsonPropertyName("auth")] LinkAuthMode Auth,
    [property: JsonPropertyName("caps")] IReadOnlyList<string> Caps,
    LinkProtoVersion? Proto = null,
    [property: JsonPropertyName("limits")] LinkLimits? Limits = null,
    /// <summary>"window" or "fixed", present only when auth is pin (1.2).</summary>
    [property: JsonPropertyName("pin_source")] string? PinSourceWire = null) : LinkMessage
{
    protected override string TypeNameCore => "hello";
    [JsonPropertyName("proto")] public LinkProtoVersion Proto { get; init; } = Proto ?? new LinkProtoVersion();
    [JsonIgnore] public LinkProtoVersion Version => Proto;
    public bool Supports(string capability) => Caps.Contains(capability);
    /// <summary>Where the PIN comes from. Missing means a window (spec 7.2),
    /// and so does a value this build doesn't know.</summary>
    [JsonIgnore] public string PinSource => PinSourceWire == LinkPinSource.Fixed ? LinkPinSource.Fixed : LinkPinSource.Window;
}

public sealed record LinkAuthToken(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("token")] string Token) : LinkMessage
{
    protected override string TypeNameCore => "auth.token";
    protected override long? RequestIdCore => Id;

    // The token stays out of logs.
    public override string ToString() => $"LinkAuthToken {{ Id = {Id} }}";
}

public sealed record LinkAuthPair(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("pin")] string Pin,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] LinkRole? Role = null) : LinkMessage
{
    protected override string TypeNameCore => "auth.pair";
    protected override long? RequestIdCore => Id;

    public override string ToString() => $"LinkAuthPair {{ Id = {Id}, Name = {Name}, Role = {Role} }}";
}

public sealed record LinkAuthList([property: JsonPropertyName("id")] long Id) : LinkMessage
{
    protected override string TypeNameCore => "auth.list";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkAuthRevoke(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("cid")] int Cid) : LinkMessage
{
    protected override string TypeNameCore => "auth.revoke";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkAuthSetRole(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("cid")] int Cid,
    [property: JsonPropertyName("role")] LinkRole Role) : LinkMessage
{
    protected override string TypeNameCore => "auth.set_role";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkDeviceListRequest([property: JsonPropertyName("id")] long Id) : LinkMessage
{
    protected override string TypeNameCore => "device.list";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkDeviceAdded([property: JsonPropertyName("device")] LinkDeviceInfo Device) : LinkMessage
{
    protected override string TypeNameCore => "device.added";
}

public sealed record LinkDeviceChanged([property: JsonPropertyName("device")] LinkDeviceInfo Device) : LinkMessage
{
    protected override string TypeNameCore => "device.changed";
}

public sealed record LinkDeviceRemoved(
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("serial")] string? Serial = null) : LinkMessage
{
    protected override string TypeNameCore => "device.removed";
}

public sealed record LinkDeviceRename(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("name")] string Name) : LinkMessage
{
    protected override string TypeNameCore => "device.rename";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkDeviceSnapshotRequest(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("handle")] int Handle) : LinkMessage
{
    protected override string TypeNameCore => "device.snapshot";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkPollSubscribe(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("polls")] IReadOnlyList<LinkPollSpec> Polls) : LinkMessage
{
    protected override string TypeNameCore => "poll.subscribe";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkPollUnsubscribe(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("slots")] IReadOnlyList<int> Slots) : LinkMessage
{
    protected override string TypeNameCore => "poll.unsubscribe";
    protected override long? RequestIdCore => Id;
}

/// <summary>After three consecutive failures of one subscribed poll (spec
/// 8.4): handle and slot identify the subscription; the codes are err's.</summary>
public sealed record LinkPollError(
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("slot")] int? Slot = null,
    [property: JsonPropertyName("code")] string? Code = null,
    [property: JsonPropertyName("msg")] string? Msg = null) : LinkMessage
{
    protected override string TypeNameCore => "poll.error";
}

public sealed record LinkLockAcquire(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("timeout_ms")] int? TimeoutMs = null) : LinkMessage
{
    protected override string TypeNameCore => "lock.acquire";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkLockRelease(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("handle")] int Handle) : LinkMessage
{
    protected override string TypeNameCore => "lock.release";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkFwInstall(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("version")] string? Version = null) : LinkMessage
{
    protected override string TypeNameCore => "fw.install";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkFwProgress(
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("phase")] LinkFwPhase Phase,
    [property: JsonPropertyName("pct")] int? Pct = null) : LinkMessage
{
    protected override string TypeNameCore => "fw.progress";
}

public sealed record LinkFwDone(
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("fw")] string? Fw = null,
    [property: JsonPropertyName("fw_beta")] int? FwBeta = null) : LinkMessage
{
    protected override string TypeNameCore => "fw.done";
}

public sealed record LinkHubStatsRequest([property: JsonPropertyName("id")] long Id) : LinkMessage
{
    protected override string TypeNameCore => "hub.stats";
    protected override long? RequestIdCore => Id;
}

public sealed record LinkHubRename(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name) : LinkMessage
{
    protected override string TypeNameCore => "hub.rename";
    protected override long? RequestIdCore => Id;
}

// ── Hub administration (spec 7.10-7.14, 1.2; every message needs admin) ──

/// <summary>Reply: <see cref="LinkHubConfigBody"/>.</summary>
public sealed record LinkHubConfigGet([property: JsonPropertyName("id")] long Id) : LinkMessage
{
    protected override string TypeNameCore => "hub.config.get";
    protected override long? RequestIdCore => Id;
}

/// <summary>Only the keys being changed; nested objects merge. Configuration
/// keys are defined by the hub kind, so they stay a JSON object. Reply:
/// <see cref="LinkHubConfigBody"/> with the whole configuration after the
/// change, or err bad_request naming the offending key in field.</summary>
public sealed record LinkHubConfigSet(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("config")] JsonObject Config) : LinkMessage
{
    protected override string TypeNameCore => "hub.config.set";
    protected override long? RequestIdCore => Id;
}

/// <summary>To every admin session after a successful change: the whole configuration.</summary>
public sealed record LinkHubConfigChanged([property: JsonPropertyName("config")] JsonObject Config) : LinkMessage
{
    protected override string TypeNameCore => "hub.config.changed";
}

/// <summary>Reply within 10 s: <see cref="LinkWifiScanBody"/>.</summary>
public sealed record LinkHubWifiScan([property: JsonPropertyName("id")] long Id) : LinkMessage
{
    protected override string TypeNameCore => "hub.wifi.scan";
    protected override long? RequestIdCore => Id;
}

/// <summary>ok means accepted, not joined: the hub then closes every session
/// with 4003 and tries the network for up to 30 s, falling back to the old one.
/// <see cref="Password"/> is empty for an open network.</summary>
public sealed record LinkHubWifiJoin(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("ssid")] string Ssid,
    [property: JsonPropertyName("password")] string Password) : LinkMessage
{
    protected override string TypeNameCore => "hub.wifi.join";
    protected override long? RequestIdCore => Id;

    // The passphrase stays out of logs.
    public override string ToString() => $"LinkHubWifiJoin {{ Id = {Id}, Ssid = {Ssid} }}";
}

/// <summary>Sets the auth mode: "none", or "pin" with a six-digit fixed PIN
/// (or no PIN, for window PINs). Revokes every token. Reply:
/// <see cref="LinkHubAuthSetBody"/>, whose token keeps this session paired.</summary>
public sealed record LinkHubAuthSet(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("mode")] LinkAuthMode Mode,
    [property: JsonPropertyName("pin")] string? Pin = null) : LinkMessage
{
    protected override string TypeNameCore => "hub.auth.set";
    protected override long? RequestIdCore => Id;

    public override string ToString() => $"LinkHubAuthSet {{ Id = {Id}, Mode = {Mode}, Pin = {(Pin == null ? "none" : "set")} }}";
}

/// <summary>After ok the hub closes every session with 4003 and restarts.</summary>
public sealed record LinkHubRestart([property: JsonPropertyName("id")] long Id) : LinkMessage
{
    protected override string TypeNameCore => "hub.restart";
    protected override long? RequestIdCore => Id;
}

/// <summary>Erases state at <see cref="Level"/> (<see cref="LinkResetLevel"/>),
/// then restarts like hub.restart.</summary>
public sealed record LinkHubReset(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("level")] string Level) : LinkMessage
{
    protected override string TypeNameCore => "hub.reset";
    protected override long? RequestIdCore => Id;
}

/// <summary>Updates the hub's own firmware. Reply: <see cref="LinkFwInstallBody"/>
/// (the xfer id for the FWDATA frames).</summary>
public sealed record LinkHubUpdate(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("version")] string? Version = null) : LinkMessage
{
    protected override string TypeNameCore => "hub.update";
    protected override long? RequestIdCore => Id;
}

/// <summary>To the requesting session. On success the hub then closes every
/// session with 4003 and restarts; on failure <see cref="Msg"/> says why.</summary>
public sealed record LinkHubUpdateDone(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("msg")] string? Msg = null) : LinkMessage
{
    protected override string TypeNameCore => "hub.update.done";
}

public sealed record LinkErr(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("id")] long? Id = null,
    [property: JsonPropertyName("msg")] string? Msg = null,
    /// <summary>With bad_request: the offending key, dotted (spec 1.2).</summary>
    [property: JsonPropertyName("field")] string? Field = null) : LinkMessage
{
    protected override string TypeNameCore => "err";
    protected override long? RequestIdCore => Id;
}

/// <summary>A type this build does not know. Clients drop unknown events;
/// hubs answer unknown requests with err unknown_type.</summary>
public sealed record LinkUnknownMessage(string Type, long? Id) : LinkMessage
{
    protected override string TypeNameCore => Type;
    protected override long? RequestIdCore => Id;

    public override JsonObject ToJsonObject()
    {
        var obj = new JsonObject();
        if (Id is { } id) obj["id"] = id;
        obj["t"] = Type;
        return obj;
    }
}

/// <summary>
/// An ok reply. Its fields depend on the request, and a client that lost
/// track of which request an id belonged to must still decode the frame, so
/// the body is kept as an object and typed views are read with
/// <see cref="DecodeBody{T}"/>.
/// </summary>
public sealed record LinkOk : LinkMessage
{
    public LinkOk(long? id, JsonObject? body = null)
    {
        Id = id;
        Body = body ?? new JsonObject();
    }

    public long? Id { get; init; }
    /// <summary>Every field but "t" and "id".</summary>
    public JsonObject Body { get; init; }

    protected override string TypeNameCore => "ok";
    protected override long? RequestIdCore => Id;

    /// <summary>An ok carrying a typed body.</summary>
    public static LinkOk WithBody<T>(long? id, T body) =>
        new(id, JsonSerializer.SerializeToNode(body, LinkJson.Options)?.AsObject());

    /// <summary>The body as <typeparamref name="T"/>. Throws only JsonException.</summary>
    public T DecodeBody<T>()
    {
        try
        {
            return Body.Deserialize<T>(LinkJson.Options) ?? throw new JsonException($"Empty {typeof(T).Name}.");
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException
                                      or NotSupportedException or OverflowException)
        {
            throw new JsonException($"Malformed {typeof(T).Name}: {e.Message}", e);
        }
    }

    // Field names only: an auth reply's body holds the token.
    public override string ToString() => $"LinkOk {{ Id = {Id}, Fields = [{string.Join(", ", Body.Select(p => p.Key))}] }}";

    internal static LinkOk FromObject(JsonObject obj)
    {
        var body = (JsonObject)obj.DeepClone();
        body.Remove("t");
        // Read the same way as any message's id, so ok and err agree.
        var id = ReadId(body["id"]);
        body.Remove("id");
        return new LinkOk(id, body);
    }

    public override JsonObject ToJsonObject()
    {
        var obj = (JsonObject)Body.DeepClone();
        if (Id is { } id) obj["id"] = id;
        obj["t"] = "ok";
        return obj;
    }
}

// ── Typed ok bodies ──

/// <summary>Reply to auth.token and auth.pair; token is present only for pair.</summary>
public sealed record LinkAuthOkBody(
    [property: JsonPropertyName("session")] int Session,
    [property: JsonPropertyName("role")] LinkRole Role,
    [property: JsonPropertyName("token")] string? Token = null,
    [property: JsonPropertyName("policy")] LinkPolicyDescriptor? Policy = null)
{
    public override string ToString() => $"LinkAuthOkBody {{ Session = {Session}, Role = {Role}, Token = {(Token == null ? "none" : "set")} }}";
}

public sealed record LinkDeviceListBody([property: JsonPropertyName("devices")] IReadOnlyList<LinkDeviceInfo> Devices);

public sealed record LinkAuthListBody([property: JsonPropertyName("clients")] IReadOnlyList<LinkAuthClient> Clients);

public sealed record LinkSnapshotBody(
    [property: JsonPropertyName("handle")] int Handle,
    [property: JsonPropertyName("wire_version")] int? WireVersion = null,
    [property: JsonPropertyName("age_ms")] int? AgeMs = null,
    [property: JsonPropertyName("bulk_b64")] string? BulkB64 = null,
    [property: JsonPropertyName("status_b64")] string? StatusB64 = null)
{
    /// <summary>The bulk blob; null when absent. Throws JsonException when the
    /// field is there but isn't standard padded base64 (spec 7.1), which is
    /// not the same as absent.</summary>
    [JsonIgnore] public byte[]? Bulk => FromBase64(BulkB64, "bulk_b64");
    [JsonIgnore] public byte[]? Status => FromBase64(StatusB64, "status_b64");

    private static byte[]? FromBase64(string? text, string field)
    {
        if (text == null) return null;
        try { return Convert.FromBase64String(text); }
        catch (FormatException e) { throw new JsonException($"{field} isn't base64.", e); }
    }
}

public sealed record LinkPollSubscribeBody([property: JsonPropertyName("granted")] IReadOnlyList<LinkPollGrant> Granted);

/// <summary>Reply to fw.install: the transfer id to put in every FWDATA tag.</summary>
public sealed record LinkFwInstallBody([property: JsonPropertyName("xfer")] int Xfer);

/// <summary>Reply to lock.acquire: the timeout the hub granted (capped at 60000).</summary>
public sealed record LinkLockOkBody([property: JsonPropertyName("timeout_ms")] int? TimeoutMs = null);

/// <summary>Reply to hub.config.get and hub.config.set. <see cref="Options"/>
/// mirrors the config's shape, holding the permitted values of keys that
/// come from a fixed set.</summary>
public sealed record LinkHubConfigBody(
    [property: JsonPropertyName("config")] JsonObject Config,
    [property: JsonPropertyName("options")] JsonObject? Options = null)
{
    /// <summary>The hub's name, the one key every hub kind defines.</summary>
    [JsonIgnore] public string? Name => Config["name"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

public sealed record LinkWifiCurrent(
    [property: JsonPropertyName("ssid")] string? Ssid = null,
    [property: JsonPropertyName("rssi")] int? Rssi = null,
    [property: JsonPropertyName("ip")] string? Ip = null);

public sealed record LinkWifiNetwork(
    [property: JsonPropertyName("ssid")] string Ssid,
    [property: JsonPropertyName("rssi")] int? Rssi = null,
    [property: JsonPropertyName("secure")] bool? Secure = null);

/// <summary>Reply to hub.wifi.scan; networks strongest first, each SSID once.</summary>
public sealed record LinkWifiScanBody(
    [property: JsonPropertyName("networks")] IReadOnlyList<LinkWifiNetwork> Networks,
    [property: JsonPropertyName("current")] LinkWifiCurrent? Current = null);

/// <summary>Reply to hub.auth.set: a fresh token when the mode became pin.</summary>
public sealed record LinkHubAuthSetBody([property: JsonPropertyName("token")] string? Token = null)
{
    public override string ToString() => $"LinkHubAuthSetBody {{ Token = {(Token == null ? "none" : "set")} }}";
}

public sealed record LinkHubStatsBody(
    [property: JsonPropertyName("sessions")] int? Sessions = null,
    [property: JsonPropertyName("uptime_s")] int? UptimeS = null,
    [property: JsonPropertyName("devices")] IReadOnlyList<LinkHubDeviceStats>? Devices = null);

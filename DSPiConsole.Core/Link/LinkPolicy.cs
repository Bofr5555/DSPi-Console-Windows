using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPiConsole.Core.Link;

/// <summary>What a session is allowed to do (spec 6.3), least to most
/// privileged. Roles are cumulative. The wire spellings are lower case.</summary>
[JsonConverter(typeof(LinkStrictEnumConverter<LinkRole>))]
public enum LinkRole
{
    /// <summary>Read-direction commands, poll subscriptions, notifications.</summary>
    [JsonStringEnumMemberName("viewer")] Viewer,
    /// <summary>A viewer's rights plus control SETs (EQ, routing, volumes,
    /// presets, DSP features).</summary>
    [JsonStringEnumMemberName("control")] Control,
    /// <summary>Everything, including config (pins, input sources, device-level
    /// flash config, firmware install, hub and client management).</summary>
    [JsonStringEnumMemberName("admin")] Admin,
}

/// <summary>How much authority a command needs (spec 6.3).</summary>
[JsonConverter(typeof(LinkStrictEnumConverter<LinkCommandClass>))]
public enum LinkCommandClass
{
    /// <summary>Returns state and mutates nothing. A write-as-read command
    /// (a GET transfer that changes the device) is never read.</summary>
    [JsonStringEnumMemberName("read")] Read,
    /// <summary>Changes listening state or runs a transient tool.</summary>
    [JsonStringEnumMemberName("control")] Control,
    /// <summary>Changes board wiring, persistence mode or device-level flash
    /// config, or takes the device away. Admin only.</summary>
    [JsonStringEnumMemberName("config")] Config,
}

public static class LinkRoleExtensions
{
    /// <summary>A value outside the three roles permits nothing.</summary>
    public static bool Permits(this LinkRole role, LinkCommandClass commandClass) => role switch
    {
        LinkRole.Viewer => commandClass == LinkCommandClass.Read,
        LinkRole.Control => commandClass is LinkCommandClass.Read or LinkCommandClass.Control,
        LinkRole.Admin => commandClass is LinkCommandClass.Read or LinkCommandClass.Control or LinkCommandClass.Config,
        _ => false,
    };

    public static string WireName(this LinkRole role) => role switch
    {
        LinkRole.Viewer => "viewer",
        LinkRole.Control => "control",
        LinkRole.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a Link role."),
    };
}

/// <summary>One row of the command table.</summary>
public sealed record LinkCommandEntry
{
    [JsonPropertyName("code")] public required byte Code { get; init; }
    /// <summary>The firmware's REQ_ name without the prefix, for logs and UI.</summary>
    [JsonPropertyName("name")] public required string Name { get; init; }
    /// <summary>Directions the firmware accepts; anything else STALLs. Kept as
    /// the wire strings so an unknown direction is ignored, not a failure.</summary>
    [JsonPropertyName("dirs")] public required IReadOnlyList<string> Dirs { get; init; }
    /// <summary>The strictest class of the row, and the class of every
    /// direction <see cref="Classes"/> does not name.</summary>
    [JsonPropertyName("class")] public required LinkCommandClass Class { get; init; }
    /// <summary>Per-direction classes keyed "set" / "get" (spec 9.3, 1.1).</summary>
    [JsonPropertyName("classes")] public IReadOnlyDictionary<string, LinkCommandClass>? Classes { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }

    public bool Accepts(LinkDirection direction) => Dirs.Contains(LinkPolicy.WireName(direction));

    public LinkCommandClass ClassFor(LinkDirection direction) =>
        Classes != null && Classes.TryGetValue(LinkPolicy.WireName(direction), out var c) ? c : Class;
}

/// <summary>
/// The hub's command authorization table: every vendor bRequest with the
/// transfer directions the firmware accepts and the class that decides which
/// roles may issue it. The table is dspi-link's policy/commands.json, embedded
/// as a resource so it is never hand-transcribed (spec 9.3). Rule 7 of spec
/// section 10: an unlisted bRequest is config, admin only, so a new firmware
/// command stays locked down until somebody classifies it. Port of the macOS
/// Console's LinkPolicy.
/// </summary>
public sealed class LinkPolicy
{
    private const string ResourceName = "DSPiConsole.Core.Link.commands.json";

    private readonly Dictionary<byte, LinkCommandEntry> _byCode;

    /// <summary>Spec version the table was taken from.</summary>
    public string SpecVersion { get; }
    public string Generated { get; }
    public IReadOnlyList<LinkCommandEntry> Entries { get; }

    private sealed record Document(
        [property: JsonPropertyName("spec_version")] string SpecVersion,
        [property: JsonPropertyName("generated")] string Generated,
        [property: JsonPropertyName("commands")] IReadOnlyList<LinkCommandEntry> Commands);

    private LinkPolicy(string specVersion, string generated, IReadOnlyList<LinkCommandEntry> entries)
    {
        SpecVersion = specVersion;
        Generated = generated;
        Entries = entries;
        _byCode = new Dictionary<byte, LinkCommandEntry>();
        foreach (var e in entries) _byCode.TryAdd(e.Code, e);
    }

    /// <summary>Parses a table. Throws <see cref="JsonException"/> when it is
    /// malformed, a field is missing or null, or a code appears twice (which
    /// would make classification and the denied list disagree).</summary>
    public static LinkPolicy Parse(ReadOnlySpan<byte> json)
    {
        var doc = JsonSerializer.Deserialize<Document>(json, LinkJson.Options)
                  ?? throw new JsonException("Empty command table.");
        var seen = new HashSet<byte>();
        foreach (var entry in doc.Commands)
            if (!seen.Add(entry.Code)) throw new JsonException($"The command table lists 0x{entry.Code:X2} twice.");
        return new LinkPolicy(doc.SpecVersion, doc.Generated, doc.Commands);
    }

    /// <summary>An empty table: every command is config, admin only. The
    /// fallback when the embedded table is missing, so a hub never permits all.</summary>
    public static LinkPolicy Empty { get; } = new("0", "", Array.Empty<LinkCommandEntry>());

    private static readonly Lazy<LinkPolicy?> EmbeddedTable = new(LoadEmbedded);

    /// <summary>The table built into this assembly, or null when it is missing
    /// or malformed. A hub that has no table must refuse to forward anything.</summary>
    public static LinkPolicy? Embedded => EmbeddedTable.Value;

    private static LinkPolicy? LoadEmbedded()
    {
        try
        {
            using var stream = typeof(LinkPolicy).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Parse(buffer.ToArray());
        }
        catch (Exception)
        {
            // Whatever is wrong with it, a hub without a table forwards nothing.
            return null;
        }
    }

    public LinkCommandEntry? Entry(byte code) => _byCode.GetValueOrDefault(code);

    /// <summary>Fails safe: an unlisted code, or a direction the firmware does
    /// not accept for a listed code, is config and so admin only.</summary>
    public LinkCommandClass Classify(byte code, LinkDirection direction) =>
        _byCode.TryGetValue(code, out var entry) && entry.Accepts(direction)
            ? entry.ClassFor(direction)
            : LinkCommandClass.Config;

    public bool IsAllowed(LinkRole role, byte code, LinkDirection direction) =>
        role.Permits(Classify(code, direction));

    /// <summary>Every (code, direction) in the table this role may not use, for
    /// the auth reply's policy.denied (spec 6.3, 7.3), so clients can grey out controls without
    /// replicating the classification. Unlisted codes are not listed: they are
    /// already denied by the fail-safe rule.</summary>
    public IReadOnlyList<(byte Code, LinkDirection Direction)> Denied(LinkRole role)
    {
        var denied = new List<(byte, LinkDirection)>();
        foreach (var entry in Entries.OrderBy(e => e.Code))
        {
            foreach (var dir in entry.Dirs)
            {
                if (ParseDirection(dir) is not { } d) continue;
                if (!role.Permits(entry.ClassFor(d))) denied.Add((entry.Code, d));
            }
        }
        return denied;
    }

    public static string WireName(LinkDirection direction) => direction == LinkDirection.Set ? "set" : "get";

    public static LinkDirection? ParseDirection(string wire) => wire switch
    {
        "set" => LinkDirection.Set,
        "get" => LinkDirection.Get,
        _ => null,
    };
}

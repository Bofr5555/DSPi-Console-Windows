using System.Globalization;

namespace DSPiConsole.Core.Models;

/// <summary>
/// Output latency (REQ_OUTPUT_LATENCY 0x03, firmware 1.1.6): how full the
/// output rings are kept, chosen from a fixed option table the device reports.
/// OUT sets a 1-byte option ID (an unknown ID is ignored); IN wValue 0 reads the
/// 12-byte status, wValue 1 the table as 8-byte entries. Device-global, never in
/// a preset, and kept across a factory reset. See the firmware's
/// current_architecture.md "Output Latency"; port of the macOS Console's
/// OutputLatencyStatus / OutputLatencyOption.
/// </summary>
public static class OutputLatency
{
    public const ushort GetStatus = 0;
    public const ushort GetOptions = 1;
    public const int StatusSize = 12;
    public const int OptionSize = 8;

    /// <summary>The option a factory-fresh device runs (4 ms). IDs are stable;
    /// 0 is never valid.</summary>
    public const byte DefaultId = 5;

    /// <summary>The option needs a sample-stream input (S/PDIF, I2S, ADAT); on
    /// USB it runs as the lowest standard option instead.</summary>
    public const byte FlagStreamOnly = 0x01;

    /// <summary>Set in a table entry when the option runs as itself on the
    /// active source.</summary>
    public const byte FlagAvailable = 0x80;

    /// <summary>Labels for the Control Surfaces OUTPUT_LATENCY enum (value =
    /// option ID − 1) when the device's table has not been read.</summary>
    public static readonly string[] FallbackLabels = { "0.67 ms", "1 ms", "2 ms", "3 ms", "4 ms", "6 ms", "8 ms" };

    /// <summary>"0.67 ms", "4 ms", "3.66 ms": whole milliseconds without
    /// decimals, anything else to two places.</summary>
    public static string FormatMs(ushort us) =>
        us % 1000 == 0
            ? $"{us / 1000} ms"
            : (us / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " ms";
}

/// <summary>
/// The 12-byte latency status (REQ_OUTPUT_LATENCY IN, wValue 0):
/// <code>
///   0      requested_id   (stored option)
///   1      effective_id   (option in force; a stream-only request on USB differs)
///   2-3    effective_us   (ring fill target at the current rate, ceiling applied)
///   4-5    target_frames
///   6-7    batch_frames   (stream-input batch at the current rate)
///   8      pending        (1 until a SET has fully applied)
///   9      option_count
///   10-11  reserved
/// </code>
/// </summary>
public readonly record struct OutputLatencyStatus(
    byte RequestedId,
    byte EffectiveId,
    ushort EffectiveUs,
    ushort TargetFrames,
    ushort BatchFrames,
    bool Pending,
    byte OptionCount)
{
    /// <summary>Null for a reply shorter than the 12-byte status.</summary>
    public static OutputLatencyStatus? FromBytes(ReadOnlySpan<byte> d)
    {
        if (d.Length < OutputLatency.StatusSize) return null;
        return new OutputLatencyStatus(
            RequestedId: d[0],
            EffectiveId: d[1],
            EffectiveUs: (ushort)(d[2] | d[3] << 8),
            TargetFrames: (ushort)(d[4] | d[5] << 8),
            BatchFrames: (ushort)(d[6] | d[7] << 8),
            Pending: d[8] != 0,
            OptionCount: d[9]);
    }
}

/// <summary>
/// One 8-byte option table entry (REQ_OUTPUT_LATENCY IN, wValue 1):
/// <code>
///   0     id
///   1     flags         (FlagStreamOnly | FlagAvailable)
///   2-3   latency_us    (nominal ring fill target)
///   4-5   batch_us      (nominal stream-input batch)
///   6-7   effective_us  (what selecting it gives now, source and rate applied)
/// </code>
/// </summary>
public readonly record struct OutputLatencyOption(
    byte Id,
    byte Flags,
    ushort LatencyUs,
    ushort BatchUs,
    ushort EffectiveUs)
{
    public bool StreamOnly => (Flags & OutputLatency.FlagStreamOnly) != 0;

    /// <summary>False when the active source runs this option as a different one.</summary>
    public bool Available => (Flags & OutputLatency.FlagAvailable) != 0;

    /// <summary>The nominal target, e.g. "0.67 ms".</summary>
    public string Label => OutputLatency.FormatMs(LatencyUs);

    /// <summary>Parse a whole table reply; a trailing partial entry is dropped.</summary>
    public static IReadOnlyList<OutputLatencyOption> TableFromBytes(ReadOnlySpan<byte> d)
    {
        var list = new List<OutputLatencyOption>(d.Length / OutputLatency.OptionSize);
        for (int o = 0; o + OutputLatency.OptionSize <= d.Length; o += OutputLatency.OptionSize)
        {
            list.Add(new OutputLatencyOption(
                Id: d[o],
                Flags: d[o + 1],
                LatencyUs: (ushort)(d[o + 2] | d[o + 3] << 8),
                BatchUs: (ushort)(d[o + 4] | d[o + 5] << 8),
                EffectiveUs: (ushort)(d[o + 6] | d[o + 7] << 8)));
        }
        return list;
    }
}

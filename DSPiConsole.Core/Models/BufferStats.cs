namespace DSPiConsole.Core.Models;

/// <summary>
/// Per-slot output buffer statistics (8 bytes on wire). Since the firmware's
/// output rings, every percentage is measured against a fixed 768-frame scale
/// (16 buckets of 48 frames) kept for wire compatibility; see
/// <see cref="OutputRing"/> for re-expressing it against the ring itself.
/// </summary>
public struct SpdifBufferStats
{
    /// <summary>Free 48-frame buckets (0-16).</summary>
    public byte ConsumerFree { get; set; }

    /// <summary>Filled 48-frame buckets (0-16).</summary>
    public byte ConsumerPrepared { get; set; }

    /// <summary>Buckets being read out (0-1).</summary>
    public byte ConsumerPlaying { get; set; }

    /// <summary>Current fill in whole percent. Moves in 6.25 % steps.</summary>
    public byte ConsumerFillPct { get; set; }

    /// <summary>Lowest observed fill percentage since last reset.</summary>
    public byte ConsumerMinFillPct { get; set; }

    /// <summary>Highest observed fill percentage since last reset.</summary>
    public byte ConsumerMaxFillPct { get; set; }

    /// <summary>Sample-granular fill in 0.01 % of the same scale (bytes 6-7,
    /// u16 LE; firmware 1.1.6). May exceed 10000. Zero on firmware that
    /// predates it, where bytes 6-7 were padding.</summary>
    public ushort FillCentiPct { get; set; }

    /// <summary>Fill in percent to frame resolution. The whole-percent figure
    /// moves in 6.25 % steps, so a slot near a bucket boundary flickers a
    /// whole step; older firmware has only that.</summary>
    public double PreciseFillPct =>
        FillCentiPct == 0 && ConsumerFillPct != 0 ? ConsumerFillPct : FillCentiPct / 100.0;

    /// <summary>The same figures re-expressed against the 512-frame output ring,
    /// so 100 % is a full ring. Port of the macOS Console's rescaledToRing.</summary>
    public SpdifBufferStats RescaledToRing()
    {
        const double factor = (double)OutputRing.SlotFillScaleFrames / OutputRing.Frames;
        static byte Pct(byte v) => (byte)Math.Min(100, Math.Round(v * factor, MidpointRounding.AwayFromZero));
        var o = this;
        o.ConsumerFillPct = Pct(ConsumerFillPct);
        o.ConsumerMinFillPct = Pct(ConsumerMinFillPct);
        o.ConsumerMaxFillPct = Pct(ConsumerMaxFillPct);
        o.FillCentiPct = (ushort)Math.Min(10_000, Math.Round(FillCentiPct * factor, MidpointRounding.AwayFromZero));
        return o;
    }

    public static SpdifBufferStats Parse(byte[] data, int offset)
    {
        return new SpdifBufferStats
        {
            ConsumerFree = data[offset],
            ConsumerPrepared = data[offset + 1],
            ConsumerPlaying = data[offset + 2],
            ConsumerFillPct = data[offset + 3],
            ConsumerMinFillPct = data[offset + 4],
            ConsumerMaxFillPct = data[offset + 5],
            FillCentiPct = (ushort)(data[offset + 6] | data[offset + 7] << 8),
        };
    }
}

/// <summary>Output ring geometry on firmware with the output latency setting
/// (1.1.6).</summary>
public static class OutputRing
{
    /// <summary>The scale REQ_GET_BUFFER_STATS percentages are measured against.</summary>
    public const int SlotFillScaleFrames = 768;
    /// <summary>Output slot ring size.</summary>
    public const int Frames = 512;
    /// <summary>Frames ahead of each reader the firmware keeps stamped with
    /// silence; a ring filled past <c>Frames - EraseAhead</c> cannot take a write.</summary>
    public const int EraseAhead = 64;
}

/// <summary>A buffer fill's health, coloured red / yellow / green by the
/// statistics window.</summary>
public enum FillHealth { Good, Warn, Bad }

/// <summary>The statistics window's fill bands. Port of the macOS Console's
/// BufferFillThresholds.</summary>
public static class BufferFillThresholds
{
    /// <summary>Bands around the live latency target, on the 512-frame ring
    /// scale. A healthy ring swings from about one delivery below the target
    /// to one above it, so green spans a quarter of the target up to two
    /// deliveries over; red is an empty ring or one too full to take a write.</summary>
    public static FillHealth RingTarget(double pct, int targetFrames, int batchFrames)
    {
        double frames = pct / 100 * OutputRing.Frames;
        if (frames < 1 || frames >= OutputRing.Frames - OutputRing.EraseAhead) return FillHealth.Bad;
        if (frames < targetFrames / 4.0 || frames > targetFrames + 2 * batchFrames) return FillHealth.Warn;
        return FillHealth.Good;
    }

    /// <summary>Fixed bands for firmware without the latency setting, whose
    /// single fill target sat at 50 % of the reported scale.</summary>
    public static FillHealth Spdif(double pct)
    {
        int v = (int)Math.Floor(pct);
        if (v is 0 or 100) return FillHealth.Bad;
        if (v < 25 || v > 75) return FillHealth.Warn;
        return FillHealth.Good;
    }

    public static FillHealth PdmDma(double pct)
    {
        int v = (int)Math.Floor(pct);
        return v > 50 ? FillHealth.Bad : v < 5 || v > 30 ? FillHealth.Warn : FillHealth.Good;
    }

    public static FillHealth PdmRing(double pct)
    {
        int v = (int)Math.Floor(pct);
        return v > 50 ? FillHealth.Bad : v > 20 ? FillHealth.Warn : FillHealth.Good;
    }
}

/// <summary>
/// The output rings' counters and watermarks (REQ_GET_STATUS 26-33, firmware
/// 1.1.6), with the latency target they are judged against. Underrun episodes,
/// refused blocks and over-budget steps are cumulative; the lowest fill and the
/// two gaps are watermarks that Reset Watermarks clears. Older firmware answers
/// these indices with 0 rather than a STALL, so the output latency status read
/// is the feature gate. Port of the macOS Console's RingHealth, plus indices
/// 32 and 33, which the macOS Console does not read yet.
/// </summary>
/// <param name="BatchFrames">Frames a stream input delivers per call; the fill swings by about this.</param>
/// <param name="LowestPrewriteFill">Lowest fill seen just before a write, in frames; null before the first write.</param>
/// <param name="SpdifInputRateMilliHz">Index 32: the S/PDIF input's measured rate in mHz; 0 when none is locked.</param>
/// <param name="VcxoTrimCentiPpm">Index 33: the soft-VCXO trim in ppm × 100 (signed); 0 when not trimming.</param>
public sealed record RingHealth(
    ushort TargetFrames,
    ushort TargetUs,
    ushort BatchFrames,
    uint UnderrunEpisodes,
    uint RefusedBlocks,
    uint? LowestPrewriteFill,
    uint WorstInputGapUs,
    uint WorstUsbPacketGapUs,
    uint OverBudgetSteps,
    uint SpdifInputRateMilliHz,
    int VcxoTrimCentiPpm)
{
    /// <summary>Index 28's "no write yet" value.</summary>
    public const uint NoFill = uint.MaxValue;

    /// <summary>Microseconds below 10 ms, milliseconds above.</summary>
    public static string FormatGapUs(uint us) =>
        us < 10_000 ? $"{us} µs" : (us / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
}

/// <summary>
/// PDM buffer statistics (8 bytes on wire).
/// </summary>
public struct PdmBufferStats
{
    public byte DmaFillPct { get; set; }
    public byte DmaMinFillPct { get; set; }
    public byte DmaMaxFillPct { get; set; }
    public byte RingFillPct { get; set; }
    public byte RingMinFillPct { get; set; }
    public byte RingMaxFillPct { get; set; }

    public static PdmBufferStats Parse(byte[] data, int offset)
    {
        return new PdmBufferStats
        {
            DmaFillPct = data[offset],
            DmaMinFillPct = data[offset + 1],
            DmaMaxFillPct = data[offset + 2],
            RingFillPct = data[offset + 3],
            RingMinFillPct = data[offset + 4],
            RingMaxFillPct = data[offset + 5],
        };
    }
}

/// <summary>
/// Complete 44-byte buffer statistics packet from REQ_GET_BUFFER_STATS (0xB0).
/// </summary>
public class BufferStatsPacket
{
    public const int PacketSize = 44;

    /// <summary>Number of active SPDIF instances (2 on RP2040, 4 on RP2350).</summary>
    public byte NumSpdif { get; set; }

    /// <summary>Status flags: bit 0 = PDM active, bit 1 = audio streaming.</summary>
    public byte Flags { get; set; }

    /// <summary>Monotonic sequence counter for missed-poll detection.</summary>
    public ushort Sequence { get; set; }

    /// <summary>Per-instance SPDIF stats (always 4 slots; only NumSpdif are valid).</summary>
    public SpdifBufferStats[] Spdif { get; set; } = new SpdifBufferStats[4];

    /// <summary>PDM buffer statistics.</summary>
    public PdmBufferStats Pdm { get; set; }

    public bool IsPdmActive => (Flags & 0x01) != 0;
    public bool IsAudioStreaming => (Flags & 0x02) != 0;

    public static BufferStatsPacket? Parse(byte[] data)
    {
        if (data == null || data.Length < PacketSize)
            return null;

        var packet = new BufferStatsPacket
        {
            NumSpdif = data[0],
            Flags = data[1],
            Sequence = BitConverter.ToUInt16(data, 2),
        };

        // 4 SPDIF instances at offset 4, 8 bytes each
        for (int i = 0; i < 4; i++)
            packet.Spdif[i] = SpdifBufferStats.Parse(data, 4 + i * 8);

        // PDM at offset 36
        packet.Pdm = PdmBufferStats.Parse(data, 36);

        return packet;
    }
}

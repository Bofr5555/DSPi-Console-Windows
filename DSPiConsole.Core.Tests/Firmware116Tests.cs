using System.Reflection;
using DSPiConsole.Core;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.Core.Tests;

/// <summary>
/// Firmware 1.1.6's host-visible additions: output latency (0x03), input source
/// names (0x54/0x55), the board name (0x56/0x57), notify events 0x0D/0x0F/0x10,
/// control-surface caps v21/v22, the buffer stats' fine fill and the output ring
/// health. Wire bytes marked "captured" were read from an RP2350 on v1.1.6.
/// </summary>
public class Firmware116Tests
{
    private static byte[] Hex(string s) => s.Split('-').Select(h => Convert.ToByte(h, 16)).ToArray();

    // ── Output latency ────────────────────────────────────────────────────

    // Captured: requested and effective 6 (6 ms), 6009 µs, 265 frames, batch 45, 7 options.
    private static readonly byte[] LatencyStatus = Hex("06-06-79-17-09-01-2D-00-00-07-00-00");

    // Captured on USB input: the two stream-only options carry no "available" flag.
    private static readonly byte[] LatencyTable = Hex(
        "01-01-9B-02-4D-01-CB-07-02-01-E8-03-4D-01-CB-07-03-80-D0-07-E8-03-CB-07-04-80-B8-0B-E8-03-B1-0B-" +
        "05-80-A0-0F-E8-03-97-0F-06-80-70-17-E8-03-79-17-07-80-40-1F-E8-03-45-1F");

    [Fact]
    public void LatencyStatusDecodes()
    {
        var s = OutputLatencyStatus.FromBytes(LatencyStatus);
        Assert.Equal(new OutputLatencyStatus(6, 6, 6009, 265, 45, false, 7), s);
        Assert.Null(OutputLatencyStatus.FromBytes(LatencyStatus.AsSpan(0, 11)));
    }

    [Fact]
    public void LatencyTableDecodesWithFlags()
    {
        var t = OutputLatencyOption.TableFromBytes(LatencyTable);
        Assert.Equal(7, t.Count);
        Assert.Equal(new OutputLatencyOption(1, 0x01, 667, 333, 1995), t[0]);
        Assert.True(t[0].StreamOnly);
        Assert.False(t[0].Available);
        Assert.True(t[4].Available);
        Assert.Equal(OutputLatency.DefaultId, t[4].Id);
        Assert.Equal("0.67 ms", t[0].Label);
        Assert.Equal("4 ms", t[4].Label);
        // A trailing partial entry is dropped.
        Assert.Equal(6, OutputLatencyOption.TableFromBytes(LatencyTable.AsSpan(0, 55)).Count);
    }

    [Theory]
    [InlineData(667, "0.67 ms")]
    [InlineData(1000, "1 ms")]
    [InlineData(8000, "8 ms")]
    [InlineData(3660, "3.66 ms")]
    [InlineData(6009, "6.01 ms")]
    public void LatencyFormatsLikeTheMac(int us, string text) =>
        Assert.Equal(text, OutputLatency.FormatMs((ushort)us));

    // ── Names ─────────────────────────────────────────────────────────────

    [Fact]
    public void NamesKeepPrintableAsciiUpTo31()
    {
        Assert.Equal("Turntable", DeviceNameText.Sanitize("Turntable"));
        // A no-break space, a tab and a newline go; the plain space stays.
        Assert.Equal("TV Box", DeviceNameText.Sanitize("TV \t Box\n"));
        Assert.Equal("Caf", DeviceNameText.Sanitize("Café"));
        Assert.Equal(new string('a', 31), DeviceNameText.Sanitize(new string('a', 40)));
        Assert.Equal("", DeviceNameText.Sanitize(null));
    }

    [Fact]
    public void BoardNameClosesGapsAndTrims()
    {
        Assert.Equal("Living Room DSPi", DeviceNameText.BoardName("  Living ☃ Room   DSPi "));
        Assert.Equal(31, DeviceNameText.BoardName(new string('x', 50)).Length);
    }

    [Fact]
    public void NamesEncodeAndDecodeThroughTheField()
    {
        var payload = DeviceNameText.Encode("Weeb's DSPi");
        Assert.Equal(32, payload.Length);
        Assert.Equal((byte)'W', payload[0]);
        Assert.All(payload[11..], b => Assert.Equal(0, b));
        Assert.Equal("Weeb's DSPi", DeviceNameText.Decode(payload));
        Assert.Equal("", DeviceNameText.Decode(new byte[32]));
        Assert.All(DeviceNameText.Encode(""), b => Assert.Equal(0, b));
        // A 31-character name still leaves the terminating NUL.
        Assert.Equal(0, DeviceNameText.Encode(new string('z', 40))[31]);
    }

    // ── Notify events ─────────────────────────────────────────────────────

    // Packets reach the decoder the way the device's do: from the transport,
    // with the origin the transport attributed (USB: our session when host-sourced).
    private static (DspDevice Device, Transport.DspDeviceTransportTests.FakeTransport Link) Device()
    {
        var link = new Transport.DspDeviceTransportTests.FakeTransport();
        return (new DspDevice(link), link);
    }

    private static void Process(Transport.DspDeviceTransportTests.FakeTransport link, byte[] packet) =>
        link.Notify(packet, DSPiConsole.Core.Link.LinkNotifyAttribution.IsHostSourced(packet) ? link.Session : (ushort)0);

    private static byte[] NamePacket(byte evt, byte target, byte source, string name)
    {
        var p = new byte[40];
        p[0] = 2; p[1] = evt; p[3] = 9; p[4] = target; p[5] = source;
        DeviceNameText.Encode(name).CopyTo(p, 8);
        return p;
    }

    [Fact]
    public void NameEventsRaiseTypedNotifications()
    {
        var (d, link) = Device();
        using var _ = d;
        InputSourceNameNotification? src = null;
        DeviceNameNotification? board = null;
        d.InputSourceNameNotified += (_, n) => src = n;
        d.DeviceNameNotified += (_, n) => board = n;

        Process(link, NamePacket(0x0F, 4, 8, "PS3"));
        Assert.Equal(new InputSourceNameNotification(4, "PS3", ParamSource.Uart, 0), src);

        // A host-sourced rename is attributed to our own session.
        Process(link, NamePacket(0x10, 0, 1, "Weeb's DSPi"));
        Assert.Equal(new DeviceNameNotification("Weeb's DSPi", ParamSource.HostSet, 1), board);
        Assert.True(d.IsOwnWrite(board!.Value.Origin));

        // A short packet is ignored.
        src = null;
        Process(link, NamePacket(0x0F, 1, 1, "x")[..39]);
        Assert.Null(src);
    }

    [Fact]
    public void RtaConfigEventCarriesTheAppliedConfigAndSource()
    {
        var (d, link) = Device();
        using var _ = d;
        RtaConfigNotification? n = null;
        d.RtaConfigNotified += (_, e) => n = e;
        var p = new byte[17];
        p[0] = 2; p[1] = 0x0D;
        for (int i = 0; i < 12; i++) p[4 + i] = (byte)(i + 1);
        p[16] = 9;
        Process(link, p);
        Assert.NotNull(n);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (byte)i), n!.Value.Config);
        Assert.Equal(ParamSource.I2c, n.Value.Source);
        Assert.Equal(0, n.Value.Origin); // another host's choice, never ours
    }

    [Fact]
    public void InterruptMonitorDecodesTheNewEvents()
    {
        var src = NotifyDecoder.Decode(NamePacket(0x0F, 2, 1, "PiDVD"));
        Assert.Contains("v2.InputSourceName", src);
        Assert.Contains("source=2", src);
        Assert.Contains("\"PiDVD\"", src);
        Assert.Contains("HOST", src);

        var cleared = NotifyDecoder.Decode(NamePacket(0x10, 0, 8, ""));
        Assert.Contains("v2.DeviceName", cleared);
        Assert.Contains("(cleared)", cleared);
        Assert.Contains("UART", cleared);

        var rta = new byte[17];
        rta[0] = 2; rta[1] = 0x0D; rta[16] = 1;
        Assert.Contains("v2.RtaConfig", NotifyDecoder.Decode(rta));
    }

    // ── Control surfaces caps v21/v22 ─────────────────────────────────────

    [Fact]
    public void NewNounsAreNamedAndFiledUnderDevice()
    {
        Assert.Equal("Save Preset", CsNounInfo.Name(CsNoun.PresetSave));
        Assert.Equal("Target Latency", CsNounInfo.Name(CsNoun.OutputLatency));
        var device = CsNounInfo.CategoryOf((int)CsNoun.OutputLatency);
        Assert.Equal("Device", device?.Title);
        Assert.Same(device, CsNounInfo.CategoryOf((int)CsNoun.PresetSave));
        Assert.Equal(
            new[] { CsNoun.Preset, CsNoun.PresetReload, CsNoun.PresetSave, CsNoun.InputSource, CsNoun.LgSync, CsNoun.OutputLatency },
            device!.Nouns);
        // Value = option ID - 1; the fallback labels when no table is known.
        Assert.Equal("0.67 ms", CsNounInfo.EnumLabel((int)CsNoun.OutputLatency, 0));
        Assert.Equal("4 ms", CsNounInfo.EnumLabel((int)CsNoun.OutputLatency, 4));
    }

    // ── Buffer stats and ring health ──────────────────────────────────────

    // Captured while idle: four slots, each 16 free buckets, one playing, empty,
    // with the watermarks at their reset values (min 100, max 0).
    private static readonly byte[] IdleBufferStats = Hex(
        "04-00-00-00-10-00-01-00-64-00-00-00-10-00-01-00-64-00-00-00-10-00-01-00-64-00-00-00-10-00-01-00-64-00-00-00-" +
        "00-00-00-00-00-00-00-00");

    [Fact]
    public void IdleBufferStatsDecode()
    {
        var p = BufferStatsPacket.Parse(IdleBufferStats)!;
        Assert.Equal(4, p.NumSpdif);
        Assert.Equal(16, p.Spdif[0].ConsumerFree);
        Assert.Equal(1, p.Spdif[0].ConsumerPlaying);
        Assert.Equal(0, p.Spdif[0].ConsumerFillPct);
        Assert.Equal(100, p.Spdif[0].ConsumerMinFillPct);
        Assert.Equal((ushort)0, p.Spdif[0].FillCentiPct);
        Assert.Equal(0, p.Spdif[0].PreciseFillPct);
    }

    [Fact]
    public void BufferStatsCarryTheFineFillInBytesSixAndSeven()
    {
        var b = (byte[])IdleBufferStats.Clone();
        // Slot 1: 34 % whole, 3456 centi-percent (u16 LE 0x0D80) in its bytes 6-7.
        b[12 + 3] = 34;
        b[12 + 6] = 0x80;
        b[12 + 7] = 0x0D;
        var p = BufferStatsPacket.Parse(b)!;
        Assert.Equal((ushort)3456, p.Spdif[1].FillCentiPct);
        Assert.Equal(34.56, p.Spdif[1].PreciseFillPct, 6);
    }

    [Fact]
    public void PreciseFillFallsBackToTheWholePercentOnOldFirmware()
    {
        var s = new SpdifBufferStats { ConsumerFillPct = 50, FillCentiPct = 0 };
        Assert.Equal(50, s.PreciseFillPct);
        Assert.Equal(0, new SpdifBufferStats().PreciseFillPct);
        Assert.Equal(12.34, new SpdifBufferStats { ConsumerFillPct = 12, FillCentiPct = 1234 }.PreciseFillPct, 6);
    }

    [Fact]
    public void RescalingReexpressesTheFillAgainstTheRing()
    {
        // 768/512 = 1.5: a third of the scale is half the ring.
        var s = new SpdifBufferStats { ConsumerFillPct = 33, ConsumerMinFillPct = 20, ConsumerMaxFillPct = 80, FillCentiPct = 3333 }
            .RescaledToRing();
        Assert.Equal(50, s.ConsumerFillPct);
        Assert.Equal(30, s.ConsumerMinFillPct);
        Assert.Equal(100, s.ConsumerMaxFillPct); // 120 clamps
        Assert.Equal((ushort)5000, s.FillCentiPct); // 4999.5 rounds away from zero
    }

    [Fact]
    public void RingTargetBandsFollowTheLiveTarget()
    {
        const int target = 265, batch = 45; // the captured 6 ms setting
        double Pct(double frames) => frames / OutputRing.Frames * 100;
        Assert.Equal(FillHealth.Bad, BufferFillThresholds.RingTarget(Pct(0.5), target, batch));
        Assert.Equal(FillHealth.Bad, BufferFillThresholds.RingTarget(Pct(448), target, batch));
        Assert.Equal(FillHealth.Warn, BufferFillThresholds.RingTarget(Pct(60), target, batch));
        Assert.Equal(FillHealth.Warn, BufferFillThresholds.RingTarget(Pct(360), target, batch));
        Assert.Equal(FillHealth.Good, BufferFillThresholds.RingTarget(Pct(265), target, batch));
        Assert.Equal(FillHealth.Good, BufferFillThresholds.RingTarget(Pct(355), target, batch));
    }

    [Fact]
    public void GapsReadInMicrosecondsUnderTenMilliseconds()
    {
        Assert.Equal("723 µs", RingHealth.FormatGapUs(723));
        Assert.Equal("9999 µs", RingHealth.FormatGapUs(9999));
        Assert.Equal("10.0 ms", RingHealth.FormatGapUs(10_000));
        Assert.Equal("123.5 ms", RingHealth.FormatGapUs(123_456));
    }
}

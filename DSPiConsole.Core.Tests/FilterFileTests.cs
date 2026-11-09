using DSPiConsole.Core.FilterFiles;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>
/// File › Import / Export Filters: the band-line codec and whole documents,
/// both this app's and the macOS Console's. Port of the macOS Console's
/// FilterFileTests and FilterDocumentTests, against this app's spellings
/// ("Crossover N:" with family, shape and slope; " BYP").
/// </summary>
public class FilterFileTests
{
    private static readonly IReadOnlyList<Channel> Rp2350 = Channel.Outputs;

    private static readonly FilterType[] PeqTypes =
    {
        FilterType.Peaking, FilterType.LowShelf, FilterType.HighShelf, FilterType.LowPass, FilterType.HighPass,
        FilterType.Notch, FilterType.AllPass, FilterType.AllPass1, FilterType.LowShelf1, FilterType.HighShelf1,
        FilterType.LinkwitzTransform, FilterType.LowPass1, FilterType.HighPass1,
    };

    // ── Band lines ──

    /// <summary>Every PEQ type survives export then import with its editable
    /// fields intact. Values differ from every default, so a dropped field
    /// shows as a mismatch rather than an accidental pass.</summary>
    [Fact]
    public void EveryPeqTypeRoundTrips()
    {
        foreach (var type in PeqTypes)
        {
            var band = new FilterParams(type, 123f, 1.25f, -4.5f);
            if (type == FilterType.LinkwitzTransform) { band.Gain = 28f; band.Qp = 1.1f; }

            var line = FilterFileService.FormatFilter(1, band);
            Assert.True(FilterFileService.IsFilterLine(line), line);
            var parsed = FilterFileService.ParseFilterLine(line);
            Assert.NotNull(parsed);
            Assert.Equal(type, parsed!.Type);
            Assert.Equal(band.Frequency, parsed.Frequency, 2);
            if (type.HasGain() || type == FilterType.LinkwitzTransform) Assert.Equal(band.Gain, parsed.Gain, 2);
            if (type.HasQ() || type == FilterType.LinkwitzTransform) Assert.Equal(band.Q, parsed.Q, 3);
            if (type == FilterType.LinkwitzTransform) Assert.Equal(band.Qp, parsed.Qp, 3);
        }
    }

    [Fact]
    public void BypassRoundTrips()
    {
        var band = new FilterParams(FilterType.Peaking, 100, 1, 3) { Bypass = true };
        var line = FilterFileService.FormatFilter(2, band);
        Assert.Contains("BYP", line);
        Assert.True(FilterFileService.ParseFilterLine(line)!.Bypass);
        band.Bypass = false;
        Assert.False(FilterFileService.ParseFilterLine(FilterFileService.FormatFilter(2, band))!.Bypass);
        // The macOS Console's marker.
        Assert.True(FilterFileService.ParseFilterLine("Filter  2: ON  HP      Fc    30.0 Hz  Q  0.50  [Bypassed]")!.Bypass);
    }

    [Fact]
    public void EveryCrossoverRoundTrips()
    {
        foreach (var type in Enum.GetValues<FilterType>().Where(t => t.IsCrossover()))
        {
            var line = FilterFileService.FormatXoverFilter(1, new FilterParams(type, 2000, 0.707f, 0));
            Assert.True(FilterFileService.IsXoverLine(line), line);
            Assert.False(FilterFileService.IsFilterLine(line));
            var parsed = FilterFileService.ParseXoverLine(line);
            Assert.Equal(type, parsed?.Type);
            Assert.Equal(2000, parsed!.Frequency, 1);
        }
    }

    [Fact]
    public void FlatBandsFormatAsOffAndKeepTheirSlot()
    {
        Assert.Equal("Filter  5: OFF", FilterFileService.FormatFilter(5, new FilterParams(FilterType.Flat, 1000, 0.707f, 0)));
        Assert.Equal(FilterType.Flat, FilterFileService.ParseFilterLine("Filter  5: OFF")!.Type);
        Assert.Equal("Crossover  2: OFF", FilterFileService.FormatXoverFilter(2, new FilterParams(FilterType.Flat, 1000, 0.707f, 0)));
        Assert.Equal(FilterType.Flat, FilterFileService.ParseXoverLine("Crossover  2: OFF")!.Type);
    }

    [Fact]
    public void ReadsTheMacsCrossoverCodes()
    {
        Assert.Equal(FilterType.Lr4Lp, FilterFileService.ParseXoverLine("Xover   1: ON  LR4LP   Fc  2000.0 Hz")?.Type);
        Assert.True(FilterFileService.IsXoverLine("Xover   1: ON  LR4LP   Fc  2000.0 Hz"));
    }

    /// <summary>Family and slope together pick the type.</summary>
    [Fact]
    public void CrossoverFamiliesAndSlopes()
    {
        Assert.Equal(FilterType.Lr8Lp, FilterFileService.ParseXoverLine("Crossover  1: ON  LR     LP  Fc  2000.0 Hz  Slope  48 dB/oct")?.Type);
        Assert.Equal(FilterType.Bw3Hp, FilterFileService.ParseXoverLine("Crossover  2: ON  BW     HP  Fc   100.0 Hz  Slope  18 dB/oct")?.Type);
        Assert.Equal(FilterType.Bes2Lp, FilterFileService.ParseXoverLine("Crossover  3: ON  Bessel LP  Fc   500.0 Hz  Slope  12 dB/oct")?.Type);
        // An odd-order Linkwitz-Riley does not exist.
        Assert.Null(FilterFileService.ParseXoverLine("Crossover  1: ON  LR     HP  Fc 80.0 Hz  Slope  18 dB/oct"));
    }

    [Fact]
    public void ParsesRewAndAutoEqLines()
    {
        var rew = FilterFileService.ParseFilterLine("Filter  1: ON  PK       Fc    63.0 Hz  Gain  -5.0 dB  Q  4.00")!;
        Assert.Equal((FilterType.Peaking, 63f, -5f, 4f), (rew.Type, rew.Frequency, rew.Gain, rew.Q));
        var autoEq = FilterFileService.ParseFilterLine("Filter 3: ON PK Fc 105 Hz Gain -4.2 dB Q 0.71")!;
        Assert.Equal(105, autoEq.Frequency, 2);
        Assert.Equal(-4.2, autoEq.Gain, 2);
        Assert.Equal(FilterType.Notch, FilterFileService.ParseFilterLine("Filter 1: ON NT Fc 50 Hz Q 10")!.Type);
        Assert.Equal(FilterType.LowShelf, FilterFileService.ParseFilterLine("Filter 1: ON LSC Fc 105 Hz Gain 3 dB Q 0.7")!.Type);
    }

    [Fact]
    public void CommaDecimalsParse()
    {
        var p = FilterFileService.ParseFilterLine("Filter  1: ON  PK  Fc 1.234,5 Hz  Gain -4,2 dB  Q 0,707")!;
        Assert.Equal(1234.5, p.Frequency, 1);
        Assert.Equal(-4.2, p.Gain, 3);
        Assert.Equal(0.707, p.Q, 4);
    }

    [Fact]
    public void OnlyNumberedBandLinesAreBands()
    {
        Assert.False(FilterFileService.IsFilterLine("Filters: 5"));
        Assert.False(FilterFileService.IsFilterLine("Filter Settings file"));
        Assert.False(FilterFileService.IsFilterLine("# DSPi Console Filter Settings"));
        Assert.False(FilterFileService.IsFilterLine("Preamp -6.5 dB"));
        Assert.True(FilterFileService.IsFilterLine("Filter 12: OFF"));
        // An unknown shape is a band of no known type.
        Assert.Null(FilterFileService.ParseFilterLine("Filter  2: ON  None"));
    }

    [Fact]
    public void PreampSpellings()
    {
        Assert.Equal("Preamp -6.5 dB", FilterFileService.FormatPreamp(-6.5f));
        Assert.Equal("Preamp +0.0 dB", FilterFileService.FormatPreamp(0));
        Assert.True(FilterFileService.TryParsePreampLine("Preamp: -6.5 dB", out var a));
        Assert.Equal(-6.5, a, 2);
        Assert.True(FilterFileService.TryParsePreampLine("  preamp -3 dB  ", out var b));
        Assert.Equal(-3, b, 2);
        Assert.False(FilterFileService.TryParsePreampLine("[Input 0: USB L]", out _));
    }

    [Fact]
    public void FormatVersionStamp()
    {
        Assert.Equal(2, FilterFileService.ParseFormatVersion("# Format: 2"));
        Assert.Equal(17, FilterFileService.ParseFormatVersion("# format 17"));
        Assert.Null(FilterFileService.ParseFormatVersion("# Exported: 2026-07-26 02:15:00"));
        Assert.Null(FilterFileService.ParseFormatVersion("# DSPi Console Filter Settings"));
    }

    // ── Documents ──

    private static List<FilterParams> Flat(int n) =>
        Enumerable.Range(0, n).Select(_ => new FilterParams(FilterType.Flat, 1000, 0.707f, 0)).ToList();

    /// <summary>The exporter's own output survives its own parser: every input
    /// with its preamp, every output with its enabled state and crossovers,
    /// all keyed by index, so a renamed channel still comes back.</summary>
    [Fact]
    public void ExportRoundTripsThroughTheParser()
    {
        var peq = Flat(10);
        peq[0] = new FilterParams(FilterType.Peaking, 100, 1, 3);
        peq[1] = new FilterParams(FilterType.HighPass, 30, 0.5f, 0) { Bypass = true };
        var inputs = new[]
        {
            new FilterExportInput(0, "Left Ear", -6.5f, peq),
            new FilterExportInput(1, "Master R", 0, Flat(10)),
        };
        var xover = Flat(4);
        xover[0] = new FilterParams(FilterType.Lr4Lp, 2000, 0.707f, 0);
        var outputs = new[]
        {
            new FilterExportOutput(0, "Woofer [L]", false, Flat(10), xover),
            new FilterExportOutput(1, "SPDIF 1 R", true, Flat(10), null),
        };

        var text = FilterFileService.GenerateExportString(inputs, outputs, new DateTime(2026, 11, 9));
        Assert.StartsWith("# DSPi Console", text);
        Assert.Contains("# Format: 2", text);
        Assert.Contains("[Input 0: Left Ear]", text);
        Assert.Contains("[Output 0: Woofer  L (Disabled)]", text);

        var parsed = FilterFileService.ParseFile(text, Rp2350);
        Assert.Equal(FilterFileFormat.DSPiConsole, parsed.Format);
        Assert.Equal(2, parsed.FormatVersion);
        int masterL = (int)Channel.MasterLeft.Id, out0 = (int)Rp2350[0].Id, out1 = (int)Rp2350[1].Id;
        Assert.Equal(10, parsed.ChannelFilters![masterL].Count);
        Assert.Equal(FilterType.HighPass, parsed.ChannelFilters[masterL][1].Type);
        Assert.Equal(0.5, parsed.ChannelFilters[masterL][1].Q, 3);
        Assert.True(parsed.ChannelFilters[masterL][1].Bypass);
        Assert.Equal(-6.5, parsed.ChannelPreamps![masterL], 2);
        Assert.Equal(0, parsed.ChannelPreamps[(int)Channel.MasterRight.Id], 2);
        Assert.False(parsed.ChannelEnabled![out0]);
        Assert.True(parsed.ChannelEnabled[out1]);
        Assert.Equal(4, parsed.ChannelXoverFilters![out0].Count);
        Assert.Equal(FilterType.Lr4Lp, parsed.ChannelXoverFilters[out0][0].Type);
        Assert.False(parsed.ChannelXoverFilters.ContainsKey(out1));
    }

    /// <summary>A macOS Console version 2 file.</summary>
    [Fact]
    public void MacDocumentSectionsParse()
    {
        const string doc = """
            # DSPi Console Filter Settings
            # Exported: 2026-07-26 02:15:00
            # Format: 2

            [Input 0: USB L]
            Preamp -6.5 dB
            Filter  1: ON  PK      Fc   100.0 Hz  Gain  +3.0 dB  Q  1.00
            Filter  2: ON  HP      Fc    30.0 Hz  Q  0.50  [Bypassed]

            [Output 0: SPDIF 1 L (Disabled)]
            Filter  1: OFF
            Xover   1: ON  LR4LP   Fc  2000.0 Hz
            Xover   2: OFF
            """;
        var parsed = FilterFileService.ParseFile(doc, Rp2350);
        int input = (int)Channel.MasterLeft.Id, output = (int)Rp2350[0].Id;
        Assert.Equal(2, parsed.FormatVersion);
        Assert.Equal(-6.5, parsed.ChannelPreamps![input], 2);
        Assert.Equal(2, parsed.ChannelFilters![input].Count);
        Assert.True(parsed.ChannelFilters[input][1].Bypass);
        Assert.False(parsed.ChannelEnabled![output]);
        Assert.Single(parsed.ChannelFilters[output]);
        Assert.Equal(2, parsed.ChannelXoverFilters![output].Count);
        Assert.Equal(FilterType.Lr4Lp, parsed.ChannelXoverFilters[output][0].Type);
        Assert.Equal(FilterType.Flat, parsed.ChannelXoverFilters[output][1].Type);
    }

    /// <summary>Files from before the index-keyed headers still load, with
    /// no stamp read as version 1 and absent sections left absent.</summary>
    [Fact]
    public void LegacyMacDocumentStillImports()
    {
        const string doc = """
            # DSPi Console Filter Settings
            # Exported: 2025-01-01 00:00:00

            [USB L]
            Filter  1: ON  PK      Fc   100.0 Hz  Gain  +3.0 dB  Q  1.00

            [Sub]
            Filter  1: OFF
            """;
        var parsed = FilterFileService.ParseFile(doc, Rp2350);
        Assert.Equal(1, parsed.FormatVersion);
        Assert.Equal(FilterType.Peaking, Assert.Single(parsed.ChannelFilters![(int)Channel.MasterLeft.Id]).Type);
        Assert.Null(parsed.ChannelPreamps);
        Assert.Null(parsed.ChannelXoverFilters);
        Assert.Null(parsed.ChannelEnabled);
        Assert.True(parsed.ChannelFilters.ContainsKey((int)Channel.Pdm.Id));
    }

    /// <summary>An older Windows file, keyed by the built-in channel name.</summary>
    [Fact]
    public void OlderWindowsDocumentStillImports()
    {
        const string doc = """
            # DSPi Console Filter Settings

            [Master L]
            Filter  1: ON  PK      Fc   100.00 Hz  Gain  +3.000 dB  Q  1.000

            [SPDIF 2 R]
            Filter  1: OFF
            Crossover  1: ON  LR     HP  Fc    80.00 Hz  Slope  24 dB/oct
            """;
        var parsed = FilterFileService.ParseFile(doc, Rp2350);
        Assert.Single(parsed.ChannelFilters![(int)Channel.MasterLeft.Id]);
        Assert.Equal(FilterType.Lr4Hp, parsed.ChannelXoverFilters![(int)Channel.Spdif2R.Id][0].Type);
        Assert.Null(parsed.ChannelEnabled);
    }

    /// <summary>Bands under a header naming no channel here do not leak into
    /// the previous section, and an unknown shape keeps its slot.</summary>
    [Fact]
    public void UnknownHeadersAndShapesKeepBandsInPlace()
    {
        const string doc = """
            # DSPi Console Filter Settings

            [Input 0: USB L]
            Filter  1: ON  None
            Filter  2: ON  PK      Fc   100.0 Hz  Gain  +3.0 dB  Q  1.00
            Filters: 2

            [Something Else]
            Filter  1: ON  PK      Fc   200.0 Hz  Gain  +6.0 dB  Q  2.00
            """;
        var bands = FilterFileService.ParseFile(doc, Rp2350).ChannelFilters![(int)Channel.MasterLeft.Id];
        Assert.Equal(2, bands.Count);
        Assert.Equal(FilterType.Flat, bands[0].Type);
        Assert.Equal(100, bands[1].Frequency, 1);
    }

    /// <summary>Equalizer APO lets the band number be left out.</summary>
    [Fact]
    public void ApoLinesWithoutANumberImport()
    {
        const string doc = """
            Preamp: -3 dB
            Filter: ON PK Fc 100 Hz Gain -3 dB Q 1.41
            Filter: ON HSC Fc 8000 Hz Gain 2 dB Q 0.7
            """;
        var parsed = FilterFileService.ParseFile(doc, Rp2350);
        Assert.Equal(FilterFileFormat.REW, parsed.Format);
        Assert.Equal(2, parsed.SingleChannelFilters!.Count);
        Assert.Equal(-3f, parsed.SinglePreamp!.Value, 2);
    }

    /// <summary>A REW or AutoEQ file: one channel's bands and its preamp.</summary>
    [Fact]
    public void RewFileCarriesItsPreamp()
    {
        const string doc = """
            Preamp: -6.2 dB
            Filter 1: ON PK Fc 105 Hz Gain 3.1 dB Q 0.70
            Filter 2: ON LSC Fc 105 Hz Gain 2.0 dB Q 0.70
            Filter 3: OFF
            """;
        var parsed = FilterFileService.ParseFile(doc, Rp2350);
        Assert.Equal(FilterFileFormat.REW, parsed.Format);
        Assert.Equal(2, parsed.SingleChannelFilters!.Count);
        Assert.Equal(-6.2f, parsed.SinglePreamp!.Value, 2);
    }
}

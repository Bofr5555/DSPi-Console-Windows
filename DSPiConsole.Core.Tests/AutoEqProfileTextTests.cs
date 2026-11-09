using DSPiConsole.Core.AutoEq;

namespace DSPiConsole.Core.Tests;

/// <summary>Reading the AutoEQ results folder when the database is rebuilt
/// from GitHub.</summary>
public class AutoEqProfileTextTests
{
    [Fact]
    public void ParsesAParametricEqFile()
    {
        const string text = """
            Preamp: -6.2 dB
            Filter 1: ON LSC Fc 105 Hz Gain 5.5 dB Q 0.70
            Filter 2: ON PK Fc 2340 Hz Gain -3.1 dB Q 1.41
            Filter 3: ON HSC Fc 10000 Hz Gain 2.0 dB Q 0.70
            Filter 4: ON NO Fc 50 Hz Q 10
            Filter 5: OFF
            """;
        var parsed = AutoEqProfileText.ParseProfile(text)!.Value;
        Assert.Equal(-6.2, parsed.Preamp, 2);
        // The notch is not a type a profile carries, so it is left out.
        Assert.Equal(new[] { "lowShelf", "peaking", "highShelf" }, parsed.Filters.Select(f => f.Type));
        Assert.Equal(2340, parsed.Filters[1].Freq, 1);
        Assert.Equal(-3.1, parsed.Filters[1].Gain, 2);
        Assert.Equal(1.41, parsed.Filters[1].Q, 2);
    }

    [Fact]
    public void AFileWithNoBandsIsNoProfile()
    {
        Assert.Null(AutoEqProfileText.ParseProfile("Preamp: -6.2 dB\n"));
        Assert.Null(AutoEqProfileText.ParseProfile("<html>404</html>"));
    }

    [Theory]
    [InlineData("Sennheiser HD 600", "Sennheiser", "HD 600")]
    [InlineData("Bang & Olufsen Beoplay H95", "Bang & Olufsen", "Beoplay H95")]
    [InlineData("64 Audio U12t", "64 Audio", "U12t")]
    [InlineData("Akg K371", "AKG", "K371")]
    [InlineData("Unknownbrand Model X", "Unknownbrand", "Model X")]
    [InlineData("Solo", "Solo", "")]
    public void SplitsManufacturerAndModel(string folder, string manufacturer, string model) =>
        Assert.Equal((manufacturer, model), AutoEqProfileText.ParseHeadphoneName(folder));

    [Theory]
    [InlineData("in-ear", "in-ear")]
    [InlineData("IEM", "in-ear")]
    [InlineData("earbud", "earbud")]
    [InlineData("over-ear", "over-ear")]
    [InlineData("Harman over-ear 2018", "over-ear")]
    public void FormFactorFromTheTargetFolder(string target, string expected) =>
        Assert.Equal(expected, AutoEqProfileText.DetectFormFactor(target));
}

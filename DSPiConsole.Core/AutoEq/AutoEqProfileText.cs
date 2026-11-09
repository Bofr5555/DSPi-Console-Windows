using DSPiConsole.Core.FilterFiles;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.AutoEq;

/// <summary>
/// Reading the AutoEQ project's results folder when the database is rebuilt
/// from GitHub: a profile's ParametricEQ.txt, its headphone's folder name and
/// the target folder it sits in. Port of the macOS Console's AutoEQManager
/// parsing helpers.
/// </summary>
public static class AutoEqProfileText
{
    /// <summary>The sources read, in order, with the priority that decides
    /// which profile a headphone keeps when several measure it (lower wins).</summary>
    public static readonly (string Folder, string Name, int Priority)[] Sources =
    {
        ("oratory1990", "oratory1990", 1),
        ("crinacle", "crinacle", 2),
        ("Rtings", "rtings", 3),
        ("Innerfidelity", "innerfidelity", 3),
        ("Headphone.com Legacy", "headphone.com", 4),
    };

    /// <summary>A profile's preamp and bands, with the bands as the database
    /// spells their types. Null when it has no band this app can take.</summary>
    public static (double Preamp, List<(string Type, double Freq, double Q, double Gain)> Filters)? ParseProfile(string text)
    {
        var parsed = FilterFileService.ParseFile(text, Array.Empty<Channel>());
        if (parsed.Format != FilterFileFormat.REW || parsed.SingleChannelFilters == null) return null;
        var filters = new List<(string, double, double, double)>();
        foreach (var f in parsed.SingleChannelFilters)
        {
            if (DatabaseType(f.Type) is not { } type) continue;
            filters.Add((type, f.Frequency, f.Q, f.Gain));
        }
        return filters.Count == 0 ? null : (parsed.SinglePreamp ?? 0, filters);
    }

    /// <summary>The database's spelling of a band type, as the macOS Console
    /// writes it; null for a type AutoEQ profiles never carry.</summary>
    public static string? DatabaseType(FilterType type) => type switch
    {
        FilterType.Peaking => "peaking",
        FilterType.LowShelf => "lowShelf",
        FilterType.HighShelf => "highShelf",
        FilterType.LowPass => "lowPass",
        FilterType.HighPass => "highPass",
        _ => null,
    };

    /// <summary>"in-ear", "earbud" or "over-ear", from the target folder's name.</summary>
    public static string DetectFormFactor(string target)
    {
        var lower = target.ToLowerInvariant();
        if (lower.Contains("in-ear") || lower.Contains("in_ear") || lower.Contains("iem")) return "in-ear";
        if (lower.Contains("earbud")) return "earbud";
        return "over-ear";
    }

    private static readonly string[] KnownManufacturers =
    {
        "AKG", "Audio-Technica", "Audeze", "Bang & Olufsen", "Beats", "Beyerdynamic",
        "Bose", "Campfire Audio", "Dan Clark Audio", "Denon", "FiiO", "Final",
        "Focal", "Grado", "HarmonicDyne", "HIFIMAN", "JBL", "Koss", "Massdrop",
        "Meze", "Moondrop", "Philips", "Pioneer", "Sennheiser", "Shure", "Sony",
        "SteelSeries", "STAX", "Tin HiFi", "V-MODA", "ZMF", "64 Audio", "7Hz",
        "Anker", "Apple", "AFUL", "BLON", "CCA", "Dunu", "Empire Ears", "Etymotic",
        "FatFreq", "Hidizs", "HiBy", "iBasso", "JVC", "KZ", "Letshuoer", "Linsoul",
        "Noble Audio", "QKZ", "Samsung", "See Audio", "Simgot", "SoftEars",
        "Tangzu", "Thieaudio", "Tinhifi", "Tripowin", "TRN", "Truthear",
        "Unique Melody", "Westone", "Yanyin", "BGVP", "CCZ",
    };

    /// <summary>A headphone folder's name split into manufacturer and model: a
    /// known manufacturer whole (so "Bang &amp; Olufsen" or "64 Audio" stay
    /// together), otherwise the first word.</summary>
    public static (string Manufacturer, string Model) ParseHeadphoneName(string folder)
    {
        foreach (var mfr in KnownManufacturers)
        {
            if (folder.Equals(mfr, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(mfr + " ", StringComparison.OrdinalIgnoreCase))
            {
                var model = folder[mfr.Length..].Trim();
                return (mfr, model.Length == 0 ? folder : model);
            }
        }
        var parts = folder.Split(' ', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (folder, "");
    }
}

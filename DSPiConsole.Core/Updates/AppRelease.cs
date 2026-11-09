using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Updates;

/// <summary>A file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string Url);

/// <summary>A DSPi Console release, as GitHub's release list describes it.</summary>
public sealed record AppRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("draft")] bool Draft = false,
    [property: JsonPropertyName("assets")] IReadOnlyList<ReleaseAsset>? Assets = null)
{
    /// <summary>Tags are spelled "v1.1.7" or "v1.1.6-beta4", the same scheme
    /// as the app's own version, with "-hotfix" allowed after either. Null for
    /// any other tag: the version parser reads an unknown suffix as a final
    /// release, which would offer "-rc1", "-alpha2" or an unnumbered "-beta"
    /// to someone on finals only.</summary>
    [JsonIgnore]
    public FirmwareVersion? Version => TagPattern.IsMatch(TagName) ? FirmwareVersion.Parse(TagName) : null;

    private static readonly Regex TagPattern = new(@"^[vV]?\d+\.\d+\.\d+(-beta[1-9]\d*)?(-hotfix)?$", RegexOptions.CultureInvariant);

    /// <summary>The release's web page; null unless it is an https address.</summary>
    [JsonIgnore]
    public Uri? Page => Uri.TryCreate(HtmlUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    [JsonIgnore]
    public IReadOnlyList<ReleaseAsset> Files => Assets ?? Array.Empty<ReleaseAsset>();
}

/// <summary>
/// Which release to offer, and whether it can be installed in place. Port of
/// the macOS Console's AppUpdateChecker selection rules.
/// </summary>
public static class UpdateFeed
{
    public const string ReleasesUrl = "https://api.github.com/repos/WeebLabs/DSPi-Console-Windows/releases?per_page=30";

    public static IReadOnlyList<AppRelease> Parse(string json) =>
        JsonSerializer.Deserialize<List<AppRelease>>(json) ?? new List<AppRelease>();

    /// <summary>
    /// The newest release worth offering over <paramref name="current"/>.
    /// Someone on a final release is offered finals only; someone already on a
    /// beta is offered newer betas too. Judged from the tag, because Windows
    /// betas have been published as full releases marked Latest, so GitHub's
    /// prerelease flag says nothing. Drafts and tags naming no version are
    /// skipped.
    /// </summary>
    public static AppRelease? NewestUpdate(IEnumerable<AppRelease> releases, FirmwareVersion current)
    {
        AppRelease? best = null;
        FirmwareVersion bestVersion = default;
        foreach (var release in releases)
        {
            if (release.Draft || release.Version is not { } version) continue;
            // An early beta (no ordinal) names no tag; it is never offered.
            if (version.Beta == FirmwareVersion.EarlyBeta) continue;
            if (current.Beta == 0 && version.Beta != 0) continue;
            if (version <= current) continue;
            if (best == null || version > bestVersion)
            {
                best = release;
                bestVersion = version;
            }
        }
        return best;
    }

    /// <summary>The release zip and its detached signature: both
    /// "DSPi.Console.&lt;tag&gt;.zip" and that name plus ".sig" (GitHub turns
    /// the spaces of "DSPi Console" into dots). Null when either is missing,
    /// in which case the release can only be opened in the browser.</summary>
    public static (ReleaseAsset Zip, ReleaseAsset Signature)? SignedZip(AppRelease release)
    {
        var zipName = $"DSPi.Console.{release.TagName}.zip";
        var zip = release.Files.FirstOrDefault(a => string.Equals(a.Name, zipName, StringComparison.OrdinalIgnoreCase));
        if (zip == null) return null;
        var sig = release.Files.FirstOrDefault(a => string.Equals(a.Name, zip.Name + ".sig", StringComparison.OrdinalIgnoreCase));
        return sig == null ? null : (zip, sig);
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using DSPiConsole.Core.Models;
using DSPiConsole.Core.Updates;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace DSPiConsole.Core.Tests;

/// <summary>Which GitHub release the update check offers. Betas are told
/// apart by tag alone, since Windows betas were published as full releases.
/// Ported from the macOS Console's AppUpdateCheckerTests.</summary>
public class UpdateFeedTests
{
    private static AppRelease Release(string tag, bool draft = false, params string[] assets) =>
        new(tag, $"https://github.com/WeebLabs/DSPi-Console-Windows/releases/tag/{tag}", draft,
            assets.Select(a => new ReleaseAsset(a, $"https://example.invalid/{a}")).ToList());

    private static string? Offered(string[] tags, string current) =>
        UpdateFeed.NewestUpdate(tags.Select(t => Release(t)), FirmwareVersion.Parse(current)!.Value)?.TagName;

    [Fact]
    public void AFinalUserIsNotOfferedBetas()
    {
        Assert.Null(Offered(new[] { "v1.1.6-beta4", "v1.1.5" }, "1.1.5"));
        Assert.Equal("v1.1.6", Offered(new[] { "v1.1.7-beta1", "v1.1.6", "v1.1.5" }, "1.1.5"));
    }

    [Fact]
    public void ABetaUserIsOfferedNewerBetasAndTheFinal()
    {
        Assert.Equal("v1.1.6-beta4", Offered(new[] { "v1.1.6-beta4", "v1.1.6-beta3" }, "1.1.6-beta3"));
        Assert.Equal("v1.1.6", Offered(new[] { "v1.1.6", "v1.1.6-beta4" }, "1.1.6-beta4"));
    }

    [Fact]
    public void TagsOutsideTheReleaseSchemeAreNeverOffered()
    {
        // The version parser reads an unknown suffix as a final release.
        Assert.Null(Offered(new[] { "v1.1.7-rc1", "v1.1.7-alpha2", "v1.1.7-beta", "v1.1.7a" }, "1.1.6"));
        Assert.Null(Offered(new[] { "v1.1.7-beta", "v1.1.7-rc1" }, "1.1.6-beta4"));
        Assert.Equal("v1.1.7-hotfix", Offered(new[] { "v1.1.7-hotfix", "v1.1.6" }, "1.1.6"));
        Assert.Equal("v1.1.7-beta2-hotfix", Offered(new[] { "v1.1.7-beta2-hotfix" }, "1.1.7-beta1"));
    }

    [Fact]
    public void OnlyAnHttpsReleasePageIsOpened()
    {
        Assert.NotNull(Release("v1.1.7").Page);
        Assert.Null(new AppRelease("v1.1.7", "file:///C:/Windows/System32/calc.exe").Page);
        Assert.Null(new AppRelease("v1.1.7", "not a url").Page);
    }

    [Fact]
    public void NothingNewerOffersNothing()
    {
        Assert.Null(Offered(new[] { "v1.1.6-beta4", "v1.1.6-beta3", "v1.1.5" }, "1.1.6-beta4"));
        Assert.Null(Offered(Array.Empty<string>(), "1.1.6"));
        Assert.Null(Offered(new[] { "v1.1.6" }, "1.1.6"));
    }

    [Fact]
    public void ADoubleDigitBetaSortsAboveASingleDigitOne()
    {
        Assert.Equal("v1.1.6-beta10", Offered(new[] { "v1.1.6-beta9", "v1.1.6-beta10" }, "1.1.6-beta2"));
    }

    [Fact]
    public void DraftsAndTagsWithoutAVersionAreIgnored()
    {
        var releases = new[] { Release("v1.1.7", draft: true), Release("nightly"), Release("v1.1.6") };
        Assert.Equal("v1.1.6", UpdateFeed.NewestUpdate(releases, new FirmwareVersion(1, 1, 5))?.TagName);
    }

    [Fact]
    public void DecodesGitHubReleaseJson()
    {
        const string json = """
            [{"tag_name":"v1.1.6","html_url":"https://github.com/WeebLabs/DSPi-Console-Windows/releases/tag/v1.1.6",
              "draft":false,"prerelease":false,
              "assets":[{"name":"DSPi.Console.v1.1.6.zip","size":1,
                         "browser_download_url":"https://github.com/WeebLabs/DSPi-Console-Windows/releases/download/v1.1.6/DSPi.Console.v1.1.6.zip"}]},
             {"tag_name":"v1.1.6-beta4","html_url":"https://example.invalid","draft":false}]
            """;
        var releases = UpdateFeed.Parse(json);
        Assert.Equal(new FirmwareVersion(1, 1, 6), releases[0].Version);
        Assert.Equal("DSPi.Console.v1.1.6.zip", releases[0].Files[0].Name);
        Assert.EndsWith("/DSPi.Console.v1.1.6.zip", releases[0].Files[0].Url);
        Assert.Empty(releases[1].Files);
    }

    [Fact]
    public void OnlyTheReleaseZipWithItsSignatureCanBeInstalled()
    {
        var signed = UpdateFeed.SignedZip(Release("v1.1.7", false, "DSPi.Console.v1.1.7.zip", "DSPi.Console.v1.1.7.zip.sig", "notes.txt"));
        Assert.Equal("DSPi.Console.v1.1.7.zip", signed?.Zip.Name);
        Assert.Equal("DSPi.Console.v1.1.7.zip.sig", signed?.Signature.Name);

        Assert.Null(UpdateFeed.SignedZip(Release("v1.1.7", false, "DSPi.Console.v1.1.7.zip")));
        // Another zip's signature does not vouch for this one.
        Assert.Null(UpdateFeed.SignedZip(Release("v1.1.7", false, "DSPi.Console.v1.1.7.zip", "Other.zip", "Other.zip.sig")));
        // A zip named for another version is not this release's.
        Assert.Null(UpdateFeed.SignedZip(Release("v1.1.7", false, "DSPi.Console.v1.1.6.zip", "DSPi.Console.v1.1.6.zip.sig")));
    }
}

/// <summary>The release signature: Ed25519 over the whole zip, base64 in the ".sig".</summary>
public class UpdateSignatureTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    // RFC 8032 section 7.1, tests 1 and 2.
    private static readonly byte[] Rfc1Key = Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
    private static readonly string Rfc1Sig = Convert.ToBase64String(Hex(
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"));
    private static readonly byte[] Rfc2Key = Hex("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
    private static readonly string Rfc2Sig = Convert.ToBase64String(Hex(
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"));

    [Fact]
    public void TheRfcVectorsVerify()
    {
        Assert.True(UpdateSignature.IsValid(Array.Empty<byte>(), Rfc1Sig, Rfc1Key));
        Assert.True(UpdateSignature.IsValid(new byte[] { 0x72 }, Rfc2Sig, Rfc2Key));
    }

    [Fact]
    public void AnythingElseFails()
    {
        // The wrong message, the wrong key, a flipped signature bit.
        Assert.False(UpdateSignature.IsValid(new byte[] { 0x73 }, Rfc2Sig, Rfc2Key));
        Assert.False(UpdateSignature.IsValid(new byte[] { 0x72 }, Rfc2Sig, Rfc1Key));
        var flipped = Convert.FromBase64String(Rfc2Sig);
        flipped[5] ^= 1;
        Assert.False(UpdateSignature.IsValid(new byte[] { 0x72 }, Convert.ToBase64String(flipped), Rfc2Key));
        // Malformed signature text and keys are invalid, not errors.
        Assert.False(UpdateSignature.IsValid(new byte[] { 0x72 }, "not base64!", Rfc2Key));
        Assert.False(UpdateSignature.IsValid(new byte[] { 0x72 }, Convert.ToBase64String(new byte[10]), Rfc2Key));
        Assert.False(UpdateSignature.IsValid(new byte[] { 0x72 }, Rfc2Sig, new byte[5]));
        Assert.False(UpdateSignature.IsValid(new byte[] { 0x72 }, ""));
    }

    [Fact]
    public void ASignatureFileWithANewlineVerifies()
    {
        Assert.True(UpdateSignature.IsValid(new byte[] { 0x72 }, Rfc2Sig + "\r\n", Rfc2Key));
    }

    [Fact]
    public void AZipSignedWithAKeyVerifiesOnlyUnderThatKey()
    {
        var secret = new byte[Ed25519.SecretKeySize];
        RandomNumberGenerator.Fill(secret);
        var pub = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(secret, 0, pub, 0);
        var data = new byte[200_000];
        RandomNumberGenerator.Fill(data);
        var sig = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(secret, 0, data, 0, data.Length, sig, 0);

        Assert.True(UpdateSignature.IsValid(data, Convert.ToBase64String(sig), pub));
        data[^1] ^= 0x80;
        Assert.False(UpdateSignature.IsValid(data, Convert.ToBase64String(sig), pub));
        // Nor does it pass for the embedded release key.
        data[^1] ^= 0x80;
        Assert.False(UpdateSignature.IsValid(data, Convert.ToBase64String(sig)));
    }

    [Fact]
    public void TheEmbeddedKeyIsAnEd25519PublicKey()
    {
        Assert.Equal(Ed25519.PublicKeySize, UpdateSignature.PublicKey.Length);
    }
}

/// <summary>Where an update is staged for the launcher to swap in.</summary>
public sealed class UpdateLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dspi-update-test-{Guid.NewGuid():N}");
    private string AppDir => Path.Combine(_root, "app");

    public UpdateLayoutTests()
    {
        Directory.CreateDirectory(AppDir);
        File.WriteAllText(Path.Combine(_root, "DSPiConsole.exe"), "stub v1");
        File.WriteAllText(Path.Combine(AppDir, "DSPiConsole.exe"), "1.1.6");
        File.WriteAllText(Path.Combine(AppDir, "Old.dll"), "old");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Stands in for reading the exe's version resource: the fake
    /// executables hold their version as text.</summary>
    private static FirmwareVersion? ReadText(string path) =>
        File.Exists(path) ? FirmwareVersion.Parse(File.ReadAllText(path)) : null;

    /// <summary>A release zip: the stub at its root (or under one folder) and the app in app\.</summary>
    private string MakeZip(string appVersion, string stub = "stub v1", string? wrapper = null, bool withApp = true)
    {
        var source = Path.Combine(_root, $"zip-source-{Guid.NewGuid():N}");
        var payload = wrapper == null ? source : Path.Combine(source, wrapper);
        Directory.CreateDirectory(Path.Combine(payload, "app", "Assets"));
        File.WriteAllText(Path.Combine(payload, "DSPiConsole.exe"), stub);
        if (withApp) File.WriteAllText(Path.Combine(payload, "app", "DSPiConsole.exe"), appVersion);
        File.WriteAllText(Path.Combine(payload, "app", "New.dll"), "new");
        File.WriteAllText(Path.Combine(payload, "app", "Assets", "icon.png"), "png");
        var zip = Path.Combine(_root, $"release-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(source, zip);
        Directory.Delete(source, recursive: true);
        return zip;
    }

    [Fact]
    public void OnlyAnAppFolderBesideTheStubIsAReleaseLayout()
    {
        var layout = new UpdateLayout(AppDir + Path.DirectorySeparatorChar);
        Assert.True(layout.IsReleaseLayout);
        Assert.Equal(_root, layout.Root);
        Assert.True(layout.CanReplace());

        // A development build's output folder.
        Assert.False(new UpdateLayout(Path.Combine(_root, "bin")).IsReleaseLayout);
        // An app folder with no launcher beside it.
        File.Delete(Path.Combine(_root, "DSPiConsole.exe"));
        Assert.False(new UpdateLayout(AppDir).IsReleaseLayout);
        Assert.False(new UpdateLayout(AppDir).CanReplace());
    }

    [Fact]
    public void AReleaseIsStagedBesideTheRunningApp()
    {
        var layout = new UpdateLayout(AppDir);
        layout.Stage(MakeZip("1.1.7"), new FirmwareVersion(1, 1, 7), ReadText);

        Assert.True(layout.HasPending);
        Assert.Equal(new FirmwareVersion(1, 1, 7), layout.PendingVersion(ReadText));
        Assert.True(File.Exists(Path.Combine(_root, "app.pending", "New.dll")));
        Assert.True(File.Exists(Path.Combine(_root, "app.pending", "Assets", "icon.png")));
        // The running app is untouched; the launcher swaps it after exit.
        Assert.Equal("1.1.6", File.ReadAllText(Path.Combine(AppDir, "DSPiConsole.exe")));
        Assert.True(File.Exists(Path.Combine(AppDir, "Old.dll")));
        // An unchanged stub is not staged.
        Assert.False(File.Exists(layout.PendingStub));
        layout.CleanWork();
        Assert.False(Directory.Exists(layout.WorkDirectory));
        Assert.True(layout.HasPending);
    }

    [Fact]
    public void AChangedStubIsStagedToo()
    {
        var layout = new UpdateLayout(AppDir);
        layout.Stage(MakeZip("1.1.7", stub: "stub v2"), new FirmwareVersion(1, 1, 7), ReadText);
        Assert.Equal("stub v2", File.ReadAllText(layout.PendingStub));
        Assert.Equal("stub v1", File.ReadAllText(layout.Stub));
    }

    [Fact]
    public void AZipOfTheReleaseFolderItselfIsAccepted()
    {
        var layout = new UpdateLayout(AppDir);
        layout.Stage(MakeZip("1.1.7-beta2", wrapper: "DSPi Console"), new FirmwareVersion(1, 1, 7, 2), ReadText);
        Assert.Equal(new FirmwareVersion(1, 1, 7, 2), layout.PendingVersion(ReadText));
    }

    [Fact]
    public void AZipOfTheWrongVersionIsRefusedAndKeepsWhatWasStaged()
    {
        var layout = new UpdateLayout(AppDir);
        layout.Stage(MakeZip("1.1.7"), new FirmwareVersion(1, 1, 7), ReadText);
        var e = Assert.Throws<UpdateException>(() => layout.Stage(MakeZip("1.1.6"), new FirmwareVersion(1, 1, 8), ReadText));
        Assert.Contains("1.1.8", e.Message);
        Assert.Equal(new FirmwareVersion(1, 1, 7), layout.PendingVersion(ReadText));
    }

    [Fact]
    public void AZipThatIsNotAReleaseFolderIsRefused()
    {
        var layout = new UpdateLayout(AppDir);
        Assert.Throws<UpdateException>(() => layout.Stage(MakeZip("1.1.7", withApp: false), new FirmwareVersion(1, 1, 7), ReadText));
        Assert.False(layout.HasPending);

        var garbage = Path.Combine(_root, "garbage.zip");
        File.WriteAllText(garbage, "not a zip");
        Assert.Throws<UpdateException>(() => layout.Stage(garbage, new FirmwareVersion(1, 1, 7), ReadText));
    }

    [Fact]
    public void ANewerStagingReplacesAnOlderOne()
    {
        var layout = new UpdateLayout(AppDir);
        layout.Stage(MakeZip("1.1.7", stub: "stub v2"), new FirmwareVersion(1, 1, 7), ReadText);
        layout.Stage(MakeZip("1.1.8"), new FirmwareVersion(1, 1, 8), ReadText);
        Assert.Equal(new FirmwareVersion(1, 1, 8), layout.PendingVersion(ReadText));
        // The earlier release's stub went with it.
        Assert.False(File.Exists(layout.PendingStub));
    }

    [Fact]
    public void DiscardingRemovesEverythingStaged()
    {
        var layout = new UpdateLayout(AppDir);
        layout.Stage(MakeZip("1.1.7", stub: "stub v2"), new FirmwareVersion(1, 1, 7), ReadText);
        layout.DiscardPending();
        Assert.False(layout.HasPending);
        Assert.False(Directory.Exists(layout.PendingApp));
        Assert.False(File.Exists(layout.PendingStub));
        Assert.Null(layout.PendingVersion(ReadText));
    }

    [Fact]
    public void OnlyOneCopyOfTheAppHoldsTheDownloadArea()
    {
        var a = new UpdateLayout(AppDir);
        var b = new UpdateLayout(AppDir);
        using (var held = a.TryLock())
        {
            Assert.NotNull(held);
            Assert.Null(b.TryLock());
        }
        using var again = b.TryLock();
        Assert.NotNull(again);
        Assert.Null(new UpdateLayout(Path.Combine(_root, "bin")).TryLock());
    }

    [Fact]
    public void LeftoversFromAnEarlierAttemptAreNotStaged()
    {
        var layout = new UpdateLayout(AppDir);
        var stale = Path.Combine(layout.WorkDirectory, "unpacked", "app");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "Stale.dll"), "old");
        layout.Stage(MakeZip("1.1.7"), new FirmwareVersion(1, 1, 7), ReadText);
        Assert.False(File.Exists(Path.Combine(layout.PendingApp, "Stale.dll")));
    }
}

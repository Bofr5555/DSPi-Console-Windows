using DSPiConsole.Core.Background;

namespace DSPiConsole.Core.Tests;

public class BackgroundModeTests
{
    // ── Open at login ──

    [Fact]
    public void AReleaseStartsThroughTheLauncherStub()
    {
        var app = @"C:\Tools\DSPi Console v1.1.6\app\DSPiConsole.exe";
        var stub = @"C:\Tools\DSPi Console v1.1.6\DSPiConsole.exe";
        Assert.Equal(stub, LoginItem.LaunchPath(app, p => p == stub));
    }

    [Fact]
    public void ABuildWithNoStubStartsItself()
    {
        var app = @"C:\src\DSPiConsole\bin\x64\Debug\app\DSPiConsole.exe";
        Assert.Equal(app, LoginItem.LaunchPath(app, _ => false));
        var plain = @"C:\src\DSPiConsole\bin\x64\Debug\DSPiConsole.exe";
        Assert.Equal(plain, LoginItem.LaunchPath(plain, _ => true));
    }

    [Fact]
    public void TheRunValueIsTheQuotedPathAndParsesBack()
    {
        var path = @"C:\Program Files\DSPi Console\DSPiConsole.exe";
        var command = LoginItem.Command(path);
        Assert.Equal($"\"{path}\" --background", command);
        Assert.Equal(path, LoginItem.ProgramOf(command));
        Assert.Equal(path, LoginItem.ProgramOf($"\"{path}\""));
        Assert.Equal(path, LoginItem.ProgramOf($"\"{path}\" --minimized"));
        Assert.Equal(@"C:\Apps\DSPiConsole.exe", LoginItem.ProgramOf(@"C:\Apps\DSPiConsole.exe --x"));
        Assert.Null(LoginItem.ProgramOf("  "));
        Assert.Null(LoginItem.ProgramOf("\"\""));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[] { }, true)]
    [InlineData(new byte[] { 2, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 6, 0 }, true)]
    [InlineData(new byte[] { 3, 1, 2, 3 }, false)]
    [InlineData(new byte[] { 7 }, false)]
    public void TaskManagersSwitchIsRead(byte[]? approved, bool on) =>
        Assert.Equal(on, LoginItem.IsApproved(approved));

    [Fact]
    public void OnMeansThisInstallAndNotSwitchedOff()
    {
        var here = @"C:\DSPi\DSPiConsole.exe";
        Assert.True(LoginItem.IsOn("\"C:\\DSPi\\DSPiConsole.exe\"", null, here));
        Assert.True(LoginItem.IsOn("\"c:\\dspi\\dspiconsole.exe\"", null, here));
        Assert.False(LoginItem.IsOn("\"C:\\DSPi\\DSPiConsole.exe\"", new byte[] { 3 }, here));
        Assert.False(LoginItem.IsOn("\"D:\\Old\\DSPiConsole.exe\"", null, here));
        Assert.False(LoginItem.IsOn(null, null, here));
    }

    [Fact]
    public void AnEntryForThisCopyWithoutTheArgumentIsRewritten()
    {
        var here = @"C:\DSPi\DSPiConsole.exe";
        Assert.True(LoginItem.IsOutdated("\"C:\\DSPi\\DSPiConsole.exe\"", here));
        Assert.False(LoginItem.IsOutdated(LoginItem.Command(here), here));
        Assert.False(LoginItem.IsOutdated("\"D:\\Other\\DSPiConsole.exe\"", here));
        Assert.False(LoginItem.IsOutdated(null, here));
    }

    [Fact]
    public void OnlyAnEntryForAMissingCopyIsTakenOver()
    {
        var here = @"C:\DSPi\DSPiConsole.exe";
        var old = "\"D:\\Old\\DSPiConsole.exe\"";
        Assert.True(LoginItem.IsStale(old, here, _ => false));
        // Another copy that still exists (the release, while a build runs) keeps it.
        Assert.False(LoginItem.IsStale(old, here, _ => true));
        Assert.False(LoginItem.IsStale("\"C:\\DSPi\\DSPiConsole.exe\"", here, _ => false));
        Assert.False(LoginItem.IsStale(null, here, _ => false));
    }

    // ── Status ──

    [Theory]
    [InlineData(true, false, 0, "DSPi connected, not sharing")]
    [InlineData(false, false, 3, "No DSPi connected, not sharing")]
    [InlineData(true, true, 0, "DSPi connected, sharing, 0 clients")]
    [InlineData(true, true, 1, "DSPi connected, sharing, 1 client")]
    [InlineData(false, true, 2, "No DSPi connected, sharing, 2 clients")]
    public void TheStatusLineIsTheMacs(bool device, bool sharing, int clients, string expected) =>
        Assert.Equal(expected, TrayStatus.Line(device, sharing, clients));

    [Fact]
    public void TheTooltipFitsANotificationIcon()
    {
        Assert.Equal("DSPi Console\nDSPi connected, not sharing", TrayStatus.Tooltip("DSPi connected, not sharing"));
        Assert.Equal(127, TrayStatus.Tooltip(new string('x', 300)).Length);
    }

    // ── Glyph ──

    private static byte Alpha(uint p) => (byte)(p >> 24);

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void TheGlyphIsDrawnAtTheSizeAskedFor(int size)
    {
        var outline = TrayGlyph.Render(size, filled: false, 255, 255, 255);
        var filled = TrayGlyph.Render(size, filled: true, 255, 255, 255);
        Assert.Equal(size * size, outline.Length);
        // Corners are clear; the filled disc covers more than the ring.
        Assert.Equal(0, Alpha(outline[0]));
        Assert.Equal(0, Alpha(filled[size * size - 1]));
        Assert.True(filled.Count(p => Alpha(p) > 128) > outline.Count(p => Alpha(p) > 128));
        // The middle bar is drawn on the ring and cut out of the disc.
        int centre = size / 2 * size + size / 2;
        Assert.True(Alpha(outline[centre]) > 128);
        Assert.True(Alpha(filled[centre]) < 128);
    }

    [Fact]
    public void TheGlyphHasStraightAlpha()
    {
        // An icon's colour bitmap isn't premultiplied: every drawn pixel keeps
        // the full colour, and the edges fade by alpha alone.
        var pixels = TrayGlyph.Render(24, filled: true, 200, 100, 50);
        Assert.Contains(pixels, p => Alpha(p) is > 0 and < 255);
        foreach (var p in pixels.Where(p => Alpha(p) > 0))
            Assert.Equal(0xC86432u, p & 0xFFFFFF);
    }
}

using System.Text;
using DSPiConsole.Core.Link;
using DSPiConsole.Usb.Transport;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>What the hub advertises over DNS-SD (spec 3.1): the instance
/// label and the TXT record, including the 400-byte rule for <c>d</c>.</summary>
public class DnsSdAdvertiserTests
{
    private const string Hid = "3f2a1b4c-0d5e-4f60-8a71-92b3c4d5e6f7";

    private static LinkTxtRecord Record(int devices, bool web = true, LinkAuthMode auth = LinkAuthMode.None) =>
        new(1, Hid, LinkHubKind.Console, auth, devices,
            Enumerable.Range(0, devices).Select(i => $"E46058388B1A{i:X4}").ToList(), Web: web);

    [Fact]
    public void TxtCarriesTheConsoleKeysInOrder()
    {
        var pairs = DnsSdAdvertisement.TxtPairs(Record(2));
        Assert.Equal(["v", "hid", "kind", "auth", "n", "web", "d"], pairs.Select(p => p.Key));
        var txt = pairs.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("1", txt["v"]);
        Assert.Equal(Hid, txt["hid"]);
        Assert.Equal("console", txt["kind"]);
        Assert.Equal("none", txt["auth"]);
        Assert.Equal("2", txt["n"]);
        Assert.Equal("1", txt["web"]);
        Assert.Equal("E46058388B1A0000,E46058388B1A0001", txt["d"]);
        Assert.All(pairs, p => Assert.True(Encoding.UTF8.GetByteCount(p.Value) == p.Value.Length, "ASCII values"));

        // It parses back as the browser would read it.
        var back = LinkTxtRecord.TryParse(txt);
        Assert.NotNull(back);
        Assert.Equal(LinkHubKind.Console, back!.Kind);
        Assert.True(back.Web);
        Assert.Equal(2, back.Serials!.Count);
    }

    [Fact]
    public void WebAndPinAreOnlySaidWhenTrue()
    {
        var txt = DnsSdAdvertisement.TxtPairs(Record(0, web: false, auth: LinkAuthMode.Pin)).ToDictionary(p => p.Key, p => p.Value);
        Assert.False(txt.ContainsKey("web"));
        Assert.False(txt.ContainsKey("d"));
        Assert.False(txt.ContainsKey("tls"));
        Assert.False(txt.ContainsKey("path"));
        Assert.Equal("pin", txt["auth"]);
        Assert.Equal("0", txt["n"]);
    }

    [Fact]
    public void SerialsAreDroppedRatherThanPassThe400ByteLimit()
    {
        for (int devices = 0; devices <= 40; devices++)
        {
            var pairs = DnsSdAdvertisement.TxtPairs(Record(devices));
            Assert.True(DnsSdAdvertisement.TxtWireSize(pairs) <= LinkService.TxtBudget, $"{devices} devices");
            Assert.All(pairs, p => Assert.True(p.Key.Length + 1 + p.Value.Length <= LinkTxtRecord.MaxTxtString));
            var txt = pairs.ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(devices.ToString(), txt["n"]); // the count always stays
            if (txt.TryGetValue("d", out var d)) Assert.Equal(devices, d.Split(',').Length);
        }
        Assert.Contains(DnsSdAdvertisement.TxtPairs(Record(14)), p => p.Key == "d");
        // 15 serials: "d=" + 254 bytes passes one TXT string's 255.
        Assert.DoesNotContain(DnsSdAdvertisement.TxtPairs(Record(15)), p => p.Key == "d");
    }

    [Theory]
    [InlineData("Studio PC DSPi Console", "Studio PC DSPi Console")]
    [InlineData("  padded  ", "padded")]
    [InlineData("", DnsSdAdvertisement.FallbackName)]
    [InlineData(null, DnsSdAdvertisement.FallbackName)]
    [InlineData("Mr. Smith's PC", "Mr․ Smith's PC")]
    [InlineData("tab\there", "tab here")]
    public void InstanceLabelIsOneSafeLabel(string? name, string expected) =>
        Assert.Equal(expected, DnsSdAdvertisement.InstanceLabel(name));

    [Fact]
    public void InstanceLabelIsCutTo63BytesOnACharacterBoundary()
    {
        var label = DnsSdAdvertisement.InstanceLabel(new string('é', 40)); // 2 bytes each
        Assert.Equal(31, label.Length);
        Assert.True(Encoding.UTF8.GetByteCount(label) <= DnsSdAdvertisement.MaxInstanceBytes);
        var emoji = DnsSdAdvertisement.InstanceLabel(string.Concat(Enumerable.Repeat("\U0001F3B5", 20))); // 4 bytes each
        Assert.Equal(15 * 2, emoji.Length);
        Assert.Equal("Studio._dspi._tcp.local", DnsSdAdvertisement.ServiceName("Studio"));
    }

    [Fact(Skip = "Live: advertises on this machine's network and browses for itself. Run by hand.")]
    public async Task AdvertisesAndWithdrawsLive()
    {
        var name = "DSPi Advertiser Test " + Guid.NewGuid().ToString("N")[..6];
        using var browser = new WindowsDnsSdBrowser();
        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Found += (_, i) => { if (i.StartsWith(name, StringComparison.OrdinalIgnoreCase)) found.TrySetResult(i); };
        browser.Lost += (_, i) => { if (i.StartsWith(name, StringComparison.OrdinalIgnoreCase)) lost.TrySetResult(i); };
        browser.Start(LinkService.ServiceType);

        using var advertiser = new WindowsDnsSdAdvertiser();
        var registered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        advertiser.RegistrationCompleted += (_, r) => registered.TrySetResult(r.Status);
        advertiser.Start(name, 11999, Record(2));
        Assert.Equal(0, await registered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(advertiser.IsAdvertising);

        var instance = await found.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resolved = await browser.ResolveAsync(instance, TimeSpan.FromSeconds(5));
        Assert.NotNull(resolved);
        Assert.Equal(11999, resolved!.Port);
        Assert.Equal("console", resolved.Txt["kind"]);
        Assert.Equal("E46058388B1A0000,E46058388B1A0001", resolved.Txt["d"]);

        // An update re-registers with the new TXT.
        registered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        advertiser.Update(name, Record(3));
        Assert.Equal(0, await registered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(1000);
        var again = await browser.ResolveAsync(instance, TimeSpan.FromSeconds(5));
        Assert.Equal("3", again?.Txt["n"]);

        advertiser.Stop();
        await advertiser.Settled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(advertiser.IsAdvertising);
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>The fake a service test drives instead of the network.</summary>
    [Fact]
    public void TheInterfaceCanBeFaked()
    {
        IDnsSdAdvertiser fake = new FakeAdvertiser();
        fake.Start("Hub", 11915, Record(1));
        fake.Update("Hub 2", Record(2));
        fake.Stop();
        Assert.Equal(["start Hub 11915 1", "update Hub 2 2", "stop"], ((FakeAdvertiser)fake).Calls);
    }

    private sealed class FakeAdvertiser : IDnsSdAdvertiser
    {
        public List<string> Calls { get; } = new();
        public bool IsAdvertising { get; private set; }
        public void Start(string name, int port, LinkTxtRecord txt) { Calls.Add($"start {name} {port} {txt.DeviceCount}"); IsAdvertising = true; }
        public void Update(string name, LinkTxtRecord txt) => Calls.Add($"update {name} {txt.DeviceCount}");
        public void Stop() { Calls.Add("stop"); IsAdvertising = false; }
        public void Dispose() => Stop();
    }
}

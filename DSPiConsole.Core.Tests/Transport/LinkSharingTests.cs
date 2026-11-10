using System.Net;
using System.Net.Sockets;
using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;
using DSPiConsole.Core.Tests.Link.Hub;
using DSPiConsole.Usb.Transport;

namespace DSPiConsole.Core.Tests.Transport;

/// <summary>Sharing on and off: the server and the advertisement kept in step
/// with the hub (loopback sockets, a fake advertiser).</summary>
public sealed class LinkSharingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dspi-share-" + Guid.NewGuid().ToString("N"));
    private readonly LinkAuthStore _auth;
    private readonly LinkHub _hub;
    private readonly FakeAdvertiser _ads = new();
    private readonly LinkSharing _sharing;

    private sealed class FakeAdvertiser : IDnsSdAdvertiser
    {
        public readonly List<string> Calls = new();
        public LinkTxtRecord? Txt;
        public string? Name;
        public bool IsAdvertising { get; private set; }
        public void Start(string name, int port, LinkTxtRecord txt)
        {
            lock (Calls) Calls.Add($"start {name} {port}");
            (Name, Txt, IsAdvertising) = (name, txt, true);
        }
        public void Update(string name, LinkTxtRecord txt)
        {
            lock (Calls) Calls.Add($"update {name}");
            (Name, Txt) = (name, txt);
        }
        public void Stop()
        {
            lock (Calls) Calls.Add("stop");
            IsAdvertising = false;
        }
        public void Dispose() { }
    }

    public LinkSharingTests()
    {
        Directory.CreateDirectory(_dir);
        _auth = new LinkAuthStore(Path.Combine(_dir, "auth.json"));
        _hub = new LinkHub(LinkPolicy.Embedded!, _auth, new DeviceRegistry(null));
        _sharing = new LinkSharing(_hub, _ads, null);
    }

    public void Dispose()
    {
        _sharing.Dispose();
        _hub.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void Until(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 3000), "timed out");

    [Fact]
    public void StartingAdvertisesTheHub()
    {
        Assert.True(_sharing.Start(0));
        Assert.True(_sharing.IsRunning);
        Assert.NotNull(_sharing.BoundPort);
        Assert.Equal($"start {_auth.HubName} {_sharing.BoundPort}", Assert.Single(_ads.Calls));
        Assert.Equal(_auth.HubId.ToString(), _ads.Txt!.HubId);
        Assert.Equal(LinkAuthMode.None, _ads.Txt.Auth);
        Assert.Equal(0, _ads.Txt.DeviceCount);
    }

    [Fact]
    public void ADeviceARenameAndAnAccessChangeAreReadvertised()
    {
        _sharing.Start(0);
        _hub.AttachDevice(new FakeHubDevice());
        Until(() => _ads.Txt!.DeviceCount == 1);
        Assert.Equal(new[] { "E46058388B1A2E2C" }, _ads.Txt!.Serials);

        _auth.HubName = "Studio PC";
        Until(() => _ads.Name == "Studio PC");
        _auth.AuthMode = LinkAuthMode.Pin;
        Until(() => _ads.Txt!.Auth == LinkAuthMode.Pin);
    }

    [Fact]
    public void ASessionComingAndGoingDoesntReadvertise()
    {
        _sharing.Start(0);
        var s = _hub.OpenSession(LinkRole.Admin);
        _hub.CloseSession(s.Id);
        Assert.Single(_ads.Calls);
    }

    [Fact]
    public void StoppingStopsBoth()
    {
        _sharing.Start(0);
        _sharing.Stop();
        Assert.False(_sharing.IsRunning);
        Assert.Equal("stop", _ads.Calls.Last());
        // Nothing re-advertises a stopped hub.
        _auth.HubName = "Elsewhere";
        Assert.Equal("stop", _ads.Calls.Last());
    }

    [Fact]
    public void APortInUseIsReported()
    {
        using var taken = new TcpListener(IPAddress.IPv6Any, 0);
        taken.Server.DualMode = true;
        taken.Start();
        int port = ((IPEndPoint)taken.LocalEndpoint).Port;
        Assert.False(_sharing.Start(port));
        Assert.False(_sharing.IsRunning);
        Assert.Contains(port.ToString(), _sharing.LastError);
        Assert.Empty(_ads.Calls);
    }

    [Fact]
    public async Task TheInfoEndpointDescribesTheHub()
    {
        _sharing.Start(0);
        _hub.AttachDevice(new FakeHubDevice());
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"http://127.0.0.1:{_sharing.BoundPort}{LinkService.InfoPath}");
        var info = LinkInfo.Decode(json);
        Assert.Equal(_auth.HubId.ToString(), info.Hub.Id);
        Assert.Equal(LinkService.DefaultPath, info.Ws);
        Assert.Equal("E46058388B1A2E2C", Assert.Single(info.Devices!).Serial);
    }

    [Fact]
    public async Task AClientCanConnectEndToEnd()
    {
        _sharing.Start(0);
        _hub.AttachDevice(new FakeHubDevice());
        using var client = new LinkClient();
        await client.ConnectAsync(new LinkEndpoint("127.0.0.1", _sharing.BoundPort!.Value), "Test", null);
        Until(() => client.State.Phase == LinkClientPhase.Ready);
        Until(() => _sharing.ClientCount == 1);
        var ok = await client.RequestAsync(id => new LinkDeviceListRequest(id));
        Assert.Equal("E46058388B1A2E2C", Assert.Single(ok.DecodeBody<LinkDeviceListBody>().Devices).Serial);
        var r = await client.CommandAsync(new LinkCmdRequest(0, 0, LinkDirection.Get, 0x7F, 0, 2, 7));
        Assert.Equal(LinkStatus.Ok, r.Status);
    }
}

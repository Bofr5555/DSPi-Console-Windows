using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>
/// Analyser sharing across consoles through the hub (Link spec 7.15): the
/// local UI (session 1) is held by a remote session's watching like anyone
/// else, and each console's RTA_CONFIG echo is attributed to the session that
/// set it, so a console follows the others and never takes their change for
/// its own push. Port of the Mac's testHubWiresSchedulerIntoStopRule plus the
/// attribution cases L4 adds.
/// </summary>
public sealed class HubRtaSharingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dspi-rta-" + Guid.NewGuid().ToString("N"));
    private readonly LinkHub _hub;
    private readonly FakeHubDevice _device = new();

    public HubRtaSharingTests()
    {
        Directory.CreateDirectory(_dir);
        _hub = new LinkHub(LinkPolicy.Embedded!, new LinkAuthStore(Path.Combine(_dir, "auth.json")), new DeviceRegistry(null));
        _hub.OpenSession(LinkRole.Admin, LinkSessionIds.Local);
        _hub.AttachDevice(_device);
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private LinkCmdResponse Submit(LinkCmdRequest request, ushort session)
    {
        var done = new TaskCompletionSource<LinkCmdResponse>();
        _hub.Submit(request with { Handle = _hub.CurrentHandle }, session, r => done.TrySetResult(r));
        Assert.True(done.Task.Wait(5000), "the hub never answered");
        return done.Task.Result;
    }

    private static LinkCmdRequest Control(ushort op) => new(7, 0, LinkDirection.Get, 0x0E, op, 2, 1);
    private static LinkCmdRequest SetConfig(byte mask) =>
        new(8, 0, LinkDirection.Set, 0x08, 0, 2, payload: new byte[] { 1, 0, mask, 0, 10, 0, 0, 0, 0, 0, 0, 0 });

    /// <summary>NOTIFY_EVT_RTA_CONFIG: the 12-byte config at 4, the source at 16.</summary>
    private static LinkNotification RtaConfigNote(byte seq, byte mask, byte source = 1)
    {
        var p = new byte[17];
        p[0] = 0x02; p[1] = 0x0D; p[3] = seq;
        p[4] = 1; p[6] = mask; p[8] = 10;
        p[16] = source;
        return new LinkNotification(p, 0, DateTime.Now);
    }

    private int StopsReachingDevice() => _device.Executed.Count(r => r.BRequest == 0x0E && r.WValue == 0);

    [Fact]
    public void TheLocalUisStopWaitsForARemoteBandPoll()
    {
        var remote = _hub.OpenSession(LinkRole.Control);
        var grants = _hub.SubscribePolls(remote.Id, _hub.CurrentHandle, new[] { new LinkPollSpec(0, 0x0B, 0, 2, 82, 10) });
        Assert.True(Assert.Single(grants).Hz > 0);

        var stop = Submit(Control(0), LinkSessionIds.Local);
        Assert.Equal(new byte[] { 1 }, stop.Payload);
        Assert.Equal(0, StopsReachingDevice());

        _hub.CloseSession(remote.Id);
        Submit(Control(0), LinkSessionIds.Local);
        Assert.Equal(1, StopsReachingDevice());
    }

    [Fact]
    public void ARemoteStopWaitsForTheLocalUiWatching()
    {
        var remote = _hub.OpenSession(LinkRole.Control);
        Submit(new LinkCmdRequest(1, 0, LinkDirection.Get, 0x0B, 0, 2, 82), LinkSessionIds.Local);
        Submit(Control(0), remote.Id);
        Assert.Equal(0, StopsReachingDevice());
    }

    [Fact]
    public void AnotherConsolesConfigIsTheirsNotOurs()
    {
        var remote = _hub.OpenSession(LinkRole.Control);
        // The local UI starts the analyser, then the other console picks channels.
        Submit(Control(1), LinkSessionIds.Local);
        Assert.Equal(LinkStatus.Ok, Submit(SetConfig(0b01), remote.Id).Status);
        Assert.Equal(remote.Id, _hub.Ingest(RtaConfigNote(1, 0b01)));
    }

    [Fact]
    public void EachConsoleGetsItsOwnEcho()
    {
        var remote = _hub.OpenSession(LinkRole.Control);
        Submit(SetConfig(0b01), LinkSessionIds.Local);
        Submit(SetConfig(0b10), remote.Id);
        Assert.Equal(LinkSessionIds.Local, _hub.Ingest(RtaConfigNote(1, 0b01)));
        Assert.Equal(remote.Id, _hub.Ingest(RtaConfigNote(2, 0b10)));
    }

    [Fact]
    public void AUartHostsConfigIsNobodys()
    {
        Submit(SetConfig(0b01), LinkSessionIds.Local);
        Assert.Equal(LinkNotification.Unattributed, _hub.Ingest(RtaConfigNote(1, 0b11, source: 8)));   // PARAM_SRC_UART
    }

    [Fact]
    public void AViewerCantChooseChannels()
    {
        var viewer = _hub.OpenSession(LinkRole.Viewer);
        Assert.Equal(LinkStatus.Denied, Submit(SetConfig(0b01), viewer.Id).Status);
        Assert.Equal(LinkStatus.Denied, Submit(Control(1), viewer.Id).Status);
        Assert.Equal(LinkStatus.Ok, Submit(new LinkCmdRequest(1, 0, LinkDirection.Get, 0x0B, 0, 2, 82), viewer.Id).Status);
    }
}

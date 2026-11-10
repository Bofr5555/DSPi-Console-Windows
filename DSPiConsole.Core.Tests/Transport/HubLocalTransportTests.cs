using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;
using DSPiConsole.Usb;
using DSPiConsole.Usb.Transport;
using FakeTransport = DSPiConsole.Core.Tests.Transport.DspDeviceTransportTests.FakeTransport;

namespace DSPiConsole.Core.Tests.Transport;

/// <summary>The local UI's link through the hub (session 1) over a fake USB
/// transport.</summary>
public sealed class HubLocalTransportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dspi-local-" + Guid.NewGuid().ToString("N"));
    private readonly LinkHub _hub;
    private readonly FakeTransport _usb = new();
    private readonly HubLocalTransport _local;
    private static readonly DSPiDeviceInfo Board = new("E46058388B1A2E2C", "path");

    /// <summary>A 7-byte platform reply: RP2350, major 1, the 4-byte form's
    /// minor/patch nibbles, 9 outputs, minor 1, patch 6, final.</summary>
    private static readonly byte[] Platform = { 1, 1, 0x16, 9, 1, 6, 0 };

    public HubLocalTransportTests()
    {
        Directory.CreateDirectory(_dir);
        _hub = new LinkHub(LinkPolicy.Embedded!, new LinkAuthStore(Path.Combine(_dir, "auth.json")), new DeviceRegistry(null));
        _usb.OnIn = (request, value, length) => request switch
        {
            VendorCommands.GetPlatform => (LinkStatus.Ok, Platform),
            VendorCommands.GetDeviceName => (LinkStatus.Error, Array.Empty<byte>()),
            _ => (LinkStatus.Ok, new byte[Math.Min(length, 4)]),
        };
        _local = new HubLocalTransport(_usb, _hub) { IdentityRetryDelays = new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(10) } };
    }

    public void Dispose()
    {
        _local.Dispose();
        _hub.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private DSPiDeviceInfo ConnectAndWait()
    {
        var connected = new TaskCompletionSource<DSPiDeviceInfo>();
        _local.Connected += (_, d) => connected.TrySetResult(d);
        _usb.Connect(Board);
        Assert.True(connected.Task.Wait(3000), "never connected");
        return connected.Task.Result;
    }

    [Fact]
    public void ConnectedMeansTheHubHasTheBoard()
    {
        Assert.False(_local.IsConnected);
        ConnectAndWait();
        Assert.True(_local.IsConnected);
        Assert.True(_hub.IsDeviceAttached);
        var d = Assert.Single(_hub.Registry.Devices);
        Assert.Equal(Board.Serial, d.Info.Serial);
        Assert.Equal("1.1.6", d.Info.Firmware);
        Assert.Equal(9, d.Info.Outputs);
        Assert.Null(d.Info.BoardName);   // the name read STALLed: older firmware
    }

    [Fact]
    public void ABoardThatNeverAnswersIsStillTheLocalUisToUse()
    {
        _usb.OnIn = (_, _, _) => (LinkStatus.Error, Array.Empty<byte>());
        ConnectAndWait();
        Assert.True(_local.IsConnected);
        Assert.Equal(Board.Serial, _hub.Registry.Devices.Single().Info.Serial);
    }

    [Fact]
    public void CommandsGoThroughTheRouter()
    {
        ConnectAndWait();
        Assert.Equal(LinkStatus.Ok, _local.ControlOut(0xD2, 0, 2, new byte[] { 1, 2, 3, 4 }));
        Assert.Contains(_usb.Outs, o => o.Request == 0xD2);
        Assert.Equal(LinkStatus.Ok, _local.ControlIn(0x7F, 0, 2, 7, out var data));
        Assert.Equal(Platform, data);
    }

    [Fact]
    public void NoBoardNoCommands()
    {
        Assert.Equal(LinkStatus.NoDevice, _local.ControlOut(0xD2, 0, 2, new byte[] { 1 }));
    }

    [Fact]
    public void AnUnplugDetachesTheBoard()
    {
        ConnectAndWait();
        bool disconnected = false;
        _local.Disconnected += (_, _) => disconnected = true;
        _usb.Drop();
        Assert.True(disconnected);
        Assert.False(_local.IsConnected);
        Assert.False(_hub.IsDeviceAttached);
        Assert.Equal(LinkDeviceState.Offline, _hub.Registry.Devices.Single().State);
    }

    private static readonly byte[] HostParamChanged = { 0x02, 0x02, 0, 1, 0x10, 0, 4, 0, 1, 0, 0, 0, 0, 0, 0, 0 };

    [Fact]
    public void TheLocalUisOwnWriteComesBackAsItsOwn()
    {
        ConnectAndWait();
        LinkNotification? seen = null;
        _local.NotificationReceived += (_, n) => seen = n;
        _local.ControlOut(0xD2, 0, 2, new byte[] { 1, 2, 3, 4 });
        _usb.Notify(HostParamChanged, 1);
        Assert.NotNull(seen);
        Assert.True(_local.IsOwnWrite(seen!.Origin));
    }

    [Fact]
    public void ARemoteWriteIsntTheLocalUisOwn()
    {
        ConnectAndWait();
        var remote = _hub.OpenSession(LinkRole.Control);
        var done = new TaskCompletionSource<LinkCmdResponse>();
        _hub.Submit(new LinkCmdRequest(1, _hub.CurrentHandle, LinkDirection.Set, 0xD2, 0, 2, payload: new byte[] { 1, 2, 3, 4 }), remote.Id, r => done.SetResult(r));
        Assert.Equal(LinkStatus.Ok, done.Task.Result.Status);

        LinkNotification? seen = null;
        _local.NotificationReceived += (_, n) => seen = n;
        // The USB reader marks every host write as the local session's.
        _usb.Notify(HostParamChanged, 1);
        Assert.Equal(remote.Id, seen!.Origin);
        Assert.False(_local.IsOwnWrite(seen.Origin));
    }

    [Fact]
    public void KeepAlivesStillReachTheLocalUi()
    {
        ConnectAndWait();
        LinkNotification? seen = null;
        _local.NotificationReceived += (_, n) => seen = n;
        _usb.Notify(new byte[] { 0x02, 0x00, 0, 0 }, 0);
        Assert.NotNull(seen);
    }

    [Fact]
    public void ARemoteLockRefusesTheLocalFlow()
    {
        ConnectAndWait();
        var remote = _hub.OpenSession(LinkRole.Control);
        Assert.True(_hub.AcquireLock(remote.Id, TimeSpan.FromSeconds(30)));
        Assert.False(_local.AcquireDeviceLock("Saving preset", TimeSpan.FromSeconds(5)));
        Assert.True(_local.LastLockRefused);
        Assert.Equal(LinkStatus.Locked, _local.ControlOut(0xD2, 0, 2, new byte[] { 1 }));
        _hub.ReleaseLock(remote.Id);
        Assert.True(_local.AcquireDeviceLock("Saving preset", TimeSpan.FromSeconds(5)));
        Assert.False(_local.LastLockRefused);
        _local.ReleaseDeviceLock();
    }

    [Fact]
    public void TheBulkReadIsChunkedInOneExclusiveSection()
    {
        var blob = new byte[6000];
        BitConverter.GetBytes((ushort)6000).CopyTo(blob, 6);
        _usb.OnIn = (request, value, length) => request switch
        {
            VendorCommands.GetPlatform => (LinkStatus.Ok, Platform),
            VendorCommands.GetAllParamsChunk => (LinkStatus.Ok, blob.Skip(value).Take(length).ToArray()),
            _ => (LinkStatus.Error, Array.Empty<byte>()),
        };
        ConnectAndWait();
        var read = _local.ReadBulkParams();
        Assert.Equal(blob, read);
        var chunks = _usb.Ins.Where(i => i.Request == VendorCommands.GetAllParamsChunk).ToList();
        // Three per read: the hub warms its snapshot cache at attach too.
        Assert.True(chunks.Count is 3 or 6);
        Assert.All(chunks, c => Assert.True(c.Exclusive));
    }

    [Fact]
    public void ARemoteRequestGoesToOurVendorInterfaceWhateverItsWIndex()
    {
        ConnectAndWait();
        var remote = _hub.OpenSession(LinkRole.Admin);
        // The macOS Console names its audio interface (0); over WinUSB only
        // interface 2 is ours, and 0 would read as the board vanishing.
        foreach (ushort index in new ushort[] { 0, 2, 0x0100 })
        {
            var done = new TaskCompletionSource<LinkCmdResponse>();
            _hub.Submit(new LinkCmdRequest(1, _hub.CurrentHandle, LinkDirection.Get, 0x0F, 0, index, 1312), remote.Id, r => done.SetResult(r));
            Assert.Equal(LinkStatus.Ok, done.Task.Result.Status);
        }
        var sent = _usb.Ins.Where(i => i.Request == 0x0F).Select(i => i.Index).ToList();
        Assert.Equal(new ushort[] { 2, 2, 0x0102 }, sent);
    }

    [Fact]
    public void ARemotesWholeBlobWriteBecomesSequentialChunks()
    {
        ConnectAndWait();
        var remote = _hub.OpenSession(LinkRole.Admin);
        var blob = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var done = new TaskCompletionSource<LinkCmdResponse>();
        _hub.Submit(new LinkCmdRequest(1, _hub.CurrentHandle, LinkDirection.Set, VendorCommands.SetAllParams, 0, 2, payload: blob), remote.Id, r => done.SetResult(r));
        Assert.Equal(LinkStatus.Ok, done.Task.Result.Status);
        var chunks = _usb.Outs.Where(o => o.Request == VendorCommands.SetAllParamsChunk).ToList();
        Assert.Equal(new ushort[] { 0, 2048, 4096 }, chunks.Select(c => c.Value));
        Assert.Equal(blob, chunks.SelectMany(c => c.Data).ToArray());
    }
}

using System.ComponentModel;
using DSPiConsole.Core;
using DSPiConsole.Core.Link;
using DSPiConsole.Usb;
using DSPiConsole.Usb.Transport;

namespace DSPiConsole.Core.Tests.Transport;

/// <summary>
/// <see cref="DspDevice"/> over a fake <see cref="IDspTransport"/>: the facade
/// must look to the view model exactly as it did when it owned the USB handle
/// (the order of its property changes and events), route every transfer
/// through the transport, and carry each notification's origin.
/// </summary>
public class DspDeviceTransportTests
{
    internal sealed class FakeTransport : IDspTransport
    {
        public ushort Session { get; set; } = 1;
        public ulong Generation { get; set; }
        public bool IsConnected { get; set; }
        public string? ErrorMessage { get; set; }
        public DSPiDeviceInfo? SelectedDevice { get; set; }
        public IReadOnlyList<DSPiDeviceInfo> AvailableDevices { get; set; } = Array.Empty<DSPiDeviceInfo>();

        public event EventHandler<DSPiDeviceInfo>? Connected;
        public event EventHandler? Disconnected;
        public event EventHandler? AvailableDevicesChanged;
        public event EventHandler? ErrorMessageChanged;
        public event EventHandler? SelectedDeviceChanged;
        public event EventHandler<LinkNotification>? NotificationReceived;

        public Func<byte, ushort, int, (LinkStatus, byte[])> OnIn = (_, _, _) => (LinkStatus.Error, Array.Empty<byte>());
        public List<(byte Request, ushort Value, ushort Index, byte[] Data)> Outs = new();
        public List<(byte Request, ushort Value, ushort Index, int Length, bool Exclusive)> Ins = new();
        public bool InExclusive;
        public bool Started, Disposed;

        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void SelectDevice(DSPiDeviceInfo device) { }
        public void Reconnect() { }
        public void Disconnect() { }

        public LinkStatus ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data)
        {
            Outs.Add((request, value, index, data.ToArray()));
            return IsConnected ? LinkStatus.Ok : LinkStatus.NoDevice;
        }

        public LinkStatus ControlIn(byte request, ushort value, ushort index, int length, out byte[] data)
        {
            Ins.Add((request, value, index, length, InExclusive));
            var (status, bytes) = OnIn(request, value, length);
            data = bytes;
            return status;
        }

        public T Exclusive<T>(Func<T> body)
        {
            InExclusive = true;
            try { return body(); }
            finally { InExclusive = false; }
        }

        public void Dispose() => Disposed = true;

        public void Connect(DSPiDeviceInfo device)
        {
            SelectedDevice = device;
            IsConnected = true;
            ErrorMessage = null;
            Generation++;
            Connected?.Invoke(this, device);
        }

        public void Drop()
        {
            IsConnected = false;
            ErrorMessage = "Disconnected";
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Vanish()
        {
            SelectedDevice = null;
            SelectedDeviceChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetError(string message)
        {
            ErrorMessage = message;
            ErrorMessageChanged?.Invoke(this, EventArgs.Empty);
        }

        public void List(params DSPiDeviceInfo[] devices)
        {
            AvailableDevices = devices;
            AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Notify(byte[] packet, ushort origin) =>
            NotificationReceived?.Invoke(this, new LinkNotification(packet, origin, DateTime.Now));
    }

    private static readonly DSPiDeviceInfo Board = new("E46058388B1A2E2C", "vid_2E8B&pid_FEAA#E46058388B1A2E2C");

    private static (DspDevice Device, FakeTransport Transport, List<string> Log) Make()
    {
        var t = new FakeTransport();
        var d = new DspDevice(t);
        var log = new List<string>();
        d.PropertyChanged += (_, e) => log.Add("prop:" + e.PropertyName);
        d.DeviceConnected += (_, _) => log.Add("DeviceConnected");
        d.DeviceDisconnected += (_, _) => log.Add("DeviceDisconnected");
        d.AvailableDevicesChanged += (_, _) => log.Add("AvailableDevicesChanged");
        return (d, t, log);
    }

    // ── Connection state, as the view model has always seen it ──

    /// <summary>The view model does its connect work from the IsConnected
    /// change, and has never been told SelectedDeviceInfo on a connect.</summary>
    [Fact]
    public void AConnectLooksAsItAlwaysHas()
    {
        var (d, t, log) = Make();
        t.SetError("Disconnected");
        log.Clear();
        t.Connect(Board);
        Assert.Equal(["prop:IsConnected", "prop:ErrorMessage", "DeviceConnected"], log);
        Assert.True(d.IsConnected);
        Assert.Null(d.ErrorMessage);
        Assert.Equal(Board, d.SelectedDeviceInfo);
        Assert.Equal(1UL, d.Generation);
    }

    [Fact]
    public void ADisconnectLooksAsItAlwaysHas()
    {
        var (d, t, log) = Make();
        t.Connect(Board);
        log.Clear();
        t.Drop();
        Assert.Equal(["prop:IsConnected", "prop:ErrorMessage", "DeviceDisconnected"], log);
        Assert.False(d.IsConnected);
        Assert.Equal("Disconnected", d.ErrorMessage);
    }

    [Fact]
    public void TheSelectionClearsWhenTheBoardVanishes()
    {
        var (d, t, log) = Make();
        t.Connect(Board);
        log.Clear();
        t.Vanish();
        Assert.Equal(["prop:SelectedDeviceInfo"], log);
        Assert.Null(d.SelectedDeviceInfo);
    }

    [Fact]
    public void TheDeviceListAndErrorsComeFromTheTransport()
    {
        var (d, t, log) = Make();
        t.List(Board);
        Assert.Contains("AvailableDevicesChanged", log);
        Assert.Equal([Board], d.AvailableDevicesList);
        t.SetError("Failed to open device");
        Assert.Equal("Failed to open device", d.ErrorMessage);
    }

    [Fact]
    public void MonitoringAndDisposalReachTheTransport()
    {
        var (d, t, _) = Make();
        d.StartMonitoring();
        Assert.True(t.Started);
        d.Dispose();
        Assert.True(t.Disposed);
    }

    // ── Transfers ──

    [Fact]
    public void TransfersGoThroughTheTransportOnTheVendorInterface()
    {
        var (d, t, _) = Make();
        t.Connect(Board);
        t.OnIn = (req, val, len) => (LinkStatus.Ok, "Living room"u8.ToArray());

        Assert.Equal("Living room", d.GetChannelNameFromDevice(0));
        var call = Assert.Single(t.Ins);
        Assert.Equal(VendorCommands.GetChannelName, call.Request);
        Assert.Equal((ushort)2, call.Index);
    }

    /// <summary>A failed or empty GET reads as no answer, as it always did.</summary>
    [Fact]
    public void AFailedOrEmptyGetIsNoAnswer()
    {
        var (d, t, _) = Make();
        t.Connect(Board);
        t.OnIn = (_, _, _) => (LinkStatus.Ok, Array.Empty<byte>());
        Assert.Null(d.GetChannelNameFromDevice(0));
        t.OnIn = (_, _, _) => (LinkStatus.Timeout, Array.Empty<byte>());
        Assert.Null(d.GetChannelNameFromDevice(0));
    }

    /// <summary>The chunked bulk read is one exclusive section: the firmware
    /// reaps the chunk session on any other request.</summary>
    [Fact]
    public void TheBulkReadIsOneExclusiveSection()
    {
        var (d, t, _) = Make();
        t.Connect(Board);
        const int total = 6136;
        var image = Enumerable.Range(0, total).Select(i => (byte)(i * 7)).ToArray();
        image[6] = total & 0xFF;
        image[7] = total >> 8;
        t.OnIn = (req, offset, len) =>
        {
            Assert.Equal(VendorCommands.GetAllParamsChunk, req);
            return (LinkStatus.Ok, image.AsSpan(offset, Math.Min(len, total - offset)).ToArray());
        };

        var bulk = d.GetAllParams();

        Assert.Equal(image, bulk);
        Assert.Equal(3, t.Ins.Count);
        Assert.All(t.Ins, i => Assert.True(i.Exclusive));
    }

    [Fact]
    public void ABulkReadWithNoDeviceIsNoAnswer()
    {
        var (d, t, _) = Make();
        Assert.Null(d.GetAllParams());
        Assert.Empty(t.Ins);
    }

    // ── Notifications carry their origin ──

    /// <summary>v2 PARAM_CHANGED for the user volume: offset, size, source, payload.</summary>
    private static byte[] UserVolumePacket(float db, ParamSource source)
    {
        var p = new byte[16];
        p[0] = 0x02; p[1] = 0x02;
        BitConverter.GetBytes((ushort)BulkParamsParser.OffsetUserVolume).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)4).CopyTo(p, 6);
        p[8] = (byte)source;
        BitConverter.GetBytes(db).CopyTo(p, 12);
        return p;
    }

    [Fact]
    public void ANotificationCarriesItsOriginToTheTypedEvent()
    {
        var (d, t, _) = Make();
        var seen = new List<UserVolumeNotification>();
        d.UserVolumeNotified += (_, n) => seen.Add(n);

        t.Notify(UserVolumePacket(-12f, ParamSource.HostSet), origin: 1);
        t.Notify(UserVolumePacket(-6f, ParamSource.Uac1), origin: 0);

        Assert.Equal(2, seen.Count);
        Assert.Equal(-12f, seen[0].Db);
        Assert.Equal(1, seen[0].Origin);
        Assert.True(d.IsOwnWrite(seen[0].Origin));
        Assert.Equal(0, seen[1].Origin);
        Assert.False(d.IsOwnWrite(seen[1].Origin));
    }

    /// <summary>Another session's write is not ours, whatever its source byte says.</summary>
    [Fact]
    public void OnlyOurOwnSessionIsOurs()
    {
        var (d, t, _) = Make();
        t.Session = 12;
        Assert.True(d.IsOwnWrite(12));
        Assert.False(d.IsOwnWrite(13));
        Assert.False(d.IsOwnWrite(0));
        t.Session = 0; // a hub that hasn't told us our id yet
        Assert.False(d.IsOwnWrite(0));
    }

    /// <summary>The Interrupt Monitor sees every packet first, keep-alives included.</summary>
    [Fact]
    public void TheRawPacketEventSeesEveryPacketFirst()
    {
        var (d, t, _) = Make();
        var order = new List<string>();
        d.NotifyPacketReceived += (_, p) => order.Add($"raw:{p.Data.Length}:{p.Origin}");
        d.UserVolumeNotified += (_, _) => order.Add("typed");

        t.Notify([0x00], origin: 0);                                    // IDLE keep-alive
        t.Notify(UserVolumePacket(-3f, ParamSource.HostSet), origin: 1);

        Assert.Equal(["raw:1:0", "raw:16:1", "typed"], order);
    }
}

public class LinkNotifyAttributionTests
{
    [Theory]
    [InlineData(new byte[] { 2, 0x02, 0, 0, 0, 0, 0, 0, 1 }, true)]            // PARAM_CHANGED, HOST_SET
    [InlineData(new byte[] { 2, 0x02, 0, 0, 0, 0, 0, 0, 2 }, true)]            // PARAM_CHANGED, BULK_SET
    [InlineData(new byte[] { 2, 0x02, 0, 0, 0, 0, 0, 0, 7 }, false)]           // PARAM_CHANGED, UAC1
    [InlineData(new byte[] { 2, 0x02, 0, 0, 0, 0, 0, 0, 8 }, false)]           // PARAM_CHANGED, UART
    [InlineData(new byte[] { 2, 0x03, 0, 0, 1 }, true)]                        // BULK_INVALIDATED, HOST_SET
    [InlineData(new byte[] { 2, 0x03, 0, 0, 3 }, false)]                       // BULK_INVALIDATED, PRESET
    [InlineData(new byte[] { 2, 0x0C, 0, 0, 0, 0, 0, 0, 1 }, true)]            // CS_AUX
    [InlineData(new byte[] { 2, 0x10, 0, 0, 0, 1 }, true)]                     // DEVICE_NAME
    [InlineData(new byte[] { 2, 0x0F, 0, 0, 0, 5 }, false)]                    // INPUT_SOURCE_NAME, GPIO
    [InlineData(new byte[] { 2, 0x04, 0, 0, 1 }, false)]                       // PRESET_LOADED: not attributable
    [InlineData(new byte[] { 0 }, false)]                                      // IDLE
    [InlineData(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 1 }, false)]              // v1
    [InlineData(new byte[] { 2, 0x02, 0, 0 }, false)]                          // truncated
    public void HostSourcedIsHostOrBulkSetWhereEachEventPutsIt(byte[] packet, bool expected) =>
        Assert.Equal(expected, LinkNotifyAttribution.IsHostSourced(packet));

    [Fact]
    public void TheRtaConfigSourceIsAtSixteen()
    {
        var packet = new byte[17];
        packet[0] = 2; packet[1] = 0x0D;
        packet[16] = 1;
        Assert.True(LinkNotifyAttribution.IsHostSourced(packet));
        packet[16] = 8;
        Assert.False(LinkNotifyAttribution.IsHostSourced(packet));
    }
}

public class InFlightGateTests
{
    [Fact]
    public async Task ARunInFlightSkipsTheNext()
    {
        var gate = new InFlightGate();
        var release = new TaskCompletionSource();
        int runs = 0;
        var first = gate.RunAsync(async () => { runs++; await release.Task; });
        await gate.RunAsync(() => { runs++; return Task.CompletedTask; });
        Assert.Equal(1, runs);
        Assert.True(gate.IsBusy);
        release.SetResult();
        await first;
        Assert.False(gate.IsBusy);
        await gate.RunAsync(() => { runs++; return Task.CompletedTask; });
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task AFailedRunStillReleases()
    {
        var gate = new InFlightGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync(() => throw new InvalidOperationException()));
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public void ABackgroundRunHoldsTheGateUntilItEnds()
    {
        var gate = new InFlightGate();
        using var go = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        gate.RunInBackground(() => { go.Wait(); done.Set(); });
        Assert.True(gate.IsBusy);
        int extra = 0;
        gate.RunInBackground(() => extra++);
        go.Set();
        Assert.True(done.Wait(5000));
        SpinWait.SpinUntil(() => !gate.IsBusy, 5000);
        Assert.False(gate.IsBusy);
        Assert.Equal(0, extra);
    }
}

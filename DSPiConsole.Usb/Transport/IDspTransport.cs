using DSPiConsole.Core.Link;

namespace DSPiConsole.Usb.Transport;

/// <summary>
/// A link to a DSPi: the connection, the device list and selection, the
/// vendor control transfers, and the notification stream. <see cref="DspDevice"/>
/// is the protocol facade above it and keeps its public API; only this layer
/// changes when the device is reached another way (a hub's session for the
/// local UI, a remote hub over DSPi Link). Port of the macOS Console's
/// DeviceTransport.
/// </summary>
public interface IDspTransport : IDisposable
{
    /// <summary>This transport's own session id, for echo suppression: a
    /// notification whose origin is this id is the echo of our own write.</summary>
    ushort Session { get; }

    /// <summary>Bumps every time the underlying device connection is
    /// replaced, so work queued for one device isn't applied to the next.</summary>
    ulong Generation { get; }

    bool IsConnected { get; }
    string? ErrorMessage { get; }
    DSPiDeviceInfo? SelectedDevice { get; }
    IReadOnlyList<DSPiDeviceInfo> AvailableDevices { get; }

    /// <summary>A device was opened. Raised after <see cref="IsConnected"/>,
    /// <see cref="SelectedDevice"/> and <see cref="Generation"/> are set.</summary>
    event EventHandler<DSPiDeviceInfo>? Connected;
    /// <summary>The open device was lost or closed.</summary>
    event EventHandler? Disconnected;
    event EventHandler? AvailableDevicesChanged;
    event EventHandler? ErrorMessageChanged;
    /// <summary>The selection changed outside a connect: the selected device
    /// disappeared. Never raised for a connect, which carries its device in
    /// <see cref="Connected"/>: the facade sets the selection quietly there,
    /// which is the order the view model has always seen.</summary>
    event EventHandler? SelectedDeviceChanged;
    /// <summary>A notification packet, on the transport's reader thread.</summary>
    event EventHandler<LinkNotification>? NotificationReceived;

    /// <summary>Starts looking for devices.</summary>
    void Start();
    void Stop();

    /// <summary>Switches to <paramref name="device"/>. Blocks; call off the UI thread.</summary>
    void SelectDevice(DSPiDeviceInfo device);
    void Reconnect();
    void Disconnect();

    /// <summary>A vendor SET (host to device).</summary>
    LinkStatus ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data);

    /// <summary>A vendor GET (device to host): the bytes the device returned
    /// (possibly fewer than asked) with <see cref="LinkStatus.Ok"/>, or a failure.</summary>
    LinkStatus ControlIn(byte request, ushort value, ushort index, int length, out byte[] data);

    /// <summary>Runs <paramref name="body"/> with no other command of this
    /// transport interleaving.</summary>
    T Exclusive<T>(Func<T> body);

    /// <summary>The whole WireBulkParams blob, read the way this link must:
    /// in chunks over USB (<see cref="BulkRead.Chunked"/>), as one 0xA0 or the
    /// hub's snapshot over DSPi Link (never 0xA2/0xA3, which a hub can't keep
    /// in one session). Null on any failure.</summary>
    byte[]? ReadBulkParams();

    /// <summary>The caller gave up on the link (a bulk read that never
    /// answered): drop it so the connection is re-established and the view
    /// model re-runs its connect sequence. Over USB, the next scan reopens the
    /// device.</summary>
    void MarkDisconnected();

    /// <summary>Takes the hub's exclusive device lock (spec 7.7), so other
    /// sessions' commands answer LOCKED until <see cref="ReleaseDeviceLock"/>.
    /// Blocks. True when granted; with no hub there is nobody to lock out.</summary>
    bool AcquireDeviceLock(string reason, TimeSpan timeout) => true;
    void ReleaseDeviceLock() { }

    /// <summary>The last <see cref="AcquireDeviceLock"/> failed because another
    /// console holds the device, rather than a dropped link or a role.</summary>
    bool LastLockRefused => false;
}

public static class DspTransportExtensions
{
    /// <summary>A notification's origin names this transport's own session:
    /// the echo of a write already applied locally. Origin 0 is never ours,
    /// even while our session id is still 0.</summary>
    public static bool IsOwnWrite(this IDspTransport transport, ushort origin) =>
        origin != LinkNotification.Unattributed && origin == transport.Session;
}

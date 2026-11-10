namespace DSPiConsole.Core.Link.Hub;

// The seams between the hub's parts (L3). The hub core (router, registry,
// relay, scheduler, snapshot, auth, session handler) is socket-free and
// USB-free; the server and the USB adapter plug in through these.

/// <summary>Session ids the hub reserves (spec 5): the local UI is always 1,
/// the hub's own reads (snapshot, adoption) run as 0xFFFF, and 0 means
/// "nobody" in a NOTIFY origin. Remote sessions get ids from 2 up.</summary>
public static class LinkSessionIds
{
    public const ushort None = 0;
    public const ushort Local = 1;
    public const ushort Internal = 0xFFFF;
}

/// <summary>The identity a hub publishes for one device (spec 7.4).</summary>
public sealed record HubDeviceInfo(
    string Serial,
    byte Platform,
    /// <summary>"1.1.6", without the beta.</summary>
    string Firmware,
    int Outputs,
    /// <summary>Beta number, 0 for a final; null when the platform reply is
    /// too short to say (spec 7.4).</summary>
    int? FirmwareBeta = null,
    int? Inputs = null,
    int? WireVersion = null,
    LinkDeviceLinkKind Link = LinkDeviceLinkKind.Usb,
    /// <summary>The board's own name: null when the firmware can't store one,
    /// empty when it can but is unnamed.</summary>
    string? BoardName = null);

/// <summary>One device the hub drives. <see cref="Execute"/> blocks; the
/// router calls it from that device's own worker, never the UI thread.</summary>
public interface IHubDevice
{
    HubDeviceInfo Info { get; }
    /// <summary>Bumps when the underlying connection is replaced, so a result
    /// computed for one attachment isn't attributed to the next.</summary>
    ulong Generation { get; }
    bool IsConnected { get; }
    /// <summary>Runs one vendor request. A SET answers an empty payload on
    /// success; a GET answers the device's bytes. 0xA0/0xA1 are served
    /// whole here (over USB: as a chunked 0xA2 read / 0xA3 write, spec
    /// 9.1.1); the router holds the device for the whole call.</summary>
    LinkCmdResponse Execute(LinkCmdRequest request);
}

/// <summary>One message a connection's handler wants sent.</summary>
public abstract record LinkOutbound
{
    private LinkOutbound() { }
    /// <summary>A control-plane JSON message (a WebSocket text frame).</summary>
    public sealed record Text(string Json) : LinkOutbound;
    /// <summary>A data-plane frame (a WebSocket binary frame).</summary>
    public sealed record Binary(byte[] Data) : LinkOutbound;
    /// <summary>Close the connection with this code (spec 5, LinkCloseCode).</summary>
    public sealed record Close(ushort Code) : LinkOutbound;
}

/// <summary>The protocol side of one WebSocket connection: the server moves
/// whole (reassembled) messages in and <see cref="LinkOutbound"/>s out. Calls
/// into one connection are never concurrent.</summary>
public interface ILinkConnection
{
    bool IsAuthenticated { get; }
    void ReceiveText(string json);
    void ReceiveBinary(byte[] data);
    /// <summary>The socket closed (either side): release the session.</summary>
    void ConnectionClosed();
}

/// <summary>Makes the handler for a new connection. <paramref name="peer"/>
/// is the remote IP (no port), for auth rate limiting; <paramref name="emit"/>
/// may be called from any thread.</summary>
public delegate ILinkConnection LinkConnectionFactory(string peer, Action<LinkOutbound> emit);

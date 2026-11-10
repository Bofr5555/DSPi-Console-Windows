using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Usb.Transport;

/// <summary>
/// The board this PC has open over USB, as the hub drives it: a tunnelled
/// request becomes the matching control transfer. A client's whole-blob
/// REQ_GET_ALL_PARAMS (0xA0) / REQ_SET_ALL_PARAMS (0xA1) can't cross WinUSB's
/// 4 KB control cap, so it runs as a chunked 0xA2 read / 0xA3 write inside
/// one exclusive section of the USB link: the firmware abandons a chunk
/// session if any other request lands between chunks (spec 9.1.1,
/// firmware bulk_params_chunking.md). Port of the macOS Console's
/// USBHubDevice.
/// </summary>
public sealed class UsbHubDevice : IHubDevice
{
    private readonly IDspTransport _usb;
    private readonly ulong _generation;
    private HubDeviceInfo _info;
    private readonly object _lock = new();

    public UsbHubDevice(IDspTransport usb, HubDeviceInfo info)
    {
        _usb = usb;
        _info = info;
        _generation = usb.Generation;
    }

    public HubDeviceInfo Info { get { lock (_lock) return _info; } }

    public void UpdateInfo(Func<HubDeviceInfo, HubDeviceInfo> change)
    {
        lock (_lock) _info = change(_info);
    }

    /// <summary>The USB open this device was made for: a new open is a new
    /// generation, and the hub attaches a new device for it.</summary>
    public ulong Generation => _usb.Generation;

    public bool IsConnected => _usb.IsConnected && _usb.Generation == _generation;

    public LinkCmdResponse Execute(LinkCmdRequest request)
    {
        var response = ExecuteCore(request);
        if (response.Status == LinkStatus.NoDevice && Interlocked.Increment(ref _noDeviceLogged) <= 10)
            LinkLog.Write($"hub: USB answered 0x{request.BRequest:X2} NoDevice (USB connected {_usb.IsConnected}, generation {_usb.Generation}, attached as {_generation})");
        return response;
    }

    private int _noDeviceLogged;

    /// <summary>
    /// The interface a tunnelled request goes to here: always the vendor
    /// interface this PC holds through WinUSB. A client names its own
    /// machine's interface in wIndex; the macOS Console talks to the board
    /// over the audio interface (0) natively, and over WinUSB a request for
    /// an interface Windows' audio driver owns fails "not found", which the
    /// USB layer takes as the board being gone. The high byte goes through.
    /// </summary>
    internal static ushort LocalIndex(ushort wIndex) => (ushort)((wIndex & 0xFF00) | VendorInterface);

    private LinkCmdResponse ExecuteCore(LinkCmdRequest request)
    {
        if (!IsConnected) return new LinkCmdResponse(request.Tag, LinkStatus.NoDevice);
        ushort index = LocalIndex(request.WIndex);

        if (request.BRequest == VendorCommands.GetAllParams && request.Direction == LinkDirection.Get)
        {
            var blob = _usb.ReadBulkParams();
            if (blob == null) return new LinkCmdResponse(request.Tag, IsConnected ? LinkStatus.Error : LinkStatus.NoDevice);
            // The device answers at most what was asked for.
            if (blob.Length > request.WLength) Array.Resize(ref blob, request.WLength);
            return new LinkCmdResponse(request.Tag, LinkStatus.Ok, blob);
        }
        if (request.BRequest == VendorCommands.SetAllParams && request.Direction == LinkDirection.Set)
            return new LinkCmdResponse(request.Tag, WriteChunked(index, request.Payload));

        if (request.Direction == LinkDirection.Get)
        {
            var status = _usb.ControlIn(request.BRequest, request.WValue, index, request.WLength, out var data);
            return new LinkCmdResponse(request.Tag, status, status == LinkStatus.Ok ? data : Array.Empty<byte>());
        }
        return new LinkCmdResponse(request.Tag, _usb.ControlOut(request.BRequest, request.WValue, index, request.Payload));
    }

    /// <summary>Sequential, non-overlapping 0xA3 chunks from offset 0; the
    /// device applies the blob when the final byte lands. A STALL anywhere
    /// means the session is gone, so the whole write fails and the client
    /// re-sends.</summary>
    private LinkStatus WriteChunked(ushort index, byte[] blob)
    {
        if (blob.Length == 0) return LinkStatus.Error;
        return _usb.Exclusive(() =>
        {
            for (int offset = 0; offset < blob.Length; offset += BulkRead.ChunkSize)
            {
                int n = Math.Min(BulkRead.ChunkSize, blob.Length - offset);
                var status = _usb.ControlOut(VendorCommands.SetAllParamsChunk, (ushort)offset, index, blob.AsSpan(offset, n));
                if (status != LinkStatus.Ok) return status;
            }
            return LinkStatus.Ok;
        });
    }

    /// <summary>Reads the open board's identity for the registry:
    /// REQ_GET_PLATFORM (7 bytes) and the board name. Null when the board
    /// doesn't answer yet (still booting); the caller probes again.</summary>
    public static HubDeviceInfo? ReadInfo(IDspTransport usb, string serial)
    {
        if (usb.ControlIn(VendorCommands.GetPlatform, 0, VendorInterface, 7, out var p) != LinkStatus.Ok
            || FirmwareVersion.FromPlatformReply(p) is not { } reply)
            return null;
        var v = reply.Version;
        // A STALL or a short reply (older firmware) means no board name; one
        // retry, so a transient miss doesn't treat a capable board as old.
        string? boardName = null;
        for (int attempt = 0; attempt < 2 && boardName == null; attempt++)
        {
            if (usb.ControlIn(VendorCommands.GetDeviceName, 0, VendorInterface, DeviceNameText.FieldLength, out var n) == LinkStatus.Ok
                && n.Length == DeviceNameText.FieldLength)
                boardName = DeviceNameText.Decode(n);
        }
        return new HubDeviceInfo(
            Serial: serial,
            Platform: reply.Platform,
            Firmware: $"{v.Major}.{v.Minor}.{v.Patch}",
            Outputs: p.Length > 3 ? p[3] : 0,
            // fw_beta reports only what byte 6 says; a short reply leaves it
            // unknown (spec 7.4).
            FirmwareBeta: p.Length >= 7 ? p[6] : null,
            Link: LinkDeviceLinkKind.Usb,
            BoardName: boardName);
    }

    private const ushort VendorInterface = 2;
}

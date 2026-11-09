using System.Buffers.Binary;

namespace DSPiConsole.Core.Link;

/// <summary>
/// Frame types of the binary data plane (spec section 8). 0x06-0x7F and
/// 0x85-0xFF are reserved for later minor versions, so decoding one is a
/// distinct error the caller can ignore rather than a failure.
/// </summary>
public enum LinkFrameType : byte
{
    CmdRequest = 0x01,
    CmdResponse = 0x81,
    Notify = 0x02,
    Poll = 0x03,
    FwData = 0x04,
    FwDataAck = 0x84,
    Resync = 0x05,
}

/// <summary>
/// Direction of a tunnelled vendor command: the client's statement of the USB
/// transfer type, so write-as-read commands travel as Get exactly as they do
/// over USB (spec 8.2).
/// </summary>
public enum LinkDirection : byte
{
    /// <summary>Host to device, bmRequestType 0x41.</summary>
    Set = 0,
    /// <summary>Device to host, bmRequestType 0xC1.</summary>
    Get = 1,
}

public enum LinkFrameErrorKind
{
    /// <summary>A frame type this build does not know. Per spec 10.4 a
    /// receiver drops these, so it is separate from a decode failure.</summary>
    UnknownType,
    Truncated,
    UnknownDirection,
}

/// <summary>A frame that could not be decoded. <see cref="Tag"/> is the
/// header's tag when the frame was long enough to carry one, so a hub can
/// answer an unknown request type with UNSUPPORTED, or a malformed frame with
/// BAD_FRAME, under the sender's tag (spec 8, 8.1).</summary>
public sealed class LinkFrameException(LinkFrameErrorKind kind, byte value = 0, byte? type = null, ushort? tag = null)
    : Exception($"Link frame: {kind}{(kind is LinkFrameErrorKind.Truncated ? "" : $" 0x{value:X2}")}")
{
    public LinkFrameErrorKind Kind { get; } = kind;
    /// <summary>The offending byte, for the kinds that have one.</summary>
    public byte Value { get; } = value;
    /// <summary>The frame's type byte, when it had one.</summary>
    public byte? FrameType { get; } = type;
    /// <summary>The frame's tag, when it had one.</summary>
    public ushort? Tag { get; } = tag;
}

/// <summary>
/// One binary frame. Every frame starts with a 4-byte header (type, flags,
/// tag) and every multi-byte integer is little-endian, matching the firmware.
/// Port of the macOS Console's LinkFrame.
/// </summary>
public abstract record LinkFrame
{
    public abstract LinkFrameType Type { get; }

    /// <summary>Decodes one frame. Throws <see cref="LinkFrameException"/>,
    /// carrying the tag whenever the header was complete.</summary>
    public static LinkFrame Decode(ReadOnlySpan<byte> data)
    {
        byte? headerType = data.Length >= 1 ? data[0] : null;
        ushort? headerTag = data.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) : null;
        try
        {
            return DecodeBody(data);
        }
        catch (LinkFrameException e) when (e.Tag == null && headerTag != null)
        {
            throw new LinkFrameException(e.Kind, e.Value, headerType, headerTag);
        }
    }

    private static LinkFrame DecodeBody(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        byte rawType = r.U8();
        if (!Enum.IsDefined(typeof(LinkFrameType), rawType))
            throw new LinkFrameException(LinkFrameErrorKind.UnknownType, rawType);
        r.U8(); // flags, reserved
        ushort tag = r.U16();

        switch ((LinkFrameType)rawType)
        {
            case LinkFrameType.CmdRequest:
            {
                byte handle = r.U8();
                byte rawDir = r.U8();
                if (rawDir > 1) throw new LinkFrameException(LinkFrameErrorKind.UnknownDirection, rawDir);
                var dir = (LinkDirection)rawDir;
                byte bRequest = r.U8();
                r.U8(); // reserved
                ushort wValue = r.U16(), wIndex = r.U16(), wLength = r.U16();
                var payload = dir == LinkDirection.Set ? r.Bytes(wLength) : Array.Empty<byte>();
                return new LinkCmdRequest(tag, handle, dir, bRequest, wValue, wIndex, wLength, payload);
            }
            case LinkFrameType.CmdResponse:
            {
                var status = r.Status();
                r.U8(); // reserved
                ushort length = r.U16();
                return new LinkCmdResponse(tag, status, r.Bytes(length));
            }
            case LinkFrameType.Notify:
            {
                byte handle = r.U8();
                r.U8(); // reserved
                ushort origin = r.U16();
                return new LinkNotifyFrame(tag, handle, origin, r.Rest());
            }
            case LinkFrameType.Poll:
            {
                byte handle = r.U8(), slot = r.U8();
                ushort length = r.U16();
                return new LinkPollFrame(tag, handle, slot, r.Bytes(length));
            }
            case LinkFrameType.FwData:
            {
                uint offset = r.U32();
                return new LinkFwDataFrame(tag, offset, r.Rest());
            }
            case LinkFrameType.FwDataAck:
            {
                uint offset = r.U32();
                return new LinkFwDataAck(tag, offset, r.Status());
            }
            default: // Resync
            {
                byte handle = r.U8(), reason = r.U8();
                return new LinkResyncFrame(handle, reason);
            }
        }
    }

    /// <summary>The frame's bytes.</summary>
    public byte[] Encode()
    {
        var w = new Writer();
        w.U8((byte)Type);
        w.U8(0); // flags
        Write(w);
        return w.ToArray();
    }

    /// <summary>Everything after the type and flags bytes.</summary>
    private protected abstract void Write(Writer w);

    /// <summary>Two frames with the same bytes. Records compare arrays by
    /// reference, so this is the comparison to use.</summary>
    public bool SameBytes(LinkFrame other) => Encode().AsSpan().SequenceEqual(other.Encode());

    // ── Byte plumbing ──

    private protected ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        public byte U8()
        {
            if (_data.Length - _offset < 1) throw new LinkFrameException(LinkFrameErrorKind.Truncated);
            return _data[_offset++];
        }

        public ushort U16()
        {
            if (_data.Length - _offset < 2) throw new LinkFrameException(LinkFrameErrorKind.Truncated);
            var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[_offset..]);
            _offset += 2;
            return v;
        }

        public uint U32()
        {
            if (_data.Length - _offset < 4) throw new LinkFrameException(LinkFrameErrorKind.Truncated);
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_offset..]);
            _offset += 4;
            return v;
        }

        /// <summary>Any byte: a bridge forwards the firmware's status verbatim
        /// and both tables may grow (spec 9.2, 10.5), so an unknown status is
        /// the caller's to judge (<see cref="LinkStatusExtensions.IsDefined"/>),
        /// not a reason to lose the response and its tag.</summary>
        public LinkStatus Status() => (LinkStatus)U8();

        public byte[] Bytes(int count)
        {
            if (count < 0 || _data.Length - _offset < count) throw new LinkFrameException(LinkFrameErrorKind.Truncated);
            var v = _data.Slice(_offset, count).ToArray();
            _offset += count;
            return v;
        }

        public byte[] Rest()
        {
            var v = _data[_offset..].ToArray();
            _offset = _data.Length;
            return v;
        }
    }

    /// <summary>A length for a u16 field; a longer payload can't be framed, and
    /// a silently wrapped length would desynchronise the receiver.</summary>
    private protected static ushort Length(int count) => count <= ushort.MaxValue
        ? (ushort)count
        : throw new InvalidOperationException($"A {count}-byte payload doesn't fit a Link frame (at most 65535).");

    private protected sealed class Writer
    {
        private readonly MemoryStream _out = new();
        public void U8(byte v) => _out.WriteByte(v);
        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _out.Write(b); }
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _out.Write(b); }
        public void Bytes(ReadOnlySpan<byte> v) => _out.Write(v);
        public byte[] ToArray() => _out.ToArray();
    }
}

/// <summary>A tunnelled vendor command. A SET carries its payload and
/// <see cref="WLength"/> counts it; a GET carries none and
/// <see cref="WLength"/> is the number of bytes requested.</summary>
public sealed record LinkCmdRequest : LinkFrame
{
    public LinkCmdRequest(ushort tag, byte handle, LinkDirection direction, byte bRequest,
        ushort wValue = 0, ushort wIndex = 0, ushort wLength = 0, byte[]? payload = null)
    {
        Tag = tag;
        Handle = handle;
        Direction = direction;
        BRequest = bRequest;
        WValue = wValue;
        WIndex = wIndex;
        WLength = wLength;
        Payload = payload ?? Array.Empty<byte>();
    }

    private readonly ushort _wLength;
    private readonly byte[] _payload = Array.Empty<byte>();

    public override LinkFrameType Type => LinkFrameType.CmdRequest;
    public ushort Tag { get; init; }
    public byte Handle { get; init; }
    public LinkDirection Direction { get; init; }
    public byte BRequest { get; init; }
    public ushort WValue { get; init; }
    public ushort WIndex { get; init; }

    /// <summary>SET: the payload's length, always (a value set by hand is
    /// ignored). GET: the number of bytes requested.</summary>
    public ushort WLength
    {
        get => Direction == LinkDirection.Set ? Length(_payload.Length) : _wLength;
        init => _wLength = value;
    }

    /// <summary>SET: the bytes to write. GET: always empty.</summary>
    public byte[] Payload
    {
        get => Direction == LinkDirection.Set ? _payload : Array.Empty<byte>();
        init => _payload = value ?? Array.Empty<byte>();
    }

    private protected override void Write(Writer w)
    {
        w.U16(Tag);
        w.U8(Handle);
        w.U8((byte)Direction);
        w.U8(BRequest);
        w.U8(0); // reserved
        w.U16(WValue);
        w.U16(WIndex);
        w.U16(WLength);
        if (Direction == LinkDirection.Set) w.Bytes(Payload);
    }
}

public sealed record LinkCmdResponse(ushort Tag, LinkStatus Status, byte[] Payload) : LinkFrame
{
    public LinkCmdResponse(ushort tag, LinkStatus status) : this(tag, status, Array.Empty<byte>()) { }

    public override LinkFrameType Type => LinkFrameType.CmdResponse;

    private protected override void Write(Writer w)
    {
        w.U16(Tag);
        w.U8((byte)Status);
        w.U8(0); // reserved
        w.U16(Length(Payload.Length));
        w.Bytes(Payload);
    }
}

/// <summary>A device notification. <see cref="Tag"/> is the Link sequence
/// number, per session: a gap means the session missed events and must re-read
/// state (it is not the firmware's own seq). <see cref="Packet"/> is the
/// verbatim v2 notification packet, first byte 0x02.</summary>
public sealed record LinkNotifyFrame(ushort Tag, byte Handle, ushort Origin, byte[] Packet) : LinkFrame
{
    public override LinkFrameType Type => LinkFrameType.Notify;

    private protected override void Write(Writer w)
    {
        w.U16(Tag);
        w.U8(Handle);
        w.U8(0); // reserved
        w.U16(Origin);
        w.Bytes(Packet);
    }
}

/// <summary>A subscribed poll's result: the verbatim response of its GET.</summary>
public sealed record LinkPollFrame(ushort Tag, byte Handle, byte Slot, byte[] Payload) : LinkFrame
{
    public override LinkFrameType Type => LinkFrameType.Poll;

    private protected override void Write(Writer w)
    {
        w.U16(Tag);
        w.U8(Handle);
        w.U8(Slot);
        w.U16(Length(Payload.Length));
        w.Bytes(Payload);
    }
}

/// <summary>A firmware chunk. <see cref="Tag"/> is the xfer id from the
/// fw.install reply.</summary>
public sealed record LinkFwDataFrame(ushort Tag, uint Offset, byte[] Data) : LinkFrame
{
    public override LinkFrameType Type => LinkFrameType.FwData;

    private protected override void Write(Writer w)
    {
        w.U16(Tag);
        w.U32(Offset);
        w.Bytes(Data);
    }
}

public sealed record LinkFwDataAck(ushort Tag, uint Offset, LinkStatus Status) : LinkFrame
{
    public override LinkFrameType Type => LinkFrameType.FwDataAck;

    private protected override void Write(Writer w)
    {
        w.U16(Tag);
        w.U32(Offset);
        w.U8((byte)Status);
    }
}

public enum LinkResyncReason : byte
{
    NotificationsDropped = 0,
    DeviceReattached = 1,
    CacheRebuilt = 2,
}

/// <summary>The hub telling a session to re-read a device's state.
/// <see cref="Reason"/> stays a raw byte so an unknown reason still decodes.</summary>
public sealed record LinkResyncFrame(byte Handle, byte Reason) : LinkFrame
{
    public override LinkFrameType Type => LinkFrameType.Resync;

    public LinkResyncReason? KnownReason => Reason <= 2 ? (LinkResyncReason)Reason : null;

    /// <summary>What to act on: an unknown reason is treated as reason 0,
    /// re-read state (spec 8.6).</summary>
    public LinkResyncReason EffectiveReason => KnownReason ?? LinkResyncReason.NotificationsDropped;

    private protected override void Write(Writer w)
    {
        w.U16(0); // tag unused
        w.U8(Handle);
        w.U8(Reason);
    }
}

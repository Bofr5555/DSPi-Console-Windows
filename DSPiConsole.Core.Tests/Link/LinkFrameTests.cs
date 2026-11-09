using DSPiConsole.Core.Link;

namespace DSPiConsole.Core.Tests.Link;

/// <summary>Byte-exact tests for the DSPi Link binary data plane (spec section
/// 8), including the worked examples of section 11. These are the bytes every
/// other Link implementation has to agree with, so they are asserted literally.
/// Ported from the macOS Console's LinkFrameTests.</summary>
public class LinkFrameTests
{
    internal static byte[] Bytes(string hex) =>
        hex.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(p => Convert.ToByte(p, 16)).ToArray();

    private static string Hex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2")));

    private static T Decode<T>(byte[] wire) where T : LinkFrame => Assert.IsType<T>(LinkFrame.Decode(wire));

    private static void AssertRoundTrips(LinkFrame frame)
    {
        var wire = frame.Encode();
        var back = LinkFrame.Decode(wire);
        Assert.Equal(frame.GetType(), back.GetType());
        Assert.Equal(Hex(wire), Hex(back.Encode()));
    }

    // ── Worked example 11.1, set master volume ──

    [Fact]
    public void WorkedExampleSetMasterVolumeRequest()
    {
        var wire = Bytes("01 00 07 00 00 00 D2 00 00 00 02 00 04 00 00 00 A0 C1");
        var f = Decode<LinkCmdRequest>(wire);
        Assert.Equal(7, f.Tag);
        Assert.Equal(0, f.Handle);
        Assert.Equal(LinkDirection.Set, f.Direction);
        Assert.Equal(0xD2, f.BRequest);
        Assert.Equal(0, f.WValue);
        Assert.Equal(2, f.WIndex);
        Assert.Equal(4, f.WLength);
        Assert.Equal(Bytes("00 00 A0 C1"), f.Payload);
        Assert.Equal(-20.0f, BitConverter.ToSingle(f.Payload));
        Assert.Equal(Hex(wire), Hex(f.Encode()));
    }

    [Fact]
    public void WorkedExampleSetMasterVolumeResponse()
    {
        var wire = Bytes("81 00 07 00 00 00 00 00");
        var f = Decode<LinkCmdResponse>(wire);
        Assert.Equal(7, f.Tag);
        Assert.Equal(LinkStatus.Ok, f.Status);
        Assert.Empty(f.Payload);
        Assert.Equal(Hex(wire), Hex(f.Encode()));
    }

    // ── Worked example 11.2, read the platform ──

    [Fact]
    public void WorkedExampleGetPlatform()
    {
        var request = Bytes("01 00 08 00 01 01 7F 00 00 00 02 00 07 00");
        var req = Decode<LinkCmdRequest>(request);
        Assert.Equal(8, req.Tag);
        Assert.Equal(1, req.Handle);
        Assert.Equal(LinkDirection.Get, req.Direction);
        Assert.Equal(0x7F, req.BRequest);
        Assert.Equal(2, req.WIndex);
        Assert.Equal(7, req.WLength);
        Assert.Empty(req.Payload); // a GET carries no payload
        Assert.Equal(Hex(request), Hex(req.Encode()));

        var response = Bytes("81 00 08 00 00 00 07 00 01 01 17 09 01 07 00");
        var resp = Decode<LinkCmdResponse>(response);
        Assert.Equal(8, resp.Tag);
        Assert.Equal(LinkStatus.Ok, resp.Status);
        Assert.Equal(Bytes("01 01 17 09 01 07 00"), resp.Payload);
        // RP2350, firmware 1.1.7 final, 9 outputs.
        var (platform, version) = DSPiConsole.Core.Models.FirmwareVersion.FromPlatformReply(resp.Payload)!.Value;
        Assert.Equal(1, platform);
        Assert.Equal(new DSPiConsole.Core.Models.FirmwareVersion(1, 1, 7, 0), version);
        Assert.Equal(Hex(response), Hex(resp.Encode()));
    }

    /// <summary>Section 11.3: one tick of a peak-meter subscription on slot 0,
    /// the 41-byte 17-channel status frame.</summary>
    [Fact]
    public void WorkedExamplePollFrame()
    {
        var payload = Enumerable.Range(0, 41).Select(i => (byte)i).ToArray();
        var wire = Bytes("03 00 2A 00 00 00 29 00").Concat(payload).ToArray();
        var f = Decode<LinkPollFrame>(wire);
        Assert.Equal(0x002A, f.Tag);
        Assert.Equal(0, f.Handle);
        Assert.Equal(0, f.Slot);
        Assert.Equal(payload, f.Payload);
        Assert.Equal(wire, f.Encode());
    }

    // ── Round trips ──

    [Fact]
    public void CmdRequestSetCountsItsPayload()
    {
        var f = new LinkCmdRequest(0x1234, 3, LinkDirection.Set, 0x42, wValue: 0x0102, wIndex: 2, payload: new byte[] { 1, 2, 3, 4, 5 });
        Assert.Equal(5, f.WLength);
        AssertRoundTrips(f);
    }

    /// <summary>A SET whose wLength was set by hand still ships the real
    /// payload length, so a hub never reads past the frame.</summary>
    [Fact]
    public void CmdRequestSetIgnoresALyingLength()
    {
        var f = new LinkCmdRequest(1, 0, LinkDirection.Set, 0x42, payload: new byte[] { 1, 2 }) { WLength = 99 };
        var back = Decode<LinkCmdRequest>(f.Encode());
        Assert.Equal(2, back.WLength);
        Assert.Equal(new byte[] { 1, 2 }, back.Payload);
    }

    [Fact]
    public void CmdRequestGetRoundTrips()
    {
        var f = new LinkCmdRequest(9, 1, LinkDirection.Get, 0x50, wValue: 9, wIndex: 2, wLength: 27);
        var back = Decode<LinkCmdRequest>(f.Encode());
        Assert.Equal(27, back.WLength);
        AssertRoundTrips(f);
    }

    [Fact]
    public void CmdResponseRoundTripCarriesStatus()
    {
        foreach (var status in new[] { LinkStatus.Ok, LinkStatus.Busy, LinkStatus.Denied, LinkStatus.Locked, LinkStatus.BadFrame })
        {
            var f = new LinkCmdResponse(5, status, status == LinkStatus.Ok ? new byte[] { 7, 8 } : Array.Empty<byte>());
            Assert.Equal(status, Decode<LinkCmdResponse>(f.Encode()).Status);
            AssertRoundTrips(f);
        }
    }

    [Fact]
    public void NotifyRoundTrips()
    {
        // A v2 PARAM_CHANGED-shaped packet: version byte first, verbatim.
        var packet = new byte[] { 0x02, 0x02, 0x00, 0x11, 0x00, 0x00, 0x00, 0x00, 0x01 };
        var f = new LinkNotifyFrame(0xBEEF, 2, 12, packet);
        var back = Decode<LinkNotifyFrame>(f.Encode());
        Assert.Equal(packet, back.Packet);
        Assert.Equal(12, back.Origin);
        Assert.Equal(0xBEEF, back.Tag);
    }

    [Fact]
    public void PollRoundTrips() => AssertRoundTrips(new LinkPollFrame(77, 0, 15, Enumerable.Repeat((byte)0xA5, 80).ToArray()));

    [Fact]
    public void FwDataRoundTrips()
    {
        var f = new LinkFwDataFrame(3, 0x0001_0000, Enumerable.Repeat((byte)0x5A, 256).ToArray());
        var back = Decode<LinkFwDataFrame>(f.Encode());
        Assert.Equal(0x0001_0000u, back.Offset);
        Assert.Equal(f.Data, back.Data);
    }

    [Fact]
    public void FwDataAckRoundTrips()
    {
        var f = new LinkFwDataAck(3, 0x0001_0000, LinkStatus.Ok);
        var wire = f.Encode();
        Assert.Equal(9, wire.Length); // header, offset, status
        Assert.Equal(f, Decode<LinkFwDataAck>(wire));
    }

    [Fact]
    public void ResyncRoundTrips()
    {
        var f = new LinkResyncFrame(1, (byte)LinkResyncReason.DeviceReattached);
        var wire = f.Encode();
        Assert.Equal(new byte[] { 0x05, 0x00, 0x00, 0x00, 0x01, 0x01 }, wire);
        var back = Decode<LinkResyncFrame>(wire);
        Assert.Equal(f, back);
        Assert.Equal(LinkResyncReason.DeviceReattached, back.KnownReason);
    }

    /// <summary>A reason from a later minor version still decodes; only its
    /// meaning is unknown.</summary>
    [Fact]
    public void ResyncKeepsAnUnknownReason()
    {
        var f = Decode<LinkResyncFrame>(new byte[] { 0x05, 0x00, 0x00, 0x00, 0x02, 0x63 });
        Assert.Equal(0x63, f.Reason);
        Assert.Null(f.KnownReason);
        // Spec 8.6: an unknown reason is acted on as reason 0, re-read state.
        Assert.Equal(LinkResyncReason.NotificationsDropped, f.EffectiveReason);
    }

    [Fact]
    public void EveryFrameTypeRoundTrips()
    {
        LinkFrame[] frames =
        [
            new LinkCmdRequest(1, 0, LinkDirection.Set, 0xD2, wIndex: 2, payload: new byte[] { 0, 0, 0xA0, 0xC1 }),
            new LinkCmdResponse(1, LinkStatus.Ok),
            new LinkNotifyFrame(2, 0, 4, new byte[] { 0x02, 0x01 }),
            new LinkPollFrame(3, 0, 1, new byte[] { 9 }),
            new LinkFwDataFrame(4, 8, new byte[] { 1, 2, 3 }),
            new LinkFwDataAck(4, 8, LinkStatus.Ok),
            new LinkResyncFrame(0, 0),
        ];
        Assert.Equal(7, frames.Select(f => f.Type).Distinct().Count());
        foreach (var frame in frames) AssertRoundTrips(frame);
    }

    // ── Flags and reserved bytes ──

    [Fact]
    public void ReservedBytesAreZeroAndIgnored()
    {
        var f = new LinkCmdRequest(1, 0, LinkDirection.Get, 0x7F, wLength: 6);
        var wire = f.Encode();
        Assert.Equal(0, wire[1]); // flags are sent as 0
        Assert.Equal(0, wire[7]); // the byte after bRequest is reserved

        var noisy = (byte[])wire.Clone();
        noisy[1] = 0xFF;
        noisy[7] = 0xFF;
        Assert.Equal(Hex(wire), Hex(LinkFrame.Decode(noisy).Encode()));
    }

    // ── Errors ──

    private static LinkFrameException Fails(byte[] wire) => Assert.Throws<LinkFrameException>(() => LinkFrame.Decode(wire));

    [Fact]
    public void AnUnknownFrameTypeIsADistinctError()
    {
        foreach (byte raw in new byte[] { 0x00, 0x06, 0x7F, 0x85, 0xFF })
        {
            var e = Fails(new byte[] { raw, 0, 0, 0, 0, 0 });
            Assert.Equal(LinkFrameErrorKind.UnknownType, e.Kind);
            Assert.Equal(raw, e.Value);
        }
    }

    [Fact]
    public void ATruncatedHeaderFails()
    {
        var header = new byte[] { 0x01, 0x00, 0x07, 0x00 };
        for (int count = 0; count < 4; count++)
            Assert.Equal(LinkFrameErrorKind.Truncated, Fails(header[..count]).Kind);
    }

    [Fact]
    public void ATruncatedBodyFails()
    {
        var full = Bytes("01 00 07 00 00 00 D2 00 00 00 02 00 04 00 00 00 A0 C1");
        for (int count = 4; count < full.Length; count++)
            Assert.Equal(LinkFrameErrorKind.Truncated, Fails(full[..count]).Kind);
    }

    [Fact]
    public void ATruncatedResponsePayloadFails() =>
        // length says 6, only 2 bytes follow.
        Assert.Equal(LinkFrameErrorKind.Truncated, Fails(Bytes("81 00 08 00 00 00 06 00 01 01")).Kind);

    /// <summary>A bridge forwards the firmware's status verbatim and both
    /// tables may grow (spec 9.2, 10.5): an unknown status still delivers the
    /// response, so its request completes.</summary>
    [Fact]
    public void AnUnknownStatusStillDecodes()
    {
        var f = Decode<LinkCmdResponse>(Bytes("81 00 07 00 88 00 00 00"));
        Assert.Equal(7, f.Tag);
        Assert.Equal(0x88, (byte)f.Status);
        Assert.False(LinkStatusExtensions.IsDefined((byte)f.Status));
        Assert.Equal(0x08, (byte)Decode<LinkFwDataAck>(Bytes("84 00 03 00 00 00 00 00 08")).Status);
    }

    [Fact]
    public void AnUnknownDirectionFails()
    {
        var direction = Fails(Bytes("01 00 01 00 00 09 42 00 00 00 02 00 00 00"));
        Assert.Equal(LinkFrameErrorKind.UnknownDirection, direction.Kind);
        Assert.Equal(0x09, direction.Value);
    }

    /// <summary>A hub answers an unknown request type with UNSUPPORTED, and a
    /// malformed frame with BAD_FRAME, under the sender's tag (spec 8, 8.1).</summary>
    [Fact]
    public void ADecodeFailureKeepsTheTag()
    {
        var unknown = Fails(Bytes("06 00 34 12 00"));
        Assert.Equal(LinkFrameErrorKind.UnknownType, unknown.Kind);
        Assert.Equal((byte)0x06, unknown.FrameType);
        Assert.Equal((ushort)0x1234, unknown.Tag);

        var direction = Fails(Bytes("01 00 07 00 00 09 42 00 00 00 02 00 00 00"));
        Assert.Equal((ushort)7, direction.Tag);
        Assert.Equal((byte)0x01, direction.FrameType);

        var truncated = Fails(Bytes("01 00 07 00 00 00 D2"));
        Assert.Equal(LinkFrameErrorKind.Truncated, truncated.Kind);
        Assert.Equal((ushort)7, truncated.Tag);

        Assert.Null(Fails(Bytes("01 00 07")).Tag); // no complete header, no tag
    }

    /// <summary>u16 length fields: a longer payload can't be framed, and a
    /// wrapped length would desynchronise the receiver.</summary>
    [Fact]
    public void AnOversizedPayloadRefusesToEncode()
    {
        Assert.Throws<InvalidOperationException>(() => new LinkCmdResponse(1, LinkStatus.Ok, new byte[65536]).Encode());
        Assert.Throws<InvalidOperationException>(() => new LinkPollFrame(1, 0, 0, new byte[70000]).Encode());
        Assert.Throws<InvalidOperationException>(() => new LinkCmdRequest(1, 0, LinkDirection.Set, 0x42, payload: new byte[70000]).Encode());
        Assert.Equal(65535 + 8, new LinkCmdResponse(1, LinkStatus.Ok, new byte[65535]).Encode().Length);
    }

    /// <summary>`with` can't leave a request whose fields disagree with what it encodes.</summary>
    [Fact]
    public void ACopiedRequestStaysConsistent()
    {
        var get = new LinkCmdRequest(1, 0, LinkDirection.Get, 0x43, wLength: 64);
        var asSet = get with { Direction = LinkDirection.Set };
        Assert.Equal(0, asSet.WLength);
        Assert.Equal(0, Decode<LinkCmdRequest>(asSet.Encode()).WLength);

        var set = new LinkCmdRequest(1, 0, LinkDirection.Set, 0x42, payload: new byte[] { 9 });
        var bigger = set with { Payload = new byte[] { 1, 2, 3, 4 } };
        Assert.Equal(4, bigger.WLength);
        var asGet = set with { Direction = LinkDirection.Get, WLength = 12 };
        Assert.Empty(asGet.Payload);
        Assert.Equal(12, asGet.WLength);
        Assert.Empty((set with { Payload = null! }).Payload);
    }

    /// <summary>A WebSocket read commonly hands out part of a larger buffer.</summary>
    [Fact]
    public void DecodesFromASliceOfALargerBuffer()
    {
        var padded = new byte[] { 0xEE, 0xEE, 0xEE }.Concat(Bytes("81 00 07 00 00 00 00 00")).ToArray();
        var f = Assert.IsType<LinkCmdResponse>(LinkFrame.Decode(padded.AsSpan(3)));
        Assert.Equal(7, f.Tag);
        Assert.Equal(LinkStatus.Ok, f.Status);
    }

    [Fact]
    public void TypeAndDirectionValues()
    {
        Assert.Equal(0x01, (byte)LinkFrameType.CmdRequest);
        Assert.Equal(0x81, (byte)LinkFrameType.CmdResponse);
        Assert.Equal(0x02, (byte)LinkFrameType.Notify);
        Assert.Equal(0x03, (byte)LinkFrameType.Poll);
        Assert.Equal(0x04, (byte)LinkFrameType.FwData);
        Assert.Equal(0x84, (byte)LinkFrameType.FwDataAck);
        Assert.Equal(0x05, (byte)LinkFrameType.Resync);
        Assert.Equal(0, (byte)LinkDirection.Set);
        Assert.Equal(1, (byte)LinkDirection.Get);
    }

    [Fact]
    public void RetryableStatuses()
    {
        Assert.True(LinkStatus.RateLimited.IsRetryable());
        Assert.True(LinkStatus.Busy.IsRetryable());
        Assert.True(LinkStatus.CrcError.IsRetryable());
        // Spec 8.1: a TIMEOUT "may or may not have executed", so it is never retried blindly.
        Assert.False(LinkStatus.Timeout.IsRetryable());
        Assert.False(LinkStatus.Denied.IsRetryable());
        Assert.False(LinkStatus.Locked.IsRetryable());
        Assert.True(LinkStatusExtensions.IsDefined(0x87));
        Assert.False(LinkStatusExtensions.IsDefined(0x08));
        Assert.False(LinkStatusExtensions.IsDefined(0x88));
    }
}

namespace DSPiConsole.Core.Link;

/// <summary>
/// One notification packet from a device, as a transport delivered it, with
/// the session it is attributed to. Over USB every packet the endpoint yields
/// arrives (IDLE keep-alives and v1 packets included, for the Interrupt
/// Monitor); a hub relays only v2 packets. Port of the macOS Console's
/// LinkNotification.
/// </summary>
public sealed record LinkNotification(byte[] Packet, ushort Origin, DateTime ReceivedAt)
{
    /// <summary>Origin 0: unattributed (a knob turn, the OS volume slider, a
    /// UART host, or a hub that hasn't told us our id). Never our own.</summary>
    public const ushort Unattributed = 0;

    public byte Version => Packet.Length > 0 ? Packet[0] : (byte)0;
    public byte EventId => Packet.Length > 1 ? Packet[1] : (byte)0;
}

/// <summary>Which notifications a hub can attribute to one of its sessions
/// (spec 8.3, 7.15).</summary>
public static class LinkNotifyAttribution
{
    /// <summary>
    /// True when the firmware tags the change as a host or bulk write
    /// (PARAM_SRC_HOST_SET 1, PARAM_SRC_BULK_SET 2): the only kind that came
    /// from a session. The source byte sits where each event puts it:
    /// PARAM_CHANGED and CS_AUX at 8, BULK_INVALIDATED at 4, RTA_CONFIG at 16,
    /// the input-source-name and device-name events at 5.
    /// </summary>
    public static bool IsHostSourced(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2 || packet[0] != 0x02) return false;
        int offset = packet[1] switch
        {
            0x02 or 0x0C => 8,
            0x03 => 4,
            0x0D => 16,
            0x0F or 0x10 => 5,
            _ => -1,
        };
        if (offset < 0 || packet.Length <= offset) return false;
        return packet[offset] is 1 or 2;
    }
}

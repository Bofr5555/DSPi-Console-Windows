namespace DSPiConsole.Usb.Transport;

/// <summary>How the WireBulkParams blob is read on each kind of link.</summary>
public static class BulkRead
{
    /// <summary>Under WinUSB's 4096-byte control-transfer cap; three transfers at V32.</summary>
    public const int ChunkSize = 2048;

    /// <summary>
    /// The chunked read over USB: REQ_GET_ALL_PARAMS_CHUNK (0xA2) at offset 0
    /// makes the device snapshot the whole struct and returns its start, whose
    /// header gives the length; the rest follows in order. The firmware reaps
    /// the chunk session if any other vendor request interleaves, so the
    /// caller runs this as one exclusive section of the link. A STALL or empty
    /// read aborts with null; the caller retries the whole fetch.
    /// <paramref name="read"/> is a GET (request, wValue, length) returning
    /// null on failure.
    /// </summary>
    public static byte[]? Chunked(Func<byte, ushort, int, byte[]?> read)
    {
        // First chunk (offset 0) opens the session and carries the header,
        // from which we read the authoritative payload length.
        var first = read(VendorCommands.GetAllParamsChunk, 0, ChunkSize);
        if (first == null || first.Length < 16)
            return null;

        int total = BitConverter.ToUInt16(first, 6); // WireHeader.payload_length
        if (total < BulkParamsParser.PacketSizeV20)
            total = BulkParamsParser.PacketSizeV20; // header underreport safety net

        var buffer = new byte[total];
        int offset = Math.Min(first.Length, total);
        Buffer.BlockCopy(first, 0, buffer, 0, offset);

        while (offset < total)
        {
            int want = Math.Min(ChunkSize, total - offset);
            var chunk = read(VendorCommands.GetAllParamsChunk, (ushort)offset, want);
            if (chunk == null || chunk.Length == 0)
                return null; // STALL/short → session lost; caller re-fetches
            int n = Math.Min(chunk.Length, total - offset);
            Buffer.BlockCopy(chunk, 0, buffer, offset, n);
            offset += n;
        }

        return buffer;
    }

    /// <summary>The largest blob a single REQ_GET_ALL_PARAMS (0xA0) is asked
    /// for over DSPi Link: a hub accepts at least 8192 (spec 7.2,
    /// limits.max_payload), and the device returns the actual length.</summary>
    public const int SingleShotLength = 8192;

    /// <summary>Whether <paramref name="blob"/> is a whole WireBulkParams: at
    /// least the header, and as long as the header says.</summary>
    public static bool IsComplete(byte[]? blob) =>
        blob is { Length: >= 16 } && blob.Length >= Math.Min(BitConverter.ToUInt16(blob, 6), (ushort)SingleShotLength);
}

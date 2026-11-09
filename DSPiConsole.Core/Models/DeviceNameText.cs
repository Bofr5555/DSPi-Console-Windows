using System.Text;

namespace DSPiConsole.Core.Models;

/// <summary>
/// The text rules shared by the input source names (REQ_SET/GET_INPUT_SOURCE_NAME
/// 0x54/0x55) and the board's own name (REQ_SET/GET_DEVICE_NAME 0x56/0x57),
/// firmware 1.1.6. Both are a 32-byte field holding at most 31 bytes and a NUL.
/// The firmware treats the bytes as opaque, but the front panel font covers only
/// characters 32-126, so hosts send printable ASCII. See the firmware's
/// input_source_names_spec.md and device_name_spec.md; port of the macOS
/// Console's sanitizeInputSourceName / decodeInputSourceName / boardName.
/// </summary>
public static class DeviceNameText
{
    /// <summary>The wire field: 31 characters plus a NUL.</summary>
    public const int FieldLength = 32;
    public const int MaxLength = FieldLength - 1;

    /// <summary>Source IDs that can carry a name today (InputSource 0..6). The
    /// firmware's table has 8 slots so a future source fits.</summary>
    public const int SourceSlots = 8;
    public const int MaxSourceId = 6;

    /// <summary>What a name field keeps as you type: printable ASCII, at most
    /// 31 characters, nothing else changed.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, MaxLength));
        foreach (char c in text)
        {
            if (c < 32 || c >= 127) continue;
            sb.Append(c);
            if (sb.Length == MaxLength) break;
        }
        return sb.ToString();
    }

    /// <summary>A name as the board stores it after a rename that did not come
    /// from a typed field (a hub rename, adopting a stored name): printable ASCII
    /// with the gaps left by dropped characters closed, ends trimmed, at most 31.</summary>
    public static string BoardName(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var ascii = new string(text.Where(c => c >= 32 && c < 127).ToArray());
        var joined = string.Join(' ', ascii.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return (joined.Length > MaxLength ? joined[..MaxLength] : joined).Trim();
    }

    /// <summary>A stored name as shown: up to the first NUL, as ASCII. An
    /// all-zero field is the empty string, meaning unnamed.</summary>
    public static string Decode(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0) end = field.Length;
        return Encoding.ASCII.GetString(field[..end]);
    }

    /// <summary>The 32-byte payload for a SET: the sanitized name, NUL-padded.
    /// An empty name is all zeros, which clears it.</summary>
    public static byte[] Encode(string? name)
    {
        var payload = new byte[FieldLength];
        var ascii = Encoding.ASCII.GetBytes(Sanitize(name));
        ascii.CopyTo(payload, 0);
        return payload;
    }
}

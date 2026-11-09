namespace DSPiConsole.Core.Link;

/// <summary>
/// Status of one tunnelled command. 0x00-0x07 are the firmware's CTRL_STATUS_*
/// codes and must never diverge from them; 0x80 and above are Link-level
/// (spec section 8.1). Port of the macOS Console's LinkStatus.
/// </summary>
public enum LinkStatus : byte
{
    Ok = 0x00,
    Busy = 0x01,
    Error = 0x02,
    Blocked = 0x03,
    BulkLocked = 0x04,
    CrcError = 0x05,
    Oversize = 0x06,
    FrameError = 0x07,
    NoDevice = 0x80,
    Denied = 0x81,
    Timeout = 0x82,
    TooLarge = 0x83,
    RateLimited = 0x84,
    Locked = 0x85,
    Unsupported = 0x86,
    BadFrame = 0x87,
}

public static class LinkStatusExtensions
{
    /// <summary>Whether <paramref name="value"/> is a status this build knows.</summary>
    public static bool IsDefined(byte value) => value <= 0x07 || value is >= 0x80 and <= 0x87;

    /// <summary>The statuses a caller should simply retry after a short wait:
    /// the command certainly did not run. Never TIMEOUT, which "may or may not
    /// have executed" (spec 8.1): retrying it could apply a preset load or a
    /// save twice. (The Mac's isRetryable includes it; that is a bug there.)</summary>
    public static bool IsRetryable(this LinkStatus status) => status is LinkStatus.Busy or LinkStatus.BulkLocked
        or LinkStatus.CrcError or LinkStatus.RateLimited;
}

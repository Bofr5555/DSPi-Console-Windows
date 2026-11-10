namespace DSPiConsole.Core.Background;

/// <summary>The words the notification-area icon shows: its menu's status
/// lines and its tooltip. The macOS Console's menu bar wording.</summary>
public static class TrayStatus
{
    /// <summary>"DSPi connected, sharing, 2 clients".</summary>
    public static string Line(bool deviceConnected, bool sharing, int clients)
    {
        var device = deviceConnected ? "DSPi connected" : "No DSPi connected";
        if (!sharing) return $"{device}, not sharing";
        return $"{device}, sharing, {(clients == 1 ? "1 client" : $"{clients} clients")}";
    }

    /// <summary>The tooltip: the app's name and the status line, within the
    /// 127 characters a notification icon's tip holds.</summary>
    public static string Tooltip(string line)
    {
        var tip = $"DSPi Console\n{line}";
        return tip.Length <= 127 ? tip : tip[..127];
    }
}

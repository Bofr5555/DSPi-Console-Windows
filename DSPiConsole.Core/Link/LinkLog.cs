namespace DSPiConsole.Core.Link;

/// <summary>
/// A plain diagnostic log of DSPi Link activity (connection states, close
/// codes, reconnects, pairing outcomes, discovery), so a failure on a network
/// can be read back afterwards. Never holds a PIN or token. Off until a path
/// is set; trimmed when it grows past 1 MB.
/// </summary>
public static class LinkLog
{
    private static readonly object Lock = new();
    private const long MaxBytes = 1 << 20;

    /// <summary>Where to write; null (the default, and in tests) writes nothing.</summary>
    public static string? Path { get; set; }

    public static void Write(string message)
    {
        var path = Path;
        if (path == null) return;
        try
        {
            lock (Lock)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = path + ".1";
                    File.Move(path, old, overwrite: true);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

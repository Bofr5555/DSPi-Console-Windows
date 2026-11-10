namespace DSPiConsole.Core.Background;

/// <summary>
/// "Open at login" as Windows keeps it: a value under HKCU\…\Run naming the
/// program, which Task Manager's Startup apps can switch off separately
/// (StartupApproved\Run). The rules are here, apart from the registry, so
/// they can be tested.
/// </summary>
public static class LoginItem
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "DSPi Console";

    /// <summary>
    /// What to start at login. A release keeps the app in <c>app\</c> with the
    /// launcher stub alone beside it (the stub installs a staged update before
    /// starting the app), so the stub is named when it is there; a build run
    /// from its output folder names itself.
    /// </summary>
    public static string LaunchPath(string appExePath, Func<string, bool> fileExists)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(appExePath));
        if (dir != null && string.Equals(Path.GetFileName(dir), "app", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(dir) is { } root)
        {
            var stub = Path.Combine(root, Path.GetFileName(appExePath));
            if (fileExists(stub)) return stub;
        }
        return Path.GetFullPath(appExePath);
    }

    /// <summary>Marks a start at login, the only start that may leave the
    /// window hidden (the launcher stub passes it on).</summary>
    public const string Argument = "--background";

    /// <summary>The Run value: the quoted path and <see cref="Argument"/>.</summary>
    public static string Command(string launchPath) => $"\"{launchPath}\" {Argument}";

    /// <summary>Our value names this copy but isn't today's command (an older
    /// one, without the argument): it should be written again.</summary>
    public static bool IsOutdated(string? runValue, string launchPath) =>
        ProgramOf(runValue) is { } program
        && string.Equals(Path.GetFullPath(program), Path.GetFullPath(launchPath), StringComparison.OrdinalIgnoreCase)
        && !string.Equals(runValue!.Trim(), Command(launchPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>The program a Run value starts: the quoted path, or an unquoted
    /// one up to its first space before arguments. Null for an empty value.</summary>
    public static string? ProgramOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    /// <summary>Task Manager's switch for one entry: none recorded, or an even
    /// first byte (02, 06), is on; an odd one (03, 07) is off.</summary>
    public static bool IsApproved(byte[]? startupApproved) =>
        startupApproved is not { Length: > 0 } || (startupApproved[0] & 1) == 0;

    /// <summary>On, as the user sees it: our value is there, names this
    /// install, and Task Manager hasn't switched it off.</summary>
    public static bool IsOn(string? runValue, byte[]? startupApproved, string launchPath) =>
        ProgramOf(runValue) is { } program
        && string.Equals(Path.GetFullPath(program), Path.GetFullPath(launchPath), StringComparison.OrdinalIgnoreCase)
        && IsApproved(startupApproved);

    /// <summary>Our value names a program that's gone (the folder was moved or
    /// deleted): this copy takes the entry over. One naming another copy that
    /// still exists is left alone, so running a second copy (a build, an
    /// older release) doesn't steal it.</summary>
    public static bool IsStale(string? runValue, string launchPath, Func<string, bool> fileExists) =>
        ProgramOf(runValue) is { } program
        && !string.Equals(Path.GetFullPath(program), Path.GetFullPath(launchPath), StringComparison.OrdinalIgnoreCase)
        && !fileExists(program);
}

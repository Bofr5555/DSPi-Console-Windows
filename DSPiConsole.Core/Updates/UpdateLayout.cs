using System.Diagnostics;
using System.IO.Compression;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Updates;

/// <summary>An update step that failed, with a message fit to show.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Where an update is staged, and how. A release folder holds the launcher
/// stub (DSPiConsole.exe) alone at its root and the app in "app\". An update
/// is unpacked into "app.update\", checked, and its app moved to
/// "app.pending\" (with a changed stub as "DSPiConsole.exe.pending"). The app
/// never replaces itself: once it has exited, the stub renames "app.pending\"
/// over "app\" and starts it (Launcher/launcher.c). Every move stays inside
/// the release folder, so each is a rename. Settings live in
/// %LOCALAPPDATA%\DSPiConsole, so nothing of the user's is in "app\".
/// </summary>
public sealed class UpdateLayout
{
    public const string StubName = "DSPiConsole.exe";
    public const string AppFolder = "app";
    public const string PendingFolder = "app.pending";
    public const string WorkFolder = "app.update";
    /// <summary>Held open by the copy of the app using <see cref="WorkFolder"/>.</summary>
    public const string LockName = "app.update.lock";
    public const string PendingStubName = StubName + ".pending";
    /// <summary>The stub's argument: wait for this process id to exit, then install.</summary>
    public const string InstallArgument = "--install-update";
    /// <summary>With <see cref="InstallArgument"/>: install, but do not start the app.</summary>
    public const string NoLaunchArgument = "--no-launch";
    /// <summary>The stub's argument to the app: an update could not be swapped
    /// in, so do not hand it back (which would loop).</summary>
    public const string NoUpdateArgument = "--no-update";

    /// <summary>The release folder, or null when this app is not running from
    /// one (a development build, or files copied out on their own).</summary>
    public string? Root { get; }

    public UpdateLayout(string appDirectory)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
        var parent = Path.GetDirectoryName(dir);
        if (parent != null
            && string.Equals(Path.GetFileName(dir), AppFolder, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(parent, StubName)))
        {
            Root = parent;
        }
    }

    public bool IsReleaseLayout => Root != null;
    public string Stub => Path.Combine(RootOrThrow, StubName);
    public string PendingApp => Path.Combine(RootOrThrow, PendingFolder);
    public string PendingStub => Path.Combine(RootOrThrow, PendingStubName);
    public string WorkDirectory => Path.Combine(RootOrThrow, WorkFolder);
    public bool HasPending => Root != null && File.Exists(Path.Combine(PendingApp, StubName));

    private string RootOrThrow => Root ?? throw new InvalidOperationException("Not running from a release folder.");

    /// <summary>Whether this copy can be replaced in place: it runs from a
    /// release folder the user can write to (not Program Files, say).</summary>
    public bool CanReplace()
    {
        if (Root == null) return false;
        try
        {
            var probe = Path.Combine(Root, $".write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Claims the download area for this copy of the app until disposed; null
    /// when another copy holds it (or this is not a release folder). Two
    /// copies run from one folder share "app.update\", and one clearing it
    /// while the other unpacks could stage an incomplete app.
    /// </summary>
    public IDisposable? TryLock()
    {
        if (Root == null) return null;
        try
        {
            return new FileStream(Path.Combine(Root, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>As <see cref="Stage(byte[], FirmwareVersion, Func{string, FirmwareVersion?}?)"/>, from a file.</summary>
    public void Stage(string zipPath, FirmwareVersion expected, Func<string, FirmwareVersion?>? readVersion = null) =>
        Stage(File.ReadAllBytes(zipPath), expected, readVersion);

    /// <summary>
    /// Unpacks a verified release zip, checks it is DSPi Console
    /// <paramref name="expected"/> in the release layout, and stages it for
    /// the stub. Replaces anything staged before. Throws
    /// <see cref="UpdateException"/> when the zip is not what it should be.
    /// </summary>
    public void Stage(byte[] zip, FirmwareVersion expected, Func<string, FirmwareVersion?>? readVersion = null)
    {
        readVersion ??= ReadExeVersion;
        // A fresh folder each time, so nothing left from an earlier attempt
        // can end up in the update.
        var unpacked = Path.Combine(WorkDirectory, $"unpacked-{Guid.NewGuid():N}");
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
            archive.ExtractToDirectory(unpacked);
            // Everything in the zip must have arrived, whole.
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                var file = new FileInfo(Path.Combine(unpacked, entry.FullName.Replace('\\', '/')));
                if (!file.Exists || file.Length != entry.Length)
                    throw new UpdateException("The download couldn't be unpacked completely.");
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException("The download couldn't be unpacked.");
        }

        var payload = FindPayload(unpacked)
            ?? throw new UpdateException("The download isn't a DSPi Console release folder.");
        var newApp = Path.Combine(payload, AppFolder);
        var version = readVersion(Path.Combine(newApp, StubName));
        if (version != expected)
            throw new UpdateException($"The download isn't DSPi Console {expected} (it reports {version?.ToString() ?? "no version"}).");

        DiscardPending();
        Directory.Move(newApp, PendingApp);
        var newStub = Path.Combine(payload, StubName);
        try
        {
            // The stub is replaced only when it changed: most releases leave it be.
            if (!FilesEqual(newStub, Stub)) File.Move(newStub, PendingStub);
        }
        catch
        {
            DiscardPending();
            throw;
        }
    }

    /// <summary>The staged app's version, or null when none is staged.</summary>
    public FirmwareVersion? PendingVersion(Func<string, FirmwareVersion?>? readVersion = null) =>
        HasPending ? (readVersion ?? ReadExeVersion)(Path.Combine(PendingApp, StubName)) : null;

    /// <summary>Drops a staged update. The folder is renamed away before it is
    /// deleted, so a delete that stops partway can never leave a broken
    /// "app.pending\" for the stub to install.</summary>
    public void DiscardPending()
    {
        if (Root == null) return;
        if (Directory.Exists(PendingApp))
        {
            var doomed = Path.Combine(WorkDirectory, $"discarded-{Guid.NewGuid():N}");
            Directory.CreateDirectory(WorkDirectory);
            Directory.Move(PendingApp, doomed);
            DeleteTree(doomed);
        }
        if (File.Exists(PendingStub)) File.Delete(PendingStub);
    }

    /// <summary>Removes the download area, best effort.</summary>
    public void CleanWork()
    {
        if (Root != null) DeleteTree(WorkDirectory);
    }

    /// <summary>The folder holding the stub and "app\": the zip's root, or the
    /// one folder in it when it was zipped with its parent.</summary>
    private static string? FindPayload(string unpacked)
    {
        static bool IsPayload(string dir) =>
            File.Exists(Path.Combine(dir, StubName)) && File.Exists(Path.Combine(dir, AppFolder, StubName));
        if (IsPayload(unpacked)) return unpacked;
        var dirs = Directory.GetDirectories(unpacked);
        return dirs.Length == 1 && IsPayload(dirs[0]) ? dirs[0] : null;
    }

    /// <summary>The version an executable carries (its informational version,
    /// "1.1.6-beta4+commit"), or null when it has none.</summary>
    public static FirmwareVersion? ReadExeVersion(string path)
    {
        try { return FirmwareVersion.Parse(FileVersionInfo.GetVersionInfo(path).ProductVersion); }
        catch (FileNotFoundException) { return null; }
    }

    private static bool FilesEqual(string a, string b)
    {
        if (!File.Exists(b)) return false;
        var x = new FileInfo(a);
        var y = new FileInfo(b);
        if (x.Length != y.Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

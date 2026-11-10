namespace DSPiConsole.Usb.Transport;

/// <summary>
/// The device lock as the user sees it (spec 7.7): one wording for "another
/// session holds the device", posted by the transports whenever a command or
/// a flow's lock request is refused, so no command needs its own error path.
/// The status row shows it for a few seconds after the last refusal.
/// </summary>
public static class LinkLockNotice
{
    public const string Message = "Another console is applying changes";

    /// <summary>Hubs cap a lock at 60 s (spec 7.7).</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Raised on whatever thread saw the refusal.</summary>
    public static event EventHandler? Posted;

    public static void Post() => Posted?.Invoke(null, EventArgs.Empty);
}

/// <summary>A flow that takes the device lock (spec 7.7): its reason as the
/// hub shows it, and how long the hub should hold it at most.</summary>
public sealed record LinkLockFlow(string Reason, TimeSpan Timeout)
{
    public static readonly LinkLockFlow ApplyingConfiguration = new("Applying configuration", TimeSpan.FromSeconds(60));
    public static readonly LinkLockFlow SavingPreset = new("Saving preset", TimeSpan.FromSeconds(5));
    public static readonly LinkLockFlow CopyingPreset = new("Copying preset", TimeSpan.FromSeconds(10));
    public static readonly LinkLockFlow SavingOutputConfiguration = new("Saving output configuration", TimeSpan.FromSeconds(5));
    public static readonly LinkLockFlow SavingToFlash = new("Saving to flash", TimeSpan.FromSeconds(5));
    public static readonly LinkLockFlow LearningIrCode = new("Learning an IR code", TimeSpan.FromSeconds(15));
    public static readonly LinkLockFlow InstallingFirmware = new("Installing firmware", TimeSpan.FromSeconds(60));
}

using System.Text.Json;

namespace DSPiConsole.Models;

public class AppSettings
{
    private static AppSettings? _instance;
    public static AppSettings Instance => _instance ??= Load();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DSPiConsole", "settings.json");

    public bool ShowGraphGlow { get; set; } = false;

    // ── Onboarding (Services/Onboarding.cs) and What's New ──
    // Null means never written, which is how a first onboarding launch is told
    // apart from a returning user.
    public List<string>? OnboardingCompletedStepIds { get; set; }
    public string? OnboardingLastSeenVersion { get; set; }
    public bool? OnboardingTourDeclined { get; set; }
    public DateTime? OnboardingFirstLaunch { get; set; }
    public bool OnboardingSimulateFreshInstall { get; set; }
    /// <summary>Developer override, applied once: "fresh", "existing",
    /// "declined" or "updater:1.1.6".</summary>
    public string? OnboardingCohortOverride { get; set; }
    public bool OnboardingForceWizard { get; set; }
    public string? WhatsNewLastShownVersion { get; set; }

    // ── DSPi Link (Services/LinkNetwork.cs) ──
    /// <summary>"Find hubs automatically": DNS-SD browsing for other hubs.</summary>
    public bool LinkLookForHubs { get; set; } = true;
    /// <summary>Hubs added by address ("host", "host:port", "[v6]:port").</summary>
    public List<string> LinkManualHubs { get; set; } = new();
    /// <summary>"Share Devices on This Network": this PC as a hub (L3). Off
    /// until the user turns it on.</summary>
    public bool LinkSharingEnabled { get; set; }
    /// <summary>The hub's TCP port (1024-65535).</summary>
    public int LinkSharingPort { get; set; } = Core.Link.LinkService.DefaultPort;

    // ── Running in the background (Services/BackgroundMode.cs) ──
    /// <summary>"Show in the notification area": an icon there, and closing
    /// the window keeps the app running (the Mac's "Show in the menu bar").</summary>
    public bool ShowInNotificationArea { get; set; }
    /// <summary>"Start in the notification area only": launch with the window
    /// hidden, when the icon is shown.</summary>
    public bool StartInNotificationArea { get; set; }
    /// <summary>"Keep this PC awake while sharing": holds off idle sleep while
    /// the hub is serving.</summary>
    public bool KeepAwakeWhileSharing { get; set; }

    // ── Software update (Services/AppUpdater.cs) ──
    public bool CheckForUpdatesAutomatically { get; set; } = true;
    /// <summary>The release tag the user chose to skip; the automatic check
    /// does not offer it again.</summary>
    public string? UpdateSkippedTag { get; set; }
    public DateTime? UpdateLastChecked { get; set; }
    /// <summary>Developer override for the release list, for trying the
    /// installer against a local feed. Safe: an install still needs a zip
    /// signed with the release key.</summary>
    public string? UpdateFeedOverride { get; set; }

    /// <summary>Whether this run found a settings file: the app had been used
    /// before. Read once at launch to tell an existing user from a new one.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LoadedFromFile { get; private set; }

    /// <summary>The Tube Modeller window shows every control (Advanced) rather
    /// than the tube, drive and mix (Basic).</summary>
    public bool TubeModellerAdvanced { get; set; }
    // ── Spectrum analyser ──
    // Names follow the macOS Console's preferences.

    /// <summary>Where the dashboard and the channel pages draw their spectrum:
    /// in the response graph, in the bar strip under it, or both.</summary>
    public bool RtaDashboardShowGraph { get; set; } = true;
    public bool RtaDashboardShowBars { get; set; }
    public bool RtaChannelShowGraph { get; set; } = true;
    public bool RtaChannelShowBars { get; set; }
    /// <summary>The dashboard's channels as an <c>RtaChannelSelection</c>
    /// storage key ("out:0,1"); empty means never chosen.</summary>
    public string RtaDashboardSelectionKey { get; set; } = "";
    /// <summary>The dashboard's selection on the side not showing.</summary>
    public string RtaDashboardOtherSideKey { get; set; } = "";
    /// <summary>Channel pages start on their own channel unless the user hid
    /// the spectrum on one.</summary>
    public bool RtaChannelPagesShowSpectrum { get; set; } = true;
    public int RtaBarColumns { get; set; } = 2;
    /// <summary>Dashboard cards per row; 0 fits as many as the window allows.</summary>
    public int DashboardCardsPerRow { get; set; }
    public double RtaBarHeight { get; set; } = 96;
    /// <summary>The main window's sidebar width, dragged at its edge.</summary>
    public double SidebarWidth { get; set; } = 280;
    public double RtaGraphOpacity { get; set; } = 1.0;
    public double RtaFloorDb { get; set; } = -90;
    public double RtaCeilingDb { get; set; } = 6;
    public bool RtaShowPeakHold { get; set; } = true;
    public bool RtaSmoothingOn { get; set; } = true;
    /// <summary>The device-side options, kept here because the device never
    /// stores them.</summary>
    public int RtaFftOrder { get; set; } = 10;
    public int RtaAvgMs { get; set; } = 300;
    public int RtaPeakDecayDbS { get; set; } = 12;

    /// <summary>The interpolation amount the displays use; 0 is off.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double RtaSmoothing => RtaSmoothingOn ? 0.35 : 0;

    public bool RtaShows(bool bars, bool onDashboard) => (bars, onDashboard) switch
    {
        (false, true) => RtaDashboardShowGraph,
        (true, true) => RtaDashboardShowBars,
        (false, false) => RtaChannelShowGraph,
        _ => RtaChannelShowBars,
    };

    public void SetRtaShows(bool bars, bool onDashboard, bool on)
    {
        switch (bars, onDashboard)
        {
            case (false, true): RtaDashboardShowGraph = on; break;
            case (true, true): RtaDashboardShowBars = on; break;
            case (false, false): RtaChannelShowGraph = on; break;
            default: RtaChannelShowBars = on; break;
        }
    }

    public double GraphLineWidth { get; set; } = 1.0;
    public double GraphAnimationSpeed { get; set; } = 0.2;
    public bool ShowDebugInfo { get; set; }

    // Graph scale
    public double GraphDbRange { get; set; } = 50.0;
    public double GraphDbCenter { get; set; } = 0.0;
    public double GraphMinFrequency { get; set; } = 15.0;
    public double GraphMaxFrequency { get; set; } = 20000.0;

    // Grid/label visibility
    public bool ShowFrequencyGrid { get; set; } = true;
    public bool ShowFrequencyLabels { get; set; } = true;
    public bool ShowDbGrid { get; set; } = true;
    public bool ShowDbLabels { get; set; } = true;
    public bool ShowDbUnits { get; set; } = true;

    // Grid line strength: 0 hides the grid, 1 is standard, 2 twice as strong.
    public double GraphGridOpacity { get; set; } = 0.4;

    // Readouts that follow the pointer over empty graph while editing bands:
    // frequency along the bottom, level along the left edge.
    public bool ShowFrequencyReadout { get; set; } = true;
    public bool ShowGainReadout { get; set; } = true;

    // Dotted lines for non-selected channels
    public bool DottedInactiveChannels { get; set; } = true;

    // Phase-response overlay (dotted curve on a right-side degree axis).
    public bool ShowPhase { get; set; } = false;
    public bool PhaseUnwrapped { get; set; } = false;

    // Whether the popout graph follows the selected channel editor page
    public bool PopoutFollowsSelectedChannel { get; set; } = true;

    // Whether output gain / input preamp offsets the level shown in the
    // response graph (off = pure filter response).
    public bool GraphLevelIncludesGain { get; set; } = true;

    // Master L/R PEQ link (input pair 0 — name kept for settings-file compat)
    public bool MasterPeqLinked { get; set; }

    // PEQ link for the extra input pairs: [0]=IN3/4, [1]=IN5/6, [2]=IN7/8
    public bool[] InputPairLinkedExt { get; set; } = new bool[3];
    /// <summary>Which input pairs are linked, per device serial: bit n is pair
    /// n (1/2, 3/4, 5/6, 7/8), as on the macOS Console. A device with no entry
    /// takes the global flags above.</summary>
    public Dictionary<string, int> LinkedInputPairsBySerial { get; set; } = new();

    // Per-channel gain/delay lock state (key = ChannelId int)
    public Dictionary<int, bool> GainLocked { get; set; } = new();
    public Dictionary<int, bool> DelayLocked { get; set; } = new();

    // Dashboard graph-visibility pills (key = ChannelId int). Sparse: a channel
    // with no entry is visible. Only dashboard toggles are recorded here — the
    // narrowed view while a channel editor is open is temporary.
    public Dictionary<int, bool> GraphChannelVisibility { get; set; } = new();

    // Show the quick-save button next to the preset dropdown when dirty
    public bool ShowPresetSaveButton { get; set; } = true;

    // Sidebar volume control mode: "master" (hardware master volume,
    // REQ_SET_MASTER_VOLUME 0xD2) or "user" (vendor-channel user volume,
    // REQ_SET_USER_VOLUME 0xDA — mirrors the UAC1 host slider). Default
    // is "master" to preserve current behavior for existing users.
    public string SidebarVolumeMode { get; set; } = "master";

    public event EventHandler? SettingsChanged;

    public void NotifyChanged()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                loaded.LoadedFromFile = true;
                return loaded;
            }
        }
        catch
        {
            // Ignore load errors
        }
        return new AppSettings();
    }
}

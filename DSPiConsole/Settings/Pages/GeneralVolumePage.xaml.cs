using System.ComponentModel;
using CommunityToolkit.WinUI.Controls;
using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// General › Globals — device-wide settings that are not part of an individual
/// preset's DSP state: master-volume persistence mode, power-on startup
/// behaviour, and output-config (physical IO) persistence mode. The startup and
/// output-config sections require preset support and are hidden behind a notice
/// otherwise. Consolidates the former Volume / Presets-Startup /
/// Presets-Inclusion pages. All three settings are flash-persistent and routed
/// through the pending-change tracker.
/// </summary>
public sealed partial class GeneralVolumePage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    private const string DeviceNameKey = "general.globals.device-name";
    private const string LatencyKey = "general.globals.latency";
    private static string SourceNameKey(InputSource s) => $"general.globals.source-name.{(int)s}";

    /// <summary>What the user typed into each name field, by tracker key. Only
    /// meaningful while the tracker still holds that key.</summary>
    private readonly Dictionary<string, string> _drafts = new();
    private byte? _latencyDraft;
    private string _latencyLayout = "";

    /// <summary>The source-name rows, rebuilt only when the set of sources
    /// changes so a field being typed in keeps its focus.</summary>
    private readonly Dictionary<InputSource, (TextBox Box, Ellipse Dot, TextBlock Detail)> _sourceRows = new();
    private string _sourceRowsLayout = "";

    public GeneralVolumePage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.PropertyChanged -= OnVmPropertyChanged;
        Vm.PropertyChanged += OnVmPropertyChanged;
        Vm.InputSourceNamesChanged -= OnNamesChanged;
        Vm.InputSourceNamesChanged += OnNamesChanged;
        Refresh();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.PropertyChanged -= OnVmPropertyChanged;
        Vm.InputSourceNamesChanged -= OnNamesChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.DeviceName):
            case nameof(MainViewModel.DeviceNameSupported):
            case nameof(MainViewModel.OutputLatencySupported):
            case nameof(MainViewModel.OutputLatencyStatus):
            case nameof(MainViewModel.OutputLatencyOptions):
            case nameof(MainViewModel.InputSourceNamesSupported):
            case nameof(MainViewModel.MultiSpdifSupported):
            case nameof(MainViewModel.InputI2sSupported):
            // S/PDIF pins and enables (SpdifRxPin doubles as that signal) and
            // ADAT selectability change which source rows exist and their details.
            case nameof(MainViewModel.SpdifRxPin):
            case nameof(MainViewModel.AdatInputSelectable):
                DispatcherQueue.TryEnqueue(Refresh);
                break;
        }
    }

    private void OnNamesChanged() => DispatcherQueue.TryEnqueue(Refresh);

    private bool IsStaged(string key) => Tracker?.Pending.Any(p => p.Key == key) == true;

    /// <summary>The text a name field should show: the user's unsaved draft
    /// while one is staged, otherwise what the device stores.</summary>
    private string FieldText(string key, string stored) =>
        IsStaged(key) && _drafts.TryGetValue(key, out var draft) ? draft : stored;

    protected override void Refresh()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            RefreshDeviceName();
            RefreshLatency();
            RefreshSourceNames();
        }
        finally { _suppress = false; }

        _suppress = true;
        try
        {
            // Refresh runs on many device notifications (names, latency, pins),
            // so a combo with an unsaved choice keeps showing that choice
            // rather than snapping back to the device value under the user.

            // Master volume mode is available regardless of preset support.
            if (!IsStaged("general.globals.master-mode"))
                MasterVolumeModeCombo.SelectedIndex = Vm.MasterVolumeMode == 1 ? 1 : 0;

            // Startup + output-config require presets — hide them behind a
            // notice when the firmware doesn't expose presets.
            bool presets = Vm.PresetsSupported;
            UnsupportedNotice.Visibility = presets ? Visibility.Collapsed : Visibility.Visible;
            StartupModeCard.Visibility = presets ? Visibility.Visible : Visibility.Collapsed;
            DefaultPresetCard.Visibility = presets ? Visibility.Visible : Visibility.Collapsed;
            OutputConfigModeCard.Visibility = presets ? Visibility.Visible : Visibility.Collapsed;
            if (!presets) return;

            // The preset list is rebuilt for its names, but never under an open
            // list (WinUI throws when items change as a popup closes).
            if (!IsStaged("general.globals.startup") && !DefaultPresetCombo.IsDropDownOpen && !StartupModeCombo.IsDropDownOpen)
            {
                StartupModeCombo.SelectedIndex = Vm.PresetStartupMode;

                DefaultPresetCombo.Items.Clear();
                for (int i = 0; i < MainViewModel.PresetSlotCount; i++)
                {
                    DefaultPresetCombo.Items.Add(new ComboBoxItem
                    {
                        Content = Vm.GetPresetDisplayName(i),
                        Tag = i
                    });
                }
                DefaultPresetCombo.SelectedIndex = Vm.PresetDefaultSlot;
                DefaultPresetCombo.IsEnabled = Vm.PresetStartupMode == 0;
            }

            // ComboBoxItem order, as on the Mac: 0=Independent (Tag=0), 1=With Preset (Tag=1).
            if (!IsStaged("general.globals.output-config-mode"))
                OutputConfigModeCombo.SelectedIndex = Vm.OutputConfigMode == 1 ? 1 : 0;
        }
        finally { _suppress = false; }
    }

    private void OnMasterVolumeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (MasterVolumeModeCombo.SelectedItem is not ComboBoxItem item) return;
        if (!byte.TryParse(item.Tag?.ToString() ?? "0", out var newMode)) return;

        // Flash-persistent — stage rather than commit.
        var oldMode = Vm.MasterVolumeMode;
        var vm = Vm;
        static string Label(byte m) => m == 1 ? "With Preset" : "Independent";
        Tracker.Stage(new PendingChange(
            Key: "general.globals.master-mode",
            PageId: Id,
            FieldLabel: "Master volume mode",
            OldDisplay: Label(oldMode),
            NewDisplay: Label(newMode),
            Apply: async () => await vm.SetMasterVolumeMode(newMode) ? (byte)0 : (byte)0xFF));
    }

    // ── Startup (moved from the former Presets › Startup page) ──────────
    private void OnStartupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (StartupModeCombo.SelectedIndex < 0) return;

        // Default-preset combo is only meaningful when mode == 0 ("Default
        // preset"); update its enabled state immediately for the UI.
        byte mode = (byte)StartupModeCombo.SelectedIndex;
        DefaultPresetCombo.IsEnabled = mode == 0;
        StageStartupChange();
    }

    private void OnDefaultPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (DefaultPresetCombo.SelectedItem is not ComboBoxItem || DefaultPresetCombo.SelectedIndex < 0) return;
        StageStartupChange();
    }

    /// <summary>
    /// Stage the combined startup config. Mode + default slot ride together in a
    /// single firmware command (REQ_SET_PRESET_STARTUP), so they share one
    /// tracker key — re-editing either combo de-dupes with the first stage.
    /// </summary>
    private void StageStartupChange()
    {
        if (Vm == null || Tracker == null) return;

        byte newMode = (byte)System.Math.Max(0, StartupModeCombo.SelectedIndex);
        byte newSlot = DefaultPresetCombo.SelectedItem is ComboBoxItem di && di.Tag is int ds
            ? (byte)ds : (byte)0;

        byte oldMode = Vm.PresetStartupMode;
        byte oldSlot = Vm.PresetDefaultSlot;

        string Describe(byte mode, byte slot) =>
            mode == 0
                ? $"Default preset → {Vm!.GetPresetDisplayName(slot)}"
                : "Last used";

        var vm = Vm;
        Tracker.Stage(new PendingChange(
            Key: "general.globals.startup",
            PageId: Id,
            FieldLabel: "Startup behaviour",
            OldDisplay: Describe(oldMode, oldSlot),
            NewDisplay: Describe(newMode, newSlot),
            Apply: async () => await vm.SetPresetStartup(newMode, newSlot) ? (byte)0 : (byte)0xFF));
    }

    // ── Output config mode (moved from the former Presets › Inclusion page) ──
    private void OnOutputConfigModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (OutputConfigModeCombo.SelectedItem is not ComboBoxItem item) return;
        if (!byte.TryParse(item.Tag?.ToString() ?? "1", out var newMode)) return;

        var oldMode = Vm.OutputConfigMode;
        var vm = Vm;
        static string Label(byte m) => m == 1 ? "With Preset" : "Independent";
        Tracker.Stage(new PendingChange(
            Key: "general.globals.output-config-mode",
            PageId: Id,
            FieldLabel: "Output configuration",
            OldDisplay: Label(oldMode),
            NewDisplay: Label(newMode),
            Apply: async () => await vm.SetOutputConfigMode(newMode) ? (byte)0 : (byte)0xFF));
    }

    // ── Device name (firmware 1.1.6) ────────────────────────────────────

    private void RefreshDeviceName()
    {
        bool supported = Vm!.DeviceNameSupported;
        DeviceNameCard.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (!supported) return;
        var serial = Vm.Device.SelectedDeviceInfo?.Serial;
        DeviceNameCard.Description = (serial != null ? $"Serial {serial}. " : "")
            + "Stored on the device, so it keeps this name on any computer or DSPi Link hub. "
            + "Shown in the device picker and on the front panel.";
        SetFieldText(DeviceNameBox, FieldText(DeviceNameKey, Vm.DeviceName));
        DeviceNameDot.Visibility = IsStaged(DeviceNameKey) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The device name box (declared in XAML, MaxLength 31) keeps only
    /// what the board can store; see <see cref="NameField"/>.</summary>
    private void OnNameTextChanging(TextBox sender, TextBoxTextChangingEventArgs args) => NameField.Filter(sender);

    /// <summary>TextChanged arrives after the change, so a programmatic set
    /// raises it once <see cref="_suppress"/> is clear again. Only a focused
    /// field is being typed in; the rest are the page filling itself.</summary>
    private static bool IsUserEdit(TextBox box) => box.FocusState != FocusState.Unfocused;

    private void OnDeviceNameTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null || !IsUserEdit(DeviceNameBox)) return;
        var typed = DeviceNameText.Sanitize(DeviceNameBox.Text);
        var vm = Vm;
        StageName(DeviceNameKey, "Device name", vm.DeviceName, typed, "(no name)",
            () => vm.SetDeviceNameAsync(typed));
        DeviceNameDot.Visibility = IsStaged(DeviceNameKey) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Stage a name edit, or drop the staged one when the field is
    /// back to the stored name.</summary>
    private void StageName(string key, string label, string stored, string typed, string emptyDisplay,
                           Func<Task<bool>> apply)
    {
        string Display(string s) => s.Length == 0 ? emptyDisplay : $"\"{s}\"";
        if (typed == stored)
        {
            _drafts.Remove(key);
            Tracker!.Discard(key);
            return;
        }
        _drafts[key] = typed;
        Tracker!.Stage(new PendingChange(
            Key: key,
            PageId: Id,
            FieldLabel: label,
            OldDisplay: Display(stored),
            NewDisplay: Display(typed),
            Apply: async () => await apply() ? (byte)0 : (byte)0xFF));
    }

    private void SetFieldText(TextBox box, string text)
    {
        if (box.Text == text) return;
        bool was = _suppress;
        _suppress = true;
        try { box.Text = text; }
        finally { _suppress = was; }
    }

    // ── Latency (firmware 1.1.6) ─────────────────────────────────────────

    private void RefreshLatency()
    {
        var options = Vm!.OutputLatencyOptions;
        bool supported = Vm.OutputLatencySupported && options.Count > 0;
        LatencyCard.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (!supported) return;

        // Rebuilding the items under an open list would close it.
        if (LatencyCombo.IsDropDownOpen) return;
        if (!IsStaged(LatencyKey)) _latencyDraft = null;
        byte selected = _latencyDraft ?? Vm.OutputLatencyStatus.RequestedId;

        // Each fetch delivers a fresh table; rebuild only when what it lists changed.
        bool listed = options.Any(o => o.Id == selected);
        string layout = string.Join(",", options.Select(o => $"{o.Id}:{o.LatencyUs}")) + (listed ? "" : $"|-{selected}");
        if (layout != _latencyLayout)
        {
            _latencyLayout = layout;
            LatencyCombo.Items.Clear();
            foreach (var o in options)
            {
                LatencyCombo.Items.Add(new ComboBoxItem
                {
                    Content = o.Id == OutputLatency.DefaultId ? $"{o.Label} (default)" : o.Label,
                    Tag = o.Id,
                });
            }
            // A stored option the table doesn't list still shows, as a dash.
            if (!listed)
                LatencyCombo.Items.Add(new ComboBoxItem { Content = "-", Tag = selected });
        }
        LatencyCombo.SelectedItem = LatencyCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (byte)i.Tag == selected);
        LatencyCard.Description = LatencyCaption(selected);
    }

    /// <summary>Explains the trade-off and, when the rate caps the selected
    /// option, what it actually runs as. After the macOS Console.</summary>
    private string LatencyCaption(byte selected)
    {
        var text = "Lower values reduce the delay from input to output. Higher values may benefit sources exhibiting high jitter in the event of audio stutters.";
        if (Vm!.OutputLatencyOptions.FirstOrDefault(o => o.Id == selected) is { Id: not 0 } option
            && option.Available && option.EffectiveUs != option.LatencyUs)
        {
            text += $" At the current sample rate the output buffer is smaller than this, so it runs as {OutputLatency.FormatMs(option.EffectiveUs)}.";
        }
        return text + " Values less than 2ms have no effect upon USB Audio.";
    }

    private void OnLatencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        if (LatencyCombo.SelectedItem is not ComboBoxItem { Tag: byte id }) return;
        var vm = Vm;
        byte stored = vm.OutputLatencyStatus.RequestedId;
        LatencyCard.Description = LatencyCaption(id);
        if (id == stored)
        {
            _latencyDraft = null;
            Tracker.Discard(LatencyKey);
            return;
        }
        _latencyDraft = id;
        Tracker.Stage(new PendingChange(
            Key: LatencyKey,
            PageId: Id,
            FieldLabel: "Latency",
            OldDisplay: vm.OutputLatencyLabel(stored),
            NewDisplay: vm.OutputLatencyLabel(id),
            Apply: async () => await vm.SetOutputLatencyAsync(id) ? (byte)0 : (byte)0xFF));
    }

    // ── Source names (firmware 1.1.6) ────────────────────────────────────

    private void RefreshSourceNames()
    {
        bool supported = Vm!.InputSourceNamesSupported;
        SourceNamesSection.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (!supported) return;

        var sources = Vm.InputSourceOptions;
        string layout = string.Join(",", sources);
        if (layout != _sourceRowsLayout)
        {
            _sourceRowsLayout = layout;
            _sourceRows.Clear();
            SourceNameRows.Children.Clear();
            foreach (var s in sources) SourceNameRows.Children.Add(BuildSourceRow(s));
        }
        foreach (var (source, row) in _sourceRows)
        {
            var key = SourceNameKey(source);
            SetFieldText(row.Box, FieldText(key, Vm.InputSourceNames[(int)source]));
            row.Dot.Visibility = IsStaged(key) ? Visibility.Visible : Visibility.Collapsed;
            row.Detail.Text = Vm.InputSourceDetail(source);
            ((SettingsCard)row.Box.Tag!).Header = Vm.BuiltInInputSourceTitle((int)source);
        }
    }

    private SettingsCard BuildSourceRow(InputSource source)
    {
        var dot = new Ellipse
        {
            Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 159, 10)),
        };
        ToolTipService.SetToolTip(dot, "Not saved yet");
        var box = new TextBox { Width = 220, PlaceholderText = "Add a name" };
        NameField.Attach(box);
        box.TextChanged += (_, _) => OnSourceNameTextChanged(source, box, dot);
        var detail = new TextBlock();
        var card = new SettingsCard
        {
            Header = Vm!.BuiltInInputSourceTitle((int)source),
            Description = detail,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { dot, box } },
        };
        box.Tag = card;
        _sourceRows[source] = (box, dot, detail);
        return card;
    }

    private void OnSourceNameTextChanged(InputSource source, TextBox box, Ellipse dot)
    {
        if (_suppress || Vm == null || Tracker == null || !IsUserEdit(box)) return;
        var typed = DeviceNameText.Sanitize(box.Text);
        var vm = Vm;
        var key = SourceNameKey(source);
        StageName(key, $"{vm.BuiltInInputSourceTitle((int)source)} name", vm.InputSourceNames[(int)source], typed,
            "(built-in name)", () => vm.SetInputSourceNameAsync((int)source, typed));
        dot.Visibility = IsStaged(key) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "general.globals";
    public string Title => "Globals";
    public SettingsCategory Category => SettingsCategory.General;
    public string IconGlyph => ""; // Volume
    public int Order => 10;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new GeneralVolumePage();
        p.Attach(vm, tracker);
        return p;
    }
}

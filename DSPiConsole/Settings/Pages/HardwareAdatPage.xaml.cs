using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › ADAT. The RP2350-only optical link, laid out like Hardware › I2S:
/// the clock mode and its lock state first, because it governs both directions,
/// then the 8-channel transmitter (enable + TX pin) and the 8-channel receiver
/// (enable + RX pin). The rate the DSPi generates as master is common to every
/// interface and lives on Hardware › Master Clock. Registered when the firmware
/// reports either half (see <see cref="IsAvailable"/>); whichever half is missing
/// hides, along with its heading and rule.
/// </summary>
public sealed partial class HardwareAdatPage : SettingsModule, ISettingsPage
{
    private bool _suppress;
    private DispatcherQueueTimer? _statusTimer;
    private readonly DSPiConsole.Core.InFlightGate _statusGate = new();

    public HardwareAdatPage()
    {
        InitializeComponent();

        // Transmit: the ADAT default (GPIO 12) is deliberately absent from
        // ValidPins (it's not a general audio-routing pin), so add it explicitly.
        // No "(Not set)" — the output always holds a pin, valid or not.
        var txPins = new List<byte>(HardwarePins.ValidPins);
        if (!txPins.Contains(MainViewModel.AdatDefaultPin)) txPins.Add(MainViewModel.AdatDefaultPin);
        txPins.Sort();
        foreach (var pin in txPins)
            OutPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        // Receive: "(Not set)" sentinel first — an unset RX pin is a real state,
        // and the input can't be enabled until it's chosen.
        InPinCombo.Items.Add(new ComboBoxItem { Content = "(Not set)", Tag = MainViewModel.AdatInputPinUnset });
        foreach (var pin in HardwarePins.ValidPins)
            InPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);
        var fetchVm = vm;
        // Each fetch is skipped when its half isn't supported — the reads STALL on
        // firmware that lacks it, and the baselined support flags already know.
        _ = Task.Run(() =>
            {
                if (fetchVm.AdatSupported) fetchVm.FetchAdatConfig();
                if (fetchVm.AdatInputSupported) fetchVm.FetchAdatInputConfig();
            })
            .ContinueWith(_ => DispatcherQueue.TryEnqueue(Refresh));
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;
        if (Vm != null)
        {
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.PropertyChanged += OnVmPropertyChanged;
            Refresh();
        }

        // The receiver's lock state has no push notification, so poll it while the
        // page is visible.
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(700);
        _statusTimer.Tick += (_, _) =>
        {
            // Skipped while the previous read is still out, so a slow link
            // can't stack reads up.
            if (Vm is { AdatInputSupported: true, AdatInputEnabled: true } vm)
                _statusGate.RunInBackground(() => vm.RefreshAdatInputStatus());
        };
        _statusTimer.Start();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
        _statusTimer?.Stop();
        _statusTimer = null;
    }

    private void OnExternalPinChange() => DispatcherQueue.TryEnqueue(RefreshConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.AdatEnabled):
            case nameof(MainViewModel.AdatPin):
            case nameof(MainViewModel.AdatSupported):
            case nameof(MainViewModel.AdatInputEnabled):
            case nameof(MainViewModel.AdatInputPin):
            case nameof(MainViewModel.AdatInputSupported):
            case nameof(MainViewModel.AdatInputClockMode):
                DispatcherQueue.TryEnqueue(Refresh);
                break;
            case nameof(MainViewModel.AdatInputStatus):
                DispatcherQueue.TryEnqueue(RefreshLockPill);
                break;
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            OutEnableToggle.IsOn = Vm.AdatEnabled;
            InEnableToggle.IsOn = Vm.AdatInputEnabled;
            SelectByStringTag(ClockModeCombo, Vm.AdatInputClockMode);
        }
        finally { _suppress = false; }
        RefreshSections();
        RefreshConflicts();
        RefreshLockPill();
        RefreshFreeRunWarning();

        // Where each direction's pin is set, for a click on it in the Overview's
        // map. Each claims its GPIO only while enabled, which is the same rule
        // the map itself follows.
        ClearPinTargets();
        if (Vm.AdatSupported && Vm.AdatEnabled) RegisterPinTarget(Vm.AdatPin, OutPinCombo);
        if (Vm.AdatInputSupported && Vm.AdatInputEnabled
            && Vm.AdatInputPin != MainViewModel.AdatInputPinUnset)
            RegisterPinTarget(Vm.AdatInputPin, InPinCombo);
    }

    /// <summary>Show the receiver's lock state beside the clock picker. Only
    /// meaningful once the input is on — the slave state is readable, never
    /// settable. It has no push notification, hence the polling timer above.</summary>
    private void RefreshLockPill()
    {
        if (Vm == null) return;
        var st = Vm.AdatInputStatus;
        if (!Vm.AdatInputSupported || !Vm.AdatInputEnabled || st == null)
        {
            ClockLockPill.Visibility = Visibility.Collapsed;
            return;
        }
        ClockLockPill.Visibility = Visibility.Visible;
        string rate = st.IsLocked ? $" · {st.DetectedRateText}" : "";
        ClockLockPill.Text = st.StateText + rate;
        ClockLockPill.Foreground = new SolidColorBrush(st.IsLocked
            ? Color.FromArgb(255, 100, 200, 140)
            : Color.FromArgb(255, 240, 180, 90));
    }

    /// <summary>Master clock mode with the ADAT output off means nothing external
    /// is locked to the DSPi's clock — the source drifts against it and produces
    /// periodic pops/clicks. Surface a warning with a one-click fix.</summary>
    private void RefreshFreeRunWarning()
    {
        FreeRunBar.IsOpen = Vm is
        {
            AdatInputSupported: true,
            AdatInputEnabled: true,
            AdatInputClockMode: 0, // Master
            AdatSupported: true,
            AdatEnabled: false,
        };
    }

    /// <summary>Show only the parts this firmware has. Clock and Input both come
    /// with the receiver, so the input half is what creates a second section —
    /// without it there is one section, and the headings and rules that exist to
    /// tell sections apart have nothing to do. Each rule sits above a section and
    /// shows only when that section has something above it to be divided from.</summary>
    private void RefreshSections()
    {
        if (Vm == null) return;
        bool tx = Vm.AdatSupported, rx = Vm.AdatInputSupported;

        ClockHeading.Visibility = Vis(rx);
        ClockModeCard.Visibility = Vis(rx);

        OutputDivider.Visibility = Vis(rx && tx);
        OutputHeading.Visibility = Vis(rx && tx);
        OutEnableCard.Visibility = Vis(tx);
        OutPinCard.Visibility = Vis(tx);

        // Input is only ever preceded by Clock, which is present whenever it is.
        InputDivider.Visibility = Vis(rx);
        InputHeading.Visibility = Vis(rx);
        InEnableCard.Visibility = Vis(rx);
        InPinCard.Visibility = Vis(rx);
    }

    private static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Grey-out pins owned by other features and reselect the current pin
    /// in each combo. Items are never rebuilt here (that races WinUI's ComboBox
    /// popup dismissal) — only their content and enabled state change.</summary>
    private void RefreshConflicts()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            // Each combo excludes only its own claim, so the transmit pin still
            // shows as taken on the receive combo and vice versa.
            if (Vm.AdatSupported)
                ApplyOwners(OutPinCombo, HardwarePins.BuildOwnerMap(Vm, excludeAdatSelf: true), Vm.AdatPin);
            if (Vm.AdatInputSupported)
                ApplyOwners(InPinCombo, HardwarePins.BuildOwnerMap(Vm, excludeAdatInputSelf: true), Vm.AdatInputPin);
        }
        finally { _suppress = false; }
    }

    private static void ApplyOwners(ComboBox combo, IReadOnlyDictionary<byte, string> owners, byte currentPin)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is not ComboBoxItem item || item.Tag is not byte pin) continue;
            if (pin == MainViewModel.AdatInputPinUnset) { item.IsEnabled = true; continue; }

            bool isCurrent = pin == currentPin;
            string? owner = null;
            if (!isCurrent && owners.TryGetValue(pin, out var o)) owner = o;
            item.Content = owner != null ? $"GPIO {pin} ({owner})" : $"GPIO {pin}";
            item.IsEnabled = owner == null;
        }
        SelectPinInCombo(combo, currentPin);
    }

    // ── Output handlers ────────────────────────────────────────────────────

    private async void OnOutEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        bool enable = OutEnableToggle.IsOn;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatEnable(enable));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus(enable ? "ADAT output enabled" : "ADAT output disabled", false);
            RefreshConflicts();
            return;
        }

        // Failed — revert the toggle to the actual state and explain.
        _suppress = true;
        OutEnableToggle.IsOn = Vm.AdatEnabled;
        _suppress = false;
        if (status == PinConfigResult.PinInUse)
        {
            await PromptForOutPinAsync();
            return;
        }
        ShowStatus(status switch
        {
            PinConfigResult.InvalidPin => "The transmit pin isn't valid — pick a different GPIO.",
            PinConfigResult.InvalidOutput => "ADAT output isn't supported on this device.",
            _ => $"Failed to change ADAT output (0x{status:X2})"
        }, true);
    }

    /// <summary>Enabling the output landed on a pin something else holds. Offer a
    /// free one and switch the output on with it, rather than reporting the clash
    /// and leaving the user to go and sort it out themselves.</summary>
    private async Task PromptForOutPinAsync()
    {
        if (Vm == null) return;
        var claims = HardwarePins.BuildAssignmentMap(Vm, excludeAdatSelf: true);
        if (!claims.TryGetValue(Vm.AdatPin, out var owner))
        {
            // Refused for something the map cannot account for: fall back to
            // naming it on the status line, which is all we can honestly do.
            ShowPinConflict(StatusText, StatusPinButton, claims,
                "The transmit pin is already assigned — pick a free GPIO first.", Vm.AdatPin);
            return;
        }

        var rows = new List<PinReassignment>
        {
            new("the ADAT output", Vm.AdatPin, owner),
        };
        await PinConflictPrompt.ShowAsync(XamlRoot, rows, claims, HardwarePins.ValidPins,
            async pins =>
            {
                if (await Task.Run(() => Vm.SetAdatPin(pins[0])) != PinConfigResult.Success) return false;
                if (await Task.Run(() => Vm.SetAdatEnable(true)) != PinConfigResult.Success) return false;
                HardwarePins.RaisePinAssignmentsChanged();
                ShowStatus($"ADAT output enabled on GPIO {pins[0]}", false);
                return true;
            });
        Refresh();
    }

    /// <summary>The same for the input's receive pin.</summary>
    private async Task PromptForInPinAsync()
    {
        if (Vm == null) return;
        var claims = HardwarePins.BuildAssignmentMap(Vm, excludeAdatInputSelf: true);
        if (!claims.TryGetValue(Vm.AdatInputPin, out var owner))
        {
            ShowPinConflict(StatusText, StatusPinButton, claims,
                "The receive pin is already claimed — pick a free GPIO.", Vm.AdatInputPin);
            return;
        }

        var rows = new List<PinReassignment>
        {
            new("the ADAT input", Vm.AdatInputPin, owner),
        };
        await PinConflictPrompt.ShowAsync(XamlRoot, rows, claims, HardwarePins.ValidPins,
            async pins =>
            {
                if (await Task.Run(() => Vm.SetAdatInputPin(pins[0])) != PinConfigResult.Success) return false;
                if (await Task.Run(() => Vm.SetAdatInputEnable(true)) != PinConfigResult.Success) return false;
                HardwarePins.RaisePinAssignmentsChanged();
                ShowStatus($"ADAT input enabled on GPIO {pins[0]}", false);
                return true;
            });
        Refresh();
    }

    private async void OnOutPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (OutPinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatPin(newPin));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"ADAT transmit pin set to GPIO {newPin}", false);
            return;
        }

        _suppress = true;
        SelectPinInCombo(OutPinCombo, Vm.AdatPin);
        _suppress = false;
        if (status == PinConfigResult.PinInUse)
        {
            ShowPinConflict(StatusText, StatusPinButton, HardwarePins.BuildAssignmentMap(Vm, excludeAdatSelf: true),
                $"GPIO {newPin} is already assigned to another output", newPin);
            return;
        }
        ShowStatus(status switch
        {
            PinConfigResult.InvalidPin => $"GPIO {newPin} can't drive the ADAT output",
            _ => $"Failed to set the ADAT transmit pin (0x{status:X2})"
        }, true);
    }

    // ── Input handlers ─────────────────────────────────────────────────────

    private async void OnInEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        bool enable = InEnableToggle.IsOn;
        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatInputEnable(enable));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshConflicts();
            return;
        }
        _suppress = true;
        InEnableToggle.IsOn = Vm.AdatInputEnabled;
        _suppress = false;
        if (status == PinConfigResult.PinInUse)
        {
            await PromptForInPinAsync();
            return;
        }
        ShowStatus(status switch
        {
            PinConfigResult.InvalidPin => "Set a valid receive pin before enabling the ADAT input.",
            PinConfigResult.InvalidOutput => "ADAT input isn't supported on this device.",
            _ => $"Failed to change ADAT input (0x{status:X2})."
        }, true);
    }

    private async void OnClockModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (ClockModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!byte.TryParse(tag, out var mode) || mode == Vm.AdatInputClockMode) return;
        ClearStatus();

        var status = await Task.Run(() => Vm.SetAdatInputClockMode(mode));
        if (status != PinConfigResult.Success)
        {
            _suppress = true;
            SelectByStringTag(ClockModeCombo, Vm.AdatInputClockMode);
            _suppress = false;
            ShowStatus($"Failed to set the ADAT clock source (0x{status:X2}).", true);
            return;
        }
        ShowStatus($"ADAT clock source set to {(mode == 1 ? "Slave" : "Master")}", false);
        RefreshFreeRunWarning();
    }

    /// <summary>The free-running warning's one-click fix. The output toggle is on
    /// this page too, so turning it on from here has to repaint it — the queued
    /// PropertyChanged(AdatEnabled) does that, and Refresh re-evaluates the
    /// warning that prompted the click.</summary>
    private async void OnEnableAdatOutputClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatEnable(true));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            Refresh();
            ShowStatus("ADAT output enabled.", false);
            return;
        }
        if (status == PinConfigResult.PinInUse)
        {
            await PromptForOutPinAsync();
            return;
        }
        ShowStatus(status switch
        {
            PinConfigResult.InvalidPin => "Pick a valid ADAT transmit pin in the Output section first.",
            _ => $"Failed to enable the ADAT output (0x{status:X2})."
        }, true);
    }

    private async void OnInPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (InPinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;
        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatInputPin(newPin));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            string label = newPin == MainViewModel.AdatInputPinUnset ? "cleared" : $"set to GPIO {newPin}";
            ShowStatus($"ADAT receive pin {label}", false);
            return;
        }
        _suppress = true;
        SelectPinInCombo(InPinCombo, Vm.AdatInputPin);
        _suppress = false;
        if (status == PinConfigResult.PinInUse)
        {
            ShowPinConflict(StatusText, StatusPinButton, HardwarePins.BuildAssignmentMap(Vm, excludeAdatInputSelf: true),
                $"GPIO {newPin} is already assigned to another peripheral.", newPin);
            return;
        }
        ShowStatus(status switch
        {
            PinConfigResult.InvalidPin => $"GPIO {newPin} can't receive the ADAT input.",
            _ => $"Failed to set the ADAT receive pin (0x{status:X2})."
        }, true);
    }

    /// <summary>Select the combo entry whose byte Tag matches <paramref name="pin"/>.
    /// No-op if no match — leaves the previous selection rather than blanking.</summary>
    private static void SelectPinInCombo(ComboBox combo, byte pin)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag is byte p && p == pin)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Select the combo item whose string Tag ("0"/"1") equals the byte
    /// <paramref name="value"/>. The clock-mode items are declared in XAML, so
    /// their tags arrive as strings rather than the bytes the pin combos use.</summary>
    private static void SelectByStringTag(ComboBox combo, byte value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem item && item.Tag is string s
                && byte.TryParse(s, out var v) && v == value)
            {
                combo.SelectedIndex = i;
                return;
            }
    }

    private void ShowStatus(string msg, bool isError)
    {
        // Any message that isn't a pin conflict takes the eye away, so one is
        // never left pointing at the destination of the message before it.
        PinConflict.Disarm(StatusPinButton);
        StatusText.Text = msg;
        StatusText.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 240, 100, 100)
            : Color.FromArgb(255, 100, 200, 140));
        StatusText.Visibility = Visibility.Visible;
    }

    private void ClearStatus()
    {
        PinConflict.Disarm(StatusPinButton);
        StatusText.Visibility = Visibility.Collapsed;
    }

    // ── ISettingsPage ──────────────────────────────────────────────────────
    public string Id => "hardware.adat";
    public string Title => "ADAT";
    public SettingsCategory Category => SettingsCategory.System;
    public string IconGlyph => "";
    public int Order => 20;
    public bool IsAvailable(MainViewModel vm) => vm.AdatSupported || vm.AdatInputSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareAdatPage();
        p.Attach(vm, tracker);
        return p;
    }
}

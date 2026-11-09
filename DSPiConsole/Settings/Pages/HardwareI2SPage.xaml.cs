using System.ComponentModel;
using System.Threading.Tasks;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › I2S — the whole interface: the clock mode and lock state,
/// the BCK pin and the slave-role clock pair, and (on V12+ firmware) the
/// input's channel count and per-pair data pins. Input and output are one
/// page because they share the bit and word clocks. The rate the DSPi
/// generates as master, and MCK, are common to every interface and live
/// on Hardware › Master Clock.
///
/// <para>
/// Subscribes to <see cref="HardwarePins.PinAssignmentsChanged"/> for
/// cross-page conflict refreshes and to <see cref="MainViewModel"/>
/// PropertyChanged for the slot- and slave-driven pin locking.
/// </para>
/// </summary>
public sealed partial class HardwareI2SPage : SettingsModule, ISettingsPage
{
    private bool _suppress;
    private readonly ComboBox[] _rxCombos;

    public HardwareI2SPage()
    {
        InitializeComponent();

        // BCK can use any audio-capable GPIO; populate it once at
        // construction with every ValidPins entry. RefreshConflicts
        // only toggles IsEnabled and updates each item's Content
        // label — it MUST NOT clear/rebuild the Items collection,
        // because doing so races the popup-dismissal of a user
        // selection and triggers "Element not found" (E_FAIL) in
        // WinUI's ComboBox layout on the next tick.
        foreach (var pin in HardwarePins.ValidPins)
            BckPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
        foreach (var pin in HardwarePins.ValidPins)
            SlaveBckCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        // Same for the input's data pins — one combo per stereo pair, each
        // tagged with its pair index so the shared handler knows which it is.
        _rxCombos = new[] { RxPinCombo0, RxPinCombo1, RxPinCombo2, RxPinCombo3 };
        for (int pair = 0; pair < _rxCombos.Length; pair++)
        {
            _rxCombos[pair].Tag = pair;
            foreach (var pin in HardwarePins.ValidPins)
                _rxCombos[pair].Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
        }

        // Subscriptions go in Loaded/Unloaded so they survive sidebar
        // navigation cycles — see HardwareOutputAssignmentPage for the
        // rationale.
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);

        // Kick off background fetches to populate the page with the
        // device's current values. The PropertyChanged handler is what
        // pushes them into the UI once they arrive. The input read is
        // skipped where the firmware lacks it — it stalls rather than
        // failing, and the baselined support flag already knows.
        var fetchVm = vm;
        _ = Task.Run(() =>
            {
                fetchVm.FetchI2SBckPin();
                fetchVm.FetchI2sClockConfig();
                if (fetchVm.InputI2sSupported) fetchVm.FetchI2sInputConfig();
            })
            // The fetches raise PropertyChanged for what they read, but a device
            // that answers nothing raises nothing — repaint once regardless so
            // the page can't be left showing pre-Attach defaults.
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
            // Re-sync from VM state in case events were missed while
            // we were unloaded (e.g., a preset switch happened while
            // the user was viewing a different Settings page).
            Refresh();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnExternalPinChange() =>
        DispatcherQueue.TryEnqueue(RefreshConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Refresh on any I2S-relevant VM property change:
        //   • AnySlotIsI2S — the BCK pin can't be re-routed while a
        //     slot is driving it.
        //   • I2SBckPin / I2sRxPin — direct combo state, fired from the
        //     setters AND from the bulk-params load path so preset
        //     reloads / reconnects also repaint.
        //   • I2sSlaveActive — greys out the BCK picker.
        //   • Platform — decides how many stereo pairs exist (RP2350 has
        //     four, RP2040 one), so a board swap changes whether the
        //     Channels card is shown at all.
        if (e.PropertyName == nameof(MainViewModel.Platform)
            || e.PropertyName == nameof(MainViewModel.AnySlotIsI2S)
            || e.PropertyName == nameof(MainViewModel.I2SBckPin)
            || e.PropertyName == nameof(MainViewModel.I2sClockMode)
            || e.PropertyName == nameof(MainViewModel.I2sClockModeSupported)
            || e.PropertyName == nameof(MainViewModel.I2sClockPinModeSupported)
            || e.PropertyName == nameof(MainViewModel.I2sClockPinMode)
            || e.PropertyName == nameof(MainViewModel.I2sBckPinSlave)
            || e.PropertyName == nameof(MainViewModel.I2sSlaveActive)
            || e.PropertyName == nameof(MainViewModel.I2sRxPin)
            || e.PropertyName == nameof(MainViewModel.I2sInputChannels)
            || e.PropertyName == nameof(MainViewModel.InputI2sSupported))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
        else if (e.PropertyName == nameof(MainViewModel.I2sSlaveStatus))
        {
            DispatcherQueue.TryEnqueue(RefreshLockPill);
        }
    }

    /// <summary>Where a pin the Overview linked here is set. Worked out from live
    /// state rather than registered as the cards are built: LRCK has no control of
    /// its own (it rides BCK + 1), and which of the input pickers is live follows
    /// the active pair count.</summary>
    public override bool HighlightPin(byte pin)
    {
        if (Vm == null) return false;
        if (pin == Vm.I2SBckPin || pin == Vm.I2SBckPin + 1) { PinFlash.Play(BckPinCombo); return true; }
        if (Vm.I2sClockSplit && (pin == Vm.I2sBckPinSlave || pin == Vm.I2sBckPinSlave + 1))
        {
            PinFlash.Play(SlaveBckCombo);
            return true;
        }
        for (int pair = 0; pair < Vm.I2sActivePairs && pair < _rxCombos.Length; pair++)
        {
            if (Vm.I2sRxPinAt(pair) != pin) continue;
            PinFlash.Play(_rxCombos[pair]);
            return true;
        }
        return false;
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        // In slave mode an external master drives BCK, so re-routing it here
        // would do nothing.
        bool slave = Vm.I2sSlaveActive;

        _suppress = true;
        try
        {
            ClockModeCard.Visibility = Vis(Vm.I2sClockModeSupported);
            if (Vm.I2sClockModeSupported)
                SelectByStringTag(ClockModeCombo, Vm.I2sClockMode);

            BckPinCard.Description = $"LRCK auto-assigned to GPIO {Vm.I2SBckPin + 1} (BCK + 1).";
            BckPinCombo.IsEnabled = !Vm.AnySlotIsI2S && !slave;

            RefreshClockCards();
            RefreshInputCards();
        }
        finally { _suppress = false; }

        RefreshConflicts();
        RefreshLockPill();
    }

    /// <summary>Show/populate the clock-pin and slave-BCK cards when the firmware
    /// supports them. Runs under the <c>_suppress</c> guard (called from
    /// Refresh).</summary>
    private void RefreshClockCards()
    {
        if (Vm == null) return;

        // Clock-pin unified/split + slave BCK pin. The slave pair only exists in
        // Split mode, so hide the card entirely in Unified (it can't be changed).
        bool pinModeShown = Vm.I2sClockPinModeSupported;
        ClockPinsCard.Visibility = Vis(pinModeShown);
        SlaveBckCard.Visibility = Vis(pinModeShown && Vm.I2sClockSplit);
        if (pinModeShown)
        {
            SelectByStringTag(ClockPinsCombo, Vm.I2sClockPinMode);
            if (Vm.I2sClockSplit)
                SlaveBckCard.Description = $"LRCLK = GPIO {Vm.I2sBckPinSlave + 1} (BCK + 1).";
        }
    }

    /// <summary>Show the input half and size it to the active pair count. The
    /// section headings and the rule above them only earn their place when there
    /// is in fact a second section. Runs under the <c>_suppress</c> guard (called
    /// from Refresh).</summary>
    private void RefreshInputCards()
    {
        if (Vm == null) return;

        bool input = Vm.InputI2sSupported;
        ClockHeading.Visibility = Vis(input);
        InputDivider.Visibility = Vis(input);
        InputHeading.Visibility = Vis(input);

        // Channel selector only on parts with more than one stereo pair (RP2350).
        bool multi = input && Vm.I2sMaxInputChannels > 2;
        ChannelsCard.Visibility = Vis(multi);
        if (multi) SelectChannelCount(Vm.I2sInputChannels);

        // One data-pin card per active stereo pair, then the parked pins of
        // the rest under Unused pairs.
        int pairs = input ? Vm.I2sActivePairs : 0;
        int maxPairs = input ? Math.Min(Vm.I2sMaxPairs, _rxCombos.Length) : 0;
        RxPinCard0.Header = maxPairs > 1 ? "Serial Data 1" : "Serial Data";
        RxPinCard0.Visibility = Vis(pairs >= 1);
        PlaceRxCards(pairs);
        var cards = new[] { RxPinCard1, RxPinCard2, RxPinCard3 };
        for (int i = 0; i < cards.Length; i++) cards[i].Visibility = Vis(i + 1 < maxPairs);
        UnusedPairsExpander.Visibility = Vis(maxPairs > pairs && pairs >= 1);
    }

    private int _placedPairs = -1;

    /// <summary>Puts cards 1-3 under the active pairs or Unused pairs. Only when
    /// the pair count changed: moving a card re-parents its combo, which must
    /// not happen on every refresh while a popup may be closing.</summary>
    private void PlaceRxCards(int pairs)
    {
        if (pairs == _placedPairs) return;
        _placedPairs = pairs;
        ActivePairsPanel.Children.Clear();
        UnusedPairsPanel.Children.Clear();
        var cards = new[] { RxPinCard1, RxPinCard2, RxPinCard3 };
        for (int i = 0; i < cards.Length; i++)
            (i + 1 < pairs ? ActivePairsPanel : UnusedPairsPanel).Children.Add(cards[i]);
    }

    /// <summary>Show the slave-mode lock state beside the clock picker. Only
    /// meaningful while slaved — as master there is nothing to lock to. The state
    /// arrives on notification 0x09, so unlike ADAT's receiver it needs no polling
    /// timer.</summary>
    private void RefreshLockPill()
    {
        if (Vm == null) return;
        var st = Vm.I2sSlaveStatus;
        if (!Vm.I2sSlaveActive || st == null)
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

    private static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Refresh the BCK and slave-BCK pin pickers' per-item
    /// state so that pins claimed by other features appear disabled and
    /// labelled with their owner ("GPIO 6 (Output 1)"), while
    /// still-selectable pins read as a plain "GPIO N". The Items
    /// collection itself is never modified here — that's a hard
    /// requirement because WinUI's ComboBox throws "Element not found"
    /// (E_FAIL) when its Items are cleared/rebuilt on a dispatcher tick
    /// that races the popup dismissal of a user selection. Both combos
    /// are populated once, in the constructor.</summary>
    private void RefreshConflicts()
    {
        if (Vm == null) return;

        _suppress = true;
        try
        {
            // ── BCK ──────────────────────────────────────────────────
            // BCK reserves pin AND pin+1 (LRCK). An item is selectable
            // iff
            //   • it's the current BCK (always selectable so the user
            //     can re-confirm), OR
            //   • the pin isn't claimed by a non-clock feature, AND
            //     pin+1 is audio-capable, AND
            //     pin+1 isn't claimed by a non-clock feature.
            // The current BCK/LRCK pair is exempted from the owner map
            // here because both pins move atomically with the
            // reassignment.
            var owners = HardwarePins.BuildOwnerMap(Vm);
            byte currentBck = Vm.I2SBckPin;

            for (int i = 0; i < BckPinCombo.Items.Count; i++)
            {
                if (BckPinCombo.Items[i] is not ComboBoxItem item) continue;
                if (item.Tag is not byte pin) continue;

                byte lrck = (byte)(pin + 1);
                bool isCurrent = pin == currentBck;
                string? ownerLabel = null;

                if (!isCurrent)
                {
                    // The pin can't be BCK for one of two reasons,
                    // each labelled with exactly one owner so the
                    // dropdown never reads as if a pin has two roles:
                    //   • the pin itself is claimed by a feature →
                    //     "GPIO 6 (OUT 1/2)",
                    //   • or pin+1 (the would-be LRCK) is invalid or
                    //     already claimed → "GPIO 5 (LRCK Conflict)".
                    //     Both LRCK-side cases collapse to the same
                    //     label; the user doesn't need to distinguish
                    //     "would land on a reserved GPIO" from "would
                    //     overlap a feature" — they're both the same
                    //     fix (pick a different BCK).
                    if (owners.TryGetValue(pin, out var owner)
                        && owner != "BCK" && owner != "LRCK")
                        ownerLabel = owner;
                    else if (!HardwarePins.IsAudioCapable(lrck))
                        ownerLabel = "LRCK Conflict";
                    else if (owners.TryGetValue(lrck, out var nextOwner)
                             && nextOwner != "BCK" && nextOwner != "LRCK")
                        ownerLabel = "LRCK Conflict";
                }

                item.Content = ownerLabel != null
                    ? $"GPIO {pin} ({ownerLabel})"
                    : $"GPIO {pin}";
                item.IsEnabled = ownerLabel == null;
            }
            SelectPinInCombo(BckPinCombo, currentBck);

            // ── Slave BCK (SPLIT mode) — reserves pin AND pin+1 (LRCLK) ──
            if (Vm.I2sClockPinModeSupported)
            {
                var slaveOwners = HardwarePins.BuildOwnerMap(Vm, excludeI2sBckSlaveSelf: true);
                byte currentSlave = Vm.I2sBckPinSlave;
                for (int i = 0; i < SlaveBckCombo.Items.Count; i++)
                {
                    if (SlaveBckCombo.Items[i] is not ComboBoxItem item || item.Tag is not byte pin) continue;
                    byte lrck = (byte)(pin + 1);
                    bool isCurrent = pin == currentSlave;
                    string? ownerLabel = null;
                    if (!isCurrent)
                    {
                        if (slaveOwners.TryGetValue(pin, out var owner)
                            && owner != "Slave BCK" && owner != "Slave LRCK")
                            ownerLabel = owner;
                        else if (!HardwarePins.IsAudioCapable(lrck))
                            ownerLabel = "LRCK Conflict";
                        else if (slaveOwners.TryGetValue(lrck, out var nextOwner)
                                 && nextOwner != "Slave BCK" && nextOwner != "Slave LRCK")
                            ownerLabel = "LRCK Conflict";
                    }
                    item.Content = ownerLabel != null ? $"GPIO {pin} ({ownerLabel})" : $"GPIO {pin}";
                    item.IsEnabled = ownerLabel == null;
                }
                SelectPinInCombo(SlaveBckCombo, currentSlave);
            }

            // ── Input data pins — every pair the part has ─────────────
            // Each pair excludes only its own claim, so an active sibling's
            // pin still shows as taken. A parked pair's pin reserves nothing,
            // so a combo offers the pins other parked pairs hold too.
            if (Vm.InputI2sSupported)
            {
                int pairs = Math.Min(Vm.I2sMaxPairs, _rxCombos.Length);
                for (int pair = 0; pair < _rxCombos.Length; pair++)
                {
                    if (pair >= pairs) continue; // hidden card — skip
                    var combo = _rxCombos[pair];
                    var rxOwners = HardwarePins.BuildOwnerMap(Vm, excludeI2sRxPair: pair);
                    byte currentRx = Vm.I2sRxPinAt(pair);

                    for (int i = 0; i < combo.Items.Count; i++)
                    {
                        if (combo.Items[i] is not ComboBoxItem item || item.Tag is not byte pin) continue;

                        string? ownerLabel = null;
                        if (pin != currentRx && rxOwners.TryGetValue(pin, out var owner))
                            ownerLabel = owner;

                        item.Content = ownerLabel != null ? $"GPIO {pin} ({ownerLabel})" : $"GPIO {pin}";
                        item.IsEnabled = ownerLabel == null;
                    }
                    SelectPinInCombo(combo, currentRx);
                }
            }
        }
        finally { _suppress = false; }
    }

    // ── Live-apply handlers ──────────────────────────────────────────
    // Per-preset parameters — each control change writes through
    // immediately. Status text + revert-on-error mirror the legacy
    // dialog's pattern.

    private async void OnBckPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (BckPinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetI2SBckPin(newPin));
        if (status == PinConfigResult.Success)
        {
            BckPinCard.Description = $"LRCK auto-assigned to GPIO {newPin + 1} (BCK + 1).";
            // The PropertyChanged(I2SBckPin) queued from Vm.SetI2SBckPin
            // already triggers Refresh→RefreshConflicts on this page's
            // dispatcher, and RaisePinAssignmentsChanged notifies the
            // other Hardware pages. Items collection isn't touched
            // (only IsEnabled/Content), so the queued path is safe.
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"BCK pin set to GPIO {newPin}, LRCK = GPIO {newPin + 1}", false);
            return;
        }

        _suppress = true;
        SelectPinInCombo(BckPinCombo, Vm.I2SBckPin);
        _suppress = false;

        if (status == PinConfigResult.PinInUse)
        {
            ShowPinConflict(StatusText, StatusPinButton, HardwarePins.BuildAssignmentMap(Vm),
                $"GPIO {newPin} or {newPin + 1} is already in use",
                newPin, (byte)(newPin + 1));
        }
        else
        {
            ShowStatus(status switch
            {
                PinConfigResult.OutputActive => "All outputs must be S/PDIF before changing BCK pin",
                _ => $"Failed to set BCK pin (0x{status:X2})"
            }, true);
        }
    }

    /// <summary>Select the combo entry whose byte Tag matches
    /// <paramref name="pin"/>. No-op if no match — leaves the
    /// previous selection in place rather than blanking the combo.</summary>
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

    // ── Clock-mode handler ──────────────────────────────────────────────────

    private async void OnClockModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (ClockModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!byte.TryParse(tag, out var mode) || mode == Vm.I2sClockMode) return;
        ClearStatus();

        // Switching mode while an I2S output is live can emit sustained loud noise
        // from the DAC if wiring hasn't been adjusted — confirm first.
        if (Vm.AnySlotIsI2S)
        {
            var dialog = new ContentDialog
            {
                Title = "Change I2S clock mode?",
                Content = "One or more I2S outputs are active. Switching between Master and Slave "
                        + "modes may cause sustained loud noise from the connected DAC if the wiring "
                        + "has not been adjusted.",
                PrimaryButtonText = "Change Clock Mode",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _suppress = true;
                SelectByStringTag(ClockModeCombo, Vm.I2sClockMode);
                _suppress = false;
                return;
            }
        }

        await Task.Run(() => Vm.SetI2sClockMode(mode));
        // Slave mode releases the BCK/LRCK GPIOs, which the pin pages show as owners.
        HardwarePins.RaisePinAssignmentsChanged();
        ShowStatus($"I2S clock mode set to {(mode == 1 ? "Slave" : "Master")}", false);
    }

    // ── Clock-pin handlers ──────────────────────────────────────────────────

    private async void OnClockPinsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (ClockPinsCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!byte.TryParse(tag, out var mode)) return;
        ClearStatus();

        var status = await Task.Run(() => Vm.SetI2sClockPinMode(mode));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"Clock pins set to {(mode == 1 ? "Split" : "Unified")}", false);
            return;
        }
        _suppress = true;
        SelectByStringTag(ClockPinsCombo, Vm.I2sClockPinMode);
        _suppress = false;
        if (status == PinConfigResult.PinInUse)
        {
            ShowPinConflict(StatusText, StatusPinButton,
                HardwarePins.BuildAssignmentMap(Vm, excludeI2sBckSlaveSelf: true),
                "The slave clock pair overlaps another pin — free it first.",
                Vm.I2sBckPinSlave, (byte)(Vm.I2sBckPinSlave + 1));
            DispatcherQueue.TryEnqueue(Refresh);
            return;
        }
        ShowStatus(status switch
        {
            PinConfigResult.OutputActive => "Can't change clock pins while an I2S output is active.",
            _ => $"Failed to change clock pins (0x{status:X2})."
        }, true);
    }

    private async void OnSlaveBckChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (SlaveBckCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;
        ClearStatus();

        var status = await Task.Run(() => Vm.SetI2sBckPinSlave(newPin));
        if (status == PinConfigResult.Success)
        {
            SlaveBckCard.Description = $"LRCLK = GPIO {newPin + 1} (BCK + 1).";
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"Slave BCK set to GPIO {newPin}, LRCLK = GPIO {newPin + 1}", false);
            return;
        }
        _suppress = true;
        SelectPinInCombo(SlaveBckCombo, Vm.I2sBckPinSlave);
        _suppress = false;
        if (status == PinConfigResult.PinInUse)
        {
            ShowPinConflict(StatusText, StatusPinButton,
                HardwarePins.BuildAssignmentMap(Vm, excludeI2sBckSlaveSelf: true),
                $"GPIO {newPin} or {newPin + 1} is already in use",
                newPin, (byte)(newPin + 1));
            return;
        }
        ShowStatus(status switch
        {
            _ => $"Failed to set slave BCK pin (0x{status:X2})"
        }, true);
    }

    // ── Input handlers ──────────────────────────────────────────────────────

    private async void OnChannelsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (ChannelsCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string s || !int.TryParse(s, out int count)) return;
        if (count == Vm.I2sInputChannels) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetI2sInputChannels(count));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"{count} channels ({count / 2} pair{(count / 2 == 1 ? "" : "s")})", false);
        }
        else if (status == PinConfigResult.PinInUse)
        {
            await PromptForPairPinAsync(count);
        }
        else
        {
            ShowStatus(status switch
            {
                PinConfigResult.InvalidOutput => "Multichannel I2S isn't supported on this device",
                _ => $"Failed to set channel count (0x{status:X2})"
            }, true);
        }
        DispatcherQueue.TryEnqueue(Refresh);
    }

    /// <summary>Raising the channel count activates the pairs above the current
    /// one, and one of their data pins is taken. Work out which pair, offer a free
    /// pin for it, and raise the count once it has moved.</summary>
    private async Task PromptForPairPinAsync(int count)
    {
        if (Vm == null) return;
        // No self-exclusion: we are looking for whatever blocks a pair that is
        // not on yet, so every pair currently holding a pin has to be visible.
        // excludeI2sRxSelf hides pair 0 — pass it here and pair 0's GPIO reads as
        // free and gets offered as the fix for a clash with pair 0.
        var claims = HardwarePins.BuildAssignmentMap(Vm);

        // Every pair the raise brings up, not merely the first that clashes: three
        // pairs can be blocked by three different things, and fixing one at a time
        // would just be refused on the next with no sign of how many remain.
        var blocked = new List<int>();
        var rows = new List<PinReassignment>();
        int oldPairs = Vm.I2sActivePairs;
        for (int pair = oldPairs; pair < count / 2; pair++)
        {
            byte pin = Vm.I2sRxPinAt(pair);
            if (claims.TryGetValue(pin, out var owner))
            {
                blocked.Add(pair);
                rows.Add(new PinReassignment(PairLabel(pair), pin, owner));
                continue;
            }
            // Two pairs the raise brings up on one parked GPIO clash with each
            // other too (firmware check_i2s_rx_pin with the new count).
            for (int other = oldPairs; other < pair; other++)
            {
                if (Vm.I2sRxPinAt(other) != pin) continue;
                blocked.Add(pair);
                rows.Add(new PinReassignment(PairLabel(pair), pin,
                    new PinAssignment(pin, PairLabel(other), PinRole.Input, Id)));
                ShowStatus($"GPIO {pin} is set on both {PairLabel(other)} and {PairLabel(pair)}", true);
                break;
            }
        }
        if (rows.Count == 0)
        {
            ShowStatus("A pair's data pin conflicts — assign different GPIOs first", true);
            return;
        }

        // Candidates: what reserves a pin once the count is raised. That is
        // everything active now plus the pairs the raise brings up that keep
        // their pins; parked pairs above the new count reserve nothing. The
        // pairs being moved are excluded, since they are what we reassign; a
        // pair that stays keeps its pin even when a moving pair shared it.
        var rxClaims = HardwarePins.BuildAssignmentMap(Vm);
        for (int pair = 0; pair < count / 2; pair++)
        {
            if (blocked.Contains(pair)) continue;
            byte pin = Vm.I2sRxPinAt(pair);
            if (!rxClaims.ContainsKey(pin))
                ((Dictionary<byte, PinAssignment>)rxClaims)[pin] =
                    new PinAssignment(pin, PairLabel(pair), PinRole.Input, Id);
        }

        await PinConflictPrompt.ShowAsync(XamlRoot, rows, rxClaims, HardwarePins.ValidPins,
            async pins =>
            {
                for (int i = 0; i < blocked.Count; i++)
                    if (await Task.Run(() => Vm.SetI2sRxPin(pins[i], blocked[i])) != PinConfigResult.Success)
                        return false;
                if (await Task.Run(() => Vm.SetI2sInputChannels(count)) != PinConfigResult.Success) return false;
                HardwarePins.RaisePinAssignmentsChanged();
                ShowStatus($"{count} channels, {blocked.Count} pin{(blocked.Count == 1 ? "" : "s")} reassigned", false);
                return true;
            });
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private async void OnI2sRxPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (sender is not ComboBox combo) return;
        int pair = combo.Tag is int t ? t : 0;
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetI2sRxPin(newPin, pair));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"{PairLabel(pair)} pin set to GPIO {newPin}", false);
            return;
        }

        _suppress = true;
        SelectPinInCombo(combo, Vm.I2sRxPinAt(pair));
        _suppress = false;

        if (status == PinConfigResult.PinInUse)
        {
            ShowPinConflict(StatusText, StatusPinButton,
                HardwarePins.BuildAssignmentMap(Vm, excludeI2sRxPair: pair),
                $"GPIO {newPin} is already in use", newPin);
        }
        else
        {
            ShowStatus(status switch
            {
                PinConfigResult.InvalidPin => $"GPIO {newPin} is not a valid pin",
                _ => $"Failed to set I2S RX pin (0x{status:X2})"
            }, true);
        }
    }

    /// <summary>Numbered wherever the part has more than one pair, rather than
    /// wherever more than one is currently on: a label that only gains its number
    /// once a second pair is up is unnumbered during the very operation that adds
    /// one, which is when telling them apart matters most.</summary>
    private string PairLabel(int pair) =>
        Vm != null && Vm.I2sMaxPairs > 1 ? $"Serial Data {pair + 1}" : "I2S RX data";

    private void SelectChannelCount(int count)
    {
        for (int i = 0; i < ChannelsCombo.Items.Count; i++)
        {
            if (ChannelsCombo.Items[i] is ComboBoxItem item
                && int.TryParse(item.Tag?.ToString(), out var c) && c == count)
            {
                ChannelsCombo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Select the combo item whose string Tag ("0"/"1") equals the byte
    /// <paramref name="value"/>.</summary>
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

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "hardware.i2s";
    public string Title => "I2S";
    public SettingsCategory Category => SettingsCategory.System;
    public string IconGlyph => ""; // SoundLevels (waveform)
    public int Order => 30;
    // The clock pins exist on every build, so the page always has something to
    // show even where the input half is absent.
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareI2SPage();
        p.Attach(vm, tracker);
        return p;
    }
}

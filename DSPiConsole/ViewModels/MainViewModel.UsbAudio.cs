using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Services;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// USB input channel count sourced from Windows. The DSPi appears to Windows as a
/// playback (render) endpoint; the channel count of its selected format — the
/// alt-mode the user picks in Sound Settings — is how many channels the PC streams
/// into the DSPi over USB. We read it from the OS (Core Audio) rather than USB
/// alt-mode notifications so the displayed input count stays accurate to the user's
/// format selection even with no audio playing (matching the macOS app).
/// </summary>
public partial class MainViewModel
{
    /// <summary>Number of USB input channels the PC is configured to stream to the
    /// DSPi (from the Windows render endpoint's selected format). Falls back to the
    /// device-reported input count if the endpoint can't be read.</summary>
    [ObservableProperty]
    private int _usbInputChannelCount = 2;

    /// <summary>Re-read the USB input channel count from the Windows audio endpoint.
    /// Does the COM query off the UI thread; marshals the property update back.
    /// Blocking on the caller — invoke via Task.Run.</summary>
    public void RefreshUsbInputChannelCount()
    {
        // The board selected here, never whichever DSPi Windows outputs to.
        var serial = _device.SelectedDeviceInfo?.Serial;
        int maxInputs = Math.Max(1, InputChannelCountForPlatform(Platform));
        // A device on another hub has no Windows endpoint on this PC: its USB
        // format is chosen on the computer it is plugged into.
        int? fromWindows = _device.IsRemote ? null : WindowsAudioDevices.GetDspiRenderChannelCount(serial);
        int count = fromWindows ?? _device.NumInputChannels;
        count = Math.Clamp(count, 1, maxInputs);

        _dispatcher.TryEnqueue(() =>
        {
            // A switch to another board while this read was out: its own read follows.
            if (_device.SelectedDeviceInfo?.Serial != serial) return;
            if (count != UsbInputChannelCount) UsbInputChannelCount = count;
        });
    }

    partial void OnUsbInputChannelCountChanged(int value) => RaiseActiveInputsChanged();
    partial void OnActiveInputSourceChanged(Usb.InputSource value) => RaiseActiveInputsChanged();

    /// <summary>Number of input channels the UI currently shows. Driven by the
    /// active input source: USB uses the Windows format count; I2S uses its
    /// channel count; ADAT is 8; SPDIF is stereo. Always at least the stereo pair,
    /// clamped to the platform's input capability (2 on RP2040, up to 8 on RP2350).</summary>
    public int ActiveInputChannelCount
    {
        get
        {
            int max = Math.Min(InputChannelCountForPlatform(Platform), Channel.AllInputs.Count);
            if (max <= 2) return 2;
            int n = ActiveInputSource switch
            {
                InputSource.Usb => UsbInputChannelCount,
                InputSource.I2s => I2sInputChannels,
                InputSource.Adat => 8,
                _ => 2   // SPDIF / SPDIF2 / SPDIF3 are stereo
            };
            return Math.Clamp(n, 2, max);
        }
    }

    /// <summary>The input channels to display (first <see cref="ActiveInputChannelCount"/>
    /// of <see cref="Channel.AllInputs"/>).</summary>
    public IReadOnlyList<Channel> ActiveInputs
    {
        get
        {
            int n = ActiveInputChannelCount;
            var list = new List<Channel>(n);
            for (int i = 0; i < n && i < Channel.AllInputs.Count; i++)
                list.Add(Channel.AllInputs[i]);
            return list;
        }
    }

    /// <summary>Notify the UI that the active-input set changed (called from the
    /// USB-count, input-source, I2S-count and platform change paths).</summary>
    internal void RaiseActiveInputsChanged()
    {
        OnPropertyChanged(nameof(ActiveInputChannelCount));
        OnPropertyChanged(nameof(ActiveInputs));
        // The upmixer only runs on a stereo input, so the derived matrix rows
        // (C/Ls/Rs) appear and vanish with the active input set.
        RaiseUpmixRowsChanged();
    }
}

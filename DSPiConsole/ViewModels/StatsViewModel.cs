using System.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;
using Microsoft.UI.Dispatching;

namespace DSPiConsole.ViewModels;

/// <summary>
/// The statistics window's data, after the macOS Console's StatsViewModel:
/// counters, system figures and the optional inputs every 2 s, and the buffer
/// fill levels every 60 ms into a 15 s history. Reads happen on timer threads;
/// <see cref="Changed"/> and <see cref="BufferChanged"/> are raised on the UI
/// thread after the values land.
/// </summary>
public sealed class StatsViewModel : IDisposable
{
    private readonly DspDevice _device;
    private readonly DispatcherQueue _dispatcher;
    private readonly System.Timers.Timer _pollTimer = new(2000) { AutoReset = true };
    private readonly System.Timers.Timer _bufferTimer = new(60) { AutoReset = true };
    private int _polling, _bufferPolling;
    private bool _disposed, _deviceInfoFetched, _wasConnected, _everConnected;
    private uint? _previousStarvationTotal;
    /// <summary>Firmware with the output latency setting reports buffer fill on
    /// a 768-frame scale over 512-frame rings; the rows show it against the ring.
    /// Set by the 2 s poll from the latency status read.</summary>
    private volatile bool _fillOnRingScale;
    /// <summary>The latency status has been read on this connection: a later
    /// failed read is a transient (the outputs restarting) rather than older
    /// firmware, so the last figures and the ring scale stay.</summary>
    private volatile bool _ringHealthKnown;

    public BufferFillHistory BufferHistory { get; } = new();

    // ── Device ──
    public string Platform { get; private set; } = "-";
    public string Firmware { get; private set; } = "-";
    public string Serial { get; private set; } = "-";
    /// <summary>Reconnections since the window opened.</summary>
    public int ReconnectCount { get; private set; }
    public bool IsConnected { get; private set; }

    // ── System ──
    public uint ClockHz { get; private set; }
    public uint CoreMillivolts { get; private set; }
    public uint SampleRateHz { get; private set; }
    public int TempCentiC { get; private set; }

    // ── Counters ──
    public uint PdmRingOverruns { get; private set; }
    public uint PdmRingUnderruns { get; private set; }
    public uint PdmDmaOverruns { get; private set; }
    public uint PdmDmaUnderruns { get; private set; }
    /// <summary>Status index 8: USB packet gaps over 2 ms. Its partner index 7
    /// (pool take failures) is retired in firmware 1.1.6 and always reads 0, so
    /// it is no longer read or shown.</summary>
    public uint SpdifUnderruns { get; private set; }
    public uint UsbRingOverruns { get; private set; }

    // ── S/PDIF DMA starvation ──
    public uint StarvationTotal { get; private set; }
    public uint[] StarvationPerInstance { get; private set; } = new uint[4];
    public uint StarvationDelta { get; private set; }
    public DateTime? StarvationLastEvent { get; private set; }
    public DateTime? StarvationPreviousEvent { get; private set; }

    // ── Buffers ──
    public BufferStatsPacket Buffers { get; private set; } = new();

    // ── Output ring health (firmware 1.1.6) ──
    /// <summary>Null on firmware without the output latency setting, which
    /// answers status 26-33 with zeros rather than a STALL.</summary>
    public RingHealth? RingHealth { get; private set; }

    // ── Optional inputs and interfaces ──
    public bool InputSourceSupported { get; private set; }
    public SpdifRxStatus SpdifRx { get; private set; }
    public byte[]? SpdifChannelStatus { get; private set; }
    public byte SpdifActivePin { get; private set; }
    public (bool Enabled, bool Present, byte Volume, bool Muted)? LgSoundSync { get; private set; }
    public AdatStatus? Adat { get; private set; }
    public I2sSlaveStatus? I2sSlave { get; private set; }

    /// <summary>The 2 s figures changed.</summary>
    public event Action? Changed;
    /// <summary>A buffer sample landed.</summary>
    public event Action? BufferChanged;

    public StatsViewModel(DspDevice device)
    {
        _device = device;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        IsConnected = _wasConnected = _everConnected = device.IsConnected;
        _device.PropertyChanged += OnDevicePropertyChanged;
        _pollTimer.Elapsed += (_, _) => Poll();
        _bufferTimer.Elapsed += (_, _) => PollBuffers();
        _pollTimer.Start();
        _bufferTimer.Start();
        Task.Run(Poll);
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DspDevice.IsConnected)) return;
        bool connected = _device.IsConnected;
        _dispatcher.TryEnqueue(() =>
        {
            // The first connection seen is not a reconnect.
            if (connected && !_wasConnected && _everConnected) ReconnectCount++;
            _everConnected |= connected;
            _wasConnected = IsConnected = connected;
            if (connected) _deviceInfoFetched = false;
            // Another device may lack the latency setting; let it prove it.
            _ringHealthKnown = false;
            // A rebooted device counts starvation from zero again.
            _previousStarvationTotal = null;
            Changed?.Invoke();
        });
    }

    private void FetchDeviceInfo()
    {
        var info = _device.GetDeviceInfo();
        var serial = _device.GetDeviceSerial();
        // The input-source feature gates the S/PDIF input section.
        bool inputSource = _device.GetInputSource() != null;
        if (info == null && serial == null) return;
        _deviceInfoFetched = true;
        _dispatcher.TryEnqueue(() =>
        {
            Platform = info?.Platform ?? "-";
            Firmware = info?.FirmwareVersion ?? "-";
            Serial = serial ?? "-";
            InputSourceSupported = inputSource;
        });
    }

    private void Poll()
    {
        if (_disposed || !_device.IsConnected) return;
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            if (!_deviceInfoFetched) FetchDeviceInfo();
            uint U(ushort v) => _device.GetStatusUInt32(v) ?? 0;
            uint clock = U(13), mv = U(14), rate = U(15);
            int temp = _device.GetStatusInt32(16) ?? 0;
            uint ringOver = U(3), ringUnder = U(4), dmaOver = U(5), dmaUnder = U(6);
            uint spdifUnder = U(8), usbOver = U(22);

            // Starvation: per-instance counters once there has been any. A failed
            // read keeps the last figures rather than reading as a reset to 0.
            uint? read = _device.GetStatusUInt32(17);
            uint total = read ?? StarvationTotal, delta = 0;
            var perInstance = StarvationPerInstance;
            if (read is { } t)
            {
                // A total below the last one means the counter was reset.
                if (_previousStarvationTotal is { } prev) delta = t >= prev ? t - prev : t;
                _previousStarvationTotal = t;
                perInstance = new uint[4];
                if (t > 0) for (int i = 0; i < 4; i++) perInstance[i] = U((ushort)(18 + i));
            }

            SpdifRxStatus rx = default;
            byte[]? channelStatus = null;
            byte activePin = SpdifActivePin;
            if (InputSourceSupported && _device.GetSpdifRxStatus() is { } r)
            {
                rx = r;
                if (r.State == SpdifInputState.Locked) channelStatus = _device.GetSpdifRxChannelStatus();
                // The GPIO of whichever S/PDIF input is active: inputs 2-4 have pins of their own.
                int src = (int)r.ActiveSource, ext = src - (int)InputSource.Spdif2 + 1;
                int index = ext is >= 1 and <= 3 ? ext : 0;
                if (_device.GetSpdifRxPin(index) is { } pin && pin != 0) activePin = pin;
            }
            var ring = ReadRingHealth();
            var lg = _device.GetLgSoundSyncStatus();
            var adat = _device.GetAdatStatus();
            var i2s = _device.GetI2SSlaveStatus();
            var now = DateTime.Now;

            _dispatcher.TryEnqueue(() =>
            {
                ClockHz = clock; CoreMillivolts = mv; SampleRateHz = rate; TempCentiC = temp;
                PdmRingOverruns = ringOver; PdmRingUnderruns = ringUnder;
                PdmDmaOverruns = dmaOver; PdmDmaUnderruns = dmaUnder;
                SpdifUnderruns = spdifUnder; UsbRingOverruns = usbOver;
                StarvationTotal = total;
                StarvationPerInstance = perInstance;
                StarvationDelta = delta;
                if (delta > 0)
                {
                    StarvationPreviousEvent = StarvationLastEvent;
                    StarvationLastEvent = now;
                }
                RingHealth = ring;
                SpdifRx = rx;
                SpdifChannelStatus = channelStatus;
                SpdifActivePin = activePin;
                LgSoundSync = lg;
                // ADAT output exists on the RP2350 only; RP2040 answers zeros.
                Adat = adat != null && Platform == "RP2350" ? adat : null;
                I2sSlave = i2s;
                Changed?.Invoke();
            });
        }
        catch { }
        finally { Volatile.Write(ref _polling, 0); }
    }

    /// <summary>The output rings' counters, gated on the latency status read,
    /// which also supplies the fill target. Port of the macOS Console's
    /// fetchRingHealth, plus indices 32 and 33.</summary>
    private RingHealth? ReadRingHealth()
    {
        if (_device.GetOutputLatencyStatus() is not { } latency)
        {
            if (_ringHealthKnown) return RingHealth;
            _fillOnRingScale = false;
            return null;
        }
        _ringHealthKnown = true;
        _fillOnRingScale = true;
        uint U(ushort v) => _device.GetStatusUInt32(v) ?? 0;
        uint? lowest = _device.GetStatusUInt32(28);
        return new RingHealth(
            TargetFrames: latency.TargetFrames,
            TargetUs: latency.EffectiveUs,
            BatchFrames: latency.BatchFrames,
            UnderrunEpisodes: U(26),
            RefusedBlocks: U(27),
            LowestPrewriteFill: lowest is { } l && l != Core.Models.RingHealth.NoFill ? l : null,
            WorstInputGapUs: U(29),
            WorstUsbPacketGapUs: U(30),
            OverBudgetSteps: U(31),
            SpdifInputRateMilliHz: U(32),
            VcxoTrimCentiPpm: _device.GetStatusInt32(33) ?? 0);
    }

    private void PollBuffers()
    {
        if (_disposed || !_device.IsConnected) return;
        if (Interlocked.Exchange(ref _bufferPolling, 1) == 1) return;
        try
        {
            if (_device.GetBufferStats() is not { } packet) return;
            if (_fillOnRingScale)
                for (int i = 0; i < Math.Min((int)packet.NumSpdif, packet.Spdif.Length); i++)
                    packet.Spdif[i] = packet.Spdif[i].RescaledToRing();
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed) return;
                Buffers = packet;
                BufferHistory.Append(packet);
                BufferChanged?.Invoke();
            });
        }
        catch { }
        finally { Volatile.Write(ref _bufferPolling, 0); }
    }

    public void ResetWatermarks() => Task.Run(() =>
    {
        if (!_disposed && _device.IsConnected) _device.ResetBufferStats();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.PropertyChanged -= OnDevicePropertyChanged;
        _pollTimer.Stop();
        _bufferTimer.Stop();
        _pollTimer.Dispose();
        _bufferTimer.Dispose();
    }
}

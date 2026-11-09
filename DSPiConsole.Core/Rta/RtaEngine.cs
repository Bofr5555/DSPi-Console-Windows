using System.Diagnostics;

namespace DSPiConsole.Core.Rta;

/// <summary>The vendor transfers the analyser needs. The device implements it;
/// tests substitute a fake.</summary>
public interface IRtaTransport
{
    /// <summary>An IN transfer, or null on a STALL or failure.</summary>
    byte[]? RtaIn(byte request, ushort value, int length);
    bool RtaOut(byte request, ushort value, byte[] data);
}

/// <summary>Who chose the configuration the device holds. The last console to
/// push one drives and every other console follows (firmware
/// spectrum_analyser_spec.md §7; the macOS Console's rta_sharing_spec.md).</summary>
/// <param name="IsFollowing">False: the device holds what this console asked for.</param>
/// <param name="Origin">The Link session that chose it while following; 0 when
/// the writer is unknown (a UART or I2C host, or firmware without the event).</param>
public readonly record struct RtaOwnership(bool IsFollowing, uint Origin = 0)
{
    public static RtaOwnership Driving => default;
    public static RtaOwnership Following(uint origin) => new(true, origin);
}

/// <summary>Everything the engine holds after a poll. Immutable: the engine
/// swaps in a new one, so a renderer can read it from any thread.</summary>
public sealed record RtaSnapshot
{
    public static RtaSnapshot Empty { get; } = new();

    /// <summary>Latest band frame per channel at <see cref="Tap"/>.</summary>
    public IReadOnlyDictionary<byte, RtaBandFrame> Frames { get; init; } = new Dictionary<byte, RtaBandFrame>();
    public RtaBinFrame? Bins { get; init; }
    public RtaStatus Status { get; init; } = new();
    /// <summary>The tap the frames were taken at. A view at the other tap must
    /// not draw them as its own: channel 0 means something else there.</summary>
    public byte Tap { get; init; } = RtaWire.TapOutput;

    /// <summary>The latest frame for a channel, only when taken at that tap.</summary>
    public RtaBandFrame? Frame(int channel, byte tap) =>
        Tap == tap && channel is >= 0 and < 256 && Frames.TryGetValue((byte)channel, out var f) ? f : null;

    /// <summary>Bands in the latest frames; 0 until one arrives.</summary>
    public int BandCount => Frames.Count == 0 ? 0 : Frames.Values.Max(f => (int)f.NBands);
}

/// <summary>The frame-derived state that decides a display's layout rather than
/// what it shows; it changes with the configuration or the rate, not per frame.
/// A reference type, so a reader on another thread never sees half of one.</summary>
public sealed record RtaDisplayState(byte Tap, uint SampleRateHz, int FirstResolvedBand, int BandCount)
{
    public static RtaDisplayState Initial { get; } = new(RtaWire.TapOutput, 0, 0, 0);

    public static RtaDisplayState From(RtaSnapshot s) =>
        new(s.Tap, s.Status.SampleRateHz, s.Status.FirstResolvedBand, s.BandCount);
}

/// <summary>
/// The app's half of the spectrum analyser: it owns the device configuration,
/// polls the band and bin frames, and republishes them. Port of the macOS
/// Console's RtaEngine.
/// <para>
/// Each on-screen analyser subscribes with what it wants; the engine folds the
/// requests into the one configuration the device's single FFT can run, the
/// newest request choosing the tap. <see cref="Tick"/> runs from the view
/// model's existing status poll, so all vendor traffic stays in one order, and
/// does nothing while nobody watches. Dropping the last subscription stops the
/// analyser on the device, which is how it costs nothing when nobody looks.
/// </para>
/// <para>
/// The device has one configuration and other consoles share it. There is no
/// reservation: the last to push drives, and the rest follow. The firmware
/// announces every applied configuration (<see cref="DeviceAnnounced"/>); one
/// this console did not ask for is followed, with no push and no rejection,
/// until the user chooses something here or presses Take control. On firmware
/// without the announcement, a readback that agreed and then changed means the
/// same thing.
/// </para>
/// <para>
/// Frames never go through property-change notification: the latest
/// <see cref="Snapshot"/> is swapped in whole and <see cref="FrameVersion"/>
/// advances when the picture changed, for renderers to read as they draw.
/// <see cref="StateChanged"/> and <see cref="TelemetryChanged"/> are raised on
/// whichever thread made the change; listeners marshal to the UI themselves.
/// </para>
/// </summary>
public sealed class RtaEngine
{
    private readonly IRtaTransport _transport;
    private readonly Func<double> _clock;
    private readonly object _lock = new();
    private readonly object _publishLock = new();

    private readonly Dictionary<Guid, (long Seq, RtaRequest Request)> _requests = new();
    private long _requestSeq;
    /// <summary>What we believe the device applied; null means push again.</summary>
    private RtaConfig? _appliedConfig;
    /// <summary>The last config sent, so asking for something different resets
    /// the give-up count rather than inheriting it.</summary>
    private RtaConfig? _lastAttempted;
    private int _pushAttempts;

    // Sharing with other consoles.
    /// <summary>The configuration someone else pushed, while following it.</summary>
    private (RtaConfig Config, uint Origin)? _following;
    /// <summary>The configuration this console's views last asked for, kept
    /// while none watches, so a display shown again is told apart from a new
    /// choice and announcements received meanwhile are judged against it.</summary>
    private RtaConfig? _lastOwn;
    /// <summary>A readback agreed since the last new push. Only then does a
    /// disagreeing readback mean another host rather than a refusal.</summary>
    private bool _everAgreed;
    /// <summary>The device's clamped averaging for the confirmed configuration,
    /// which the bins are averaged with.</summary>
    private ushort? _adoptedAvgMs;
    /// <summary>A push not yet confirmed by an announcement or a readback.</summary>
    private bool _pushOutstanding;
    /// <summary>An announcement arrived on this connection, so the periodic
    /// GET_CONFIG readback is needed only to confirm a push.</summary>
    private bool _eventSeen;
    /// <summary>Bumped by anything that changes ownership, so a readback taken
    /// before the change is not applied after it.</summary>
    private long _stateEpoch;

    private RtaOptions _pollOptions = new();
    /// <summary>How long the device takes to publish one frame, from the
    /// status; the bin reads follow it.</summary>
    private double _pollFrameInterval = 1024.0 / 48000.0;
    private double _lastBinRead = double.NegativeInfinity;
    private double _lastStatusRead = double.NegativeInfinity;
    private double _lastConfigRead = double.NegativeInfinity;
    /// <summary>How often the configuration is read back even once the device
    /// announces it.</summary>
    private const double ConfigBackstopInterval = 5.0;
    /// <summary>The followed configuration's transform size (a boxed byte),
    /// null while driving; read by the drawing code without the lock.</summary>
    private volatile object? _analysedOrder;
    private readonly RtaBinAverage _binAverage = new();
    private int _ticking;
    private int _generation;
    /// <summary>Serialises the caps probe's results against a disconnect, so a
    /// probe that finishes as the device goes cannot mark it supported.</summary>
    private readonly object _capsLock = new();

    private volatile RtaSnapshot _snapshot = RtaSnapshot.Empty;
    private long _frameVersion;

    public RtaEngine(IRtaTransport transport, Func<double>? clock = null)
    {
        _transport = transport;
        _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    // ── Published state ──

    /// <summary>True once GET_CAPS answered with a complete centre table.
    /// Firmware without the analyser STALLs it: the whole feature gate.</summary>
    public bool Supported { get; private set; }
    public RtaCaps Caps { get; private set; } = new();
    /// <summary>Nominal third-octave centres from the caps table, so axis labels
    /// and band edges share one source.</summary>
    public IReadOnlyList<double> BandCentresHz { get; private set; } = Array.Empty<double>();
    /// <summary>The device-side options the app is asking for.</summary>
    public RtaOptions Options { get; private set; } = new();
    /// <summary>Set when the device has refused the configuration three times;
    /// views show the reason rather than an empty graph.</summary>
    public bool ConfigRejected { get; private set; }
    /// <summary>Whether this console drives the analyser or follows another.</summary>
    public RtaOwnership Ownership { get; private set; }
    /// <summary>The device's tap and channels while following, null while
    /// driving. Views draw this instead of their own selection, so they show
    /// what the analyser is doing, but keep registering their own requests.</summary>
    public RtaChannelSelection? EffectiveSelection { get; private set; }
    public RtaDisplayState Display { get; private set; } = RtaDisplayState.Initial;

    /// <summary>The latest frames, bins and status.</summary>
    public RtaSnapshot Snapshot => _snapshot;
    /// <summary>Advances whenever what a display draws changed.</summary>
    public long FrameVersion => Interlocked.Read(ref _frameVersion);

    /// <summary>Supported, caps, options, display state, rejection or
    /// ownership changed.</summary>
    public event Action? StateChanged;
    /// <summary>The status changed (about twice a second while running).</summary>
    public event Action? TelemetryChanged;

    public bool IsWatching { get { lock (_lock) return _requests.Count > 0; } }

    // ── Subscription ──

    /// <summary>Start watching; hand the token back to <see cref="Release"/>.</summary>
    public Guid Subscribe(RtaRequest request)
    {
        var id = Guid.NewGuid();
        Update(id, request);
        return id;
    }

    /// <summary>Change what a subscription wants without losing its place. An
    /// unchanged request keeps its sequence, so republishing the same thing
    /// does not steal the tap from a newer view.</summary>
    public void Update(Guid id, RtaRequest request)
    {
        lock (_lock)
        {
            if (_requests.TryGetValue(id, out var existing) && existing.Request == request) return;
            _requests[id] = (++_requestSeq, request);
            // Only a change of what this console asks for takes the analyser
            // back. Showing another display of the same selection (the graph,
            // the bars, the window) is not a choice, and must not push over
            // a console we are following.
            if (OwnConfigLocked() is { } mine)
            {
                if (mine != _lastOwn) TakeControlLocked();
                _lastOwn = mine;
            }
        }
        PublishSharing();
    }

    public void Release(Guid id)
    {
        bool empty;
        lock (_lock)
        {
            if (!_requests.Remove(id)) return;
            empty = _requests.Count == 0;
            // Ownership survives the last view going away: the device keeps
            // its configuration through STOP and auto-off, and announcements
            // keep arriving, so a display shown again resumes driving or
            // following rather than pushing over another console.
            if (empty) _stateEpoch++;
        }
        if (!empty) return;
        // Nobody is watching. The device would stop itself in five seconds;
        // stopping it now hands the CPU back at once. A short grace lets a
        // view that is being rebuilt subscribe again first, since a STOP
        // would clear the device's averaging and blink the picture.
        Task.Run(async () =>
        {
            await Task.Delay(300);
            if (IsWatching) return;
            _transport.RtaIn(RtaWire.Control, RtaWire.CtlStop, 1);
            // A view can come back before STOP completes; never clear data
            // that now belongs to it.
            if (IsWatching) return;
            lock (_publishLock)
            {
                _binAverage.Reset();
                Publish(RtaSnapshot.Empty);
            }
        });
    }

    /// <summary>Clear the running averages (the device's bands and the bins
    /// here) and the peak hold without disturbing the frame in flight.</summary>
    public void ResetAveraging()
    {
        lock (_publishLock) _binAverage.Reset();
        Task.Run(() => _transport.RtaIn(RtaWire.Control, RtaWire.CtlResetAvg, 1));
    }

    /// <summary>Adopt new device-side options, pushed on the next tick.</summary>
    public void SetOptions(RtaOptions options)
    {
        lock (_lock)
        {
            if (options == Options) return;
            Options = options;
            _pollOptions = options;
            ResetPush();
            // A device-side option is a choice too.
            TakeControlLocked();
            // Remembered with the new options, so a display shown next with
            // the same selection is not taken for another choice.
            _lastOwn = OwnConfigLocked()
                ?? (_lastOwn is { } l ? l with { FftOrder = options.FftOrder, AvgMs = options.AvgMs, PeakDecayDbS = options.PeakDecayDbS } : null);
            ConfigRejected = false;
        }
        Interlocked.Increment(ref _frameVersion);
        PublishSharing(force: true);
    }

    /// <summary>Push this console's own configuration again after following
    /// someone else's: the Take control button.</summary>
    public void TakeControl()
    {
        lock (_lock) TakeControlLocked();
        PublishSharing();
    }

    /// <summary>Stop following; the next tick pushes our own configuration.
    /// Caller holds <see cref="_lock"/>.</summary>
    private void TakeControlLocked()
    {
        _stateEpoch++;
        if (_following == null) return;
        _following = null;
        ResetPush();
    }

    /// <summary>Push the configuration again on the next tick, after a device
    /// switch or anything else that could have reset the device's engine.</summary>
    public void ConfigurationChanged()
    {
        bool wasRejected;
        lock (_lock)
        {
            ResetPush();
            ForgetSharingLocked();
            wasRejected = ConfigRejected;
            ConfigRejected = false;
        }
        PublishSharing(force: wasRejected);
    }

    private void ResetPush()
    {
        _appliedConfig = null;
        _lastAttempted = null;
        _pushAttempts = 0;
    }

    /// <summary>Everything learned about other consoles on this connection.
    /// Caller holds <see cref="_lock"/>.</summary>
    private void ForgetSharingLocked()
    {
        _following = null;
        _everAgreed = false;
        _pushOutstanding = false;
        _eventSeen = false;
        _stateEpoch++;
    }

    // ── Sharing ──

    /// <summary>
    /// A NOTIFY_EVT_RTA_CONFIG from the device: the configuration it just
    /// applied. <paramref name="own"/> when this console wrote it (over USB,
    /// a host-sourced write; over Link, our own session). <paramref name="origin"/>
    /// is the writing session as far as it is known, 0 otherwise. Any thread.
    /// </summary>
    public void DeviceAnnounced(RtaConfig config, bool own, uint origin = 0)
    {
        bool changed = false;
        lock (_lock)
        {
            _eventSeen = true;
            _stateEpoch++;
            // While no view watches, judge it against what we last asked for,
            // so a display shown later follows a change made meanwhile.
            if ((OwnConfigLocked() ?? _lastOwn) is { } mine)
            {
                bool clearRejected = false;
                if (config.Matches(mine))
                {
                    // Our echo, or someone else choosing exactly what we want.
                    _following = null;
                    // Confirm only what we sent. After an averaging or decay
                    // change not yet pushed, an earlier push's echo still
                    // matches on tap, mask and size, and confirming it would
                    // swallow the change.
                    if (_appliedConfig == mine || _lastAttempted == mine) ConfirmLocked(mine, config);
                    clearRejected = true;
                }
                else if (!own)
                {
                    _following = (config, origin);
                    _pushOutstanding = false;
                    clearRejected = true;
                }
                // An echo of ours that no longer matches is an older push,
                // superseded by one still in flight.

                // Cleared under the lock, so a tick that has just decided
                // "rejected" cannot set it again after this.
                if (clearRejected && ConfigRejected)
                {
                    ConfigRejected = false;
                    changed = true;
                }
            }
        }
        PublishSharing(force: changed);
    }

    /// <summary>The configuration this console's own views ask for: the newest
    /// request's tap, and its mask alone when it is one channel (a fine-curve
    /// view), else every request's mask at that tap. Every view registers the
    /// same selection, so the two differ only while a change reaches them one
    /// by one. Null while nobody watches or the mask is empty. Caller holds
    /// <see cref="_lock"/>.</summary>
    private RtaConfig? OwnConfigLocked()
    {
        if (_requests.Count == 0) return null;
        var primary = _requests.Values.MaxBy(r => r.Seq).Request;
        ushort mask = System.Numerics.BitOperations.PopCount(primary.Mask) == 1
            ? primary.Mask
            : (ushort)_requests.Values.Where(r => r.Request.Tap == primary.Tap).Aggregate(0, (m, r) => m | r.Request.Mask);
        if (mask == 0) return null;
        return new RtaConfig(primary.Tap, mask, _pollOptions.FftOrder, _pollOptions.AvgMs, _pollOptions.PeakDecayDbS, 0);
    }

    /// <summary>The device holds what we want: adopt its clamped averaging and
    /// stop pushing. <see cref="_appliedConfig"/> keeps the values we asked
    /// for, so a clamped one does not read as a difference and get pushed on
    /// every tick. Caller holds <see cref="_lock"/>.</summary>
    private void ConfirmLocked(RtaConfig mine, RtaConfig device)
    {
        _appliedConfig = mine;
        _adoptedAvgMs = device.AvgMs;
        _pushAttempts = 0;
        _everAgreed = true;
        _pushOutstanding = false;
    }

    /// <summary>Publish who drives and what the views should draw, raising
    /// <see cref="StateChanged"/> when either changed (or when
    /// <paramref name="force"/>d by another change). Reads the state at call
    /// time, so a late tick cannot publish an ownership already replaced.</summary>
    private void PublishSharing(bool force = false)
    {
        bool changed;
        lock (_lock)
        {
            var f = _following;
            var ownership = f is { } x ? RtaOwnership.Following(x.Origin) : RtaOwnership.Driving;
            var selection = f is { } y ? RtaChannelSelection.FromMask(y.Config.Tap, y.Config.ChannelMask) : null;
            changed = ownership != Ownership || !Equals(selection, EffectiveSelection);
            Ownership = ownership;
            EffectiveSelection = selection;
            _analysedOrder = f is { } z ? z.Config.FftOrder : null;
        }
        if (changed || force) StateChanged?.Invoke();
    }

    // ── Capability probe ──

    /// <summary>Reads the caps header and the band-centre table, once per
    /// connect. A STALL leaves <see cref="Supported"/> false. Blocking.</summary>
    public void FetchCaps()
    {
        int generation = Volatile.Read(ref _generation);
        var caps = RtaCaps.Parse(_transport.RtaIn(RtaWire.GetCaps, 0, RtaWire.CapsSize));
        if (caps == null)
        {
            lock (_capsLock)
            {
                if (generation != _generation) return;
                Supported = false;
                BandCentresHz = Array.Empty<double>();
            }
            StateChanged?.Invoke();
            return;
        }

        // Centres arrive 32 to a chunk from wValue 1; the table ends with a
        // short or refused chunk.
        var centres = new List<double>();
        for (ushort chunk = 1; centres.Count < caps.MaxBands && chunk < 16; chunk++)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            var c = _transport.RtaIn(RtaWire.GetCaps, chunk, RtaWire.CentresPerChunk * 2);
            if (c == null || c.Length < 2) break;
            for (int i = 0; i + 1 < c.Length; i += 2) centres.Add(c[i] | (c[i + 1] << 8));
            if (c.Length < RtaWire.CentresPerChunk * 2) break;
        }
        lock (_capsLock)
        {
            if (generation != _generation) return;
            Caps = caps;
            BandCentresHz = centres.Take(caps.MaxBands).ToArray();
            // A size outside this device's range would be STALLed on every push.
            var o = Options;
            if (o.FftOrder < caps.FftOrderMin || o.FftOrder > caps.FftOrderMax)
                o = o with { FftOrder = caps.FftOrderDefault };
            SetOptions(o);
            // A fresh device knows nothing of the previous one's configuration.
            ConfigurationChanged();
            // Last, so no tick runs on the previous device's state.
            Supported = centres.Count >= caps.MaxBands;
        }
        Interlocked.Increment(ref _frameVersion);
        StateChanged?.Invoke();
    }

    /// <summary>Forget everything about the device that went away.</summary>
    public void DeviceDisconnected()
    {
        lock (_capsLock)
        {
            _generation++;
            Supported = false;
        }
        lock (_lock)
        {
            ResetPush();
            ForgetSharingLocked();
            ConfigRejected = false;
        }
        lock (_publishLock)
        {
            _binAverage.Reset();
            Publish(RtaSnapshot.Empty);
        }
        PublishSharing(force: true);
    }

    // ── Polling ──

    /// <summary>
    /// One poll, from the view model's status poll thread. Each product has its
    /// own cadence in elapsed time, so a slower timer or a larger transform
    /// changes how often things are read, not what is read. Overlapping calls
    /// (a slow transfer outlasting the timer) are skipped.
    /// </summary>
    public void Tick()
    {
        if (!Supported) return;
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try { TickCore(); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private void TickCore()
    {
        RtaConfig want, own;
        bool needsPush = false, readBins, readStatus, readConfig;
        long epoch;
        int bandSlots, bandFrameSize, binFrameLength;
        int generation = Volatile.Read(ref _generation);
        double now = _clock();
        lock (_lock)
        {
            // An empty mask is a STALL, and there is nothing to draw either:
            // hold the previous configuration and skip the poll.
            if (OwnConfigLocked() is not { } mine) return;
            own = mine;
            bool wantsBins = _requests.Values.Any(r => r.Request.WantsBins);
            if (_following is { } f)
            {
                // Poll what the device is analysing; never push over it.
                want = f.Config;
            }
            else
            {
                want = own;
                // Give up after three rejected pushes of the same config, so a
                // refused setting is not a write on every tick; asking for
                // something different starts the count again.
                if (_appliedConfig != want)
                {
                    if (_lastAttempted != want)
                    {
                        _lastAttempted = want;
                        _pushAttempts = 0;
                        _everAgreed = false;
                    }
                    if (_pushAttempts < 3)
                    {
                        _pushAttempts++;
                        _appliedConfig = want;
                        _adoptedAvgMs = null;
                        _pushOutstanding = true;
                        needsPush = true;
                    }
                }
                if (!needsPush && _adoptedAvgMs is { } avg) want = want with { AvgMs = avg };
            }
            epoch = _stateEpoch;
            // Bins no faster than the device publishes them (a frame time
            // apart: 21 ms at 1024 points) and never faster than 20 a second.
            // Keyed to what the device analyses, whoever chose it, so a
            // follower draws the same fine curve as the console it follows.
            readBins = wantsBins && System.Numerics.BitOperations.PopCount(want.ChannelMask) == 1
                && now - _lastBinRead >= Math.Max(_pollFrameInterval, RtaMath.MinBinInterval);
            readStatus = now - _lastStatusRead >= RtaMath.StatusInterval;
            // Once the device announces its configurations, reading it back is
            // only needed to tell a refused push from one still being applied.
            // A slow backstop besides: a lost announcement would otherwise
            // leave this console drawing silence for channels the device no
            // longer analyses.
            readConfig = readStatus && (!_eventSeen || _pushOutstanding || now - _lastConfigRead >= ConfigBackstopInterval);
            if (readConfig) _lastConfigRead = now;
            if (readBins) _lastBinRead = now;
            if (readStatus) _lastStatusRead = now;
            bandSlots = Caps.MaxBands;
            bandFrameSize = Caps.BandFrameSize;
            binFrameLength = Caps.MaxBinFrame > 0 ? Caps.MaxBinFrame : RtaWire.BinFrameMax;
        }

        if (needsPush) _transport.RtaOut(RtaWire.SetConfig, 0, want.ToBytes());

        // Band frames. GET_BANDS_ALL answers for every live channel in one
        // transfer, each frame naming its own channel.
        var frames = new Dictionary<byte, RtaBandFrame>();
        if (System.Numerics.BitOperations.PopCount(want.ChannelMask) > 1)
        {
            var d = _transport.RtaIn(RtaWire.GetBandsAll, 0, bandFrameSize * 16);
            if (d != null)
                for (int off = 0; off + bandFrameSize <= d.Length; off += bandFrameSize)
                    if (RtaBandFrame.Parse(d, off, bandSlots) is { } f) frames[f.Channel] = f;
        }
        else
        {
            int ch = System.Numerics.BitOperations.TrailingZeroCount(want.ChannelMask);
            if (RtaBandFrame.Parse(_transport.RtaIn(RtaWire.GetBands, (ushort)ch, bandFrameSize), 0, bandSlots) is { } f)
                frames[f.Channel] = f;
        }

        var bins = readBins ? ReadBinFrame(binFrameLength) : null;
        double binsReadAt = _clock();

        RtaStatus? status = null;
        RtaConfig? applied = null;
        if (readStatus)
            status = RtaStatus.Parse(_transport.RtaIn(RtaWire.GetStatus, 0, RtaWire.StatusSize));
        if (readConfig)
            applied = RtaConfig.Parse(_transport.RtaIn(RtaWire.GetConfig, 0, RtaWire.ConfigSize));
        if (status != null)
            lock (_lock) _pollFrameInterval = RtaMath.ChannelRefreshInterval(status, want.FftOrder);

        bool? rejected = null;
        bool rejectionChanged = false;
        if (applied is { } a)
        {
            lock (_lock)
            {
                // Anything that changed ownership mid-tick outranks this reading.
                if (epoch == _stateEpoch)
                {
                    if (a.Matches(own))
                    {
                        // Ours, or someone else's identical choice.
                        _following = null;
                        ConfirmLocked(own, a);
                        if (want.Tap == a.Tap) want = want with { AvgMs = a.AvgMs, PeakDecayDbS = a.PeakDecayDbS };
                        rejected = false;
                    }
                    else if (_following is { } f)
                    {
                        // Firmware without the announcement: follow further
                        // changes in place.
                        if (!a.Matches(f.Config)) _following = (a, 0);
                    }
                    else if (_everAgreed)
                    {
                        // It held ours and now holds something else: another
                        // host changed it, which is not a refusal.
                        _following = (a, 0);
                        _pushOutstanding = false;
                        rejected = false;
                    }
                    else if (_pushAttempts >= 3) rejected = true;
                    // Not applied yet (the device applies a staged config from
                    // its main loop) or refused: try again next tick.
                    else _appliedConfig = null;
                }
                if (rejected is { } r && r != ConfigRejected)
                {
                    ConfigRejected = r;
                    rejectionChanged = true;
                }
            }
        }

        lock (_publishLock)
        {
            // Released, or the device gone, while this tick was reading: leave
            // the cleared state rather than restore what was read.
            if (!IsWatching || generation != Volatile.Read(ref _generation)) return;
            var s = _snapshot;
            var merged = s.Tap == want.Tap ? new Dictionary<byte, RtaBandFrame>(s.Frames) : new Dictionary<byte, RtaBandFrame>();
            // Only channels still asked for and, while the device runs, live:
            // a channel dropped from the rotation (an output disabled) stops
            // being reported, and its last frame would otherwise stay frozen.
            var current = status ?? s.Status;
            int keep = want.ChannelMask & (current.IsRunning && current.LiveMask != 0 ? current.LiveMask : 0xFFFF);
            foreach (var ch in merged.Keys.Where(c => c >= 16 || (keep & (1 << c)) == 0).ToArray()) merged.Remove(ch);
            var held = s.Tap == want.Tap ? s.Bins : null;
            var (fresh, stale) = RtaMath.SilencingStale(frames, StaleAfterMs(s.Status));
            foreach (var (ch, f) in fresh) merged[ch] = f;
            if (bins != null)
            {
                var levels = _binAverage.Add(bins, want.Tap, binsReadAt, want.AvgMs, LevelDb);
                held = bins with { LevelsDb = levels, SmoothedLevelsDb = _binAverage.SmoothedLevels };
            }
            // Bins carry no age of their own; they go stale with their channel.
            if (held != null && stale.Contains(held.Channel))
            {
                held = null;
                _binAverage.Reset();
            }
            Publish(new RtaSnapshot { Tap = want.Tap, Frames = merged, Bins = held, Status = status ?? s.Status });
        }
        PublishSharing(force: rejectionChanged);
    }

    /// <summary>A few rotation intervals, so a slow rotation through many
    /// channels is not mistaken for a stopped stream.</summary>
    private double StaleAfterMs(RtaStatus status) =>
        Math.Max(500, RtaMath.ChannelRefreshInterval(status, AnalysedFftOrder) * 4000);

    /// <summary>How often one channel refreshes, for the displays' smoothing.</summary>
    public double ChannelRefreshInterval => RtaMath.ChannelRefreshInterval(_snapshot.Status, AnalysedFftOrder);

    /// <summary>The transform size the device runs: the followed console's
    /// while following, else this console's own option.</summary>
    public byte AnalysedFftOrder => _analysedOrder is byte b ? b : Options.FftOrder;

    /// <summary>Adopt a snapshot: the frame version moves only when something a
    /// display draws changed, telemetry and display state only when they did.
    /// Caller holds <see cref="_publishLock"/>.</summary>
    private void Publish(RtaSnapshot s)
    {
        var old = _snapshot;
        _snapshot = s;
        bool statusChanged = s.Status != old.Status;
        if (s.Tap != old.Tap || statusChanged || !BinsDrawSame(s.Bins, old.Bins) || !FramesDrawSame(s.Frames, old.Frames))
            Interlocked.Increment(ref _frameVersion);
        if (statusChanged) TelemetryChanged?.Invoke();
        var d = RtaDisplayState.From(s);
        if (d != Display)
        {
            Display = d;
            StateChanged?.Invoke();
        }
    }

    private static bool BinsDrawSame(RtaBinFrame? a, RtaBinFrame? b) =>
        a == null ? b == null : b != null && a.DrawsSame(b) && ReferenceEquals(a.LevelsDb, b.LevelsDb);

    /// <summary>Whether two sets of band frames draw the same picture.</summary>
    public static bool FramesDrawSame(IReadOnlyDictionary<byte, RtaBandFrame> a, IReadOnlyDictionary<byte, RtaBandFrame> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (ch, f) in a)
            if (!b.TryGetValue(ch, out var g) || !f.DrawsSame(g)) return false;
        return true;
    }

    // ── Reads that need more than one transfer ──

    /// <summary>Reads and validates the published bin frame. wValue is a byte
    /// offset, so a short answer is picked up where it stopped; a torn read
    /// (the engine republished mid-read) is read again rather than drawn.</summary>
    private RtaBinFrame? ReadBinFrame(int length)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var frame = new List<byte>(length);
            while (frame.Count < length)
            {
                var chunk = _transport.RtaIn(RtaWire.GetBins, (ushort)frame.Count, length - frame.Count);
                if (chunk == null || chunk.Length == 0) break;
                frame.AddRange(chunk);
                // A frame is shorter than the ceiling below the largest size:
                // stop once the header's own length is in.
                if (RtaBinFrame.FrameLength(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(frame)) is { } n && frame.Count >= n) break;
            }
            if (RtaBinFrame.Parse(frame.ToArray()) is { } f) return f;
        }
        return null;
    }

    // ── Levels ──

    /// <summary>dBFS of one wire level byte, against the caps' zero point.</summary>
    public double LevelDb(byte v) => RtaMath.LevelDb(v, Caps.LevelZero);

    /// <summary>The lowest level the wire carries: what an empty band and a
    /// silent channel both read.</summary>
    public double FloorDb => LevelDb(0);

    // ── Reading helpers ──

    /// <summary>Whether a band is measured at the current size and rate: the
    /// bass bank always, the FFT bands when a bin falls inside them.</summary>
    public bool TransformHasBin(int band)
    {
        if (band < 0 || band >= BandCentresHz.Count) return true;
        double rate = Display.SampleRateHz > 0 ? Display.SampleRateHz : 48000;
        return RtaMath.BandIsPopulated(band, rate, AnalysedFftOrder, Caps.BassBands);
    }

    /// <summary>"2 channels, each refreshed every 43 ms", or "idle".</summary>
    public string RefreshDescription
    {
        get
        {
            var s = _snapshot.Status;
            if (!s.IsRunning || s.LiveCount == 0) return "idle";
            int ms = (int)Math.Round(ChannelRefreshInterval * 1000);
            return $"{(s.LiveCount == 1 ? "1 channel" : $"{s.LiveCount} channels")}, each refreshed every {ms} ms";
        }
    }
}

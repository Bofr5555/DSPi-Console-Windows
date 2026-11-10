namespace DSPiConsole.Core.Link.Hub;

/// <summary>
/// The hub's poll engine for one device (spec 7.6, 8.4). Meters, RTA frames and
/// other transient state are not notified by the firmware, so a session
/// subscribes GET commands at a requested rate and the hub polls them and
/// pushes the verbatim responses back as POLL frames. This class deduplicates
/// identical polls so a shared meter is read once and fanned out, keeps each
/// session inside a bandwidth budget, backs off a failing poll, and stops
/// polling a command the moment its last subscriber leaves (RTA and some
/// meters switch themselves off when unread, so a zombie poll costs device
/// CPU). Port of the macOS Console's PollScheduler.
/// </summary>
/// <remarks>
/// <para><b>Execution.</b> Unlike the Mac, the scheduler never touches the
/// device: each poll is handed to <c>execute</c>, which the hub wires to its
/// command router, so polls queue behind other sessions' commands in arrival
/// order and can never land between the chunks of a bulk read (spec 9.1.1).
/// <c>execute</c> blocks, possibly for seconds behind a bulk transfer, so it is
/// always called with no lock held: subscribing, unsubscribing and
/// <see cref="SessionsSubscribed"/> (the router's RTA STOP check, which may run
/// while a poll of the same device waits in the router) never wait on device
/// I/O.</para>
/// <para><b>Threading.</b> One worker thread runs <see cref="Tick"/> every
/// <see cref="TickInterval"/>; ticks never overlap. The callbacks run on that
/// thread, outside every lock, so they may call back in, but they delay the
/// next poll for as long as they take, so they should only queue a frame.</para>
/// <para><b>Rates.</b> Each spec keeps a fractional accumulator advanced by
/// the time that really elapsed (from the injected clock), so a late timer
/// tick doesn't slow the poll rate down, and a spec runs at most once per tick
/// so a stall never turns into a burst.</para>
/// </remarks>
public sealed class PollScheduler : IDisposable
{
    /// <summary>One poll's full identity. Subscriptions with the same tuple are
    /// the same physical read and are executed once (spec 7.6).</summary>
    public readonly record struct PollSpec(byte Handle, byte Req, ushort WValue, ushort WIndex, ushort Len);

    /// <summary>One entry of a <c>poll.subscribe</c>.</summary>
    public readonly record struct PollRequest(int Slot, PollSpec Spec, double Hz);

    /// <summary>What a subscribe entry was granted; 0 Hz means it was refused
    /// and is not polled.</summary>
    public readonly record struct PollGrant(int Slot, double Hz);

    /// <summary>Slots are a session-chosen 0..15 (spec 7.6).</summary>
    public const int MaxSlot = 15;
    /// <summary>Consecutive failures that make one <c>poll.error</c> and halve
    /// the rate (spec 8.4).</summary>
    public const int FailuresBeforeError = 3;

    private readonly object _lock = new();
    /// <summary>Held for a whole tick, so a direct <see cref="Tick"/> and the
    /// worker never run one concurrently.</summary>
    private readonly object _tickGate = new();
    private readonly Func<LinkCmdRequest, LinkCmdResponse> _execute;
    private readonly LinkPolicy _policy;
    private readonly Action<ushort, int, byte, byte[]> _onPoll;
    private readonly Action<ushort, int, byte, LinkStatus>? _onPollError;
    private readonly TimeProvider _clock;

    private readonly Dictionary<PollSpec, SpecState> _specs = new();
    /// <summary>Reverse index: each session's slot to the spec it polls. Drives
    /// unsubscribe, grant lookups and per-session budget scaling.</summary>
    private readonly Dictionary<ushort, Dictionary<int, PollSpec>> _sessionSlots = new();
    /// <summary>Clock timestamp of the previous tick (construction, before the
    /// first).</summary>
    private long _lastTick;
    private volatile bool _stopped;
    private readonly ManualResetEventSlim _stopSignal = new(false);

    /// <param name="execute">Runs one GET through the hub's command router and
    /// blocks until it is answered. An exception counts as a failed poll.</param>
    /// <param name="onPoll">(session, slot, handle, payload) for each delivered
    /// result. The payload array is shared by every subscriber of that read:
    /// don't modify it.</param>
    /// <param name="onPollError">(session, slot, handle, status), once per
    /// subscriber per failure episode, for the <c>poll.error</c> event.</param>
    /// <param name="driveWithTimer">False in tests, which call
    /// <see cref="Tick"/> themselves against a fake clock.</param>
    public PollScheduler(
        Func<LinkCmdRequest, LinkCmdResponse> execute,
        LinkPolicy policy,
        Action<ushort, int, byte, byte[]> onPoll,
        Action<ushort, int, byte, LinkStatus>? onPollError = null,
        TimeProvider? clock = null,
        double maxHz = 20,
        double budgetBytesPerSecond = 200_000,
        TimeSpan? tickInterval = null,
        bool driveWithTimer = true)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _onPoll = onPoll ?? throw new ArgumentNullException(nameof(onPoll));
        _onPollError = onPollError;
        _clock = clock ?? TimeProvider.System;
        MaxHz = maxHz;
        BudgetBytesPerSecond = budgetBytesPerSecond;
        TickInterval = tickInterval ?? TimeSpan.FromMilliseconds(50);
        if (TickInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(tickInterval));
        _lastTick = _clock.GetTimestamp();
        if (driveWithTimer)
        {
            new Thread(Run) { IsBackground = true, Name = "Link poll scheduler" }.Start();
        }
    }

    /// <summary>Ceiling on any one granted rate, before the budget.</summary>
    public double MaxHz { get; }
    /// <summary>Per-session poll bandwidth, estimated as the sum of
    /// len * granted Hz over the session's polls (<c>poll_budget_bps</c>).</summary>
    public double BudgetBytesPerSecond { get; }
    public TimeSpan TickInterval { get; }

    // ---- Subscription ----

    /// <summary>
    /// Subscribes a session to a batch of polls and returns each entry's granted
    /// rate, in request order. An entry is refused (0 Hz, not polled) when its
    /// slot is outside 0..15, its rate isn't positive, its command isn't
    /// classed <i>read</i> as a GET, or the role doesn't permit it (spec 7.6).
    /// Otherwise the rate is capped at <see cref="MaxHz"/> and then all the
    /// session's polls are scaled down together to fit the budget, which can
    /// lower the grants of slots subscribed earlier too. A slot already in use
    /// is replaced, including by a refused entry, which leaves the slot empty:
    /// the reply says 0 Hz, so leaving the old poll running would send the
    /// client frames it now reads as a different command.
    /// </summary>
    public IReadOnlyList<PollGrant> Subscribe(ushort session, LinkRole role, IReadOnlyList<PollRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        lock (_lock)
        {
            if (_stopped) return requests.Select(r => new PollGrant(r.Slot, 0)).ToList();

            foreach (var request in requests)
            {
                if (request.Slot is < 0 or > MaxSlot) continue;
                RemoveSlotLocked(session, request.Slot);
                if (!IsPollable(role, request.Spec.Req)) continue;
                // NaN and negatives are refused along with zero.
                if (!(request.Hz > 0)) continue;

                double capped = Math.Min(request.Hz, MaxHz);
                if (!_specs.TryGetValue(request.Spec, out var state))
                {
                    state = new SpecState(request.Spec);
                    _specs[request.Spec] = state;
                }
                state.Subs[(session, request.Slot)] = new Sub(session, request.Slot, capped);
                if (!_sessionSlots.TryGetValue(session, out var slots))
                    _sessionSlots[session] = slots = new Dictionary<int, PollSpec>();
                slots[request.Slot] = request.Spec;
            }

            RescaleLocked(session);
            return requests.Select(r => new PollGrant(r.Slot, GrantedLocked(session, r.Slot) ?? 0)).ToList();
        }
    }

    public void Unsubscribe(ushort session, IEnumerable<int> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        lock (_lock)
        {
            foreach (int slot in slots) RemoveSlotLocked(session, slot);
            // The session's remaining polls may fit the budget better now.
            RescaleLocked(session);
        }
    }

    /// <summary>The session closed, or the device went offline for it.</summary>
    public void UnsubscribeAll(ushort session)
    {
        lock (_lock)
        {
            if (!_sessionSlots.TryGetValue(session, out var slots)) return;
            foreach (int slot in slots.Keys.ToList()) RemoveSlotLocked(session, slot);
            _sessionSlots.Remove(session);
        }
    }

    // ---- Queries ----

    /// <summary>
    /// True when a session other than <paramref name="excluding"/> holds a poll
    /// of any of <paramref name="codes"/>: the router's RTA STOP rule (spec
    /// 7.15, a poll of 0x0B/0x0C/0x0F counts as watching). Takes only the
    /// scheduler's lock, which is never held across a device read.
    /// </summary>
    public bool SessionsSubscribed(IReadOnlySet<byte> codes, ushort excluding)
    {
        ArgumentNullException.ThrowIfNull(codes);
        lock (_lock)
        {
            foreach (var state in _specs.Values)
            {
                if (!codes.Contains(state.Spec.Req)) continue;
                foreach (var key in state.Subs.Keys)
                    if (key.Session != excluding) return true;
            }
            return false;
        }
    }

    public double? GrantedHz(ushort session, int slot)
    {
        lock (_lock) return GrantedLocked(session, slot);
    }

    /// <summary>Distinct reads being polled; a spec nobody reads is dropped at
    /// once, so this is the real device traffic.</summary>
    public int ActiveSpecCount
    {
        get { lock (_lock) return _specs.Count; }
    }

    // ---- Lifecycle ----

    /// <summary>
    /// Forgets every subscription and ends the worker. Doesn't wait for it (a
    /// callback may call this): a poll already in the router is answered and
    /// discarded, and a callback already running finishes, but no new one
    /// starts.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            _specs.Clear();
            _sessionSlots.Clear();
        }
        _stopSignal.Set();
    }

    public void Dispose() => Stop();

    // ---- Ticking ----

    private void Run()
    {
        while (!_stopSignal.Wait(TickInterval))
        {
            try { Tick(); }
            catch (Exception ex)
            {
                // A throwing callback must not end polling for everyone.
                LinkLog.Write($"Link hub: poll tick failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Advances the schedule by the time elapsed since the previous tick and
    /// runs whatever is due. The worker calls this; tests call it directly
    /// against a fake clock. Returns at once if a tick is already running.
    /// </summary>
    internal void Tick()
    {
        if (!Monitor.TryEnter(_tickGate)) return;
        try
        {
            var due = CollectDue();
            foreach (var state in due)
            {
                if (_stopped) return;
                RunOne(state);
            }
        }
        finally
        {
            Monitor.Exit(_tickGate);
        }
    }

    /// <summary>Advances every spec's accumulator; returns those due now.</summary>
    private List<SpecState> CollectDue()
    {
        var due = new List<SpecState>();
        lock (_lock)
        {
            if (_stopped) return due;
            long now = _clock.GetTimestamp();
            double elapsed = Math.Max(0, _clock.GetElapsedTime(_lastTick, now).TotalSeconds);
            _lastTick = now;

            foreach (var state in _specs.Values)
            {
                // The read runs at the highest rate any subscriber was granted;
                // slower subscribers get a share of the results.
                double maxGranted = state.MaxGranted();
                if (maxGranted <= 0) continue;
                state.ExecAcc += maxGranted * state.RateFactor * elapsed;
                if (state.ExecAcc < 1) continue;
                // Keep the fraction (so 12 Hz averages 12 Hz on a 50 ms tick)
                // but not whole periods a stall missed: no catch-up burst.
                state.ExecAcc -= Math.Floor(state.ExecAcc);
                due.Add(state);
            }
        }
        return due;
    }

    private void RunOne(SpecState state)
    {
        lock (_lock)
        {
            // Unsubscribed since it was found due: don't read it at all.
            if (!IsCurrentLocked(state)) return;
        }

        var spec = state.Spec;
        var request = new LinkCmdRequest(0, spec.Handle, LinkDirection.Get, spec.Req, spec.WValue, spec.WIndex, spec.Len);
        LinkCmdResponse response;
        try
        {
            response = _execute(request);
        }
        catch (Exception ex)
        {
            LinkLog.Write($"Link hub: poll 0x{spec.Req:X2} threw: {ex.GetType().Name}: {ex.Message}");
            response = new LinkCmdResponse(0, LinkStatus.Error);
        }

        List<(ushort Session, int Slot, Sub Sub)>? deliveries = null;
        List<(ushort Session, int Slot)>? errors = null;
        lock (_lock)
        {
            if (!IsCurrentLocked(state)) return;
            if (response.Status == LinkStatus.Ok)
            {
                state.ConsecutiveFailures = 0;
                state.ErrorReported = false;
                state.RateFactor = 1;
                double maxGranted = state.MaxGranted();
                foreach (var sub in state.Subs.Values)
                {
                    if (maxGranted <= 0) break;
                    // Deliver with probability grantedHz / maxGranted, so a
                    // half-rate subscriber sees every other result.
                    sub.DeliverAcc += sub.GrantedHz / maxGranted;
                    if (sub.DeliverAcc < 1) continue;
                    sub.DeliverAcc -= 1;
                    (deliveries ??= new()).Add((sub.Session, sub.Slot, sub));
                }
            }
            else
            {
                // Spec 8.4: send nothing for a failed tick; after three in a
                // row, one poll.error per subscriber and half rate until a
                // read succeeds.
                state.ConsecutiveFailures++;
                if (state.ConsecutiveFailures >= FailuresBeforeError && !state.ErrorReported)
                {
                    state.ErrorReported = true;
                    state.RateFactor = 0.5;
                    foreach (var sub in state.Subs.Values) (errors ??= new()).Add((sub.Session, sub.Slot));
                }
            }
        }

        if (deliveries != null)
        {
            foreach (var (session, slot, sub) in deliveries)
            {
                if (_stopped) return;
                // Unsubscribed, or the slot given another poll, since the
                // list was made: a client reads a POLL frame by its slot alone.
                lock (_lock)
                {
                    if (!state.Subs.TryGetValue((session, slot), out var current) || !ReferenceEquals(current, sub)
                        || !IsCurrentLocked(state))
                        continue;
                }
                _onPoll(session, slot, spec.Handle, response.Payload);
            }
        }
        if (errors != null && _onPollError != null)
        {
            foreach (var (session, slot) in errors)
            {
                if (_stopped) return;
                _onPollError(session, slot, spec.Handle, response.Status);
            }
        }
    }

    // ---- Helpers (call with _lock held) ----

    /// <summary>Polls travel as GET transfers, so the command must be classed
    /// read for GET (a write-as-read command like SAVE_PARAMS never is, even
    /// for an admin) and the role must permit it (spec 6.3, 7.6).</summary>
    /// <summary>A read the session's role allows, and not the bulk blob
    /// (0xA0-0xA3): polling a full chunked read many times a second would
    /// crowd the router, and device.snapshot exists for it.</summary>
    private bool IsPollable(LinkRole role, byte req) =>
        req is < 0xA0 or > 0xA3
        && _policy.Classify(req, LinkDirection.Get) == LinkCommandClass.Read
        && _policy.IsAllowed(role, req, LinkDirection.Get);

    private bool IsCurrentLocked(SpecState state) =>
        !_stopped && _specs.TryGetValue(state.Spec, out var current) && ReferenceEquals(current, state);

    private double? GrantedLocked(ushort session, int slot)
    {
        if (!_sessionSlots.TryGetValue(session, out var slots) || !slots.TryGetValue(slot, out var spec)) return null;
        return _specs.TryGetValue(spec, out var state) && state.Subs.TryGetValue((session, slot), out var sub)
            ? sub.GrantedHz
            : null;
    }

    private void RemoveSlotLocked(ushort session, int slot)
    {
        if (!_sessionSlots.TryGetValue(session, out var slots) || !slots.Remove(slot, out var spec)) return;
        if (slots.Count == 0) _sessionSlots.Remove(session);
        if (!_specs.TryGetValue(spec, out var state)) return;
        state.Subs.Remove((session, slot));
        // Last reader gone: stop polling it now (spec 7.6).
        if (state.Subs.Count == 0) _specs.Remove(spec);
    }

    /// <summary>Scales all of one session's grants by one common factor so its
    /// estimated bandwidth (sum of len * Hz) stays within the budget. Scaling
    /// from each sub's capped request, not its current grant, keeps it stable
    /// as polls come and go.</summary>
    private void RescaleLocked(ushort session)
    {
        if (!_sessionSlots.TryGetValue(session, out var slots)) return;
        var subs = new List<(Sub Sub, ushort Len)>();
        foreach (var (slot, spec) in slots)
        {
            if (_specs.TryGetValue(spec, out var state) && state.Subs.TryGetValue((session, slot), out var sub))
                subs.Add((sub, spec.Len));
        }
        double total = subs.Sum(s => s.Len * s.Sub.CappedHz);
        double scale = total > BudgetBytesPerSecond && total > 0 ? BudgetBytesPerSecond / total : 1;
        foreach (var (sub, _) in subs) sub.GrantedHz = sub.CappedHz * scale;
    }

    // ---- State ----

    /// <summary>One subscriber's view of one spec.</summary>
    private sealed class Sub(ushort session, int slot, double cappedHz)
    {
        public ushort Session { get; } = session;
        public int Slot { get; } = slot;
        /// <summary>The request clamped to MaxHz, before the budget scale.</summary>
        public double CappedHz { get; } = cappedHz;
        /// <summary>After the session's budget scale: what was granted.</summary>
        public double GrantedHz { get; set; }
        /// <summary>Share-of-results accumulator for fan-out.</summary>
        public double DeliverAcc { get; set; }
    }

    /// <summary>Everything tracked for one deduplicated read.</summary>
    private sealed class SpecState(PollSpec spec)
    {
        public PollSpec Spec { get; } = spec;
        public Dictionary<(ushort Session, int Slot), Sub> Subs { get; } = new();
        /// <summary>Executions owed; the read runs when it reaches 1.</summary>
        public double ExecAcc { get; set; }
        public int ConsecutiveFailures { get; set; }
        /// <summary>The current failure episode's poll.error has been sent.</summary>
        public bool ErrorReported { get; set; }
        /// <summary>1 normally, 0.5 while failing (spec 8.4).</summary>
        public double RateFactor { get; set; } = 1;

        public double MaxGranted()
        {
            double max = 0;
            foreach (var sub in Subs.Values) max = Math.Max(max, sub.GrantedHz);
            return max;
        }
    }
}

using System.Collections.Concurrent;
using DSPiConsole.Core.Rta;

namespace DSPiConsole.Core.Link.Hub;

/// <summary>What the router must know about a session to route its command:
/// the live role (it can change mid-session) and the id.</summary>
public readonly record struct RouterSession(ushort Id, LinkRole Role, bool ExemptFromInflightCap = false);

/// <summary>A completed command plus the session a successful SET is
/// attributed to (0 for a GET, a failure, or a rejection).</summary>
public readonly record struct RoutedResult(LinkCmdResponse Response, ushort AttributedTo);

/// <summary>
/// Serialises tunnelled vendor commands for one device across every session
/// (the local UI, remote clients, the hub's own reads and the poll
/// scheduler), enforces roles and the exclusive lock, caps in-flight work per
/// remote session, times the device out, and remembers which session wrote
/// what so the notification that follows can be attributed (spec 8.2, 8.3).
/// Port of the macOS Console's CommandRouter, socket- and USB-free.
///
/// Unlike the Mac, everything that touches the device goes through here,
/// polls included: over USB a client's 0xA0/0xA1 is a chunked 0xA2/0xA3
/// session the firmware abandons if any other request lands between chunks
/// (spec 9.1.1), so nothing may bypass the queue.
/// </summary>
public sealed class CommandRouter : IDisposable
{
    private readonly byte _handle;
    private readonly IHubDevice _device;
    private readonly LinkPolicy _policy;
    private readonly object _lock = new();

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private readonly DeviceCaller _exec;

    private ushort? _lockHolder;
    private DateTime _lockDeadline;
    private readonly Dictionary<ushort, int> _inflight = new();
    private bool _shutDown;

    /// <summary>Recent writers in dispatch order. Each is recorded before its
    /// device call runs (commands are serial per device, so its notification
    /// can't arrive first) and consumed by the next host-sourced notification,
    /// oldest first: a queue rather than "the latest writer", so B's write
    /// landing before A's notification is read doesn't steal A's attribution.
    /// Best effort: the firmware coalesces repeated writes to one parameter,
    /// so repeats merge in place and expired entries are dropped.</summary>
    private readonly List<WriterEntry> _writers = new();
    private ulong _nextWriterToken;
    private WriterEntry? _lastConsumed;
    private int _noDeviceLogged;

    private readonly record struct WriterEntry(ulong Token, ushort Session, byte Code, uint Param, DateTime At);

    /// <summary>Last time each session read analyser bands or bins or sent
    /// RTA START (spec 7.15): a STOP reaches the device only when no other
    /// session watched within <see cref="RtaWatchWindow"/>.</summary>
    private readonly Dictionary<ushort, DateTime> _rtaWatchers = new();

    /// <summary>The firmware's own analyser idle timeout, so a swallowed STOP
    /// never keeps the analyser running longer than an unread one would.</summary>
    public static readonly TimeSpan RtaWatchWindow = TimeSpan.FromSeconds(5);
    private static readonly HashSet<byte> RtaWatchCodes = new() { RtaWire.GetBands, RtaWire.GetBins, RtaWire.GetBandsAll };
    public static IReadOnlySet<byte> RtaWatchCodeSet => RtaWatchCodes;
    private const ushort RtaCtlStop = 0, RtaCtlStart = 1;

    private const byte SetEqParam = 0x42;
    private static readonly HashSet<byte> BulkOpcodes = new() { 0xA0, 0xA1, 0xA2, 0xA3 };

    /// <summary>Whether a session other than the given one holds a poll for
    /// one of the codes (the hub wires this to its PollScheduler).</summary>
    public Func<IReadOnlySet<byte>, ushort, bool>? OtherSessionPolls { get; set; }

    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan BulkTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxInflightPerSession { get; init; } = 8;

    public byte Handle => _handle;

    public CommandRouter(byte handle, IHubDevice device, LinkPolicy policy)
    {
        _handle = handle;
        _device = device;
        _policy = policy;
        _exec = new DeviceCaller(device, $"DSPi Link device {handle}");
        _worker = new Thread(Run) { IsBackground = true, Name = $"DSPi Link router {handle}" };
        _worker.Start();
    }

    private void Run()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Submits one command. Rejections (role, lock, rate) are decided
    /// at once, before the device is touched, but answered in turn like every
    /// other command, on the router's worker. Every command completes exactly
    /// once, so a caller blocked on the completion always hears back.</summary>
    public void Submit(LinkCmdRequest request, RouterSession session, Action<RoutedResult> completion)
    {
        // 1. Role. Fails safe: an unclassified command is config, admin only.
        if (!_policy.IsAllowed(session.Role, request.BRequest, request.Direction))
        {
            Answer(Reject(request, LinkStatus.Denied));
            return;
        }

        RoutedResult? rejected = null;
        lock (_lock)
        {
            if (_shutDown) rejected = Reject(request, LinkStatus.NoDevice);
            else rejected = Admit(request, session);
        }
        if (rejected is { } r)
        {
            Answer(r);
            return;
        }
        Enqueue(request, session, completion);

        // A rejection still answers after the session's earlier commands
        // (spec 8.2: answers come in the order sent), and never under the lock.
        void Answer(RoutedResult result)
        {
            try { _queue.Add(() => completion(result)); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { completion(result); }
        }
    }

    /// <summary>The lock and in-flight checks, under the lock; null admits the
    /// command (and counts it in flight).</summary>
    private RoutedResult? Admit(LinkCmdRequest request, RouterSession session)
    {
        // 2. Lock: while another session holds the device only its
        // commands run. An expired lock counts as released. The hub's own
        // reads (polls, the snapshot) aren't writes, and the Mac's polls
        // never met the lock either, so they carry on through one.
        if (_lockHolder is { } holder && holder != session.Id && session.Id != LinkSessionIds.Internal)
        {
            if (_lockDeadline < Now()) _lockHolder = null;
            else return Reject(request, LinkStatus.Locked);
        }
        // 3. In-flight cap (remote sessions; the local UI and the hub's
        // own work can't retry a rejection).
        int count = _inflight.GetValueOrDefault(session.Id);
        if (!session.ExemptFromInflightCap && count >= MaxInflightPerSession)
            return Reject(request, LinkStatus.RateLimited);
        _inflight[session.Id] = count + 1;
        return null;
    }

    private void Enqueue(LinkCmdRequest request, RouterSession session, Action<RoutedResult> completion)
    {
        // Bound to the attachment current now: a switch landing before the
        // queue reaches this command fails it rather than sending it to the
        // board that replaced the one it was meant for.
        ulong generation = _device.Generation;
        var timeout = BulkOpcodes.Contains(request.BRequest) ? BulkTimeout : CommandTimeout;

        void Work()
        {
            RoutedResult result;
            try { result = Execute(request, session, generation, timeout); }
            catch (Exception ex)
            {
                LinkLog.Write($"hub: command 0x{request.BRequest:X2} threw {ex.GetType().Name}");
                result = Reject(request, LinkStatus.Error);
            }
            finally
            {
                lock (_lock)
                {
                    if (_inflight.TryGetValue(session.Id, out var n))
                    {
                        if (n <= 1) _inflight.Remove(session.Id);
                        else _inflight[session.Id] = n - 1;
                    }
                }
            }
            try { completion(result); }
            catch (Exception ex) { LinkLog.Write($"hub: completion threw {ex.GetType().Name}"); }
        }

        try { _queue.Add(Work); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Disposed between the check and the add: still answer.
            lock (_lock) _inflight.Remove(session.Id);
            completion(Reject(request, LinkStatus.NoDevice));
        }
    }

    /// <summary>Submits and waits (for the hub's own reads and the poll
    /// scheduler, which already run off the UI thread).</summary>
    public LinkCmdResponse Run(LinkCmdRequest request, RouterSession session, TimeSpan wait)
    {
        var done = new TaskCompletionSource<LinkCmdResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        Submit(request, session, r => done.TrySetResult(r.Response));
        return done.Task.Wait(wait) ? done.Task.Result : new LinkCmdResponse(request.Tag, LinkStatus.Timeout);
    }

    private RoutedResult Execute(LinkCmdRequest request, RouterSession session, ulong generation, TimeSpan timeout)
    {
        bool shutDown;
        lock (_lock) shutDown = _shutDown;
        if (shutDown || !_device.IsConnected || _device.Generation != generation)
        {
            if (session.Id != LinkSessionIds.Local && Interlocked.Increment(ref _noDeviceLogged) <= 10)
                LinkLog.Write($"hub: router {_handle} refused 0x{request.BRequest:X2} from session {session.Id}: " +
                              (shutDown ? "shut down" : !_device.IsConnected ? "board not connected" : $"generation {generation} became {_device.Generation}"));
            return Reject(request, LinkStatus.NoDevice);
        }

        // Decided here, in queue order, so a band read queued ahead of a STOP
        // counts as watching.
        if (RtaStopIsSwallowed(request, session.Id))
            return new RoutedResult(new LinkCmdResponse(request.Tag, LinkStatus.Ok, new byte[] { 0x01 }), 0);

        // Anything that can change device state is a write for attribution,
        // whichever transfer direction carries it (write-as-read included),
        // except RTA_CONTROL: start, stop and reset never notify, so its entry
        // would only sit in the queue and claim the next session's
        // notification (another console's RTA_CONFIG taken for our echo).
        ulong? token = null;
        WriterEntry? previous = null;
        if (request.BRequest != RtaWire.Control
            && _policy.Classify(request.BRequest, request.Direction) != LinkCommandClass.Read)
        {
            uint param = ParameterKey(request);
            lock (_lock)
            {
                // A repeat of one parameter by one session is one entry, merged
                // where it sits, as the firmware coalesces into an unsent slot.
                int i = _writers.FindLastIndex(w => w.Session == session.Id && w.Code == request.BRequest && w.Param == param);
                if (i >= 0)
                {
                    previous = _writers[i];
                    _writers[i] = _writers[i] with { At = Now() };
                    token = _writers[i].Token;
                }
                else
                {
                    token = _nextWriterToken++;
                    _writers.Add(new WriterEntry(token.Value, session.Id, request.BRequest, param, Now()));
                    if (_writers.Count > 64) _writers.RemoveAt(0);
                }
            }
        }

        var response = _exec.Call(request, timeout) with { Tag = request.Tag };

        // A failed write gives back only its own attribution: a failed repeat
        // restores the earlier successful entry. One already consumed by its
        // notification stays consumed.
        if (token is { } t && response.Status != LinkStatus.Ok)
        {
            lock (_lock)
            {
                int i = _writers.FindIndex(w => w.Token == t);
                if (i >= 0)
                {
                    if (previous is { } p) _writers[i] = p;
                    else _writers.RemoveAt(i);
                }
            }
        }

        // Replaced while the command ran: not this session's result.
        if (_device.Generation != generation)
            return Reject(request, LinkStatus.NoDevice);

        ushort attributed = request.Direction == LinkDirection.Set && response.Status == LinkStatus.Ok ? session.Id : (ushort)0;
        return new RoutedResult(response, attributed);
    }

    /// <summary>Which parameter a write targets, so repeats merge exactly where
    /// the firmware coalesces. REQ_SET_EQ_PARAM sends wValue 0 and names its
    /// channel and band in the payload.</summary>
    public static uint ParameterKey(LinkCmdRequest request)
    {
        var p = request.Payload;
        if (request.BRequest != SetEqParam || p.Length < 2) return request.WValue;
        return request.WValue | (uint)p[0] << 16 | (uint)p[1] << 24;
    }

    private bool RtaStopIsSwallowed(LinkCmdRequest request, ushort session)
    {
        // The scheduler's reads run as the hub; who they're for is asked of
        // the scheduler itself (OtherSessionPolls).
        if (session == LinkSessionIds.Internal) return false;
        byte code = request.BRequest;
        bool control = code == RtaWire.Control;
        if (!control && !RtaWatchCodes.Contains(code)) return false;
        var now = Now();
        if (!control || request.WValue == RtaCtlStart)
        {
            lock (_lock) _rtaWatchers[session] = now;
            return false;
        }
        if (request.WValue != RtaCtlStop) return false;

        bool others;
        lock (_lock)
        {
            _rtaWatchers.Remove(session);
            foreach (var stale in _rtaWatchers.Where(w => now - w.Value > RtaWatchWindow).Select(w => w.Key).ToList())
                _rtaWatchers.Remove(stale);
            others = _rtaWatchers.Count > 0;
        }
        // Asked outside the lock: the scheduler has its own.
        return others || (OtherSessionPolls?.Invoke(RtaWatchCodes, session) ?? false);
    }

    private static RoutedResult Reject(LinkCmdRequest request, LinkStatus status) =>
        new(new LinkCmdResponse(request.Tag, status), 0);

    /// <summary>The device is gone: queued commands answer NO_DEVICE without
    /// touching it, and so does anything submitted later. A command already
    /// inside the device finishes and reports as usual.</summary>
    public void Shutdown()
    {
        lock (_lock) _shutDown = true;
        _queue.CompleteAdding();
    }

    // ── Locks (spec 7.7) ──

    /// <summary>Takes the device lock; fails while another session holds a
    /// live one. Taking it again extends the holder's own lock.</summary>
    public bool AcquireLock(ushort session, TimeSpan timeout)
    {
        lock (_lock)
        {
            if (_shutDown) return false;
            if (_lockHolder is { } holder && holder != session && _lockDeadline >= Now()) return false;
            _lockHolder = session;
            _lockDeadline = Now() + timeout;
            return true;
        }
    }

    public void ReleaseLock(ushort session)
    {
        lock (_lock)
            if (_lockHolder == session) _lockHolder = null;
    }

    public ushort? CurrentLockHolder
    {
        get
        {
            lock (_lock) return _lockHolder is { } h && _lockDeadline >= Now() ? h : null;
        }
    }

    /// <summary>A session went away: its lock, in-flight count and analyser
    /// watching go with it (nothing is sent; the firmware idles itself).</summary>
    public void SessionDidClose(ushort session)
    {
        lock (_lock)
        {
            if (_lockHolder == session) _lockHolder = null;
            _inflight.Remove(session);
            _rtaWatchers.Remove(session);
        }
    }

    /// <summary>The oldest writer within <paramref name="window"/>, consumed,
    /// for one host-sourced notification; 0 when nobody wrote.</summary>
    public ushort ConsumeAttribution(TimeSpan window)
    {
        lock (_lock)
        {
            var now = Now();
            _writers.RemoveAll(w => now - w.At > window);
            if (_writers.Count == 0)
            {
                // A second notify for one merged write (the firmware sent the
                // first before the repeat landed): it's still that writer's,
                // not a stranger's, or its own UI would apply it as foreign
                // and snap a slider back mid-drag.
                return _lastConsumed is { } last && now - last.At <= window ? last.Session : (ushort)0;
            }
            var first = _writers[0];
            _writers.RemoveAt(0);
            _lastConsumed = first;
            return first.Session;
        }
    }

    public void Dispose()
    {
        Shutdown();
        _exec.Dispose();
    }

    /// <summary>Runs the blocking device call on one persistent thread, so the
    /// router can give up on a call that wedges (the USB transfer has its own
    /// timeout; this guards the pathological case) without making a thread
    /// per command on the high-rate poll path. A late answer is dropped.</summary>
    private sealed class DeviceCaller : IDisposable
    {
        private readonly IHubDevice _device;
        private readonly BlockingCollection<(LinkCmdRequest Request, TaskCompletionSource<LinkCmdResponse> Done)> _calls = new();

        public DeviceCaller(IHubDevice device, string name)
        {
            _device = device;
            new Thread(Run) { IsBackground = true, Name = name }.Start();
        }

        private void Run()
        {
            try
            {
                foreach (var (request, done) in _calls.GetConsumingEnumerable())
                {
                    // Its caller was told TIMEOUT while a wedged call held this
                    // thread: running it now would surprise everyone.
                    if (done.Task.IsCompleted) continue;
                    try { done.TrySetResult(_device.Execute(request)); }
                    catch (Exception ex)
                    {
                        LinkLog.Write($"hub: device call threw {ex.GetType().Name}");
                        done.TrySetResult(new LinkCmdResponse(request.Tag, LinkStatus.Error));
                    }
                }
            }
            catch (ObjectDisposedException) { }
        }

        public LinkCmdResponse Call(LinkCmdRequest request, TimeSpan timeout)
        {
            var done = new TaskCompletionSource<LinkCmdResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { _calls.Add((request, done)); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                return new LinkCmdResponse(request.Tag, LinkStatus.NoDevice);
            }
            if (done.Task.Wait(timeout)) return done.Task.Result;
            // Answered TIMEOUT now, so the device thread skips it if it gets
            // there later; a reply already on its way wins the race instead.
            var timedOut = new LinkCmdResponse(request.Tag, LinkStatus.Timeout);
            return done.TrySetResult(timedOut) ? timedOut : done.Task.Result;
        }

        public void Dispose() => _calls.CompleteAdding();
    }
}

namespace DSPiConsole.Core.Link.Hub;

/// <summary>
/// Fans a hub's device notifications out to every authenticated Link session
/// (spec 8.3 NOTIFY). Each session gets its own Link sequence counter (the
/// frame <c>tag</c>), its own bounded outbound queue and its own worker thread,
/// so a slow reader never stalls the device's notification reader or any other
/// session. When a session falls behind, its oldest frames are dropped and it is
/// told to re-read state with one RESYNC (spec 8.6) rather than being fed a
/// truncated stream as if nothing happened. Port of the macOS Console's
/// NotifyRelay.
/// </summary>
/// <remarks>
/// <para><see cref="Publish"/> only enqueues: no callback runs on the caller's
/// thread. Each session's callbacks run on that session's own worker thread,
/// one at a time, in the order the frames were queued, so a session sees its
/// NOTIFY and RESYNC frames in publish order (minus drops).</para>
/// <para>Dedicated threads rather than the thread pool, because a deliver
/// callback may block on a slow socket, and parking pool threads on I/O starves
/// everything else in the process that uses the pool.</para>
/// </remarks>
public sealed class NotifyRelay : IDisposable
{
    /// <summary>Spec 8.6 reason 0: the hub dropped notifications for this
    /// session (slow reader).</summary>
    private const byte ReasonDropped = (byte)LinkResyncReason.NotificationsDropped;

    private readonly object _lock = new();
    private readonly Dictionary<ushort, Session> _sessions = new();
    private readonly int _capacity;
    private bool _disposed;

    /// <param name="capacity">How many undelivered NOTIFY frames a session may
    /// have queued before the oldest are dropped. RESYNC frames don't count and
    /// are never dropped.</param>
    public NotifyRelay(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int SessionCount
    {
        get { lock (_lock) return _sessions.Count; }
    }

    /// <summary>
    /// Starts relaying to a session. Its tags start at 0, independent of every
    /// other session's. <paramref name="deliver"/> and <paramref name="resync"/>
    /// run on the session's own worker thread, never concurrently with each
    /// other; they may block (that only delays this session) and may call back
    /// into the relay. Adding an id that is already present replaces it.
    /// </summary>
    public void AddSession(ushort id, Action<LinkNotifyFrame> deliver, Action<LinkResyncFrame> resync)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        ArgumentNullException.ThrowIfNull(resync);
        var session = new Session(id, deliver, resync);
        Session? replaced;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessions.TryGetValue(id, out replaced);
            _sessions[id] = session;
        }
        replaced?.Close();
        session.Start();
    }

    /// <summary>
    /// Stops relaying to a session and discards whatever it had queued. Doesn't
    /// wait for its worker (this may be called from inside its own callback): a
    /// callback already running when this is called finishes, but no further
    /// one starts.
    /// </summary>
    public void RemoveSession(ushort id)
    {
        Session? session;
        lock (_lock)
        {
            if (!_sessions.Remove(id, out session)) return;
        }
        session.Close();
    }

    /// <summary>
    /// Queues one notification for every session, each with its own next tag.
    /// Returns without running any callback, so it is safe to call from the
    /// device's notification reader. Only v2, non-idle packets are relayed (spec
    /// 8.3: v1 legacy packets are never relayed, every v1 event has a v2 twin),
    /// so the caller may pass every packet the endpoint yields.
    /// <paramref name="notification"/>'s origin must already be attributed.
    /// </summary>
    public void Publish(byte handle, LinkNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.Version != 0x02 || notification.EventId == 0x00) return;
        foreach (var session in Targets())
            session.EnqueueNotify(handle, notification.Origin, notification.Packet, _capacity);
    }

    /// <summary>
    /// Sends every session a RESYNC (spec 8.6), e.g. reason 1 when the device
    /// reattaches or 2 when the hub rebuilds its cache. It is queued behind
    /// each session's pending frames, so a client doesn't re-read state and then
    /// apply older events on top of it.
    /// </summary>
    public void ResyncAll(byte handle, byte reason)
    {
        var frame = new LinkResyncFrame(handle, reason);
        foreach (var session in Targets()) session.EnqueueResync(frame);
    }

    /// <summary>Undelivered NOTIFY frames queued for a session (the one being
    /// delivered excluded). For tests and diagnostics.</summary>
    public int PendingDepth(ushort id)
    {
        Session? session;
        lock (_lock) _sessions.TryGetValue(id, out session);
        return session?.PendingNotifies ?? 0;
    }

    public void Dispose()
    {
        List<Session> all;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            all = _sessions.Values.ToList();
            _sessions.Clear();
        }
        foreach (var session in all) session.Close();
    }

    private List<Session> Targets()
    {
        lock (_lock) return _sessions.Count == 0 ? [] : _sessions.Values.ToList();
    }

    /// <summary>One session's queue and worker. Everything mutable is guarded
    /// by <see cref="_gate"/>, which is never held while a callback runs, so a
    /// blocked callback never blocks a publisher.</summary>
    private sealed class Session(ushort id, Action<LinkNotifyFrame> deliver, Action<LinkResyncFrame> resync)
    {
        private readonly object _gate = new();
        /// <summary>NOTIFY and RESYNC frames in delivery order.</summary>
        private readonly LinkedList<LinkFrame> _queue = new();
        private int _notifies;
        /// <summary>Next Link sequence number: per session, wraps at 0xFFFF, and
        /// independent of the firmware's 8-bit seq inside the packet. Assigned
        /// at publish, so a dropped frame leaves a gap the client can see.</summary>
        private ushort _nextTag;
        /// <summary>Handles that lost frames in the current drop episode, in
        /// the order they first did. Each gets one RESYNC reason 0 when the queue
        /// next empties.</summary>
        private readonly List<byte> _droppedHandles = new();
        private bool _closed;
        private Thread? _worker;

        public int PendingNotifies
        {
            get { lock (_gate) return _notifies; }
        }

        public void Start()
        {
            _worker = new Thread(Run)
            {
                IsBackground = true,
                Name = $"Link notify relay (session {id})",
            };
            _worker.Start();
        }

        public void EnqueueNotify(byte handle, ushort origin, byte[] packet, int capacity)
        {
            lock (_gate)
            {
                if (_closed) return;
                var frame = new LinkNotifyFrame(_nextTag, handle, origin, packet);
                _nextTag = unchecked((ushort)(_nextTag + 1));
                _queue.AddLast(frame);
                _notifies++;
                while (_notifies > capacity) DropOldestNotify();
                Monitor.Pulse(_gate);
            }
        }

        public void EnqueueResync(LinkResyncFrame frame)
        {
            lock (_gate)
            {
                if (_closed) return;
                _queue.AddLast(frame);
                Monitor.Pulse(_gate);
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _queue.Clear();
                _notifies = 0;
                _droppedHandles.Clear();
                Monitor.Pulse(_gate);
            }
        }

        private void DropOldestNotify()
        {
            for (var node = _queue.First; node != null; node = node.Next)
            {
                if (node.Value is not LinkNotifyFrame dropped) continue;
                _queue.Remove(node);
                _notifies--;
                if (!_droppedHandles.Contains(dropped.Handle)) _droppedHandles.Add(dropped.Handle);
                return;
            }
        }

        private void Run()
        {
            while (true)
            {
                LinkFrame next;
                lock (_gate)
                {
                    while (!_closed && _queue.Count == 0 && _droppedHandles.Count == 0)
                        Monitor.Wait(_gate);
                    if (_closed) return;
                    if (_queue.Count > 0)
                    {
                        next = _queue.First!.Value;
                        _queue.RemoveFirst();
                        if (next is LinkNotifyFrame) _notifies--;
                    }
                    else
                    {
                        // Drained after a drop: one RESYNC per handle that lost
                        // frames stands in for them, sent only now so the client
                        // re-reads after every newer event it was given (as the
                        // Mac does). A gap in the tags already told it.
                        next = new LinkResyncFrame(_droppedHandles[0], ReasonDropped);
                        _droppedHandles.RemoveAt(0);
                    }
                }
                try
                {
                    if (next is LinkNotifyFrame notify) deliver(notify);
                    else resync((LinkResyncFrame)next);
                }
                catch (Exception ex)
                {
                    // A failing callback must not kill the session's worker.
                    LinkLog.Write($"Link hub: notify delivery to session {id} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}

namespace DSPiConsole.Core.Link.Hub;

/// <summary>
/// Delivers a store's events in the order its changes happened, without
/// calling handlers while the store's lock is held.
///
/// Raising events after unlocking (as the Mac does) lets two threads' events
/// overtake each other: two quick role changes can reach the live sessions as
/// "viewer, admin" although the store ends on viewer, and an offline/online
/// pair can leave every session showing the device offline. Raising them under
/// the lock would keep the order but let a slow or re-entrant handler stall
/// the store. So a store <see cref="Post"/>s each event while it still holds
/// its lock (queue order is change order), then calls <see cref="Drain"/>
/// after unlocking. Whichever thread drains first delivers everything queued,
/// including what other threads post meanwhile; the rest return at once. An
/// event can therefore arrive on another thread, and just after the call that
/// caused it returns, but never out of order. On one thread (tests, a UI
/// call) delivery is synchronous.
/// </summary>
internal sealed class OrderedEventQueue
{
    private readonly object _gate = new();
    private readonly Queue<Action> _pending = new();
    private bool _draining;

    /// <summary>Queues one event. Call while holding the owner's lock.</summary>
    public void Post(Action raise)
    {
        lock (_gate) _pending.Enqueue(raise);
    }

    /// <summary>Delivers what is queued. Call after releasing the owner's lock.
    /// A handler that throws is logged and skipped, so one bad subscriber can't
    /// strand every later event in the queue.</summary>
    public void Drain()
    {
        lock (_gate)
        {
            if (_draining) return;
            _draining = true;
        }
        while (true)
        {
            Action next;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _draining = false;
                    return;
                }
                next = _pending.Dequeue();
            }
            try
            {
                next();
            }
            catch (Exception e)
            {
                LinkLog.Write($"hub: an event handler threw {e.GetType().Name}: {e.Message}");
            }
        }
    }
}

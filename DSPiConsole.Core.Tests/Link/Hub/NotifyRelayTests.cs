using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>The notification relay (spec 8.3 NOTIFY, 8.6 RESYNC): verbatim
/// fan-out, per-session tags, and the bounded queue that turns a slow reader
/// into one RESYNC instead of a stalled publisher. Ported from the Mac's
/// LinkNotifyRelayTests; waits are on signals with a timeout, never sleeps.</summary>
public class NotifyRelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Everything one session received, in order.</summary>
    private sealed class Sink
    {
        private readonly object _lock = new();
        private readonly List<LinkFrame> _frames = new();

        public void Add(LinkFrame frame)
        {
            lock (_lock) { _frames.Add(frame); Monitor.PulseAll(_lock); }
        }

        public List<LinkFrame> All { get { lock (_lock) return _frames.ToList(); } }
        public List<LinkNotifyFrame> Notifies => All.OfType<LinkNotifyFrame>().ToList();
        public List<LinkResyncFrame> Resyncs => All.OfType<LinkResyncFrame>().ToList();

        /// <summary>Waits until <paramref name="done"/> holds for what arrived.</summary>
        public void WaitFor(Func<List<LinkFrame>, bool> done)
        {
            var deadline = DateTime.UtcNow + Timeout;
            lock (_lock)
            {
                while (!done(_frames))
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) throw new TimeoutException($"Got only {_frames.Count} frames.");
                    Monitor.Wait(_lock, left);
                }
            }
        }

        public void WaitForCount(int count) => WaitFor(f => f.Count >= count);
    }

    private static LinkNotification Note(byte marker, ushort origin = 0) =>
        new([0x02, 0x02, 0, 0, marker], origin, DateTime.UtcNow);

    private static void Add(NotifyRelay relay, ushort id, Sink sink, Action<LinkNotifyFrame>? before = null) =>
        relay.AddSession(id, f => { before?.Invoke(f); sink.Add(f); }, sink.Add);

    [Fact]
    public void PublishReachesEverySessionVerbatimWithOrigin()
    {
        using var relay = new NotifyRelay();
        var a = new Sink();
        var b = new Sink();
        Add(relay, 2, a);
        Add(relay, 3, b);

        byte[] packet = [0x02, 0x02, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x01];
        relay.Publish(0, new LinkNotification(packet, 12, DateTime.UtcNow));

        foreach (var sink in new[] { a, b })
        {
            sink.WaitForCount(1);
            var frame = Assert.Single(sink.Notifies);
            Assert.Equal(packet, frame.Packet);
            Assert.Equal(12, frame.Origin);
            Assert.Equal(0, frame.Handle);
            Assert.Equal(0, frame.Tag);
        }
    }

    [Fact]
    public void TagsAreIndependentPerSessionAndDoNotInherit()
    {
        using var relay = new NotifyRelay();
        var a = new Sink();
        var b = new Sink();
        Add(relay, 2, a);
        for (byte i = 0; i < 3; i++) relay.Publish(0, Note(i));

        Add(relay, 3, b);
        for (byte i = 3; i < 5; i++) relay.Publish(0, Note(i));

        a.WaitForCount(5);
        b.WaitForCount(2);
        Assert.Equal(new ushort[] { 0, 1, 2, 3, 4 }, a.Notifies.Select(f => f.Tag));
        Assert.Equal(new ushort[] { 0, 1 }, b.Notifies.Select(f => f.Tag));
        Assert.Equal(new byte[] { 3, 4 }, b.Notifies.Select(f => f.Packet[4]));
    }

    [Fact]
    public void TagWrapsAtSixteenBits()
    {
        using var relay = new NotifyRelay();
        var sink = new Sink();
        Add(relay, 2, sink);
        for (int i = 0; i < 65536; i++) relay.Publish(0, Note(0));
        relay.Publish(0, Note(0xEE));
        sink.WaitFor(f => f.OfType<LinkNotifyFrame>().Any(n => n.Packet[4] == 0xEE));
        // Frames may have been dropped on the way; the last one is never dropped.
        Assert.Equal(0, sink.Notifies.Single(n => n.Packet[4] == 0xEE).Tag);
    }

    [Fact]
    public void OnlyV2NonIdlePacketsAreRelayed()
    {
        using var relay = new NotifyRelay();
        var sink = new Sink();
        Add(relay, 2, sink);
        relay.Publish(0, new LinkNotification([0x00], 0, DateTime.UtcNow));                       // IDLE
        relay.Publish(0, new LinkNotification([0x02, 0x00, 0, 0], 0, DateTime.UtcNow));           // v2 idle
        relay.Publish(0, new LinkNotification([0x01, 0, 0, 0, 0, 0, 0xA0, 0xC1], 0, DateTime.UtcNow)); // v1 volume
        relay.Publish(0, Note(7));
        sink.WaitForCount(1);
        var frame = Assert.Single(sink.Notifies);
        Assert.Equal(7, frame.Packet[4]);
        Assert.Equal(0, frame.Tag); // skipped packets don't use up tags
    }

    [Fact]
    public void SlowSessionDropsOldestThenGetsOneResyncAndNeverBlocksTheFastOne()
    {
        using var relay = new NotifyRelay(capacity: 4);
        var fast = new Sink();
        var slow = new Sink();
        using var gate = new ManualResetEventSlim(false);
        using var slowEntered = new ManualResetEventSlim(false);

        Add(relay, 2, fast);
        Add(relay, 3, slow, f =>
        {
            if (f.Tag != 0) return;
            slowEntered.Set();
            gate.Wait(Timeout); // stall this session only
        });

        relay.Publish(7, Note(0));
        Assert.True(slowEntered.Wait(Timeout));
        for (byte i = 1; i < 10; i++)
        {
            relay.Publish(7, Note(i));
            // Pace on the fast reader so it never overflows its own queue; the
            // slow one, parked, overflows regardless.
            fast.WaitForCount(i + 1);
        }

        Assert.Equal(Enumerable.Range(0, 10).Select(i => (ushort)i), fast.Notifies.Select(f => f.Tag));
        Assert.Empty(fast.Resyncs);
        Assert.Empty(slow.All); // still stuck delivering frame 0
        Assert.Equal(4, relay.PendingDepth(3));

        gate.Set();
        slow.WaitFor(f => f.OfType<LinkResyncFrame>().Any());

        // Frame 0, then the four newest (1..5 were dropped: the tag gap shows
        // it), then one RESYNC once the queue drained.
        var all = slow.All;
        Assert.Equal(new ushort[] { 0, 6, 7, 8, 9 }, all.Take(5).Cast<LinkNotifyFrame>().Select(f => f.Tag));
        var resync = Assert.IsType<LinkResyncFrame>(all[5]);
        Assert.Equal(7, resync.Handle);
        Assert.Equal((byte)LinkResyncReason.NotificationsDropped, resync.Reason);

        // Carries on: the next frame arrives normally and no second RESYNC.
        relay.Publish(7, Note(10));
        slow.WaitForCount(7);
        Assert.Equal(10, slow.All[6] is LinkNotifyFrame n ? n.Tag : -1);
        Assert.Single(slow.Resyncs);
    }

    [Fact]
    public void OneResyncPerEpisodeAndAnotherAfterFallingBehindAgain()
    {
        using var relay = new NotifyRelay(capacity: 2);
        var sink = new Sink();
        var gate = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        int blockTag = 0;
        Add(relay, 2, sink, f =>
        {
            if (f.Tag != Volatile.Read(ref blockTag)) return;
            entered.Set();
            gate.Wait(Timeout);
        });

        // Episode 1: block on tag 0, overflow, release.
        relay.Publish(0, Note(0));
        Assert.True(entered.Wait(Timeout));
        for (byte i = 1; i < 8; i++) relay.Publish(0, Note(i));
        gate.Set();
        sink.WaitFor(f => f.OfType<LinkResyncFrame>().Count() == 1);
        relay.Publish(0, Note(8)); // tag 8: a sentinel; nothing more was owed before it
        sink.WaitFor(f => f.OfType<LinkNotifyFrame>().Any(n => n.Tag == 8));
        Assert.Single(sink.Resyncs);

        // Episode 2: the session had caught up, so falling behind again earns
        // a second RESYNC.
        gate.Reset();
        entered.Reset();
        Volatile.Write(ref blockTag, 9);
        relay.Publish(0, Note(9));
        Assert.True(entered.Wait(Timeout));
        for (byte i = 10; i < 16; i++) relay.Publish(0, Note(i));
        gate.Set();
        sink.WaitFor(f => f.OfType<LinkResyncFrame>().Count() == 2);
        relay.Publish(0, Note(16));
        sink.WaitFor(f => f.OfType<LinkNotifyFrame>().Any(n => n.Tag == 16));
        Assert.Equal(2, sink.Resyncs.Count);
    }

    [Fact]
    public void EachHandleThatLostFramesGetsItsOwnResync()
    {
        using var relay = new NotifyRelay(capacity: 2);
        var sink = new Sink();
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        Add(relay, 2, sink, f =>
        {
            if (f.Tag != 0) return;
            entered.Set();
            gate.Wait(Timeout);
        });

        relay.Publish(0, Note(0));
        Assert.True(entered.Wait(Timeout));
        relay.Publish(4, Note(1)); // dropped
        relay.Publish(5, Note(2)); // dropped
        relay.Publish(5, Note(3));
        relay.Publish(5, Note(4));
        gate.Set();
        sink.WaitFor(f => f.OfType<LinkResyncFrame>().Count() == 2);
        Assert.Equal(new byte[] { 4, 5 }, sink.Resyncs.Select(r => r.Handle).Order());
        Assert.All(sink.Resyncs, r => Assert.Equal(0, r.Reason));
    }

    [Fact]
    public void ResyncAllReachesEverySessionBehindItsPendingFrames()
    {
        using var relay = new NotifyRelay();
        var a = new Sink();
        var b = new Sink();
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        Add(relay, 2, a, f =>
        {
            if (f.Tag != 0) return;
            entered.Set();
            gate.Wait(Timeout);
        });
        Add(relay, 3, b);

        relay.Publish(3, Note(0));
        Assert.True(entered.Wait(Timeout));
        relay.Publish(3, Note(1));
        relay.Publish(3, Note(2));
        relay.ResyncAll(3, (byte)LinkResyncReason.DeviceReattached);
        gate.Set();

        foreach (var sink in new[] { a, b })
        {
            sink.WaitForCount(4);
            var all = sink.All;
            Assert.Equal(new ushort[] { 0, 1, 2 }, all.Take(3).Cast<LinkNotifyFrame>().Select(f => f.Tag));
            var resync = Assert.IsType<LinkResyncFrame>(all[3]);
            Assert.Equal(3, resync.Handle);
            Assert.Equal((byte)LinkResyncReason.DeviceReattached, resync.Reason);
        }
    }

    [Fact]
    public void RemoveSessionStopsDeliveryAndUpdatesCount()
    {
        using var relay = new NotifyRelay();
        Assert.Equal(0, relay.SessionCount);
        var kept = new Sink();
        var removed = new Sink();
        Add(relay, 2, kept);
        Add(relay, 3, removed);
        Assert.Equal(2, relay.SessionCount);

        relay.RemoveSession(3);
        relay.RemoveSession(3); // twice is harmless
        Assert.Equal(1, relay.SessionCount);

        relay.Publish(0, Note(0));
        relay.Publish(0, Note(1));
        kept.WaitForCount(2);
        Assert.Empty(removed.All);
    }

    [Fact]
    public void PublishWithNoSessionsIsANoOp()
    {
        using var relay = new NotifyRelay();
        relay.Publish(0, Note(0));
        relay.ResyncAll(0, 1);
        Assert.Equal(0, relay.SessionCount);
    }

    [Fact]
    public void AThrowingCallbackDoesNotStopTheSession()
    {
        using var relay = new NotifyRelay();
        var sink = new Sink();
        relay.AddSession(2, f =>
        {
            if (f.Tag == 0) throw new InvalidOperationException("socket gone");
            sink.Add(f);
        }, sink.Add);
        relay.Publish(0, Note(0));
        relay.Publish(0, Note(1));
        sink.WaitForCount(1);
        Assert.Equal(1, sink.Notifies.Single().Tag);
    }

    [Fact]
    public void DisposeStopsEverySession()
    {
        var relay = new NotifyRelay();
        var sink = new Sink();
        Add(relay, 2, sink);
        relay.Dispose();
        Assert.Equal(0, relay.SessionCount);
        relay.Publish(0, Note(0));
        Assert.Throws<ObjectDisposedException>(() => relay.AddSession(3, _ => { }, _ => { }));
        Assert.Empty(sink.All);
    }
}

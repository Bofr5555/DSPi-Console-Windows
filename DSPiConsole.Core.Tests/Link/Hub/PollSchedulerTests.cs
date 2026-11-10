using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;
using static DSPiConsole.Core.Link.Hub.PollScheduler;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>The poll engine (spec 7.6, 8.4): read-only gating, rate cap,
/// shared reads, per-session budget, unsubscribe teardown and failure back-off.
/// Ported from the Mac's LinkPollSchedulerTests. Time is a manual clock and
/// ticks are driven by hand, so every count is exact.</summary>
public class PollSchedulerTests
{
    private static readonly TimeSpan TickLength = TimeSpan.FromMilliseconds(50);
    private static LinkPolicy Policy => LinkPolicy.Embedded ?? throw new InvalidOperationException("commands.json is not embedded");

    // 0x50 GET_STATUS wValue 9: a read. 0xD2 SET_MASTER_VOLUME: control, SET only.
    // 0x51 SAVE_PARAMS: a write-as-read GET, config.
    private static readonly PollSpec Status = new(0, 0x50, 9, 2, 27);

    /// <summary>The router as the scheduler sees it: counts reads, answers a
    /// fixed status and payload.</summary>
    private sealed class FakeRouter
    {
        private readonly object _lock = new();
        private readonly Dictionary<byte, int> _counts = new();
        public LinkStatus Status { get; set; } = LinkStatus.Ok;
        public byte[] Payload { get; set; } = [0xAA, 0xBB, 0xCC];
        public Action<LinkCmdRequest>? During { get; set; }
        public List<LinkCmdRequest> Requests { get; } = new();

        public LinkCmdResponse Execute(LinkCmdRequest request)
        {
            lock (_lock)
            {
                _counts[request.BRequest] = _counts.GetValueOrDefault(request.BRequest) + 1;
                Requests.Add(request);
            }
            During?.Invoke(request);
            return Status == LinkStatus.Ok
                ? new LinkCmdResponse(request.Tag, LinkStatus.Ok, Payload)
                : new LinkCmdResponse(request.Tag, Status);
        }

        public int Count(byte req) { lock (_lock) return _counts.GetValueOrDefault(req); }
    }

    private sealed class Harness
    {
        public FakeRouter Router { get; } = new();
        public ManualClock Clock { get; } = new();
        public List<(ushort Session, int Slot, byte Handle, byte[] Payload)> Polls { get; } = new();
        public List<(ushort Session, int Slot, byte Handle, LinkStatus Status)> Errors { get; } = new();
        public PollScheduler Scheduler { get; }

        public Harness(double maxHz = 20, double budget = 200_000)
        {
            Scheduler = new PollScheduler(Router.Execute, Policy,
                (s, slot, h, p) => Polls.Add((s, slot, h, p)),
                (s, slot, h, st) => Errors.Add((s, slot, h, st)),
                Clock, maxHz, budget, TickLength, driveWithTimer: false);
        }

        public void Ticks(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Clock.Advance(TickLength);
                Scheduler.Tick();
            }
        }

        public int PollsFor(ushort session) => Polls.Count(p => p.Session == session);
    }

    private static PollRequest[] One(int slot, PollSpec spec, double hz) => [new PollRequest(slot, spec, hz)];

    [Fact]
    public void ViewerReceivesVerbatimPayloadsForAReadPoll()
    {
        var h = new Harness();
        var granted = h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 10));
        Assert.Equal(new PollGrant(0, 10), Assert.Single(granted));
        Assert.Equal(1, h.Scheduler.ActiveSpecCount);

        h.Ticks(20); // one second
        Assert.Equal(10, h.Polls.Count);
        Assert.All(h.Polls, p => Assert.Equal(((ushort)2, 0, (byte)0), (p.Session, p.Slot, p.Handle)));
        Assert.Equal(h.Router.Payload, h.Polls[0].Payload);

        // The read went out as the subscribed GET.
        var request = h.Router.Requests[0];
        Assert.Equal(LinkDirection.Get, request.Direction);
        Assert.Equal(((byte)0x50, (ushort)9, (ushort)2, (ushort)27),
            (request.BRequest, request.WValue, request.WIndex, request.WLength));
    }

    [Theory]
    [InlineData(0xD2, LinkRole.Admin)]   // control, and SET only: never a read
    [InlineData(0x51, LinkRole.Admin)]   // a GET, but write-as-read config: never pollable
    [InlineData(0x4F, LinkRole.Admin)]   // not in the table: fails safe
    public void NonReadPollIsRefusedAndNeverExecuted(byte req, LinkRole role)
    {
        var h = new Harness();
        var spec = new PollSpec(0, req, 0, 2, 4);
        var granted = h.Scheduler.Subscribe(2, role, One(0, spec, 10));
        Assert.Equal(0, Assert.Single(granted).Hz);
        Assert.Equal(0, h.Scheduler.ActiveSpecCount);
        Assert.Null(h.Scheduler.GrantedHz(2, 0));

        h.Ticks(20);
        Assert.Equal(0, h.Router.Count(req));
        Assert.Empty(h.Polls);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(16, 10)]
    [InlineData(0, 0)]
    [InlineData(0, -5)]
    [InlineData(0, double.NaN)]
    public void BadSlotOrRateIsRefused(int slot, double hz)
    {
        var h = new Harness();
        Assert.Equal(0, Assert.Single(h.Scheduler.Subscribe(2, LinkRole.Viewer, One(slot, Status, hz))).Hz);
        Assert.Equal(0, h.Scheduler.ActiveSpecCount);
    }

    [Fact]
    public void ARefusedReplacementEmptiesTheSlot()
    {
        var h = new Harness();
        h.Scheduler.Subscribe(2, LinkRole.Control, One(0, Status, 10));
        var granted = h.Scheduler.Subscribe(2, LinkRole.Control, One(0, new PollSpec(0, 0xD2, 0, 2, 4), 10));
        Assert.Equal(0, Assert.Single(granted).Hz);
        Assert.Equal(0, h.Scheduler.ActiveSpecCount);
        h.Ticks(20);
        Assert.Empty(h.Polls);
    }

    [Fact]
    public void ResubscribingASlotReplacesItsPoll()
    {
        var h = new Harness();
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 10));
        var meters = new PollSpec(0, 0x0B, 0, 2, 64);
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, meters, 20));
        Assert.Equal(1, h.Scheduler.ActiveSpecCount);
        h.Ticks(5);
        Assert.Equal(0, h.Router.Count(0x50));
        Assert.Equal(5, h.Router.Count(0x0B));
    }

    [Fact]
    public void GrantedRateIsCappedAndTheReadNeverRunsFaster()
    {
        var h = new Harness();
        Assert.Equal(20, Assert.Single(h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 100))).Hz);
        h.Ticks(40); // two seconds
        Assert.Equal(40, h.Router.Count(0x50));
    }

    [Fact]
    public void FractionalRatesAverageOut()
    {
        var h = new Harness();
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 12));
        h.Ticks(100); // five seconds
        Assert.InRange(h.Router.Count(0x50), 59, 60);
    }

    [Fact]
    public void LateTicksAreMadeUpButNeverBurst()
    {
        var h = new Harness();
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 5));
        // A timer running late (every 100 ms): 5 Hz still holds...
        for (int i = 0; i < 20; i++) { h.Clock.Advance(TimeSpan.FromMilliseconds(100)); h.Scheduler.Tick(); }
        Assert.Equal(10, h.Router.Count(0x50));
        // ...and a 3 s stall costs one read, not fifteen at once.
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Scheduler.Tick();
        Assert.Equal(11, h.Router.Count(0x50));
    }

    [Fact]
    public void IdenticalPollsShareOneReadPerTick()
    {
        var h = new Harness();
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20));
        h.Scheduler.Subscribe(3, LinkRole.Viewer, One(3, Status, 20));
        Assert.Equal(1, h.Scheduler.ActiveSpecCount);

        h.Ticks(40);
        Assert.Equal(40, h.Router.Count(0x50)); // one read per tick, not per session
        Assert.Equal(40, h.PollsFor(2));
        Assert.Equal(40, h.PollsFor(3));
        Assert.All(h.Polls.Where(p => p.Session == 3), p => Assert.Equal(3, p.Slot));
    }

    [Fact]
    public void ASharedReadRunsAtTheFastestRateAndFansOutAtEachGrant()
    {
        var h = new Harness();
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20));
        h.Scheduler.Subscribe(3, LinkRole.Viewer, One(0, Status, 10));
        h.Ticks(40);
        Assert.Equal(40, h.Router.Count(0x50));
        Assert.Equal(40, h.PollsFor(2));
        Assert.Equal(20, h.PollsFor(3));
    }

    [Fact]
    public void DifferingPollsAreSeparateReads()
    {
        var h = new Harness();
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20));
        h.Scheduler.Subscribe(3, LinkRole.Viewer, One(0, Status with { Len = 41 }, 20));
        Assert.Equal(2, h.Scheduler.ActiveSpecCount);
        h.Ticks(10);
        Assert.Equal(20, h.Router.Count(0x50));
    }

    [Fact]
    public void ByteBudgetScalesAHeavySessionDown()
    {
        var h = new Harness();
        // Ten 2000-byte polls at the cap: 400000 B/s, twice the budget.
        var requests = Enumerable.Range(0, 10)
            .Select(i => new PollRequest(i, new PollSpec(0, 0x50, (ushort)i, 2, 2000), 20))
            .ToArray();
        var granted = h.Scheduler.Subscribe(2, LinkRole.Viewer, requests);

        Assert.True(granted.Sum(g => 2000 * g.Hz) <= 200_000 + 0.5);
        Assert.All(granted, g => Assert.Equal(10, g.Hz, 3));

        // Another session's budget is its own.
        Assert.Equal(20, Assert.Single(h.Scheduler.Subscribe(3, LinkRole.Viewer,
            One(0, new PollSpec(0, 0x50, 0, 2, 2000), 20))).Hz);

        // Dropping half the load restores the rest to full rate.
        h.Scheduler.Unsubscribe(2, Enumerable.Range(5, 5));
        for (int slot = 0; slot < 5; slot++) Assert.Equal(20, h.Scheduler.GrantedHz(2, slot)!.Value, 3);
    }

    [Fact]
    public void UnsubscribeStopsDeliveryForThatSessionOnly()
    {
        var h = new Harness();
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20));
        h.Scheduler.Subscribe(3, LinkRole.Viewer, One(0, Status, 20));
        h.Ticks(5);
        Assert.Equal(5, h.PollsFor(2));

        h.Scheduler.Unsubscribe(2, [0]);
        h.Ticks(5);
        Assert.Equal(5, h.PollsFor(2));
        Assert.Equal(10, h.PollsFor(3)); // the shared read carries on for the other
    }

    [Fact]
    public void UnsubscribeAllDropsEverySlotAndTheLastReaderStopsTheRead()
    {
        var h = new Harness();
        h.Scheduler.Subscribe(2, LinkRole.Viewer,
            [new PollRequest(0, Status, 20), new PollRequest(1, new PollSpec(0, 0x0B, 0, 2, 64), 20)]);
        h.Ticks(5);
        int executed = h.Router.Count(0x50);
        Assert.Equal(5, executed);

        h.Scheduler.UnsubscribeAll(2);
        Assert.Equal(0, h.Scheduler.ActiveSpecCount);
        Assert.Null(h.Scheduler.GrantedHz(2, 0));
        h.Ticks(10);
        Assert.Equal(executed, h.Router.Count(0x50));
        Assert.Equal(5, h.Router.Count(0x0B));
    }

    [Fact]
    public void ThreeFailuresMakeOneErrorAndHalveTheRateUntilRecovery()
    {
        var h = new Harness();
        h.Router.Status = LinkStatus.Busy;
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20));
        h.Scheduler.Subscribe(3, LinkRole.Viewer, One(4, Status, 20));

        h.Ticks(3);
        Assert.Equal(3, h.Router.Count(0x50));
        // One poll.error per subscriber, carrying the device's status.
        Assert.Equal(new[] { (2, 0, LinkStatus.Busy), (3, 4, LinkStatus.Busy) },
            h.Errors.Select(e => ((int)e.Session, e.Slot, e.Status)).Order());
        Assert.Empty(h.Polls); // a failed tick sends nothing

        h.Ticks(20);
        Assert.Equal(2, h.Errors.Count); // once per episode, not every tick
        Assert.Equal(3 + 10, h.Router.Count(0x50)); // half rate

        // The device recovers: full rate and delivery resume.
        h.Router.Status = LinkStatus.Ok;
        h.Ticks(2); // the next half-rate read succeeds
        int before = h.Router.Count(0x50);
        h.Ticks(10);
        Assert.Equal(before + 10, h.Router.Count(0x50));
        Assert.NotEmpty(h.Polls);

        // A fresh episode reports again.
        h.Router.Status = LinkStatus.NoDevice;
        h.Ticks(3);
        Assert.Equal(4, h.Errors.Count);
        Assert.All(h.Errors.Skip(2), e => Assert.Equal(LinkStatus.NoDevice, e.Status));
    }

    [Fact]
    public void AnExecutorThatThrowsCountsAsAFailure()
    {
        var h = new Harness();
        h.Router.During = _ => throw new IOException("router gone");
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20));
        h.Ticks(3);
        Assert.Equal(LinkStatus.Error, Assert.Single(h.Errors).Status);
    }

    [Fact]
    public void SessionsSubscribedSeesOtherSessionsOnly()
    {
        var h = new Harness();
        var rtaReads = new HashSet<byte> { 0x0B, 0x0C, 0x0F };
        Assert.False(h.Scheduler.SessionsSubscribed(rtaReads, 2));

        h.Scheduler.Subscribe(3, LinkRole.Viewer, One(0, new PollSpec(0, 0x0F, 0, 2, 200), 10));
        Assert.True(h.Scheduler.SessionsSubscribed(rtaReads, 2));
        Assert.False(h.Scheduler.SessionsSubscribed(rtaReads, 3));     // its own poll doesn't count
        Assert.False(h.Scheduler.SessionsSubscribed(new HashSet<byte> { 0x50 }, 2));

        h.Scheduler.UnsubscribeAll(3);
        Assert.False(h.Scheduler.SessionsSubscribed(rtaReads, 2));
    }

    [Fact]
    public void NoLockIsHeldWhileAReadIsInTheRouter()
    {
        // The router may be holding a poll behind a bulk read when another
        // session's RTA STOP asks who is watching, or a session subscribes: none
        // of that may wait on the device. Ask from another thread mid-read.
        var h = new Harness();
        bool? answered = null;
        IReadOnlyList<PollGrant>? grants = null;
        h.Router.During = _ =>
        {
            var other = new Thread(() =>
            {
                answered = h.Scheduler.SessionsSubscribed(new HashSet<byte> { 0x50 }, 9);
                grants = h.Scheduler.Subscribe(4, LinkRole.Viewer, One(1, Status, 5));
            });
            other.Start();
            Assert.True(other.Join(TimeSpan.FromSeconds(5)), "the scheduler blocked on an in-flight read");
            h.Router.During = null;
        };
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20));
        h.Ticks(1);
        Assert.True(answered);
        Assert.Equal(5, Assert.Single(grants!).Hz);
    }

    [Fact]
    public void AReadUnsubscribedWhileInTheRouterIsNotDelivered()
    {
        var h = new Harness();
        h.Router.During = _ => h.Scheduler.UnsubscribeAll(2);
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20));
        h.Ticks(1);
        Assert.Equal(1, h.Router.Count(0x50));
        Assert.Empty(h.Polls);
    }

    [Fact]
    public void StopForgetsEverythingAndRefusesNewPolls()
    {
        var h = new Harness();
        h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20));
        h.Scheduler.Stop();
        Assert.Equal(0, h.Scheduler.ActiveSpecCount);
        Assert.Equal(0, Assert.Single(h.Scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20))).Hz);
        h.Ticks(5);
        Assert.Equal(0, h.Router.Count(0x50));
    }

    [Fact]
    public void TheWorkerPollsOnItsOwn()
    {
        // The real timer, real clock: only checks that it ticks and stops.
        var router = new FakeRouter();
        using var got = new CountdownEvent(3);
        var scheduler = new PollScheduler(router.Execute, Policy,
            (_, _, _, _) => { if (!got.IsSet) got.Signal(); });
        try
        {
            scheduler.Subscribe(2, LinkRole.Viewer, One(0, Status, 20));
            Assert.True(got.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            scheduler.Dispose();
        }
    }
}

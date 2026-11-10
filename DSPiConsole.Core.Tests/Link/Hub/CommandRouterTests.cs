using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>A device for hub tests: records every request, answers from a
/// script, and can block or be swapped (a new generation).</summary>
internal sealed class FakeHubDevice : IHubDevice
{
    private readonly object _lock = new();
    private readonly List<LinkCmdRequest> _executed = new();
    public HubDeviceInfo Info { get; set; } = new("E46058388B1A2E2C", 1, "1.1.6", 9, 0);
    public ulong Generation { get; set; } = 1;
    public bool IsConnected { get; set; } = true;
    /// <summary>When set, Execute waits on it (to hold the queue).</summary>
    public ManualResetEventSlim? Gate;
    public Func<LinkCmdRequest, LinkCmdResponse>? Answer;
    /// <summary>Runs inside Execute, after recording (a notify racing it).</summary>
    public Action<LinkCmdRequest>? During;

    public IReadOnlyList<LinkCmdRequest> Executed
    {
        get { lock (_lock) return _executed.ToList(); }
    }

    public LinkCmdResponse Execute(LinkCmdRequest request)
    {
        lock (_lock) _executed.Add(request);
        During?.Invoke(request);
        Gate?.Wait(TimeSpan.FromSeconds(10));
        return Answer?.Invoke(request)
               ?? new LinkCmdResponse(request.Tag, LinkStatus.Ok,
                   request.Direction == LinkDirection.Get ? new byte[Math.Min((int)request.WLength, 4)] : Array.Empty<byte>());
    }
}

public class CommandRouterTests
{
    private static readonly LinkPolicy Policy = LinkPolicy.Embedded!;
    private static readonly RouterSession Local = new(LinkSessionIds.Local, LinkRole.Admin, true);
    private static readonly RouterSession Remote = new(2, LinkRole.Control);
    private static readonly RouterSession Other = new(3, LinkRole.Control);
    private static readonly RouterSession Viewer = new(4, LinkRole.Viewer);

    private const byte SetMasterVolume = 0xD2;   // control (spec 11.1)
    private const byte GetPlatform = 0x7F;       // read
    private const byte EnterBootloader = 0xF0;   // config

    private static LinkCmdRequest Set(byte code = SetMasterVolume, ushort value = 0, ushort tag = 1) =>
        new(tag, 0, LinkDirection.Set, code, value, 2, payload: new byte[] { 1, 2, 3, 4 });

    private static LinkCmdRequest Get(byte code = GetPlatform, ushort tag = 1) =>
        new(tag, 0, LinkDirection.Get, code, 0, 2, 7);

    private static RoutedResult Run(CommandRouter router, LinkCmdRequest request, RouterSession session)
    {
        var done = new TaskCompletionSource<RoutedResult>();
        router.Submit(request, session, r => done.TrySetResult(r));
        Assert.True(done.Task.Wait(5000), "the router never answered");
        return done.Task.Result;
    }

    [Fact]
    public void ASetIsRunAndAttributedToItsSession()
    {
        var device = new FakeHubDevice();
        using var router = new CommandRouter(0, device, Policy);
        var r = Run(router, Set(tag: 77), Remote);
        Assert.Equal(LinkStatus.Ok, r.Response.Status);
        Assert.Equal(77, r.Response.Tag);
        Assert.Equal(Remote.Id, r.AttributedTo);
        Assert.Single(device.Executed);
    }

    [Fact]
    public void AGetIsNotAttributed()
    {
        using var router = new CommandRouter(0, new FakeHubDevice(), Policy);
        Assert.Equal(0, Run(router, Get(), Remote).AttributedTo);
    }

    [Fact]
    public void AViewerCantWriteAndControlCantConfigure()
    {
        var device = new FakeHubDevice();
        using var router = new CommandRouter(0, device, Policy);
        Assert.Equal(LinkStatus.Denied, Run(router, Set(), Viewer).Response.Status);
        Assert.Equal(LinkStatus.Denied, Run(router, Set(EnterBootloader), Remote).Response.Status);
        Assert.Equal(LinkStatus.Ok, Run(router, Get(), Viewer).Response.Status);
        Assert.Single(device.Executed);
    }

    [Fact]
    public void ALockKeepsOtherSessionsOutUntilReleased()
    {
        using var router = new CommandRouter(0, new FakeHubDevice(), Policy);
        Assert.True(router.AcquireLock(Remote.Id, TimeSpan.FromSeconds(10)));
        Assert.False(router.AcquireLock(Other.Id, TimeSpan.FromSeconds(10)));
        Assert.Equal(LinkStatus.Locked, Run(router, Set(), Other).Response.Status);
        // The local UI isn't exempt from locks (spec 7.7).
        Assert.Equal(LinkStatus.Locked, Run(router, Set(), Local).Response.Status);
        Assert.Equal(LinkStatus.Ok, Run(router, Set(), Remote).Response.Status);
        router.ReleaseLock(Remote.Id);
        Assert.Equal(LinkStatus.Ok, Run(router, Set(), Other).Response.Status);
    }

    [Fact]
    public void AnExpiredLockCountsAsReleased()
    {
        var now = DateTime.UtcNow;
        using var router = new CommandRouter(0, new FakeHubDevice(), Policy) { Now = () => now };
        Assert.True(router.AcquireLock(Remote.Id, TimeSpan.FromSeconds(5)));
        now += TimeSpan.FromSeconds(6);
        Assert.Null(router.CurrentLockHolder);
        Assert.Equal(LinkStatus.Ok, Run(router, Set(), Other).Response.Status);
        Assert.True(router.AcquireLock(Other.Id, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AClosedSessionsLockGoesWithIt()
    {
        using var router = new CommandRouter(0, new FakeHubDevice(), Policy);
        router.AcquireLock(Remote.Id, TimeSpan.FromSeconds(30));
        router.SessionDidClose(Remote.Id);
        Assert.Null(router.CurrentLockHolder);
    }

    [Fact]
    public void ARemoteSessionIsCappedInFlightButTheLocalUiIsnt()
    {
        var device = new FakeHubDevice { Gate = new ManualResetEventSlim(false) };
        using var router = new CommandRouter(0, device, Policy) { MaxInflightPerSession = 2 };
        var pending = new List<Task<RoutedResult>>();
        for (int i = 0; i < 2; i++)
        {
            var tcs = new TaskCompletionSource<RoutedResult>();
            router.Submit(Set(tag: (ushort)i), Remote, r => tcs.TrySetResult(r));
            pending.Add(tcs.Task);
        }
        // Refused at once, answered in turn after the session's earlier two.
        var third = new TaskCompletionSource<RoutedResult>();
        router.Submit(Set(tag: 9), Remote, r => third.TrySetResult(r));

        var local = new List<Task<RoutedResult>>();
        for (int i = 0; i < 5; i++)
        {
            var tcs = new TaskCompletionSource<RoutedResult>();
            router.Submit(Set(), Local, r => tcs.TrySetResult(r));
            local.Add(tcs.Task);
        }
        Assert.False(third.Task.IsCompleted);
        device.Gate.Set();
        Assert.True(Task.WaitAll(pending.Concat(local).ToArray(), 5000));
        Assert.Equal(LinkStatus.RateLimited, third.Task.Result.Response.Status);
        Assert.All(local, t => Assert.Equal(LinkStatus.Ok, t.Result.Response.Status));
        // The cap counts in flight, not ever: room again once answered.
        Assert.Equal(LinkStatus.Ok, Run(router, Set(), Remote).Response.Status);
    }

    [Fact]
    public void CommandsRunInSubmissionOrderAcrossSessions()
    {
        var device = new FakeHubDevice();
        using var router = new CommandRouter(0, device, Policy) { MaxInflightPerSession = 100 };
        var all = new List<Task>();
        for (ushort i = 0; i < 20; i++)
        {
            var tcs = new TaskCompletionSource();
            router.Submit(Set(value: i), i % 2 == 0 ? Remote : Other, _ => tcs.SetResult());
            all.Add(tcs.Task);
        }
        Assert.True(Task.WaitAll(all.ToArray(), 5000));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (ushort)i), device.Executed.Select(r => r.WValue));
    }

    [Fact]
    public void AttributionGoesToTheOldestWriterInTheWindow()
    {
        var now = DateTime.UtcNow;
        using var router = new CommandRouter(0, new FakeHubDevice(), Policy) { Now = () => now };
        Run(router, Set(value: 1), Remote);
        Run(router, Set(value: 2), Other);
        Assert.Equal(Remote.Id, router.ConsumeAttribution(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(Other.Id, router.ConsumeAttribution(TimeSpan.FromMilliseconds(500)));
        // An extra notify (a merged repeat the firmware sent twice) is still
        // the last writer's while it's fresh, then nobody's.
        Assert.Equal(Other.Id, router.ConsumeAttribution(TimeSpan.FromMilliseconds(500)));
        now += TimeSpan.FromSeconds(1);
        Assert.Equal(0, router.ConsumeAttribution(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void RepeatedWritesToOneParameterMergeAsTheFirmwareCoalesces()
    {
        var now = DateTime.UtcNow;
        using var router = new CommandRouter(0, new FakeHubDevice(), Policy) { Now = () => now };
        for (int i = 0; i < 5; i++) Run(router, Set(value: 7), Remote);
        Run(router, Set(value: 8), Other);
        // One entry for the five repeats, so the next notify is Other's.
        Assert.Equal(Remote.Id, router.ConsumeAttribution(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(Other.Id, router.ConsumeAttribution(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void AnOldWriterExpires()
    {
        var now = DateTime.UtcNow;
        using var router = new CommandRouter(0, new FakeHubDevice(), Policy) { Now = () => now };
        Run(router, Set(), Remote);
        now += TimeSpan.FromSeconds(1);
        Assert.Equal(0, router.ConsumeAttribution(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void AFailedWriteGivesBackItsAttribution()
    {
        var device = new FakeHubDevice { Answer = r => new LinkCmdResponse(r.Tag, LinkStatus.Error) };
        using var router = new CommandRouter(0, device, Policy);
        var r = Run(router, Set(), Remote);
        Assert.Equal(LinkStatus.Error, r.Response.Status);
        Assert.Equal(0, r.AttributedTo);
        Assert.Equal(0, router.ConsumeAttribution(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void ANotifyArrivingDuringTheWriteIsAttributed()
    {
        var device = new FakeHubDevice();
        ushort seen = 0;
        CommandRouter? router = null;
        device.During = _ => seen = router!.ConsumeAttribution(TimeSpan.FromMilliseconds(500));
        using (router = new CommandRouter(0, device, Policy))
            Run(router, Set(), Remote);
        Assert.Equal(Remote.Id, seen);
    }

    [Fact]
    public void ACommandForAReplacedBoardAnswersNoDevice()
    {
        var device = new FakeHubDevice { Gate = new ManualResetEventSlim(false) };
        using var router = new CommandRouter(0, device, Policy);
        var first = new TaskCompletionSource<RoutedResult>();
        var queued = new TaskCompletionSource<RoutedResult>();
        router.Submit(Get(), Remote, r => first.TrySetResult(r));
        router.Submit(Set(), Remote, r => queued.TrySetResult(r));
        device.Generation = 2;   // a switch while the first runs
        device.Gate.Set();
        Assert.Equal(LinkStatus.NoDevice, first.Task.Result.Response.Status);
        Assert.Equal(LinkStatus.NoDevice, queued.Task.Result.Response.Status);
        Assert.Single(device.Executed);
    }

    [Fact]
    public void ShutdownAnswersEveryQueuedCommand()
    {
        var device = new FakeHubDevice { Gate = new ManualResetEventSlim(false) };
        var router = new CommandRouter(0, device, Policy);
        var results = new List<Task<RoutedResult>>();
        for (int i = 0; i < 4; i++)
        {
            var tcs = new TaskCompletionSource<RoutedResult>();
            router.Submit(Set(), Local, r => tcs.TrySetResult(r));
            results.Add(tcs.Task);
        }
        router.Shutdown();
        device.Gate.Set();
        Assert.True(Task.WaitAll(results.ToArray(), 5000));
        Assert.All(results.Skip(1), t => Assert.Equal(LinkStatus.NoDevice, t.Result.Response.Status));
        Assert.Equal(LinkStatus.NoDevice, Run(router, Set(), Local).Response.Status);
        router.Dispose();
    }

    [Fact]
    public void AWedgedDeviceTimesOut()
    {
        var device = new FakeHubDevice { Gate = new ManualResetEventSlim(false) };
        using var router = new CommandRouter(0, device, Policy) { CommandTimeout = TimeSpan.FromMilliseconds(100) };
        Assert.Equal(LinkStatus.Timeout, Run(router, Get(), Remote).Response.Status);
        device.Gate.Set();
    }

    // ── Analyser sharing (spec 7.15) ──

    private static LinkCmdRequest RtaBands(ushort tag = 1) => new(tag, 0, LinkDirection.Get, 0x0B, 0, 2, 82);
    private static LinkCmdRequest RtaControl(ushort value) => new(1, 0, LinkDirection.Get, 0x0E, value, 2, 1);

    [Fact]
    public void AStopIsSwallowedWhileAnotherSessionWatches()
    {
        var now = DateTime.UtcNow;
        var device = new FakeHubDevice();
        using var router = new CommandRouter(0, device, Policy) { Now = () => now };
        Run(router, RtaBands(), Remote);
        var stop = Run(router, RtaControl(0), Other);
        Assert.Equal(LinkStatus.Ok, stop.Response.Status);
        Assert.Equal(new byte[] { 1 }, stop.Response.Payload);
        Assert.DoesNotContain(device.Executed, r => r.BRequest == 0x0E);
    }

    [Fact]
    public void AStopReachesTheDeviceOnceNobodyElseWatches()
    {
        var now = DateTime.UtcNow;
        var device = new FakeHubDevice();
        using var router = new CommandRouter(0, device, Policy) { Now = () => now };
        Run(router, RtaBands(), Remote);
        now += TimeSpan.FromSeconds(6);   // the other watcher went quiet
        Run(router, RtaControl(0), Other);
        Assert.Contains(device.Executed, r => r.BRequest == 0x0E);
    }

    [Fact]
    public void ASessionsOwnWatchingDoesntSwallowItsStop()
    {
        var device = new FakeHubDevice();
        using var router = new CommandRouter(0, device, Policy);
        Run(router, RtaBands(), Remote);
        Run(router, RtaControl(0), Remote);
        Assert.Contains(device.Executed, r => r.BRequest == 0x0E);
    }

    [Fact]
    public void AnotherSessionsPollCountsAsWatching()
    {
        var device = new FakeHubDevice();
        using var router = new CommandRouter(0, device, Policy) { OtherSessionPolls = (codes, excluding) => excluding != Remote.Id && codes.Contains(0x0B) };
        Run(router, RtaControl(0), Other);
        Assert.DoesNotContain(device.Executed, r => r.BRequest == 0x0E);
    }

    [Fact]
    public void TheEqParameterKeyNamesChannelAndBand()
    {
        var a = new LinkCmdRequest(1, 0, LinkDirection.Set, 0x42, 0, 2, payload: new byte[] { 3, 5, 0, 0 });
        var b = new LinkCmdRequest(1, 0, LinkDirection.Set, 0x42, 0, 2, payload: new byte[] { 3, 6, 0, 0 });
        Assert.NotEqual(CommandRouter.ParameterKey(a), CommandRouter.ParameterKey(b));
    }
}

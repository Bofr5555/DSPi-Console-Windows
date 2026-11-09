using DSPiConsole.Core.Rta;
using static DSPiConsole.Core.Tests.RtaWireTests;

namespace DSPiConsole.Core.Tests;

/// <summary>
/// Sharing the device's one analyser with other consoles: the last console to
/// push drives, the rest follow. Port of the macOS Console's RtaSharingTests
/// (rta_sharing_spec.md §3). Each tick advances the clock past the status
/// cadence, so status and config are read back on every tick, as the Mac tests
/// do with a zero status interval.
/// </summary>
public class RtaSharingTests
{
    /// <summary>A device with one analyser configuration, driven only through
    /// vendor requests: SET_CONFIG stages, GET_CONFIG reads back. Records what
    /// the engine sent so the tests can count pushes and readbacks.</summary>
    private sealed class Device : IRtaTransport
    {
        /// <summary>What the device holds.</summary>
        public RtaConfig Config = new(RtaWire.TapOutput, 0x0001);
        /// <summary>False: every SET is refused (STALLed), so the readback never agrees.</summary>
        public bool Applies = true;
        /// <summary>What the device clamps averaging to.</summary>
        public ushort? ClampAvgMs;
        /// <summary>Like the firmware: a SET is staged and applied later from
        /// the main loop (<see cref="ApplyStaged"/>), so a readback in the
        /// same tick still sees the old configuration.</summary>
        public bool Staged;
        private RtaConfig? _staged;

        public void ApplyStaged()
        {
            if (_staged is { } c) Config = c;
            _staged = null;
        }

        public readonly List<RtaConfig> Pushes = new();
        public int ConfigReads;
        public readonly List<(byte Request, ushort Value)> BandReads = new();
        public int BinReads;

        public byte[]? RtaIn(byte request, ushort value, int length)
        {
            switch (request)
            {
                case RtaWire.GetCaps when value == 0: return CapsBytes();
                case RtaWire.GetCaps: return Centres(value, length);
                case RtaWire.GetConfig: ConfigReads++; return Config.ToBytes();
                case RtaWire.GetBins: BinReads++; return null;
                case RtaWire.GetBands or RtaWire.GetBandsAll: BandReads.Add((request, value)); return null;
                case RtaWire.Control: return new byte[] { 0 };
                default: return null;
            }
        }

        public bool RtaOut(byte request, ushort value, byte[] data)
        {
            if (request != RtaWire.SetConfig || RtaConfig.Parse(data) is not { } c) return false;
            Pushes.Add(c);
            if (!Applies) return false;
            var applied = ClampAvgMs is { } clamp ? c with { AvgMs = clamp } : c;
            if (Staged) _staged = applied;
            else Config = applied;
            return true;
        }

        private static byte[] Centres(ushort chunk, int length)
        {
            var all = Enumerable.Range(0, RtaWire.MaxBands).Select(i => (ushort)Math.Round(10 * Math.Pow(10, i / 10.0))).ToArray();
            return all.Skip((chunk - 1) * RtaWire.CentresPerChunk).Take(length / 2)
                .SelectMany(hz => new[] { (byte)hz, (byte)(hz >> 8) }).ToArray();
        }
    }

    private const uint OwnSession = 1;
    private readonly Device _device = new();
    private readonly RtaEngine _engine;
    private double _now;

    public RtaSharingTests()
    {
        _engine = new RtaEngine(_device, () => _now);
        _engine.FetchCaps();
        Assert.True(_engine.Supported);
        _engine.SetOptions(new RtaOptions(10, 300, 12));
    }

    private void Tick(int n = 1)
    {
        for (int i = 0; i < n; i++)
        {
            _now += 0.6;
            _engine.Tick();
        }
    }

    private static RtaConfig Out(ushort mask) => new(RtaWire.TapOutput, mask, 10, 300, 12);

    private void Own(RtaConfig c) => _engine.DeviceAnnounced(c, own: true, OwnSession);
    private void Foreign(RtaConfig c, uint origin = 7) => _engine.DeviceAnnounced(c, own: false, origin);

    private static RtaChannelSelection Outputs(params int[] ch) => new(RtaWire.TapOutput, ch);

    // ── Own echo ──

    /// <summary>Our echo confirms the push: no more pushes, the clamped
    /// averaging is not mistaken for a difference, and the readback stops.</summary>
    [Fact]
    public void OwnEchoConfirmsAndStopsReadback()
    {
        // Staged, so the push tick's readback disagrees and only the echo
        // can confirm it.
        _device.Staged = true;
        _device.ClampAvgMs = 250;
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b10));
        Tick();
        Assert.Single(_device.Pushes);

        _device.ApplyStaged();
        Own(Out(0b10) with { AvgMs = 250 });
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);

        int reads = _device.ConfigReads;
        Tick(5);
        Assert.Single(_device.Pushes);            // a clamped value is not re-pushed
        Assert.Equal(reads, _device.ConfigReads); // the announcement replaces the readback
        Assert.False(_engine.ConfigRejected);
    }

    /// <summary>A refused configuration ends rejected; the echo of a later
    /// accepted push clears it.</summary>
    [Fact]
    public void OwnEchoClearsRejection()
    {
        _device.Applies = false;
        _device.Config = Out(0b10);
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick(4);
        Assert.True(_engine.ConfigRejected);
        Own(Out(0b1));
        Assert.False(_engine.ConfigRejected);
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);
    }

    // ── Following ──

    /// <summary>Someone else's configuration without our channels is followed:
    /// no push over it, and the device's channels are what views draw.</summary>
    [Fact]
    public void ForeignAnnouncementFollows()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        _device.Config = Out(0b100);
        Foreign(Out(0b100));
        Assert.Equal(RtaOwnership.Following(7), _engine.Ownership);
        Assert.Equal(Outputs(2), _engine.EffectiveSelection);

        int pushes = _device.Pushes.Count;
        Tick(3);
        Assert.Equal(pushes, _device.Pushes.Count); // a follower never pushes
        Assert.Equal((RtaWire.GetBands, (ushort)2), _device.BandReads[^1]); // bands for the device's channel
        Assert.False(_engine.ConfigRejected);
    }

    /// <summary>A UART or I2C writer arrives with origin 0, followed the same way.</summary>
    [Fact]
    public void UnattributedAnnouncementFollowsWithOriginZero()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        Foreign(new RtaConfig(RtaWire.TapInput, 0b11), origin: 0);
        Assert.Equal(RtaOwnership.Following(0), _engine.Ownership);
        Assert.Equal(RtaWire.TapInput, _engine.EffectiveSelection?.Tap);
    }

    /// <summary>A foreign superset of what we want is someone else's choice
    /// like any other: followed, not pushed over and not taken as ours.</summary>
    [Fact]
    public void ForeignSupersetIsFollowed()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        _device.Config = Out(0b11);
        Foreign(Out(0b11));
        Assert.Equal(RtaOwnership.Following(7), _engine.Ownership);
        Assert.Equal(Outputs(0, 1), _engine.EffectiveSelection);
        int pushes = _device.Pushes.Count;
        Tick(3);
        Assert.Equal(pushes, _device.Pushes.Count);
    }

    /// <summary>A host-sourced announcement that no longer matches is an older
    /// push of ours, superseded by one in flight: never followed.</summary>
    [Fact]
    public void AStaleEchoOfOursIsNotFollowed()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        Own(Out(0b100));
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);
        Assert.Null(_engine.EffectiveSelection);
    }

    // ── Taking control ──

    /// <summary>Choosing channels while following pushes ours alone: the other
    /// console follows us rather than keeping its channels in the mask.</summary>
    [Fact]
    public void UserChangeTakesControlWithOwnMask()
    {
        var id = _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        _device.Config = Out(0b100);
        Foreign(Out(0b100));
        Assert.Equal(RtaOwnership.Following(7), _engine.Ownership);

        _engine.Update(id, new RtaRequest(RtaWire.TapOutput, 0b11));
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);
        Assert.Null(_engine.EffectiveSelection);
        Tick();
        Assert.Equal(0b11, _device.Pushes[^1].ChannelMask);
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);
    }

    /// <summary>The Take control button does the same without changing the selection.</summary>
    [Fact]
    public void TakeControlPushesOwnMask()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        _device.Config = Out(0b100);
        Foreign(Out(0b100));
        _engine.TakeControl();
        Tick();
        Assert.Equal(0b1, _device.Pushes[^1].ChannelMask);
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);
    }

    /// <summary>At the other tap there is nothing to keep: the push is ours alone.</summary>
    [Fact]
    public void TakeControlAtTheOtherTapPushesOursOnly()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        var theirs = new RtaConfig(RtaWire.TapInput, 0b1000, 10);
        _device.Config = theirs;
        Foreign(theirs);
        _engine.TakeControl();
        Tick();
        Assert.Equal(RtaWire.TapOutput, _device.Pushes[^1].Tap);
        Assert.Equal(0b1, _device.Pushes[^1].ChannelMask);
    }

    /// <summary>Changing a device-side option is a choice too.</summary>
    [Fact]
    public void ChangingAnOptionTakesControl()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        _device.Config = Out(0b100);
        Foreign(Out(0b100));
        _engine.SetOptions(new RtaOptions(9, 300, 12));
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);
        Tick();
        Assert.Equal(new RtaConfig(RtaWire.TapOutput, 0b1, 9, 300, 12), _device.Pushes[^1]);
    }

    /// <summary>After an options change, showing another display of the same
    /// selection is still not a choice: a console that took the analyser
    /// meanwhile keeps it.</summary>
    [Fact]
    public void AnotherDisplayAfterAnOptionsChangeDoesNotTakeControl()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        _engine.SetOptions(new RtaOptions(9, 300, 12));
        Tick();
        Foreign(new RtaConfig(RtaWire.TapOutput, 0b100, 9));
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Assert.Equal(RtaOwnership.Following(7), _engine.Ownership);
    }

    /// <summary>A view re-publishing the same request does not steal the
    /// analyser. (The engine's identical-request shortcut; another display of
    /// the same selection is <see cref="ShowingAnotherDisplayDoesNotTakeControl"/>.)</summary>
    [Fact]
    public void TheSameRequestDoesNotTakeControl()
    {
        var id = _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        Foreign(Out(0b100));
        _engine.Update(id, new RtaRequest(RtaWire.TapOutput, 0b1));
        Assert.Equal(RtaOwnership.Following(7), _engine.Ownership);
    }

    /// <summary>Showing a second display of the same selection (the bars
    /// beside the graph) is not a choice, so it never pushes over the console
    /// being followed.</summary>
    [Fact]
    public void ShowingAnotherDisplayDoesNotTakeControl()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1, WantsBins: true));
        Tick();
        _device.Config = Out(0b100);
        Foreign(Out(0b100));
        int pushes = _device.Pushes.Count;

        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick(3);
        Assert.Equal(RtaOwnership.Following(7), _engine.Ownership);
        Assert.Equal(pushes, _device.Pushes.Count);
    }

    /// <summary>Hiding the only display and showing it again keeps following.</summary>
    [Fact]
    public void ReshowingAfterHidingAllKeepsFollowing()
    {
        var id = _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        _device.Config = Out(0b100);
        Foreign(Out(0b100));
        int pushes = _device.Pushes.Count;

        _engine.Release(id);
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick(3);
        Assert.Equal(RtaOwnership.Following(7), _engine.Ownership);
        Assert.Equal(pushes, _device.Pushes.Count);
    }

    /// <summary>While driving, showing a display again does not push the same
    /// configuration over again: the device kept it.</summary>
    [Fact]
    public void ReshowingWhileDrivingDoesNotPushAgain()
    {
        _device.Staged = true;
        var id = _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b10));
        Tick();
        _device.ApplyStaged();
        Own(Out(0b10));
        _engine.Release(id);
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b10));
        Tick(3);
        Assert.Single(_device.Pushes);
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);
    }

    /// <summary>The echo of a push made before an averaging change matches on
    /// tap, mask and size, but must not confirm the change unsent.</summary>
    [Fact]
    public void AnEarlierEchoDoesNotSwallowAnOptionsChange()
    {
        _device.Staged = true;
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b10));
        Tick();
        _engine.SetOptions(new RtaOptions(10, 500, 12));
        _device.ApplyStaged();
        Own(Out(0b10)); // the 300 ms push's echo
        Tick();
        Assert.Equal((ushort)500, _device.Pushes[^1].AvgMs);
    }

    /// <summary>A lost announcement is caught by the slow readback: the
    /// console follows rather than drawing silence for its own channels.</summary>
    [Fact]
    public void ALostAnnouncementIsCaughtByTheBackstopReadback()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        Own(Out(0b1));
        _device.Config = Out(0b100); // its announcement never arrives
        Tick(5);                      // 3 s: the readback waits for 5 s
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);
        Tick(4);
        Assert.Equal(RtaOwnership.Following(0), _engine.Ownership);
        Assert.Equal(Outputs(2), _engine.EffectiveSelection);
    }

    /// <summary>Which bands are populated follows the transform size the device
    /// runs, the followed console's while following.</summary>
    [Fact]
    public void FollowingUsesTheFollowedTransformSize()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        Foreign(new RtaConfig(RtaWire.TapOutput, 0b100, 8));
        Assert.Equal(8, _engine.AnalysedFftOrder);
        _engine.TakeControl();
        Assert.Equal(10, _engine.AnalysedFftOrder);
    }

    /// <summary>A change someone else makes while nothing here watches is
    /// followed when a display comes back, not pushed over.</summary>
    [Fact]
    public void AChangeWhileHiddenIsFollowedOnReshow()
    {
        var id = _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        Own(Out(0b1));
        _engine.Release(id);
        _device.Config = Out(0b100);
        Foreign(Out(0b100));

        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick(3);
        Assert.Equal(RtaOwnership.Following(7), _engine.Ownership);
        Assert.Single(_device.Pushes);
    }

    // ── What the displays draw ──

    /// <summary>While following at the other tap the displays draw the
    /// device's channels, and re-registering the page's own request on a
    /// relayout keeps following.</summary>
    [Fact]
    public void DisplaysDrawTheFollowedSelectionWithoutTakingControl()
    {
        var own = Outputs(0);
        var id = _engine.Subscribe(new RtaRequest(own.Tap, own.Mask));
        Tick();
        var theirs = new RtaConfig(RtaWire.TapInput, 0b110, 10);
        _device.Config = theirs;
        Foreign(theirs);

        var drawn = RtaChannelSelection.Drawn(own, _engine.EffectiveSelection);
        Assert.Equal(new RtaChannelSelection(RtaWire.TapInput, new[] { 1, 2 }), drawn);

        _engine.Update(id, new RtaRequest(own.Tap, own.Mask));
        Tick();
        Assert.Equal(RtaOwnership.Following(7), _engine.Ownership);
        Assert.Single(_device.Pushes); // drawing the followed selection pushes nothing
    }

    /// <summary>Driving, the displays draw the page's own selection.</summary>
    [Fact]
    public void DisplaysDrawTheirOwnSelectionWhileDriving()
    {
        var own = new RtaChannelSelection(RtaWire.TapInput, new[] { 0, 1 });
        Assert.Null(_engine.EffectiveSelection);
        Assert.Equal(own, RtaChannelSelection.Drawn(own, _engine.EffectiveSelection));
    }

    // ── Readback (firmware without the announcement) ──

    /// <summary>A readback that agreed and then changes means another host:
    /// follow with an unknown origin, and stop pushing.</summary>
    [Fact]
    public void ReadbackFollowsAfterAnEarlierAgreement()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);

        _device.Config = Out(0b1000);
        Tick();
        Assert.Equal(RtaOwnership.Following(0), _engine.Ownership);
        Assert.Equal(Outputs(3), _engine.EffectiveSelection);
        int pushes = _device.Pushes.Count;
        Tick(4);
        Assert.Equal(pushes, _device.Pushes.Count);
        Assert.False(_engine.ConfigRejected);

        // A further change is followed in place.
        _device.Config = Out(0b10000);
        Tick();
        Assert.Equal(Outputs(4), _engine.EffectiveSelection);
    }

    /// <summary>A readback that never agreed is a refusal, never a reason to follow.</summary>
    [Fact]
    public void ANeverAgreeingReadbackEndsRejected()
    {
        _device.Applies = false;
        _device.Config = Out(0b1000);
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick(6);
        Assert.Equal(3, _device.Pushes.Count); // three attempts, then give up
        Assert.True(_engine.ConfigRejected);
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);
    }

    /// <summary>A device switch forgets the announcement, so the readback
    /// comes back.</summary>
    [Fact]
    public void ConfigurationChangedResumesReadback()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        Own(Out(0b1));
        int reads = _device.ConfigReads;
        Tick(2);
        Assert.Equal(reads, _device.ConfigReads);

        _engine.ConfigurationChanged();
        Tick(2);
        Assert.True(_device.ConfigReads > reads);
    }

    /// <summary>A disconnect forgets whom we were following.</summary>
    [Fact]
    public void DisconnectStopsFollowing()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick();
        Foreign(Out(0b100));
        _engine.DeviceDisconnected();
        Assert.Equal(RtaOwnership.Driving, _engine.Ownership);
        Assert.Null(_engine.EffectiveSelection);
    }

    // ── Bins ──

    /// <summary>A follower reads the bins when the device analyses one channel,
    /// even though its own selection has several, so it draws the same fine
    /// curve.</summary>
    [Fact]
    public void AFollowerReadsBinsForASingleChannelConfig()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b11, WantsBins: true));
        Tick();
        Assert.Equal(0, _device.BinReads); // a driver analysing two channels reads no bins
        _device.Config = Out(0b100);
        Foreign(Out(0b100));
        Tick(3);
        Assert.True(_device.BinReads > 0);
    }

    /// <summary>A follower does not read bins while the device rotates through
    /// several channels, even though its own selection is one.</summary>
    [Fact]
    public void AFollowerSkipsBinsForAMultichannelConfig()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1, WantsBins: true));
        Tick();
        _device.Config = Out(0b110);
        Foreign(Out(0b110));
        int reads = _device.BinReads;
        Tick(3);
        Assert.Equal(reads, _device.BinReads);
    }

    /// <summary>Bar displays never ask for bins.</summary>
    [Fact]
    public void BarsAloneReadNoBins()
    {
        _engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1));
        Tick(3);
        Assert.Equal(0, _device.BinReads);
    }
}

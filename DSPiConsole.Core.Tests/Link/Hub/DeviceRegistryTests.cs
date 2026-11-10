using System.Text.Json;
using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>
/// The hub's device inventory (spec 7.4) and its naming rules
/// (device_name_spec.md section 5). Port of the registry cases in the macOS
/// Console's LinkHubCoreTests and LinkHubDeviceNameTests, plus handle reuse,
/// the grace on an injected clock, and adoption-once.
/// </summary>
public sealed class DeviceRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _namesPath;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    public DeviceRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DeviceRegistryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _namesPath = Path.Combine(_dir, "link-device-names.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private DeviceRegistry MakeRegistry() => new(_namesPath, _clock);

    /// <summary>A registry whose names file already holds these entries.</summary>
    private DeviceRegistry MakeRegistry(params (string Serial, string Name)[] stored)
    {
        File.WriteAllText(_namesPath, JsonSerializer.Serialize(stored.ToDictionary(s => s.Serial, s => s.Name)));
        return MakeRegistry();
    }

    private static HubDeviceInfo Info(string serial = "E46058388B1A2E2C", string? boardName = null, string fw = "1.1.7") =>
        new(serial, Platform: 1, Firmware: fw, Outputs: 9, Inputs: 8, WireVersion: 30, BoardName: boardName);

    private static List<DeviceRegistryEvent> Record(DeviceRegistry registry)
    {
        var events = new List<DeviceRegistryEvent>();
        registry.DeviceEvent += (_, e) => events.Add(e);
        return events;
    }

    // ── Handles ──

    [Fact]
    public void FirstDeviceGetsHandleZeroAndRaisesAdded()
    {
        var reg = MakeRegistry();
        var events = Record(reg);
        var d = reg.DeviceOnline(Info());
        Assert.Equal(0, d.Handle);
        Assert.Equal(LinkDeviceState.Online, d.State);
        Assert.Null(d.LockedBy);
        var added = Assert.IsType<DeviceRegistryEvent.Added>(Assert.Single(events));
        Assert.Equal(d, added.Device);
        Assert.Equal(d, reg.Device(0));
        Assert.Equal(d, reg.Device("E46058388B1A2E2C"));
    }

    [Fact]
    public void DevicesGetTheLowestFreeHandles()
    {
        var reg = MakeRegistry();
        Assert.Equal(0, reg.DeviceOnline(Info("AAAA")).Handle);
        Assert.Equal(1, reg.DeviceOnline(Info("BBBB")).Handle);
        Assert.Equal(2, reg.DeviceOnline(Info("CCCC")).Handle);
        Assert.Equal(new byte[] { 0, 1, 2 }, reg.Devices.Select(d => d.Handle));
    }

    [Fact]
    public void DeviceReturningWithinGraceKeepsItsHandle()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info("AAAA"));
        var b = reg.DeviceOnline(Info("BBBB")).Handle;
        var events = Record(reg);

        reg.DeviceOffline("BBBB");
        Assert.Equal(LinkDeviceState.Offline, reg.Device(b)!.State);
        _clock.Advance(TimeSpan.FromSeconds(30));
        reg.ReapOffline();
        Assert.Equal(b, reg.DeviceOnline(Info("BBBB")).Handle);

        Assert.Collection(events,
            e => Assert.Equal(LinkDeviceState.Offline, Assert.IsType<DeviceRegistryEvent.Changed>(e).Device.State),
            e => Assert.Equal(LinkDeviceState.Online, Assert.IsType<DeviceRegistryEvent.Changed>(e).Device.State));
    }

    [Fact]
    public void OfflineDeviceIsReapedOnlyAfterTheGrace()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info("AAAA"));
        var events = Record(reg);
        reg.DeviceOffline("AAAA");
        reg.DeviceOffline("AAAA"); // already offline: nothing

        _clock.Advance(TimeSpan.FromSeconds(59));
        reg.ReapOffline();
        Assert.NotNull(reg.Device("AAAA"));

        _clock.Advance(TimeSpan.FromSeconds(1));
        reg.ReapOffline();
        Assert.Null(reg.Device("AAAA"));
        Assert.Empty(reg.Devices);
        Assert.Equal(2, events.Count);
        var removed = Assert.IsType<DeviceRegistryEvent.Removed>(events[1]);
        Assert.Equal((byte)0, removed.Handle);
        Assert.Equal("AAAA", removed.Serial);
    }

    /// <summary>A different board plugged in while the old one is in its grace
    /// must not take the old one's handle: a client still holding it would
    /// send commands to the wrong board.</summary>
    [Fact]
    public void ReplacementBoardDuringGraceGetsTheNextFreeHandle()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info("AAAA"));
        reg.DeviceOffline("AAAA");
        Assert.Equal(1, reg.DeviceOnline(Info("BBBB")).Handle);

        _clock.Advance(TimeSpan.FromSeconds(60));
        reg.ReapOffline();
        // Once AAAA is gone its handle is free again, and the lowest.
        Assert.Equal(0, reg.DeviceOnline(Info("CCCC")).Handle);
        // AAAA returning after removal is a new device.
        Assert.Equal(2, reg.DeviceOnline(Info("AAAA")).Handle);
    }

    [Fact]
    public void ZeroGraceReapsAtOnce()
    {
        var reg = MakeRegistry();
        reg.OfflineGrace = TimeSpan.Zero;
        reg.DeviceOnline(Info("AAAA"));
        reg.DeviceOffline("AAAA");
        reg.ReapOffline();
        Assert.Null(reg.Device("AAAA"));
    }

    [Fact]
    public void FullTableEvictsTheLongestOfflineDevice()
    {
        var reg = MakeRegistry();
        for (int i = 0; i < 255; i++) reg.DeviceOnline(Info($"S{i:D4}"));
        reg.DeviceOffline("S0007");
        _clock.Advance(TimeSpan.FromSeconds(1));
        reg.DeviceOffline("S0003");
        var events = Record(reg);

        Assert.Equal(7, reg.DeviceOnline(Info("NEW1")).Handle);
        Assert.Equal(new byte[] { 7 }, events.OfType<DeviceRegistryEvent.Removed>().Select(r => r.Handle));
        Assert.Equal(3, reg.DeviceOnline(Info("NEW2")).Handle);
        Assert.Throws<InvalidOperationException>(() => reg.DeviceOnline(Info("NEW3")));
    }

    [Fact]
    public void BackOnlineByStateStopsTheGrace()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info("AAAA"));
        reg.DeviceOffline("AAAA");
        reg.SetState("AAAA", LinkDeviceState.Updating);
        _clock.Advance(TimeSpan.FromMinutes(5));
        reg.ReapOffline();
        Assert.Equal(LinkDeviceState.Updating, reg.Device("AAAA")!.State);
    }

    [Fact]
    public void IdentityChangesRaiseChangedAndRepeatsRaiseNothing()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info());
        var events = Record(reg);
        reg.DeviceOnline(Info());
        Assert.Empty(events);
        reg.DeviceOnline(Info(fw: "1.1.8"));
        var changed = Assert.IsType<DeviceRegistryEvent.Changed>(Assert.Single(events));
        Assert.Equal("1.1.8", changed.Device.Info.Firmware);
        Assert.Equal(0, changed.Device.Handle);
    }

    // ── Locks ──

    [Fact]
    public void LockHolderRaisesChangedOnlyWhenItChanges()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info());
        var events = Record(reg);

        Assert.True(reg.SetLockHolder(0, 5));
        Assert.True(reg.SetLockHolder(0, 5));
        Assert.True(reg.SetLockHolder(0, null));
        Assert.False(reg.SetLockHolder(9, 5));

        Assert.Equal(new ushort?[] { 5, null },
                     events.Cast<DeviceRegistryEvent.Changed>().Select(c => c.Device.LockedBy));
    }

    [Fact]
    public void GoingOfflineDropsTheLock()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info());
        reg.SetLockHolder(0, LinkSessionIds.Local);
        reg.DeviceOffline("E46058388B1A2E2C");
        Assert.Null(reg.Device(0)!.LockedBy);
    }

    [Fact]
    public void EventsArriveInChangeOrderAcrossThreads()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info());
        ushort? last = 0;
        reg.DeviceEvent += (_, e) => { if (e is DeviceRegistryEvent.Changed c) last = c.Device.LockedBy; };

        Parallel.For(0, 2000, i => reg.SetLockHolder(0, i % 3 == 0 ? null : (ushort)(i % 3 + 1)));

        Assert.Equal(reg.Device(0)!.LockedBy, last);
    }

    // ── Wire ──

    [Fact]
    public void ToLinkDeviceInfoCarriesEveryField()
    {
        var reg = MakeRegistry();
        var info = new HubDeviceInfo("E46058388B1A2E2C", 2, "1.1.6", 9, FirmwareBeta: 5, Inputs: 8, WireVersion: 32,
                                     Link: LinkDeviceLinkKind.Usb, BoardName: "Living room");
        reg.DeviceOnline(info);
        reg.SetLockHolder(0, 12);
        var wire = reg.Device(0)!.ToLinkDeviceInfo();
        Assert.Equal(new LinkDeviceInfo(0, "E46058388B1A2E2C", "Living room", 2, "1.1.6", 5, 9, 8, 32,
                                        LinkDeviceState.Online, LinkDeviceLinkKind.Usb, 12), wire);

        var json = JsonSerializer.Serialize(wire, LinkJson.Options);
        Assert.Contains("\"locked_by\":12", json);
        Assert.Contains("\"state\":\"online\"", json);
    }

    // ── Names ──

    [Fact]
    public void DefaultNameIsTheSerialsLastSix()
    {
        var reg = MakeRegistry();
        Assert.Equal("DSPi 1A2E2C", reg.DeviceOnline(Info("E46058388B1A2E2C")).Name);
        Assert.Equal("DSPi AAAA", reg.DeviceOnline(Info("AAAA")).Name);
    }

    [Fact]
    public void NamePrecedenceIsBoardThenStoredThenDefault()
    {
        Assert.Equal("Rack left", MakeRegistry(("S1", "Hub name")).DeviceOnline(Info("S1", "Rack left")).Name);
        Assert.Equal("Hub name", MakeRegistry(("S1", "Hub name")).DeviceOnline(Info("S1", "")).Name);
        Assert.Equal("Hub name", MakeRegistry(("S1", "Hub name")).DeviceOnline(Info("S1", null)).Name);
        Assert.Equal("DSPi S1", MakeRegistry(Array.Empty<(string, string)>()).DeviceOnline(Info("S1", "")).Name);
    }

    [Fact]
    public void RenamedNamePersistsBySerial()
    {
        var first = MakeRegistry();
        first.DeviceOnline(Info());
        var events = Record(first);
        Assert.Equal(DeviceRenameOutcome.Renamed, first.Rename(0, "  Living room "));
        Assert.Equal("Living room", first.Device(0)!.Name);
        Assert.Single(events);

        Assert.Equal("Living room", MakeRegistry().DeviceOnline(Info()).Name);
    }

    [Fact]
    public void RenameToBlankRestoresTheDefault()
    {
        var reg = MakeRegistry(("E46058388B1A2E2C", "Kitchen"));
        reg.DeviceOnline(Info());
        Assert.Equal(DeviceRenameOutcome.Renamed, reg.Rename(0, "  "));
        Assert.Equal("DSPi 1A2E2C", reg.Device(0)!.Name);
        Assert.Null(reg.StoredName("E46058388B1A2E2C"));
        Assert.Null(MakeRegistry().StoredName("E46058388B1A2E2C"));
    }

    [Fact]
    public void RenameIsCappedForHubStoredNames()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info());
        reg.Rename(0, new string('x', 500));
        Assert.Equal(DeviceRegistry.MaxStoredNameLength, reg.Device(0)!.Name.Length);
    }

    [Fact]
    public void RenameOfANameCapableBoardIsLeftToTheBoard()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info(boardName: ""));
        var events = Record(reg);
        Assert.Equal(DeviceRenameOutcome.BoardStoresName, reg.Rename(0, "Studio"));
        Assert.Equal(DeviceRenameOutcome.NoDevice, reg.Rename(7, "Studio"));
        Assert.Empty(events);
        Assert.Null(reg.StoredName("E46058388B1A2E2C"));
    }

    [Fact]
    public void OldFirmwareCanBeRenamedWhileOffline()
    {
        var reg = MakeRegistry();
        reg.DeviceOnline(Info());
        reg.DeviceOffline("E46058388B1A2E2C");
        Assert.Equal(DeviceRenameOutcome.Renamed, reg.Rename(0, "Kitchen"));
        Assert.Equal("Kitchen", reg.StoredName("E46058388B1A2E2C"));
    }

    [Fact]
    public void BoardNameWinsAndRetiresTheStoredName()
    {
        var reg = MakeRegistry(("AAAA", "Hub name"));
        Assert.Equal("Rack left", reg.DeviceOnline(Info("AAAA", "Rack left")).Name);
        Assert.Null(reg.StoredName("AAAA"));
        Assert.Null(reg.AdoptionName(Info("AAAA", "Rack left")));
        Assert.Null(MakeRegistry().StoredName("AAAA"));
    }

    [Fact]
    public void UnnamedCapableBoardShowsAndAdoptsTheStoredName()
    {
        var reg = MakeRegistry(("AAAA", "Hub name"));
        Assert.Equal("Hub name", reg.DeviceOnline(Info("AAAA", "")).Name);
        Assert.Equal("Hub name", reg.AdoptionName(Info("AAAA", "")));
        Assert.Equal("Hub name", reg.StoredName("AAAA")); // kept until the board confirms
    }

    /// <summary>Spec 7.4: the stored name is written to the board once. The
    /// accepted write retires the entry, so a later attach (even after a user
    /// cleared the name on the board) never adopts again.</summary>
    [Fact]
    public void AdoptionHappensOnce()
    {
        var reg = MakeRegistry(("AAAA", "Hub name"));
        reg.DeviceOnline(Info("AAAA", ""));
        Assert.Equal("Hub name", reg.AdoptionName(Info("AAAA", "")));
        reg.AdoptionAccepted("AAAA");

        Assert.Null(reg.AdoptionName(Info("AAAA", "")));
        reg.DeviceOffline("AAAA");
        reg.DeviceOnline(Info("AAAA", ""));
        Assert.Null(reg.AdoptionName(Info("AAAA", "")));
        Assert.Null(MakeRegistry().AdoptionName(Info("AAAA", "")));
    }

    [Fact]
    public void OldFirmwareNeverAdopts()
    {
        var reg = MakeRegistry(("AAAA", "Hub name"));
        Assert.Equal("Hub name", reg.DeviceOnline(Info("AAAA", null)).Name);
        Assert.Null(reg.AdoptionName(Info("AAAA", null)));
    }

    [Fact]
    public void AdoptionNameIsTheBoardsAsciiForm()
    {
        string cafe = "Caf" + (char)0xE9 + " " + (char)0x2013 + " Rack " + char.ConvertFromUtf32(0x1F50A);
        string emoji = char.ConvertFromUtf32(0x1F50A) + char.ConvertFromUtf32(0x1F50A);
        var reg = MakeRegistry(("AAAA", cafe), ("BBBB", emoji));
        Assert.Equal("Caf Rack", reg.AdoptionName(Info("AAAA", "")));
        Assert.Null(reg.AdoptionName(Info("BBBB", "")));
    }

    /// <summary>The 0x10 notify sets the name and retires the stored entry; a
    /// clear falls back to the default and is never undone by adoption.</summary>
    [Fact]
    public void BoardNameNotifyUpdatesTheNameAndRetiresTheStore()
    {
        var reg = MakeRegistry(("AAAA", "Hub name"));
        reg.DeviceOnline(Info("AAAA", ""));
        var events = Record(reg);

        reg.BoardNameStored("AAAA", "Hub name");
        Assert.Null(reg.StoredName("AAAA"));
        Assert.Empty(events); // the adopted name was already shown

        reg.BoardNameStored("AAAA", "");
        var changed = Assert.IsType<DeviceRegistryEvent.Changed>(Assert.Single(events));
        Assert.Equal("DSPi AAAA", changed.Device.Name);
        Assert.Equal("", reg.Device("AAAA")!.Info.BoardName);
        Assert.Null(reg.AdoptionName(reg.Device("AAAA")!.Info));

        reg.BoardNameStored("AAAA", "Studio");
        Assert.Equal("Studio", reg.Device("AAAA")!.Name);
    }

    [Fact]
    public void BoardNameNotifyForAnUnknownBoardStillRetiresItsEntry()
    {
        var reg = MakeRegistry(("AAAA", "Hub name"));
        reg.BoardNameStored("AAAA", "Board");
        Assert.Null(reg.StoredName("AAAA"));
    }

    [Fact]
    public void BoardNameCleanup()
    {
        Assert.Equal("Living Room", DeviceRegistry.BoardName("  Living   Room  "));
        Assert.Equal("", DeviceRegistry.BoardName(char.ConvertFromUtf32(0x1F50A) + char.ConvertFromUtf32(0x1F50A)));
        Assert.Equal(31, DeviceRegistry.BoardName(new string('A', 40)).Length);
        // A cut that ends on a space is trimmed.
        Assert.Equal(new string('A', 30), DeviceRegistry.BoardName(new string('A', 30) + " B"));
    }

    [Fact]
    public void DamagedNamesFileStartsEmpty()
    {
        File.WriteAllText(_namesPath, "{ not json");
        var reg = MakeRegistry();
        Assert.Equal("DSPi 1A2E2C", reg.DeviceOnline(Info()).Name);
        Assert.Equal(DeviceRenameOutcome.Renamed, reg.Rename(0, "Kitchen"));
        Assert.Equal("Kitchen", MakeRegistry().StoredName("E46058388B1A2E2C"));
    }

    [Fact]
    public void NullPathKeepsNamesInMemory()
    {
        var reg = new DeviceRegistry(null, _clock);
        reg.DeviceOnline(Info());
        reg.Rename(0, "Kitchen");
        Assert.Equal("Kitchen", reg.Device(0)!.Name);
        Assert.False(File.Exists(_namesPath));
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public void Advance(TimeSpan by) => Now += by;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

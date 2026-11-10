using System.Text.Json;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Link.Hub;

/// <summary>One device in the hub's inventory (spec 7.4).</summary>
public sealed record RegisteredDevice(
    /// <summary>0..254, stable until <c>device.removed</c> (spec 2).</summary>
    byte Handle,
    HubDeviceInfo Info,
    /// <summary>The resolved name: the board's own, else the hub-stored one,
    /// else "DSPi " and the serial's last six characters.</summary>
    string Name,
    LinkDeviceState State,
    /// <summary>The session holding the exclusive lock, or null.</summary>
    ushort? LockedBy)
{
    /// <summary>The <c>device.list</c> / <c>device.added</c> / <c>device.changed</c>
    /// object (spec 7.4).</summary>
    public LinkDeviceInfo ToLinkDeviceInfo() => new(
        Handle: Handle,
        Serial: Info.Serial,
        Name: Name,
        Platform: Info.Platform,
        Fw: Info.Firmware,
        FwBeta: Info.FirmwareBeta,
        Outputs: Info.Outputs,
        Inputs: Info.Inputs,
        WireVersion: Info.WireVersion,
        State: State,
        Link: Info.Link,
        LockedBy: LockedBy);
}

/// <summary>What changed in the inventory; each maps to one spec 7.4 event.</summary>
public abstract record DeviceRegistryEvent
{
    private DeviceRegistryEvent() { }
    /// <summary><c>device.added</c>.</summary>
    public sealed record Added(RegisteredDevice Device) : DeviceRegistryEvent;
    /// <summary><c>device.changed</c>.</summary>
    public sealed record Changed(RegisteredDevice Device) : DeviceRegistryEvent;
    /// <summary><c>device.removed</c>.</summary>
    public sealed record Removed(byte Handle, string Serial) : DeviceRegistryEvent;
}

/// <summary>The outcome of <see cref="DeviceRegistry.Rename"/>.</summary>
public enum DeviceRenameOutcome
{
    Renamed,
    /// <summary>No device has that handle.</summary>
    NoDevice,
    /// <summary>The board's firmware stores its own name: write REQ_SET_DEVICE_NAME
    /// (0x56) to it instead, and let its 0x10 notify reach
    /// <see cref="DeviceRegistry.BoardNameStored"/> (spec 7.4).</summary>
    BoardStoresName,
}

/// <summary>
/// The hub's device inventory (spec 7.4): maps each board's serial to the small
/// handle binary frames carry, resolves its name, tracks online / offline /
/// updating with a grace period, and raises the added / changed / removed
/// events the hub relays to every session. Pure logic: the hub feeds it.
/// Port of the macOS Console's DeviceRegistry.swift; the naming rules are the
/// firmware repo's device_name_spec.md section 5.
///
/// Thread-safe. <see cref="DeviceEvent"/> is raised outside the lock, in the
/// order the changes happened, on whichever thread is delivering (often the
/// caller's); see <see cref="OrderedEventQueue"/>.
/// </summary>
public sealed class DeviceRegistry
{
    /// <summary>Handle 255 is reserved (spec 2).</summary>
    private const int HandleCount = 255;
    /// <summary>A hub-stored name off the network is cut to this, so a hostile
    /// <c>device.rename</c> can't grow the names file without bound.</summary>
    public const int MaxStoredNameLength = 64;

    private readonly object _lock = new();
    private readonly OrderedEventQueue _events = new();
    private readonly TimeProvider _time;
    private readonly string? _namesPath;
    private readonly Dictionary<string, RegisteredDevice> _bySerial = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _offlineSince = new(StringComparer.Ordinal);
    /// <summary>Hub-stored names by serial. Only boards that can't store a name
    /// rely on these; for the rest an entry lives until it's adopted.</summary>
    private readonly Dictionary<string, string> _storedNames;

    /// <param name="namesPath">The hub-stored names file (a JSON object of
    /// serial to name, what the Mac keeps in its LinkDeviceNames default); the
    /// app passes %LOCALAPPDATA%\DSPiConsole\link-device-names.json. Null keeps
    /// names in memory only. A missing or damaged file starts empty.</param>
    /// <param name="time">The clock; tests pass one they can advance.</param>
    public DeviceRegistry(string? namesPath, TimeProvider? time = null)
    {
        _namesPath = namesPath;
        _time = time ?? TimeProvider.System;
        _storedNames = LoadNames(namesPath);
    }

    /// <summary>How long a device that dropped keeps its handle, so a brief
    /// unplug or a firmware re-enumeration doesn't renumber it (spec 7.4: 60 s).</summary>
    public TimeSpan OfflineGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Every inventory change, for the hub to relay.</summary>
    public event EventHandler<DeviceRegistryEvent>? DeviceEvent;

    // ── Presence ──

    /// <summary>
    /// The device is attached with this identity. Gives a new serial the lowest
    /// free handle; a known serial (online, or offline within its grace) keeps
    /// its own, and its identity is refreshed (firmware after an update, the
    /// wire version once read). The name is resolved again each time, as spec
    /// 7.4 asks. Raises <see cref="DeviceRegistryEvent.Added"/>, or
    /// <see cref="DeviceRegistryEvent.Changed"/> when anything differs.
    /// </summary>
    /// <exception cref="InvalidOperationException">All 255 handles are held by
    /// online devices.</exception>
    public RegisteredDevice DeviceOnline(HubDeviceInfo info)
    {
        RegisteredDevice device;
        lock (_lock)
        {
            _offlineSince.Remove(info.Serial);
            var name = ResolveNameLocked(info);
            if (_bySerial.TryGetValue(info.Serial, out var existing))
            {
                device = existing with { Info = info, Name = name, State = LinkDeviceState.Online };
                _bySerial[info.Serial] = device;
                if (device != existing) PostLocked(new DeviceRegistryEvent.Changed(device));
            }
            else
            {
                device = new RegisteredDevice(AllocateHandleLocked(), info, name, LinkDeviceState.Online, null);
                _bySerial[info.Serial] = device;
                PostLocked(new DeviceRegistryEvent.Added(device));
            }
        }
        _events.Drain();
        return device;
    }

    /// <summary>The device dropped. It stays listed, offline and unlocked, with
    /// its handle until <see cref="ReapOffline"/> finds its grace over.</summary>
    public void DeviceOffline(string serial) => SetState(serial, LinkDeviceState.Offline);

    /// <summary>Removes every device offline for at least <see cref="OfflineGrace"/>,
    /// raising <see cref="DeviceRegistryEvent.Removed"/> for each. The hub calls
    /// this on a timer (every few seconds).</summary>
    public void ReapOffline()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            foreach (var serial in _offlineSince.Where(o => now - o.Value >= OfflineGrace).Select(o => o.Key).ToList())
                RemoveLocked(serial);
        }
        _events.Drain();
    }

    /// <summary>Sets online / offline / updating. Going offline starts the
    /// grace and drops the lock holder; any other state stops the grace, so a
    /// device marked back online is never reaped. Raises changed when it differs.</summary>
    public void SetState(string serial, LinkDeviceState state)
    {
        lock (_lock)
        {
            if (!_bySerial.TryGetValue(serial, out var d) || d.State == state) return;
            if (state == LinkDeviceState.Offline)
            {
                _offlineSince[serial] = _time.GetUtcNow();
                d = d with { State = state, LockedBy = null };
            }
            else
            {
                _offlineSince.Remove(serial);
                d = d with { State = state };
            }
            _bySerial[serial] = d;
            PostLocked(new DeviceRegistryEvent.Changed(d));
        }
        _events.Drain();
    }

    /// <summary>Records who holds the device's exclusive lock (spec 7.7);
    /// raises changed when it differs. False for an unknown handle.</summary>
    public bool SetLockHolder(byte handle, ushort? session)
    {
        lock (_lock)
        {
            if (FindLocked(handle) is not { } d) return false;
            if (d.LockedBy == session) return true;
            d = d with { LockedBy = session };
            _bySerial[d.Info.Serial] = d;
            PostLocked(new DeviceRegistryEvent.Changed(d));
        }
        _events.Drain();
        return true;
    }

    // ── Names ──

    /// <summary>
    /// <c>device.rename</c> for a board whose firmware can't store a name
    /// (<see cref="HubDeviceInfo.BoardName"/> null): the hub keeps the name and
    /// raises changed itself (spec 7.4). The name is trimmed and cut to
    /// <see cref="MaxStoredNameLength"/>; a blank one clears the stored name, so
    /// the device shows its default again. Works while the device is offline.
    /// A name-capable board is left alone and answers
    /// <see cref="DeviceRenameOutcome.BoardStoresName"/>.
    /// </summary>
    public DeviceRenameOutcome Rename(byte handle, string name)
    {
        lock (_lock)
        {
            if (FindLocked(handle) is not { } d) return DeviceRenameOutcome.NoDevice;
            if (d.Info.BoardName != null) return DeviceRenameOutcome.BoardStoresName;
            var clean = (name ?? "").Trim();
            if (clean.Length > MaxStoredNameLength) clean = clean[..MaxStoredNameLength].TrimEnd();
            if (clean.Length == 0) RemoveStoredNameLocked(d.Info.Serial);
            else SetStoredNameLocked(d.Info.Serial, clean);
            var resolved = clean.Length == 0 ? DefaultName(d.Info.Serial) : clean;
            if (resolved != d.Name)
            {
                d = d with { Name = resolved };
                _bySerial[d.Info.Serial] = d;
                PostLocked(new DeviceRegistryEvent.Changed(d));
            }
        }
        _events.Drain();
        return DeviceRenameOutcome.Renamed;
    }

    /// <summary>
    /// The board reported its stored name (<c>NOTIFY_EVT_DEVICE_NAME</c>, 0x10,
    /// already decoded). On name-capable firmware this is the only thing that
    /// sets the registry name, so a rename from any source is seen. It retires
    /// the hub-stored entry, so a later clear is never undone by adoption; an
    /// empty name falls back to the default. Raises changed when the shown
    /// name differs.
    /// </summary>
    public void BoardNameStored(string serial, string name)
    {
        lock (_lock)
        {
            RemoveStoredNameLocked(serial);
            if (!_bySerial.TryGetValue(serial, out var d)) return;
            var resolved = name.Length == 0 ? DefaultName(serial) : name;
            var updated = d with { Info = d.Info with { BoardName = name }, Name = resolved };
            _bySerial[serial] = updated;
            if (updated.Name != d.Name) PostLocked(new DeviceRegistryEvent.Changed(updated));
        }
        _events.Drain();
    }

    /// <summary>
    /// The name to write to a name-capable board that reports none, at attach
    /// (spec 7.4): the hub-stored name in the board's ASCII form, or null when
    /// there is nothing to adopt (old firmware, a named board, no stored name,
    /// or one with no printable ASCII). The entry stays until the board accepts
    /// the write (<see cref="AdoptionAccepted"/>) or reports any name, so it
    /// is written once.
    /// </summary>
    public string? AdoptionName(HubDeviceInfo info)
    {
        if (info.BoardName != "") return null;
        lock (_lock)
        {
            if (!_storedNames.TryGetValue(info.Serial, out var stored)) return null;
            var ascii = BoardName(stored);
            return ascii.Length == 0 ? null : ascii;
        }
    }

    /// <summary>The board accepted the adoption write: its stored entry is done with.</summary>
    public void AdoptionAccepted(string serial)
    {
        lock (_lock) RemoveStoredNameLocked(serial);
    }

    /// <summary>The hub-stored name for a serial, or null.</summary>
    public string? StoredName(string serial)
    {
        lock (_lock) return _storedNames.GetValueOrDefault(serial);
    }

    /// <summary>A name a network client sent, as the board can store it:
    /// printable ASCII, gaps left by dropped characters closed, ends trimmed,
    /// at most 31 characters (spec 7.4).</summary>
    public static string BoardName(string name) => DeviceNameText.BoardName(name);

    /// <summary>"DSPi " and the serial's last six characters.</summary>
    public static string DefaultName(string serial) =>
        "DSPi " + (serial.Length > 6 ? serial[^6..] : serial);

    // ── Lookup ──

    /// <summary>Every device, by handle.</summary>
    public IReadOnlyList<RegisteredDevice> Devices
    {
        get { lock (_lock) return _bySerial.Values.OrderBy(d => d.Handle).ToArray(); }
    }

    public RegisteredDevice? Device(byte handle)
    {
        lock (_lock) return FindLocked(handle);
    }

    public RegisteredDevice? Device(string serial)
    {
        lock (_lock) return _bySerial.GetValueOrDefault(serial);
    }

    // ── Private (lock held) ──

    private RegisteredDevice? FindLocked(byte handle)
    {
        foreach (var d in _bySerial.Values)
            if (d.Handle == handle) return d;
        return null;
    }

    private void PostLocked(DeviceRegistryEvent e) => _events.Post(() => DeviceEvent?.Invoke(this, e));

    private void RemoveLocked(string serial)
    {
        _offlineSince.Remove(serial);
        if (_bySerial.Remove(serial, out var d))
            PostLocked(new DeviceRegistryEvent.Removed(d.Handle, serial));
    }

    /// <summary>The lowest handle not in use. A device still in its grace holds
    /// its handle, so a different board plugged in meanwhile gets the next one.
    /// With every handle taken, the device offline longest gives its up early.</summary>
    private byte AllocateHandleLocked()
    {
        var used = new bool[HandleCount];
        foreach (var d in _bySerial.Values) used[d.Handle] = true;
        for (int h = 0; h < HandleCount; h++)
            if (!used[h]) return (byte)h;

        if (_offlineSince.Count == 0)
            throw new InvalidOperationException("Every device handle is in use.");
        var oldest = _offlineSince.MinBy(o => o.Value).Key;
        byte freed = _bySerial[oldest].Handle;
        RemoveLocked(oldest);
        return freed;
    }

    /// <summary>Board name when set, else the hub-stored name, else the
    /// default. A non-empty board name retires the stored entry.</summary>
    private string ResolveNameLocked(HubDeviceInfo info)
    {
        if (!string.IsNullOrEmpty(info.BoardName))
        {
            RemoveStoredNameLocked(info.Serial);
            return info.BoardName;
        }
        return _storedNames.GetValueOrDefault(info.Serial) ?? DefaultName(info.Serial);
    }

    private void SetStoredNameLocked(string serial, string name)
    {
        if (_storedNames.TryGetValue(serial, out var old) && old == name) return;
        _storedNames[serial] = name;
        SaveNamesLocked();
    }

    private void RemoveStoredNameLocked(string serial)
    {
        if (_storedNames.Remove(serial)) SaveNamesLocked();
    }

    // ── Persistence ──

    private void SaveNamesLocked()
    {
        if (_namesPath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_namesPath))!);
            var temp = _namesPath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(
                new SortedDictionary<string, string>(_storedNames, StringComparer.Ordinal),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _namesPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LinkLog.Write($"hub: could not save device names: {e.Message}");
        }
    }

    private static Dictionary<string, string> LoadNames(string? path)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (path == null) return names;
        try
        {
            if (!File.Exists(path)) return names;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllBytes(path));
            foreach (var (serial, name) in loaded ?? new())
                if (!string.IsNullOrWhiteSpace(serial) && !string.IsNullOrWhiteSpace(name)) names[serial] = name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            LinkLog.Write($"hub: could not read device names, starting empty: {e.Message}");
        }
        return names;
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPiConsole.Core.Link.Hub;

/// <summary>Why an <c>auth.token</c> or <c>auth.pair</c> was refused. Map it to
/// a wire code with <see cref="LinkAuthFailureExtensions.ToErrorCode"/>.</summary>
public enum LinkAuthFailure
{
    /// <summary>No pairing window is open: never opened, cancelled, consumed
    /// by a pairing, or killed by five wrong PINs.</summary>
    NoPairingActive,
    WrongPin,
    /// <summary>A window was open but its two minutes had run out. The window
    /// is closed by this attempt.</summary>
    Expired,
    /// <summary>Too many recent failures from this address (spec 6.2). The
    /// attempt was not judged and does not count as another failure.</summary>
    RateLimited,
    /// <summary>The token is malformed or matches no paired client. A revoked
    /// client reads this way too: no tombstone of its hash is kept.</summary>
    UnknownToken,
}

public static class LinkAuthFailureExtensions
{
    /// <summary>The spec 7.1 error code to answer with: <c>rate_limited</c>
    /// for a blocked address, <c>unauthenticated</c> for everything else, so
    /// a guesser learns nothing about why a PIN or token failed.</summary>
    public static string ToErrorCode(this LinkAuthFailure failure) =>
        failure == LinkAuthFailure.RateLimited ? LinkErrorCode.RateLimited : LinkErrorCode.Unauthenticated;
}

/// <summary>One client that has paired with this hub (spec 6.2). The token is
/// never kept, only the lowercase hex SHA-256 of its 32 raw bytes.</summary>
public sealed record LinkPairedClient(
    /// <summary><c>cid</c>: assigned in order, never reused, so a revoked
    /// client's id can't come back attached to someone else.</summary>
    int Cid,
    string Name,
    LinkRole Role,
    DateTimeOffset Created,
    /// <summary>The last successful <c>auth.token</c>; null until the first.</summary>
    DateTimeOffset? LastSeen,
    string TokenHash)
{
    /// <summary>The <c>auth.list</c> entry (spec 7.3). The store doesn't know
    /// which clients have a live session, so the caller says.</summary>
    public LinkAuthClient ToWire(bool? online = null) => new(Cid, Name, Role, Created, LastSeen, online);
}

/// <summary>A client's role changed: its live sessions take the new role at
/// once (spec 6.2).</summary>
public sealed record LinkClientRoleChange(int Cid, LinkRole Role);

/// <summary>The outcome of <see cref="LinkAuthStore.Authenticate"/>.</summary>
public sealed record LinkAuthResult
{
    private LinkAuthResult(LinkPairedClient? client, LinkAuthFailure? failure)
    {
        Client = client;
        Failure = failure;
    }

    public LinkPairedClient? Client { get; }
    public LinkAuthFailure? Failure { get; }

    [MemberNotNullWhen(true, nameof(Client))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool Succeeded => Client != null;

    internal static LinkAuthResult Success(LinkPairedClient client) => new(client, null);
    internal static LinkAuthResult Fail(LinkAuthFailure failure) => new(null, failure);
}

/// <summary>The outcome of <see cref="LinkAuthStore.Pair"/>. <see cref="Token"/>
/// is the only copy of the raw token that ever exists on the hub: send it in
/// the <c>ok</c> and drop it.</summary>
public sealed record LinkPairResult
{
    private LinkPairResult(LinkPairedClient? client, string? token, LinkAuthFailure? failure)
    {
        Client = client;
        Token = token;
        Failure = failure;
    }

    public LinkPairedClient? Client { get; }
    public string? Token { get; }
    public LinkAuthFailure? Failure { get; }

    [MemberNotNullWhen(true, nameof(Client), nameof(Token))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool Succeeded => Client != null;

    internal static LinkPairResult Success(LinkPairedClient client, string token) => new(client, token, null);
    internal static LinkPairResult Fail(LinkAuthFailure failure) => new(null, null, failure);
}

/// <summary>
/// The DSPi Link hub's authentication state (spec 6, 7.3): who this hub is
/// (<c>hid</c> and name), whether it asks for a PIN, which clients have paired,
/// and the short-lived pairing window. Port of the macOS Console's
/// AuthStore.swift, except that a new hub starts in <c>none</c> (open), as
/// spec 1.2 section 6.1 requires; the Mac predates 1.2 and starts in <c>pin</c>.
///
/// Thread-safe: connection handlers call in from socket threads and the
/// settings UI from its own. Every event is raised outside the lock, in the
/// order the changes happened, on whichever thread is delivering at the time
/// (often, but not always, the caller's); see <see cref="OrderedEventQueue"/>.
/// A UI subscriber marshals to its dispatcher.
///
/// The pairing window and the rate-limit ledger live only in memory: a restart
/// should carry neither an open window nor a block across it.
/// </summary>
public sealed class LinkAuthStore
{
    /// <summary>Failures from one address inside <see cref="RateWindow"/>
    /// before it is blocked, and wrong PINs before a window PIN dies (6.2).</summary>
    public const int FailureLimit = 5;
    /// <summary>The span failures are counted over, and the block's length.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);
    /// <summary>How long a pairing window stays open (spec 6.2: 2 minutes).</summary>
    public static readonly TimeSpan PairingTtl = TimeSpan.FromSeconds(120);
    /// <summary>A client name off the network is cut to this, so a hostile
    /// <c>auth.pair</c> can't grow the store file without bound.</summary>
    public const int MaxClientNameLength = 64;

    private const int TokenBytes = 32;
    private const int StoreVersion = 1;
    /// <summary>Addresses tracked before stale ledger entries are swept, so
    /// failures from many addresses can't grow memory without bound.</summary>
    private const int LedgerSweepThreshold = 256;

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly OrderedEventQueue _events = new();

    private PersistedState _state;

    private string? _pin;
    private DateTimeOffset _pinExpiry;
    private int _pinFailures;
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.Ordinal);

    /// <param name="path">The store file; the app passes
    /// %LOCALAPPDATA%\DSPiConsole\link-auth.json, tests a temp path. A missing,
    /// unreadable or corrupt file starts a fresh hub with a new id (a corrupt
    /// one is kept aside as <c>.corrupt</c> first).</param>
    /// <param name="time">The clock; tests pass one they can advance.</param>
    public LinkAuthStore(string path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        var loaded = Load(path, out bool unreadable);
        if (loaded != null)
        {
            _state = loaded;
        }
        else if (unreadable)
        {
            // Locked by a scan or a backup right now: run from a fresh
            // identity in memory, but never write it over the real one, or
            // every paired client would have to pair again.
            _persist = false;
            _state = new PersistedState
            {
                Version = StoreVersion,
                HubId = Guid.NewGuid(),
                HubName = DefaultHubName(),
                AuthMode = LinkAuthMode.None,
                Clients = new(),
                NextCid = 1,
            };
        }
        else
        {
            _state = new PersistedState
            {
                Version = StoreVersion,
                HubId = Guid.NewGuid(),
                HubName = DefaultHubName(),
                AuthMode = LinkAuthMode.None,
                Clients = new(),
                NextCid = 1,
            };
            lock (_lock) SaveLocked();
        }
    }

    /// <summary>The longest hub name, in UTF-8 bytes: one DNS-SD label (the
    /// name is the advertised instance name).</summary>
    public const int MaxHubNameBytes = 63;

    /// <summary>A name from the settings page or a client's <c>hub.rename</c>:
    /// control characters dropped, trimmed, cut to <see cref="MaxHubNameBytes"/>;
    /// blank restores the default.</summary>
    public static string CleanHubName(string? value)
    {
        var name = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        while (System.Text.Encoding.UTF8.GetByteCount(name) > MaxHubNameBytes)
            name = name[..^(char.IsLowSurrogate(name[^1]) && name.Length > 1 ? 2 : 1)];
        name = name.Trim();
        return name.Length == 0 ? DefaultHubName() : name;
    }

    /// <summary>The default for a new hub: "&lt;computer name&gt; DSPi Console".</summary>
    public static string DefaultHubName() => $"{Environment.MachineName} DSPi Console";

    // ── Events ──

    /// <summary>The hub's name changed (here or by <c>hub.rename</c>): re-advertise.</summary>
    public event EventHandler<string>? HubNameChanged;
    /// <summary>The auth mode changed: re-advertise and answer hellos with the new one.</summary>
    public event EventHandler<LinkAuthMode>? AuthModeChanged;
    /// <summary>A client was revoked: close its live sessions with 4002 (spec 6.2).</summary>
    public event EventHandler<int>? ClientRevoked;
    /// <summary>A client's role changed: its live sessions take it at once (spec 6.2).</summary>
    public event EventHandler<LinkClientRoleChange>? ClientRoleChanged;
    /// <summary>Anything the settings UI shows changed: name, mode, the client
    /// list (including a last-seen stamp), or the pairing window opening or
    /// closing. A window that simply runs out raises nothing; the UI counts
    /// down to <see cref="PairingExpires"/> itself.</summary>
    public event EventHandler? Changed;

    // ── Identity and mode ──

    /// <summary><c>hid</c>: made once and kept, because clients key their token
    /// on it rather than on the address (spec 6.2).</summary>
    public Guid HubId
    {
        get { lock (_lock) return _state.HubId; }
    }

    /// <summary>The user-visible hub name (hello, TXT record, <c>hub.rename</c>).
    /// Setting a blank name restores <see cref="DefaultHubName"/>.</summary>
    public string HubName
    {
        get { lock (_lock) return _state.HubName; }
        set
        {
            var name = CleanHubName(value);
            lock (_lock)
            {
                if (name == _state.HubName) return;
                _state.HubName = name;
                SaveLocked();
                _events.Post(() => HubNameChanged?.Invoke(this, name));
                PostChangedLocked();
            }
            _events.Drain();
        }
    }

    /// <summary><c>none</c> (every session is admin) or <c>pin</c>. Starts
    /// <c>none</c> (spec 6.1). Switching to <c>none</c> closes any open pairing
    /// window. Tokens survive a switch: spec 6.2 revokes them only when a
    /// <em>fixed</em> PIN or its mode changes, and this hub uses window PINs.</summary>
    public LinkAuthMode AuthMode
    {
        get { lock (_lock) return _state.AuthMode; }
        set
        {
            lock (_lock)
            {
                if (value == _state.AuthMode) return;
                _state.AuthMode = value;
                if (value == LinkAuthMode.None) ClearPinLocked();
                SaveLocked();
                _events.Post(() => AuthModeChanged?.Invoke(this, value));
                PostChangedLocked();
            }
            _events.Drain();
        }
    }

    /// <summary>hello's <c>pin_source</c> (spec 7.2), sent only when
    /// <see cref="AuthMode"/> is <c>pin</c>. This hub has a screen, so it
    /// always generates a PIN per window and never offers a fixed one.</summary>
    public string PinSource => LinkPinSource.Window;

    // ── Pairing window ──

    /// <summary>The PIN on show, or null when no window is open or it has run out.</summary>
    public string? ActivePin
    {
        get { lock (_lock) return WindowOpenLocked(_time.GetUtcNow()) ? _pin : null; }
    }

    /// <summary>When the open window closes, or null when none is open.</summary>
    public DateTimeOffset? PairingExpires
    {
        get { lock (_lock) return WindowOpenLocked(_time.GetUtcNow()) ? _pinExpiry : null; }
    }

    /// <summary>Opens a two-minute pairing window with a fresh 6-digit PIN,
    /// replacing any PIN already on show, and returns it for the UI.</summary>
    public string BeginPairing()
    {
        var pin = MakePin();
        lock (_lock)
        {
            _pin = pin;
            _pinExpiry = _time.GetUtcNow() + PairingTtl;
            _pinFailures = 0;
            PostChangedLocked();
        }
        _events.Drain();
        return pin;
    }

    public void CancelPairing()
    {
        lock (_lock)
        {
            if (_pin == null) return;
            ClearPinLocked();
            PostChangedLocked();
        }
        _events.Drain();
    }

    // ── Pairing and authentication ──

    /// <summary>
    /// <c>auth.pair</c> in <c>pin</c> mode (spec 6.2): exchanges the window's
    /// PIN for a new client and its token. Success consumes the window (one PIN
    /// pairs one client) and grants the requested role. Every failure counts
    /// against <paramref name="peer"/>; a wrong PIN also counts against the
    /// window, which dies at the fifth whatever addresses they came from.
    /// In <c>none</c> mode the caller doesn't come here: it opens an admin
    /// session whatever the PIN (spec 6.1).
    /// </summary>
    /// <param name="peer">The remote IP, no port.</param>
    public LinkPairResult Pair(string pin, string clientName, LinkRole requestedRole, string peer)
    {
        LinkPairResult result;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (IsBlockedLocked(peer, now)) return LinkPairResult.Fail(LinkAuthFailure.RateLimited);

            if (_pin == null)
            {
                NoteFailureLocked(peer, now);
                return LinkPairResult.Fail(LinkAuthFailure.NoPairingActive);
            }
            if (now >= _pinExpiry)
            {
                // A window that has run out is gone for good, not merely refused.
                ClearPinLocked();
                NoteFailureLocked(peer, now);
                PostChangedLocked();
                result = LinkPairResult.Fail(LinkAuthFailure.Expired);
            }
            else if (!PinMatches(pin, _pin))
            {
                NoteFailureLocked(peer, now);
                if (++_pinFailures >= FailureLimit)
                {
                    ClearPinLocked();
                    PostChangedLocked();
                    LinkLog.Write("hub: pairing window closed after 5 wrong PINs");
                }
                result = LinkPairResult.Fail(LinkAuthFailure.WrongPin);
            }
            else
            {
                ClearPinLocked();
                ClearFailuresLocked(peer);
                var raw = RandomNumberGenerator.GetBytes(TokenBytes);
                var client = new LinkPairedClient(_state.NextCid++, CleanClientName(clientName), requestedRole,
                                                  now, null, HashHex(raw));
                _state.Clients.Add(client);
                SaveLocked();
                PostChangedLocked();
                LinkLog.Write($"hub: paired client {client.Cid} as {client.Role}");
                result = LinkPairResult.Success(client, Base64Url(raw));
            }
        }
        _events.Drain();
        return result;
    }

    /// <summary><c>auth.token</c> (spec 7.3): finds a returning client by its
    /// token and stamps <see cref="LinkPairedClient.LastSeen"/>.</summary>
    /// <param name="peer">The remote IP, no port.</param>
    public LinkAuthResult Authenticate(string token, string peer)
    {
        LinkPairedClient client;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (IsBlockedLocked(peer, now)) return LinkAuthResult.Fail(LinkAuthFailure.RateLimited);

            var raw = DecodeBase64Url(token);
            int index = raw == null ? -1 : FindByHashLocked(SHA256.HashData(raw));
            if (index < 0)
            {
                NoteFailureLocked(peer, now);
                return LinkAuthResult.Fail(LinkAuthFailure.UnknownToken);
            }

            ClearFailuresLocked(peer);
            client = _state.Clients[index] with { LastSeen = now };
            _state.Clients[index] = client;
            SaveLocked();
            PostChangedLocked();
        }
        _events.Drain();
        return LinkAuthResult.Success(client);
    }

    /// <summary>Whether <paramref name="peer"/> is inside a block now. A handler
    /// can check it after a failure to close the socket with 4001 ("failure
    /// limit reached", spec 5) rather than keep answering <c>rate_limited</c>.</summary>
    public bool IsBlocked(string peer)
    {
        lock (_lock) return IsBlockedLocked(peer, _time.GetUtcNow());
    }

    // ── Client management (spec 6.2, 7.3; admin) ──

    /// <summary>Paired clients, in pairing order.</summary>
    public IReadOnlyList<LinkPairedClient> Clients
    {
        get { lock (_lock) return _state.Clients.ToArray(); }
    }

    public LinkPairedClient? Client(int cid)
    {
        lock (_lock) return _state.Clients.Find(c => c.Cid == cid);
    }

    /// <summary>Forgets a client, so its token reads as unknown, and raises
    /// <see cref="ClientRevoked"/>. False when no such client exists.</summary>
    public bool Revoke(int cid)
    {
        lock (_lock)
        {
            if (_state.Clients.RemoveAll(c => c.Cid == cid) == 0) return false;
            SaveLocked();
            _events.Post(() => ClientRevoked?.Invoke(this, cid));
            PostChangedLocked();
            LinkLog.Write($"hub: revoked client {cid}");
        }
        _events.Drain();
        return true;
    }

    /// <summary>Changes a client's role and raises <see cref="ClientRoleChanged"/>
    /// when it differs. False when no such client exists.</summary>
    public bool SetRole(int cid, LinkRole role)
    {
        lock (_lock)
        {
            int index = _state.Clients.FindIndex(c => c.Cid == cid);
            if (index < 0) return false;
            if (_state.Clients[index].Role == role) return true;
            _state.Clients[index] = _state.Clients[index] with { Role = role };
            SaveLocked();
            var change = new LinkClientRoleChange(cid, role);
            _events.Post(() => ClientRoleChanged?.Invoke(this, change));
            PostChangedLocked();
        }
        _events.Drain();
        return true;
    }

    /// <summary>Renames a client in the hub's list (the user's label for it).
    /// False when no such client exists.</summary>
    public bool RenameClient(int cid, string name)
    {
        lock (_lock)
        {
            int index = _state.Clients.FindIndex(c => c.Cid == cid);
            if (index < 0) return false;
            var clean = CleanClientName(name);
            if (_state.Clients[index].Name == clean) return true;
            _state.Clients[index] = _state.Clients[index] with { Name = clean };
            SaveLocked();
            PostChangedLocked();
        }
        _events.Drain();
        return true;
    }

    // ── Rate limiting and the window (lock held) ──

    private bool WindowOpenLocked(DateTimeOffset now) => _pin != null && now < _pinExpiry;

    private bool IsBlockedLocked(string peer, DateTimeOffset now)
    {
        if (!_blockedUntil.TryGetValue(peer, out var until)) return false;
        if (now < until) return true;
        // The block has lapsed: the address starts again from a clean slate.
        ClearFailuresLocked(peer);
        return false;
    }

    private void NoteFailureLocked(string peer, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(peer, out var recent))
        {
            if (_failures.Count >= LedgerSweepThreshold) SweepLedgerLocked(now);
            _failures[peer] = recent = new();
        }
        recent.RemoveAll(t => now - t >= RateWindow);
        recent.Add(now);
        // The block runs 60 s from the fifth failure. That fifth attempt still
        // gets its own error; the sixth is refused outright.
        if (recent.Count >= FailureLimit)
        {
            _blockedUntil[peer] = now + RateWindow;
            LinkLog.Write($"hub: blocking {peer} for {RateWindow.TotalSeconds:0} s after {FailureLimit} failed attempts");
        }
    }

    private void ClearFailuresLocked(string peer)
    {
        _failures.Remove(peer);
        _blockedUntil.Remove(peer);
    }

    /// <summary>Drops addresses with no failure inside the window and no block.</summary>
    private void SweepLedgerLocked(DateTimeOffset now)
    {
        foreach (var peer in _failures.Keys.ToList())
        {
            bool blocked = _blockedUntil.TryGetValue(peer, out var until) && now < until;
            if (!blocked && _failures[peer].TrueForAll(t => now - t >= RateWindow)) ClearFailuresLocked(peer);
        }
        foreach (var peer in _blockedUntil.Where(b => now >= b.Value).Select(b => b.Key).ToList())
            ClearFailuresLocked(peer);
    }

    private void ClearPinLocked()
    {
        _pin = null;
        _pinExpiry = default;
        _pinFailures = 0;
    }

    private void PostChangedLocked() => _events.Post(() => Changed?.Invoke(this, EventArgs.Empty));

    private int FindByHashLocked(byte[] hash)
    {
        // Every stored hash was checked to be 64 hex digits when it was loaded
        // or made. Fixed-time anyway: it costs nothing.
        for (int i = 0; i < _state.Clients.Count; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(_state.Clients[i].TokenHash), hash))
                return i;
        }
        return -1;
    }

    private static bool PinMatches(string candidate, string pin) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate ?? ""), Encoding.ASCII.GetBytes(pin));

    /// <summary>A client name as kept: control characters dropped, ends trimmed,
    /// at most <see cref="MaxClientNameLength"/>; "Unnamed client" if nothing is left.</summary>
    private static string CleanClientName(string? name)
    {
        var kept = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (kept.Length > MaxClientNameLength) kept = kept[..MaxClientNameLength].TrimEnd();
        return kept.Length == 0 ? "Unnamed client" : kept;
    }

    // ── Persistence ──

    /// <summary>snake_case like the wire, so the client entries read
    /// <c>token_hash</c>, <c>last_seen</c>.</summary>
    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Exactly what is on disk. The window and the ledger are absent on
    /// purpose; so is every raw token.</summary>
    private sealed class PersistedState
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("hub_id")] public Guid HubId { get; set; }
        [JsonPropertyName("hub_name")] public string HubName { get; set; } = "";
        [JsonPropertyName("auth_mode")] public LinkAuthMode AuthMode { get; set; }
        [JsonPropertyName("next_cid")] public int NextCid { get; set; }
        [JsonPropertyName("clients")] public List<LinkPairedClient> Clients { get; set; } = new();
    }

    /// <summary>False when the file existed but couldn't be read: it is
    /// left alone rather than replaced by this run's stand-in.</summary>
    private readonly bool _persist = true;

    private void SaveLocked()
    {
        if (!_persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_state, FileJson);
            // Written aside and moved over, so a crash mid-write can't lose the
            // hub id and every pairing.
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The hub keeps working from memory; the next change tries again.
            LinkLog.Write($"hub: could not save the auth store: {e.Message}");
        }
    }

    /// <summary>The stored state, repaired where it can be; null to start fresh.</summary>
    private static PersistedState? Load(string path, out bool unreadable)
    {
        unreadable = false;
        byte[]? bytes = null;
        if (!File.Exists(path)) return null;
        // A scan or a backup can hold the file for a moment.
        for (int attempt = 0; bytes == null; attempt++)
        {
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt < 4)
                {
                    Thread.Sleep(100 * (attempt + 1));
                    continue;
                }
                LinkLog.Write($"hub: could not read the auth store, running without it this time: {e.Message}");
                unreadable = true;
                return null;
            }
        }

        PersistedState? state = null;
        try
        {
            state = JsonSerializer.Deserialize<PersistedState>(bytes, FileJson);
        }
        catch (JsonException) { state = LoadLeniently(bytes); }
        catch (NotSupportedException) { }

        if (state == null || state.HubId == Guid.Empty)
        {
            LinkLog.Write("hub: the auth store is damaged; starting a fresh hub (old file kept as .corrupt)");
            try { File.Copy(path, path + ".corrupt", overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return null;
        }

        if (string.IsNullOrWhiteSpace(state.HubName)) state.HubName = DefaultHubName();
        if (!Enum.IsDefined(state.AuthMode)) state.AuthMode = LinkAuthMode.Pin;
        // A client entry that can't be matched or told apart is dropped rather
        // than sinking the whole store: that client pairs again.
        var seen = new HashSet<int>();
        state.Clients = (state.Clients ?? new())
            .Where(c => c != null && c.Cid > 0 && seen.Add(c.Cid) && IsHashHex(c.TokenHash) && c.Name != null
                        && Enum.IsDefined(c.Role))
            .ToList();
        int floor = state.Clients.Count == 0 ? 1 : state.Clients.Max(c => c.Cid) + 1;
        state.NextCid = Math.Max(state.NextCid, floor);
        state.Version = StoreVersion;
        return state;
    }

    /// <summary>The store read field by field, so one client entry this
    /// build can't read (a role it doesn't know) drops that client instead
    /// of the hub's identity and every other pairing.</summary>
    private static PersistedState? LoadLeniently(byte[] bytes)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(bytes) is not System.Text.Json.Nodes.JsonObject obj) return null;
            if (obj["hub_id"]?.GetValue<string>() is not { } id || !Guid.TryParse(id, out var hid)) return null;
            var state = new PersistedState
            {
                Version = StoreVersion,
                HubId = hid,
                HubName = obj["hub_name"]?.GetValue<string>() ?? "",
                AuthMode = obj["auth_mode"]?.GetValue<string>() == "none" ? LinkAuthMode.None : LinkAuthMode.Pin,
                NextCid = obj["next_cid"]?.GetValue<int>() ?? 1,
                Clients = new(),
            };
            if (obj["clients"] is System.Text.Json.Nodes.JsonArray clients)
            {
                foreach (var c in clients)
                {
                    try
                    {
                        if (c?.Deserialize<LinkPairedClient>(FileJson) is { } client) state.Clients.Add(client);
                    }
                    catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
                }
            }
            LinkLog.Write("hub: the auth store had entries this build can't read; they were dropped");
            return state;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool IsHashHex(string? s) =>
        s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // ── Token and PIN helpers ──

    /// <summary>Lowercase hex SHA-256: the only form of a token this hub keeps.</summary>
    public static string HashHex(ReadOnlySpan<byte> raw) => Convert.ToHexStringLower(SHA256.HashData(raw));

    /// <summary>base64url without padding (RFC 4648 section 5), the token's
    /// encoding (spec 6.2): 32 bytes are 43 characters.</summary>
    public static string Base64Url(ReadOnlySpan<byte> raw) =>
        Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>The inverse of <see cref="Base64Url"/>, tolerating padding a
    /// client might add; null for anything that isn't base64url.</summary>
    public static byte[]? DecodeBase64Url(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 1024) return null;
        var s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 1: return null;
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    /// <summary>A fresh 6-digit PIN, zero padded, from the system CSPRNG.</summary>
    public static string MakePin() => MakePin(() =>
    {
        Span<byte> four = stackalloc byte[4];
        RandomNumberGenerator.Fill(four);
        return BitConverter.ToUInt32(four);
    });

    /// <summary>
    /// The sampler behind <see cref="MakePin()"/>, with the random source
    /// passed in so tests can feed it edge values. Rejection sampling: a 32-bit
    /// value at or above the largest multiple of 1,000,000 is drawn again, so
    /// every PIN is equally likely; a plain modulo would favour the low end.
    /// </summary>
    public static string MakePin(Func<uint> nextUInt32)
    {
        const uint range = 1_000_000;
        const uint limit = uint.MaxValue - uint.MaxValue % range;
        uint value;
        do value = nextUInt32();
        while (value >= limit);
        return (value % range).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}

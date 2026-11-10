using System.Text.Json.Nodes;
using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>
/// The hub's authentication store (spec 6, 7.3). Port of the macOS Console's
/// LinkAuthStoreTests plus the Windows additions: Open by default (spec 1.2
/// section 6.1), an injected clock instead of explicit timestamps, and the
/// events the hub acts on. Each test gets its own temp directory.
/// </summary>
public sealed class LinkAuthStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    public LinkAuthStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LinkAuthStoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "link-auth.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private LinkAuthStore MakeStore() => new(_path, _clock);

    private static LinkPairResult PairSucceeding(LinkAuthStore store, string name = "Troy's iPhone",
                                                 LinkRole role = LinkRole.Control, string peer = "192.168.1.10")
    {
        var pin = store.BeginPairing();
        var result = store.Pair(pin, name, role, peer);
        Assert.True(result.Succeeded, $"expected pairing to succeed, got {result.Failure}");
        return result;
    }

    private static string WrongPin(string pin) => pin == "000000" ? "111111" : "000000";

    private static string Bogus(byte fill) => LinkAuthStore.Base64Url(Enumerable.Repeat(fill, 32).ToArray());

    // ── Identity, defaults and persistence ──

    [Fact]
    public void HubIdentityPersistsAcrossInstances()
    {
        var first = MakeStore();
        var second = MakeStore();
        Assert.NotEqual(Guid.Empty, first.HubId);
        Assert.Equal(first.HubId, second.HubId);
        Assert.Equal(first.HubName, second.HubName);
    }

    /// <summary>Spec 1.2 section 6.1: hubs default to none. The Mac (pre-1.2)
    /// defaults to pin; this is the deliberate difference.</summary>
    [Fact]
    public void DefaultsAreOpenModeAndTheMachineName()
    {
        var store = MakeStore();
        Assert.Equal(LinkAuthMode.None, store.AuthMode);
        Assert.Equal($"{Environment.MachineName} DSPi Console", store.HubName);
        Assert.Equal(LinkPinSource.Window, store.PinSource);
        Assert.Null(store.ActivePin);
        Assert.Null(store.PairingExpires);
        Assert.Empty(store.Clients);
        Assert.True(File.Exists(_path), "a new hub saves its id at once");
    }

    [Fact]
    public void HubNameAndModePersistAndRaiseEvents()
    {
        var store = MakeStore();
        var names = new List<string>();
        var modes = new List<LinkAuthMode>();
        int changed = 0;
        store.HubNameChanged += (_, n) => names.Add(n);
        store.AuthModeChanged += (_, m) => modes.Add(m);
        store.Changed += (_, _) => changed++;

        store.HubName = "  Studio PC  ";
        store.HubName = "Studio PC";          // unchanged: no event
        store.AuthMode = LinkAuthMode.Pin;
        store.AuthMode = LinkAuthMode.Pin;    // unchanged: no event

        Assert.Equal(new[] { "Studio PC" }, names);
        Assert.Equal(new[] { LinkAuthMode.Pin }, modes);
        Assert.Equal(2, changed);

        var reloaded = MakeStore();
        Assert.Equal("Studio PC", reloaded.HubName);
        Assert.Equal(LinkAuthMode.Pin, reloaded.AuthMode);
    }

    [Fact]
    public void BlankHubNameRestoresTheDefault()
    {
        var store = MakeStore();
        store.HubName = "Studio PC";
        store.HubName = "   ";
        Assert.Equal(LinkAuthStore.DefaultHubName(), store.HubName);
    }

    /// <summary>A damaged file must not take the hub down: a fresh, working
    /// store (with a new hid) replaces it, and the bad file is kept aside.</summary>
    [Fact]
    public void CorruptStoreFileYieldsAFreshStore()
    {
        var original = MakeStore().HubId;
        File.WriteAllText(_path, "{ this is not json");

        var store = MakeStore();
        Assert.NotEqual(original, store.HubId);
        Assert.Empty(store.Clients);
        Assert.Equal(LinkAuthMode.None, store.AuthMode);
        Assert.True(File.Exists(_path + ".corrupt"));
        PairSucceeding(store);
        Assert.Single(store.Clients);
        Assert.Equal(store.HubId, MakeStore().HubId);
    }

    [Fact]
    public void MissingDirectoryIsCreated()
    {
        var nested = Path.Combine(_dir, "a", "b", "link-auth.json");
        var store = new LinkAuthStore(nested, _clock);
        Assert.True(File.Exists(nested));
        Assert.Equal(store.HubId, new LinkAuthStore(nested, _clock).HubId);
    }

    /// <summary>One damaged client entry costs that client its pairing, not
    /// the whole store its hub id.</summary>
    [Fact]
    public void DamagedClientEntryIsDroppedAndTheRestKept()
    {
        var store = MakeStore();
        var a = PairSucceeding(store, name: "A");
        var b = PairSucceeding(store, name: "B");

        var root = JsonNode.Parse(File.ReadAllText(_path))!;
        root["clients"]![0]!["token_hash"] = "not a hash";
        File.WriteAllText(_path, root.ToJsonString());

        var reloaded = MakeStore();
        Assert.Equal(store.HubId, reloaded.HubId);
        Assert.Equal(new[] { b.Client!.Cid }, reloaded.Clients.Select(c => c.Cid));
        Assert.False(reloaded.Authenticate(a.Token!, "10.0.0.1").Succeeded);
        Assert.True(reloaded.Authenticate(b.Token!, "10.0.0.1").Succeeded);
        // The next pairing still gets a fresh cid.
        Assert.True(PairSucceeding(reloaded).Client!.Cid > b.Client.Cid);
    }

    // ── Pairing window ──

    [Fact]
    public void PairingSucceedsAndTokenAuthenticates()
    {
        var store = MakeStore();
        int changed = 0;
        store.Changed += (_, _) => changed++;
        var pin = store.BeginPairing();
        Assert.Equal(pin, store.ActivePin);
        Assert.Equal(_clock.Now + TimeSpan.FromSeconds(120), store.PairingExpires);

        var result = store.Pair(pin, "Troy's iPhone", LinkRole.Control, "192.168.1.10");
        Assert.True(result.Succeeded);
        Assert.Equal("Troy's iPhone", result.Client.Name);
        Assert.Equal(LinkRole.Control, result.Client.Role);
        Assert.Equal(_clock.Now, result.Client.Created);
        Assert.Single(store.Clients);
        // Success consumes the window: one PIN, one client.
        Assert.Null(store.ActivePin);
        Assert.Equal(2, changed); // opened, then paired

        var auth = store.Authenticate(result.Token, "192.168.1.10");
        Assert.True(auth.Succeeded);
        Assert.Equal(result.Client.Cid, auth.Client.Cid);
        Assert.Equal(LinkRole.Control, auth.Client.Role);
    }

    [Fact]
    public void WrongPinFailsAndTheWindowStaysOpen()
    {
        var store = MakeStore();
        var pin = store.BeginPairing();
        var result = store.Pair(WrongPin(pin), "Imposter", LinkRole.Admin, "10.0.0.9");
        Assert.Equal(LinkAuthFailure.WrongPin, result.Failure);
        Assert.Empty(store.Clients);
        Assert.Equal(pin, store.ActivePin);
    }

    [Fact]
    public void PairWithoutAWindowFails()
    {
        var store = MakeStore();
        var result = store.Pair("123456", "Nobody", LinkRole.Viewer, "10.0.0.9");
        Assert.Equal(LinkAuthFailure.NoPairingActive, result.Failure);
    }

    [Fact]
    public void CancelPairingClosesTheWindow()
    {
        var store = MakeStore();
        var pin = store.BeginPairing();
        int changed = 0;
        store.Changed += (_, _) => changed++;
        store.CancelPairing();
        store.CancelPairing(); // nothing open: no event
        Assert.Equal(1, changed);
        Assert.Null(store.ActivePin);
        Assert.Null(store.PairingExpires);
        Assert.Equal(LinkAuthFailure.NoPairingActive, store.Pair(pin, "Late", LinkRole.Viewer, "10.0.0.9").Failure);
    }

    /// <summary>Spec 6.2: a window PIN is invalidated after any 5 failures.
    /// Five addresses, so the per-address limit can't fire first; the sixth
    /// attempt has the right PIN and finds no window.</summary>
    [Fact]
    public void FiveWrongPinsKillTheWindow()
    {
        var store = MakeStore();
        var pin = store.BeginPairing();
        int changed = 0;
        store.Changed += (_, _) => changed++;
        for (int i = 1; i <= 5; i++)
            Assert.Equal(LinkAuthFailure.WrongPin, store.Pair(WrongPin(pin), "Imposter", LinkRole.Viewer, $"10.0.0.{i}").Failure);

        Assert.Null(store.ActivePin);
        Assert.Equal(1, changed);
        Assert.Equal(LinkAuthFailure.NoPairingActive, store.Pair(pin, "Troy", LinkRole.Control, "10.0.0.6").Failure);
        Assert.Empty(store.Clients);
    }

    [Fact]
    public void FourWrongPinsLeaveTheWindowOpen()
    {
        var store = MakeStore();
        var pin = store.BeginPairing();
        for (int i = 1; i <= 4; i++) store.Pair(WrongPin(pin), "Imposter", LinkRole.Viewer, $"10.0.0.{i}");
        Assert.True(store.Pair(pin, "Troy", LinkRole.Control, "10.0.0.9").Succeeded);
    }

    [Fact]
    public void NewWindowResetsTheWrongPinCount()
    {
        var store = MakeStore();
        var first = store.BeginPairing();
        for (int i = 1; i <= 4; i++) store.Pair(WrongPin(first), "Imposter", LinkRole.Viewer, $"10.0.0.{i}");
        var pin = store.BeginPairing();
        store.Pair(WrongPin(pin), "Imposter", LinkRole.Viewer, "10.0.0.5");
        Assert.Equal(pin, store.ActivePin);
    }

    [Fact]
    public void WindowExpiresAfterTwoMinutes()
    {
        var store = MakeStore();
        var pin = store.BeginPairing();

        _clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(pin, store.ActivePin);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(store.ActivePin);
        Assert.Null(store.PairingExpires);
        Assert.Equal(LinkAuthFailure.Expired, store.Pair(pin, "Troy", LinkRole.Control, "10.0.0.9").Failure);
        // An expired window is cleared, not merely refused.
        Assert.Equal(LinkAuthFailure.NoPairingActive, store.Pair(pin, "Troy", LinkRole.Control, "10.0.0.9").Failure);
        Assert.Empty(store.Clients);
    }

    [Fact]
    public void PairingJustInsideTheWindowSucceeds()
    {
        var store = MakeStore();
        var pin = store.BeginPairing();
        _clock.Advance(TimeSpan.FromSeconds(119.9));
        Assert.True(store.Pair(pin, "Troy", LinkRole.Control, "10.0.0.9").Succeeded);
    }

    [Fact]
    public void PinsAreSixDigitsAndUniform()
    {
        const int count = 20000;
        var perPosition = new int[6, 10];
        for (int n = 0; n < count; n++)
        {
            var pin = LinkAuthStore.MakePin();
            Assert.Equal(6, pin.Length);
            for (int i = 0; i < 6; i++)
            {
                Assert.InRange(pin[i], '0', '9');
                perPosition[i, pin[i] - '0']++;
            }
        }
        // Expected 2000 per digit, sd about 42: a band of 400 is ~9.5 sd, so
        // this fails only for a real bias, never by chance.
        for (int i = 0; i < 6; i++)
            for (int d = 0; d < 10; d++)
                Assert.InRange(perPosition[i, d], 1600, 2400);
    }

    /// <summary>Values at or above the largest multiple of 1,000,000 under
    /// 2^32 are drawn again, which is what keeps the PINs uniform.</summary>
    [Fact]
    public void PinSamplerRejectsTheBiasedTail()
    {
        const uint limit = 4_294_000_000;
        var draws = new Queue<uint>(new[] { uint.MaxValue, limit, limit - 1 });
        Assert.Equal("999999", LinkAuthStore.MakePin(() => draws.Dequeue()));
        Assert.Empty(draws);

        Assert.Equal("000000", LinkAuthStore.MakePin(() => 0));
        Assert.Equal("000123", LinkAuthStore.MakePin(() => 7_000_123));
    }

    // ── Token shape and storage ──

    [Fact]
    public void TokenIsBase64UrlWithoutPadding()
    {
        var token = PairSucceeding(MakeStore()).Token!;
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.Equal(43, token.Length); // 32 bytes, unpadded
        Assert.Equal(32, LinkAuthStore.DecodeBase64Url(token)!.Length);

        // Bytes that standard base64 writes as "+/+/".
        var awkward = new byte[] { 0xFB, 0xFF, 0xBF };
        Assert.Equal("+/+/", Convert.ToBase64String(awkward));
        Assert.Equal("-_-_", LinkAuthStore.Base64Url(awkward));
        Assert.Equal(awkward, LinkAuthStore.DecodeBase64Url("-_-_"));
        // Padding a client adds is tolerated.
        Assert.Equal(new byte[] { 0xFB }, LinkAuthStore.DecodeBase64Url("-w=="));
        Assert.Equal(new byte[] { 0xFB }, LinkAuthStore.DecodeBase64Url("-w"));
    }

    [Fact]
    public void TokensAreDistinct()
    {
        var store = MakeStore();
        var a = PairSucceeding(store, name: "A").Token;
        var b = PairSucceeding(store, name: "B").Token;
        Assert.NotEqual(a, b);
    }

    /// <summary>Spec 6.2: a hub stores only a SHA-256 of the token.</summary>
    [Fact]
    public void StoreFileHoldsOnlyTheTokenHash()
    {
        var store = MakeStore();
        var pairing = PairSucceeding(store);
        Assert.True(pairing.Succeeded);
        var raw = LinkAuthStore.DecodeBase64Url(pairing.Token)!;

        var contents = File.ReadAllText(_path);
        Assert.DoesNotContain(pairing.Token, contents);
        Assert.DoesNotContain(Convert.ToBase64String(raw), contents);
        Assert.DoesNotContain(Convert.ToHexString(raw), contents, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(pairing.Client.TokenHash, contents);
        Assert.Matches("^[0-9a-f]{64}$", pairing.Client.TokenHash);
        Assert.Equal(LinkAuthStore.HashHex(raw), pairing.Client.TokenHash);
        // Nor does the transient state reach the file.
        var pin = store.BeginPairing();
        Assert.DoesNotContain(pin, File.ReadAllText(_path));
    }

    [Fact]
    public void TokenSurvivesARestart()
    {
        var pairing = PairSucceeding(MakeStore());
        var auth = MakeStore().Authenticate(pairing.Token!, "192.168.1.10");
        Assert.True(auth.Succeeded);
        Assert.Equal(pairing.Client!.Cid, auth.Client.Cid);
    }

    [Fact]
    public void PairingWindowDoesNotSurviveARestart()
    {
        MakeStore().BeginPairing();
        Assert.Null(MakeStore().ActivePin);
    }

    // ── Authentication ──

    [Fact]
    public void AuthenticateStampsLastSeenAndPersistsIt()
    {
        var store = MakeStore();
        var pairing = PairSucceeding(store);
        Assert.Null(pairing.Client!.LastSeen);
        int changed = 0;
        store.Changed += (_, _) => changed++;

        _clock.Advance(TimeSpan.FromMinutes(5));
        store.Authenticate(pairing.Token!, "192.168.1.10");
        Assert.Equal(_clock.Now, store.Clients[0].LastSeen);

        _clock.Advance(TimeSpan.FromHours(1));
        store.Authenticate(pairing.Token!, "192.168.1.10");
        Assert.Equal(_clock.Now, store.Clients[0].LastSeen);
        Assert.Equal(2, changed);

        Assert.Equal(_clock.Now, MakeStore().Clients[0].LastSeen);
    }

    [Fact]
    public void UnknownAndMalformedTokensFail()
    {
        var store = MakeStore();
        Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate("not-a-real-token", "10.0.0.9").Failure);
        Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate("!!!!", "10.0.0.8").Failure);
        Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate("", "10.0.0.7").Failure);
        Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate("a", "10.0.0.6").Failure);
        Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate(Bogus(0x42), "10.0.0.5").Failure);
    }

    [Fact]
    public void FailuresMapToSpecErrorCodes()
    {
        Assert.Equal(LinkErrorCode.RateLimited, LinkAuthFailure.RateLimited.ToErrorCode());
        foreach (var f in new[] { LinkAuthFailure.NoPairingActive, LinkAuthFailure.WrongPin,
                                  LinkAuthFailure.Expired, LinkAuthFailure.UnknownToken })
            Assert.Equal(LinkErrorCode.Unauthenticated, f.ToErrorCode());
    }

    // ── Client management ──

    /// <summary>Revoking forgets the client, so its token reads as unknown (no
    /// tombstone), and the hub is told so it can close live sessions with 4002.</summary>
    [Fact]
    public void RevokeInvalidatesTheTokenAndRaisesTheEvent()
    {
        var store = MakeStore();
        var pairing = PairSucceeding(store);
        var revoked = new List<int>();
        store.ClientRevoked += (_, cid) => revoked.Add(cid);

        Assert.True(store.Revoke(pairing.Client!.Cid));
        Assert.False(store.Revoke(pairing.Client.Cid));   // already gone: no second event
        Assert.False(store.Revoke(999));

        Assert.Equal(new[] { pairing.Client.Cid }, revoked);
        Assert.Empty(store.Clients);
        Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate(pairing.Token!, "192.168.1.10").Failure);
        Assert.Empty(MakeStore().Clients);
    }

    [Fact]
    public void SetRoleRaisesTheEventAndPersists()
    {
        var store = MakeStore();
        var pairing = PairSucceeding(store, name: "iPhone", role: LinkRole.Viewer);
        int cid = pairing.Client!.Cid;
        var changes = new List<LinkClientRoleChange>();
        store.ClientRoleChanged += (_, c) => changes.Add(c);

        Assert.True(store.SetRole(cid, LinkRole.Admin));
        Assert.True(store.SetRole(cid, LinkRole.Admin));  // same role: no event
        Assert.False(store.SetRole(999, LinkRole.Admin));
        Assert.Equal(new[] { new LinkClientRoleChange(cid, LinkRole.Admin) }, changes);

        Assert.True(store.RenameClient(cid, "Troy's iPhone"));
        Assert.False(store.RenameClient(999, "x"));

        var reloaded = MakeStore();
        Assert.Equal(LinkRole.Admin, reloaded.Client(cid)!.Role);
        Assert.Equal("Troy's iPhone", reloaded.Client(cid)!.Name);
        // A later authentication reports the new role.
        Assert.Equal(LinkRole.Admin, reloaded.Authenticate(pairing.Token!, "192.168.1.10").Client!.Role);
    }

    /// <summary>Role-change events arrive in the order the changes happened,
    /// even when several threads change roles at once, so the last one the hub
    /// applies to live sessions is the role the store ends on.</summary>
    [Fact]
    public void RoleEventsArriveInChangeOrderAcrossThreads()
    {
        var store = MakeStore();
        int cid = PairSucceeding(store).Client!.Cid;
        LinkRole? last = null;
        store.ClientRoleChanged += (_, c) => last = c.Role;

        Parallel.For(0, 2000, i => store.SetRole(cid, (LinkRole)(i % 3)));

        Assert.Equal(store.Client(cid)!.Role, last);
    }

    /// <summary>cids are never reused, across revokes and restarts.</summary>
    [Fact]
    public void ClientIdsAreMonotonic()
    {
        var store = MakeStore();
        var first = PairSucceeding(store, name: "A").Client!;
        store.Revoke(first.Cid);
        var second = PairSucceeding(MakeStore(), name: "B").Client!;
        Assert.True(second.Cid > first.Cid);
    }

    [Fact]
    public void ClientNamesAreCleaned()
    {
        var store = MakeStore();
        Assert.Equal("Bad Name", PairSucceeding(store, name: "  Bad\u0007 Name\n ").Client!.Name);
        Assert.Equal("Unnamed client", PairSucceeding(store, name: "   ").Client!.Name);
        Assert.Equal(LinkAuthStore.MaxClientNameLength, PairSucceeding(store, name: new string('x', 500)).Client!.Name.Length);
    }

    [Fact]
    public void WireEntryCarriesTheClientFields()
    {
        var store = MakeStore();
        var client = PairSucceeding(store).Client!;
        var wire = client.ToWire(online: true);
        Assert.Equal(new LinkAuthClient(client.Cid, client.Name, client.Role, client.Created, null, true), wire);
    }

    [Fact]
    public void SwitchingToOpenClosesThePairingWindowButKeepsClients()
    {
        var store = MakeStore();
        store.AuthMode = LinkAuthMode.Pin;
        var pairing = PairSucceeding(store);
        store.BeginPairing();

        store.AuthMode = LinkAuthMode.None;
        Assert.Null(store.ActivePin);
        Assert.Single(store.Clients);
        store.AuthMode = LinkAuthMode.Pin;
        Assert.True(store.Authenticate(pairing.Token!, "10.0.0.1").Succeeded);
    }

    // ── Rate limiting (spec 6.2) ──

    /// <summary>Five failures from one address within a minute block it for
    /// 60 s from the fifth. The fifth still gets its own error.</summary>
    [Fact]
    public void FifthFailureBlocksTheAddressForAMinute()
    {
        var store = MakeStore();
        var bogus = Bogus(0xAB);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate(bogus, "10.1.1.1").Failure);
            Assert.Equal(i == 4, store.IsBlocked("10.1.1.1"));
            if (i < 4) _clock.Advance(TimeSpan.FromSeconds(1));
        }
        var fifth = _clock.Now;

        Assert.Equal(LinkAuthFailure.RateLimited, store.Authenticate(bogus, "10.1.1.1").Failure);
        var pin = store.BeginPairing();
        Assert.Equal(LinkAuthFailure.RateLimited, store.Pair(pin, "Troy", LinkRole.Control, "10.1.1.1").Failure);

        _clock.Now = fifth + TimeSpan.FromSeconds(59);
        Assert.Equal(LinkAuthFailure.RateLimited, store.Authenticate(bogus, "10.1.1.1").Failure);

        _clock.Now = fifth + TimeSpan.FromSeconds(60);
        Assert.False(store.IsBlocked("10.1.1.1"));
        Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate(bogus, "10.1.1.1").Failure);
    }

    /// <summary>Failed pairs count the same as failed tokens.</summary>
    [Fact]
    public void FailedPairsBlockTheAddressToo()
    {
        var store = MakeStore();
        for (int i = 0; i < 5; i++)
            Assert.Equal(LinkAuthFailure.NoPairingActive, store.Pair("123456", "x", LinkRole.Viewer, "10.1.1.1").Failure);
        Assert.Equal(LinkAuthFailure.RateLimited, store.Pair("123456", "x", LinkRole.Viewer, "10.1.1.1").Failure);
    }

    [Fact]
    public void RateLimitIsPerAddress()
    {
        var store = MakeStore();
        var bogus = Bogus(0xCD);
        for (int i = 0; i < 5; i++) store.Authenticate(bogus, "10.1.1.1");
        Assert.Equal(LinkAuthFailure.RateLimited, store.Authenticate(bogus, "10.1.1.1").Failure);
        Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate(bogus, "10.1.1.2").Failure);
    }

    /// <summary>A blocked address's attempts aren't judged, so they can't use
    /// up another client's pairing window either.</summary>
    [Fact]
    public void BlockedAttemptsDoNotCountAgainstTheWindow()
    {
        var store = MakeStore();
        for (int i = 0; i < 5; i++) store.Authenticate(Bogus(0x01), "10.1.1.1");
        var pin = store.BeginPairing();
        for (int i = 0; i < 10; i++)
            Assert.Equal(LinkAuthFailure.RateLimited, store.Pair(WrongPin(pin), "x", LinkRole.Viewer, "10.1.1.1").Failure);
        Assert.Equal(pin, store.ActivePin);
        Assert.True(store.Pair(pin, "Troy", LinkRole.Control, "10.1.1.2").Succeeded);
    }

    /// <summary>Failures older than a minute fall out of the count.</summary>
    [Fact]
    public void FailuresOutsideTheWindowDoNotAccumulate()
    {
        var store = MakeStore();
        var bogus = Bogus(0xEF);
        for (int i = 0; i < 4; i++)
        {
            store.Authenticate(bogus, "10.2.2.2");
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        _clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate(bogus, "10.2.2.2").Failure);
        Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate(bogus, "10.2.2.2").Failure);
        Assert.False(store.IsBlocked("10.2.2.2"));
    }

    [Fact]
    public void SuccessResetsTheFailureCount()
    {
        var store = MakeStore();
        var pairing = PairSucceeding(store, peer: "10.3.3.3");
        var bogus = Bogus(0x11);
        for (int i = 0; i < 4; i++) store.Authenticate(bogus, "10.3.3.3");
        Assert.True(store.Authenticate(pairing.Token!, "10.3.3.3").Succeeded);
        for (int i = 0; i < 4; i++)
            Assert.Equal(LinkAuthFailure.UnknownToken, store.Authenticate(bogus, "10.3.3.3").Failure);
    }

    [Fact]
    public void ManyAddressesDoNotBreakTheLedger()
    {
        var store = MakeStore();
        var bogus = Bogus(0x22);
        for (int i = 0; i < 1000; i++) store.Authenticate(bogus, $"10.9.{i / 256}.{i % 256}");
        // Every address has one recent failure; none is blocked yet.
        Assert.False(store.IsBlocked("10.9.0.0"));
        _clock.Advance(TimeSpan.FromMinutes(2));
        for (int i = 0; i < 5; i++) store.Authenticate(bogus, "10.8.0.1");
        Assert.True(store.IsBlocked("10.8.0.1"));
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public void Advance(TimeSpan by) => Now += by;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

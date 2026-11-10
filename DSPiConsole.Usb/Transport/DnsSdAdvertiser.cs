using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DSPiConsole.Core.Link;

namespace DSPiConsole.Usb.Transport;

/// <summary>Advertising this hub over DNS-SD (spec 3.1); faked in tests.</summary>
public interface IDnsSdAdvertiser : IDisposable
{
    /// <summary>True once the current registration has been accepted.</summary>
    bool IsAdvertising { get; }

    /// <summary>Advertise <c>name._dspi._tcp.local</c> on <paramref name="port"/>,
    /// replacing any earlier registration.</summary>
    void Start(string name, int port, LinkTxtRecord txt);

    /// <summary>New name or TXT (a rename, an access-mode change, a device
    /// list change) on the same port. Ignored when not started.</summary>
    void Update(string name, LinkTxtRecord txt);

    void Stop();
}

/// <summary>What gets registered: the instance label and the TXT pairs.
/// Pure, so the rules are testable without the network.</summary>
public static class DnsSdAdvertisement
{
    /// <summary>A DNS label holds at most 63 bytes (RFC 1035 2.3.4; RFC 6763 4.1.1).</summary>
    public const int MaxInstanceBytes = 63;

    public const string FallbackName = "DSPi Console";

    /// <summary>
    /// The hub name as a DNS-SD instance label: trimmed, control characters
    /// and dots replaced (Windows' API takes the instance as the first label
    /// of a dotted name, with no escaping), and cut to 63 UTF-8 bytes on a
    /// character boundary.
    /// </summary>
    public static string InstanceLabel(string? hubName)
    {
        var sb = new StringBuilder();
        foreach (var rune in (hubName ?? "").Trim().EnumerateRunes())
        {
            var r = Rune.IsControl(rune) ? new Rune(' ') : rune.Value == '.' ? new Rune('․') : rune;
            if (Encoding.UTF8.GetByteCount(sb.ToString() + r.ToString()) > MaxInstanceBytes) break;
            sb.Append(r.ToString());
        }
        var label = sb.ToString().Trim();
        return label.Length == 0 ? FallbackName : label;
    }

    /// <summary>The full service instance name Windows registers.</summary>
    public static string ServiceName(string? hubName) =>
        InstanceLabel(hubName) + "." + LinkService.ServiceType + ".local";

    /// <summary>The TXT pairs in registration order (spec 3.1). <c>d</c> is
    /// already dropped by <see cref="LinkTxtRecord.ToDictionary"/> when it
    /// would pass 400 bytes or one string's 255.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> TxtPairs(LinkTxtRecord txt) =>
        txt.ToDictionary().ToList();

    /// <summary>The TXT record's wire size: a length byte plus "key=value"
    /// per entry, the measure spec 3.1's 400-byte limit applies to.</summary>
    public static int TxtWireSize(IEnumerable<KeyValuePair<string, string>> pairs) =>
        pairs.Sum(p => 1 + Encoding.UTF8.GetByteCount(p.Key) + 1 + Encoding.UTF8.GetByteCount(p.Value));
}

/// <summary>
/// Windows' own DNS-SD registration (dnsapi.dll, Windows 10 1809+; decision
/// N3): <c>DnsServiceRegister</c> / <c>DnsServiceDeRegister</c>. Windows'
/// responder does the multicast, so nothing here touches UDP 5353 and no
/// firewall prompt appears (L0 spike a). WinRT's DnssdServiceInstance was
/// ruled out because it insists on owning the listening socket.
/// <para>There is no call to change a registration's TXT, so an update is a
/// deregister then a register; clients' prune grace covers the gap. Calls are
/// queued and run in order on a pool thread, each change waiting for the
/// previous deregistration to complete so the same name never collides with
/// itself.</para>
/// </summary>
public sealed class WindowsDnsSdAdvertiser : IDnsSdAdvertiser
{
    private readonly object _lock = new();
    private Task _tail = Task.CompletedTask;
    private Registration? _current;   // touched only on the queue
    private int _port;
    private volatile bool _advertising;

    /// <summary>A registration finished: the Win32 status (0 is success) and
    /// the instance name. Raised on a pool thread, never dnsapi's callback
    /// thread, so a handler may call back into this class.</summary>
    public event EventHandler<(string Instance, int Status)>? RegistrationCompleted;

    public bool IsAdvertising => _advertising;

    /// <summary>How long a change waits for the old deregistration's callback.</summary>
    private static readonly TimeSpan DeregisterWait = TimeSpan.FromSeconds(2);

    public void Start(string name, int port, LinkTxtRecord txt) => Post(() =>
    {
        Withdraw();
        _port = port;
        _current = Register(name, port, txt);
    });

    /// <summary>Re-registers with the new name and TXT. Registers afresh after
    /// a Start whose registration failed, so one refusal doesn't hide the hub
    /// until sharing is switched off and on.</summary>
    public void Update(string name, LinkTxtRecord txt) => Post(() =>
    {
        if (_port == 0) return;   // not started, or stopped
        Withdraw();
        _current = Register(name, _port, txt);
    });

    public void Stop() => Post(() =>
    {
        Withdraw();
        _port = 0;
    });

    /// <summary>Withdraws the advertisement and waits briefly for it, so the
    /// goodbye goes out before the process exits.</summary>
    public void Dispose()
    {
        Stop();
        Task tail;
        lock (_lock) tail = _tail;
        tail.Wait(DeregisterWait + TimeSpan.FromSeconds(1));
    }

    /// <summary>A task that completes when every queued change has run (tests).</summary>
    public Task Settled { get { lock (_lock) return _tail; } }

    private void Post(Action work)
    {
        lock (_lock)
        {
            _tail = _tail.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception e) { LinkLog.Write($"DNS-SD advertise: {e.Message}"); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Withdraw()
    {
        var reg = _current;
        _current = null;
        _advertising = false;
        if (reg == null) return;
        reg.Deregister();
        reg.Withdrawn.Wait(DeregisterWait);
    }

    private Registration? Register(string name, int port, LinkTxtRecord txt)
    {
        var reg = Registration.Create(DnsSdAdvertisement.ServiceName(name), HostName(), port,
            DnsSdAdvertisement.TxtPairs(txt), OnRegistered);
        if (reg == null) LinkLog.Write($"DNS-SD advertise: couldn't register {name}");
        return reg;
    }

    private void OnRegistered(Registration reg, int status)
    {
        if (ReferenceEquals(reg, _current)) _advertising = status == 0;
        LinkLog.Write(status == 0
            ? $"DNS-SD advertise: registered {reg.ServiceName}"
            : $"DNS-SD advertise: registering {reg.ServiceName} failed ({status})");
        if (RegistrationCompleted is { } handler)
            ThreadPool.QueueUserWorkItem(_ => handler(this, (reg.ServiceName, status)));
    }

    /// <summary>The host the SRV record names: this machine's mDNS name.</summary>
    private static string HostName()
    {
        string host;
        try { host = Dns.GetHostName(); }
        catch (SocketException) { host = Environment.MachineName; }
        return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? host : host + ".local";
    }

    /// <summary>
    /// One registration and everything the native side may read until it is
    /// done with it: the instance, the request, the strings and the callback
    /// delegate. Freed only after every call the native side accepted has
    /// called back; if a deregistration never completes, this leaks a few
    /// hundred bytes rather than risking a use-after-free.
    /// </summary>
    private sealed class Registration
    {
        /// <summary>Registrations the native side may still call back.</summary>
        private static readonly HashSet<Registration> Live = new();

        private readonly object _lock = new();
        private readonly List<IntPtr> _strings = new();
        private IntPtr _keys, _values, _instance, _request, _cancel;
        private RegisterCallback _callback = null!;
        private Action<Registration, int> _onRegistered = null!;
        private int _pending;
        private bool _deregistering;
        public string ServiceName { get; private init; } = "";
        public ManualResetEventSlim Withdrawn { get; } = new(false);

        public static Registration? Create(string serviceName, string host, int port,
            IReadOnlyList<KeyValuePair<string, string>> txt, Action<Registration, int> onRegistered)
        {
            var reg = new Registration { ServiceName = serviceName, _onRegistered = onRegistered };
            reg._callback = reg.OnComplete;
            try
            {
                var name = reg.Str(serviceName);
                var hostPtr = reg.Str(host);
                reg._keys = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, txt.Count));
                reg._values = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, txt.Count));
                for (int i = 0; i < txt.Count; i++)
                {
                    Marshal.WriteIntPtr(reg._keys, i * IntPtr.Size, reg.Str(txt[i].Key));
                    Marshal.WriteIntPtr(reg._values, i * IntPtr.Size, reg.Str(txt[i].Value));
                }
                reg._instance = DnsServiceConstructInstance(name, hostPtr, IntPtr.Zero, IntPtr.Zero,
                    (ushort)port, 0, 0, (uint)txt.Count, reg._keys, reg._values);
                if (reg._instance == IntPtr.Zero)
                {
                    reg.Free();
                    return null;
                }
                reg._request = Marshal.AllocHGlobal(Marshal.SizeOf<DNS_SERVICE_REGISTER_REQUEST>());
                Marshal.StructureToPtr(new DNS_SERVICE_REGISTER_REQUEST
                {
                    Version = DNS_QUERY_REQUEST_VERSION1,
                    InterfaceIndex = 0, // every interface
                    pServiceInstance = reg._instance,
                    pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(reg._callback),
                }, reg._request, false);
                reg._cancel = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(reg._cancel, IntPtr.Zero);

                lock (Live) Live.Add(reg);
                lock (reg._lock) reg._pending++;
                int status = DnsServiceRegister(reg._request, reg._cancel);
                if (status != DNS_REQUEST_PENDING)
                {
                    LinkLog.Write($"DNS-SD advertise: DnsServiceRegister returned {status}");
                    lock (reg._lock) reg._pending--;
                    lock (Live) Live.Remove(reg);
                    reg.Free();
                    return null;
                }
                return reg;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // Older than Windows 10 1809.
                LinkLog.Write($"DNS-SD advertise: unavailable ({e.Message})");
                reg.Free();
                return null;
            }
        }

        private IntPtr Str(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            _strings.Add(p);
            return p;
        }

        public void Deregister()
        {
            lock (_lock)
            {
                if (_deregistering) return;
                _deregistering = true;
                _pending++;
            }
            int status;
            try { status = DnsServiceDeRegister(_request, IntPtr.Zero); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { status = -1; }
            if (status == DNS_REQUEST_PENDING) return;
            LinkLog.Write($"DNS-SD advertise: DnsServiceDeRegister returned {status}");
            lock (_lock) _pending--;
            // The native side may still hold the registration: keep its
            // memory (leak) but stop waiting for it.
            Withdrawn.Set();
        }

        private void OnComplete(uint status, IntPtr context, IntPtr instance)
        {
            try
            {
                // The callback's copy is ours to free; the instance we built
                // is freed with the rest of this registration.
                if (instance != IntPtr.Zero && instance != _instance) DnsServiceFreeInstance(instance);
                bool deregistering, done;
                lock (_lock)
                {
                    _pending--;
                    deregistering = _deregistering;
                    done = _deregistering && _pending == 0;
                }
                if (!deregistering) _onRegistered(this, (int)status);
                if (done)
                {
                    lock (Live) Live.Remove(this);
                    Free();
                    Withdrawn.Set();
                }
            }
            catch { /* never throw into the native caller */ }
        }

        private void Free()
        {
            if (_instance != IntPtr.Zero) { DnsServiceFreeInstance(_instance); _instance = IntPtr.Zero; }
            foreach (var p in _strings) Marshal.FreeHGlobal(p);
            _strings.Clear();
            foreach (var p in new[] { _keys, _values, _request, _cancel })
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            _keys = _values = _request = _cancel = IntPtr.Zero;
        }
    }

    // ── dnsapi.dll ──

    private const uint DNS_QUERY_REQUEST_VERSION1 = 1;
    private const int DNS_REQUEST_PENDING = 9506;

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled;
    }

    private delegate void RegisterCallback(uint status, IntPtr context, IntPtr instance);

    [DllImport("dnsapi.dll")]
    private static extern IntPtr DnsServiceConstructInstance(IntPtr serviceName, IntPtr hostName, IntPtr ip4, IntPtr ip6,
        ushort port, ushort priority, ushort weight, uint propertyCount, IntPtr keys, IntPtr values);
    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr instance);
    [DllImport("dnsapi.dll")] private static extern int DnsServiceRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern int DnsServiceDeRegister(IntPtr request, IntPtr cancel);
}

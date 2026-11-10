using System.Net;
using System.Runtime.InteropServices;

namespace DSPiConsole.Usb.Transport;

/// <summary>A resolved DNS-SD service instance.</summary>
public sealed record DnsSdService(
    string Instance,
    string Host,
    int Port,
    IReadOnlyDictionary<string, string> Txt,
    IReadOnlyList<IPAddress> Addresses,
    uint InterfaceIndex);

/// <summary>DNS-SD as the hub browser needs it; faked in tests.</summary>
public interface IDnsSdBrowser : IDisposable
{
    /// <summary>An instance appeared (full name, "Studio Mac._dspi._tcp.local").</summary>
    event EventHandler<string>? Found;
    /// <summary>An instance said goodbye.</summary>
    event EventHandler<string>? Lost;

    void Start(string serviceType);
    void Stop();
    Task<DnsSdService?> ResolveAsync(string instance, TimeSpan timeout);
}

/// <summary>
/// Windows' own DNS-SD (dnsapi.dll, Windows 10 1809+): DnsServiceBrowse and
/// DnsServiceResolve. Windows' responder does the multicast, so nothing here
/// listens on UDP 5353 and no firewall prompt appears (L0 spike a). Callbacks
/// arrive on system threads.
/// </summary>
public sealed class WindowsDnsSdBrowser : IDnsSdBrowser
{
    private readonly object _lock = new();
    private DNS_SERVICE_CANCEL _cancel;
    private bool _browsing;
    private IntPtr _queryName;
    private readonly BrowseCallback _onBrowse;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<string>? Found;
    public event EventHandler<string>? Lost;

    public WindowsDnsSdBrowser()
    {
        _onBrowse = OnBrowse; // kept alive for the native side
    }

    public void Start(string serviceType)
    {
        lock (_lock)
        {
            if (_browsing) return;
            _queryName = Marshal.StringToHGlobalUni(serviceType.EndsWith(".local") ? serviceType : serviceType + ".local");
            var request = new DNS_SERVICE_BROWSE_REQUEST
            {
                Version = DNS_QUERY_REQUEST_VERSION1,
                QueryName = _queryName,
                pBrowseCallback = Marshal.GetFunctionPointerForDelegate(_onBrowse),
            };
            _cancel = new DNS_SERVICE_CANCEL();
            int status = DnsServiceBrowse(ref request, ref _cancel);
            _browsing = status == DNS_REQUEST_PENDING;
            if (!_browsing)
            {
                Marshal.FreeHGlobal(_queryName);
                _queryName = IntPtr.Zero;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_browsing) return;
            DnsServiceBrowseCancel(ref _cancel);
            _browsing = false;
            // The native side may still hold the name until the cancel lands;
            // a few bytes are cheaper than a use-after-free.
            _queryName = IntPtr.Zero;
            _known.Clear();
        }
    }

    /// <summary>A browse answer: a list of DNS records. A PTR names an
    /// instance; a TTL of 0 is its goodbye.</summary>
    private void OnBrowse(uint status, IntPtr context, IntPtr record)
    {
        try
        {
            for (var p = record; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p))
            {
                ushort type = (ushort)Marshal.ReadInt16(p, 16);
                if (type != DNS_TYPE_PTR) continue;
                uint ttl = (uint)Marshal.ReadInt32(p, 24);
                var instance = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, 32));
                if (string.IsNullOrEmpty(instance)) continue;
                if (ttl > 0)
                {
                    lock (_lock) _known.Add(instance);
                    Found?.Invoke(this, instance);
                }
                else
                {
                    bool known;
                    lock (_lock) known = _known.Remove(instance);
                    if (known) Lost?.Invoke(this, instance);
                }
            }
        }
        catch { /* never throw into the native caller */ }
        finally
        {
            if (record != IntPtr.Zero) DnsRecordListFree(record, 1);
        }
    }

    public Task<DnsSdService?> ResolveAsync(string instance, TimeSpan timeout)
    {
        var done = new TaskCompletionSource<DnsSdService?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var name = Marshal.StringToHGlobalUni(instance);
        int fired = 0;
        ResolveCallback? callback = null;
        callback = (status, ctx, inst) =>
        {
            Volatile.Write(ref fired, 1);
            try { done.TrySetResult(inst == IntPtr.Zero ? null : Read(instance, inst)); }
            catch { done.TrySetResult(null); }
            finally
            {
                if (inst != IntPtr.Zero) DnsServiceFreeInstance(inst);
                lock (KeepAlive) KeepAlive.Remove(callback!);
            }
        };
        lock (KeepAlive) KeepAlive.Add(callback);
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => { lock (KeepAlive) KeepAlive.Remove(callback); }, TaskScheduler.Default);
        var request = new DNS_SERVICE_RESOLVE_REQUEST
        {
            Version = DNS_QUERY_REQUEST_VERSION1,
            QueryName = name,
            pResolveCompletionCallback = Marshal.GetFunctionPointerForDelegate(callback),
        };
        var cancel = new DNS_SERVICE_CANCEL();
        if (DnsServiceResolve(ref request, ref cancel) != DNS_REQUEST_PENDING)
        {
            Marshal.FreeHGlobal(name);
            return Task.FromResult<DnsSdService?>(null);
        }
        return Finish();

        async Task<DnsSdService?> Finish()
        {
            try
            {
                if (await Task.WhenAny(done.Task, Task.Delay(timeout)).ConfigureAwait(false) != done.Task)
                {
                    DnsServiceResolveCancel(ref cancel);
                    // Give a callback already in flight its chance to land first.
                    await Task.WhenAny(done.Task, Task.Delay(500)).ConfigureAwait(false);
                    done.TrySetResult(null);
                }
                return await done.Task.ConfigureAwait(false);
            }
            finally
            {
                // Once the callback has run the native side is done with the
                // name; a cancelled resolve that never called back may still
                // read it, and a few bytes are cheaper than a use-after-free.
                if (Volatile.Read(ref fired) == 1) Marshal.FreeHGlobal(name);
            }
        }
    }

    private static DnsSdService Read(string instance, IntPtr inst)
    {
        var si = Marshal.PtrToStructure<DNS_SERVICE_INSTANCE>(inst);
        var txt = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < si.dwPropertyCount; i++)
        {
            var k = Marshal.PtrToStringUni(Marshal.ReadIntPtr(si.keys, i * IntPtr.Size));
            var v = Marshal.PtrToStringUni(Marshal.ReadIntPtr(si.values, i * IntPtr.Size));
            if (!string.IsNullOrEmpty(k)) txt[k] = v ?? "";
        }
        var addresses = new List<IPAddress>();
        if (si.ip4Address != IntPtr.Zero) addresses.Add(new IPAddress((uint)Marshal.ReadInt32(si.ip4Address)));
        if (si.ip6Address != IntPtr.Zero)
        {
            var b = new byte[16];
            Marshal.Copy(si.ip6Address, b, 0, 16);
            var v6 = new IPAddress(b);
            // A link-local address is only usable with its interface.
            addresses.Add(v6.IsIPv6LinkLocal ? new IPAddress(b, si.dwInterfaceIndex) : v6);
        }
        return new DnsSdService(instance, Marshal.PtrToStringUni(si.pszHostName) ?? "", si.wPort, txt, addresses, si.dwInterfaceIndex);
    }

    public void Dispose() => Stop();

    /// <summary>Resolve callbacks the native side may still call.</summary>
    private static readonly HashSet<object> KeepAlive = new();

    // ── dnsapi.dll ──

    private const uint DNS_QUERY_REQUEST_VERSION1 = 1;
    private const int DNS_REQUEST_PENDING = 9506;
    private const ushort DNS_TYPE_PTR = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_INSTANCE
    {
        public IntPtr pszInstanceName;
        public IntPtr pszHostName;
        public IntPtr ip4Address;
        public IntPtr ip6Address;
        public ushort wPort;
        public ushort wPriority;
        public ushort wWeight;
        public uint dwPropertyCount;
        public IntPtr keys;
        public IntPtr values;
        public uint dwInterfaceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_BROWSE_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr pBrowseCallback;
        public IntPtr pQueryContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_RESOLVE_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr pResolveCompletionCallback;
        public IntPtr pQueryContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_CANCEL { public IntPtr reserved; }

    private delegate void BrowseCallback(uint status, IntPtr context, IntPtr record);
    private delegate void ResolveCallback(uint status, IntPtr context, IntPtr instance);

    [DllImport("dnsapi.dll")] private static extern int DnsServiceBrowse(ref DNS_SERVICE_BROWSE_REQUEST request, ref DNS_SERVICE_CANCEL cancel);
    [DllImport("dnsapi.dll")] private static extern int DnsServiceBrowseCancel(ref DNS_SERVICE_CANCEL cancel);
    [DllImport("dnsapi.dll")] private static extern int DnsServiceResolve(ref DNS_SERVICE_RESOLVE_REQUEST request, ref DNS_SERVICE_CANCEL cancel);
    [DllImport("dnsapi.dll")] private static extern int DnsServiceResolveCancel(ref DNS_SERVICE_CANCEL cancel);
    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr instance);
    [DllImport("dnsapi.dll")] private static extern void DnsRecordListFree(IntPtr list, int freeType);
}

using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Usb.Transport;

/// <summary>
/// "Share Devices on This Network": the hub's server and its DNS-SD
/// advertisement, started and stopped together, with the advertisement kept
/// current as devices, the hub's name and its access mode change. The hub
/// itself runs whether or not this is on (the local UI goes through it).
/// The settings page and its status line drive this one object. Port of the
/// macOS Console's LinkService, minus the power assertions.
/// </summary>
public sealed class LinkSharing : IDisposable
{
    private readonly LinkHub _hub;
    private readonly IDnsSdAdvertiser _advertiser;
    private readonly Func<LinkServer> _makeServer;
    private readonly object _lock = new();
    private LinkServer? _server;
    private string? _advertised;

    /// <summary>Running state, the client count or the error line changed.
    /// Raised on any thread.</summary>
    public event EventHandler? Changed;

    public LinkSharing(LinkHub hub, IDnsSdAdvertiser advertiser, string? webRoot, Func<LinkServer>? makeServer = null)
    {
        _hub = hub;
        _advertiser = advertiser;
        _makeServer = makeServer ?? (() => new LinkServer(LinkSessionHandler.Factory(hub), InfoJson, webRoot));
        LinkSessionHandler.WebBundlePresent = webRoot != null && LinkWebRoot.IsPresent(webRoot);

        // Device-list changes (n, d), a rename and an access-mode change all
        // reach clients through the advertisement.
        _hub.Changed += (_, _) => Refresh();
        _hub.Auth.HubNameChanged += (_, _) => Refresh();
        _hub.Auth.AuthModeChanged += (_, _) => Refresh();
    }

    public bool IsRunning
    {
        get { lock (_lock) return _server?.IsRunning == true; }
    }

    public int? BoundPort
    {
        get { lock (_lock) return _server?.BoundPort; }
    }

    /// <summary>Why the last start failed, for the settings page; null once
    /// it starts.</summary>
    public string? LastError { get; private set; }

    /// <summary>Remote clients connected now.</summary>
    public int ClientCount => _hub.RemoteSessionCount;

    /// <summary>Starts listening and advertising on <paramref name="port"/>.
    /// False (with <see cref="LastError"/>) when the port can't be had.</summary>
    public bool Start(int port)
    {
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1) return false;
            if (_server?.IsRunning == true) return true;
            var server = _makeServer();
            try
            {
                server.Start(port);
            }
            catch (Exception e)
            {
                server.Dispose();
                // The server's own message says which (in use, or reserved by Windows).
                LastError = e is LinkServerStartException ? e.Message : $"Couldn't share on port {port}: {e.Message}";
                LinkLog.Write($"hub: start on {port} failed: {e.GetType().Name}: {e.Message}");
                Raise();
                return false;
            }
            server.ConnectionCountChanged += (_, _) => Raise();
            _server = server;
            LastError = null;
            _advertised = null;
            LinkLog.Write($"hub: sharing on port {server.BoundPort}");
        }
        Refresh();
        Raise();
        return true;
    }

    /// <summary>Stops advertising, then closes every client with 4003 (hub
    /// shutting down: they reconnect with backoff).</summary>
    public void Stop()
    {
        LinkServer? server;
        lock (_lock)
        {
            server = _server;
            _server = null;
            _advertised = null;
            if (server != null) _advertiser.Stop();
        }
        if (server == null) return;
        server.Stop(LinkCloseCode.ShuttingDown);
        server.Dispose();
        LinkLog.Write("hub: sharing stopped");
        Raise();
    }

    /// <summary>The advertisement for the hub as it is now. Re-registers only
    /// when something a client reads changed (the hub's session count, which
    /// also raises the hub's Changed, isn't in it).</summary>
    private void Refresh()
    {
        string name;
        LinkTxtRecord txt;
        int port;
        bool start;
        lock (_lock)
        {
            if (_server is not { IsRunning: true, BoundPort: { } bound } server) return;
            port = bound;
            name = _hub.Auth.HubName;
            var serials = _hub.Registry.Devices.Select(d => d.Info.Serial).ToList();
            txt = new LinkTxtRecord(1, _hub.Auth.HubId.ToString(), LinkHubKind.Console, _hub.Auth.AuthMode,
                serials.Count, serials, Web: server.ServesWeb);
            var key = name + "\n" + string.Join(";", txt.ToDictionary().OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
            if (key == _advertised) return;
            start = _advertised == null;
            _advertised = key;
            // Under the lock (the advertiser only queues): two refreshes can't
            // reach it out of order, nor one after a stop.
            if (start) _advertiser.Start(name, port, txt);
            else _advertiser.Update(name, txt);
        }
        Raise();
    }

    /// <summary>GET /dspi/v1/info (spec 3.2).</summary>
    private string InfoJson()
    {
        var auth = _hub.Auth;
        return new LinkInfo(
            new LinkProtoVersion(),
            new LinkHubInfo(auth.HubId.ToString(), auth.HubName, LinkHubKind.Console, LinkSessionHandler.AppVersion),
            auth.AuthMode,
            LinkService.DefaultPath,
            Devices: _hub.Registry.Devices.Select(d => new LinkInfoDevice(d.Info.Serial, d.Name)).ToList()).Encode();
    }

    private void Raise()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { LinkLog.Write($"hub: sharing listener threw {ex.GetType().Name}"); }
    }

    private int _disposed;

    /// <summary>Stops sharing and lets the advertiser send its goodbye. Safe
    /// to call twice (the app stops sharing first thing at exit).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Stop();
        _advertiser.Dispose();
    }
}

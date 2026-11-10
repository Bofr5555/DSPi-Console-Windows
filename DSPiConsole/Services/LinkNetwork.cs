using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;
using DSPiConsole.Models;
using DSPiConsole.Usb;
using DSPiConsole.Usb.Transport;
using Microsoft.UI.Dispatching;

namespace DSPiConsole.Services;

/// <summary>
/// DSPi Link as the app uses it: the transport the view model's device sits
/// on (boards here plus devices other hubs share), the hub browser feeding it,
/// the stored pairing tokens, and this PC's own hub, which the local UI
/// always goes through as session 1 whether or not sharing is on. One per app.
/// </summary>
public sealed class LinkNetwork : IDisposable
{
    public static LinkNetwork Shared { get; } = new();

    public LinkTokenStore Tokens { get; } = new();
    public LinkHubBrowser Browser { get; }
    public CompositeTransport Transport { get; }

    /// <summary>This PC's hub: its identity, access mode and paired clients.</summary>
    public LinkAuthStore Auth { get; }
    public LinkHub Hub { get; }
    /// <summary>The hub's server and advertisement ("Share Devices on This Network").</summary>
    public LinkSharing Sharing { get; }

    /// <summary>The name this PC gives hubs (hello, pairing): the computer's name.</summary>
    public static string ClientName => Environment.MachineName;

    private LinkNetwork()
    {
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSPiConsole");
        LinkLog.Path = System.IO.Path.Combine(folder, "link.log");
        Auth = new LinkAuthStore(System.IO.Path.Combine(folder, "link-auth.json"));
        Hub = new LinkHub(LinkPolicy.Embedded ?? LinkPolicy.Empty, Auth,
            new DeviceRegistry(System.IO.Path.Combine(folder, "link-device-names.json")));
        LinkSessionHandler.AppVersion = AppInfo.Version;
        Sharing = new LinkSharing(Hub, new WindowsDnsSdAdvertiser(), System.IO.Path.Combine(AppContext.BaseDirectory, "Web"));
        Transport = new CompositeTransport(new HubLocalTransport(new UsbTransport(), Hub), Tokens, (hubId, hub) =>
            new NetworkTransport(new LinkClient(appVersion: AppInfo.Version), hubId, hub.Name, Tokens, ClientName, IsUiThread));
        // Our own hub never lists itself.
        Browser = new LinkHubBrowser(new WindowsDnsSdBrowser()) { OwnHubId = Auth.HubId.ToString() };
        Browser.HubsChanged += (_, hubs) => Transport.UpdateHubs(hubs);
        Transport.HubUnreachable += (_, hubId) => Browser.Reresolve(hubId);
    }

    /// <summary>True on a thread with a dispatcher (a window's UI thread): a
    /// GET there must not wait on the network for long.</summary>
    private static bool IsUiThread() => DispatcherQueue.GetForCurrentThread() != null;

    /// <summary>Applies the Networking settings: discovery on or off, and the
    /// hubs added by address.</summary>
    public void ApplySettings()
    {
        var s = AppSettings.Instance;
        Browser.Enabled = s.LinkLookForHubs;
        Browser.SetManual(s.LinkManualHubs ?? new List<string>());
    }

    /// <summary>Called once the main window can show dialogs.</summary>
    public void Start(Func<LinkPairingRequest, Task<string?>> pairingPrompt)
    {
        Transport.PairingPrompt = pairingPrompt;
        ApplySettings();
        if (AppSettings.Instance.LinkSharingEnabled)
            lock (_sharingLock) _sharingStart = Task.Run(() => Sharing.Start(SharingPort));
    }

    private readonly object _sharingLock = new();
    private Task _sharingStart = Task.CompletedTask;

    /// <summary>The saved port, kept in range (a hand-edited settings file
    /// could hold anything).</summary>
    public static int SharingPort => Math.Clamp(AppSettings.Instance.LinkSharingPort, 1024, 65535);

    /// <summary>Turns sharing on or off and remembers the choice, whether or
    /// not a start succeeds: a port that's taken now may be free at the next
    /// launch, and the page shows why it failed (<see cref="LinkSharing.LastError"/>).
    /// Waits for the launch's own start, so an "off" can't be overtaken by it.</summary>
    public bool SetSharing(bool on)
    {
        Task launch;
        lock (_sharingLock) launch = _sharingStart;
        try { launch.Wait(); } catch (AggregateException) { }
        AppSettings.Instance.LinkSharingEnabled = on;
        AppSettings.Instance.Save();
        if (on) return Sharing.Start(SharingPort);
        Sharing.Stop();
        return false;
    }

    /// <summary>First thing at exit, while the board is still attached: clients
    /// hear 4003 (hub shutting down) rather than the board disappearing, and
    /// the advertisement is withdrawn once, not re-registered without it.</summary>
    public void StopSharingForExit()
    {
        try { Sharing.Dispose(); }
        catch (Exception ex) { LinkLog.Write($"hub: stopping sharing at exit threw {ex.GetType().Name}"); }
    }

    public void Dispose()
    {
        Browser.Dispose();
        Sharing.Dispose();
        Hub.Dispose();
    }
}

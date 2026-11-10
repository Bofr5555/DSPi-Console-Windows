using DSPiConsole.Core.Link;
using DSPiConsole.Models;
using DSPiConsole.Usb;
using DSPiConsole.Usb.Transport;
using Microsoft.UI.Dispatching;

namespace DSPiConsole.Services;

/// <summary>
/// DSPi Link as the app uses it: the transport the view model's device sits
/// on (boards here plus devices other hubs share), the hub browser feeding it,
/// and the stored pairing tokens. One per app.
/// </summary>
public sealed class LinkNetwork : IDisposable
{
    public static LinkNetwork Shared { get; } = new();

    public LinkTokenStore Tokens { get; } = new();
    public LinkHubBrowser Browser { get; }
    public CompositeTransport Transport { get; }

    /// <summary>The name this PC gives hubs (hello, pairing): the computer's name.</summary>
    public static string ClientName => Environment.MachineName;

    private LinkNetwork()
    {
        LinkLog.Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSPiConsole", "link.log");
        Transport = new CompositeTransport(new UsbTransport(), Tokens, (hubId, hub) =>
            new NetworkTransport(new LinkClient(appVersion: AppInfo.Version), hubId, hub.Name, Tokens, ClientName, IsUiThread));
        Browser = new LinkHubBrowser(new WindowsDnsSdBrowser());
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
    }

    public void Dispose()
    {
        Browser.Dispose();
    }
}

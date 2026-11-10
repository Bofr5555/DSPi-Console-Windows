using System.Diagnostics;
using System.IO;

namespace DSPiConsole.Services;

/// <summary>What Windows Firewall will do with other computers connecting to
/// this app on a Private network.</summary>
public enum LinkFirewallState
{
    /// <summary>An allow rule covers it, or the firewall is off.</summary>
    Allowed,
    /// <summary>A block rule for the app (typically left by dismissing the
    /// first-run prompt): other computers can't connect.</summary>
    Blocked,
    /// <summary>No rule yet: Windows asks the first time the hub listens.</summary>
    NoRule,
    /// <summary>The firewall couldn't be read.</summary>
    Unknown,
}

/// <summary>
/// The hub's biggest field risk (spec 9.1.1, plan L3 item 5): the first
/// network listen by an unpackaged app shows the Windows Firewall prompt, and
/// dismissing it leaves a silent block rule. This reads the rules for the
/// app and the current network's category, and adds the allow rule
/// elevated when the user asks. Reads through the firewall's own COM API
/// (HNetCfg.FwPolicy2) and the Network List Manager; writes through netsh,
/// which needs administrator rights, so it runs elevated.
/// </summary>
public static class LinkFirewall
{
    private const int ProfilePrivate = 2;
    private const int DirectionIn = 1;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int ProtocolTcp = 6;
    private const int ProtocolAny = 256;

    public const string RuleName = "DSPi Console (DSPi Link)";

    /// <summary>The app's own executable (under app\ in a release).</summary>
    public static string AppPath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DSPiConsole.exe");

    public static LinkFirewallState Check(int port) => Check(AppPath, port);

    public static LinkFirewallState Check(string exePath, int port)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return LinkFirewallState.Unknown;
            dynamic policy = Activator.CreateInstance(type)!;
            if (!(bool)policy.FirewallEnabled[ProfilePrivate]) return LinkFirewallState.Allowed;

            bool allowed = false, blocked = false;
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                if (app == null || !PathsEqual(app, exePath)) continue;
                if ((int)rule.Direction != DirectionIn || !(bool)rule.Enabled) continue;
                if (((int)rule.Profiles & ProfilePrivate) == 0) continue;
                int protocol = (int)rule.Protocol;
                if (protocol != ProtocolTcp && protocol != ProtocolAny) continue;
                if ((int)rule.Action == ActionBlock && CoversPort((string?)rule.LocalPorts, port)) blocked = true;
                else if ((int)rule.Action == ActionAllow && CoversPort((string?)rule.LocalPorts, port)) allowed = true;
            }
            // A block rule wins over any allow rule.
            if (blocked) return LinkFirewallState.Blocked;
            return allowed ? LinkFirewallState.Allowed : LinkFirewallState.NoRule;
        }
        catch (Exception ex)
        {
            Core.Link.LinkLog.Write($"firewall: check failed: {ex.GetType().Name}");
            return LinkFirewallState.Unknown;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(a)), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>"*", empty, a list, or ranges ("11915", "1024-65535").</summary>
    internal static bool CoversPort(string? ports, int port)
    {
        if (string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*") return true;
        foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-');
            if (range.Length == 1 && int.TryParse(range[0], out var single) && single == port) return true;
            if (range.Length == 2 && int.TryParse(range[0], out var lo) && int.TryParse(range[1], out var hi) && port >= lo && port <= hi) return true;
        }
        return false;
    }

    /// <summary>Replaces every inbound rule for the app with one allow rule
    /// for TCP on Private networks, elevated (one UAC prompt). Block rules
    /// have to go: Windows lets a block rule win over an allow rule. True
    /// when the commands ran; the caller re-checks.</summary>
    public static async Task<bool> AllowAsync(int port)
    {
        // cmd expands %NAME% even inside quotes; a path can't hold a quote.
        var app = AppPath.Replace("%", "%%");
        var script = $"/c netsh advfirewall firewall delete rule name=all dir=in program=\"{app}\" & " +
                     $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{app}\" " +
                     $"enable=yes profile=private protocol=TCP localport={port}";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", script)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process == null) return false;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;   // the user declined the UAC prompt
        }
    }

    // ── Network category (INetworkListManager) ──

    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private const int NlmEnumNetworkConnected = 1;
    private const int NlmCategoryPublic = 0;

    /// <summary>The connected networks Windows treats as Public, where it
    /// blocks discovery and incoming connections: other computers won't
    /// find or reach the hub there. Empty when none (or unknown).</summary>
    public static IReadOnlyList<string> PublicNetworks()
    {
        var names = new List<string>();
        try
        {
            var type = Type.GetTypeFromCLSID(NetworkListManagerClsid);
            if (type == null) return names;
            dynamic manager = Activator.CreateInstance(type)!;
            foreach (dynamic network in manager.GetNetworks(NlmEnumNetworkConnected))
            {
                if ((int)network.GetCategory() == NlmCategoryPublic) names.Add((string)network.GetName());
            }
        }
        catch (Exception ex)
        {
            Core.Link.LinkLog.Write($"firewall: network list failed: {ex.GetType().Name}");
        }
        return names;
    }

    /// <summary>Opens Settings at the network's properties, where the profile
    /// is switched to Private.</summary>
    public static void OpenNetworkSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true }); }
        catch (Exception) { }
    }
}

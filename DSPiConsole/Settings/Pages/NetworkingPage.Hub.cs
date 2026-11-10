using CommunityToolkit.WinUI.Controls;
using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;
using DSPiConsole.Models;
using DSPiConsole.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// The hub half of Settings › Networking (L3): "Share Devices on This
/// Network" with its status line, the Windows Firewall and network-profile
/// warnings, This Hub (name, access, port) and, with Require Pairing, the
/// paired clients and the pairing code. Live state is refreshed in place;
/// nothing the user may be typing in is rebuilt.
/// </summary>
internal sealed partial class NetworkingPanel
{
    private ToggleSwitch _share = null!;
    private SettingsCard _shareCard = null!;
    private InfoBar _shareError = null!;
    private InfoBar _firewall = null!;
    private InfoBar _publicNetwork = null!;
    private TextBox _hubName = null!;
    private ComboBox _access = null!;
    private NumberBox _port = null!;
    private TextBlock _portNote = null!;
    private StackPanel _clientsSection = null!;
    private StackPanel _clients = null!;
    private Button _pairNew = null!;
    private TextBlock _pairNote = null!;
    private readonly DispatcherTimer _pinTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SettingsCard? _pinCard;
    private TextBlock? _pinCode;
    private string? _pinShown;
    private TextBlock? _noClients;
    /// <summary>One row per paired client, kept across refreshes: a refresh
    /// updates a row in place, so a role list open on it stays open.</summary>
    private readonly Dictionary<int, ClientRow> _rows = new();

    private sealed class ClientRow(SettingsCard card, ComboBox role)
    {
        public SettingsCard Card { get; } = card;
        public ComboBox Role { get; } = role;
        public bool Updating;
    }
    private bool _updatingHub;
    private LinkFirewallState _firewallState = LinkFirewallState.Unknown;

    private void BuildHubSection(StackPanel root)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        // ── Share Devices on This Network ──
        _share = new ToggleSwitch { OnContent = "On", OffContent = "Off" };
        _share.Toggled += async (_, _) =>
        {
            if (_updatingHub) return;
            bool on = _share.IsOn;
            _share.IsEnabled = false;
            try
            {
                await Task.Run(() => _link.SetSharing(on));
            }
            catch (Exception ex)
            {
                Core.Link.LinkLog.Write($"hub: sharing switch failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _share.IsEnabled = true;
                RefreshHub();
            }
            if (on) _ = CheckFirewallAsync();
        };
        _shareCard = new SettingsCard
        {
            Header = "Share Devices on This Network",
            HeaderIcon = new FontIcon { Glyph = ((char)0xE968).ToString() },
            Content = _share,
        };
        _shareError = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };

        _firewall = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
        var allow = new Button { Content = "Allow in Windows Firewall" };
        allow.Click += async (_, _) =>
        {
            allow.IsEnabled = false;
            await LinkFirewall.AllowAsync(_link.Sharing.BoundPort ?? Port);
            await CheckFirewallAsync();
            allow.IsEnabled = true;
        };
        _firewall.ActionButton = allow;

        _publicNetwork = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
        var openSettings = new Button { Content = "Open Network Settings" };
        openSettings.Click += (_, _) => LinkFirewall.OpenNetworkSettings();
        _publicNetwork.ActionButton = openSettings;

        root.Children.Add(Section("Sharing", top: 0));
        root.Children.Add(_shareCard);
        root.Children.Add(_shareError);
        root.Children.Add(_firewall);
        root.Children.Add(_publicNetwork);

        // ── This Hub ──
        _hubName = new TextBox { Width = 260, MaxLength = 63 };
        _hubName.LostFocus += (_, _) => CommitHubName();
        _hubName.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            CommitHubName();
            e.Handled = true;
        };

        _access = new ComboBox { MinWidth = 180 };
        _access.Items.Add(new ComboBoxItem { Content = "Open", Tag = LinkAuthMode.None });
        _access.Items.Add(new ComboBoxItem { Content = "Require Pairing", Tag = LinkAuthMode.Pin });
        _access.SelectionChanged += (_, _) =>
        {
            if (_updatingHub || _access.SelectedItem is not ComboBoxItem { Tag: LinkAuthMode mode }) return;
            _link.Auth.AuthMode = mode;
        };

        _port = new NumberBox
        {
            Minimum = 1024,
            Maximum = 65535,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            Width = 120,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        };
        _port.ValueChanged += (_, e) =>
        {
            if (_updatingHub || double.IsNaN(e.NewValue)) return;
            AppSettings.Instance.LinkSharingPort = (int)Math.Clamp(e.NewValue, 1024, 65535);
            AppSettings.Instance.Save();
        };
        _portNote = Footer("Turn sharing off to change the port.", secondary);

        root.Children.Add(Section("This hub", top: 20));
        root.Children.Add(new SettingsCard { Header = "Name", Description = "Other Consoles see this PC as a hub with this name.", Content = _hubName });
        root.Children.Add(new SettingsCard
        {
            Header = "Access",
            Description = "Open lets anyone on this network use the shared device. Require Pairing asks each new client for a code shown here.",
            Content = _access,
        });
        root.Children.Add(new SettingsCard { Header = "Port", Content = _port });
        root.Children.Add(_portNote);
        root.Children.Add(Footer("Sharing works on your local network only. To reach it from elsewhere, use a VPN such as Tailscale. Don't forward the port on your router.", secondary));

        // ── Paired clients (Require Pairing only) ──
        _clients = new StackPanel { Spacing = 4 };
        _pairNew = new Button { Content = "Pair a New Client", Margin = new Thickness(0, 8, 0, 0) };
        _pairNew.Click += (_, _) =>
        {
            _link.Auth.BeginPairing();
            RefreshHub();
        };
        _pairNote = Footer("Turn sharing on to pair a new client.", secondary);
        _clientsSection = new StackPanel { Spacing = 4 };
        _clientsSection.Children.Add(Section("Paired clients", top: 20));
        _clientsSection.Children.Add(_clients);
        _clientsSection.Children.Add(_pairNew);
        _clientsSection.Children.Add(_pairNote);
        _clientsSection.Children.Add(Footer("Viewer can only watch. Control can adjust devices. Admin can also change these hub settings.", secondary));
        root.Children.Add(_clientsSection);

        _pinTimer.Tick += (_, _) => RefreshClients();

        EventHandler? onChange = null;
        void Unhook()
        {
            _link.Sharing.Changed -= onChange;
            _link.Auth.Changed -= onChange;
            _link.Hub.Changed -= onChange;
            _pinTimer.Stop();
        }
        // Unloaded doesn't always come when the Settings window closes on this
        // page: a panel no longer in a window lets go on the next event.
        onChange = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (XamlRoot == null) Unhook();
            else RefreshHub();
        });
        Loaded += (_, _) =>
        {
            _link.Sharing.Changed -= onChange;
            _link.Sharing.Changed += onChange;
            _link.Auth.Changed -= onChange;
            _link.Auth.Changed += onChange;
            _link.Hub.Changed -= onChange;
            _link.Hub.Changed += onChange;
            RefreshHub();
            if (_link.Sharing.IsRunning) _ = CheckFirewallAsync();
        };
        Unloaded += (_, _) => Unhook();
    }

    private static int Port => LinkNetwork.SharingPort;

    private static TextBlock Footer(string text, Brush brush) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 4, 2, 0),
    };

    /// <summary>An empty name reverts; anything else is the hub's new name
    /// (re-advertised at once).</summary>
    private void CommitHubName()
    {
        var name = _hubName.Text.Trim();
        if (name.Length == 0 || name == _link.Auth.HubName)
        {
            _hubName.Text = _link.Auth.HubName;
            return;
        }
        _link.Auth.HubName = name;
    }

    private void RefreshHub()
    {
        _updatingHub = true;
        try
        {
            var sharing = _link.Sharing;
            bool running = sharing.IsRunning;
            _share.IsOn = running;
            _shareCard.Description = running
                ? StatusLine(sharing)
                : "Lets phones, browsers and other Consoles control the DSPi plugged into this PC.";
            _shareError.Message = sharing.LastError ?? "";
            _shareError.IsOpen = !running && sharing.LastError != null;

            // Never rewrite the name under the user's caret.
            if (_hubName.FocusState == FocusState.Unfocused && _hubName.Text != _link.Auth.HubName)
                _hubName.Text = _link.Auth.HubName;
            var mode = _link.Auth.AuthMode;
            var item = _access.Items.OfType<ComboBoxItem>().First(i => (LinkAuthMode)i.Tag == mode);
            if (!ReferenceEquals(_access.SelectedItem, item)) _access.SelectedItem = item;
            if (_port.FocusState == FocusState.Unfocused) _port.Value = Port;
            _port.IsEnabled = !running;
            _portNote.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            _clientsSection.Visibility = mode == LinkAuthMode.Pin ? Visibility.Visible : Visibility.Collapsed;
            _pairNew.IsEnabled = running;
            _pairNote.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            if (!running)
            {
                _firewall.IsOpen = false;
                _publicNetwork.IsOpen = false;
            }
            RefreshClients();
        }
        finally { _updatingHub = false; }
    }

    private static string StatusLine(Usb.Transport.LinkSharing sharing)
    {
        int count = sharing.ClientCount;
        var clients = count == 0 ? "No clients connected" : count == 1 ? "1 client connected" : $"{count} clients connected";
        return LinkNetwork.LanAddress() is { } ip ? $"{ip}:{sharing.BoundPort} · {clients}" : clients;
    }

    // ── Paired clients ──

    /// <summary>Brings the pairing code and the client rows up to date in
    /// place: rows are added and removed as clients come and go, and an
    /// existing row only has its text and role refreshed.</summary>
    private void RefreshClients()
    {
        var auth = _link.Auth;
        if (auth.ActivePin is { } pin && auth.PairingExpires is { } expires)
        {
            if (_pinCard == null)
            {
                _pinCard = PinCard(out var code);
                _pinCode = code;
                _clients.Children.Insert(0, _pinCard);
            }
            if (_pinShown != pin)
            {
                _pinShown = pin;
                _pinCode!.Text = $"{pin[..3]} {pin[3..]}";
            }
            _pinCard.Description = Countdown(expires);
            _pairNew.Visibility = Visibility.Collapsed;
            if (!_pinTimer.IsEnabled) _pinTimer.Start();
        }
        else
        {
            if (_pinCard != null) _clients.Children.Remove(_pinCard);
            _pinCard = null;
            _pinCode = null;
            _pinShown = null;
            _pairNew.Visibility = Visibility.Visible;
            _pinTimer.Stop();
        }

        var clients = auth.Clients;
        var online = _link.Hub.OnlineClientIds();
        foreach (var gone in _rows.Keys.Where(cid => clients.All(c => c.Cid != cid)).ToList())
        {
            _clients.Children.Remove(_rows[gone].Card);
            _rows.Remove(gone);
        }
        foreach (var client in clients)
        {
            if (!_rows.TryGetValue(client.Cid, out var row))
            {
                row = MakeClientRow(client.Cid);
                _rows[client.Cid] = row;
                _clients.Children.Add(row.Card);
            }
            if (!Equals(row.Card.Header, client.Name)) row.Card.Header = client.Name;
            var description = online.Contains(client.Cid) ? "Connected now"
                : client.LastSeen is { } seen ? $"Last seen {Ago(seen)}" : "Never connected";
            if (!Equals(row.Card.Description, description)) row.Card.Description = description;
            // Never under the user's hand.
            if (!row.Role.IsDropDownOpen && row.Role.FocusState == FocusState.Unfocused)
            {
                var item = row.Role.Items.OfType<ComboBoxItem>().First(i => (LinkRole)i.Tag == client.Role);
                if (!ReferenceEquals(row.Role.SelectedItem, item))
                {
                    row.Updating = true;
                    row.Role.SelectedItem = item;
                    row.Updating = false;
                }
            }
        }

        if (clients.Count == 0)
        {
            _noClients ??= new TextBlock
            {
                Text = "No paired clients yet.",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(2, 4, 2, 0),
            };
            if (!_clients.Children.Contains(_noClients)) _clients.Children.Add(_noClients);
        }
        else if (_noClients != null)
        {
            _clients.Children.Remove(_noClients);
        }
    }

    private SettingsCard PinCard(out TextBlock code)
    {
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) =>
        {
            _link.Auth.CancelPairing();
            RefreshHub();
        };
        code = new TextBlock
        {
            FontSize = 28,
            FontFamily = new FontFamily("Consolas"),
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        right.Children.Add(code);
        right.Children.Add(cancel);
        return new SettingsCard
        {
            Header = "Enter this code on the new client",
            HeaderIcon = new FontIcon { Glyph = ((char)0xE8D7).ToString() },
            Content = right,
        };
    }

    private static string Countdown(DateTimeOffset expires)
    {
        var left = expires - DateTimeOffset.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return $"Expires in {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private ClientRow MakeClientRow(int cid)
    {
        var role = new ComboBox { MinWidth = 120 };
        foreach (var (label, value) in new[] { ("Viewer", LinkRole.Viewer), ("Control", LinkRole.Control), ("Admin", LinkRole.Admin) })
            role.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        var remove = new Button { Content = "Remove" };
        ToolTipService.SetToolTip(remove, "Forget this client. It disconnects now and must pair again.");
        remove.Click += (_, _) => _link.Auth.Revoke(cid);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(role);
        right.Children.Add(remove);
        var row = new ClientRow(new SettingsCard
        {
            HeaderIcon = new FontIcon { Glyph = ((char)0xE7F4).ToString() },
            Content = right,
        }, role);
        // Applied when the list closes, not on every arrow step through it.
        role.SelectionChanged += (_, _) =>
        {
            if (row.Updating || role.IsDropDownOpen) return;
            ApplyRole(cid, role);
        };
        role.DropDownClosed += (_, _) => ApplyRole(cid, role);
        return row;
    }

    private void ApplyRole(int cid, ComboBox role)
    {
        if (role.SelectedItem is ComboBoxItem { Tag: LinkRole r } && _link.Auth.Client(cid) is { } client && client.Role != r)
            _link.Auth.SetRole(cid, r);
    }

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return when.ToLocalTime().ToString("d MMM yyyy");
    }

    // ── Windows Firewall and the network profile (spec 9.1.1) ──

    private async Task CheckFirewallAsync()
    {
        int port = _link.Sharing.BoundPort ?? Port;
        var (state, publicNetworks) = await Task.Run(() => (LinkFirewall.Check(port), LinkFirewall.PublicNetworks()));
        _firewallState = state;
        bool running = _link.Sharing.IsRunning;
        switch (state)
        {
            case LinkFirewallState.Blocked:
                _firewall.Title = "Windows Firewall is blocking other computers";
                _firewall.Message = "Phones and other Consoles can't connect until DSPi Console is allowed on private networks.";
                _firewall.IsOpen = running;
                break;
            case LinkFirewallState.NoRule:
                _firewall.Title = "Windows Firewall hasn't allowed DSPi Console yet";
                _firewall.Message = "Windows may ask the first time another computer connects. Choose Allow, or allow it now.";
                _firewall.IsOpen = running;
                break;
            default:
                _firewall.IsOpen = false;
                break;
        }
        if (publicNetworks.Count > 0)
        {
            _publicNetwork.Title = publicNetworks.Count == 1 ? $"\"{publicNetworks[0]}\" is a public network" : "This PC is on a public network";
            _publicNetwork.Message = "Windows hides this PC from other devices on public networks. If this is your home or studio network, set it to Private.";
            _publicNetwork.IsOpen = running;
        }
        else
        {
            _publicNetwork.IsOpen = false;
        }
    }
}

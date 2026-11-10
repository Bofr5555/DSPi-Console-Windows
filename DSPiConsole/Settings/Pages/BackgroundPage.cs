using CommunityToolkit.WinUI.Controls;
using DSPiConsole.Services;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Settings › Background (L5): the notification icon, starting hidden,
/// opening at login and keeping the PC awake while sharing. The macOS
/// Console's "Running in the Background" options, given their own page.
/// </summary>
public sealed class BackgroundPage : ISettingsPage
{
    public string Id => "general.background";
    public string Title => "Background";
    public SettingsCategory Category => SettingsCategory.General;
    public string IconGlyph => ""; // Taskbar
    public int Order => 50;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker) => new BackgroundPanel();
}

/// <summary>Switches set in place from <see cref="BackgroundMode"/>, never rebuilt.</summary>
internal sealed class BackgroundPanel : UserControl
{
    private readonly ToggleSwitch _showInTray = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _startInTray = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _openAtLogin = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _keepAwake = new() { OnContent = "On", OffContent = "Off" };
    private readonly InfoBar _loginError = new() { Severity = InfoBarSeverity.Warning, IsClosable = false };
    private bool _updating;

    public BackgroundPanel()
    {
        var background = BackgroundMode.Shared;
        _showInTray.Toggled += (_, _) => { if (!_updating) background.ShowInNotificationArea = _showInTray.IsOn; };
        _startInTray.Toggled += (_, _) => { if (!_updating) background.StartInNotificationArea = _startInTray.IsOn; };
        _openAtLogin.Toggled += (_, _) => { if (!_updating) background.SetStartsAtLogin(_openAtLogin.IsOn); };
        _keepAwake.Toggled += (_, _) => { if (!_updating) background.KeepAwakeWhileSharing = _keepAwake.IsOn; };

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(new SettingsCard
        {
            Header = "Show in the notification area",
            Description = "Closing the window keeps DSPi Console running in the notification area.",
            Content = _showInTray,
        });
        root.Children.Add(new SettingsCard
        {
            Header = "Start in the notification area only",
            Description = "When DSPi Console opens at login, leave the window closed.",
            Content = _startInTray,
        });
        root.Children.Add(new SettingsCard { Header = "Open at login", Content = _openAtLogin });
        root.Children.Add(_loginError);
        root.Children.Add(new SettingsCard
        {
            Header = "Keep this PC awake while sharing",
            Description = "Stops the PC going to sleep on its own while other devices can use the shared DSPi.",
            Content = _keepAwake,
        });
        Content = root;

        EventHandler onChange = (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        Loaded += (_, _) =>
        {
            background.Changed -= onChange;
            background.Changed += onChange;
            Refresh();
        };
        Unloaded += (_, _) => background.Changed -= onChange;
    }

    private void Refresh()
    {
        if (XamlRoot == null) return;
        var background = BackgroundMode.Shared;
        _updating = true;
        try
        {
            _showInTray.IsOn = background.ShowInNotificationArea;
            _startInTray.IsOn = background.StartInNotificationArea;
            _startInTray.IsEnabled = background.ShowInNotificationArea;
            _openAtLogin.IsOn = background.StartsAtLogin;
            _keepAwake.IsOn = background.KeepAwakeWhileSharing;
            _loginError.Message = background.LoginItemError ?? "";
            _loginError.IsOpen = background.LoginItemError != null;
        }
        finally { _updating = false; }
    }
}

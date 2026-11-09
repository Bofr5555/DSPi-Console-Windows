using System.ComponentModel;
using CommunityToolkit.WinUI.Controls;
using DSPiConsole.Models;
using DSPiConsole.Services;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Software Update: the running version, the result of the last check, the
/// controls to check or install, and the automatic-check switch. Sits beside
/// About at the foot of the sidebar. Driven by <see cref="AppUpdater"/>; port
/// of the macOS Console's SoftwareUpdateSettingsTab.
/// </summary>
public sealed class SoftwareUpdatePage : ISettingsPage
{
    public string Id => "about.software-update";
    public string Title => "Software Update";
    public SettingsCategory Category => SettingsCategory.About;
    public string IconGlyph => "\uE896"; // Download
    public int Order => 20;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker) => new SoftwareUpdatePanel();
}

internal sealed class SoftwareUpdatePanel : UserControl
{
    private const string GlyphDownload = "\uE896";
    private const string GlyphDone = "\uE930";
    private const string GlyphWarning = "\uE7BA";

    private readonly AppUpdater _updater = AppUpdater.Shared;
    private readonly FontIcon _icon = new() { FontSize = 28 };
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
    private readonly HyperlinkButton _notes = new() { Content = "Release Notes" };
    private readonly Button _cancel = new() { Content = "Cancel" };
    private readonly Button _install = new() { Content = "Install and Relaunch", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Button _check = new() { Content = "Check Now" };
    private readonly TextBlock _note = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 0) };
    private bool _replaceable;

    public SoftwareUpdatePanel()
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _detail.Foreground = secondary;
        _note.Foreground = secondary;
        _title.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(_title);
        text.Children.Add(_detail);
        text.Children.Add(_bar);
        var status = new Grid { ColumnSpacing = 14 };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _icon.VerticalAlignment = VerticalAlignment.Top;
        status.Children.Add(_icon);
        Grid.SetColumn(text, 1);
        status.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_notes);
        buttons.Children.Add(new Border { Width = 0, HorizontalAlignment = HorizontalAlignment.Stretch });
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_install);
        buttons.Children.Add(_check);

        var statusBox = new StackPanel();
        statusBox.Children.Add(status);
        statusBox.Children.Add(buttons);
        var statusCard = new Border
        {
            Child = statusBox,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };

        var auto = new ToggleSwitch { IsOn = AppSettings.Instance.CheckForUpdatesAutomatically, OnContent = "On", OffContent = "Off" };
        auto.Toggled += (_, _) =>
        {
            AppSettings.Instance.CheckForUpdatesAutomatically = auto.IsOn;
            AppSettings.Instance.Save();
        };

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(Section("Status", top: 0));
        root.Children.Add(statusCard);
        root.Children.Add(Section("Automatic checks", top: 16));
        root.Children.Add(new SettingsCard { Header = "Check for updates automatically", Content = auto });
        root.Children.Add(_note);
        Content = root;

        _notes.Click += async (_, _) => { if (_updater.Available is { } r) await AppUpdater.OpenReleasePageAsync(r); };
        _cancel.Click += (_, _) => _updater.CancelInstall();
        _install.Click += async (_, _) =>
        {
            if (_updater.InstallState == UpdateInstallState.WaitingForQuit) _updater.RetryQuit();
            else if (_updater.Available is { } r) await _updater.InstallAsync(r);
        };
        // The outcome shows on this page, failures included, so nothing to catch up on here.
        _check.Click += async (_, _) =>
        {
            // A failed install shown here has been seen; the card shows the check now.
            _updater.ClearFailure();
            try { await _updater.CheckAsync(); } catch (Exception) { }
        };

        Loaded += (_, _) =>
        {
            _replaceable = _updater.CanReplace;
            _updater.PropertyChanged += OnChanged;
            Render();
        };
        Unloaded += (_, _) => _updater.PropertyChanged -= OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        var state = _updater.InstallState;
        var available = _updater.Available;

        _title.Text = state switch
        {
            UpdateInstallState.Downloading => "Downloading update...",
            UpdateInstallState.Verifying => "Verifying update...",
            UpdateInstallState.Ready => "Installing update...",
            UpdateInstallState.WaitingForQuit => "Update ready",
            UpdateInstallState.Failed => "The update couldn't be installed",
            _ when _updater.IsChecking => "Checking for updates...",
            _ when available != null => $"DSPi Console {available.Version?.ToString() ?? available.TagName} is available",
            _ when _updater.CheckError != null => "Couldn't check for updates",
            _ when _updater.LastChecked == null => "Not checked yet",
            _ => "DSPi Console is up to date",
        };
        _detail.Text = state switch
        {
            UpdateInstallState.Ready => "DSPi Console is quitting so the update can be installed.",
            UpdateInstallState.WaitingForQuit => "It will be installed when DSPi Console quits.",
            UpdateInstallState.Failed => _updater.Failure ?? "",
            _ when _updater.CheckError is { } error && !_updater.IsChecking => error,
            _ => VersionLine(),
        };

        bool busy = state is UpdateInstallState.Downloading or UpdateInstallState.Verifying or UpdateInstallState.Ready;
        (_icon.Glyph, _icon.Foreground) = state switch
        {
            UpdateInstallState.Failed => (GlyphWarning, Brush("SystemFillColorCautionBrush")),
            _ when busy || state == UpdateInstallState.WaitingForQuit || available != null => (GlyphDownload, Brush("AccentTextFillColorPrimaryBrush")),
            _ when _updater.CheckError != null && !_updater.IsChecking => (GlyphWarning, Brush("SystemFillColorCautionBrush")),
            _ => (GlyphDone, Brush("SystemFillColorSuccessBrush")),
        };

        _bar.Visibility = state is UpdateInstallState.Downloading or UpdateInstallState.Verifying ? Visibility.Visible : Visibility.Collapsed;
        _bar.IsIndeterminate = state == UpdateInstallState.Verifying;
        if (state == UpdateInstallState.Downloading) _bar.Value = _updater.Progress;

        _notes.Visibility = available != null ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Visibility = state == UpdateInstallState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        bool canInstall = state == UpdateInstallState.WaitingForQuit
                          || (available != null && !busy && _replaceable && _updater.CanInstall(available));
        _install.Visibility = canInstall ? Visibility.Visible : Visibility.Collapsed;
        _check.IsEnabled = !_updater.IsChecking && !_updater.IsInstalling;

        _note.Text = _replaceable
            ? "DSPi Console checks GitHub for new releases at launch and once a day. Each release includes matching DSPi firmware, which the app offers to install when your device is connected. A download is checked against the release signature before it is installed."
            : "DSPi Console can't update itself from where it is running: its folder can't be written to, or it isn't a release folder. Move the release folder somewhere you can write to, such as your Documents folder, to install updates from here; until then, updates open the release page.";
    }

    private string VersionLine()
    {
        var line = $"Version {_updater.CurrentVersionText}";
        if (_updater.IsChecking || _updater.LastChecked is not { } when) return line;
        var day = when.Date == DateTime.Today ? "today"
                : when.Date == DateTime.Today.AddDays(-1) ? "yesterday"
                : when.ToString("d MMMM yyyy");
        return $"{line}, last checked {day} at {when:t}";
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static TextBlock Section(string title, double top) => new()
    {
        Text = title,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(1, top, 0, 4),
    };
}

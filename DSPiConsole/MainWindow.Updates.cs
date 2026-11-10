using DSPiConsole.Core.Updates;
using DSPiConsole.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DSPiConsole;

/// <summary>
/// Software updates in the main window: the prompt when a check finds a
/// release, Help › Check for Updates, the install progress dialog, and the
/// quit that lets the launcher swap the new version in. The work itself is
/// <see cref="AppUpdater"/>'s. After the macOS Console's AppUpdateChecker.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The update dialog on screen (the offer, the progress, a
    /// message), so a quit can move it out of the way of its own prompts.</summary>
    private ContentDialog? _updateDialog;
    private Task<ContentDialogResult>? _updateDialogTask;
    /// <summary>An automatic offer waiting for the window to be shown; a newer
    /// offer replaces it.</summary>
    private EventHandler? _deferredUpdate;

    private void InitializeUpdates()
    {
        var updater = AppUpdater.Shared;
        updater.UpdateFound += release => DispatcherQueue.TryEnqueue(() => _ = PresentUpdateAsync(release, automatic: true));
        updater.ReadyToQuit += () => DispatcherQueue.TryEnqueue(QuitForUpdate);
        updater.Start(DispatcherQueue);
    }

    /// <summary>Only one ContentDialog may be open on a window at a time.</summary>
    private bool IsContentDialogOpen() =>
        Content?.XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(p => p.Child is ContentDialog);

    /// <summary>Waits for another dialog to close, giving up after
    /// <paramref name="limit"/>; true when none is open.</summary>
    private async Task<bool> WaitForNoDialogAsync(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (IsContentDialogOpen())
        {
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        return true;
    }

    /// <summary>Shows an update dialog in the tracked slot.</summary>
    private async Task<ContentDialogResult> ShowUpdateDialogAsync(ContentDialog dialog)
    {
        _updateDialog = dialog;
        try
        {
            _updateDialogTask = dialog.ShowAsync().AsTask();
            return await _updateDialogTask;
        }
        finally
        {
            if (_updateDialog == dialog)
            {
                _updateDialog = null;
                _updateDialogTask = null;
            }
        }
    }

    /// <summary>Closes the update dialog on screen, if any, and waits until it
    /// has gone. Closing the progress dialog cancels a download.</summary>
    private async Task DismissUpdateDialogAsync()
    {
        if (_updateDialog is not { } dialog || _updateDialogTask is not { } shown) return;
        // The progress dialog can't be closed while the download is unpacked.
        if (AppUpdater.Shared.InstallState == UpdateInstallState.Verifying) return;
        dialog.Hide();
        await shown;
    }

    /// <summary>Help › Check for Updates: always answers, including when there
    /// is nothing new or the check failed, and ignores a skipped version.</summary>
    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (IsContentDialogOpen()) return;
        var updater = AppUpdater.Shared;
        if (updater.InstallState == UpdateInstallState.WaitingForQuit)
        {
            await OfferStagedUpdateAsync();
            return;
        }
        if (updater.IsInstalling)
        {
            await ShowInstallProgressAsync();
            return;
        }

        AppRelease? release;
        try
        {
            release = await updater.CheckAsync();
        }
        catch (Exception)
        {
            await ShowUpdateMessageAsync("Couldn't check for updates", updater.CheckError ?? "The check failed.");
            return;
        }
        if (release != null)
            await PresentUpdateAsync(release, automatic: false);
        else
            await ShowUpdateMessageAsync("DSPi Console is up to date",
                $"Version {updater.CurrentVersionText} is the newest version available.");
    }

    private async Task ShowUpdateMessageAsync(string title, string message)
    {
        if (IsContentDialogOpen()) return;
        await ShowUpdateDialogAsync(new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = Content.XamlRoot });
    }

    /// <summary>An update is staged and waits for the quit: offers to quit now.</summary>
    private async Task OfferStagedUpdateAsync()
    {
        if (IsContentDialogOpen()) return;
        var updater = AppUpdater.Shared;
        var version = updater.Available?.Version?.ToString();
        var ready = new ContentDialog
        {
            Title = "Update ready",
            Content = (version != null ? $"DSPi Console {version} has" : "The update has")
                      + " been downloaded. It will be installed when DSPi Console quits.",
            PrimaryButtonText = "Install and Relaunch",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowUpdateDialogAsync(ready) == ContentDialogResult.Primary) updater.RetryQuit();
    }

    /// <summary>Offers <paramref name="release"/>: Install and Relaunch when it
    /// can be installed in place, otherwise View Release; then Skip This
    /// Version and Later.</summary>
    private async Task PresentUpdateAsync(AppRelease release, bool automatic)
    {
        // Hidden in the notification area: a dialog on the hidden window would
        // sit there unseen, so the icon says so and the offer waits for the
        // window to come back.
        var background = BackgroundMode.Shared;
        if (automatic && background.IsMinimised)
        {
            var name0 = release.Version?.ToString() ?? release.TagName;
            background.Notify($"DSPi Console {name0} is available", "Open DSPi Console to install it.");
            EventHandler? shown = null;
            shown = (_, _) =>
            {
                if (background.IsMinimised) return;
                background.Changed -= shown;
                DispatcherQueue.TryEnqueue(() => _ = PresentUpdateAsync(release, automatic: true));
            };
            background.Changed -= _deferredUpdate;
            _deferredUpdate = shown;
            background.Changed += shown;
            return;
        }
        // An automatic offer waits for any other dialog to close rather than
        // colliding with it, and gives up after a few minutes; the next daily
        // check offers it again.
        if (automatic && !await WaitForNoDialogAsync(TimeSpan.FromMinutes(3))) return;
        if (IsContentDialogOpen()) return;

        var updater = AppUpdater.Shared;
        if (updater.IsInstalling) return;
        bool signed = UpdateFeed.SignedZip(release) != null;
        bool replaceable = updater.CanReplace;
        bool installable = signed && replaceable;

        var name = release.Version?.ToString() ?? release.TagName;
        var text = $"You have version {updater.CurrentVersionText}. The new version includes matching DSPi firmware, "
                 + "which it will offer to install when your device is connected.";
        if (signed && !replaceable)
            text += "\n\nTo let DSPi Console update itself, move its folder somewhere you can write to, such as your Documents folder.";
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (installable && release.Page is { } page)
        {
            body.Children.Add(new HyperlinkButton
            {
                Content = "View release notes",
                NavigateUri = page,
                Padding = new Thickness(0),
            });
        }

        var dialog = new ContentDialog
        {
            Title = $"DSPi Console {name} is available",
            Content = body,
            PrimaryButtonText = installable ? "Install and Relaunch" : "View Release",
            SecondaryButtonText = "Skip This Version",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        switch (await ShowUpdateDialogAsync(dialog))
        {
            case ContentDialogResult.Primary when installable:
                await StartInstallAsync(release);
                break;
            case ContentDialogResult.Primary:
                await AppUpdater.OpenReleasePageAsync(release);
                break;
            case ContentDialogResult.Secondary:
                updater.Skip(release);
                break;
        }
    }

    private async Task StartInstallAsync(AppRelease release)
    {
        var install = AppUpdater.Shared.InstallAsync(release);
        // Downloading (or already failed) by the time InstallAsync first yields.
        await ShowInstallProgressAsync();
        await install;
    }

    /// <summary>The install's progress, with Cancel while downloading. Hides
    /// itself once the update is staged, so the quit's own prompts can open.</summary>
    private async Task ShowInstallProgressAsync()
    {
        var updater = AppUpdater.Shared;
        if (IsContentDialogOpen()) return;
        if (updater.InstallState is not (UpdateInstallState.Downloading or UpdateInstallState.Verifying or UpdateInstallState.Failed)) return;

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 340 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(status);
        panel.Children.Add(bar);
        var dialog = new ContentDialog { Title = "Software Update", Content = panel, XamlRoot = Content.XamlRoot };

        void Render()
        {
            switch (updater.InstallState)
            {
                case UpdateInstallState.Downloading:
                    status.Text = "Downloading update...";
                    bar.Visibility = Visibility.Visible;
                    bar.IsIndeterminate = false;
                    bar.Value = updater.Progress;
                    dialog.CloseButtonText = "Cancel";
                    break;
                case UpdateInstallState.Verifying:
                    status.Text = "Verifying update...";
                    bar.Visibility = Visibility.Visible;
                    bar.IsIndeterminate = true;
                    dialog.CloseButtonText = "";
                    break;
                case UpdateInstallState.Failed:
                    dialog.Title = "The update couldn't be installed";
                    status.Text = updater.Failure ?? "";
                    bar.Visibility = Visibility.Collapsed;
                    dialog.CloseButtonText = "Close";
                    break;
                default:
                    dialog.Hide();
                    break;
            }
        }

        void OnChanged(object? s, System.ComponentModel.PropertyChangedEventArgs a) => DispatcherQueue.TryEnqueue(Render);
        dialog.Closing += (_, args) =>
        {
            switch (updater.InstallState)
            {
                // Nothing to stop while the download is checked and unpacked.
                case UpdateInstallState.Verifying:
                    args.Cancel = true;
                    break;
                // Cancel at once, not once the dialog has gone: a download
                // that finishes meanwhile would otherwise go on to install.
                case UpdateInstallState.Downloading:
                    updater.CancelInstall();
                    break;
            }
        };
        Render();
        updater.PropertyChanged += OnChanged;
        try
        {
            await ShowUpdateDialogAsync(dialog);
        }
        finally
        {
            updater.PropertyChanged -= OnChanged;
        }
        // Closed by the user: dismiss a failure.
        updater.ClearFailure();
    }

    /// <summary>The update is staged: quit, so the launcher can install it and
    /// start the new version. The quit runs its usual guards and prompts
    /// (unsaved changes, pending Settings), and cancelling one keeps the update
    /// for the next quit (<see cref="QuitAbandoned"/>). A quit doesn't take
    /// another dialog away from the user: then the update waits too.</summary>
    private async void QuitForUpdate()
    {
        await DismissUpdateDialogAsync();
        if (IsContentDialogOpen())
        {
            QuitAbandoned();
            return;
        }
        RequestQuit();
    }

    /// <summary>A quit did not happen. If it was the one an install asked for,
    /// the update waits for the next quit, which installs it without reopening
    /// the app; the user is told once nothing else is on screen.</summary>
    private void QuitAbandoned()
    {
        var updater = AppUpdater.Shared;
        if (updater.InstallState != UpdateInstallState.Ready) return;
        updater.QuitCancelled();
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (await WaitForNoDialogAsync(TimeSpan.FromMinutes(10)))
                await ShowUpdateMessageAsync("Update ready", "The update will be installed when DSPi Console quits.");
        });
    }
}

using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole;

/// <summary>
/// The sidebar's side of the input source names and the board name (firmware
/// 1.1.6): the source picker shows the user's names, a right-click on it renames
/// the selected source, and the device picker shows each board's own name. After
/// the macOS Console; see the firmware's input_source_names_spec.md §8.
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeSourceNames()
    {
        ViewModel.InputSourceNamesChanged += RefreshSourceComboBox;
        ViewModel.DeviceNamesChanged += UpdateDeviceSelector;

        SourceComboBox.RightTapped += (_, e) =>
        {
            var menu = new MenuFlyout();
            BuildSourceMenu(menu);
            if (menu.Items.Count == 0) return;
            e.Handled = true;
            menu.ShowAt(SourceComboBox, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = e.GetPosition(SourceComboBox),
            });
        };
    }

    /// <summary>Rename for the selected source, and Use Built-in Name when it
    /// has a name of its own. Left empty, so no menu opens, when the firmware
    /// has no source names.</summary>
    private void BuildSourceMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        if (!ViewModel.IsDeviceConnected || !ViewModel.InputSourceNamesSupported) return;
        int source = (int)ViewModel.ActiveInputSource;
        string builtIn = ViewModel.BuiltInInputSourceTitle(source);

        var rename = new MenuFlyoutItem { Text = $"Rename {builtIn}…" };
        rename.Click += async (_, _) => await RenameSourceAsync(source);
        menu.Items.Add(rename);

        if (ViewModel.InputSourceNames[source].Length > 0)
        {
            var reset = new MenuFlyoutItem { Text = "Use Built-in Name" };
            reset.Click += async (_, _) => await CommitSourceNameAsync(source, "");
            menu.Items.Add(reset);
        }
    }

    private async Task RenameSourceAsync(int source)
    {
        var box = new TextBox
        {
            Text = ViewModel.InputSourceNames[source],
            PlaceholderText = ViewModel.BuiltInInputSourceTitle(source),
            MinWidth = 260,
        };
        // The same filtering as the Settings field: printable ASCII, 31 characters.
        NameField.Attach(box);
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };

        var dialog = new ContentDialog
        {
            Title = $"Rename {ViewModel.BuiltInInputSourceTitle(source)}",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    box,
                    new TextBlock
                    {
                        Text = "Shown in the sidebar, on the front panel and on every connected computer. An empty name uses the built-in one.",
                        FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                },
            },
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await CommitSourceNameAsync(source, box.Text);
    }

    /// <summary>Writes at once, and replaces any unsaved Settings draft for the
    /// same source so a later Save does not undo it.</summary>
    private async Task CommitSourceNameAsync(int source, string name)
    {
        _settingsWindow?.Tracker.Discard($"general.globals.source-name.{source}");
        if (!await ViewModel.SetInputSourceNameAsync(source, name))
            await ShowErrorDialog("The device did not accept the new name.");
    }
}

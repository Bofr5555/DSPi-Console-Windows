using DSPiConsole.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DSPiConsole.Dialogs;

public sealed partial class ChannelSelectionDialog : ContentDialog
{
    private readonly List<CheckBox> _checkboxes = new();

    public List<int> SelectedChannelIds { get; } = new();

    public ChannelSelectionDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Configure for single-channel import (REW format) - applies to any channel.
    /// </summary>
    public void ConfigureForSingleChannel(int filterCount, IReadOnlyList<Channel> activeInputs,
        IReadOnlyList<Channel> activeOutputs, Func<int, bool> isOutputEnabled,
        Func<Channel, string> getChannelName, float? preampDb = null)
    {
        Title = "Import Filters";
        // The preamp goes on each ticked input, so it is worth saying up front.
        string preamp = preampDb is { } db
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, " and a {0:+0.0;-0.0;+0.0} dB preamp", db)
            : "";
        MessageText.Text = $"Found {filterCount} filter(s){preamp}. Select which channel(s) to apply them to:";

        // Input channels — the base stereo pair is checked by default; the
        // extra multichannel inputs (RP2350) are offered but left unchecked.
        for (int i = 0; i < activeInputs.Count; i++)
            AddCheckbox(activeInputs[i], isChecked: i < 2, getChannelName(activeInputs[i]));

        // All output channels — unchecked
        for (int o = 0; o < activeOutputs.Count; o++)
            AddCheckbox(activeOutputs[o], isChecked: false, getChannelName(activeOutputs[o]));
    }

    /// <summary>
    /// Configure for multi-channel import (DSPi format) - shows all channels,
    /// pre-checks enabled ones that are present in the file.
    /// </summary>
    public void ConfigureForMultiChannel(IEnumerable<int> availableChannelIds,
        IReadOnlyList<Channel> activeInputs, IReadOnlyList<Channel> activeOutputs,
        Func<int, bool> isOutputEnabled, Func<Channel, string> getChannelName)
    {
        Title = "Import Filters";
        MessageText.Text = "This file contains filter settings for multiple channels. Select which channels to import:";
        var inFile = new HashSet<int>(availableChannelIds);

        // Input channels — checked if present in file
        foreach (var channel in activeInputs)
            AddCheckbox(channel, isChecked: inFile.Contains((int)channel.Id), getChannelName(channel));

        // All output channels — checked if present in file AND enabled
        for (int o = 0; o < activeOutputs.Count; o++)
            AddCheckbox(activeOutputs[o],
                isChecked: inFile.Contains((int)activeOutputs[o].Id) && isOutputEnabled(o),
                getChannelName(activeOutputs[o]));
    }

    /// <summary>
    /// Configure for AutoEQ profile application - groups channels by stereo pairs with custom names.
    /// </summary>
    public void ConfigureForAutoEQ(int filterCount, IReadOnlyList<Channel> activeInputs,
        IReadOnlyList<Channel> activeOutputs, Func<int, bool> isOutputEnabled,
        Func<Channel, string> getChannelName)
    {
        Title = "Apply AutoEQ Profile";
        PrimaryButtonText = "Apply";
        MessageText.Text = $"Select which channel(s) to apply {filterCount} filter(s) to:";

        // Inputs group — paired up like the outputs. Only the first pair is
        // checked by default; an AutoEQ curve targets one listening path.
        AddGroupHeader("Inputs");
        for (int i = 0; i < activeInputs.Count; i++)
        {
            if (i + 1 < activeInputs.Count)
            {
                AddStereoCheckbox(activeInputs[i], activeInputs[i + 1], getChannelName, isChecked: i == 0);
                i++; // skip partner
            }
            else
            {
                AddCheckbox(activeInputs[i], isChecked: i == 0, getChannelName(activeInputs[i]));
            }
        }

        // Outputs group
        AddGroupHeader("Outputs");
        for (int o = 0; o < activeOutputs.Count; o++)
        {
            var ch = activeOutputs[o];
            if (o + 1 < activeOutputs.Count &&
                (int)activeOutputs[o + 1].Id == (int)ch.Id + 1 &&
                isOutputEnabled(o) && isOutputEnabled(o + 1))
            {
                AddStereoCheckbox(ch, activeOutputs[o + 1], getChannelName, isChecked: false);
                o++; // skip partner
            }
            else if (isOutputEnabled(o))
            {
                AddCheckbox(ch, isChecked: false, getChannelName(ch));
            }
        }
    }

    private void AddGroupHeader(string text)
    {
        ChannelCheckboxes.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
    }

    private void AddStereoCheckbox(Channel left, Channel right,
        Func<Channel, string> getChannelName, bool isChecked)
    {
        var leftName = getChannelName(left);
        var rightName = getChannelName(right);
        var checkbox = new CheckBox
        {
            Content = $"{leftName} / {rightName}",
            Tag = new int[] { (int)left.Id, (int)right.Id },
            IsChecked = isChecked
        };
        _checkboxes.Add(checkbox);
        ChannelCheckboxes.Children.Add(checkbox);
    }

    private void AddCheckbox(Channel channel, bool isChecked, string? displayName = null)
    {
        var checkbox = new CheckBox
        {
            Content = displayName ?? channel.Name,
            Tag = (int)channel.Id,
            IsChecked = isChecked
        };
        _checkboxes.Add(checkbox);
        ChannelCheckboxes.Children.Add(checkbox);
    }

    public void CollectSelectedChannels()
    {
        SelectedChannelIds.Clear();
        foreach (var checkbox in _checkboxes)
        {
            if (checkbox.IsChecked != true) continue;
            if (checkbox.Tag is int channelId)
                SelectedChannelIds.Add(channelId);
            else if (checkbox.Tag is int[] channelIds)
                SelectedChannelIds.AddRange(channelIds);
        }
    }
}

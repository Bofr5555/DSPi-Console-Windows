using DSPiConsole.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Controls;

/// <summary>
/// The as-you-type rules for a field holding a device-stored name (an input
/// source name or the board's own name, firmware 1.1.6): printable ASCII only,
/// at most 31 characters. The length is the box's MaxLength, so a full field
/// refuses a keystroke rather than dropping its tail; anything else the board
/// cannot store is removed as it arrives, with the caret kept in place.
/// </summary>
public static class NameField
{
    /// <summary>Apply the rules to a box built in code.</summary>
    public static void Attach(TextBox box)
    {
        box.MaxLength = DeviceNameText.MaxLength;
        box.TextChanging += (sender, _) => Filter(sender);
    }

    /// <summary>The TextChanging step, for a box declared in XAML.</summary>
    public static void Filter(TextBox box)
    {
        var text = box.Text;
        var clean = DeviceNameText.Sanitize(text);
        if (clean == text) return;
        // The caret keeps its place among the characters that stayed.
        int caret = DeviceNameText.Sanitize(text[..Math.Clamp(box.SelectionStart, 0, text.Length)]).Length;
        box.Text = clean;
        box.SelectionStart = Math.Min(caret, clean.Length);
    }
}

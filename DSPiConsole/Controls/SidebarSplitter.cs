using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DSPiConsole.Controls;

/// <summary>
/// The sidebar's draggable edge, as the macOS Console's split view has: a thin
/// strip along the content's left side (inside its padding, so it covers
/// nothing of the sidebar's, its scroll bar included) that resizes the sidebar
/// column, shows the resize cursor, and returns to the default width on a
/// double-click. A drag's final width goes to <see cref="Committed"/>.
/// </summary>
public sealed class SidebarSplitter : Grid
{
    private readonly ColumnDefinition _column;
    private readonly double _defaultWidth;
    private double _startWidth, _startX;
    private bool _dragging, _moved;

    /// <summary>The drag ended (or the width was reset), with the new width.</summary>
    public event Action<double>? Committed;

    public SidebarSplitter(ColumnDefinition column, double defaultWidth)
    {
        _column = column;
        _defaultWidth = defaultWidth;
        Width = 6;
        HorizontalAlignment = HorizontalAlignment.Left;
        // Hit-testable but invisible.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        ToolTipService.SetToolTip(this, "Drag to resize the sidebar. Double-click for the default width.");
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => End();
        DoubleTapped += (_, _) => SetWidth(_defaultWidth, commit: true);
    }

    /// <summary>Applies a width within the column's limits.</summary>
    public void SetWidth(double width, bool commit = false)
    {
        _column.Width = new GridLength(Math.Clamp(width, _column.MinWidth, _column.MaxWidth));
        if (commit) Committed?.Invoke(_column.Width.Value);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = CapturePointer(e.Pointer);
        _moved = false;
        _startX = e.GetCurrentPoint(null).Position.X;
        _startWidth = _column.ActualWidth;
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        double dx = e.GetCurrentPoint(null).Position.X - _startX;
        if (Math.Abs(dx) >= 1) _moved = true;
        SetWidth(_startWidth + dx);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        ReleasePointerCapture(e.Pointer);
        End();
        e.Handled = true;
    }

    private void End()
    {
        if (!_dragging) return;
        _dragging = false;
        // A click that never moved changes nothing worth saving.
        if (_moved) Committed?.Invoke(_column.Width.Value);
    }
}

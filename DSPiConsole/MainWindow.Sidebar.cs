using DSPiConsole.Controls;
using DSPiConsole.Models;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole;

/// <summary>The sidebar's width: draggable at its edge, as the macOS Console's
/// split view is, and remembered between runs.</summary>
public sealed partial class MainWindow
{
    /// <summary>The width the sidebar opens at, and returns to on a double-click.</summary>
    private const double DefaultSidebarWidth = 280;

    private void InitializeSidebarSplitter()
    {
        if (SidebarGrid.Parent is not Grid host || host.ColumnDefinitions.Count < 2) return;
        var splitter = new SidebarSplitter(host.ColumnDefinitions[0], DefaultSidebarWidth);
        // On the content's edge, inside its padding, added last so it sits on top.
        Grid.SetColumn(splitter, 1);
        host.Children.Add(splitter);
        splitter.SetWidth(AppSettings.Instance.SidebarWidth > 0 ? AppSettings.Instance.SidebarWidth : DefaultSidebarWidth);
        splitter.Committed += width =>
        {
            AppSettings.Instance.SidebarWidth = Math.Round(width);
            AppSettings.Instance.Save();
        };
    }
}

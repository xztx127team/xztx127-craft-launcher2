using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Move the existing column, keeping bindings/animation clocks and page instances attached.</summary>
internal static class SidebarPlacementLayout
{
    internal static int Apply(Grid body, ColumnDefinition sidebarColumn, Border sidebar, FrameworkElement content,
        SidebarPlacement placement, bool drawerOpen, params FrameworkElement[] contentLayers)
    {
        var column = SidebarPreferences.SidebarColumn(placement);
        if (body.ColumnDefinitions.IndexOf(sidebarColumn) != column)
        {
            body.ColumnDefinitions.Remove(sidebarColumn);
            body.ColumnDefinitions.Insert(column, sidebarColumn);
        }
        var contentColumn = 1 - column;
        Grid.SetColumn(content, contentColumn);
        foreach (var layer in contentLayers) Grid.SetColumn(layer, contentColumn);
        Grid.SetColumn(sidebar, drawerOpen ? 0 : column);
        Grid.SetColumnSpan(sidebar, drawerOpen ? 2 : 1);
        sidebar.HorizontalAlignment = drawerOpen
            ? placement == SidebarPlacement.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left
            : HorizontalAlignment.Stretch;
        sidebar.BorderThickness = placement == SidebarPlacement.Right
            ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
        return contentColumn;
    }
}

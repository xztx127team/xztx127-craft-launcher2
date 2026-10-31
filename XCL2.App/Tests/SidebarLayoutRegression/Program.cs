using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 0.01;

    [STAThread]
    private static void Main()
    {
        var body = new Grid();
        var sidebarColumn = new ColumnDefinition { Width = new GridLength(180) };
        var pageColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        body.ColumnDefinitions.Add(sidebarColumn); body.ColumnDefinitions.Add(pageColumn);
        var sidebar = new Border();
        var page = new Border { Child = new TextBox { Text = "保留中的输入" } };
        var ai = new Canvas(); var reminder = new Border(); var debug = new Border();
        foreach (var element in new FrameworkElement[] { page, sidebar, ai, reminder, debug }) body.Children.Add(element);

        foreach (var width in new[] { 420d, 500, 760, 1280, 1912 })
        foreach (var placement in new[] { SidebarPlacement.Left, SidebarPlacement.Right, SidebarPlacement.Left, SidebarPlacement.Right })
        foreach (var compactDrawer in new[] { false, true })
        {
            sidebarColumn.Width = new GridLength(compactDrawer ? 56 : 180);
            sidebar.Width = compactDrawer ? 180 : double.NaN;
            var contentColumn = SidebarPlacementLayout.Apply(body, sidebarColumn, sidebar, page, placement, compactDrawer, ai, reminder, debug);
            body.Measure(new Size(width, 600)); body.Arrange(new Rect(0, 0, width, 600)); body.UpdateLayout();
            var contentX = page.TranslatePoint(new Point(), body).X;
            var sidebarX = sidebar.TranslatePoint(new Point(), body).X;
            var railWidth = compactDrawer ? 56 : 180;
            Check(body.ColumnDefinitions.Count == 2 && ReferenceEquals(body.ColumnDefinitions[contentColumn], pageColumn), "Switching sides never clones or loses column definitions.");
            Check(Near(page.ActualWidth, width - railWidth), "Content keeps all space outside the actual sidebar rail.");
            Check(Near(contentX, placement == SidebarPlacement.Right ? 0 : railWidth), "Page position follows the selected side.");
            Check(Near(sidebarX, placement == SidebarPlacement.Right ? width - 180 : 0), "Compact drawers open from the selected edge.");
            Check(new FrameworkElement[] { ai, reminder, debug }.All(layer => Grid.GetColumn(layer) == contentColumn && Near(layer.ActualWidth, page.ActualWidth)), "Drawers, reminders and debug content remain in the page column.");
            Check(((TextBox)page.Child).Text == "保留中的输入", "Layout changes preserve the live page and user input.");
            Check(page.FlowDirection == FlowDirection.LeftToRight, "A right sidebar does not mirror text, numbers or controls.");
        }
        Console.WriteLine($"Sidebar layout: {_checks} WPF layout checks passed.");
    }
}

using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

/// <summary>在当前弹窗上方阅读文档，避免导航到了仍被父弹窗遮住的页面。</summary>
internal sealed class PluginApiDocumentationDialog : OverlayDialogControl
{
    internal PluginApiDocumentationDialog()
    {
        Width = 1000;
        Height = 700;
        var root = new DockPanel();
        var close = new Button
        {
            Content = "关闭开发文档",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 10, 16, 0),
            Padding = new Thickness(12, 6, 12, 6)
        };
        close.Click += (_, _) => CloseWith(null);
        DockPanel.SetDock(close, Dock.Top);
        root.Children.Add(close);
        root.Children.Add(new PluginApiDocumentationPage());
        Content = root;
    }
}

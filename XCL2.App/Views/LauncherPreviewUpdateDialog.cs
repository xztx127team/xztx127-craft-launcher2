using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

internal enum LauncherPreviewChoice { Ignore, SuppressVersion, Install }

internal sealed class LauncherPreviewUpdateDialog : OverlayDialogControl
{
    internal LauncherPreviewChoice Choice { get; private set; } = LauncherPreviewChoice.Ignore;
    internal LauncherPreviewUpdateDialog(string tag, string current, string changelog, string? installUnavailable)
    {
        Width = 590;
        MaxWidth = Math.Max(300, (Application.Current.MainWindow?.ActualWidth ?? 760) - 64);
        MaxHeight = Math.Max(260, (Application.Current.MainWindow?.ActualHeight ?? 640) - 64);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "有新的启动器预览版", FontSize = 19, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = $"当前 v{current} → {tag}", Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = "预览版可能仍有问题，安装需要您的确认。忽略只跳过本次；“本次版本不再提示”只影响该预览版本，后续更高版本仍会提醒。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new ScrollViewer { MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = changelog, TextWrapping = TextWrapping.Wrap } });
        if (!string.IsNullOrWhiteSpace(installUnavailable))
            root.Children.Add(new TextBlock { Text = installUnavailable, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        void Add(string label, LauncherPreviewChoice choice, bool enabled = true)
        {
            var button = new Button { Content = label, IsEnabled = enabled, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(7, 0, 0, 6) };
            button.Click += (_, _) => { Choice = choice; CloseWith(choice == LauncherPreviewChoice.Install); };
            buttons.Children.Add(button);
        }
        Add("本次版本不再提示", LauncherPreviewChoice.SuppressVersion);
        Add("忽略", LauncherPreviewChoice.Ignore);
        Add("安装", LauncherPreviewChoice.Install, string.IsNullOrWhiteSpace(installUnavailable));
        root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

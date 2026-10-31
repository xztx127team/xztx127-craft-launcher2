using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

/// <summary>No dismiss/skip button; failed downloads keep the gate visible with a retry action.</summary>
internal sealed class LauncherRequiredUpdateDialog : OverlayDialogControl
{
    internal LauncherRequiredUpdateDialog(string tag, string reason, string changelog,
        Func<Task<string>> install, Action exit, Func<Task<bool>> recheck)
    {
        Width = 590;
        MaxWidth = Math.Max(300, (Application.Current.MainWindow?.ActualWidth ?? 760) - 64);
        MaxHeight = Math.Max(260, (Application.Current.MainWindow?.ActualHeight ?? 640) - 64);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "需要更新启动器", FontSize = 20, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = $"{tag}\n{reason}\n完成安装后才能继续使用启动器。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        root.Children.Add(new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = changelog, TextWrapping = TextWrapping.Wrap } });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
        root.Children.Add(status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var quit = new Button { Content = "退出启动器", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        quit.Click += (_, _) => exit();
        var update = new Button { Content = "安装更新", Padding = new Thickness(12, 7, 12, 7) };
        var refresh = new Button { Content = "重新检查要求", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        update.Click += async (_, _) =>
        {
            refresh.IsEnabled = update.IsEnabled = quit.IsEnabled = false;
            status.Text = "正在下载并校验更新…";
            try { status.Text = await install(); }
            catch (Exception ex) { status.Text = "更新未完成，请重试：" + ex.Message; }
            finally { refresh.IsEnabled = update.IsEnabled = quit.IsEnabled = true; update.Content = "重试安装"; }
        };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = update.IsEnabled = quit.IsEnabled = false;
            try
            {
                status.Text = "正在重新检查发布者的更新要求…";
                if (await recheck()) { Close(); return; }
                status.Text = "仍需安装更新，或暂时无法联网确认。可以重试安装或退出。";
            }
            catch (Exception ex) { status.Text = "重新检查失败：" + ex.Message; }
            finally { refresh.IsEnabled = update.IsEnabled = quit.IsEnabled = true; }
        };
        buttons.Children.Add(refresh); buttons.Children.Add(quit); buttons.Children.Add(update); root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

internal static class AiTermsRevocationConfirmation
{
    internal static bool Confirm(Window owner)
        => Ask(owner, "关闭后将停止 AI 助手的当前请求，并返回首页。要拒绝 AI 助手的用户协议与条款吗？", "关闭 AI 助手 · 第一次确认")
           && Ask(owner, "确认撤回同意？下次打开 AI 助手需要重新阅读并同意协议。已有本地对话历史会保留。", "关闭 AI 助手 · 第二次确认");

    internal static void ShowError(Window owner, string message)
    {
        if (owner is MainWindow)
            MessageBoxDialog.ShowError(message, "关闭 AI 助手");
        else
            MessageBox.Show(owner, message, "关闭 AI 助手", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool Ask(Window owner, string message, string title)
    {
        if (owner is MainWindow)
            return MessageBoxDialog.ShowCustomYesNo(message, title, "继续使用", "确认关闭", rightIsPrimary: false);
        // A native modal settings window disables its owner. Host confirmations in that window
        // instead of putting an unreachable launcher overlay behind it.
        var dialog = new Window { Owner = owner, Title = title, Width = Math.Min(460, SystemParameters.WorkArea.Width - 40),
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(Window.BackgroundProperty, "PanelBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        var body = new StackPanel { Margin = new Thickness(22) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 20) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "继续使用", IsCancel = true, IsDefault = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "确认关闭", Padding = new Thickness(14, 7, 14, 7) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); body.Children.Add(buttons);
        dialog.Content = body;
        return dialog.ShowDialog() == true;
    }
}

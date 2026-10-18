using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class FriendlyErrorDialog : OverlayDialogControl
{
    public FriendlyErrorDialog(string summary, string detail, string title)
    {
        MinWidth = 340; MaxWidth = 640;
        MaxHeight = Math.Max(300, Math.Min(600, (Application.Current.MainWindow?.ActualHeight ?? 740) - 100));
        var code = GetCode(summary + "\n" + detail);
        var safeSummary = DiagnosticRedactionService.Sanitize(summary, 4000);
        var safeDetail = DiagnosticRedactionService.Sanitize(detail, 16000);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = DiagnosticRedactionService.Sanitize(title, 256), FontWeight = FontWeights.Bold, FontSize = 18, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = safeSummary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) });
        root.Children.Add(new TextBlock { Text = "错误编号：" + code, Margin = new Thickness(0, 0, 0, 8) });
        var details = new TextBox { Text = safeDetail, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        details.SetResourceReference(Control.BackgroundProperty, "SideBrush");
        details.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        root.Children.Add(new Expander { Header = "查看脱敏后的技术详情", Content = details });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; root.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "复制错误说明", MinWidth = 120, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText($"{code}\n{safeSummary}\n\n{safeDetail}"); status.Text = "已复制脱敏后的错误说明。"; } catch { status.Text = "剪贴板暂时不可用，请重试。"; } };
        var close = new Button { Content = "关闭", MinWidth = 80, Padding = new Thickness(12, 7, 12, 7) };
        close.SetResourceReference(StyleProperty, "PrimaryButton"); close.Click += (_, _) => Close();
        actions.Children.Add(copy); actions.Children.Add(close); root.Children.Add(actions);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    public static string GetCode(string evidence)
    {
        var text = evidence.ToLowerInvariant();
        if (text.Contains("unauthorized") || text.Contains("权限") || text.Contains("拒绝访问")) return "XCL-PERMISSION";
        if (text.Contains("java") || text.Contains("jvm")) return "XCL-JAVA";
        if (text.Contains("http") || text.Contains("网络") || text.Contains("timeout") || text.Contains("下载")) return "XCL-NETWORK";
        if (text.Contains("文件") || text.Contains("目录") || text.Contains("ioexception")) return "XCL-FILE";
        return "XCL-OPERATION";
    }
    public static void Show(string summary, string detail, string title) => OverlayDialogService.ShowModal(new FriendlyErrorDialog(summary, detail, title));
}

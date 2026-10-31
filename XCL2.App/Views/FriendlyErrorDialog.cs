using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class FriendlyErrorDialog : OverlayDialogControl
{
    public FriendlyErrorDialog(string summary, string detail, string title)
        : this(ErrorReport.Create(summary, detail, title), false) { }

    public FriendlyErrorDialog(ErrorReport report, bool logged)
    {
        MinWidth = 340; MaxWidth = 680;
        MaxHeight = Math.Max(300, Math.Min(680, (Application.Current.MainWindow?.ActualHeight ?? 800) - 100));
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = report.Title, FontWeight = FontWeights.Bold, FontSize = 18, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = report.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) });
        root.Children.Add(new TextBlock { Text = $"错误代码：{report.Code}\n报告编号：{report.IncidentId}\n发生时间：{report.Time:yyyy-MM-dd HH:mm:ss zzz}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = "错误原因", FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock { Text = report.Cause, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });
        root.Children.Add(new TextBlock { Text = "处理方法", FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock { Text = report.Guidance, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });
        root.Children.Add(new TextBlock { Text = logged
            ? "本次报告已写入本地日志。可复制或保存下方预览，反馈时补充操作步骤、预期结果与实际结果。"
            : "本地日志未能写入。请复制或保存本次报告，避免关闭窗口后丢失诊断信息。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var details = new TextBox { Text = report.ToText(), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        details.SetResourceReference(Control.BackgroundProperty, "SideBrush");
        details.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        root.Children.Add(new Expander { Header = "预览脱敏报告（复制和保存使用此内容）", Content = details });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; root.Children.Add(status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Button Add(string label, RoutedEventHandler action)
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 8, 6) };
            button.Click += action; actions.Children.Add(button); return button;
        }
        Add("复制报告", (_, _) => { try { Clipboard.SetText(details.Text); status.Text = "已复制脱敏报告。"; } catch { status.Text = "剪贴板暂时不可用，请重试或保存报告。"; } });
        Add("保存报告…", (_, _) =>
        {
            var picker = new SaveFileDialog { Title = "保存脱敏错误报告", FileName = $"XCL2-error-{report.Time:yyyyMMdd-HHmmss}-{report.IncidentId[..8]}.txt", Filter = "文本报告|*.txt", AddExtension = true };
            if (picker.ShowDialog() != true) return;
            try { File.WriteAllText(picker.FileName, details.Text, System.Text.Encoding.UTF8); status.Text = "脱敏报告已保存。"; }
            catch (Exception ex) { status.Text = "保存失败：" + DiagnosticRedactionService.Sanitize(ex.Message, 1000); }
        });
        Add("问题反馈", (_, _) => ExternalLinkService.Open(ErrorPresenter.GitHubRepoUrl + "/issues/new"));
        Add("关闭", (_, _) => Close()).SetResourceReference(StyleProperty, "PrimaryButton");
        root.Children.Add(actions);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public static string GetCode(string evidence) => "XCL-" + ErrorReport.Classify(evidence).Category;
    public static void Show(ErrorReport report, bool logged) => OverlayDialogService.ShowModal(new FriendlyErrorDialog(report, logged));
    public static void Show(string summary, string detail, string title)
    {
        var report = ErrorReport.Create(summary, detail, title);
        Show(report, ErrorPresenter.TryLogTechnicalDetail(report.ToText()));
    }
}

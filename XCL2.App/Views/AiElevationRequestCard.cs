using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>Shares one completion with the desktop notice; approval never changes saved permissions.</summary>
internal sealed class AiElevationRequestCard : Border
{
    private readonly TaskCompletionSource<AiElevationDecision> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsPending => !_completion.Task.IsCompleted;
    public Task<AiElevationDecision> Completion => _completion.Task;

    public AiElevationRequestCard(AiElevationRequest request)
    {
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(14);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(BorderBrushProperty, "AccentBrush");
        var rows = new StackPanel();
        var title = Text("AI 助手申请管理员权限", 15);
        title.FontWeight = FontWeights.SemiBold;
        rows.Children.Add(title);
        rows.Children.Add(Text("原命令遇到 Windows 权限不足。允许后将显示系统 UAC 确认，并完整重试这条命令；原尝试可能已产生部分修改。", 12));
        rows.Children.Add(Text($"Shell：{request.Shell}  ·  超时：{request.TimeoutSeconds} 秒\n工作目录：{request.Directory}", 11));
        var command = new TextBox { Text = request.Command, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 6) };
        rows.Children.Add(command);
        rows.Children.Add(new Expander { Header = "查看普通权限失败原因", Margin = new Thickness(0, 3, 0, 5),
            Content = Text(request.PreviousFailure.Length > 3000 ? request.PreviousFailure[..3000] + "…" : request.PreviousFailure, 11) });
        rows.Children.Add(Text("“允许全部”仅对本次打开期间的当前对话有效。其他命令仍须通过原有命令审批、模式及权限检查；每次系统 UAC 仍由 Windows 决定。", 11));
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        AddButton(buttons, "允许一次", AiElevationDecision.AllowOnce);
        AddButton(buttons, "允许全部", AiElevationDecision.AllowSession);
        AddButton(buttons, "不允许", AiElevationDecision.Deny);
        rows.Children.Add(buttons);
        Child = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 };
    }

    private static TextBlock Text(string text, double size)
    {
        var value = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
        value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return value;
    }

    private void AddButton(Panel panel, string label, AiElevationDecision decision)
    {
        var button = new Button { Content = label, Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(5, 0, 0, 0) };
        button.Click += (_, _) => Resolve(decision);
        panel.Children.Add(button);
    }

    public void Resolve(AiElevationDecision decision)
    {
        if (_completion.TrySetResult(decision)) IsEnabled = false;
    }
    public void Cancel(CancellationToken token) => _completion.TrySetCanceled(token);
}

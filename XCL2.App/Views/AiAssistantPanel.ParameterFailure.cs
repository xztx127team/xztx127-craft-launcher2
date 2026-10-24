using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantPanel
{
    private Border? _parameterFailureCard;
    private readonly Dictionary<string, AiParameterAdaptationException> _parameterFailures = new();
    private void ShowParameterFailure()
    {
        if (Content is not Grid host) return;
        if (_parameterFailureCard != null) host.Children.Remove(_parameterFailureCard);
        _parameterFailureCard = null;
        if (!_parameterFailures.TryGetValue(_session.Id, out var failure)) return;
        var sessionId = _session.Id;
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "此模型参数适配未完成", FontWeight = FontWeights.SemiBold, FontSize = 15 });
        body.Children.Add(new TextBlock { Text = failure.ModelName + "\n" + failure.Message,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 100, Margin = new Thickness(0, 8, 0, 8) });
        body.Children.Add(new TextBlock { Text = "建议检查此模型的接口请求文档与参数规则。不确定时，可交给其他 AI 分析。",
            TextWrapping = TextWrapping.Wrap });
        if (failure.Attempts.Count > 0) body.Children.Add(new TextBlock {
            Text = "已尝试：" + string.Join("；", failure.Attempts.Select(AiParameterAdaptationException.Describe)), TextWrapping = TextWrapping.Wrap });
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; body.Children.Add(actions);
        void Edit(bool analyze)
        {
            var dialog = new AiModelRequestDialog(_config, failure.ModelKey, failure.Message, analyze);
            var accepted = dialog.ShowDialog();
            if (dialog.AnalysisRequested) { _config.DiagnosticModelKey = dialog.DiagnosticModelKey; PersistQuickConfig(); }
            if (accepted == true && dialog.Result != null)
            {
                _config.ModelRequestProfiles ??= new();
                _config.ModelRequestProfiles[failure.ModelKey] = dialog.Result;
                _config.DiagnosticModelKey = dialog.DiagnosticModelKey;
                _service?.UpdateConfig(_config); PersistQuickConfig();
                if (dialog.ResultWasTested) _parameterFailures.Remove(sessionId);
                ShowParameterFailure();
                ToastService.ShowInfo(dialog.ResultWasTested ? "规则测试通过并已保存。可重新发送刚才的问题。" : "请求文档已保存，生效规则未改变。");
            }
        }
        foreach (var title in new[] { "编辑请求文档", "交给其他 AI 分析" })
        { var b = new Button { Content = title, Margin = new Thickness(0, 0, 6, 4) }; b.Click += (_, _) => Edit(title == "交给其他 AI 分析"); actions.Children.Add(b); }
        var retry = new Button { Content = "修正后重试", Margin = new Thickness(0, 0, 6, 4) };
        retry.Click += async (_, _) =>
        {
            if (_sending || _session.Id != sessionId) return;
            var user = _session.Messages.LastOrDefault(m => m.Role == XCL2.App.Models.AiMessageRole.User);
            if (user == null) return;
            // Explicit user action only. Previous activity/history remains visible.
            await GenerateAsync(user.Content, false, user.Attachments?.Select(a => a.Clone()).ToList());
        };
        actions.Children.Add(retry);
        var close = new Button { Content = "关闭", Margin = new Thickness(0, 0, 0, 4) };
        close.Click += (_, _) => { _parameterFailures.Remove(sessionId); ShowParameterFailure(); }; actions.Children.Add(close);
        _parameterFailureCard = new Border { Child = body, Padding = new Thickness(16), Margin = new Thickness(16),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        _parameterFailureCard.SetResourceReference(BackgroundProperty, "PanelBrush");
        _parameterFailureCard.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        Grid.SetRowSpan(_parameterFailureCard, 2); Panel.SetZIndex(_parameterFailureCard, 20); host.Children.Add(_parameterFailureCard);
    }
}

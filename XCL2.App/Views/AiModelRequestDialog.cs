using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>Reviewable per-model documentation, diagnostics and request rules. Never executes AI output.</summary>
internal sealed class AiModelRequestDialog : Window
{
    private readonly AiAssistantConfig _config;
    private readonly string _modelKey;
    private readonly string _error;
    private readonly TextBox _docs = Editor(120);
    private readonly TextBox _rules = Editor(150);
    private readonly TextBox _diagnostic = Editor(100);
    private readonly TextBox _analysis = Editor(150);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly ComboBox _diagnosticModel = new() { DisplayMemberPath = "DisplayName", SelectedValuePath = "ModelKey", MinWidth = 200 };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Button> _operationButtons = new();
    public AiModelRequestProfile? Result { get; private set; }
    public bool ResultWasTested { get; private set; }
    public bool AnalysisRequested { get; private set; }
    public string DiagnosticModelKey => _diagnosticModel.SelectedValue as string ?? "";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public AiModelRequestDialog(AiAssistantConfig config, string modelKey, string error = "", bool analyzeOnOpen = false)
    {
        _config = config; _modelKey = modelKey; _error = error;
        Title = "此模型的接口请求文档与参数规则";
        Width = 780; Height = 810; MinWidth = 560; MinHeight = 440;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var stack = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var catalog = AiModelIds.GetCatalog(config);
        var selected = catalog.FirstOrDefault(m => m.ModelKey == modelKey);
        stack.Children.Add(new TextBlock { Text = selected == null ? modelKey : selected.ProviderName + " · " + selected.DisplayName,
            FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Label("请求文档：粘贴此模型的参数说明。不要填写密钥。文本不会自动执行。", stack);
        stack.Children.Add(_docs);
        Label("生效规则 JSON：条件 any / thinking / off。留空规则沿用原设置。", stack);
        stack.Children.Add(_rules);
        var profile = AiModelParameterPolicy.Profile(config, modelKey).Clone();
        _docs.Text = profile.Documentation;
        PutRules(profile);
        Label("其他 AI 分析（仅分析，不执行工具；会使用所选接口的额度）", stack);
        var failedTarget = AiEndpointRouting.GetCandidates(config, modelKey).FirstOrDefault();
        var choices = catalog.Where(m => m.ModelKey != modelKey &&
            AiEndpointRouting.GetCandidates(config, m.ModelKey).FirstOrDefault() is { } t &&
            (failedTarget == null || AiModelParameterMemory.ModelScope(t) != AiModelParameterMemory.ModelScope(failedTarget))).ToList();
        _diagnosticModel.ItemsSource = choices;
        _diagnosticModel.SelectedValue = choices.Any(m => m.ModelKey == config.DiagnosticModelKey)
            ? config.DiagnosticModelKey : choices.FirstOrDefault()?.ModelKey;
        stack.Children.Add(_diagnosticModel);
        Label("将发送的脱敏诊断（可编辑；不含聊天记录）", stack);
        stack.Children.Add(_diagnostic);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        stack.Children.Add(actions);
        AddButton(actions, "刷新脱敏诊断", (_, _) => RefreshDiagnostic());
        AddButton(actions, "复制诊断", (_, _) => { try { Clipboard.SetText(_diagnostic.Text); } catch { _status.Text = "复制失败，请手动选择文本复制。"; } });
        AddButton(actions, "交给其他 AI 分析", Analyze);
        _analysis.IsReadOnly = true;
        stack.Children.Add(_analysis);
        AddButton(stack, "把建议放入编辑框（待审核）", (_, _) => ImportSuggestion());
        stack.Children.Add(_status);
        var bottom = new WrapPanel(); stack.Children.Add(bottom);
        AddButton(bottom, "应用并测试此模型", ApplyAndTest);
        AddButton(bottom, "仅保存文档", (_, _) =>
        {
            var saved = AiModelParameterPolicy.Profile(config, modelKey).Clone();
            saved.Documentation = _docs.Text;
            if (AiModelParameterPolicy.Validate(saved) is { } invalid) { _status.Text = invalid; return; }
            Result = saved; ResultWasTested = false; DialogResult = true;
        });
        AddButton(bottom, "清除此模型自动适配记录", (_, _) =>
        {
            try
            {
                using var service = new AiAssistantService(config, App.DataDir);
                service.ClearModelCompatibility(modelKey);
                _status.Text = "此模型的自动适配记录已清除，下次请求重新验证。";
            }
            catch { _status.Text = "无法清除记录，请检查模型和配置目录。"; }
        });
        var close = new Button { Content = "取消", Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6) };
        close.Click += (_, _) => Close(); bottom.Children.Add(close);
        Closed += (_, _) => _lifetime.Cancel();
        RefreshDiagnostic();
        if (analyzeOnOpen) Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(config.DiagnosticModelKey) && DiagnosticModelKey == config.DiagnosticModelKey)
                Analyze(this, new RoutedEventArgs());
            else { _diagnosticModel.Focus(); _status.Text = "首次使用请选择诊断模型，查看脱敏内容后点击分析。"; }
        };
    }

    private static TextBox Editor(double height) => new() { Height = height, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 5, 0, 8) };
    private static void Label(string text, Panel parent) => parent.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 3) });
    private void AddButton(Panel parent, string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += handler; parent.Children.Add(button); _operationButtons.Add(button);
    }
    private void Busy(bool busy) { foreach (var button in _operationButtons) button.IsEnabled = !busy; }
    private AiModelRequestProfile ReadProfile()
    {
        if (_rules.Text.Length > 32000) throw new InvalidOperationException("规则内容过长。");
        var p = JsonSerializer.Deserialize<AiModelRequestProfile>(_rules.Text, Json) ?? new();
        p.Documentation = _docs.Text;
        if (AiModelParameterPolicy.Validate(p) is { } error) throw new InvalidOperationException(error);
        return p;
    }
    private void PutRules(AiModelRequestProfile p) => _rules.Text = JsonSerializer.Serialize(new { p.ThinkingProtocol, p.SupportedEfforts, p.Rules }, Json);
    private void RefreshDiagnostic()
    {
        try { using var service = new AiAssistantService(_config, App.DataDir); _diagnostic.Text = service.PrepareModelDiagnostic(_modelKey, _error, ReadProfile()); }
        catch (Exception ex) { _status.Text = "无法生成诊断：" + ex.Message; }
    }
    private async void Analyze(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DiagnosticModelKey)) { _status.Text = "没有其他可用模型；可复制脱敏诊断交给其他 AI。"; return; }
        AnalysisRequested = true;
        Busy(true); _status.Text = "正在分析，结果不会自动应用……";
        try
        {
            using var service = new AiAssistantService(_config, App.DataDir);
            // Re-scrub edited previews before transmitting them.
            var safe = service.PrepareModelDiagnostic(_modelKey, _diagnostic.Text, new());
            _analysis.Text = await service.AnalyzeModelRequestAsync(DiagnosticModelKey, _modelKey, safe, _lifetime.Token);
            _status.Text = "分析完成。请审核建议后放入编辑框，再应用并测试。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "分析失败，不再自动转交其他模型：" + ex.Message; }
        finally { Busy(false); }
    }
    private void ImportSuggestion()
    {
        try
        {
            var match = Regex.Match(_analysis.Text, @"```(?:json)?\s*(\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
            var text = match.Success ? match.Groups[1].Value : _analysis.Text.Trim();
            if (text.Length > 32000) throw new InvalidOperationException("建议过长。");
            var p = JsonSerializer.Deserialize<AiModelRequestProfile>(text, Json) ?? throw new InvalidOperationException("未提供规则 JSON。");
            if (AiModelParameterPolicy.Validate(p) is { } error) throw new InvalidOperationException(error);
            PutRules(p);
            _status.Text = "建议已放入编辑框，尚未生效。请检查参数变更。";
        }
        catch (Exception ex) { _status.Text = "无法导入建议：" + ex.Message; }
    }
    private async void ApplyAndTest(object sender, RoutedEventArgs e)
    {
        Busy(true);
        try
        {
            var profile = ReadProfile();
            var draft = JsonSerializer.Deserialize<AiAssistantConfig>(JsonSerializer.Serialize(_config))!;
            draft.ModelRequestProfiles ??= new(); draft.ModelRequestProfiles[_modelKey] = profile;
            draft.EnableEndpointFailover = false;
            _status.Text = "正在使用当前规则和思考档位测试此模型……";
            using var service = new AiAssistantService(draft, App.DataDir);
            var result = await service.TestModelAsync(_modelKey, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            if (!result.Ok) { _status.Text = "测试失败，规则未应用：" + result.Message; return; }
            Result = profile; ResultWasTested = true; DialogResult = true;
        }
        catch (Exception ex) { _status.Text = "规则未应用：" + ex.Message; }
        finally { Busy(false); }
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Services;

namespace XCL2.App.Views.Tools;

/// <summary>Shared accessible input/output shell. Computation is explicit and never runs a command.</summary>
public sealed class UtilityToolPanel : UserControl
{
    private readonly UtilityToolDefinition _definition;
    private readonly List<TextBox> _inputs = new();
    private readonly List<TextBlock> _errors = new();
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, MinHeight = 180, MaxHeight = 500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap, Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _run = new() { Content = "计算 / 转换", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 8) };
    private readonly Button _copy = new() { Content = "复制结果", IsEnabled = false, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 8) };
    private bool _running;
    private CancellationTokenSource? _operation;

    public UtilityToolPanel(UtilityToolDefinition definition)
    {
        _definition = definition;
        Unloaded += (_, _) => _operation?.Cancel();
        var panel = new StackPanel { Margin = new Thickness(4, 12, 4, 4), MaxWidth = 960 };
        panel.Children.Add(new TextBlock { Text = definition.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var description = new TextBlock { Text = definition.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(description);
        for (int i = 0; i < definition.Fields.Length; i++)
        {
            var field = definition.Fields[i];
            panel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 0, 0, 4) });
            var input = new TextBox { Text = field.Example, AcceptsReturn = field.Multiline, AcceptsTab = field.Multiline, MinHeight = field.Multiline ? 100 : 34, MaxHeight = field.Multiline ? 240 : 100, TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 6, 8, 6), MaxLength = 262144 };
            AutomationProperties.SetName(input, field.Label);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 8) };
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
            input.TextChanged += (_, _) =>
            {
                if (_running) return;
                error.Visibility = Visibility.Collapsed;
                input.ClearValue(Control.BorderBrushProperty);
                _copy.IsEnabled = false;
                _status.Text = "输入已更改，请重新计算。";
            };
            _inputs.Add(input); _errors.Add(error);
            panel.Children.Add(input); panel.Children.Add(error);
            panel.Children.Add(new Border { Height = 8 });
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        actions.Children.Add(_run); actions.Children.Add(_copy);
        _run.Click += Run_Click;
        _copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_output.Text); ToastService.ShowSuccess("结果已复制"); }
            catch (Exception ex) { _status.Text = "复制失败：" + ex.Message; ToastService.ShowWarning("剪贴板暂时不可用，可在结果框中选择文本后复制。"); }
        };
        panel.Children.Add(actions); panel.Children.Add(_status);
        panel.Children.Add(new TextBlock { Text = "输出", FontWeight = FontWeights.SemiBold });
        AutomationProperties.SetName(_output, definition.Title + "输出");
        panel.Children.Add(_output);
        panel.Children.Add(new TextBlock { Text = "所有处理均在本机完成。工具只生成文本结果，不会执行输出中的命令。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 8) });
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        _operation = new CancellationTokenSource();
        var cancellationToken = _operation.Token;
        _run.IsEnabled = false; _copy.IsEnabled = false;
        foreach (var input in _inputs) { input.IsEnabled = false; input.ClearValue(Control.BorderBrushProperty); }
        foreach (var error in _errors) error.Visibility = Visibility.Collapsed;
        string[] snapshot = _inputs.Select(x => x.Text).ToArray();
        _status.Text = "正在处理…";
        try
        {
            string result = await Task.Run(() => UtilityToolCatalog.Run(_definition.Id, snapshot, cancellationToken));
            _output.Text = result;
            _copy.IsEnabled = result.Length > 0;
            _status.Text = result.Length == 0 ? "已完成，结果为空。" : $"已完成，输出 {result.Length:N0} 个字符。";
        }
        catch (OperationCanceledException)
        {
            _output.Clear();
            _status.Text = "已取消，请重新计算。";
        }
        catch (UtilityToolInputException ex)
        {
            _output.Clear();
            int index = Math.Clamp(ex.FieldIndex, 0, _inputs.Count - 1);
            _errors[index].Text = ex.Message; _errors[index].Visibility = Visibility.Visible;
            _inputs[index].BorderBrush = Brushes.IndianRed;
            _status.Text = "未生成结果，请修改红字对应的输入。";
            ToastService.ShowWarning(_definition.Title + "：" + ex.Message);
            _inputs[index].BringIntoView();
        }
        catch (Exception ex)
        {
            _output.Clear();
            _status.Text = "处理失败：" + ex.Message;
            ToastService.ShowWarning(_status.Text);
        }
        finally
        {
            _operation.Dispose(); _operation = null;
            _running = false; _run.IsEnabled = true;
            foreach (var input in _inputs) input.IsEnabled = true;
        }
    }
}

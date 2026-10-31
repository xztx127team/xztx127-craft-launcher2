using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantSettingsPanel
{
    private Dictionary<string, AiModelRequestProfile> _requestProfiles = new();
    private string _diagnosticModelKey = "";
    private bool _disableSearchHint;
    private readonly SettingsScrollHintPolicy _searchHint = new();
    private long _lastScrollInput;
    private static DateTime _lastSearchHintUtc = DateTime.MinValue;
    private readonly DispatcherTimer _searchHintTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    private void InitializeRequestExperience()
    {
        SettingsScroll.PreviewMouseWheel += (_, _) => _lastScrollInput = Environment.TickCount64;
        SettingsScroll.PreviewTouchMove += (_, _) => _lastScrollInput = Environment.TickCount64;
        SettingsScroll.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) _lastScrollInput = Environment.TickCount64; };
        SettingsScroll.PreviewKeyDown += (_, e) =>
        { if (e.Key is Key.PageDown or Key.PageUp or Key.Down or Key.Up or Key.Home or Key.End) _lastScrollInput = Environment.TickCount64; };
        SettingsScroll.ScrollChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, SettingsScroll) || _disableSearchHint || _loading ||
                Math.Abs(e.VerticalChange) < 0.1 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) return;
            var now = Environment.TickCount64;
            if (_lastScrollInput == 0 || now - _lastScrollInput > 700 ||
                DateTime.UtcNow - _lastSearchHintUtc < TimeSpan.FromHours(24)) return;
            if (!_searchHint.Observe(now, true)) return;
            SearchHintBubble.Visibility = Visibility.Visible;
            _lastSearchHintUtc = DateTime.UtcNow;
            _searchHintTimer.Start();
        };
        _searchHintTimer.Tick += (_, _) => HideSearchHint();
        Loaded += (_, _) => _searchHint.Reset();
        Unloaded += (_, _) => { HideSearchHint(); _searchHint.Reset(); _lastScrollInput = 0; };
    }

    private void LoadRequestExperience(AiAssistantConfig config)
    {
        _requestProfiles = (config.ModelRequestProfiles ?? new()).Where(p => p.Value != null)
            .ToDictionary(p => p.Key, p => p.Value.Clone());
        _diagnosticModelKey = config.DiagnosticModelKey;
        _disableSearchHint = config.DisableSettingsSearchHint;
    }

    private void EditModelRequest_Click(object sender, RoutedEventArgs e)
    {
        var config = CaptureUiAsConfig();
        var key = AccountingModelCombo.SelectedValue as string;
        if (string.IsNullOrWhiteSpace(key)) { ToastService.ShowInfo("请先选择要编辑的模型。"); return; }
        var dialog = new AiModelRequestDialog(config, key) { Owner = Window.GetWindow(this) ?? Application.Current.MainWindow };
        var accepted = dialog.ShowDialog();
        if (dialog.AnalysisRequested) _diagnosticModelKey = dialog.DiagnosticModelKey;
        if (accepted == true && dialog.Result != null)
        {
            _requestProfiles[key] = dialog.Result;
            _diagnosticModelKey = dialog.DiagnosticModelKey;
            ToastService.ShowInfo(dialog.ResultWasTested ? "此模型规则测试通过，保存 AI 设置后生效。" : "文档已更新，保存 AI 设置后生效；请求规则未改变。");
        }
    }

    private void HideSearchHint() { _searchHintTimer.Stop(); SearchHintBubble.Visibility = Visibility.Collapsed; }
    private void SearchHint_Close(object sender, RoutedEventArgs e) => HideSearchHint();
    private void SearchHint_Search(object sender, RoutedEventArgs e)
    { HideSearchHint(); SettingsSearchBox.Focus(); SettingsSearchBox.SelectAll(); }
    private void SearchHint_Disable(object sender, RoutedEventArgs e)
    { _disableSearchHint = true; HideSearchHint(); ToastService.ShowInfo("本次不再提醒；保存设置后会记住此选择。"); }

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SettingsSearchResults == null || SettingsScroll == null) return;
        SettingsSearchResults.Children.Clear();
        var query = SettingsSearchBox.Text.Trim();
        if (query.Length == 0) return;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = Descendants(SettingsScroll).OfType<FrameworkElement>().Select(element =>
        {
            var label = element switch { TextBlock t => t.Text, CheckBox c => c.Content as string,
                Button b => b.Content as string, _ => null };
            return (element, label);
        }).Where(x => !string.IsNullOrWhiteSpace(x.label) && x.label!.Length <= 200 &&
            words.All(w => x.label.Contains(w, StringComparison.OrdinalIgnoreCase))).GroupBy(x => x.label).Take(8).Select(g => g.First()).ToArray();
        foreach (var match in matches)
        {
            var button = new Button { Content = match.label!.Length > 35 ? match.label[..35] + "…" : match.label,
                ToolTip = match.label, Margin = new Thickness(0, 5, 6, 0) };
            button.Click += (_, _) =>
            {
                _lastScrollInput = 0; _searchHint.Reset(); HideSearchHint();
                match.element.BringIntoView();
                var original = match.element.Opacity; match.element.Opacity = 0.6;
                var flash = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                flash.Tick += (_, _) => { flash.Stop(); match.element.Opacity = original; }; flash.Start();
            };
            SettingsSearchResults.Children.Add(button);
        }
        if (matches.Length == 0) SettingsSearchResults.Children.Add(new TextBlock { Text = "未找到设置，请换一个关键词。", Margin = new Thickness(0, 5, 0, 0) });
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}

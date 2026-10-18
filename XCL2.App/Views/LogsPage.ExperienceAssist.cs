using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class LogsPage
{
    private readonly Dictionary<TextBox, string> _logViewSources = new();
    private readonly Dictionary<TextBox, LogViewSnapshot> _logViewProjections = new();
    private DispatcherTimer? _filteredGameLogTimer;
    private string _logViewKeyword = "";
    private string _logViewLevel = "";
    private double _logReadingFontSize = 12;
    private TextBox[] LogTextBoxes => new[] { GameLogBox, LauncherLogBox, CrashRawBox, FullLauncherLogBox };

    private void InitializeLogReading()
    {
        _filteredGameLogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _filteredGameLogTimer.Tick += (_, _) =>
        { _filteredGameLogTimer.Stop(); SetLogViewSource(GameLogBox, RawLogView(GameLogBox)); };
        Unloaded += (_, _) => _filteredGameLogTimer.Stop();
        foreach (var box in LogTextBoxes)
        {
            box.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                (_, e) => { CopySelectedLog(box); e.Handled = true; },
                (_, e) => { e.CanExecute = box.SelectionLength > 0; e.Handled = true; }));
            var menu = new ContextMenu();
            var copy = new MenuItem { Header = "复制所选内容（脱敏）" };
            copy.Click += (_, _) => CopySelectedLog(box);
            var reading = new MenuItem { Header = "阅读字号与视图筛选…" };
            reading.Click += LogReading_Click;
            var bottom = new MenuItem { Header = "返回最新日志" };
            bottom.Click += (_, _) => box.ScrollToEnd();
            var selectAll = new MenuItem { Header = "全选" };
            selectAll.Click += (_, _) => box.SelectAll();
            menu.Items.Add(copy); menu.Items.Add(selectAll); menu.Items.Add(reading); menu.Items.Add(bottom);
            menu.Opened += (_, _) => copy.IsEnabled = box.SelectionLength > 0;
            box.ContextMenu = menu;
        }
    }

    private void SetLogViewSource(TextBox box, string text, bool initial = false)
    {
        if (!initial && _logViewSources.TryGetValue(box, out var previous) && previous == text &&
            _logViewProjections.TryGetValue(box, out var unchanged) && box.Text == unchanged.Text) return;
        _logViewSources[box] = text;
        var projection = LogViewTextService.Project(text, _logViewKeyword, _logViewLevel);
        _logViewProjections[box] = projection;
        if (box.Text == projection.Text) return;
        var offset = box.VerticalOffset;
        var horizontal = box.HorizontalOffset;
        var atBottom = box.ExtentHeight <= box.ViewportHeight + 2 || offset >= box.ExtentHeight - box.ViewportHeight - 2;
        var follow = initial || !_owner.ConfigService.Config.Ux50LogSmartFollow || atBottom;
        var selectionStart = box.SelectionStart;
        var selectionLength = box.SelectionLength;
        box.Text = projection.Text;
        box.FontSize = _logReadingFontSize;
        if (follow) box.ScrollToEnd();
        else
        {
            box.Select(Math.Min(selectionStart, box.Text.Length), Math.Min(selectionLength, Math.Max(0, box.Text.Length - selectionStart)));
            box.ScrollToVerticalOffset(offset);
        }
        box.ScrollToHorizontalOffset(horizontal);
    }

    private void AppendLogViewLine(TextBox box, string line)
        => AppendLogViewText(box, line + Environment.NewLine);

    private void AppendLogViewText(TextBox box, string text)
    {
        if (text.Length == 0) return;
        var previous = RawLogView(box);
        if (ReferenceEquals(box, GameLogBox) && previous == Loc.T("Str_Cs_No_Output_Yet", "(暂无输出)"))
        { SetLogViewSource(box, ""); previous = ""; }
        var raw = previous + text;
        if (raw.Length > 500_000)
        {
            // Mirror the process manager's bounded live buffer; never grow an independent endless log.
            var begin = raw.IndexOf('\n', raw.Length - 400_000);
            raw = begin >= 0 ? raw[(begin + 1)..] : raw[^400_000..];
            SetLogViewSource(box, raw); return;
        }
        if (_logViewKeyword.Length > 0 || _logViewLevel.Length > 0)
        {
            _logViewSources[box] = raw;
            _logViewProjections.Remove(box);
            // High-volume live output is coalesced; filter work runs at most once per 150 ms.
            if (_filteredGameLogTimer?.IsEnabled != true) _filteredGameLogTimer?.Start();
            return;
        }
        _logViewSources[box] = raw;
        _logViewProjections.Remove(box); // Construct source-line map only if the user copies a selection.
        var offset = box.VerticalOffset;
        var follow = !_owner.ConfigService.Config.Ux50LogSmartFollow || box.ExtentHeight <= box.ViewportHeight + 2
            || offset >= box.ExtentHeight - box.ViewportHeight - 2;
        box.AppendText(text);
        if (follow) box.ScrollToEnd(); else box.ScrollToVerticalOffset(offset);
    }

    private string RawLogView(TextBox box) => _logViewSources.TryGetValue(box, out var raw) ? raw : box.Text;

    private bool CopySelectedLog(TextBox box, FrameworkElement? feedbackSource = null)
    {
        if (box.SelectionLength == 0) return false;
        var text = DiagnosticRedactionService.Sanitize(box.SelectedText);
        var view = _logViewProjections.TryGetValue(box, out var existing) ? existing : LogViewTextService.Project(RawLogView(box), _logViewKeyword, _logViewLevel);
        text = LogViewTextService.CopySelection(view, box.SelectionStart, text, _owner.ConfigService.Config.Ux50LogCopyLineNumbers);
        CopyLogTextToClipboard(text, feedbackSource ?? box);
        return true;
    }

    private void LogHelp_Click(object sender, RoutedEventArgs e) => OfflineHelpService.Show("logs");

    private void LogReading_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OverlayDialogControl { MinWidth = 320, MaxWidth = 540 };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "日志阅读与筛选", FontSize = 18, FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock { Text = "只影响当前页面。筛选不修改原日志；AI 分析、诊断导出仍使用原始内容。未知级别的开头信息保留。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        root.Children.Add(new TextBlock { Text = "关键词（不区分大小写）" });
        var keyword = new TextBox { Text = _logViewKeyword, MaxLength = 200, Margin = new Thickness(0, 4, 0, 10) };
        root.Children.Add(keyword);
        root.Children.Add(new TextBlock { Text = "日志级别" });
        var levels = new[] { "全部", "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL" };
        var level = new ComboBox { ItemsSource = levels, SelectedIndex = Math.Max(0, Array.IndexOf(levels, _logViewLevel)), Margin = new Thickness(0, 4, 0, 10) };
        root.Children.Add(level);
        root.Children.Add(new TextBlock { Text = "阅读字号（仅当前页面）" });
        var fonts = new[] { 10, 12, 14, 16, 18, 20, 24 };
        var font = new ComboBox { ItemsSource = fonts, SelectedItem = (int)_logReadingFontSize, Margin = new Thickness(0, 4, 0, 10) };
        root.Children.Add(font);
        var number = new CheckBox { Content = "复制所选日志时附带原始行号", IsChecked = _owner.ConfigService.Config.Ux50LogCopyLineNumbers, Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(number);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "恢复阅读默认值", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8) };
        reset.Click += (_, _) => { keyword.Clear(); level.SelectedIndex = 0; font.SelectedItem = 12; };
        var apply = new Button { Content = "应用", MinWidth = 80, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8) };
        apply.Click += (_, _) =>
        {
            _logViewKeyword = keyword.Text.Trim();
            _logViewLevel = level.SelectedIndex > 0 ? levels[level.SelectedIndex] : "";
            _logReadingFontSize = font.SelectedItem is int value ? value : 12;
            _logViewProjections.Clear();
            if (_owner.ConfigService.Config.Ux50LogCopyLineNumbers != (number.IsChecked == true))
            { _owner.ConfigService.Config.Ux50LogCopyLineNumbers = number.IsChecked == true; _owner.ConfigService.Save(); }
            foreach (var box in LogTextBoxes) { box.FontSize = _logReadingFontSize; SetLogViewSource(box, RawLogView(box)); }
            LogFilterNotice.Text = _logViewKeyword.Length > 0 || _logViewLevel.Length > 0
                ? "视图筛选中 · 原日志仍完整保留；在“更多 → 阅读与筛选”清除" : "";
            LogFilterNotice.Visibility = LogFilterNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            dialog.Close();
        };
        actions.Children.Add(reset); actions.Children.Add(apply); root.Children.Add(actions);
        dialog.Content = new ScrollViewer { Content = root, MaxHeight = Math.Max(200, _owner.ActualHeight - 120), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        OverlayDialogService.ShowNonModal(dialog);
    }
}

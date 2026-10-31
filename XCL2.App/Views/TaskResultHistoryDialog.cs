using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class TaskResultHistoryDialog : OverlayDialogControl, IDisposable
{
    private readonly ListBox _list = new() { MinHeight = 60, MaxHeight = 320, BorderThickness = new Thickness(0) };
    private bool _disposed;
    public TaskResultHistoryDialog(AppConfig config, Action save)
    {
        MinWidth = 330; MaxWidth = 620;
        MaxHeight = Math.Max(280, Math.Min(600, (Application.Current.MainWindow?.ActualHeight ?? 740) - 100));
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "本次会话任务结果", FontWeight = FontWeights.Bold, FontSize = 18 });
        heading.Children.Add(new TextBlock { Text = "最多保留 100 条简短结果，关闭启动器后清除。仅记录任务类型、时间和结果。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        root.Children.Add(heading);
        var remember = new CheckBox { Content = "记住本次会话的任务结果", IsChecked = config.RememberTaskResults, Margin = new Thickness(0, 0, 0, 12) };
        remember.Checked += (_, _) => { config.RememberTaskResults = true; save(); };
        remember.Unchecked += (_, _) => { config.RememberTaskResults = false; save(); TaskResultHistoryService.Clear(); };
        Grid.SetRow(remember, 1); root.Children.Add(remember);
        _list.SetResourceReference(Control.BackgroundProperty, "SideBrush");
        _list.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var itemTemplate = new DataTemplate();
        var itemText = new FrameworkElementFactory(typeof(TextBlock));
        itemText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        itemText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text"));
        itemText.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("ToolTip"));
        itemTemplate.VisualTree = itemText; _list.ItemTemplate = itemTemplate;
        Grid.SetRow(_list, 2); root.Children.Add(_list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var clear = new Button { Content = "清空记录", MinWidth = 90, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, _) => TaskResultHistoryService.Clear();
        var close = new Button { Content = "关闭", MinWidth = 80, Padding = new Thickness(12, 6, 12, 6) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(clear); buttons.Children.Add(close); Grid.SetRow(buttons, 3); root.Children.Add(buttons);
        Content = root;
        TaskResultHistoryService.Changed += Refresh;
        OverlayClosed += (_, _) => Dispose();
        Refresh();
    }
    private void Refresh()
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        var rows = TaskResultHistoryService.Snapshot().Select(r => new TaskHistoryViewRow(
            $"{PresentationPreferencesService.FormatDateTime(new DateTimeOffset(r.OccurredAt))}   {Kind(r.Kind)} · {Outcome(r.Outcome)}",
            PresentationPreferencesService.FormatFullDateTime(new DateTimeOffset(r.OccurredAt)))).ToArray();
        _list.ItemsSource = rows.Length == 0 ? new[] { new TaskHistoryViewRow("暂无任务结果", "暂无任务结果") } : rows;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TaskResultHistoryService.Changed -= Refresh;
    }
    private sealed record TaskHistoryViewRow(string Text, string ToolTip);
    private static string Kind(TaskResultKind kind) => kind switch { TaskResultKind.ModUpdate => "模组更新", TaskResultKind.Backup => "备份", TaskResultKind.Restore => "恢复", _ => "下载 / 安装" };
    private static string Outcome(TaskResultOutcome outcome) => outcome switch { TaskResultOutcome.Completed => "已完成", TaskResultOutcome.Canceled => "已取消", _ => "未完成" };
    public static void Show(AppConfig config, Action save) => OverlayDialogService.ShowNonModal(new TaskResultHistoryDialog(config, save));
}

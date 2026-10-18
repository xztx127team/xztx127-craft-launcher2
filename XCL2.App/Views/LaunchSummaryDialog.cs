using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class LaunchSummaryDialog : OverlayDialogControl
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly StackPanel _rows = new();
    private bool _started;

    public LaunchSummaryDialog(Func<CancellationToken, Task<IReadOnlyList<LaunchSummaryRow>>> load)
    {
        var owner = Application.Current?.MainWindow;
        Width = Math.Max(280, Math.Min(620, (owner?.ActualWidth ?? 1000) - 80));
        MaxHeight = Math.Max(240, (owner?.ActualHeight ?? 640) - 80);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = Text("启动摘要", 20); title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(0, 0, 0, 14); root.Children.Add(title);
        _rows.Children.Add(Text("正在读取本地设置…", 13));
        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var close = new Button { Content = "关闭", Padding = new Thickness(18, 6, 18, 6),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        close.Click += (_, _) => Close(); Grid.SetRow(close, 2); root.Children.Add(close);
        var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(22), Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        border.BorderThickness = new Thickness(1); Content = border;
        Unloaded += (_, _) => _cancellation.Cancel();
        Loaded += async (_, _) =>
        {
            if (_started) return; _started = true;
            try
            {
                var rows = await load(_cancellation.Token);
                if (_cancellation.IsCancellationRequested) return;
                _rows.Children.Clear();
                foreach (var row in rows)
                {
                    var label = Text(row.Label, 12); label.Margin = new Thickness(0, 8, 0, 3);
                    label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    _rows.Children.Add(label); _rows.Children.Add(Text(row.Value, 13));
                }
                var note = Text("摘要只读取本地信息，不会联网或执行命令。实际启动前会再次检查配置。", 12);
                note.Margin = new Thickness(0, 14, 0, 0); _rows.Children.Add(note);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (_cancellation.IsCancellationRequested) return;
                _rows.Children.Clear(); _rows.Children.Add(Text("无法读取摘要，请检查所选实例。", 13));
                ErrorPresenter.LogFallback("读取启动摘要失败", ex);
            }
        };
    }

    private static TextBlock Text(string value, double size)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return text;
    }
}

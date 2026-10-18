using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public static class ImportPreviewDialog
{
    private static bool _open;
    public static async Task<bool> ConfirmAsync(string source, string destination, string name, ImportPreviewKind kind)
    {
        if (ConfigService.Active?.Config.Ux50ImportPreview == false) return true;
        if (_open) { ToastService.ShowInfo("已有导入预览，请先完成或取消该预览。"); return false; }
        _open = true;
        try
        {
            using var preview = new PreviewControl(source, destination, name, kind);
            return await OverlayDialogService.ShowModalAsync(preview) == true;
        }
        finally { _open = false; }
    }

    private sealed class PreviewControl : OverlayDialogControl, IDisposable
    {
        private readonly TextBlock _summary = new() { Text = "正在读取文件清单与同名项目…此过程不会写入或解压文件。", TextWrapping = TextWrapping.Wrap };
        private readonly Button _confirm = new() { Content = "确认并继续导入", IsEnabled = false, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        private readonly CancellationTokenSource _stop = new();
        private readonly Window? _owner;
        private bool _started, _disposed;
        public PreviewControl(string source, string destination, string name, ImportPreviewKind kind)
        {
            var owner = Application.Current?.MainWindow;
            _owner = owner;
            if (_owner is not null) _owner.Closed += OwnerClosed;
            Width = Math.Min(620, Math.Max(320, (owner?.ActualWidth ?? 854) - 100));
            MaxHeight = Math.Max(260, (owner?.ActualHeight ?? 480) - 90);
            var panel = new Grid { Margin = new Thickness(22) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.Children.Add(new TextBlock { Text = "导入前预览", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
            var scroll = new ScrollViewer { Content = _summary, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1); panel.Children.Add(scroll);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = "取消", Padding = new Thickness(16, 6, 16, 6) };
            cancel.Click += (_, _) => CloseWith(false); _confirm.Click += (_, _) => CloseWith(true);
            actions.Children.Add(cancel); actions.Children.Add(_confirm); Grid.SetRow(actions, 2); panel.Children.Add(actions);
            Content = panel;
            RequestClose += (_, _) => Dispose();
            Loaded += async (_, _) =>
            {
                if (_started || _disposed) return; _started = true;
                try
                {
                    var ct = _stop.Token;
                    var result = await Task.Run(() => ImportPreviewService.Inspect(source, destination, name, kind, ct), ct);
                    if (_disposed) return;
                    var conflicts = result.ConflictExamples.Count == 0 ? "" : "\n同名示例：\n" + string.Join("\n", result.ConflictExamples);
                    _summary.Text = $"实例：{result.Name}\n目标位置：{result.Destination}\n已打包内容文件：{result.FileCount}{(result.CountLimited ? "（达到预览上限）" : "")}\n同名文件或重复条目：{result.ConflictCount}" + conflicts +
                        (result.PendingDownloadCount > 0 ? $"\n需联网解析的必需文件：{result.PendingDownloadCount}（文件名、大小和冲突在官方资料解析前无法确定）" : "") +
                        "\n\n此预览没有写入文件。确认后继续原有导入流程；联网安装的游戏本体与加载器数量不计入这里。";
                    _confirm.IsEnabled = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (_disposed) return;
                    _summary.Text = "导入预览未完成，未写入文件：\n" + DiagnosticRedactionService.Sanitize(ex.Message, 2000);
                    ErrorPresenter.LogFallback("导入预览失败", ex);
                }
            };
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (_owner is not null) _owner.Closed -= OwnerClosed;
            _stop.Cancel(); _stop.Dispose();
        }
        private void OwnerClosed(object? sender, EventArgs e) => Dispose();
    }
}

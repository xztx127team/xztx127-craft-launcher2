using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>Only opened by an explicit export action; preview is the exact exported snapshot.</summary>
public sealed class DiagnosticsExportDialog : OverlayDialogControl
{
    private readonly List<(CheckBox Check, DiagnosticExportEntry Entry)> _entries = new();
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, MinHeight = 80 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxHeight = 44, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
    private bool _busy;

    private DiagnosticsExportDialog(IReadOnlyList<DiagnosticExportEntry> prepared)
    {
        MaxWidth = 800;
        MaxHeight = Math.Max(300, Math.Min(650, (Application.Current.MainWindow?.ActualHeight ?? 760) - 100));
        MinWidth = 360;
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "诊断导出预览", FontSize = 18, FontWeight = FontWeights.Bold });
        header.Children.Add(new TextBlock { Text = "选择需要的内容，再检查下方预览。已隐藏常见凭据与个人路径；分享前仍可自行检查。仅保存到本地。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) });
        root.Children.Add(header);
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var entry in prepared)
        {
            var check = new CheckBox { Content = entry.Title, IsChecked = true, Margin = new Thickness(0, 0, 16, 8), MaxWidth = 260,
                ToolTip = entry.Title };
            check.Checked += (_, _) => UpdatePreview();
            check.Unchecked += (_, _) => UpdatePreview();
            _entries.Add((check, entry));
            options.Children.Add(check);
        }
        var optionScroll = new ScrollViewer { Content = options, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(optionScroll, 1); root.Children.Add(optionScroll);
        _preview.SetResourceReference(Control.BackgroundProperty, "SideBrush");
        _preview.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetRow(_preview, 2); root.Children.Add(_preview);
        Grid.SetRow(_status, 3); root.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var close = new Button { Content = "关闭", MinWidth = 80, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        close.Click += (_, _) => { if (!_busy) Close(); };
        var export = new Button { Content = "保存 ZIP", MinWidth = 100, Padding = new Thickness(12, 7, 12, 7) };
        export.SetResourceReference(StyleProperty, "PrimaryButton");
        export.Click += async (_, _) =>
        {
            var selected = _entries.Where(x => x.Check.IsChecked == true).Select(x => x.Entry).ToArray();
            if (selected.Length == 0) { _status.Text = "请至少选择一项内容。"; return; }
            var dialog = new SaveFileDialog { Title = "保存诊断包", Filter = "ZIP 压缩包|*.zip", AddExtension = true,
                FileName = $"XCL2-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
            if (dialog.ShowDialog() != true) return;
            if (ConfigService.Active?.Config.Ux50ExportLocationPreview != false)
            {
                var confirmed = await MessageBoxDialog.ShowConfirmAsync(
                    "将保存到：\n" + System.IO.Path.GetFullPath(dialog.FileName)
                    + "\n\n文件名：" + System.IO.Path.GetFileName(dialog.FileName)
                    + "\n仅保存所选的脱敏内容，不会上传。", "确认诊断包保存位置", "保存");
                if (confirmed != true) return;
            }
            _busy = true; export.IsEnabled = false; close.IsEnabled = false;
            try { await Task.Run(() => DiagnosticsExportService.Export(dialog.FileName, selected)); _status.Text = "诊断包已保存。"; }
            catch (Exception ex) { _status.Text = "保存失败：" + DiagnosticRedactionService.Sanitize(ex.Message, 1000); }
            finally { _busy = false; export.IsEnabled = true; close.IsEnabled = true; }
        };
        actions.Children.Add(close); actions.Children.Add(export);
        Grid.SetRow(actions, 4); root.Children.Add(actions);
        Content = root;
        UpdatePreview();
    }

    private void UpdatePreview() => _preview.Text = string.Join("\n\n====================\n\n", _entries
        .Where(x => x.Check.IsChecked == true).Select(x => x.Entry.Title + "\n\n" + x.Entry.SanitizedText));

    public static async void Show(IEnumerable<DiagnosticExportSource> sources)
    {
        try
        {
            var snapshot = sources.Take(8).ToArray();
            var prepared = await Task.Run(() => DiagnosticsExportService.Prepare(snapshot));
            OverlayDialogService.ShowNonModal(new DiagnosticsExportDialog(prepared));
        }
        catch (Exception ex) { ToastService.ShowWarning("准备诊断预览失败：" + DiagnosticRedactionService.Sanitize(ex.Message, 1000)); }
    }
}

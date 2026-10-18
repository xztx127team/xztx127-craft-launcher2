using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class ScreenshotBrowserDialog : OverlayDialogControl
{
    private readonly string _gameDirectory;
    private readonly ListBox _list = new() { MinHeight = 90, BorderThickness = new Thickness(0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _open, _export, _refresh;
    private readonly MenuItem _delete;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _closed;
    private bool _initialLoadStarted;
    private sealed record Row(ScreenshotFile File, ImageSource? Thumbnail);

    public ScreenshotBrowserDialog(string gameDirectory)
    {
        _gameDirectory = gameDirectory;
        MinWidth = 350; MaxWidth = 760;
        MaxHeight = Math.Max(300, Math.Min(650, (Application.Current.MainWindow?.ActualHeight ?? 760) - 100));
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "实例截图", FontSize = 18, FontWeight = FontWeights.Bold });
        heading.Children.Add(new TextBlock { Text = "只显示当前实例的 PNG / JPEG 图片，最多显示 400 张，每次最多检查 5000 个文件。选中图片后可打开、导出或删除。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        root.Children.Add(heading);
        _list.SetResourceReference(Control.BackgroundProperty, "SideBrush");
        _list.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        var template = new DataTemplate(typeof(Row));
        var grid = new FrameworkElementFactory(typeof(DockPanel));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 5));
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetValue(FrameworkElement.WidthProperty, 104d); image.SetValue(FrameworkElement.HeightProperty, 64d);
        image.SetValue(Image.StretchProperty, Stretch.Uniform); image.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Row.Thumbnail))); grid.AppendChild(image);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("File.Name"));
        text.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("File.Name")); grid.AppendChild(text);
        template.VisualTree = grid; _list.ItemTemplate = template;
        _list.SelectionChanged += (_, _) => UpdateActions();
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        Grid.SetRow(_list, 1); root.Children.Add(_list);
        Grid.SetRow(_status, 2); root.Children.Add(_status);
        var actions = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        _open = MakeButton("打开", (_, _) => OpenSelected());
        _export = MakeButton("导出", async (_, _) => await ExportSelectedAsync());
        left.Children.Add(_open); left.Children.Add(_export);
        actions.Children.Add(left);
        var more = new Button { Content = "更多 ▾", Padding = new Thickness(10, 6, 10, 6), MinWidth = 76 };
        var menu = new ContextMenu();
        _delete = new MenuItem { Header = "删除选中的截图…" };
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        menu.Items.Add(_delete);
        var folder = new MenuItem { Header = "打开截图文件夹" }; folder.Click += (_, _) => OpenFolder(); menu.Items.Add(folder);
        var refreshMenu = new MenuItem { Header = "刷新列表" }; refreshMenu.Click += async (_, _) => await ReloadAsync(); menu.Items.Add(refreshMenu);
        more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
        _refresh = more;
        var right = new StackPanel { Orientation = Orientation.Horizontal }; right.Children.Add(more);
        var close = MakeButton("关闭", (_, _) => Close()); close.Margin = new Thickness(8, 0, 0, 0); right.Children.Add(close);
        Grid.SetColumn(right, 1); actions.Children.Add(right);
        Grid.SetRow(actions, 3); root.Children.Add(actions);
        Content = root;
        Loaded += async (_, _) =>
        {
            if (_initialLoadStarted) return;
            _initialLoadStarted = true;
            await ReloadAsync();
        };
        OverlayClosed += (_, _) => { _closed = true; _lifetime.Cancel(); };
        UpdateActions();
    }
    private static Button MakeButton(string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 6, 10, 6), MinWidth = 64, Margin = new Thickness(0, 0, 6, 0) };
        button.Click += handler; return button;
    }
    private void UpdateActions()
    {
        var enabled = !_busy && _list.SelectedItem is Row;
        _open.IsEnabled = enabled; _export.IsEnabled = enabled; _delete.IsEnabled = enabled; _refresh.IsEnabled = !_busy;
    }
    private async Task ReloadAsync()
    {
        if (_busy || _closed) return;
        _busy = true; UpdateActions(); _status.Text = "正在读取截图…";
        try
        {
            var rows = await Task.Run(() =>
            {
                var items = new List<Row>();
                foreach (var file in ScreenshotBrowserService.List(_gameDirectory))
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    ImageSource? thumb = null;
                    try
                    {
                        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.StreamSource = stream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 160; bitmap.DecodePixelHeight = 100; bitmap.EndInit(); bitmap.Freeze(); thumb = bitmap;
                    }
                    catch { /* Corrupt images retain their name; other files remain usable. */ }
                    items.Add(new(file, thumb));
                }
                return items;
            }, _lifetime.Token);
            if (_closed) return;
            _list.ItemsSource = rows;
            _status.Text = rows.Count == 0 ? "当前实例还没有截图。进入游戏后可按游戏截图快捷键保存。" : $"显示 {rows.Count} 张截图。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) _status.Text = "无法读取截图：" + DiagnosticRedactionService.Sanitize(ex.Message, 1000); }
        finally { _busy = false; if (!_closed) UpdateActions(); }
    }
    private void OpenSelected()
    {
        if (_busy || _list.SelectedItem is not Row row) return;
        try
        {
            ScreenshotBrowserService.Validate(_gameDirectory, row.File);
            Process.Start(new ProcessStartInfo(row.File.Path) { UseShellExecute = true });
        }
        catch (Exception ex) { _status.Text = "打开失败：" + DiagnosticRedactionService.Sanitize(ex.Message, 1000); }
    }
    private async Task ExportSelectedAsync()
    {
        if (_busy || _list.SelectedItem is not Row row) return;
        var dialog = new SaveFileDialog { Title = "导出截图", FileName = row.File.Name, Filter = "图片文件|*" + Path.GetExtension(row.File.Name), AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        _busy = true; UpdateActions();
        try { await Task.Run(() => ScreenshotBrowserService.Export(_gameDirectory, row.File, dialog.FileName)); if (!_closed) _status.Text = "截图已导出。"; }
        catch (Exception ex) { if (!_closed) _status.Text = "导出失败：" + DiagnosticRedactionService.Sanitize(ex.Message, 1000); }
        finally { _busy = false; if (!_closed) UpdateActions(); }
    }
    private async Task DeleteSelectedAsync()
    {
        if (_busy || _list.SelectedItem is not Row row) return;
        _busy = true; UpdateActions();
        var confirmed = await MessageBoxDialog.ShowConfirmAsync($"确定永久删除截图“{row.File.Name}”？\n此操作不会删除其他截图。", "删除截图", "删除");
        if (confirmed != true || _closed) { _busy = false; if (!_closed) UpdateActions(); return; }
        var deleted = false;
        try { await Task.Run(() => ScreenshotBrowserService.Delete(_gameDirectory, row.File)); deleted = true; }
        catch (Exception ex) { if (!_closed) _status.Text = "删除失败：" + DiagnosticRedactionService.Sanitize(ex.Message, 1000); }
        finally { _busy = false; if (!_closed) UpdateActions(); }
        if (!_closed && deleted) await ReloadAsync();
    }
    private void OpenFolder()
    {
        try
        {
            var directory = ScreenshotBrowserService.GetDirectory(_gameDirectory);
            if (!Directory.Exists(directory)) { _status.Text = "截图文件夹尚未创建。"; return; }
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { _status.Text = "打开失败：" + DiagnosticRedactionService.Sanitize(ex.Message, 1000); }
    }
    public static void Show(string gameDirectory) => OverlayDialogService.ShowNonModal(new ScreenshotBrowserDialog(gameDirectory));
}

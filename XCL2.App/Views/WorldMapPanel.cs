using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XCL2.App.Services.Worlds;

namespace XCL2.App.Views;

/// <summary>Map controls and cancellable loading, separate from editable world settings.</summary>
internal sealed class WorldMapPanel : UserControl
{
    private readonly ComboBox _dimension = new() { MinWidth = 140, DisplayMemberPath = nameof(WorldMapDimension.DisplayName) };
    private readonly ComboBox _view = new() { MinWidth = 170, ItemsSource = new[] { "鸟瞰 / 地表投影", "水平切面（X-Z）", "垂直切面（X-Y）", "三维样式 / 等距地表", "方块分布（体积统计）" }, SelectedIndex = 0 };
    private readonly TextBox _sliceY = new() { Width = 65, Text = "64" }, _minY = new() { Width = 65, Text = "-64" }, _maxY = new() { Width = 65, Text = "319" };
    private readonly TextBox _x = new() { Width = 92, Text = "0" }, _z = new() { Width = 92, Text = "0" }, _radius = new() { Width = 56, Text = "64" };
    private readonly CheckBox _height = new() { Content = "高度图", Margin = new Thickness(8, 5, 8, 0) }, _grid = new() { Content = "区块网格", IsChecked = true, Margin = new Thickness(8, 5, 8, 0) };
    private readonly Button _load = new() { Content = "预览", IsEnabled = false }, _cancel = new() { Content = "取消读取", IsEnabled = false }, _export = new() { Content = "导出 PNG", IsEnabled = false };
    private readonly TextBlock _status = new() { Text = "选择存档后可读取已生成地表。", TextWrapping = TextWrapping.Wrap }, _sample = new() { Text = "北 ↑（−Z） · 东 →（+X）", TextWrapping = TextWrapping.Wrap };
    private readonly WorldMapViewport _viewport = new();
    private readonly List<Button> _viewActions = new();
    private WorldArchive? _world;
    private WorldMapVisualization? _visualization;
    private WorldMapPreview? _preview;
    private BitmapSource? _bitmap;
    private CancellationTokenSource? _reading;
    private CancellationTokenSource? _rendering;
    private int _renderGeneration;
    private int _generation;

    public WorldMapPanel()
    {
        var page = new Grid { Margin = new Thickness(12) };
        for (int i = 0; i < 3; i++) page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var controls = new WrapPanel();
        void Field(string name, FrameworkElement value)
        { controls.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 4) }); value.Margin = new Thickness(0, 0, 12, 4); controls.Children.Add(value); }
        Field("视图", _view); Field("维度", _dimension); Field("中心 X", _x); Field("Z", _z); Field("半径", _radius); Field("切片 Y", _sliceY); Field("Y 起点", _minY); Field("终点", _maxY);
        _radius.ToolTip = "8–256 格；三维样式和体积统计最多 64 格。只读取已生成的区块。";
        _sliceY.ToolTip = "水平切面的绝对 Y 高度。"; _minY.ToolTip = _maxY.ToolTip = "垂直切面和体积统计的 Y 范围，最多 512 格；垂直切面固定为中心 Z。";
        _view.SelectionChanged += (_, _) => { CancelRead(true); UpdateModeControls(); _status.Text = "视图已切换，点击预览读取。"; };
        UpdateModeControls();
        _load.ToolTip = "读取所选维度在指定坐标附近已生成的地表，不修改世界。";
        _cancel.ToolTip = "取消当前地图读取，存档内容保持不变。";
        foreach (var button in new[] { _load, _cancel }) { button.SetResourceReference(StyleProperty, "CompactActionButton"); button.Margin = new Thickness(0, 0, 8, 4); ToolTipService.SetShowOnDisabled(button, true); controls.Children.Add(button); }
        _load.Click += async (_, _) => await LoadMapAsync(); _cancel.Click += (_, _) => { CancelRead(false); _status.Text = "已取消读取。"; };
        _dimension.SelectionChanged += (_, _) => { CancelRead(true); _status.Text = "维度已切换，点击预览读取。"; };
        page.Children.Add(controls);
        var tools = new WrapPanel();
        Button Tool(string title, Action action, string tip, bool needsPreview = false)
        {
            var button = new Button { Content = title, ToolTip = tip, IsEnabled = !needsPreview, Margin = new Thickness(0, 0, 8, 4) };
            button.SetResourceReference(StyleProperty, "CompactActionButton"); ToolTipService.SetShowOnDisabled(button, true);
            if (needsPreview) _viewActions.Add(button);
            button.Click += (_, _) => action(); tools.Children.Add(button); return button;
        }
        Tool("出生点", GoToSpawn, "将中心坐标设为主世界出生点，再点击“预览”读取周围地图。"); Tool("玩家位置", GoToPlayer, "定位 level.dat 中的单人玩家维度及坐标，再点击“预览”读取地图。");
        Tool("适应窗口", _viewport.Fit, "读取预览后，将整张地图居中显示。", true); Tool("＋", () => _viewport.Zoom(1.4), "读取预览后放大地图；也可使用鼠标滚轮。", true); Tool("−", () => _viewport.Zoom(1 / 1.4), "读取预览后缩小地图；也可使用鼠标滚轮。", true);
        _export.SetResourceReference(StyleProperty, "CompactActionButton"); ToolTipService.SetShowOnDisabled(_export, true);
        _export.Margin = new Thickness(0, 0, 8, 4); _export.Click += (_, _) => Export(); _export.ToolTip = "先读取地图预览，再导出当前视图 PNG，不含定位标记和网格。"; tools.Children.Add(_export);
        Tool("导出统计 CSV", ExportDistribution, "选择方块分布视图并预览后导出全部方块名称和计数。", true);
        tools.Children.Add(_height); tools.Children.Add(_grid);
        _height.Checked += async (_, _) => await RecolorAsync(); _height.Unchecked += async (_, _) => await RecolorAsync();
        _grid.Checked += (_, _) => { _viewport.ShowGrid = true; _viewport.InvalidateVisual(); }; _grid.Unchecked += (_, _) => { _viewport.ShowGrid = false; _viewport.InvalidateVisual(); };
        Grid.SetRow(tools, 1); page.Children.Add(tools);
        var help = new TextBlock { Text = "读取 Java 1.13+ 已生成的真实方块；方块用示意颜色。灰格 = 未生成，暗色 = 空气，红格 = 无法读取。三维样式按真实地表高度投影，侧面仅为高度示意；不显示地下结构。切面可查看地下。统计不含空气。读取可能占用较多 CPU、内存及磁盘；建议先退出游戏并从小范围开始。", TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 2, 0, 8) };
        Grid.SetRow(help, 2); page.Children.Add(help);
        var frame = new Border { Child = _viewport, BorderThickness = new Thickness(1), ClipToBounds = true, MinHeight = 180 };
        frame.SetResourceReference(Border.BackgroundProperty, "SideBrush"); frame.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        Grid.SetRow(frame, 3); page.Children.Add(frame);
        _viewport.CellHovered += cell =>
        {
            string detail = cell.Cell.Kind switch { WorldMapCellKind.Surface => cell.Cell.Block, WorldMapCellKind.Empty => cell.Y == null ? "此列没有可见地表（空气或结构空位）" : "空气 / 此位置没有非空气方块", WorldMapCellKind.Unavailable => "此区块无法读取，查看下方提示", _ => "未生成 / 没有区块数据" };
            string y = cell.Y?.ToString() ?? (cell.Cell.Kind == WorldMapCellKind.Surface ? cell.Cell.Y.ToString() : "—");
            _sample.Text = $"X {cell.X} · Y {y} · Z {cell.Z} · {detail}";
        };
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; footer.Children.Add(_sample); footer.Children.Add(_status); Grid.SetRow(footer, 4); page.Children.Add(footer);
        Content = page; Unloaded += (_, _) => CancelRead(false);
    }

    public void SetWorld(WorldArchive world)
    {
        CancelRead(true); _world = world;
        try { _dimension.ItemsSource = WorldMapReader.Dimensions(world.Path); _dimension.SelectedIndex = 0; }
        catch (Exception ex) { _dimension.ItemsSource = null; _status.Text = "无法列出维度：" + ex.Message; return; }
        _x.Text = (world.Settings.SpawnX ?? 0).ToString(CultureInfo.InvariantCulture); _z.Text = (world.Settings.SpawnZ ?? 0).ToString(CultureInfo.InvariantCulture);
        _load.IsEnabled = true; _status.Text = "以出生点为中心；点击预览读取本地已有地表。";
    }
    private void UpdateModeControls()
    {
        var mode = (WorldMapView)_view.SelectedIndex;
        _sliceY.IsEnabled = mode == WorldMapView.HorizontalSlice;
        _minY.IsEnabled = _maxY.IsEnabled = mode is WorldMapView.VerticalSlice or WorldMapView.BlockDistribution;
        _height.IsEnabled = mode is WorldMapView.TopDown or WorldMapView.HorizontalSlice or WorldMapView.VerticalSlice;
        _grid.IsEnabled = mode is WorldMapView.TopDown or WorldMapView.HorizontalSlice;
    }
    private void CancelRead(bool clear)
    {
        _renderGeneration++; _rendering?.Cancel(); _rendering = null;
        _generation++; _reading?.Cancel(); _reading = null; _cancel.IsEnabled = false; _load.IsEnabled = _world != null;
        if (clear) { _visualization = null; _preview = null; _bitmap = null; _export.IsEnabled = false; foreach (var button in _viewActions) button.IsEnabled = false; _viewport.Clear(); _sample.Text = "北 ↑（−Z） · 东 →（+X）"; }
    }
    private async Task LoadMapAsync()
    {
        if (_world == null || _dimension.SelectedItem is not WorldMapDimension dimension) { _status.Text = "请先选择存档和维度。"; return; }
        if (!int.TryParse(_x.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) || !int.TryParse(_z.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z) || !int.TryParse(_radius.Text, out int radius))
        { _status.Text = "中心坐标与半径需要整数。"; return; }
        var mode = (WorldMapView)_view.SelectedIndex;
        int sliceY = 64, minY = -64, maxY = 319;
        if (mode == WorldMapView.HorizontalSlice && !int.TryParse(_sliceY.Text, out sliceY) ||
            (mode is WorldMapView.VerticalSlice or WorldMapView.BlockDistribution) && (!int.TryParse(_minY.Text, out minY) || !int.TryParse(_maxY.Text, out maxY)))
        { _status.Text = "切片 Y 与 Y 范围需要整数。"; return; }
        var options = new WorldMapReadOptions(mode, sliceY, minY, maxY);
        try { WorldMapReader.ValidateVisualization(radius, options); }
        catch (ArgumentOutOfRangeException ex) { _status.Text = ex.Message; return; }
        if (x is < -30000000 or > 30000000 || z is < -30000000 or > 30000000) { _status.Text = "中心坐标应在 ±30000000 内。"; return; }
        var world = _world; int beforePrompt = _generation;
        if (await MessageBoxDialog.ShowConfirmAsync($"即将读取 {radius * 2 + 1} × {radius * 2 + 1} 格范围的真实存档数据。\n\n此操作可能占用较多 CPU、内存及磁盘，三维投影和体积统计尤其明显。单区块解压上限 8 MiB，总解压上限 256 MiB；可随时取消。建议关闭正在使用该存档的游戏或服务器，先从小范围开始。\n\n只读取已保存数据，不修改或生成区块。是否开始？", "存档可视化 · 性能提示", "开始读取") != true) return;
        if (beforePrompt != _generation || world != _world) return;
        CancelRead(true); int generation = _generation;
        var reading = new CancellationTokenSource(); _reading = reading;
        _load.IsEnabled = false; _cancel.IsEnabled = true; _status.Text = "正在读取已生成区块…";
        try
        {
            var visualization = await Task.Run(() => WorldMapReader.ReadVisualization(world.Path, dimension, x, z, radius, options, reading.Token), reading.Token);
            var preview = visualization.Surface;
            if (generation != _generation || reading.IsCancellationRequested) return;
            _visualization = visualization; _preview = preview; await RecolorAsync(true);
            if (_bitmap == null) return;
            if (generation != _generation || reading.IsCancellationRequested) return;
            _status.Text = $"X {preview.MinX}–{preview.MinX + preview.Width - 1} · Z {preview.MinZ}–{preview.MinZ + preview.Width - 1} · {preview.Width} × {preview.Width} 方块\n已读 {preview.LoadedChunks} 个区块 · 未生成 {preview.MissingChunks} · 无法读取 {preview.UnavailableChunks}\n" + string.Join("\n", preview.Warnings) + (mode == WorldMapView.BlockDistribution ? $"\n统计 Y {minY}–{maxY}：{visualization.NonAirBlocks:N0} 个非空气方块，{visualization.Blocks.Count} 种（仅已成功读取区块）。" : mode == WorldMapView.VerticalSlice ? $"\n垂直切面：Z = {z}，Y {minY}–{maxY}；图像顶部为较高 Y。" : mode == WorldMapView.HorizontalSlice ? $"\n水平切面：Y = {sliceY}。" : "");
        }
        catch (OperationCanceledException) { if (generation == _generation) _status.Text = "已取消读取。"; }
        catch (Exception ex) { if (generation == _generation) _status.Text = "地图预览失败：" + ex.Message; }
        finally
        {
            if (generation == _generation) { _reading = null; _cancel.IsEnabled = false; _load.IsEnabled = true; }
            reading.Dispose();
        }
    }
    private void GoToSpawn()
    {
        if (_world == null) { _status.Text = "请先在存档列表中选择一个世界。"; return; }
        if (_world.Settings.SpawnX is not int x || _world.Settings.SpawnZ is not int z) { _status.Text = "存档没有记录出生点坐标。"; return; }
        CancelRead(true);
        _dimension.SelectedIndex = 0; _x.Text = x.ToString(CultureInfo.InvariantCulture); _z.Text = z.ToString(CultureInfo.InvariantCulture);
        _status.Text = "已定位主世界出生点；点击预览读取。";
    }
    private void GoToPlayer()
    {
        if (_world?.Settings.Player is not { X: double x, Z: double z } player || !double.IsFinite(x) || !double.IsFinite(z) || x is < -30000000 or > 30000000 || z is < -30000000 or > 30000000)
        { _status.Text = "存档没有可用的单人玩家位置（不读取多人 playerdata）。"; return; }
        string id = player.Dimension switch { "0" => "minecraft:overworld", "-1" => "minecraft:the_nether", "1" => "minecraft:the_end", null => "minecraft:overworld", _ => player.Dimension };
        var dimension = (_dimension.ItemsSource as IEnumerable<WorldMapDimension>)?.FirstOrDefault(d => d.Id == id);
        if (dimension == null) { _status.Text = "玩家所在维度无法定位：" + id; return; }
        CancelRead(true);
        _dimension.SelectedItem = dimension; _x.Text = ((int)Math.Floor(x)).ToString(CultureInfo.InvariantCulture); _z.Text = ((int)Math.Floor(z)).ToString(CultureInfo.InvariantCulture);
        _status.Text = "已定位玩家保存的位置；点击预览读取。";
    }
    private async Task RecolorAsync(bool fit = false)
    {
        if (_visualization == null || _world == null) return;
        _rendering?.Cancel(); var rendering = new CancellationTokenSource(); _rendering = rendering;
        int generation = ++_renderGeneration; var visualization = _visualization; bool height = _height.IsChecked == true;
        try
        {
            BitmapSource bitmap;
            if (visualization.Options.View == WorldMapView.BlockDistribution) bitmap = DrawDistribution(visualization);
            else
            {
                var raster = await Task.Run(() => WorldMapRasterizer.Render(visualization, height, rendering.Token), rendering.Token);
                bitmap = BitmapSource.Create(raster.Width, raster.Height, 96, 96, PixelFormats.Bgra32, null, raster.Pixels, raster.Width * 4);
                bitmap.Freeze();
            }
            if (generation != _renderGeneration || rendering.IsCancellationRequested || _visualization != visualization) return;
            _bitmap = bitmap; var preview = visualization.Surface;
            int? spawnX = preview.Dimension == "minecraft:overworld" ? _world.Settings.SpawnX : null, spawnZ = preview.Dimension == "minecraft:overworld" ? _world.Settings.SpawnZ : null;
            string? playerDimension = _world.Settings.Player?.Dimension switch { "0" => "minecraft:overworld", "-1" => "minecraft:the_nether", "1" => "minecraft:the_end", null => "minecraft:overworld", var id => id };
            var player = _world.Settings.Player;
            _viewport.SetMap(visualization, bitmap, spawnX, spawnZ,
                playerDimension == preview.Dimension && player?.X is double px && double.IsFinite(px) ? px : null,
                playerDimension == preview.Dimension && player?.Z is double pz && double.IsFinite(pz) ? pz : null, fit);
            _export.IsEnabled = true; foreach (var button in _viewActions) button.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _renderGeneration)
            {
                _bitmap = null; _export.IsEnabled = false; _viewport.Clear();
                foreach (var button in _viewActions) button.IsEnabled = false;
                _status.Text = "绘制地图失败：" + ex.Message;
            }
        }
        finally { if (generation == _renderGeneration) _rendering = null; rendering.Dispose(); }
    }
    private static BitmapSource DrawDistribution(WorldMapVisualization view)
    {
        var rows = view.Blocks.Take(20).ToArray(); int width = 1100, height = 130 + Math.Max(1, rows.Length) * 32;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 28, 34)), null, new Rect(0, 0, width, height));
            void Label(string text, double x, double y, double size = 14)
            {
                var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.White, 1) { MaxTextWidth = width - x - 20 };
                drawing.DrawText(label, new Point(x, y));
            }
            Label($"真实方块分布 · Y {view.Options.MinY}–{view.Options.MaxY} · 前 20 种", 20, 16, 20);
            Label($"仅统计成功读取的区块；不含空气、未生成或无法读取的数据。总计 {view.NonAirBlocks:N0} 个，{view.Blocks.Count} 种。", 20, 48);
            if (rows.Length == 0) Label("范围内没有可统计的已读取非空气方块。", 20, 90);
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i]; double y = 82 + i * 32; var color = WorldMapReader.BlockColor(row.Block);
                string label = row.Block.Length > 50 ? row.Block[..47] + "…" : row.Block;
                Label(label, 20, y, 13);
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), null, new Rect(470, y + 2, 350.0 * row.Count / Math.Max(1, rows[0].Count), 20));
                Label($"{row.Count:N0}  ({row.Count * 100.0 / Math.Max(1, view.NonAirBlocks):F2}%)", 840, y, 13);
            }
            Label("完整方块名称和计数可通过“导出统计 CSV”保存。", 20, height - 30, 12);
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); result.Render(visual); result.Freeze(); return result;
    }
    private void ExportDistribution()
    {
        if (_visualization?.Options.View != WorldMapView.BlockDistribution) { _status.Text = "请先选择“方块分布”视图并点击预览。"; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV 表格|*.csv", FileName = "world-block-distribution.csv", AddExtension = true, DefaultExt = ".csv" };
        if ((Window.GetWindow(this) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) != true) return;
        try
        {
            string Quoted(string value) => "\"" + ((value.Length > 0 && "=+-@".Contains(value[0]) ? "'" : "") + value).Replace("\"", "\"\"") + "\"";
            var lines = new[] { "block,count" }.Concat(_visualization.Blocks.Select(b => Quoted(b.Block) + "," + b.Count.ToString(CultureInfo.InvariantCulture)));
            File.WriteAllLines(dialog.FileName, lines, new System.Text.UTF8Encoding(true)); _status.Text = "已导出全部方块统计：" + dialog.FileName;
        }
        catch (Exception ex) { _status.Text = "统计导出失败：" + ex.Message; }
    }
    private void Export()
    {
        if (_bitmap == null || _preview == null) { _status.Text = "请先点击“预览”读取地图，再导出图片。"; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片|*.png", FileName = $"map-{_visualization?.Options.View}-{_preview.MinX}-{_preview.MinZ}.png", AddExtension = true, DefaultExt = ".png" };
        if ((Window.GetWindow(this) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) != true) return;
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(_bitmap));
            using var output = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write); encoder.Save(output);
            _status.Text = "已导出当前视图：" + dialog.FileName;
        }
        catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; }
    }
}

internal sealed class WorldMapViewport : FrameworkElement
{
    private WorldMapPreview? _map;
    private WorldMapVisualization? _view;
    private BitmapSource? _image;
    private double _scale = 1, _left, _top;
    private Point? _drag;
    private bool _needsFit;
    private int? _spawnX, _spawnZ;
    private double? _playerX, _playerZ;
    public bool ShowGrid { get; set; } = true;
    public event Action<(int X, int Z, int? Y, WorldMapCell Cell)>? CellHovered;
    public WorldMapViewport()
    {
        ClipToBounds = true; Focusable = true; Cursor = Cursors.Hand;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SizeChanged += (_, _) => { if (_needsFit) Fit(); else InvalidateVisual(); };
        MouseWheel += (_, e) => { Zoom(e.Delta > 0 ? 1.25 : .8, e.GetPosition(this)); e.Handled = true; };
        MouseLeftButtonDown += (_, e) => { Focus(); _drag = e.GetPosition(this); CaptureMouse(); e.Handled = true; };
        MouseLeftButtonUp += (_, e) => { _drag = null; ReleaseMouseCapture(); e.Handled = true; };
        LostMouseCapture += (_, _) => _drag = null;
        MouseMove += (_, e) =>
        {
            var point = e.GetPosition(this);
            if (_drag is Point previous) { _left += point.X - previous.X; _top += point.Y - previous.Y; _drag = point; InvalidateVisual(); }
            if (_map == null) return;
            int dx = (int)Math.Floor((point.X - _left) / _scale), dz = (int)Math.Floor((point.Y - _top) / _scale);
            if (_view == null || _view.Options.View is WorldMapView.Isometric or WorldMapView.BlockDistribution) return;
            if (dx >= 0 && dz >= 0 && dx < _view.Width && dz < _view.Height)
            {
                int? y = _view.Options.View == WorldMapView.VerticalSlice ? _view.Options.MaxY - dz : _view.Options.View == WorldMapView.HorizontalSlice ? _view.Options.SliceY : null;
                CellHovered?.Invoke((_map.MinX + dx, _view.Options.View == WorldMapView.VerticalSlice ? _view.SliceZ : _map.MinZ + dz, y, _view.Cells[dz * _view.Width + dx]));
            }
        };
    }
    public void Clear() { _view = null; _map = null; _image = null; InvalidateVisual(); }
    public void SetMap(WorldMapVisualization view, BitmapSource image, int? spawnX, int? spawnZ, double? playerX, double? playerZ, bool fit)
    { _view = view; _map = view.Surface; _image = image; _spawnX = spawnX; _spawnZ = spawnZ; _playerX = playerX; _playerZ = playerZ; if (fit) Fit(); InvalidateVisual(); }
    public void Fit()
    {
        if (_map == null || _image == null || ActualWidth < 1 || ActualHeight < 1) { _needsFit = true; return; }
        _needsFit = false; _scale = Math.Clamp(Math.Min(ActualWidth / _image.PixelWidth, ActualHeight / _image.PixelHeight) * .94, .05, 32);
        _left = (ActualWidth - _image.PixelWidth * _scale) / 2; _top = (ActualHeight - _image.PixelHeight * _scale) / 2; InvalidateVisual();
    }
    public void Zoom(double factor) => Zoom(factor, new Point(ActualWidth / 2, ActualHeight / 2));
    private void Zoom(double factor, Point point)
    {
        if (_map == null) return;
        double next = Math.Clamp(_scale * factor, .05, 32), ratio = next / _scale;
        _left = point.X - (point.X - _left) * ratio; _top = point.Y - (point.Y - _top) * ratio; _scale = next; _needsFit = false; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing); drawing.DrawRectangle(TryFindResource("SideBrush") as Brush ?? Brushes.Transparent, null, new Rect(RenderSize));
        if (_map == null || _image == null) return;
        double length = _image.PixelWidth * _scale;
        drawing.DrawImage(_image, new Rect(_left, _top, length, _image.PixelHeight * _scale));
        if (_view?.Options.View is not (WorldMapView.TopDown or WorldMapView.HorizontalSlice)) return;
        if (ShowGrid && _scale >= .65)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)), .7);
            for (int x = (WorldMapReader.FloorDiv(_map.MinX, 16) + 1) * 16; x < _map.MinX + _map.Width; x += 16)
            { double px = _left + (x - _map.MinX) * _scale; drawing.DrawLine(pen, new Point(px, _top), new Point(px, _top + length)); }
            for (int z = (WorldMapReader.FloorDiv(_map.MinZ, 16) + 1) * 16; z < _map.MinZ + _map.Width; z += 16)
            { double pz = _top + (z - _map.MinZ) * _scale; drawing.DrawLine(pen, new Point(_left, pz), new Point(_left + length, pz)); }
        }
        void Marker(double? x, double? z, Brush color)
        {
            if (x is not double mx || z is not double mz || mx < _map.MinX || mz < _map.MinZ || mx >= _map.MinX + _map.Width || mz >= _map.MinZ + _map.Width) return;
            var point = new Point(_left + (mx - _map.MinX + .5) * _scale, _top + (mz - _map.MinZ + .5) * _scale);
            drawing.DrawEllipse(Brushes.Black, new Pen(color, 2), point, 5, 5);
            drawing.DrawLine(new Pen(color, 2), new Point(point.X - 9, point.Y), new Point(point.X + 9, point.Y));
            drawing.DrawLine(new Pen(color, 2), new Point(point.X, point.Y - 9), new Point(point.X, point.Y + 9));
        }
        Marker(_spawnX, _spawnZ, Brushes.Gold); Marker(_playerX, _playerZ, Brushes.Cyan);
    }
}

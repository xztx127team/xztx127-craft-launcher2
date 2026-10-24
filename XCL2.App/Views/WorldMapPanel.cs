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
    private readonly TextBox _x = new() { Width = 92, Text = "0" }, _z = new() { Width = 92, Text = "0" }, _radius = new() { Width = 56, Text = "64" };
    private readonly CheckBox _height = new() { Content = "高度图", Margin = new Thickness(8, 5, 8, 0) }, _grid = new() { Content = "区块网格", IsChecked = true, Margin = new Thickness(8, 5, 8, 0) };
    private readonly Button _load = new() { Content = "预览", IsEnabled = false }, _cancel = new() { Content = "取消读取", IsEnabled = false }, _export = new() { Content = "导出 PNG", IsEnabled = false };
    private readonly TextBlock _status = new() { Text = "选择存档后可读取已生成地表。", TextWrapping = TextWrapping.Wrap }, _sample = new() { Text = "北 ↑（−Z） · 东 →（+X）", TextWrapping = TextWrapping.Wrap };
    private readonly WorldMapViewport _viewport = new();
    private readonly List<Button> _viewActions = new();
    private WorldArchive? _world;
    private WorldMapPreview? _preview;
    private BitmapSource? _bitmap;
    private CancellationTokenSource? _reading;
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
        Field("维度", _dimension); Field("中心 X", _x); Field("Z", _z); Field("半径", _radius);
        _radius.ToolTip = "8–256 个方块；只读取正方形范围内已经生成的区块。";
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
        _export.Margin = new Thickness(0, 0, 8, 4); _export.Click += (_, _) => Export(); _export.ToolTip = "先读取地图预览，再导出地表 PNG；1 像素 / 方块，不含定位标记和网格。"; tools.Children.Add(_export);
        tools.Children.Add(_height); tools.Children.Add(_grid);
        _height.Checked += (_, _) => Recolor(); _height.Unchecked += (_, _) => Recolor();
        _grid.Checked += (_, _) => { _viewport.ShowGrid = true; _viewport.InvalidateVisual(); }; _grid.Unchecked += (_, _) => { _viewport.ShowGrid = false; _viewport.InvalidateVisual(); };
        Grid.SetRow(tools, 1); page.Children.Add(tools);
        var help = new TextBlock { Text = "Java 1.13+ 二维地表 · 滚轮缩放，按住左键平移，移动鼠标查看方块。灰格 = 未生成，暗色 = 空区块，红格 = 无法读取。下界显示最上层屋顶；方块使用示意颜色。", TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 2, 0, 8) };
        Grid.SetRow(help, 2); page.Children.Add(help);
        var frame = new Border { Child = _viewport, BorderThickness = new Thickness(1), ClipToBounds = true, MinHeight = 180 };
        frame.SetResourceReference(Border.BackgroundProperty, "SideBrush"); frame.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        Grid.SetRow(frame, 3); page.Children.Add(frame);
        _viewport.CellHovered += cell =>
        {
            string detail = cell.Cell.Kind switch { WorldMapCellKind.Surface => $"Y {cell.Cell.Y} · {cell.Cell.Block}", WorldMapCellKind.Empty => "空区块（无非空气方块）", WorldMapCellKind.Unavailable => "此区块无法读取，查看下方提示", _ => "未生成 / 没有区块数据" };
            _sample.Text = $"X {cell.X} · Z {cell.Z} · {detail}\n北 ↑（−Z） · 东 →（+X） · 黄色十字：出生点 · 青色十字：玩家";
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
    private void CancelRead(bool clear)
    {
        _generation++; _reading?.Cancel(); _reading = null; _cancel.IsEnabled = false; _load.IsEnabled = _world != null;
        if (clear) { _preview = null; _bitmap = null; _export.IsEnabled = false; foreach (var button in _viewActions) button.IsEnabled = false; _viewport.Clear(); _sample.Text = "北 ↑（−Z） · 东 →（+X）"; }
    }
    private async Task LoadMapAsync()
    {
        if (_world == null || _dimension.SelectedItem is not WorldMapDimension dimension) { _status.Text = "请先选择存档和维度。"; return; }
        if (!int.TryParse(_x.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) || !int.TryParse(_z.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z) || !int.TryParse(_radius.Text, out int radius))
        { _status.Text = "中心坐标与半径需要整数。"; return; }
        CancelRead(true); var world = _world; int generation = _generation;
        var reading = new CancellationTokenSource(); _reading = reading;
        _load.IsEnabled = false; _cancel.IsEnabled = true; _status.Text = "正在读取已生成区块…";
        try
        {
            var preview = await Task.Run(() => WorldMapReader.Read(world.Path, dimension, x, z, radius, reading.Token), reading.Token);
            if (generation != _generation || reading.IsCancellationRequested) return;
            _preview = preview; Recolor(true);
            _status.Text = $"X {preview.MinX}–{preview.MinX + preview.Width - 1} · Z {preview.MinZ}–{preview.MinZ + preview.Width - 1} · {preview.Width} × {preview.Width} 方块\n已读 {preview.LoadedChunks} 个区块 · 未生成 {preview.MissingChunks} · 无法读取 {preview.UnavailableChunks}\n" + string.Join("\n", preview.Warnings);
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
    private void Recolor(bool fit = false)
    {
        if (_preview == null || _world == null) return;
        var preview = _preview; var pixels = new byte[preview.Width * preview.Width * 4];
        var surfaces = preview.Cells.Where(c => c.Kind == WorldMapCellKind.Surface).ToArray();
        int minY = surfaces.Length == 0 ? 0 : surfaces.Min(c => c.Y), maxY = surfaces.Length == 0 ? 0 : surfaces.Max(c => c.Y);
        for (int i = 0; i < preview.Cells.Length; i++)
        {
            var cell = preview.Cells[i]; int xx = i % preview.Width, zz = i / preview.Width;
            (byte R, byte G, byte B) color;
            if (cell.Kind == WorldMapCellKind.Surface)
            {
                if (_height.IsChecked == true) { byte gray = (byte)(55 + (cell.Y - minY) * 190L / Math.Max(1, maxY - minY)); color = (gray, gray, gray); }
                else
                {
                    color = WorldMapReader.BlockColor(cell.Block);
                    var north = zz > 0 ? preview.Cells[i - preview.Width] : cell;
                    double shade = north.Kind == WorldMapCellKind.Surface ? 1 + Math.Clamp((cell.Y - north.Y) * .025, -.16, .16) : 1;
                    color = ((byte)Math.Min(255, color.R * shade), (byte)Math.Min(255, color.G * shade), (byte)Math.Min(255, color.B * shade));
                }
            }
            else { byte tone = (byte)(((xx / 8 + zz / 8) & 1) == 0 ? 44 : 55); color = cell.Kind switch { WorldMapCellKind.Empty => ((byte)23, (byte)27, (byte)31), WorldMapCellKind.Unavailable => ((byte)(tone + 45), tone, tone), _ => (tone, tone, tone) }; }
            pixels[i * 4] = color.B; pixels[i * 4 + 1] = color.G; pixels[i * 4 + 2] = color.R; pixels[i * 4 + 3] = 255;
        }
        _bitmap = BitmapSource.Create(preview.Width, preview.Width, 96, 96, PixelFormats.Bgra32, null, pixels, preview.Width * 4); _bitmap.Freeze();
        int? spawnX = preview.Dimension == "minecraft:overworld" ? _world.Settings.SpawnX : null, spawnZ = preview.Dimension == "minecraft:overworld" ? _world.Settings.SpawnZ : null;
        string? playerDimension = _world.Settings.Player?.Dimension switch { "0" => "minecraft:overworld", "-1" => "minecraft:the_nether", "1" => "minecraft:the_end", null => "minecraft:overworld", var id => id };
        var player = _world.Settings.Player;
        _viewport.SetMap(preview, _bitmap, spawnX, spawnZ,
            playerDimension == preview.Dimension && player?.X is double px && double.IsFinite(px) ? px : null,
            playerDimension == preview.Dimension && player?.Z is double pz && double.IsFinite(pz) ? pz : null, fit);
        _export.IsEnabled = true;
        foreach (var button in _viewActions) button.IsEnabled = true;
    }
    private void Export()
    {
        if (_bitmap == null || _preview == null) { _status.Text = "请先点击“预览”读取地图，再导出图片。"; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片|*.png", FileName = $"map-{_preview.MinX}-{_preview.MinZ}.png", AddExtension = true, DefaultExt = ".png" };
        if ((Window.GetWindow(this) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) != true) return;
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(_bitmap));
            using var output = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write); encoder.Save(output);
            _status.Text = "已导出地表图（1 像素 / 方块）：" + dialog.FileName;
        }
        catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; }
    }
}

internal sealed class WorldMapViewport : FrameworkElement
{
    private WorldMapPreview? _map;
    private BitmapSource? _image;
    private double _scale = 1, _left, _top;
    private Point? _drag;
    private bool _needsFit;
    private int? _spawnX, _spawnZ;
    private double? _playerX, _playerZ;
    public bool ShowGrid { get; set; } = true;
    public event Action<(int X, int Z, WorldMapCell Cell)>? CellHovered;
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
            if (dx >= 0 && dz >= 0 && dx < _map.Width && dz < _map.Width) CellHovered?.Invoke((_map.MinX + dx, _map.MinZ + dz, _map.Cells[dz * _map.Width + dx]));
        };
    }
    public void Clear() { _map = null; _image = null; InvalidateVisual(); }
    public void SetMap(WorldMapPreview map, BitmapSource image, int? spawnX, int? spawnZ, double? playerX, double? playerZ, bool fit)
    { _map = map; _image = image; _spawnX = spawnX; _spawnZ = spawnZ; _playerX = playerX; _playerZ = playerZ; if (fit) Fit(); InvalidateVisual(); }
    public void Fit()
    {
        if (_map == null || ActualWidth < 1 || ActualHeight < 1) { _needsFit = true; return; }
        _needsFit = false; _scale = Math.Clamp(Math.Min(ActualWidth, ActualHeight) / _map.Width * .94, .25, 32);
        _left = (ActualWidth - _map.Width * _scale) / 2; _top = (ActualHeight - _map.Width * _scale) / 2; InvalidateVisual();
    }
    public void Zoom(double factor) => Zoom(factor, new Point(ActualWidth / 2, ActualHeight / 2));
    private void Zoom(double factor, Point point)
    {
        if (_map == null) return;
        double next = Math.Clamp(_scale * factor, .25, 32), ratio = next / _scale;
        _left = point.X - (point.X - _left) * ratio; _top = point.Y - (point.Y - _top) * ratio; _scale = next; _needsFit = false; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing); drawing.DrawRectangle(TryFindResource("SideBrush") as Brush ?? Brushes.Transparent, null, new Rect(RenderSize));
        if (_map == null || _image == null) return;
        double length = _map.Width * _scale;
        drawing.DrawImage(_image, new Rect(_left, _top, length, length));
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

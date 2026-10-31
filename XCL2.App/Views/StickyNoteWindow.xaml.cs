using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using XCL2.App.Services;
using System.Windows.Media;
using System.Windows.Threading;

namespace XCL2.App.Views;

/// <summary>
/// 桌面便签窗口（百宝箱「桌面便签」工具的置顶弹出窗口）。
/// 内容与样式都纯本地保存；正文仍是原来的 .txt，样式另存为同名 .style 文件，
/// 不破坏用户已有便签文本格式。
/// </summary>
public partial class StickyNoteWindow : Window, IGlobalWindowTreatmentExempt
{
    public static readonly List<StickyNoteWindow> OpenWindows = new();

    private sealed record NotePalette(
        string Key, string Card, string TitleBar, string Editor, string Text,
        string TitleText, string Selection, string Grip);

    private static readonly NotePalette[] Palettes =
    {
        new("Yellow",   "#FFF9C4", "#F7E28A", "#FFFDF0", "#3D3D3D", "#6B5B1E", "#F0DE84", "#B8A45A"),
        new("Blue",     "#DCEEFF", "#BBDDFC", "#EFF8FF", "#17324D", "#24547B", "#A8D4FA", "#5A93BE"),
        new("Mint",     "#DDF7E8", "#BDEBCF", "#F0FCF5", "#173C2B", "#286548", "#A9E2C0", "#65A982"),
        new("Pink",     "#FFE2EC", "#F7C4D7", "#FFF3F7", "#4D2635", "#7D3E57", "#F2B6CE", "#BA718F"),
        new("Purple",   "#EDE3FF", "#D5C0F6", "#F8F4FF", "#35284C", "#604B82", "#CDB7EE", "#8B70B2"),
        new("Paper",    "#F7F7F4", "#E4E5E1", "#FFFFFF", "#262A2F", "#505963", "#D9E0E6", "#88929D"),
        new("Midnight", "#232936", "#30394B", "#1C222D", "#EEF3FA", "#D9E6F5", "#4B5F7A", "#8AA1BF"),
        new("Peach",    "#FFE5D1", "#F5C29C", "#FFF5EC", "#583521", "#75472B", "#EFB892", "#B8764F"),
        new("Ocean",    "#D3F2F1", "#8BD1D2", "#EDFCFC", "#163D46", "#215964", "#A1DADB", "#438A99"),
        new("Sage",     "#E5EDDD", "#C3D0B4", "#F5F8EF", "#35442B", "#4A623B", "#C3D9AD", "#80956C"),
        new("Sand",     "#EDE1C9", "#D7C3A0", "#FBF6E9", "#4C4031", "#69563D", "#D9C49F", "#9A815A"),
        new("Graphite", "#303236", "#41454D", "#25272B", "#F0F1F2", "#DFE3EB", "#5D6473", "#9CA5B5"),
        new("Terminal", "#17251C", "#243D2D", "#0F1B13", "#A8EFBE", "#BEF6CC", "#305B3F", "#81B18F"),
        new("Notebook", "#F0F5FA", "#C7D8EB", "#FCFDFF", "#25374B", "#314F73", "#B9D1ED", "#7194BB")
    };

    private readonly string _filePath;
    private readonly DispatcherTimer _saveTimer;
    private bool _suppressSave;
    private bool _dirty;
    private string _styleKey = "Yellow";
    private StickyNoteOptions _options = new();
    private readonly DispatcherTimer _sizeTimer;
    private bool _adjustingSize;

    public string FilePath => _filePath;

    public StickyNoteWindow(string filePath, string? styleKey = null)
    {
        _filePath = filePath;
        InitializeComponent();
        _sizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _sizeTimer.Tick += (_, _) => { _sizeTimer.Stop(); FitToText(); };
        try { _options = StickyNoteOptions.Load(filePath); }
        catch (Exception ex) { ToastService.ShowWarning("便签布局读取失败，暂用默认布局：" + ex.Message); }
        ApplyLayout(_options, persist: false);

        // 便签独立渲染：跳过全局材质/整窗透明，避免驱动合成失败显示黑块。
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is HwndTarget target)
            {
                target.RenderMode = RenderMode.SoftwareOnly;
                target.BackgroundColor = ((SolidColorBrush)Background).Color;
            }
            TryDisableSystemBackdrop();
        };
        Loaded += (_, _) =>
        {
            Opacity = 1;
            ContentBox.Focus();
            FitToText();
        };

        TitleText.Text = Path.GetFileName(filePath);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => Save();

        _suppressSave = true;
        try { ContentBox.Text = File.Exists(filePath) ? File.ReadAllText(filePath) : ""; }
        catch (Exception ex)
        {
            ContentBox.Text = "";
            ContentBox.IsReadOnly = true;
            TitleText.ToolTip = "便签读取失败，已暂停编辑以保护原文件：" + ex.Message;
            ToastService.ShowWarning("便签读取失败，未覆盖原文件：" + ex.Message);
        }
        _suppressSave = false;

        ApplyStyle(string.IsNullOrWhiteSpace(styleKey) ? ReadStyleKey(filePath) : styleKey!, persist: false);
        NoteCard.ContextMenu = CreateNoteMenu(includeEditing: false);
        ContentBox.ContextMenu = CreateNoteMenu(includeEditing: true);
        TitleBar.ToolTip = "按住标题栏拖动；右键可直接选择便签配色、设置置顶或保存。正文输入会自动保存。";
        ContentBox.ToolTip = "停笔后自动保存。右键可剪切、复制、粘贴，或在同一菜单中配置便签外观。";
        ToolTipService.SetShowDuration(ContentBox, 10000);
        UpdatePinHint();

        OpenWindows.Add(this);
        Closed += (_, _) => { _sizeTimer.Stop(); _saveTimer.Stop(); OpenWindows.Remove(this); };
    }

    private static string StylePath(string notePath) => notePath + ".style";

    private ContextMenu CreateNoteMenu(bool includeEditing)
    {
        var menu = new ContextMenu();
        void Populate()
        {
            menu.Items.Clear();
            if (includeEditing)
            {
                foreach (var (title, command) in new[]
                {
                    ("撤销", ApplicationCommands.Undo), ("剪切", ApplicationCommands.Cut),
                    ("复制", ApplicationCommands.Copy), ("粘贴", ApplicationCommands.Paste),
                    ("全选", ApplicationCommands.SelectAll)
                })
                    menu.Items.Add(new MenuItem { Header = title, Command = command, CommandTarget = ContentBox });
                menu.Items.Add(new Separator());
            }
            var styles = new MenuItem { Header = "便签配色", ToolTip = "选择后立即应用，并随这份便签保存。" };
            foreach (var palette in Palettes)
            {
                var item = new MenuItem { Header = GetStyleDisplayName(palette.Key), IsCheckable = true,
                    IsChecked = string.Equals(_styleKey, palette.Key, StringComparison.OrdinalIgnoreCase) };
                item.Click += (_, _) => ApplyStyle(palette.Key);
                styles.Items.Add(item);
            }
            menu.Items.Add(styles);
            var align = new MenuItem { Header = "文字靠左上角显示", IsCheckable = true, IsChecked = _options.AlignTopLeft };
            align.Click += (_, _) => ApplyLayout(_options with { AlignTopLeft = align.IsChecked });
            menu.Items.Add(align);
            var size = new MenuItem { Header = "便签根据文字调整大小", IsCheckable = true, IsChecked = _options.SizeToText,
                ToolTip = "默认关闭。开启后随正文调整宽高；超过屏幕可用区域时仍可滚动查看。" };
            size.Click += (_, _) => ApplyLayout(_options with { SizeToText = size.IsChecked });
            menu.Items.Add(size);
            var pin = new MenuItem { Header = "固定在最上层", IsCheckable = true, IsChecked = Topmost,
                ToolTip = "控制当前便签是否显示在其他普通窗口上方。" };
            pin.Click += PinBtn_Click;
            menu.Items.Add(pin);
            menu.Items.Add(new Separator());
            var save = new MenuItem { Header = "立即保存", IsEnabled = !ContentBox.IsReadOnly,
                ToolTip = "将正文保存到本地便签文件；保存失败时保留窗口和内容。" };
            save.Click += (_, _) => { if (Save()) ToastService.ShowSuccess("便签已保存。"); };
            menu.Items.Add(save);
            var close = new MenuItem { Header = "保存并关闭便签", ToolTip = "先保存正文，再关闭窗口。保存失败时会保留便签供你处理。" };
            close.Click += CloseBtn_Click;
            menu.Items.Add(close);
        }
        menu.Opened += (_, _) => Populate();
        Populate();
        return menu;
    }

    /// <summary>关闭 Windows 11 给无标题栏顶层窗口自动加的云母/亚克力背景材质。
    /// 失败（比如系统版本更老、dwmapi.dll 里没有该属性）就直接忽略——
    /// 老系统本来就不会有该自动加材质的行为，不需要该调用也不受影响。</summary>
    private void TryDisableSystemBackdrop()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int backdropNone = DwmsbtNone;
            NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaSystembackdropType, ref backdropNone, sizeof(int));
        }
        catch { /* 老系统/驱动不支持，忽略即可 */ }
    }

    private const int DwmwaSystembackdropType = 38; // DWMWA_SYSTEMBACKDROP_TYPE（Windows 11 22H2+）
    private const int DwmsbtNone = 1;                // DWMSBT_NONE：不要任何自动背景材质

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }

    public static string ReadStyleKey(string notePath)
    {
        try
        {
            var value = File.Exists(StylePath(notePath)) ? File.ReadAllText(StylePath(notePath)).Trim() : "Yellow";
            return Palettes.Any(p => string.Equals(p.Key, value, StringComparison.OrdinalIgnoreCase)) ? value : "Yellow";
        }
        catch { return "Yellow"; }
    }

    public static void WriteStyleKey(string notePath, string styleKey)
    {
        var normalized = Palettes.FirstOrDefault(p => string.Equals(p.Key, styleKey, StringComparison.OrdinalIgnoreCase))?.Key ?? "Yellow";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(notePath)!);
            File.WriteAllText(StylePath(notePath), normalized);
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[桌面便签] 保存配色失败：" + ex.Message);
            ToastService.ShowWarning("便签配色暂未保存：" + ex.Message);
        }
    }

    public void ApplyStyle(string styleKey, bool persist = true)
    {
        var palette = Palettes.FirstOrDefault(p => string.Equals(p.Key, styleKey, StringComparison.OrdinalIgnoreCase)) ?? Palettes[0];
        _styleKey = palette.Key;

        Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        // 窗口本身现在是不透明的（见 xaml 头部关于去掉 AllowsTransparency 的说明），
        // Window.Background 也要跟着换色，不然切样式时只有 NoteCard 变了色，
        // 四个直角窗口边缘会露出上一个样式的颜色，跟 NoteCard 的圆角对不上。
        Background = B(palette.Card);
        if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = ((SolidColorBrush)Background).Color;
        NoteCard.Background = B(palette.Card);
        TitleBar.Background = B(palette.TitleBar);
        ContentBox.Background = B(palette.Editor);
        ContentBox.FontFamily = new FontFamily(palette.Key == "Terminal" ? "Consolas" : "Microsoft YaHei UI");
        if (palette.Key == "Notebook")
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(B(palette.Editor), null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
            drawing.Children.Add(new GeometryDrawing(null, new Pen(B("#D9E5F1"), 1), new LineGeometry(new Point(0, 23), new Point(24, 23))));
            ContentBox.Background = new DrawingBrush(drawing) { TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 24, 24), Stretch = Stretch.None };
        }
        ContentBox.Foreground = B(palette.Text);
        ContentBox.CaretBrush = B(palette.Text);
        ContentBox.SelectionBrush = B(palette.Selection);
        TitleText.Foreground = B(palette.TitleText);
        StyleBtn.Foreground = PinBtn.Foreground = NoteCloseButton.Foreground = B(palette.TitleText);
        NoteResizeGrip.Foreground = B(palette.Grip);

        if (persist) WriteStyleKey(_filePath, _styleKey);
        FitToText();
        StyleBtn.ToolTip = $"点击切换下一种配色；右键可直接选择。当前：{GetStyleDisplayName(_styleKey)}。";
    }

    private static string GetStyleDisplayName(string key) => key switch
    {
        "Blue" => "天空蓝",
        "Mint" => "薄荷绿",
        "Pink" => "樱花粉",
        "Purple" => "紫晶",
        "Paper" => "纸白",
        "Midnight" => "深夜",
        "Peach" => "蜜桃",
        "Ocean" => "海洋",
        "Sage" => "鼠尾草",
        "Sand" => "牛皮纸",
        "Graphite" => "石墨",
        "Terminal" => "终端绿字",
        "Notebook" => "横线笔记纸",
        _ => "经典黄纸"
    };

    public void ApplyLayout(StickyNoteOptions options, bool persist = true)
    {
        if (persist)
        {
            try { options.Save(_filePath); }
            catch (Exception ex)
            {
                ToastService.ShowWarning("便签布局未保存：" + ex.Message);
                return;
            }
        }
        _options = options;
        ContentBox.TextAlignment = options.AlignTopLeft ? TextAlignment.Left : TextAlignment.Center;
        ContentBox.VerticalContentAlignment = options.AlignTopLeft ? VerticalAlignment.Top : VerticalAlignment.Center;
        MinHeight = options.SizeToText ? 96 : 160;
        if (!options.SizeToText && Height < MinHeight) Height = MinHeight;
        FitToText();
    }

    private void FitToText()
    {
        if (!IsLoaded || !_options.SizeToText || _adjustingSize) return;
        _adjustingSize = true;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var area = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            var availableWidth = Math.Max(MinWidth, area.Width / dpi.DpiScaleX - 24);
            var availableHeight = Math.Max(MinHeight, area.Height / dpi.DpiScaleY - 24);
            // Bound measurement work for very large notes; the full text stays in the editor/file.
            var text = ContentBox.Text;
            if (text.Length > 50000) text = text[..50000];
            var formatted = new FormattedText(string.IsNullOrEmpty(text) ? " " : text,
                System.Globalization.CultureInfo.CurrentUICulture, ContentBox.FlowDirection,
                new Typeface(ContentBox.FontFamily, ContentBox.FontStyle, ContentBox.FontWeight, ContentBox.FontStretch),
                ContentBox.FontSize, ContentBox.Foreground, dpi.PixelsPerDip);
            var padding = ContentBox.Padding;
            Width = Math.Clamp(formatted.WidthIncludingTrailingWhitespace + padding.Left + padding.Right + 20,
                MinWidth, Math.Min(640, availableWidth));
            formatted.MaxTextWidth = Math.Max(1, Width - padding.Left - padding.Right - 20);
            Height = Math.Clamp(formatted.Height + padding.Top + padding.Bottom + 34 + 16,
                MinHeight, availableHeight);
            // Use native screen pixels for mixed-DPI monitor boundaries, then convert the correction to DIPs.
            var screen = PointToScreen(new Point(0, 0));
            var targetX = Math.Clamp(screen.X, area.Left, Math.Max(area.Left, area.Right - Width * dpi.DpiScaleX));
            var targetY = Math.Clamp(screen.Y, area.Top, Math.Max(area.Top, area.Bottom - Height * dpi.DpiScaleY));
            Left += (targetX - screen.X) / dpi.DpiScaleX;
            Top += (targetY - screen.Y) / dpi.DpiScaleY;
        }
        finally { _adjustingSize = false; }
    }

    private void StyleBtn_Click(object sender, RoutedEventArgs e)
    {
        var current = Array.FindIndex(Palettes, p => string.Equals(p.Key, _styleKey, StringComparison.OrdinalIgnoreCase));
        var next = Palettes[(current + 1 + Palettes.Length) % Palettes.Length];
        ApplyStyle(next.Key);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdatePinHint();
    }

    private void UpdatePinHint()
    {
        PinBtn.Content = Topmost ? "📌" : "📍";
        PinBtn.ToolTip = Topmost ? "当前便签已置顶。点击后允许其他窗口覆盖便签。" : "点击让当前便签保持在其他普通窗口上方。";
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ContentBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressSave || _saveTimer == null) return;
        _dirty = true;
        if (_options.SizeToText) { _sizeTimer.Stop(); _sizeTimer.Start(); }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void ContentBox_LostFocus(object sender, RoutedEventArgs e) => Save();
    private void StickyNoteWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!Save()) e.Cancel = true;
    }

    private bool Save()
    {
        _saveTimer.Stop();
        if (_suppressSave || !_dirty || ContentBox.IsReadOnly) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, ContentBox.Text);
            _dirty = false;
            return true;
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[桌面便签] 保存失败：" + ex.Message);
            ToastService.ShowWarning("便签未能保存，内容仍保留在窗口中：" + ex.Message);
            return false;
        }
    }
}

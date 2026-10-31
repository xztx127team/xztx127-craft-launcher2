using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Models;

namespace XCL2.App.Views;

internal sealed class AiFloatingBetaSettingsDialog : Window
{
    private readonly AiFloatingBetaSettings _draft;
    private readonly ComboBox _style = new();
    private readonly ComboBox _scope = new();
    private readonly TextBox _color = new();
    private readonly CheckBox _ballTopmost = new() { Content = "桌面悬浮球置顶", Margin = new Thickness(0, 8, 0, 4) };
    private readonly CheckBox _chatTopmost = new() { Content = "桌面聊天窗口置顶", Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBox _image = new() { IsReadOnly = true };
    private readonly Button _preview = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Action<AiFloatingBetaSettings> _save;

    internal AiFloatingBetaSettingsDialog(Window owner, AiFloatingBetaSettings current, Action<AiFloatingBetaSettings> save)
    {
        _draft = current.Copy();
        _draft.Enabled = true;
        _save = save;
        Owner = owner;
        Title = "AI助手悬浮球 · Beta 设置";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 80);
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var body = new StackPanel { Margin = new Thickness(22) };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new TextBlock { Text = "自定义你的 AI助手悬浮球", FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "点击悬浮球打开聊天，右键切换模式、模型或关闭入口。聊天窗关闭后，悬浮球仍会保留。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) });
        foreach (var pair in new[] { (AiFloatingBallStyle.Circle, "圆形"), (AiFloatingBallStyle.RoundedSquare, "圆角方形"), (AiFloatingBallStyle.Outline, "描边圆形") })
            _style.Items.Add(new ComboBoxItem { Content = pair.Item2, Tag = pair.Item1 });
        _style.SelectedItem = _style.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _draft.Style)) ?? _style.Items[0];
        foreach (var pair in new[] { (AiFloatingBallScope.Launcher, "仅在启动器内部显示"), (AiFloatingBallScope.Desktop, "作为桌面独立悬浮窗显示") })
            _scope.Items.Add(new ComboBoxItem { Content = pair.Item2, Tag = pair.Item1 });
        _scope.SelectedItem = _scope.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _draft.Scope)) ?? _scope.Items[0];
        _color.Text = _draft.Color;
        _image.Text = _draft.ImagePath;
        _style.ToolTip = "圆形、圆角方形和描边样式会实时显示在下方预览中；保存后应用到悬浮球。";
        _scope.ToolTip = "启动器内部显示会随主窗口一起隐藏；桌面模式是独立悬浮窗，可拖动并记住位置。";
        _color.ToolTip = "输入 #RRGGBB；也可用 #AARRGGBB 设置透明度（AA 至少为 40）。修改后可在下方预览。";
        _ballTopmost.ToolTip = "仅控制桌面悬浮球是否保持在其他普通窗口上方。";
        _chatTopmost.ToolTip = "单独控制桌面聊天窗口的置顶状态，与悬浮球置顶互不影响。";
        _image.ToolTip = "自定义图片会替换球内的 AI 文字。保存时复制到启动器数据目录，移动原图后仍可显示。";
        AddField(body, "悬浮球样式", _style);
        AddField(body, "显示范围", _scope);
        _ballTopmost.IsChecked = _draft.BallTopmost;
        _chatTopmost.IsChecked = _draft.ChatTopmost;
        body.Children.Add(_ballTopmost); body.Children.Add(_chatTopmost);
        var colors = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var (hex, name) in new[] { ("#3B82F6", "晴空蓝"), ("#6366F1", "靛蓝"), ("#8B5CF6", "紫罗兰"), ("#DB2777", "玫瑰粉"), ("#EF4444", "珊瑚红"), ("#F97316", "橘橙"), ("#D97706", "琥珀"), ("#16A34A", "草木绿"), ("#0D9488", "青绿"), ("#0891B2", "湖蓝"), ("#475569", "岩灰"), ("#18181B", "墨黑") })
        {
            AiFloatingBallAppearance.TryColor(hex, out var shade);
            var swatch = new Button { Width = 30, Height = 30, Margin = new Thickness(0, 0, 6, 6),
                Background = new System.Windows.Media.SolidColorBrush(shade), ToolTip = $"{name} · {hex}，点击预览此颜色",
                Content = "●", Foreground = new System.Windows.Media.SolidColorBrush(shade) };
            System.Windows.Automation.AutomationProperties.SetName(swatch, name);
            swatch.Click += (_, _) => _color.Text = hex;
            colors.Children.Add(swatch);
        }
        var colorPicker = new Button { Content = "颜色图…", ToolTip = "打开颜色选择器，可在颜色图中选色或输入自定义 RGB 数值。", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 6) };
        colorPicker.Click += (_, _) =>
        {
            using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            if (AiFloatingBallAppearance.TryColor(_color.Text, out var shade)) picker.Color = System.Drawing.Color.FromArgb(shade.R, shade.G, shade.B);
            if (picker.ShowDialog(new NativeOwner(this)) == System.Windows.Forms.DialogResult.OK)
                _color.Text = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        };
        colors.Children.Add(colorPicker);
        AddField(body, "颜色预设与颜色图", colors);
        AddField(body, "颜色（#RRGGBB 或 #AARRGGBB）", _color);
        var imageRow = new Grid();
        imageRow.ColumnDefinitions.Add(new ColumnDefinition());
        imageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        imageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        imageRow.Children.Add(_image);
        var choose = new Button { Content = "选择图片", ToolTip = "选择本地图片作为悬浮球图案，文件不能超过 10 MB。", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(9, 5, 9, 5) };
        Grid.SetColumn(choose, 1); imageRow.Children.Add(choose);
        var clear = new Button { Content = "清除", ToolTip = "恢复球内的 AI 文字，保留当前颜色和形状。", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(9, 5, 9, 5) };
        Grid.SetColumn(clear, 2); imageRow.Children.Add(clear);
        AddField(body, "球内图片（可选，最多 10 MB）", imageRow);
        choose.Click += (_, _) => ChooseImage();
        clear.Click += (_, _) => { _image.Text = ""; UpdatePreview(); };
        _preview.HorizontalAlignment = HorizontalAlignment.Center;
        _preview.Margin = new Thickness(0, 8, 0, 10);
        _preview.Focusable = false;
        _preview.IsHitTestVisible = false;
        body.Children.Add(new TextBlock { Text = "外观预览", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
        body.Children.Add(_preview);
        body.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "保存并应用", ToolTip = "保存这些设置并显示 AI助手悬浮球。", IsDefault = true, Padding = new Thickness(16, 7, 16, 7) };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); body.Children.Add(buttons);
        cancel.Click += (_, _) => Close();
        apply.Click += (_, _) => Save();
        _style.SelectionChanged += (_, _) => UpdatePreview();
        _color.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private sealed class NativeOwner : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; }
        internal NativeOwner(Window owner) => Handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
    }

    private static void AddField(Panel body, string label, UIElement control)
    {
        body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 5), FontWeight = FontWeights.SemiBold });
        body.Children.Add(control);
    }

    private void ChooseImage()
    {
        var picker = new OpenFileDialog { Title = "选择悬浮球图片", CheckFileExists = true,
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (AiFloatingBallAppearance.ReadImage(picker.FileName) == null) throw new InvalidDataException("无法读取所选图片。");
            _image.Text = picker.FileName;
            _status.Text = "✓ 已选择图片，保存后会保留一份副本。";
            UpdatePreview();
        }
        catch (Exception ex) { _status.Text = "图片不可用：" + ex.Message; }
    }

    private void UpdatePreview()
    {
        _draft.Style = (_style.SelectedItem as ComboBoxItem)?.Tag is AiFloatingBallStyle style ? style : AiFloatingBallStyle.Circle;
        _draft.Color = _color.Text.Trim();
        _draft.ImagePath = _image.Text;
        AiFloatingBallAppearance.Apply(_preview, _draft, 64);
    }

    private void Save()
    {
        if (!AiFloatingBallAppearance.TryColor(_color.Text, out _))
        {
            _status.Text = "请填写有效的十六进制颜色，透明度需不低于 25%。";
            _color.Focus();
            return;
        }
        try
        {
            UpdatePreview();
            _draft.BallTopmost = _ballTopmost.IsChecked == true;
            _draft.ChatTopmost = _chatTopmost.IsChecked == true;
            _draft.Scope = (_scope.SelectedItem as ComboBoxItem)?.Tag is AiFloatingBallScope scope ? scope : AiFloatingBallScope.Launcher;
            if (!string.IsNullOrWhiteSpace(_draft.ImagePath))
            {
                if (AiFloatingBallAppearance.ReadImage(_draft.ImagePath) == null) throw new InvalidDataException("所选图片已不存在，请重新选择或清除。");
                var bytes = File.ReadAllBytes(_draft.ImagePath);
                var directory = Path.Combine(App.DataDir, "ai-floating-assets");
                Directory.CreateDirectory(directory);
                var asset = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + Path.GetExtension(_draft.ImagePath).ToLowerInvariant());
                if (!File.Exists(asset)) File.WriteAllBytes(asset, bytes);
                _draft.ImagePath = asset;
            }
            _save(_draft);
            DialogResult = true;
        }
        catch (Exception ex) { _status.Text = "保存失败：" + ex.Message; }
    }
}

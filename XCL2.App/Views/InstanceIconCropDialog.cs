using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XCL2.App.Views;

/// <summary>真实像素裁剪；只输出新的 PNG，不改用户选择的图片。</summary>
internal sealed class InstanceIconCropDialog : OverlayDialogControl
{
    private readonly BitmapSource _source;
    private readonly Image _preview;
    private readonly Slider _zoom = new() { Minimum = 1, Maximum = 4, Value = 1, Width = 230 };
    private readonly Slider _horizontal = new() { Minimum = 0, Maximum = 1, Value = 0.5, Width = 230 };
    private readonly Slider _vertical = new() { Minimum = 0, Maximum = 1, Value = 0.5, Width = 230 };
    public BitmapSource? Result { get; private set; }

    public InstanceIconCropDialog(string file)
    {
        var owner = Application.Current?.MainWindow;
        Width = Math.Max(260, Math.Min(370, (owner?.ActualWidth ?? 854) - 100));
        MaxHeight = Math.Max(180, Math.Min(600, (owner?.ActualHeight ?? 640) - 100));
        using (var input = File.OpenRead(file))
        {
            if (input.Length > 32 * 1024 * 1024) throw new InvalidOperationException("请选择小于 32 MB 的图片。");
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 20000 || frame.PixelHeight > 20000 ||
                (long)frame.PixelWidth * frame.PixelHeight > 100_000_000) throw new InvalidOperationException("图片尺寸过大，请先缩小图片。");
            input.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (frame.PixelWidth > 2048 || frame.PixelHeight > 2048)
            {
                if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = 2048;
                else bitmap.DecodePixelHeight = 2048;
            }
            bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze(); _source = bitmap;
        }
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "裁剪实例图标", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        _preview = new Image { Width = 230, Height = 230, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center };
        var border = new Border { Child = _preview, BorderThickness = new Thickness(1), Padding = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center };
        border.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); panel.Children.Add(border);
        AddSlider(panel, "缩放", _zoom); AddSlider(panel, "水平位置", _horizontal); AddSlider(panel, "垂直位置", _vertical);
        foreach (var slider in new[] { _zoom, _horizontal, _vertical }) slider.ValueChanged += (_, _) => UpdatePreview();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "取消", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6), IsCancel = true };
        cancel.Click += (_, _) => CloseWith(false);
        var save = new Button { Content = "使用图标", MinWidth = 90, Padding = new Thickness(12, 6, 12, 6) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => { Result = RenderCrop(); CloseWith(true); };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = MaxHeight };
        UpdatePreview();
    }

    private static void AddSlider(Panel panel, string label, Slider slider)
    {
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(slider); panel.Children.Add(row);
    }

    private CroppedBitmap CurrentCrop()
    {
        var side = Math.Max(1, (int)(Math.Min(_source.PixelWidth, _source.PixelHeight) / _zoom.Value));
        var x = (int)Math.Round((_source.PixelWidth - side) * _horizontal.Value);
        var y = (int)Math.Round((_source.PixelHeight - side) * _vertical.Value);
        return new CroppedBitmap(_source, new Int32Rect(x, y, side, side));
    }

    private void UpdatePreview()
    {
        var side = Math.Max(1, (int)(Math.Min(_source.PixelWidth, _source.PixelHeight) / _zoom.Value));
        _horizontal.IsEnabled = side < _source.PixelWidth; _vertical.IsEnabled = side < _source.PixelHeight;
        _preview.Source = CurrentCrop();
    }

    private BitmapSource RenderCrop()
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawImage(CurrentCrop(), new Rect(0, 0, 128, 128));
        var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing); bitmap.Freeze(); return bitmap;
    }
}

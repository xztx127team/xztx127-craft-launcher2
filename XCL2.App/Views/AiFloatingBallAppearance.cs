using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XCL2.App.Models;

namespace XCL2.App.Views;

internal static class AiFloatingBallAppearance
{
    internal static bool TryColor(string? text, out Color color)
    {
        color = Colors.DodgerBlue;
        try
        {
            if (string.IsNullOrWhiteSpace(text) || !text.Trim().StartsWith('#') || text.Trim().Length is not (7 or 9)) return false;
            color = (Color)ColorConverter.ConvertFromString(text.Trim());
            return color.A >= 64;
        }
        catch (FormatException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    internal static BitmapImage? ReadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > 10 * 1024 * 1024) throw new InvalidDataException("图片不能超过 10 MB。");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("图片没有可显示的画面。");
        var portrait = decoder.Frames[0].PixelHeight > decoder.Frames[0].PixelWidth;
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (portrait) image.DecodePixelHeight = 128;
        else image.DecodePixelWidth = 128;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static void Apply(Button button, AiFloatingBetaSettings settings, double size)
    {
        if (!TryColor(settings.Color, out var color)) color = Color.FromRgb(59, 130, 246);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var radius = settings.Style == AiFloatingBallStyle.RoundedSquare ? size * .24 : size / 2;
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "BallBorder";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(Border.BackgroundProperty, settings.Style == AiFloatingBallStyle.Outline
            ? new SolidColorBrush(Color.FromArgb(240, 248, 250, 252)) : brush);
        border.SetValue(Border.BorderBrushProperty, brush);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .82, "BallBorder"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .64, "BallBorder"));
        template.Triggers.Add(pressed);
        button.Template = template;
        button.Width = button.Height = size;
        button.Padding = new Thickness(0);
        button.Foreground = settings.Style == AiFloatingBallStyle.Outline ? brush : Brushes.White;
        button.ToolTip = settings.Scope == AiFloatingBallScope.Desktop
            ? "点击打开 AI助手聊天；按住左键拖动位置。右键可搜索、切换模型，设置颜色和置顶，或关闭悬浮球。"
            : "点击打开 AI助手聊天；右键可搜索、切换模型，设置颜色和样式，或切换为桌面悬浮球。";
        ToolTipService.SetShowDuration(button, 12000);
        System.Windows.Automation.AutomationProperties.SetName(button, "AI助手");
        BitmapImage? image = null;
        try { image = ReadImage(settings.ImagePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException or System.Security.SecurityException)
        { /* A removed/broken custom asset falls back to a usable AI label. */ }
        if (image == null)
        {
            button.Content = new Viewbox
            {
                Width = size * .75, Height = size * .44, Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly, IsHitTestVisible = false,
                Child = new TextBlock { Text = "AI", FontSize = size * .32, FontWeight = FontWeights.SemiBold,
                    Foreground = button.Foreground, TextWrapping = TextWrapping.NoWrap }
            };
            return;
        }
        var imageSize = size - 8;
        button.Content = new Image { Source = image, Width = imageSize, Height = imageSize, Stretch = Stretch.UniformToFill,
            Clip = new RectangleGeometry(new Rect(0, 0, imageSize, imageSize), Math.Max(1, radius - 4), Math.Max(1, radius - 4)),
            IsHitTestVisible = false };
    }
}

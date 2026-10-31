using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「皮肤头像生成器」：从一张标准 Minecraft 皮肤贴图(64x64，8x8 每格)里裁剪出
/// "脸部"图层(基础层 8,8 起 8x8 像素)和"帽子"图层(叠加层 40,8 起 8x8 像素，也就是
/// 玩家戴的"第二层"头饰，比如帽子/头发装饰)，按最近邻(NearestNeighbor)放大合成成一张
/// 方形头像 PNG——这是 Minecraft 皮肤玩家头像渲染最常见的做法（Crafatar/NameMC 等
/// 第三方头像服务都是该思路），用最近邻缩放而不是双线性，是为了保留像素风格的锐利边缘，
/// 避免放大后头像糊成一团、丢失"像素画"的观感。
///
/// 不依赖任何第三方图像库(System.Drawing/ImageSharp 等)，纯用 WPF 自带的
/// System.Windows.Media.Imaging 完成裁剪/合成/编码，跟项目现有的"没有引入额外图像处理
/// NuGet 包"该既有选择保持一致。
/// </summary>
public static class SkinAvatarRenderService
{

    public static byte[] RenderFaceAvatar(byte[] skinPngBytes, int outputSize, bool includeHatLayer = true)
        => Plugins.PluginRuntimeRegistry.RenderAvatar(skinPngBytes, outputSize, includeHatLayer,
            () => RenderFaceAvatarBuiltIn(skinPngBytes, outputSize, includeHatLayer));

    /// <summary>内置头像渲染器。公开入口先经过插件渲染链；next() 最终落到这里。</summary>
    private static byte[] RenderFaceAvatarBuiltIn(byte[] skinPngBytes, int outputSize, bool includeHatLayer)
    {
        using var ms = new MemoryStream(skinPngBytes);
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        if (converted.PixelWidth > 2048 || converted.PixelHeight > 2048)
            throw new ArgumentException("皮肤图片尺寸过大。", nameof(skinPngBytes));
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        var rendered = SkinAvatarPixels.Render(pixels, converted.PixelWidth, converted.PixelHeight, outputSize, includeHatLayer);
        var bitmap = BitmapSource.Create(outputSize, outputSize, 96, 96, PixelFormats.Bgra32, null, rendered, outputSize * 4);
        return EncodePng(bitmap);
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}

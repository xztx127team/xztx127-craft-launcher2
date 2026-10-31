using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed class AccountAvatarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Account account) return SteveDefaultAvatar;

        // 第一档：用户导入的头像照片。
        if (!string.IsNullOrWhiteSpace(account.AvatarPhotoPath) && File.Exists(account.AvatarPhotoPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // 立即读完并释放文件句柄，避免占用用户原图文件
                bmp.UriSource = new Uri(Path.GetFullPath(account.AvatarPhotoPath));
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { /* 照片损坏/被移动了，落到下一档而不是显示空白 */ }
        }

        // 未配置皮肤或主动选 Steve 的离线账户固定使用内嵌默认皮肤的正脸。
        // 忽略残留 CustomSkinPath/同名正版缓存，保留用户主动导入的头像照片优先级。
        if (OfflineDefaultSkin.AppliesTo(account))
        {
            try { return OfflineDefaultSkin.LoadAvatar(); }
            catch { /* 损坏的资源仍按最后一档兜底，转换器不向 UI 抛异常。 */ }
        }

        // 第二档：后台查到的服务器端"当前公开皮肤"缓存。
        var onlineCachePath = AccountAvatarCacheService.GetCachePath(account);
        if (AccountAvatarCacheService.ShouldFetch(account) && File.Exists(onlineCachePath))
        {
            try
            {
                byte[] png = SkinAvatarRenderService.RenderFaceAvatar(File.ReadAllBytes(onlineCachePath), 64);
                using var onlineStream = new MemoryStream(png);
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = onlineStream; bmp.EndInit(); bmp.Freeze();
                return bmp;
            }
            catch { /* 缓存文件损坏，落到下一档 */ }
        }

        // 第三档：自定义 Minecraft 皮肤正脸渲染。
        if (!OfflineDefaultSkin.AppliesTo(account)
            && !string.IsNullOrWhiteSpace(account.CustomSkinPath) && File.Exists(account.CustomSkinPath))
        {
            try
            {
                byte[] png = SkinAvatarRenderService.RenderFaceAvatar(File.ReadAllBytes(account.CustomSkinPath), 64);
                using var stream = new MemoryStream(png);
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = stream; bmp.EndInit(); bmp.Freeze();
                return bmp;
            }
            catch { /* 皮肤文件损坏，落到默认头像 */ }
        }

        // 第四档：按 SkinType 显示内置的史蒂夫/艾利克斯默认头像图片。
        bool isAlex = account.Type == AccountType.Offline && account.SkinType == OfflineSkinType.Alex;
        if (account.Type == AccountType.Offline && !isAlex)
        {
            try { return OfflineDefaultSkin.LoadAvatar(); }
            catch { /* 资源损坏时继续使用原有安全兜底。 */ }
        }
        return isAlex ? AlexDefaultAvatar : SteveDefaultAvatar;
    }

    /// <summary>内置默认头像图片，打包成嵌入资源，不依赖任何外部文件/下载。</summary>
    private static ImageSource SteveDefaultAvatar => LoadEmbeddedAvatar("steve-default.png", ref _steveCache);
    private static ImageSource AlexDefaultAvatar => LoadEmbeddedAvatar("alex-default.png", ref _alexCache);

    private static ImageSource? _steveCache;
    private static ImageSource? _alexCache;

    private static ImageSource LoadEmbeddedAvatar(string fileName, ref ImageSource? cache)
    {
        if (cache != null) return cache;
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream($"XCL2.App.Resources.Avatars.{fileName}");
            if (stream == null) throw new FileNotFoundException(fileName);

            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            cache = frame;
            return frame;
        }
        catch
        {
            // 极端情况下（资源没打进程序集）退回一个纯色占位方块，保证转换器永远不抛异常。
            var fallback = new WriteableBitmap(8, 8, 96, 96, PixelFormats.Bgra32, null);
            cache = fallback;
            return fallback;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

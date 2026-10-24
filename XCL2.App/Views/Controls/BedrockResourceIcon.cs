using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;

namespace XCL2.App.Views.Controls;

/// <summary>
/// 社区资源封面。列表回收时取消旧请求，图片始终在后台读取、解码和冻结；
/// 缺图、请求失败或格式不受支持时保留本地绘制的占位图。
/// </summary>
public sealed class BedrockResourceIcon : Grid
{
    public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.Register(
        nameof(SourceUrl), typeof(string), typeof(BedrockResourceIcon),
        new PropertyMetadata("", OnSourceUrlChanged));

    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(
        nameof(FallbackText), typeof(string), typeof(BedrockResourceIcon),
        new PropertyMetadata("", OnFallbackTextChanged));

    private const int MaximumImageBytes = 4 * 1024 * 1024;
    private const int MaximumCacheEntries = 96;
    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim DownloadSlots = new(6);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();
    private static readonly Geometry CubeOutline = FreezeGeometry(
        "M 36,10 L 59,23 L 59,49 L 36,62 L 13,49 L 13,23 Z M 13,23 L 36,36 L 59,23 M 36,36 L 36,62");
    private static readonly Geometry CubeTop = FreezeGeometry("M 36,10 L 59,23 L 36,36 L 13,23 Z");
    private readonly Image _image;
    private readonly TextBlock _monogram;
    private CancellationTokenSource? _loadCancellation;
    private long _generation;

    private sealed record CacheEntry(BitmapSource? Image, DateTimeOffset ExpiresAt);

    public string SourceUrl
    {
        get => (string)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    public string FallbackText
    {
        get => (string)GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    public BedrockResourceIcon()
    {
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var fallback = new Grid();
        var background = new LinearGradientBrush(
            Color.FromRgb(31, 69, 84), Color.FromRgb(27, 39, 62), 45);
        background.Freeze();
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = background,
            BorderBrush = new SolidColorBrush(Color.FromArgb(55, 139, 217, 231)),
            BorderThickness = new Thickness(1),
            Child = fallback
        });

        var cube = new Grid { Width = 72, Height = 72 };
        cube.Children.Add(new Path
        {
            Data = CubeTop, Fill = new SolidColorBrush(Color.FromArgb(35, 126, 224, 223))
        });
        cube.Children.Add(new Path
        {
            Data = CubeOutline, Stroke = new SolidColorBrush(Color.FromArgb(180, 146, 226, 228)),
            StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round
        });
        fallback.Children.Add(new Viewbox
        {
            Margin = new Thickness(9), Stretch = Stretch.Uniform, Child = cube
        });

        _monogram = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(217, 247, 247)),
            FontSize = 11, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        fallback.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(5, 1, 5, 2),
            MinWidth = 22, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(44, 79, 95)), Child = _monogram
        });

        _image = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Children.Add(_image);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        UpdateFallback();
    }

    private static void OnSourceUrlChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((BedrockResourceIcon)sender).RestartLoad();

    private static void OnFallbackTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((BedrockResourceIcon)sender).UpdateFallback();

    private void OnLoaded(object sender, RoutedEventArgs args) => RestartLoad();

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        CancelLoad();
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (ActualWidth > 0 && ActualHeight > 0)
            _image.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 14, 14);
    }

    private void UpdateFallback()
    {
        var text = (FallbackText ?? "").Trim();
        _monogram.Text = text.Length == 0 ? "BE" : StringInfo.GetNextTextElement(text).ToUpperInvariant();
        AutomationProperties.SetName(this, text.Length == 0 ? "基岩版资源封面" : text + "的资源封面");
    }

    private void CancelLoad()
    {
        ++_generation;
        var cancellation = _loadCancellation;
        _loadCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void RestartLoad()
    {
        CancelLoad();
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        if (!IsLoaded || !TryGetImageUri(SourceUrl, out var uri)) return;
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _ = LoadImageAsync(uri, _generation, cancellation.Token);
    }

    private async Task LoadImageAsync(Uri uri, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var bitmap = await GetImageAsync(uri, cancellationToken);
            if (cancellationToken.IsCancellationRequested || generation != _generation || !IsLoaded || bitmap == null)
                return;
            _image.Source = bitmap;
            _image.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        // 图像是可选的展示信息：网络或编解码失败不能中断搜索、选择和下载。
        catch (Exception) { }
    }

    private static bool TryGetImageUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) ||
            !parsed.IsDefaultPort || parsed.UserInfo.Length != 0 ||
            (parsed.Host != "i.mcmod.cn" && parsed.Host != "www.mcmod.cn")) return false;
        // 目录中仍可能出现 http 图片；官方图床支持 https，避免明文请求。
        uri = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1, Fragment = "" }.Uri;
        return true;
    }

    private static async Task<BitmapSource?> GetImageAsync(Uri uri, CancellationToken cancellationToken)
    {
        var key = uri.AbsoluteUri;
        if (TryReadCache(key, out var cached)) return cached;
        await DownloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryReadCache(key, out cached)) return cached;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));
            var fetchToken = timeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                fetchToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumImageBytes)
                throw new InvalidDataException("资源封面过大。");

            await using var input = await response.Content.ReadAsStreamAsync(fetchToken).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), fetchToken).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > MaximumImageBytes) throw new InvalidDataException("资源封面过大。");
                bytes.Write(buffer, 0, read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var data = bytes.ToArray();
            var bitmap = await Task.Run(() => DecodeImage(data), cancellationToken).ConfigureAwait(false);
            WriteCache(key, bitmap, TimeSpan.FromMinutes(30));
            return bitmap;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            WriteCache(key, null, TimeSpan.FromMinutes(2));
            return null;
        }
        finally { DownloadSlots.Release(); }
    }

    private static BitmapSource DecodeImage(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("资源封面无有效图像。");
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > 40000000)
            throw new InvalidDataException("资源封面尺寸不受支持。");
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, 256);
        else bitmap.DecodePixelHeight = Math.Min(height, 256);
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            throw new InvalidDataException("资源封面无有效像素。");
        bitmap.Freeze();
        return bitmap;
    }

    private static bool TryReadCache(string key, out BitmapSource? bitmap)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                bitmap = entry.Image;
                return true;
            }
        }
        bitmap = null;
        return false;
    }

    private static void WriteCache(string key, BitmapSource? bitmap, TimeSpan lifetime)
    {
        lock (CacheLock)
        {
            if (!Cache.ContainsKey(key)) CacheOrder.Enqueue(key);
            Cache[key] = new CacheEntry(bitmap, DateTimeOffset.UtcNow + lifetime);
            while (Cache.Count > MaximumCacheEntries && CacheOrder.TryDequeue(out var oldest)) Cache.Remove(oldest);
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(18)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2/2.2.24");
        http.DefaultRequestHeaders.Referrer = new Uri("https://www.mcmod.cn/");
        return http;
    }

    private static Geometry FreezeGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}

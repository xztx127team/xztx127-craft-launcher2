using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>
/// 只供旧触控路径使用。LWJGL2 以透明 HCURSOR 隐藏光标，CURSOR_SHOWING 仍可能为 1。
/// 用 Windows 自己的 DrawIconEx 同时处理 alpha 光标和传统 AND/XOR 光标；不修改系统光标。
/// </summary>
internal static class LegacyTouchCursorVisibility
{
    private const int MaximumCursorSize = 512;
    private const int CacheMilliseconds = 750;
    private const int MaximumCacheEntries = 24;
    private const uint DiNormal = 0x0003;
    // 黑/白底色排除颜色巧合，并让低 alpha 像素在至少一张底图上产生可见差异。
    private static readonly int[] Backgrounds = [unchecked((int)0xff000000), unchecked((int)0xffffffff)];
    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, (long At, bool? Visible)> Cache = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int Icon;
        public uint HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        public uint Color;
    }

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon,
        int width, int height, uint animationStep, IntPtr backgroundBrush, uint flags);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, in BITMAPINFO info, uint usage,
        out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    public static bool IsVisible(IntPtr cursor, bool nativeShowing)
    {
        if (!nativeShowing || cursor == IntPtr.Zero) return false;
        return LegacyTouchCursorVisibilityPolicy.IsVisible(nativeShowing, true, HasVisiblePixels(cursor));
    }

    /// <summary>
    /// false 仅表示已成功绘制并确认全透明，true 表示绘制出像素；空句柄/检查失败返回 null。
    /// 可用于区分“普通箭头被 Windows 因触摸抑制”和“游戏本身提供的透明形状”。
    /// </summary>
    public static bool? HasVisiblePixels(IntPtr cursor)
    {
        if (cursor == IntPtr.Zero) return null;
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (Cache.TryGetValue(cursor, out var cached) && now >= cached.At && now - cached.At < CacheMilliseconds)
                return cached.Visible;
        }

        bool? visible;
        try { visible = InspectCursor(cursor); }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or OverflowException or
            DllNotFoundException or EntryPointNotFoundException)
        {
            visible = null;
        }

        lock (Gate)
        {
            // 句柄可被 Windows 回收再利用；短 TTL 与上限防止长期缓存错误形状或累积句柄。
            if (Cache.Count >= MaximumCacheEntries) Cache.Clear();
            Cache[cursor] = (now, visible);
        }
        return visible;
    }

    private static bool? InspectCursor(IntPtr cursor)
    {
        if (!GetIconInfo(cursor, out var icon)) return null;
        IntPtr dc = IntPtr.Zero, dib = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            var source = icon.Color != IntPtr.Zero ? icon.Color : icon.Mask;
            if (source == IntPtr.Zero || GetObject(source, Marshal.SizeOf<BITMAP>(), out var bitmap) == 0)
                return null;
            var width = bitmap.Width;
            var height = icon.Color != IntPtr.Zero ? bitmap.Height : bitmap.Height / 2;
            if (width <= 0 || height <= 0 || width > MaximumCursorSize || height > MaximumCursorSize ||
                (icon.Color == IntPtr.Zero && bitmap.Height % 2 != 0)) return null;

            dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            var info = new BITMAPINFO
            {
                Header = new BITMAPINFOHEADER
                {
                    Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    Width = width, Height = -height, Planes = 1, BitCount = 32
                }
            };
            dib = CreateDIBSection(dc, in info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            previous = SelectObject(dc, dib);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return null;
            var pixels = new int[checked(width * height)];

            foreach (var background in Backgrounds)
            {
                Array.Fill(pixels, background);
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                if (!DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, DiNormal) || !GdiFlush())
                    return null;
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                if (LegacyTouchCursorVisibilityPolicy.ChangesRgb(pixels, background)) return true;
            }
            return false;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            // GetIconInfo 为调用方复制这两个位图；HCURSOR 本身属于游戏，绝不销毁。
            if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero && icon.Color != icon.Mask) DeleteObject(icon.Color);
        }
    }
}

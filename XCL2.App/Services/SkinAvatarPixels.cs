namespace XCL2.App.Services;

/// <summary>Pure BGRA face renderer. Minecraft forces the base skin opaque, including transparent PNG input.</summary>
internal static class SkinAvatarPixels
{
    internal static byte[] Render(byte[] pixels, int width, int height, int outputSize, bool includeHat)
    {
        if (width < 64 || width > 2048 || width % 64 != 0 || (height != width && height != width / 2)
            || pixels.Length != checked(width * height * 4))
            throw new ArgumentException("皮肤必须是 64×64 或 64×32 的整数倍 PNG。", nameof(pixels));
        if (outputSize < 1 || outputSize > 2048) throw new ArgumentOutOfRangeException(nameof(outputSize));
        var scale = width / 64;
        // Legacy 64x32 skins do have a hat layer. An entirely opaque legacy hat region
        // is the old unused/background-filled layout and is cleared by Minecraft.
        if (height == width / 2 && includeHat)
        {
            var allOpaque = true;
            for (var y = 0; y < 32 * scale && allOpaque; y++)
                for (var x = 32 * scale; x < width; x++)
                    if (pixels[(y * width + x) * 4 + 3] < 128) { allOpaque = false; break; }
            if (allOpaque) includeHat = false;
        }
        var result = new byte[checked(outputSize * outputSize * 4)];
        for (var y = 0; y < outputSize; y++)
        for (var x = 0; x < outputSize; x++)
        {
            var sx = x * 8 * scale / outputSize;
            var sy = y * 8 * scale / outputSize;
            var face = ((8 * scale + sy) * width + 8 * scale + sx) * 4;
            var hat = ((8 * scale + sy) * width + 40 * scale + sx) * 4;
            var target = (y * outputSize + x) * 4;
            var alpha = includeHat ? pixels[hat + 3] : 0;
            for (var c = 0; c < 3; c++)
                result[target + c] = (byte)((pixels[hat + c] * alpha + pixels[face + c] * (255 - alpha) + 127) / 255);
            result[target + 3] = 255;
        }
        return result;
    }
}

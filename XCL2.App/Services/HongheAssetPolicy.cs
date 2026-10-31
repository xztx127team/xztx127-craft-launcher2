using System.IO;

namespace XCL2.App.Services;

/// <summary>Public page URLs only: the site does not publish a stable asset download API.</summary>
public static class HongheAssetPolicy
{
    public const string IconsUrl = "https://honghemc.cn/app/";
    public const string EffectsUrl = "https://honghemc.cn/app/greenscreen-maker.html?template=villager-plains-boom";
    public const string Text3dUrl = "https://honghemc.cn/app/text3d.html";
    public const int MaxIconBytes = 20 * 1024 * 1024;

    public static bool IsSupportedDownload(string? fileName) => Path.GetExtension(fileName ?? "").ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".webm" or ".mp4" or ".mov";

    public static byte[] ReadIcon(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("所选 PNG 已被移动或删除，请重新选择。", path);
        if (file.Length > MaxIconBytes) throw new InvalidDataException("图标超过 20 MiB，请先在素材站导出较小的 PNG。");
        using var stream = file.OpenRead();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxIconBytes) throw new InvalidDataException("图标超过 20 MiB。");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        ValidatePng(bytes);
        return bytes;
    }

    public static void ValidatePng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 24 || bytes.Length > MaxIconBytes || !bytes.AsSpan(0, 8).SequenceEqual(signature)
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("请选择从红河素材站导出的有效 PNG 图片。");
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > 8192 || height is 0 or > 8192 || (ulong)width * height > 32_000_000)
            throw new InvalidDataException("PNG 尺寸过大（最长边 8192、最多 3200 万像素），请缩小后再导入。");
    }
}

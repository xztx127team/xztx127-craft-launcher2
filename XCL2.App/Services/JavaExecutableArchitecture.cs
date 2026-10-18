using System.IO;

namespace XCL2.App.Services;

/// <summary>读实际 Java 可执行文件的 PE 头，不按启动器自身位数或文件夹名猜测。</summary>
internal static class JavaExecutableArchitecture
{
    internal static int? TryReadBitness(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadBitness(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static int? ReadBitness(Stream stream)
        => ReadMachine(stream) switch
        {
            0x014c or 0x01c0 or 0x01c4 => 32,
            0x8664 or 0xaa64 or 0xa641 or 0xa64e or 0x0200 => 64,
            _ => null
        };

    internal static string? TryReadArchitecture(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadMachine(stream) switch
            {
                0x014c => "x86", 0x8664 => "amd64", 0xaa64 or 0xa641 or 0xa64e => "aarch64",
                0x01c0 or 0x01c4 => "arm", 0x0200 => "ia64", _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    private static ushort? ReadMachine(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek || stream.Length < 64) return null;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        if (reader.ReadUInt16() != 0x5a4d) return null;
        stream.Position = 0x3c;
        var peOffset = reader.ReadUInt32();
        if (peOffset < 64 || peOffset > stream.Length - 24) return null;
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) return null;
        return reader.ReadUInt16();
    }
}

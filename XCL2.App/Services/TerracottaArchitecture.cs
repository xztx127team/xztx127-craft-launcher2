using System.IO;
using System.Runtime.InteropServices;

namespace XCL2.App.Services;

public static class TerracottaArchitecture
{
    public enum Machine { X86 = 0x014c, X64 = 0x8664, Arm64 = 0xaa64 }

    public static Machine ReadMachine(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new BadImageFormatException("所选文件不是有效的 Windows 可执行文件。");
        stream.Position = 0x3c;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 6) throw new BadImageFormatException("可执行文件 PE 头不完整。");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) throw new BadImageFormatException("可执行文件 PE 标识无效。");
        var machine = (Machine)reader.ReadUInt16();
        if (!Enum.IsDefined(machine)) throw new BadImageFormatException("陶瓦本体使用了不支持的处理器架构。");
        return machine;
    }

    public static bool IsCompatible(Machine machine, Architecture operatingSystem) => operatingSystem switch
    {
        Architecture.X86 => machine == Machine.X86,
        Architecture.X64 => machine is Machine.X86 or Machine.X64,
        Architecture.Arm64 => machine is Machine.X86 or Machine.Arm64,
        _ => false
    };

    public static void Validate(string path)
    {
        var machine = ReadMachine(path);
        if (!IsCompatible(machine, RuntimeInformation.OSArchitecture))
            throw new BadImageFormatException($"[XCL-ARCH-001] 陶瓦本体架构不兼容：文件为 {machine}，系统为 {RuntimeInformation.OSArchitecture}。32 位 Windows 需要真正的 x86 本体。");
    }

    public static string? ChooseEmbedded(IEnumerable<string> resourceNames, Architecture operatingSystem)
    {
        string[] names = resourceNames.ToArray();
        string[] suffixes = operatingSystem switch
        {
            Architecture.X86 => new[] { "windows-x86.exe", "windows-i686.exe" },
            Architecture.X64 => new[] { "windows-x86.exe", "windows-i686.exe", "windows-x86_64.exe" },
            Architecture.Arm64 => new[] { "windows-arm64.exe", "windows-x86.exe", "windows-i686.exe" },
            _ => Array.Empty<string>()
        };
        foreach (var suffix in suffixes)
        {
            var candidate = names.FirstOrDefault(name => name.StartsWith("XCL2.App.Resources.Terracotta.", StringComparison.Ordinal)
                && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (candidate != null) return candidate;
        }
        return null;
    }
}

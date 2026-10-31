using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Keep a non-default Java architecture's extracted DLLs separate from the default runtime.</summary>
internal static class NativeDirectoryPolicy
{
    internal static string Resolve(string clientDirectory, string? architecture = null)
    {
        var nativeRoot = Path.Combine(clientDirectory, "natives");
        var current = MinecraftPlatformRulePolicy.CurrentArchitecture;
        if (architecture == null || architecture == current) return nativeRoot;
        if (architecture is not ("x86" or "amd64" or "aarch64" or "arm" or "ia64"))
            throw new InvalidDataException("未知的 Java 本机库架构。");
        return Path.Combine(nativeRoot, "java-" + architecture);
    }

    internal static bool HasCompatibleDll(string directory, string? architecture = null)
    {
        if (!Directory.Exists(directory)) return false;
        var arch = architecture ?? MinecraftPlatformRulePolicy.CurrentArchitecture;
        return Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .Any(file => JavaExecutableArchitecture.TryReadArchitecture(file) == arch);
    }
}

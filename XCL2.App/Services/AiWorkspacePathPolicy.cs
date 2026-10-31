using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XCL2.App.Services;

/// <summary>Validate namespace/network access before any filesystem probe can authenticate to a server.</summary>
internal static class AiWorkspacePathPolicy
{
    internal static bool IsDevicePath(string? path)
    {
        var value = (path ?? "").Trim().Replace('/', '\\');
        return value.StartsWith(@"\\?\", StringComparison.Ordinal)
            || value.StartsWith(@"\\.\", StringComparison.Ordinal)
            || value.StartsWith(@"\??\", StringComparison.Ordinal)
            || value.StartsWith(@"\\??\", StringComparison.Ordinal);
    }

    internal static bool IsNetworkPath(string? path) =>
        (path ?? "").Trim().Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal);

    internal static void EnsureNoLinks(string path)
    {
        var ancestors = new Stack<string>();
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            ancestors.Push(current);
        // Inspect parents first and never call Exists through a junction. A link may
        // point at an SMB server, so checking the leaf first is already too late.
        while (ancestors.Count > 0)
        {
            try
            {
                if ((File.GetAttributes(ancestors.Pop()) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("路径包含符号链接或目录联接，请使用真实路径。");
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
        }
    }

    internal static string? GetDenial(string? path, IEnumerable<string>? approvedRoots = null)
    {
        if (IsDevicePath(path))
            return "AI 文件工具不接受设备命名空间路径，请使用普通本地路径。";
        if (!IsNetworkPath(path)) return null;
        var candidate = path!.Trim().Replace('/', '\\').TrimEnd('\\');
        // Full disk access only grants local disks. Network access must originate from a
        // directory the user explicitly selected in settings, never a model-generated request.
        if ((approvedRoots ?? Array.Empty<string>()).Where(root => !IsDevicePath(root)).Any(root =>
        {
            var approved = root.Trim().Replace('/', '\\').TrimEnd('\\');
            return candidate.Equals(approved, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(approved + "\\", StringComparison.OrdinalIgnoreCase);
        })) return null;
        return "未授权访问网络路径。请先由用户在 AI 设置中明确选择该网络工作目录；未探测服务器。";
    }
}

using System.IO;
using System.IO.Compression;

namespace XCL2.App.Services.Plugins;

internal enum PluginExportFormat { Xcl, Dll, Exe }

/// <summary>Exports complete artifacts atomically; never converts a binary by renaming its extension.</summary>
internal static class PluginUpgradeExport
{
    internal static bool IsPackage(string path) => Path.GetExtension(path).Equals(".xcl", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".xclplugin", StringComparison.OrdinalIgnoreCase);

    internal static void RequireFormat(string entry, PluginExportFormat format)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        if (format == PluginExportFormat.Xcl) return;
        var expected = format == PluginExportFormat.Dll ? ".dll" : ".exe";
        if (!Path.GetExtension(entry).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"此插件的入口是 {Path.GetExtension(entry)}，不能直接导出为 {expected}。DLL 插件与 EXE 管道插件的运行契约不同；请使用对应入口的源码，或选择 XCL 包。");
    }

    internal static void ExportDirectory(string source, string destination, CancellationToken ct,
        bool skipBuild = false, Action<string>? validate = null)
    {
        destination = CheckDestination(source, destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            CopyTree(source, temporary, ct, skipBuild);
            validate?.Invoke(temporary);
            ct.ThrowIfCancellationRequested();
            Directory.Move(temporary, destination);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    internal static void ExportZip(string source, string destination, CancellationToken ct, bool skipBuild = false)
    {
        destination = CheckDestination(source, destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (var file in Files(source, skipBuild, ct))
                {
                    var entry = archive.CreateEntry(Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.Optimal);
                    using var input = File.OpenRead(file);
                    using var output = entry.Open();
                    CopyStream(input, output, ct);
                }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void CopyTree(string source, string destination, CancellationToken ct, bool skipBuild = true)
    {
        var sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (targetRoot.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("目标目录不能位于源目录内。");
        RejectLinks(destination);
        Directory.CreateDirectory(destination);
        foreach (var file in Files(source, skipBuild, ct))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            RejectLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = File.OpenRead(file);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            CopyStream(input, output, ct);
        }
    }

    private static string CheckDestination(string source, string destination)
    {
        var full = Path.GetFullPath(destination);
        var root = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("导出位置不能位于工作副本内。");
        RejectLinks(full);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("目标已存在，请选择新的文件或文件夹名称；不会覆盖现有文件。");
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new DirectoryNotFoundException("导出位置的父目录不存在。");
        return full;
    }

    private static IEnumerable<string> Files(string root, bool skipBuild, CancellationToken ct)
    {
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        long bytes = 0; var count = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var item))
        {
            ct.ThrowIfCancellationRequested(); RejectLinks(item.Path);
            if (item.Depth > 64) throw new IOException("文件目录层级超过 64 层。");
            foreach (var file in Directory.EnumerateFiles(item.Path))
            {
                ct.ThrowIfCancellationRequested(); RejectLinks(file);
                bytes += new FileInfo(file).Length;
                if (bytes > 512L * 1024 * 1024 || ++count > 10000) throw new IOException("副本超过 512 MiB / 1 万文件上限。");
                if (!names.Add(Path.GetRelativePath(root, file))) throw new IOException("存在仅大小写不同的重复文件名。");
                yield return file;
            }
            foreach (var directory in Directory.EnumerateDirectories(item.Path))
            {
                var name = Path.GetFileName(directory);
                if (skipBuild && name.ToLowerInvariant() is ".git" or ".vs" or "bin" or "obj") continue;
                pending.Push((directory, item.Depth + 1));
            }
        }
    }

    private static void CopyStream(Stream input, Stream output, CancellationToken ct)
    {
        var buffer = new byte[81920]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
    }

    private static void RejectLinks(string path)
    {
        var ancestors = new Stack<string>();
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            ancestors.Push(current);
        while (ancestors.Count > 0)
        {
            try
            {
                if ((File.GetAttributes(ancestors.Pop()) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不支持符号链接或重解析路径。");
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
        }
    }
}

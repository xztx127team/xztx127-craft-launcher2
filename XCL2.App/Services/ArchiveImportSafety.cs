using System.IO;
using System.IO.Compression;

namespace XCL2.App.Services;

/// <summary>Enforced by the writer, independently of optional UI previews.</summary>
internal static class ArchiveImportSafety
{
    internal const int MaximumEntries = 100000;
    internal const int MaximumManifestBytes = 4 * 1024 * 1024;

    internal static void CreateNewDirectory(string destination)
    {
        destination = Path.GetFullPath(destination); CheckLinks(destination);
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("实例目录不能是磁盘根目录。");
        Directory.CreateDirectory(parent); CheckLinks(parent);
        var temporary = Path.Combine(parent, ".xcl2-new-instance-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            // Unlike CreateDirectory(destination), this fails when another task already owns the target.
            Directory.Move(temporary, destination);
        }
        finally { try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); } catch { } }
    }

    internal static void CheckLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("导入或导出路径包含符号链接或目录联接，请选择真实目录。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\0') || Path.IsPathRooted(relative))
            throw new InvalidDataException("整合包包含无效路径。");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.TrimEnd(' ', '.') != p ||
            p.Any(c => c < 32) || p.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 || IsDeviceName(p)))
            throw new InvalidDataException("整合包包含不安全路径。");
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(normalized, Path.Combine(parts)));
        if (!full.StartsWith(normalized + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("整合包条目超出目标目录。");
        CheckLinks(full);
        return full;
    }

    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9';
    }

    internal static void ValidateArchive(ZipArchive archive, string root)
    {
        CheckLinks(root);
        if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("整合包超过十万项，请先整理后再导入。");
        foreach (var entry in archive.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("整合包包含符号链接，未执行导入。");
            var relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
            if (relative.Length > 0) _ = Resolve(root, relative);
        }
    }

    internal static string ReadManifest(ZipArchiveEntry entry)
    {
        if (entry.Length > MaximumManifestBytes) throw new InvalidDataException("整合包清单超过 4 MB。");
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaximumManifestBytes) throw new InvalidDataException("整合包清单超过 4 MB。");
            output.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    internal static void Extract(ZipArchiveEntry entry, string root, string relative, CancellationToken ct = default)
    {
        var destination = Resolve(root, relative);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        CheckLinks(parent);
        var temporary = Path.Combine(parent, ".xcl2-import-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = entry.Open())
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                output.Flush(true);
            }
            ct.ThrowIfCancellationRequested(); CheckLinks(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    internal static void ExportAtomically(string destination, Action<string> build)
    {
        destination = Path.GetFullPath(destination);
        CheckLinks(destination);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".xcl2-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            build(temporary);
            using (var output = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) output.Flush(true);
            CheckLinks(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}

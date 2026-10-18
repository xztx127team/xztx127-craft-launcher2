using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace XCL2.App.Services;

public enum ImportPreviewKind { InstanceFolder, InstanceZip, ModpackArchive }
public sealed record ImportPreview(string Name, string Destination, int FileCount, int ConflictCount,
    IReadOnlyList<string> ConflictExamples, bool DestinationExists, bool CountLimited)
{
    public int PendingDownloadCount { get; init; }
}

/// <summary>Bounded, read-only planning. No extraction, directory creation, network or symbolic-link traversal.</summary>
public static class ImportPreviewService
{
    public const int MaximumFiles = 100000;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;

    public static ImportPreview Inspect(string source, string destination, string name, ImportPreviewKind kind, CancellationToken ct = default)
    {
        var target = Path.GetFullPath(destination);
        EnsureNoLinks(target);
        var paths = kind == ImportPreviewKind.InstanceFolder ? FolderFiles(source, ct) : ArchiveFiles(source, kind, ct);
        var count = 0; var conflicts = 0; var examples = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var limited = false;
        foreach (var relative in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > MaximumFiles) { limited = true; count = MaximumFiles; break; }
            var full = Resolve(target, relative);
            EnsureNoLinks(full);
            if (!seen.Add(full) || File.Exists(full) || Directory.Exists(full))
            { conflicts++; if (examples.Count < 8) examples.Add(relative); }
        }
        return new(name, target, count, conflicts, examples, Directory.Exists(target), limited)
        { PendingDownloadCount = kind == ImportPreviewKind.ModpackArchive ? CurseForgePendingDownloads(source) : 0 };
    }

    private static IEnumerable<string> FolderFiles(string source, CancellationToken ct)
    {
        var root = Path.GetFullPath(source); EnsureNoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("导入源文件夹不存在。");
        var pending = new Stack<string>(); pending.Push(root); var visited = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested(); var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > MaximumFiles) throw new InvalidDataException("导入源条目过多，请先整理到十万项以内。");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("导入源含符号链接或目录联接，请先移除后再导入。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return Path.GetRelativePath(root, entry);
            }
        }
    }

    private static IEnumerable<string> ArchiveFiles(string source, ImportPreviewKind kind, CancellationToken ct)
    {
        EnsureNoLinks(Path.GetFullPath(source));
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Count > MaximumFiles) throw new InvalidDataException("压缩包条目过多，请先整理到十万项以内。");
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("压缩包含符号链接，无法进行安全导入预览。");
            _ = Resolve(Path.GetFullPath(Path.GetTempPath()), entry.FullName);
        }
        var prefix = "";
        if (kind == ImportPreviewKind.InstanceZip)
        {
            // Match FolderService's wrapper walk without extracting any payload.
            for (var depth = 0; depth < 64; depth++)
            {
                var entries = zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (entries.Any(e => !string.IsNullOrEmpty(e.Name) && !e.FullName.Replace('\\', '/')[prefix.Length..].Contains('/') && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) break;
                var children = entries.Select(e => e.FullName.Replace('\\', '/')[prefix.Length..]).Where(e => e.Contains('/')).Select(e => e.Split('/')[0]).Where(e => e != "__MACOSX").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var child = children.FirstOrDefault(e => e.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
                    ?? children.FirstOrDefault(e => e.Equals("versions", StringComparison.OrdinalIgnoreCase))
                    ?? (children.Length == 1 ? children[0] : null);
                if (child == null) break;
                prefix += child + "/";
            }
        }
        var mrpack = kind == ImportPreviewKind.ModpackArchive ? zip.GetEntry("modrinth.index.json") : null;
        if (mrpack != null)
        {
            if (mrpack.Length > MaximumManifestBytes) throw new InvalidDataException("整合包清单过大。");
            using var stream = mrpack.Open();
            using var bounded = new MemoryStream();
            var buffer = new byte[81920]; var total = 0; int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            { ct.ThrowIfCancellationRequested(); total += read; if (total > MaximumManifestBytes) throw new InvalidDataException("整合包清单过大。"); bounded.Write(buffer, 0, read); }
            bounded.Position = 0;
            using var document = JsonDocument.Parse(bounded, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                foreach (var file in files.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    if (file.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object && env.TryGetProperty("client", out var client) && client.GetString() == "unsupported") continue;
                    if (file.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String) yield return path.GetString()!;
                }
        }
        var cfPrefix = mrpack == null && kind == ImportPreviewKind.ModpackArchive ? CurseForgeOverridePrefix(zip) : null;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested(); if (string.IsNullOrEmpty(entry.Name)) continue;
            var path = entry.FullName.Replace('\\', '/');
            if (kind == ImportPreviewKind.InstanceZip)
            { if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield return path[prefix.Length..]; }
            else if (mrpack != null)
            { if (path.StartsWith("overrides/", StringComparison.Ordinal)) yield return path[10..]; else if (path.StartsWith("client-overrides/", StringComparison.Ordinal)) yield return path[17..]; }
            else if (cfPrefix != null)
            { if (path.StartsWith(cfPrefix, StringComparison.Ordinal)) yield return path[cfPrefix.Length..]; }
            else if (new[] { "mods", "config", "resourcepacks", "shaderpacks" }.Contains(path.Split('/')[0])) yield return path;
        }
    }

    private static string? CurseForgeOverridePrefix(ZipArchive zip)
    {
        if (zip.GetEntry("manifest.json") is not { } manifest) return null;
        using var document = OptionalCurseForgeManifest(manifest);
        if (document == null) return null;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("minecraft", out _) || !root.TryGetProperty("files", out _)) return null;
        var prefix = root.TryGetProperty("overrides", out var overrides) && overrides.ValueKind == JsonValueKind.String ? overrides.GetString() : "overrides";
        if (string.IsNullOrWhiteSpace(prefix)) throw new InvalidDataException("CurseForge overrides 路径无效。");
        return prefix.Replace('\\', '/').TrimEnd('/') + "/";
    }

    private static int CurseForgePendingDownloads(string source)
    {
        using var zip = ZipFile.OpenRead(source);
        if (zip.GetEntry("modrinth.index.json") != null || zip.GetEntry("manifest.json") is not { } manifest) return 0;
        using var document = OptionalCurseForgeManifest(manifest);
        if (document == null) return 0;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("minecraft", out _) || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return 0;
        return files.EnumerateArray().Count(file => !file.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.False);
    }

    private static JsonDocument? OptionalCurseForgeManifest(ZipArchiveEntry entry)
    {
        try { return JsonDocument.Parse(ArchiveImportSafety.ReadManifest(entry)); }
        catch (JsonException) { return null; }
    }

    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\0') || Path.IsPathRooted(relative)) throw new InvalidDataException("导入条目包含无效路径。");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "." or ".." || p.TrimEnd(' ', '.') != p || p.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0)) throw new InvalidDataException("导入条目包含不安全路径。");
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(normalized, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("导入条目超出目标目录。");
        return full;
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("导入路径包含符号链接或目录联接，请选择真实目录。");
    }
}

using System.IO;
using System.IO.Compression;

namespace XCL2.App.Services;

/// <summary>Stages downloaded worlds and refuses to overwrite or delete any published world.</summary>
internal static class WorldArchiveInstaller
{
    internal static Task InstallAsync(string archivePath, string targetDirectory,
        CancellationToken ct = default, string? suggestedName = null)
        => Task.Run(() => Install(archivePath, targetDirectory, ct, suggestedName), ct);

    private static void Install(string archivePath, string targetDirectory, CancellationToken ct, string? suggestedName)
    {
        ct.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(targetDirectory);
        ArchiveImportSafety.CheckLinks(target);
        using var archive = ZipFile.OpenRead(archivePath);
        ArchiveImportSafety.ValidateArchive(archive, target);
        var files = archive.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToArray();
        if (files.Length == 0) throw new InvalidDataException("地图压缩包没有文件。");

        // A flat world must be wrapped in its own directory, otherwise saves/level.dat is
        // invisible to Minecraft and can mingle with other imports.
        var flatWorld = files.Any(e => e.FullName.Equals("level.dat", StringComparison.OrdinalIgnoreCase));
        var wrapper = flatWorld ? SafeWorldName(suggestedName ?? Path.GetFileName(archivePath)) : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var relative = Relative(entry.FullName, wrapper);
            if (relative.Length == 0) continue;
            if (!seen.Add(relative)) throw new InvalidDataException("地图压缩包含重复路径（含仅大小写不同的路径），未执行安装。");
            var segments = relative.Split('/');
            for (var count = 1; count <= segments.Length; count++)
            {
                var prefix = string.Join('/', segments.Take(count));
                if (prefixCase.TryGetValue(prefix, out var original) && original != prefix)
                    throw new InvalidDataException("地图压缩包的目录大小写不一致，未执行安装。");
                prefixCase[prefix] = prefix;
            }
            roots.Add(relative.Split('/')[0]);
        }
        foreach (var root in roots) EnsureAbsent(ArchiveImportSafety.Resolve(target, root));

        Directory.CreateDirectory(target);
        ArchiveImportSafety.CheckLinks(target);
        var staging = Path.Combine(target, ".xcl2-world-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var entry in files)
            {
                ct.ThrowIfCancellationRequested();
                ArchiveImportSafety.Extract(entry, staging, Relative(entry.FullName, wrapper), ct);
            }
            // Preserve empty directories too. All paths were checked before the first write.
            foreach (var entry in archive.Entries.Except(files))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Relative(entry.FullName, wrapper);
                if (relative.Length > 0) Directory.CreateDirectory(ArchiveImportSafety.Resolve(staging, relative));
            }
            ct.ThrowIfCancellationRequested();
            // Directory renames are short. Once publication begins, finish it instead of
            // introducing partial publication merely because the user pressed Cancel.
            PublishStagedRoots(staging, target, roots);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { /* A locked staging directory can be removed after the locking process exits. */ }
        }
    }

    internal static void PublishStagedRoots(string staging, string target, IEnumerable<string> roots)
    {
        var published = new List<string>();
        try
        {
            foreach (var root in roots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            {
                var destination = ArchiveImportSafety.Resolve(target, root);
                EnsureAbsent(destination);
                var source = ArchiveImportSafety.Resolve(staging, root);
                // No overwrite even if a competing writer appeared after preflight.
                if (Directory.Exists(source)) Directory.Move(source, destination);
                else File.Move(source, destination);
                published.Add(root);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (published.Count == 0) throw;
            // A published directory is visible to Minecraft and external writers. Deleting
            // it on rollback could erase a newly opened or replaced world. Preserve it and
            // describe partial success explicitly instead of claiming a multi-root transaction.
            throw new IOException("地图导入未全部完成；已导入并保留：" + string.Join("、", published)
                + "。为保护存档未回滚这些目录；请检查目标目录后重试其余地图。", ex);
        }
    }

    private static string Relative(string entry, string? wrapper)
    {
        var relative = entry.Replace('\\', '/').TrimEnd('/');
        return wrapper == null || relative.Length == 0 ? relative : wrapper + "/" + relative;
    }

    private static void EnsureAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("地图目标已存在，为保护已有存档未覆盖：" + Path.GetFileName(path) + "。请选择空目录或先重命名已有存档。");
    }

    private static string SafeWorldName(string name)
    {
        // Remote filenames are labels, not filesystem paths.
        name = name.Replace('\\', '/').Split('/').Last();
        name = Path.GetFileNameWithoutExtension(name);
        var invalid = "<>:\"/\\|?*";
        name = new string(name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (name.Length > 100) name = name[..100].TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) name = "ImportedWorld";
        // Prefixing also keeps DOS device basenames (CON, AUX, COM1, ...) safe.
        return "World-" + name;
    }
}

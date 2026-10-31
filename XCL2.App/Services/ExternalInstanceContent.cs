using System.IO;

namespace XCL2.App.Services;

internal static class ExternalInstanceContent
{
    // Rebuild launcher-managed binaries. Preserve modpack content, worlds, screenshots, options and custom folders.
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { "versions", "libraries", "assets", "natives", ".git", "xcl", ".xcl2", ".xcl-external-import" };
    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    { "minecraftinstance.json", "profile.json", "mmc-pack.json", "instance.cfg", "launcher_profiles.json", "launcher_accounts.json", "accounts.json", "xcl-import-origin.json" };

    internal static void Copy(string source, string destination, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ArchiveImportSafety.CheckLinks(source); ArchiveImportSafety.CheckLinks(destination);
        var src = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dst = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase) || dst.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导入副本不能位于源目录内部。");
        if (Directory.Exists(destination)) throw new IOException("导入暂存目录已存在。");
        var pending = new Stack<(string Source, string Target, bool Top)>();
        pending.Push((source, destination, true));
        var count = 0;
        while (pending.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested(); ArchiveImportSafety.CheckLinks(entry.Source); ArchiveImportSafety.CheckLinks(entry.Target);
            Directory.CreateDirectory(entry.Target);
            foreach (var file in Directory.EnumerateFiles(entry.Source))
            {
                ct.ThrowIfCancellationRequested(); ArchiveImportSafety.CheckLinks(file);
                var fileName = Path.GetFileName(file);
                if (entry.Top && ExcludedFiles.Contains(fileName)) continue;
                if (++count > ArchiveImportSafety.MaximumEntries) throw new InvalidDataException("实例超过十万个内容文件或文件夹，请先整理后导入。");
                var target = ArchiveImportSafety.Resolve(entry.Target, fileName);
                // Hold a read handle that denies writes while copying; never copy a live changing world file silently.
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                if (count % 25 == 0) progress?.Report(new ProgressInfo("复制实例内容", count, 0, fileName));
            }
            foreach (var directory in Directory.EnumerateDirectories(entry.Source))
            {
                ct.ThrowIfCancellationRequested();
                var directoryName = Path.GetFileName(directory);
                if (entry.Top && ExcludedDirectories.Contains(directoryName)) continue;
                if (++count > ArchiveImportSafety.MaximumEntries) throw new InvalidDataException("实例超过十万个内容文件或文件夹，请先整理后导入。");
                ArchiveImportSafety.CheckLinks(directory);
                pending.Push((directory, ArchiveImportSafety.Resolve(entry.Target, directoryName), false));
            }
        }
    }
}

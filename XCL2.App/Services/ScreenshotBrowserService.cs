using System.IO;

namespace XCL2.App.Services;

public sealed record ScreenshotFile(string Path, string Name, long Size, DateTime ModifiedUtc);

public static class ScreenshotBrowserService
{
    public const int MaxFiles = 400;
    public const long MaxImageBytes = 40 * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };

    public static string GetDirectory(string gameDirectory)
    {
        var path = Path.GetFullPath(Path.Combine(gameDirectory, "screenshots"));
        RejectReparseAncestors(path);
        return path;
    }
    private static void RejectReparseAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("截图目录经过了链接或重定向目录，请直接使用实际实例目录。");
    }
    public static IReadOnlyList<ScreenshotFile> List(string gameDirectory)
    {
        var directory = GetDirectory(gameDirectory);
        if (!Directory.Exists(directory)) return Array.Empty<ScreenshotFile>();
        // Single-directory enumeration only. Never follows directories supplied inside a screenshot name.
        var result = new List<ScreenshotFile>();
        foreach (var path in Directory.EnumerateFiles(directory).Take(5000))
        {
            if (!Extensions.Contains(Path.GetExtension(path))) continue;
            try
            {
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > MaxImageBytes) continue;
                result.Add(new(file.FullName, file.Name, file.Length, file.LastWriteTimeUtc));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(f => f.ModifiedUtc).Take(MaxFiles).ToArray();
    }
    public static void Validate(string gameDirectory, ScreenshotFile snapshot)
    {
        var directory = GetDirectory(gameDirectory);
        var full = Path.GetFullPath(snapshot.Path);
        if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase) || !Extensions.Contains(Path.GetExtension(full)))
            throw new IOException("该文件不属于当前实例的截图目录。");
        var file = new FileInfo(full);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != snapshot.Size || file.LastWriteTimeUtc != snapshot.ModifiedUtc)
            throw new IOException("截图已变化，请刷新后重试。");
    }
    public static void Delete(string gameDirectory, ScreenshotFile snapshot)
    {
        Validate(gameDirectory, snapshot);
        File.Delete(snapshot.Path);
    }
    public static void Export(string gameDirectory, ScreenshotFile snapshot, string destination)
    {
        Validate(gameDirectory, snapshot);
        var target = Path.GetFullPath(destination);
        if (string.Equals(target, Path.GetFullPath(snapshot.Path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择截图原位置以外的保存位置。");
        File.Copy(snapshot.Path, target, overwrite: true);
    }
}

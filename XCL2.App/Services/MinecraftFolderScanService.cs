using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

public static class MinecraftFolderScanService
{
    private const string FolderName = ".minecraft";

    /// <summary>排除标记：目录下存在该名字的文件/文件夹，扫描就跳过该目录（不阻止手动添加）。</summary>
    public const string ExcludeMarkerName = ".NOXCL";

    /// <summary>
    /// 判断某个目录是否带有 .NOXCL 排除标记。只看是否存在同名条目（文件或文件夹都算），
    /// 不关心内容。IO 异常一律当作"没有标记"处理，不能因为探测标记本身出错而挂掉扫描。
    /// </summary>
    public static bool HasExcludeMarker(string dir)
    {
        try
        {
            var marker = Path.Combine(dir, ExcludeMarkerName);
            return File.Exists(marker) || Directory.Exists(marker);
        }
        catch
        {
            return false;
        }
    }

    private const int MaxDepth = 3;

    /// <summary>只发现路径，不读取或修改共享配置；可在工作线程调用。</summary>
    public static List<string> ScanCandidates()
    {
        var roots = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                if (SafeIsReady(drive)) roots.Add(drive.RootDirectory.FullName);
        }
        catch { /* 无法枚举磁盘时仍尝试 AppData */ }
        string? appData = null;
        try { appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        catch { }
        return ScanCandidates(roots, appData);
    }

    internal static List<string> ScanCandidates(IEnumerable<string> roots, string? appData)
    {
        var found = new List<string>();
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var candidate = Path.Combine(appData, FolderName);
            if (Directory.Exists(candidate) && !HasExcludeMarker(candidate)) found.Add(candidate);
        }
        foreach (var root in roots)
            ScanDriveForMinecraftFolders(root, found);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>在配置所属线程提交扫描快照，按提交时的配置去重，保留扫描期间的手动修改。</summary>
    public static List<GameFolder> RegisterCandidates(AppConfig config, IEnumerable<string> candidates)
    {
        var existing = config.Folders.Select(folder => NormalizePath(folder.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<GameFolder>();
        foreach (var candidate in candidates)
        {
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                // 发现到提交之间可能被移动、拔出或添加排除标记。自动扫描不能重新创建目录。
                if (!Directory.Exists(path) || HasExcludeMarker(path) || !existing.Add(NormalizePath(path))) continue;
                var folder = new GameFolder { Path = path, Name = GuessDisplayName(path) };
                config.Folders.Add(folder);
                added.Add(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { /* 一个失效候选不影响其他目录 */ }
        }
        return added;
    }

    /// <summary>同步兼容入口；UI 应分别在后台扫描、配置所属线程提交。</summary>
    public static List<GameFolder> ScanAndRegister(AppConfig config)
        => RegisterCandidates(config, ScanCandidates());

    /// <summary>逐级递归查找名字是 .minecraft 的子目录，最多下探 MaxDepth 层。
    /// 找到一个 .minecraft 目录就不再往它内部继续递归——.minecraft 目录内部本身可能有
    /// 几百个 mod/资源文件，没必要也不应该在里面继续找"更深一层的 .minecraft"。</summary>
    private static void ScanDriveForMinecraftFolders(string dir, List<string> found, int depth = 0)
    {
        if (depth >= MaxDepth || HasExcludeMarker(dir)) return;

        string[] subDirs;
        try
        {
            // EnumerateDirectories 的异常也可能发生在 MoveNext；在 try 内物化，
            // 防止一个断开的目录中断整个磁盘其余兄弟目录的扫描。
            subDirs = Directory.GetDirectories(dir);
        }
        catch
        {
            return; // 没权限访问 / 目录已被移除等，跳过这一层，不影响兄弟目录
        }

        foreach (var sub in subDirs)
        {
            string name;
            try { name = Path.GetFileName(sub); }
            catch { continue; }

            if (string.IsNullOrEmpty(name)) continue;

            // 跳过系统/回收站等明显不会是游戏目录、且体积或权限问题容易拖慢扫描的特殊目录
            if (name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))
                continue;

            // .NOXCL 排除标记：不管该目录是不是 .minecraft，只要带了标记就整个跳过——
            // 既不把它收进自动发现列表（哪怕它就叫 .minecraft），也不再往它内部递归查找。
            // 只影响自动扫描；用户手动"添加已有文件夹"不受此限制。
            if (HasExcludeMarker(sub)) continue;
            try
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (string.Equals(name, FolderName, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(sub);
                continue; // 命中了就不再往该目录内部继续扫
            }

            ScanDriveForMinecraftFolders(sub, found, depth + 1);
        }
    }

    /// <summary>DriveInfo.IsReady 在某些异常盘符（没插盘的读卡器、断开的网络映射盘）上
    /// 本身可能抛异常而不是老老实实返回 false，这里包一层保护。</summary>
    private static bool SafeIsReady(DriveInfo drive)
    {
        try { return drive.IsReady; }
        catch { return false; }
    }

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(path.Replace('/', '\\')).ToLowerInvariant();

    /// <summary>给自动发现的文件夹起个比"当前文件夹"更有辨识度的显示名，带上上一级目录名，
    /// 方便用户在有多个 .minecraft 时分清楚哪个是哪个，比如 "Games\.minecraft" 而不是
    /// 一堆都叫 ".minecraft" 分不清谁是谁。</summary>
    private static string GuessDisplayName(string path)
    {
        var parent = Directory.GetParent(path)?.Name;
        return string.IsNullOrEmpty(parent) ? FolderName : $"{parent}\\{FolderName}";
    }
}

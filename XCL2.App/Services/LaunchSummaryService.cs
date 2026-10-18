using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed record LaunchSummaryRow(string Label, string Value);

/// <summary>只读的启动预览。不会探测 Java 进程、创建实例设置、下载文件或运行启动命令。</summary>
public static class LaunchSummaryService
{
    /// <summary>UI线程只复制摘要所需的偏好；不序列化聊天记录、密钥或其他大型配置。</summary>
    public static AppConfig Capture(AppConfig source)
    {
        var selected = source.SelectedVersionId;
        var folder = source.Folders?.FirstOrDefault(f => string.Equals(f.Path, source.SelectedFolderPath,
            StringComparison.OrdinalIgnoreCase));
        var copy = new AppConfig
        {
            SelectedVersionId = selected, SelectedFolderPath = source.SelectedFolderPath,
            Folders = folder == null ? new() : new() { new GameFolder { Name = folder.Name, Path = folder.Path } },
            IsolateVersionsByDefault = source.IsolateVersionsByDefault,
            AdvancedMode = source.AdvancedMode, PreferredJavaMajorVersion = source.PreferredJavaMajorVersion,
            SelectedJavaId = source.SelectedJavaId,
            InstalledJavas = (source.InstalledJavas ?? new()).Where(j => j != null).Select(j => new InstalledJava
            { Id = j.Id, Name = j.Name, JavawPath = j.JavawPath, MajorVersion = j.MajorVersion, Priority = j.Priority }).ToList(),
            MinMemoryMb = source.MinMemoryMb, MaxMemoryMb = source.MaxMemoryMb,
            EnableMemoryOptimization = source.EnableMemoryOptimization, MemoryAllocationMode = source.MemoryAllocationMode,
            DynamicMemoryMinimumMb = source.DynamicMemoryMinimumMb, DynamicMemoryMaximumMb = source.DynamicMemoryMaximumMb,
            DynamicMemoryMinimumPercent = source.DynamicMemoryMinimumPercent, DynamicMemoryMaximumPercent = source.DynamicMemoryMaximumPercent,
            FixedMemoryAllocationMb = source.FixedMemoryAllocationMb, MemoryDisplayBasis = source.MemoryDisplayBasis
        };
        if (!string.IsNullOrWhiteSpace(selected))
        {
            if (source.VersionIsolationOverrides.TryGetValue(selected, out var isolation)) copy.VersionIsolationOverrides[selected] = isolation;
            if (source.VersionJavaOverrides.TryGetValue(selected, out var major)) copy.VersionJavaOverrides[selected] = major;
            if (source.VersionJavaIdOverrides.TryGetValue(selected, out var javaId)) copy.VersionJavaIdOverrides[selected] = javaId;
        }
        return copy;
    }

    public static IReadOnlyList<LaunchSummaryRow> Read(AppConfig config, string accountLabel,
        string loginStatus, int? sessionMinMemory, int? sessionMaxMemory, CancellationToken ct)
    {
        var rows = new List<LaunchSummaryRow>
        {
            new("账户", accountLabel), new("登录状态", loginStatus)
        };
        var folder = config.Folders?.FirstOrDefault(f => string.Equals(f.Path, config.SelectedFolderPath,
            StringComparison.OrdinalIgnoreCase));
        var id = config.SelectedVersionId;
        if (folder == null || string.IsNullOrWhiteSpace(id))
        {
            rows.Add(new("实例", "尚未选择实例，请先在版本选择页选择"));
            return rows;
        }
        if (id != Path.GetFileName(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id is "." or "..")
            throw new InvalidDataException("实例名称无效，请重新选择实例。");
        ct.ThrowIfCancellationRequested();
        var versionDir = Path.Combine(folder.Path, "versions", id);
        var instance = InstanceConfigService.TryLoad(versionDir);
        var isolate = config.VersionIsolationOverrides.TryGetValue(id, out var value) ? value : config.IsolateVersionsByDefault;
        var gameDir = isolate ? versionDir : folder.Path;
        rows.Add(new("实例", id));
        rows.Add(new("运行目录", gameDir));
        rows.Add(new("版本隔离", isolate ? "已开启" : "未开启，与同目录实例共享存档、模组和截图"));

        var requiredMajor = LauncherService.GetRequiredJavaMajorVersion(folder.Path, id, isolate);
        int? preferredMajor = config.VersionJavaOverrides.TryGetValue(id, out var major) && major > 0 ? major
            : requiredMajor ?? (config.AdvancedMode ? config.PreferredJavaMajorVersion : (int?)null);
        var exactId = config.VersionJavaIdOverrides.TryGetValue(id, out var javaId) ? javaId : config.SelectedJavaId;
        var java = config.InstalledJavas?.FirstOrDefault(j => j.Id == exactId && File.Exists(j.JavawPath));
        java ??= config.InstalledJavas?.OrderBy(j => j.Priority).FirstOrDefault(j => File.Exists(j.JavawPath) &&
            (preferredMajor is not > 0 || (j.MajorVersion is int m && JavaService.IsJavaMajorCompatible(m, preferredMajor.Value))));
        var javaPath = java?.JavawPath;
        rows.Add(new("Java", java == null
            ? "启动时检测可用 Java" + (preferredMajor is > 0 ? $"（要求主版本 {preferredMajor}）" : "")
            : $"{java.Name}\n{java.JavawPath}\n此为本地登记候选，启动时会重新检查"));
        var memory = MemoryOptimizerService.ConstrainForJava(config,
            MemoryOptimizerService.ResolveLaunchMemory(config, instance), JavaService.TryGetJavaBitness(javaPath));
        if (sessionMinMemory.HasValue || sessionMaxMemory.HasValue)
        {
            var limits = CommandLineSessionPolicy.ResolveMemory(memory.MinMemoryMb, memory.MaxMemoryMb,
                sessionMinMemory, sessionMaxMemory);
            memory = new(limits.MinMb, limits.MaxMb, false, "本次命令行覆盖");
        }
        rows.Add(new("内存", $"最小 {memory.MinMemoryMb:N0} MB / 最大 {memory.MaxMemoryMb:N0} MB" +
            (memory.IsDynamic ? "\n动态值，实际启动时重新计算" : "")));
        ct.ThrowIfCancellationRequested();
        var mods = Path.Combine(gameDir, "mods");
        var count = 0;
        if (Directory.Exists(mods))
            foreach (var file in Directory.EnumerateFiles(mods, "*.jar", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) count++;
            }
        rows.Add(new("启用的模组", $"{count:N0} 个 JAR 文件（不包含 .disabled，不代表兼容性检查结果）"));
        return rows;
    }
}

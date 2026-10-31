using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 删除实例的两种模式：
///   1. 从列表中删除（不删文件）——把该实例加进 <see cref="AppConfig.HiddenInstanceKeys"/>
///      黑名单，下次扫描时过滤掉，磁盘上的 versions/&lt;id&gt; 目录原封不动。用户随时可以在
///      "已隐藏的实例"里把它取消隐藏，重新出现在列表里，找回来的文件不需要任何额外操作
///      （本来就没动过）。
///   2. 从电脑中删除（删除所有文件）——物理删除 versions/&lt;id&gt; 整个目录，并清掉所有跟该
///      版本相关的全局配置字典条目（Java 覆盖/隔离覆盖/自动加入服务器等），避免这些残留
///      条目将来被同名新实例意外继承。这一步不可撤销，调用方负责在动手之前用
///      DangerousConfirmDialog（xztx127 确认码）拦一道，本服务本身不做确认交互，只负责
///      "确认通过后到底怎么删"这部分。
/// </summary>
public static class InstanceDeletionService
{
    /// <summary>拼出 <see cref="AppConfig.HiddenInstanceKeys"/> 用的复合 key：
    /// "{文件夹路径}|{版本Id}"，统一转小写、路径分隔符标准化，避免大小写/斜杠差异导致
    /// 同一个实例被判定成两个不同的 key。</summary>
    public static string BuildKey(string folderPath, string versionId)
    {
        var normalizedFolder = (folderPath ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var normalizedId = (versionId ?? "").ToLowerInvariant();
        return $"{normalizedFolder}|{normalizedId}";
    }

    /// <summary>某个实例是否已经被"从列表中删除"（隐藏）。</summary>
    public static bool IsHidden(AppConfig config, string folderPath, string versionId)
        => config.HiddenInstanceKeys.Contains(BuildKey(folderPath, versionId), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 从列表中删除（不删文件）：把实例加进隐藏黑名单。调用方需要自行调用
    /// ConfigService.Save() 落盘并刷新列表（跟其它设置改动的写回方式保持一致，
    /// 该方法只改内存里的 config 对象，不负责保存和刷新界面）。
    /// </summary>
    public static void HideFromList(AppConfig config, string folderPath, string versionId)
    {
        var key = BuildKey(folderPath, versionId);
        if (!config.HiddenInstanceKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            config.HiddenInstanceKeys.Add(key);
    }

    /// <summary>取消隐藏，实例重新出现在列表里（文件本来就没动过，这里只是把黑名单条目摘掉）。</summary>
    public static void UnhideFromList(AppConfig config, string folderPath, string versionId)
    {
        var key = BuildKey(folderPath, versionId);
        config.HiddenInstanceKeys.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
    }

    public static void Rename(AppConfig config, string folderPath, string oldVersionId, string newVersionId)
    {
        var oldDir = ResolveVersionDirectory(folderPath, oldVersionId);
        var newDir = ResolveVersionDirectory(folderPath, newVersionId);
        if (string.Equals(oldDir, newDir, StringComparison.Ordinal)) return;

        if (!Directory.Exists(oldDir))
            throw new DirectoryNotFoundException($"找不到实例目录：{oldDir}");
        if (Directory.Exists(newDir))
            throw new IOException($"目标名称已存在：{newDir}");

        Directory.Move(oldDir, newDir);

        MoveDictionaryEntry(config.VersionIsolationOverrides, oldVersionId, newVersionId);
        MoveDictionaryEntry(config.VersionResourcePackIsolationOverrides, oldVersionId, newVersionId);
        MoveDictionaryEntry(config.VersionJavaOverrides, oldVersionId, newVersionId);
        MoveDictionaryEntry(config.VersionJavaIdOverrides, oldVersionId, newVersionId);
        MoveDictionaryEntry(config.VersionAutoJoinServer, oldVersionId, newVersionId);
        if (config.VersionSkipJavaMismatchPrompt.Remove(oldVersionId))
            config.VersionSkipJavaMismatchPrompt.Add(newVersionId);
        RenameScopedReferences(config, folderPath, oldVersionId, newVersionId);

        for (int i = 0; i < config.FavoriteVersionIds.Count; i++)
            if (string.Equals(config.FavoriteVersionIds[i], oldVersionId, StringComparison.OrdinalIgnoreCase))
                config.FavoriteVersionIds[i] = newVersionId;

        foreach (var fav in config.FavoriteItems)
            if (fav.Type == FavoriteItemType.Version && string.Equals(fav.SourceId, oldVersionId, StringComparison.OrdinalIgnoreCase))
                fav.SourceId = newVersionId;

        if (IsHidden(config, folderPath, oldVersionId))
        {
            UnhideFromList(config, folderPath, oldVersionId);
            HideFromList(config, folderPath, newVersionId);
        }

        if (BuildKey(config.SelectedFolderPath ?? "", config.SelectedVersionId ?? "") == BuildKey(folderPath, oldVersionId))
            config.SelectedVersionId = newVersionId;
        Plugins.PluginHostBridge.Current?.NotifyInstanceRenamed(folderPath, oldVersionId, newVersionId);
    }

    // The service is also called by plugins, so UI validation is not the write boundary.
    // Reject path traversal, DOS device names, ADS, and directory junctions before moving/deleting.
    internal static string ResolveVersionDirectory(string folderPath, string versionId)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Path.IsPathFullyQualified(folderPath))
            throw new InvalidDataException("游戏目录必须是完整的绝对路径。");
        if (string.IsNullOrWhiteSpace(versionId) || versionId.IndexOfAny(new[] { '/', '\\' }) >= 0)
            throw new InvalidDataException("实例名称必须是有效的单级目录名称。");
        return ArchiveImportSafety.Resolve(Path.Combine(folderPath, "versions"), versionId);
    }

    private static void RenameScopedReferences(AppConfig config, string folderPath, string oldId, string newId)
    {
        var oldKey = BuildKey(folderPath, oldId);
        var newKey = BuildKey(folderPath, newId);
        if (config.InstancePersonalizations != null && config.InstancePersonalizations.Remove(oldKey, out var metadata))
            config.InstancePersonalizations[newKey] = metadata;
        if (config.FavoriteInstanceKeys != null)
            for (var i = 0; i < config.FavoriteInstanceKeys.Count; i++)
                if (string.Equals(config.FavoriteInstanceKeys[i], oldKey, StringComparison.OrdinalIgnoreCase))
                    config.FavoriteInstanceKeys[i] = newKey;
        foreach (var recent in config.RecentInstances ?? new())
            if (recent != null && BuildKey(recent.FolderPath, recent.VersionId) == oldKey) recent.VersionId = newId;
    }

    private static void MoveDictionaryEntry<TValue>(Dictionary<string, TValue> dict, string oldKey, string newKey)
    {
        if (dict.Remove(oldKey, out var value)) dict[newKey] = value;
    }

    /// <summary>
    /// 从电脑中删除：物理删除 versions/&lt;id&gt; 目录 + 清理所有跟该版本相关的全局配置
    /// 字典条目。调用方需要先经过 xztx127 二次确认，这里不再重复确认，直接执行。
    /// 目录删除失败（文件被占用等）会抛异常，调用方自行捕获展示错误，不在这里吞掉——
    /// 删除失败但配置字典已经清了一半这种"半成品"状态好过静默失败、用户以为删掉了实际没删。
    /// 为了避免这种半成品，这里先删目录，成功之后才清配置字典。
    /// </summary>
    public static void DeletePermanently(AppConfig config, string folderPath, string versionId)
    {
        var existed = DeleteFiles(folderPath, versionId, recycle: false);
        CleanupConfigReferences(config, folderPath, versionId);
        if (existed) Plugins.PluginHostBridge.Current?.NotifyInstanceRemoved(folderPath, versionId);
    }

    /// <summary>Only filesystem work runs in the background; config and notifications resume on the caller's context.</summary>
    public static async Task DeletePermanentlyAsync(AppConfig config, string folderPath, string versionId)
    {
        var existed = await Task.Run(() => DeleteFiles(folderPath, versionId, recycle: false));
        CleanupConfigReferences(config, folderPath, versionId);
        if (existed) Plugins.PluginHostBridge.Current?.NotifyInstanceRemoved(folderPath, versionId);
    }

    /// <summary>
    /// 从电脑中删除，但送进回收站而不是直接抹掉——用户点错了还能从回收站救回来。
    /// 用 Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory 而不是自己拼 Shell API，
    /// 这是 .NET 里做"删到回收站"最简单可靠的方式（UseWindowsForms 已经为真，该程序集
    /// 不需要额外加 NuGet 包，直接能用）。因为可撤销，调用方不需要像永久删除那样强制
    /// xztx127 二次确认。
    /// </summary>
    public static void DeleteToRecycleBin(AppConfig config, string folderPath, string versionId)
    {
        var existed = DeleteFiles(folderPath, versionId, recycle: true);
        CleanupConfigReferences(config, folderPath, versionId);
        if (existed) Plugins.PluginHostBridge.Current?.NotifyInstanceRemoved(folderPath, versionId);
    }

    public static async Task DeleteToRecycleBinAsync(AppConfig config, string folderPath, string versionId)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.SetResult(DeleteFiles(folderPath, versionId, recycle: true)); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "XCL2 recycle instance" };
        if (OperatingSystem.IsWindows()) worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        var existed = await completion.Task;
        CleanupConfigReferences(config, folderPath, versionId);
        if (existed) Plugins.PluginHostBridge.Current?.NotifyInstanceRemoved(folderPath, versionId);
    }

    private static bool DeleteFiles(string folderPath, string versionId, bool recycle)
    {
        var versionDir = ResolveVersionDirectory(folderPath, versionId);
        var existed = Directory.Exists(versionDir);
        if (existed)
        {
            if (recycle)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    versionDir,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            else Directory.Delete(versionDir, recursive: true);
            if (Directory.Exists(versionDir)) throw new IOException("实例目录仍然存在，未清除配置或报告删除成功。");
        }
        return existed;
    }

    /// <summary>两种"从电脑中删除"模式共用的配置清理逻辑，从原来的 DeletePermanently 里
    /// 抽出来，避免 DeleteToRecycleBin 复制一遍同样的字典清理代码。</summary>
    private static void CleanupConfigReferences(AppConfig config, string folderPath, string versionId)
    {
        config.VersionIsolationOverrides.Remove(versionId);
        config.VersionResourcePackIsolationOverrides.Remove(versionId);
        config.VersionJavaOverrides.Remove(versionId);
        config.VersionJavaIdOverrides.Remove(versionId);
        config.VersionAutoJoinServer.Remove(versionId);
        config.VersionSkipJavaMismatchPrompt.Remove(versionId);
        var key = BuildKey(folderPath, versionId);
        config.InstancePersonalizations?.Remove(key);
        config.FavoriteInstanceKeys?.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        config.RecentInstances?.RemoveAll(r => r != null && BuildKey(r.FolderPath, r.VersionId) == key);
        config.FavoriteVersionIds.RemoveAll(id => string.Equals(id, versionId, StringComparison.OrdinalIgnoreCase));
        config.FavoriteItems.RemoveAll(f => f.Type == FavoriteItemType.Version
            && string.Equals(f.SourceId, versionId, StringComparison.OrdinalIgnoreCase));

        UnhideFromList(config, folderPath, versionId);

        if (BuildKey(config.SelectedFolderPath ?? "", config.SelectedVersionId ?? "") == BuildKey(folderPath, versionId))
            config.SelectedVersionId = null;
    }
}

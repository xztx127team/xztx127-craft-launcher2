namespace XCL2.App.Models;

public partial class AppConfig
{
    [ExperiencePreference("实例与模组细节", "共享游戏目录标记", Description = "仅在多个实例实际共用同一游戏目录时显示。")]
    public bool Ux50ShowSharedGameDirectory { get; set; } = true;
    [ExperiencePreference("实例与模组细节", "切换实例后的后台任务归属", Description = "实例页仅在存在其他实例的后台任务时显示。")]
    public bool Ux50ShowBackgroundTaskOwnership { get; set; } = true;
    [ExperiencePreference("实例与模组细节", "导入前预览", Description = "写入前查看目标、文件数量和同名冲突；预览不会解压或创建目录。")]
    public bool Ux50ImportPreview { get; set; } = true;
    [ExperiencePreference("实例与模组细节", "同名文件夹显示所属目录")]
    public bool Ux50DisambiguateFolderNames { get; set; } = true;
    [ExperiencePreference("实例与模组细节", "实例重命名前预览")]
    public bool Ux50RenamePreview { get; set; } = true;
    [ExperiencePreference("实例与模组细节", "批量启停模组前显示数量摘要")]
    public bool Ux50BulkModSummary { get; set; } = true;
    [ExperiencePreference("实例与模组细节", "提示筛选后隐藏的已选模组")]
    public bool Ux50ShowHiddenModSelections { get; set; } = true;
    [ExperiencePreference("实例与模组细节", "显示模组资料缓存时间", Description = "资料仅在手动检查更新后缓存于本次会话，不会后台联网。")]
    public bool Ux50ShowCachedModMetadata { get; set; } = true;
    public List<IgnoredModUpdateVersion> Ux50IgnoredModUpdateVersions { get; set; } = new();
}

public sealed class IgnoredModUpdateVersion
{
    public string Scope { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string VersionName { get; set; } = "";
}

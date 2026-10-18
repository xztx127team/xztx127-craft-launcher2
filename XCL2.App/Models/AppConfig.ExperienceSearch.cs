namespace XCL2.App.Models;

public partial class AppConfig
{
    [ExperiencePreference("搜索与列表", "中文输入完成后再搜索")]
    public bool Ux50SearchWaitForComposition { get; set; } = true;
    [ExperiencePreference("搜索与列表", "本地搜索支持多个关键词")]
    public bool Ux50SearchMultipleKeywords { get; set; } = true;
    [ExperiencePreference("搜索与列表", "轻量标记命中的文字")]
    public bool Ux50SearchHighlight { get; set; } = true;
    [ExperiencePreference("搜索与列表", "显示可单独移除的筛选标签")]
    public bool Ux50SearchFilterChips { get; set; } = true;
    [ExperiencePreference("搜索与列表", "Esc 先收起建议、再清空搜索")]
    public bool Ux50SearchLayeredEscape { get; set; } = true;
    [ExperiencePreference("搜索与列表", "允许保存筛选组合", Description = "只在搜索框菜单中主动保存，关闭后不读取或修改已保存的组合。")]
    public bool Ux50SearchSavedFilters { get; set; }
    [ExperiencePreference("搜索与列表", "保存本地搜索历史", Description = "仅记录主动提交的搜索；可在搜索框菜单中删除单条或全部清空。关闭后不记录，也不显示历史。")]
    public bool Ux50SearchHistory { get; set; }
    [ExperiencePreference("搜索与列表", "本地列表显示排序方向并保持同值顺序")]
    public bool Ux50SearchStableSort { get; set; } = true;
    [ExperiencePreference("搜索与列表", "显示本页实际加载数量")]
    public bool Ux50SearchLoadedCounts { get; set; } = true;

    public string Ux50VersionListSort { get; set; } = "newest";
    public List<ExperienceSearchHistory> Ux50SearchHistoryItems { get; set; } = new();
    public List<ExperienceSavedFilter> Ux50SearchFilterPresets { get; set; } = new();
    public Dictionary<string, List<string>> Ux50TableHiddenColumns { get; set; } = new();
}

public sealed class ExperienceSearchHistory
{
    public string Scope { get; set; } = "";
    public string Query { get; set; } = "";
}

public sealed class ExperienceSavedFilter
{
    public string Scope { get; set; } = "";
    public string Label { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = new();
}

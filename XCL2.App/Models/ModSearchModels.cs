namespace XCL2.App.Models;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

/// <summary>下载中心「Mod」分类的数据源选择。综合 = 同时查 Modrinth + CurseForge（MC百科结果单独展示，
/// 因为它没有直接下载能力，混进统一下载列表会误导用户点了却装不上）。</summary>
public enum ModSource
{
    Combined,
    Modrinth,
    CurseForge
}

/// <summary>收藏夹里一条收藏项的"类型"：游戏版本，或者下载中心里的四种社区资源。</summary>
public enum FavoriteItemType
{
    Version,
    Mod,
    ResourcePack,
    DataPack,
    Shader,
    Map,
    BedrockClient,
    BedrockServer
}

public class FavoriteItem
{
    public FavoriteItemType Type { get; set; }
    public ModSource Source { get; set; }
    public string SourceId { get; set; } = "";

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string? IconUrl { get; set; }
    public long Downloads { get; set; }

    /// <summary>是否显示图标，跟 Mod/资源包/地图列表同一套开关（AppConfig.ShowModIcons），
    /// 由 DownloadCenterPage 刷新"我的收藏"面板时统一赋值，不参与配置文件的 JSON 往返
    /// （每次打开收藏面板都会重新按当前设置赋值一次，不需要持久化该展示状态）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowIcon { get; set; } = true;

    /// <summary>实际绑定给 Image.Source 用的值：关闭图标显示、或者这条收藏本身没存图标时给 null。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DisplayIconUrl => ShowIcon ? IconUrl : null;

    /// <summary>判断这条收藏记录是否对应给定的 (类型, 来源ID, 来源平台)。
    /// Type=Version 时按约定 Source 恒为 Combined，调用方传 ModSource.Combined 即可命中。</summary>
    public bool MatchesKey(FavoriteItemType type, string sourceId, ModSource source)
        => Type == type && SourceId == sourceId && Source == source;
}

/// <summary>统一的 Mod 搜索结果条目，屏蔽 Modrinth/CurseForge 数据结构差异，供 UI 统一绑定展示。</summary>
public class UnifiedModItem : INotifyPropertyChanged
{
    public ModSource Source { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string? IconUrl { get; set; }
    public long Downloads { get; set; }

    /// <summary>是否显示图标，由页面根据 AppConfig.ShowModIcons 在结果加载完成/设置变更时统一赋值
    /// （不用 XAML 转换器绑定 Config 是因为项目里目前没有 IValueConverter 基础设施，这样更省事，
    /// 也不用给每个 DataTemplate 单独接一份 Converter 资源）。默认 true，跟 ShowModIcons 默认值一致。</summary>
    public bool ShowIcon { get; set; } = true;

    /// <summary>实际绑定给 Image.Source 用的值：关闭图标显示、或者没有图标 URL 时给 null，
    /// WPF 的 Image 控件在 Source 为 null 时不会渲染任何内容（不会报错/不会显示裂图占位）。</summary>
    public string? DisplayIconUrl => ShowIcon ? IconUrl : null;

    /// <summary>Modrinth 用 project_id (字符串)，CurseForge 用 modId (数字)，两种来源用同一个字段存字符串形式，
    /// 具体下载时靠 Source 判断走哪条链路重新按 id 查询。</summary>
    public string SourceId { get; set; } = "";

    public string SourceLabel => Source switch
    {
        ModSource.Modrinth => "Modrinth",
        ModSource.CurseForge => "CurseForge",
        _ => ""
    };

    /// <summary>原始对象引用，方便下载时不用重新反查（Modrinth: ModrinthSearchHit；CurseForge: CurseForgeMod）。</summary>
    public object? RawItem { get; set; }

    private bool _isFavorite;
    /// <summary>是否已收藏。在结果加载完成时由 DownloadCenterPage 按 FavoriteItems 里
    /// 是否存在匹配的 (Mod, SourceId, Source) 赋值，点击"☆ 收藏"按钮时切换。</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite == value) return; _isFavorite = value; OnPropertyChanged(); OnPropertyChanged(nameof(FavoriteLabel)); }
    }
    public string FavoriteLabel => IsFavorite ? "★ 已收藏" : "☆ 收藏";

    private bool _isExpanded;
    /// <summary>是否展开显示版本列表。同一时刻通常只有一个条目展开（由调用方在展开新条目前
    /// 把其余条目收起），但这里不强制，留给调用方决定要不要支持多个同时展开。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
    }

    private bool _isLoadingVersions;
    /// <summary>正在拉取版本列表（展开动画期间显示"加载中"提示用）。</summary>
    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        set { if (_isLoadingVersions == value) return; _isLoadingVersions = value; OnPropertyChanged(); }
    }

    private bool _hasNoResults;
    /// <summary>加载完成但版本列表为空时置 true，供"没有找到匹配的版本"提示绑定显示。
    /// 不直接绑 Versions.Count 是因为 ObservableCollection.Count 变化不会自动触发
    /// PropertyChanged("Versions.Count") 这种路径绑定刷新，显式维护一个 bool 更可靠。</summary>
    public bool HasNoResults
    {
        get => _hasNoResults;
        set { if (_hasNoResults == value) return; _hasNoResults = value; OnPropertyChanged(); }
    }

    /// <summary>展开后显示的版本/文件列表，首次展开时异步填充；之后再次展开直接复用，不重复请求网络。
    /// 保留该扁平集合供旧的/其他还没切到分组展示的调用点继续绑定，不强制一次性改完全部使用点。</summary>
    public ObservableCollection<InlineVersionEntry> Versions { get; } = new();

    public ObservableCollection<VersionGroup> Groups { get; } = new();

    /// <summary>是否已经成功拉取过一次版本列表（用于"再次点击直接展开，不重复请求"的判断，
    /// 跟 Versions.Count == 0 不完全等价——请求失败/无结果时 Count 也是 0，不该被判定为"已加载过"）。</summary>
    public bool VersionsLoaded { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 统一的材质包/光影包/数据包搜索结果条目，跟 UnifiedModItem 是同一个思路：屏蔽 Modrinth/CurseForge
/// 数据结构差异，供「下载中心」资源面板统一绑定展示。之前该面板只查 Modrinth，没有该类型，
/// 现在补上 CurseForge 结果后需要一个统一的形状。
/// </summary>
public class UnifiedResourceItem : INotifyPropertyChanged
{
    public ModSource Source { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string? IconUrl { get; set; }
    public long Downloads { get; set; }

    /// <summary>是否显示图标，规则同 UnifiedModItem.ShowIcon。</summary>
    public bool ShowIcon { get; set; } = true;
    public string? DisplayIconUrl => ShowIcon ? IconUrl : null;

    /// <summary>Modrinth 用 project_id，CurseForge 用 modId 的字符串形式。</summary>
    public string SourceId { get; set; } = "";

    public string SourceLabel => Source switch
    {
        ModSource.Modrinth => "Modrinth",
        ModSource.CurseForge => "CurseForge",
        _ => ""
    };

    /// <summary>原始对象引用：Modrinth: ModrinthSearchHit；CurseForge: CurseForgeMod。</summary>
    public object? RawItem { get; set; }

    /// <summary>这条资源具体是哪一类（材质包/数据包/光影包），收藏时要写进 FavoriteItem.Type，
    /// 由 DownloadCenterPage 在构造 UnifiedResourceItem 时按当前 _currentResourceType 赋值。</summary>
    public FavoriteItemType FavoriteType { get; set; } = FavoriteItemType.ResourcePack;

    private bool _isFavorite;
    /// <summary>是否已收藏，规则同 UnifiedModItem.IsFavorite。</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite == value) return; _isFavorite = value; OnPropertyChanged(); OnPropertyChanged(nameof(FavoriteLabel)); }
    }
    public string FavoriteLabel => IsFavorite ? "★ 已收藏" : "☆ 收藏";

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
    }

    private bool _isLoadingVersions;
    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        set { if (_isLoadingVersions == value) return; _isLoadingVersions = value; OnPropertyChanged(); }
    }

    private bool _hasNoResults;
    public bool HasNoResults
    {
        get => _hasNoResults;
        set { if (_hasNoResults == value) return; _hasNoResults = value; OnPropertyChanged(); }
    }

    public ObservableCollection<InlineVersionEntry> Versions { get; } = new();

    /// <summary>按"加载器 + 游戏版本"分组后的展示数据，规则同 UnifiedModItem.Groups——
    /// LoadResourceVersionsAsync 在 Versions 填充完成后调用 ModVersionGrouping.Group(Versions)
    /// 生成这份数据，整页详情（ModDetailPage.ShowGroups）改绑该字段而不是 Versions。</summary>
    public ObservableCollection<VersionGroup> Groups { get; } = new();

    public bool VersionsLoaded { get; set; }

    /// <summary>数据包场景专用：展开面板里"安装到哪个存档"下拉框的候选列表和当前选中项。
    /// 之前这一步是弹窗里的 SaveCombo，现在挪进展开面板内联展示，逻辑不变——数据包必须
    /// 挂在具体存档下才生效，所以选存档这一步不能省略，只是从"弹窗里的控件"变成
    /// "卡片展开面板里的控件"。非数据包类型该集合始终为空，UI 上对应的下拉框不会显示
    /// （由展开面板 DataTemplate 按 IsDataPack 绑定可见性控制）。</summary>
    public ObservableCollection<string> SaveNames { get; } = new();
    public bool IsDataPack { get; set; }

    private string? _selectedSaveName;
    public string? SelectedSaveName
    {
        get => _selectedSaveName;
        set { if (_selectedSaveName == value) return; _selectedSaveName = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 一键开服向导里"已选清单"的一条记录：用户在 ModDetailPage 里挑了具体版本后点"加入清单"，
/// 就生成这么一条，取代旧版"勾选列表后直接装自动匹配的最新版本"的做法——现在清单里的每一条
/// 都锁定了具体的 InlineVersionEntry（含 RawVersion），最后统一下载时直接按这条记录里的
/// 版本下载，不再重新按"游戏版本+加载器"去反查一次"最新兼容版本"。
///
/// IsResourcePackKind 区分这条清单记录来自 Mod 步骤还是资源包/光影包步骤，方便向导确认页/
/// 下载步骤分别展示"Mod：n 个"/"资源包：n 个"的统计。
/// </summary>
public class WizardSelectionEntry
{
    public string Title { get; }
    public string VersionLabel { get; }
    public ModSource Source { get; }
    /// <summary>下载时实际使用的版本条目：ModrinthVersion 或 CurseForgeFile，
    /// 跟 InlineVersionEntry.RawVersion 是同一份对象引用，不重新反查。</summary>
    public InlineVersionEntry Entry { get; }
    /// <summary>该条记录所属的原始搜索结果条目（UnifiedModItem 或 UnifiedResourceItem），
    /// 用于展示图标/描述，以及数据包场景下取 SelectedSaveName。</summary>
    public object SourceItem { get; }
    public bool IsResourcePackKind { get; }

    /// <summary>数据包场景下，用户在详情页里选的目标存档名；非数据包时恒为 null。</summary>
    public string? SelectedSaveName { get; set; }

    public WizardSelectionEntry(string title, InlineVersionEntry entry, object sourceItem, bool isResourcePackKind)
    {
        Title = title;
        Entry = entry;
        SourceItem = sourceItem;
        IsResourcePackKind = isResourcePackKind;
        Source = entry.Source;
        VersionLabel = string.IsNullOrEmpty(entry.VersionNumber) ? entry.Name : entry.VersionNumber;
    }
}

/// <summary>卡片内联展开面板里的一行：屏蔽 Modrinth 版本 / CurseForge 文件的数据结构差异，
/// 统一成"标题 + 版本号 + 支持的游戏版本 + 下载动作"这一套形状，供展开面板的 ItemsControl
/// 绑定展示。取代之前 ModrinthVersionPickerWindow.VersionDisplayItem / MapFileDisplayItem
/// 两套各自为政的弹窗专属模型。</summary>
public class InlineVersionEntry
{
    public ModSource Source { get; }
    public string Name { get; }
    public string VersionNumber { get; }
    public string GameVersionsText { get; }
    public List<string> GameVersions { get; }

    /// <summary>该版本支持的加载器列表，Modrinth 原样取自 version.loaders；CurseForge 文件没有
    /// 该概念(一个文件只对应一种 loader，见下方 CurseForgeFile 构造函数)，统一成 List 方便两边共用
    /// 同一套"按 loader 分组"逻辑，不用在分组代码里对 Source 再 if/else 一次。</summary>
    public List<string> Loaders { get; }

    public List<ModrinthDependency> RequiredDependencies { get; }

    /// <summary>Modrinth: ModrinthVersion；CurseForge: CurseForgeFile。下载时按 Source 判断
    /// 具体类型再强转，跟 UnifiedResourceItem.RawItem 是同一个思路。</summary>
    public object RawVersion { get; }

    /// <summary>
    /// 该版本是不是"预览版"（Modrinth version_type == beta/alpha，或 CurseForge
    /// releaseType == 2/3）。版本列表默认只展示正式版(release)，预览版默认隐藏，需要用户
    /// 主动点开"显示预览版"该隐藏按钮才会出现在分组列表里。
    /// </summary>
    public bool IsPreview { get; }

    public InlineVersionEntry(ModrinthVersion version)
    {
        Source = ModSource.Modrinth;
        Name = string.IsNullOrEmpty(version.Name) ? version.VersionNumber : version.Name;
        VersionNumber = version.VersionNumber;
        GameVersions = version.GameVersions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        GameVersionsText = string.Join(", ", GameVersions.Take(6)) + (version.GameVersions.Count > 6 ? " ..." : "");
        Loaders = version.Loaders.Count > 0 ? version.Loaders : new List<string> { "minecraft" }; // 材质包/光影包等没有 loader 概念的项目类型，Modrinth 返回空数组，这里退回一个占位分组名而不是让分组逻辑收到空列表出错
        RequiredDependencies = version.Dependencies.Where(d => d.DependencyType == "required").ToList();
        RawVersion = version;
        IsPreview = version.VersionType is "beta" or "alpha";
    }

    public InlineVersionEntry(CurseForgeFile file)
    {
        Source = ModSource.CurseForge;
        Name = string.IsNullOrEmpty(file.DisplayName) ? file.FileName : file.DisplayName;
        VersionNumber = file.FileName;
        GameVersions = file.GameVersions.Where(v =>
            System.Text.RegularExpressions.Regex.IsMatch(v, @"^(?:\d+\.\d+|\d{2}w\d{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        GameVersionsText = string.Join(", ", GameVersions.Take(6)) + (GameVersions.Count > 6 ? " ..." : "");
        var knownLoaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "fabric", "forge", "neoforge", "quilt", "liteloader", "bukkit", "spigot", "paper", "sponge" };
        Loaders = file.GameVersions.Select(v => v.Replace(" ", "").Replace("-", "").ToLowerInvariant())
            .Where(knownLoaders.Contains).Distinct().ToList();
        if (Loaders.Count == 0) Loaders.Add("curseforge");
        RequiredDependencies = new List<ModrinthDependency>();
        RawVersion = file;
        IsPreview = file.ReleaseType is 2 or 3; // CurseForge: 1=release 2=beta 3=alpha
    }
}

public class VersionGroup : INotifyPropertyChanged
{
    public string GroupTitle { get; }
    public string GameVersion { get; }
    public string Loader { get; }
    public int EntryCount => Entries.Count;
    public List<ModrinthDependency> RequiredDependencies { get; }
    public bool HasRequiredDependencies => RequiredDependencies.Count > 0;
    public List<InlineVersionEntry> Entries { get; }

    private bool _isExpanded;
    

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
    }

    public VersionGroup(string groupTitle, List<InlineVersionEntry> entries, string? gameVersion = null, string? loader = null)
    {
        GroupTitle = groupTitle;
        var split = groupTitle.LastIndexOf(' ');
        GameVersion = gameVersion ?? (split < 0 ? groupTitle : groupTitle[(split + 1)..]);
        Loader = loader ?? (split < 0 ? "通用" : groupTitle[..split]);
        Entries = entries;
        RequiredDependencies = entries.FirstOrDefault()?.RequiredDependencies ?? new List<ModrinthDependency>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

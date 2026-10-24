using System.ComponentModel;

namespace XCL2.App.Models;

public enum BedrockMarketSort
{
    Default,
    Newest,
    RecentlyUpdated
}

/// <summary>MC百科基岩目录公开提供的筛选；筛选作用于完整目录而非当前页。</summary>
public sealed class BedrockMarketSearchOptions
{
    public string Query { get; init; } = "";
    public int Page { get; init; } = 1;
    public int Category { get; init; }
    public int Status { get; init; }
    public BedrockMarketSort Sort { get; init; }
    public bool FuzzySearch { get; init; }
}

/// <summary>MC百科基岩版目录条目。目录只提供项目索引，不声称每个项目都有直链。</summary>
public sealed class BedrockMarketProject : INotifyPropertyChanged
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string EnglishName { get; init; } = "";
    public string Summary { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string CategoryLabel { get; init; } = "基岩版附加内容";
    public bool IsResolvingPublicationPage { get; private set; }
    public bool IsPublicationPageUnavailable { get; private set; }
    public bool CanOpenPublicationPage => !IsResolvingPublicationPage && !IsPublicationPageUnavailable;
    public string PublicationActionText => IsResolvingPublicationPage ? "正在读取发布页…"
        : IsPublicationPageUnavailable ? "发布页面不可用" : "打开发布页面";
    public string PublicationActionHint => IsPublicationPageUnavailable
        ? "此资源未提供有效的发布网页，可在右侧打开百科页面查看其他来源。"
        : "在浏览器中打开此资源明确关联的上游发布页面，不下载文件。";

    internal void SetPublicationPageState(bool resolving, bool unavailable = false)
    {
        IsResolvingPublicationPage = resolving;
        IsPublicationPageUnavailable = unavailable;
        foreach (var property in new[] { nameof(IsResolvingPublicationPage), nameof(IsPublicationPageUnavailable),
                     nameof(CanOpenPublicationPage), nameof(PublicationActionText), nameof(PublicationActionHint) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class BedrockMarketSearchResult
{
    public List<BedrockMarketProject> Projects { get; init; } = new();
    public int Page { get; init; } = 1;
    public int TotalPages { get; init; } = 1;
    public int TotalCount { get; init; }
}

/// <summary>上游官方 API 实际返回的资产，下载器仍须检查包内 manifest。</summary>
public sealed class BedrockMarketFile
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    /// <summary>上游明确返回的网页地址；缺失时不能用下载直链代替。</summary>
    public string ReleasePageUrl { get; init; } = "";
    public string Source { get; init; } = "";
    public long Size { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha256 { get; init; }
    public string? Md5 { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string GameVersions { get; init; } = "";
}

/// <summary>百科条目明确关联的上游项目网页，与包文件下载地址分开保存。</summary>
public sealed record BedrockMarketPublicationPage(string Source, string Url);

public sealed class BedrockMarketFileResult
{
    public List<BedrockMarketFile> Files { get; init; } = new();
    public string Notice { get; init; } = "";
}

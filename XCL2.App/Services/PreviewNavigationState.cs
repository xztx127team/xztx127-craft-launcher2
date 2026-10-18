namespace XCL2.App.Services;

internal sealed record PreviewNavigationCategory(string Id, string Title, string DefaultPage);

/// <summary>两级导航的页面归属和分组记忆；页面仍由启动器原有入口创建。</summary>
internal sealed class PreviewNavigationState
{
    internal static readonly IReadOnlyList<PreviewNavigationCategory> Categories = Array.AsReadOnly(new[]
    {
        new PreviewNavigationCategory("launch", "启动", "home"),
        new PreviewNavigationCategory("downloads", "下载", "downloads"),
        new PreviewNavigationCategory("multiplayer", "联机", "multiplayer"),
        new PreviewNavigationCategory("tools", "工具", "toolbox"),
        new PreviewNavigationCategory("settings", "设置", "settings")
    });
    private readonly Dictionary<string, string> _lastPages = new(StringComparer.Ordinal);
    internal string Category { get; private set; } = "launch";

    internal static string? CategoryForPage(string? page) => page switch
    {
        "home" or "instances" or "worlds" or "mods" or "bedrock" => "launch",
        "downloads" => "downloads",
        "multiplayer" or "servers" => "multiplayer",
        "ai" or "toolbox" or "logs" => "tools",
        "accounts" or "settings" or "plugins" or "api" or "experimental" or "about" or "more" => "settings",
        _ when page?.StartsWith("plugin:", StringComparison.Ordinal) == true => "settings",
        _ => null
    };

    internal void ObservePage(string? page)
    {
        if (page == null || CategoryForPage(page) is not { } category) return;
        Category = category;
        _lastPages[category] = page;
    }

    internal string? SelectCategory(string category, IEnumerable<string> availablePages)
    {
        var definition = Categories.FirstOrDefault(item => item.Id == category);
        if (definition == null) return null;
        Category = category;
        var available = availablePages.Where(page => CategoryForPage(page) == category).ToArray();
        if (_lastPages.TryGetValue(category, out var last) && available.Contains(last, StringComparer.Ordinal)) return last;
        return available.Contains(definition.DefaultPage, StringComparer.Ordinal) ? definition.DefaultPage : available.FirstOrDefault();
    }
}

namespace XCL2.App.Models;

public partial class AppConfig
{
    [ExperiencePreference("实验性功能", "显示迷你世界下载与启动入口", Description = "关闭后隐藏实验性功能中的迷你世界入口。作者保持中立，不评判任何游戏；重新开启后仍须阅读声明并答题。")]
    public bool EnableMiniWorldExperimental { get; set; } = true;

    public Dictionary<string, string> MiniWorldClientPaths { get; set; } = new();
    public List<MiniWorldCommunityClient> MiniWorldCommunityClients { get; set; } = new();
    public List<MiniWorldSavedVersion> MiniWorldSavedVersions { get; set; } = new();
    public List<MiniWorldLocalInstallation> MiniWorldLocalInstallations { get; set; } = new();
}

public sealed class MiniWorldLocalInstallation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BindingKey { get; set; } = "";
    public string SourceId { get; set; } = "local";
    public string SourceName { get; set; } = "本地导入";
    public string VersionId { get; set; } = "";
    public string VersionName { get; set; } = "本地安装（版本未识别）";
    public string Name { get; set; } = "迷你世界本地客户端";
    public string ExecutablePath { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public List<string> Arguments { get; set; } = new();
    public DateTimeOffset? LastLaunchedAt { get; set; }
}

public sealed class MiniWorldSavedVersion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourcePageUrl { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public string PublishedDate { get; set; } = "";
    public List<string> Mirrors { get; set; } = new();
    public string Sha256 { get; set; } = "";
}

public sealed class MiniWorldCommunityClient
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "社区客户端";
    public string DownloadPageUrl { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Category { get; set; } = "社区版本";
    public string VersionIndexUrl { get; set; } = "";
}

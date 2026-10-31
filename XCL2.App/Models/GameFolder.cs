namespace XCL2.App.Models;

/// <summary>
/// 一个 .minecraft 根目录（类似 PCL 的"文件夹列表"）。
/// 每个 GameFolder 下可以有多个版本(Version)。
/// </summary>
public class GameFolder
{
    public string Name { get; set; } = "当前文件夹";
    public string Path { get; set; } = "";
    public bool IsDefault { get; set; }

    public override string ToString() => Name;
}

/// <summary>
/// .minecraft/versions/<VersionId> 下的一个可运行版本。
/// </summary>
public class GameVersion
{
    public string Id { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string? ModLoader { get; set; } // Fabric / Forge / NeoForge / Quilt / null
    public string? ModLoaderVersion { get; set; }
    public string FolderPath { get; set; } = "";
    public bool IsInstalled { get; set; }

    // 仅实例页的个人显示状态；扫描其它页面时保持默认，不影响启动逻辑。
    [System.Text.Json.Serialization.JsonIgnore] public bool IsFavorite { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? CustomIconPath { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasCustomIcon => !string.IsNullOrEmpty(CustomIconPath);
    [System.Text.Json.Serialization.JsonIgnore] public string MetadataSummary { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string PersonalNotes { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public bool SharesGameDirectory { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayTitle => (IsFavorite ? "★ " : "") + Id;
    [System.Text.Json.Serialization.JsonIgnore] public string Details => ModLoader is null ? McVersion : $"{McVersion} · {ModLoader} {ModLoaderVersion}";
    [System.Text.Json.Serialization.JsonIgnore] public string FullDisplayToolTip => string.Join("\n", new[] { Id, Details, MetadataSummary, PersonalNotes.Length > 500 ? PersonalNotes[..500] + "…" : PersonalNotes }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string SubTitle => ModLoader is null
        ? $"{Id}（{McVersion}）"
        : $"{Id}（{McVersion} {ModLoader} {ModLoaderVersion}）";
}

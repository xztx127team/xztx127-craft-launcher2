using System.IO;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>A read-only description. Merely discovering an instance never creates a game profile.</summary>
public sealed record ExternalInstanceCandidate(string Name, string SourceKind, string InstanceDirectory,
    string GameDirectory, string? MinecraftVersion, string? Loader, string? LoaderVersion,
    string? SharedRoot, string? Problem = null)
{
    public bool CanImport => string.IsNullOrWhiteSpace(Problem) && !string.IsNullOrWhiteSpace(MinecraftVersion) &&
        (Loader == null || !string.IsNullOrWhiteSpace(LoaderVersion));
    public string Summary => $"{Name} · {SourceKind} · {MinecraftVersion ?? "版本未知"} · {Loader ?? "原版"} {LoaderVersion}".Trim();
}

public sealed record ExternalInstanceScan(IReadOnlyList<ExternalInstanceCandidate> Instances, IReadOnlyList<string> Warnings);

public static class ExternalInstanceDiscovery
{
    private const int MaximumInstances = 2000;
    public static ExternalInstanceScan Scan(string selectedPath, CancellationToken ct = default)
    {
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedPath));
        if (!Directory.Exists(selected)) throw new DirectoryNotFoundException("找不到选择的实例目录。");
        ArchiveImportSafety.CheckLinks(selected);
        var found = new Dictionary<string, ExternalInstanceCandidate>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        void Add(string path)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ArchiveImportSafety.CheckLinks(path);
                var candidate = ReadCandidate(path);
                if (candidate != null) found.TryAdd(Path.GetFullPath(candidate.InstanceDirectory), candidate);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { warnings.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
        }
        Add(selected);
        if (Path.GetFileName(selected) is ".minecraft" or "minecraft" && Directory.GetParent(selected) is { } parent) Add(parent.FullName);
        // A single selected instance must not accidentally import its siblings.
        if (found.Count == 0)
        {
            var roots = new[] { selected, Path.Combine(selected, "instances"), Path.Combine(selected, "Instances"), Path.Combine(selected, "profiles") }
                .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                var count = 0;
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++count > MaximumInstances) { warnings.Add("目录超过 2000 项，请选择更具体的 instances/profiles 目录。"); break; }
                    Add(directory);
                }
            }
        }
        // The modern Modrinth App stores profile definitions in app.db, not profile.json.
        foreach (var root in Ancestors(selected, 3))
        {
            var db = Path.Combine(root, "app.db");
            if (!File.Exists(db)) continue;
            try
            {
                foreach (var candidate in ModrinthProfileDatabase.Read(db, selected, ct))
                    found[Path.GetFullPath(candidate.InstanceDirectory)] = candidate;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { warnings.Add("Modrinth App 元数据无法读取，请退出 Modrinth App 后重试，或导出 .mrpack：" + ex.Message); }
            break;
        }
        if (found.Count == 0) warnings.Add("未找到 mmc-pack.json、minecraftinstance.json、profile.json 或可读取的 Modrinth app.db。请选实例本身、instances/profiles 文件夹或启动器数据根目录。");
        return new(found.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray(), warnings);
    }

    private static IEnumerable<string> Ancestors(string path, int count)
    {
        for (var current = path; current != null && count-- > 0; current = Directory.GetParent(current)?.FullName) yield return current;
    }
    private static ExternalInstanceCandidate? ReadCandidate(string root)
    {
        if (File.Exists(Path.Combine(root, "mmc-pack.json"))) return ReadMultiMc(root);
        if (File.Exists(Path.Combine(root, "minecraftinstance.json"))) return ReadCurseForge(root);
        if (File.Exists(Path.Combine(root, "profile.json"))) return ReadModrinth(root);
        return null;
    }
    internal static JsonDocument ReadJson(string path)
    {
        ArchiveImportSafety.CheckLinks(path);
        if (new FileInfo(path).Length > ArchiveImportSafety.MaximumManifestBytes) throw new InvalidDataException("实例元数据超过 4 MB。");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
    internal static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static JsonElement Node(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    internal static string? NormalizeLoader(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "vanilla" or "minecraft" => null,
        "fabric" or "fabric-loader" => "Fabric", "quilt" or "quilt-loader" => "Quilt",
        "forge" => "Forge", "neoforge" or "neo-forge" => "NeoForge", _ => value
    };
    internal static ExternalInstanceCandidate Validate(ExternalInstanceCandidate candidate)
    {
        string? problem = candidate.Problem;
        if (!Directory.Exists(candidate.GameDirectory)) problem ??= "缺少游戏数据目录，请先在原启动器安装该实例。";
        if (string.IsNullOrWhiteSpace(candidate.MinecraftVersion)) problem ??= "元数据没有明确的 Minecraft 版本，无法安全生成启动配置。";
        if (candidate.Loader is not (null or "Fabric" or "Quilt" or "Forge" or "NeoForge")) problem ??= $"暂不支持组件 {candidate.Loader}；请从原启动器导出可支持的整合包。";
        if (candidate.Loader != null && string.IsNullOrWhiteSpace(candidate.LoaderVersion)) problem ??= "元数据缺少加载器的精确版本。";
        // Version strings become installer paths and URLs. Reject traversal before touching the destination.
        foreach (var value in new[] { candidate.MinecraftVersion, candidate.LoaderVersion })
            if (value != null && (value.Contains('/') || value.Contains('\\') || value.Contains(':') || value.Contains("..") || value.Any(char.IsControl)))
                problem ??= "版本号含有不安全的路径字符。";
        return candidate with { Problem = problem };
    }
    private static ExternalInstanceCandidate ReadMultiMc(string root)
    {
        using var doc = ReadJson(Path.Combine(root, "mmc-pack.json"));
        var components = Node(doc.RootElement, "components");
        string? mc = null, loader = null, loaderVersion = null, problem = null;
        if (components.ValueKind != JsonValueKind.Array) problem = "mmc-pack.json 缺少组件列表。";
        else foreach (var component in components.EnumerateArray())
        {
            var uid = Text(component, "uid"); var version = Text(component, "version");
            if (uid == "net.minecraft") mc = version;
            else if (uid is "net.fabricmc.fabric-loader" or "org.quiltmc.quilt-loader" or "net.minecraftforge" or "net.neoforged")
            {
                if (loader != null) problem = "实例包含多个加载器，不能无损自动迁移。";
                loader = uid switch { "net.fabricmc.fabric-loader" => "Fabric", "org.quiltmc.quilt-loader" => "Quilt", "net.minecraftforge" => "Forge", _ => "NeoForge" };
                loaderVersion = version;
            }
            else if (uid is not ("org.lwjgl" or "org.lwjgl3")) problem = $"包含自定义组件 {uid ?? "未知组件"}，请先在原启动器导出整合包。";
        }
        ArchiveImportSafety.CheckLinks(Path.Combine(root, "patches"));
        ArchiveImportSafety.CheckLinks(Path.Combine(root, "jarmods"));
        if (Directory.Exists(Path.Combine(root, "patches")) && Directory.EnumerateFiles(Path.Combine(root, "patches"), "*.json").Any())
            problem = "实例包含 patches 自定义启动补丁，自动重建可能改变行为，请先导出整合包。";
        if (Directory.Exists(Path.Combine(root, "jarmods")) && Directory.EnumerateFiles(Path.Combine(root, "jarmods")).Any())
            problem = "实例使用 jar-mods，不能按普通加载器无损迁移。";
        var cfg = Path.Combine(root, "instance.cfg"); var name = Path.GetFileName(root);
        if (File.Exists(cfg))
        {
            ArchiveImportSafety.CheckLinks(cfg);
            if (new FileInfo(cfg).Length > ArchiveImportSafety.MaximumManifestBytes) throw new InvalidDataException("instance.cfg 超过 4 MB。");
            var nameLine = File.ReadLines(cfg).FirstOrDefault(line => line.StartsWith("name=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(nameLine?[5..])) name = nameLine![5..].Trim();
        }
        var game = Directory.Exists(Path.Combine(root, ".minecraft")) ? Path.Combine(root, ".minecraft") : Path.Combine(root, "minecraft");
        var shared = Directory.GetParent(root)?.Parent?.FullName;
        return Validate(new(name, "Prism / MultiMC", root, game, mc, loader, loaderVersion, shared, problem));
    }
    private static ExternalInstanceCandidate ReadModrinth(string root)
    {
        using var doc = ReadJson(Path.Combine(root, "profile.json"));
        var outer = doc.RootElement; var metadata = Node(outer, "metadata");
        if (metadata.ValueKind != JsonValueKind.Object) metadata = outer;
        var loaderText = Text(metadata, "loader") ?? Text(metadata, "mod_loader");
        var loader = NormalizeLoader(loaderText);
        var problem = string.IsNullOrWhiteSpace(loaderText) ? "Modrinth 元数据没有明确声明加载器类型，无法安全判断是否为原版。" : null;
        var lv = Node(metadata, "loader_version");
        var version = lv.ValueKind == JsonValueKind.String ? lv.GetString() : Text(lv, "id");
        version ??= Text(metadata, "mod_loader_version");
        return Validate(new(Text(metadata, "name") ?? Path.GetFileName(root), "Modrinth App", root, root,
            Text(metadata, "game_version"), loader, version, Directory.GetParent(root)?.Parent?.FullName is { } appRoot ? Path.Combine(appRoot, "meta") : null, problem));
    }
    private static ExternalInstanceCandidate ReadCurseForge(string root)
    {
        using var doc = ReadJson(Path.Combine(root, "minecraftinstance.json"));
        var data = doc.RootElement; var component = Node(data, "baseModLoader");
        var mc = Text(data, "gameVersion") ?? Text(component, "minecraftVersion");
        var raw = Text(component, "name") ?? Text(component, "id");
        string? loader = null, version = null, problem = null;
        if (component.ValueKind == JsonValueKind.Undefined) problem = "CurseForge 元数据没有声明 baseModLoader，无法安全判断是否为原版。";
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var split = raw.IndexOf('-'); var prefix = split >= 0 ? raw[..split] : raw;
            loader = NormalizeLoader(prefix);
            version = split >= 0 ? raw[(split + 1)..] : null;
            if (mc != null && version?.EndsWith("-" + mc, StringComparison.OrdinalIgnoreCase) == true) version = version[..^(mc.Length + 1)];
            if (mc != null && version?.StartsWith(mc + "-", StringComparison.OrdinalIgnoreCase) == true) version = version[(mc.Length + 1)..];
        }
        else if (component.ValueKind == JsonValueKind.Object) problem = "CurseForge baseModLoader 没有可识别的名称和精确版本。";
        return Validate(new(Text(data, "name") ?? Path.GetFileName(root), "CurseForge App", root, root, mc, loader, version,
            Directory.GetParent(root)?.Parent?.FullName is { } parent ? Path.Combine(parent, "Install") : null, problem));
    }
}

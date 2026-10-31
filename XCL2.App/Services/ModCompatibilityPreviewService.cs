using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

public sealed record ModCompatibilityIssue(string ModName, string Kind, string Detail);
public sealed record ModCompatibilityReport(string MinecraftVersion, string Loader, int EnabledCount, int RecognizedCount,
    IReadOnlyList<ModCompatibilityIssue> Issues, IReadOnlyList<string> Declarations);

/// <summary>Read-only local metadata preview. Unknown range syntax is shown for review, never guessed compatible.</summary>
public static class ModCompatibilityPreviewService
{
    private sealed record Requirement(string Id, string Range);
    private sealed class Metadata
    {
        public string Name = "", Id = ""; public bool AmbiguousForgeDescriptor; public readonly HashSet<string> Loaders = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Provides = new(); public readonly List<Requirement> Requires = new();
    }
    private static readonly Regex TomlString = new("^\\s*(?<key>[A-Za-z_]+)\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
    public static ModCompatibilityReport Analyze(IEnumerable<LocalModInfo> mods, string mcVersion, string? loader, CancellationToken ct = default)
    {
        var enabled = mods.Where(m => m.IsEnabled).ToArray(); var metadata = new List<Metadata>(); var issues = new List<ModCompatibilityIssue>(); var declarations = new List<string>(); var recognized = 0;
        foreach (var mod in enabled)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var archive = ZipFile.OpenRead(mod.FilePath);
                var local = Read(archive, mod.DisplayName, ct); if (local == null) { issues.Add(new(mod.DisplayName, "未识别", "未找到受支持的元数据，无法判断兼容性。")); continue; }
                recognized++; metadata.Add(local);
                // Read embedded Fabric modules so required API submodules are not incorrectly reported missing.
                var nestedTotal = 0L;
                var fabric = Json(archive, "fabric.mod.json");
                if (fabric != null)
                {
                    using (fabric)
                    if (fabric.RootElement.TryGetProperty("jars", out var jars) && jars.ValueKind == JsonValueKind.Array)
                    foreach (var jar in jars.EnumerateArray().Take(128))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!jar.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String) continue;
                        var entry = archive.GetEntry(file.GetString()!); if (entry == null || entry.Length > 16 * 1048576 || nestedTotal + entry.Length > 64 * 1048576) continue;
                        nestedTotal += entry.Length;
                        using var input = entry.Open(); using var copy = new MemoryStream(); var buffer = new byte[81920]; long copied = 0; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        { ct.ThrowIfCancellationRequested(); copied += count; if (copied > entry.Length) throw new InvalidDataException("内嵌模组大小异常。"); copy.Write(buffer, 0, count); }
                        copy.Position = 0;
                        using var nested = new ZipArchive(copy, ZipArchiveMode.Read); var nestedInfo = Read(nested, mod.DisplayName, ct); if (nestedInfo != null) metadata.Add(nestedInfo);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
            { issues.Add(new(mod.DisplayName, "无法读取", "元数据异常或文件被占用，未修改此文件。")); }
        }
        var installed = metadata.Select(m => m.Id).Concat(metadata.SelectMany(m => m.Provides)).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeLoader = (loader ?? "").Trim().ToLowerInvariant();
        if (activeLoader.Contains("neoforge")) activeLoader = "neoforge";
        else if (activeLoader.Contains("fabric")) activeLoader = "fabric";
        else if (activeLoader.Contains("quilt")) activeLoader = "quilt";
        else if (activeLoader.Contains("forge")) activeLoader = "forge";
        foreach (var mod in metadata)
        {
            ct.ThrowIfCancellationRequested();
            if (activeLoader == "neoforge" && mod.AmbiguousForgeDescriptor)
                issues.Add(new(mod.Name, "加载器需要核对", "仅有旧 mods.toml 描述，不能据此确认是否支持当前 NeoForge。"));
            else if (mod.Loaders.Count > 0 && activeLoader.Length > 0 && !mod.Loaders.Contains(activeLoader)
                && !(activeLoader == "quilt" && mod.Loaders.Contains("fabric")))
                issues.Add(new(mod.Name, "加载器不匹配", "文件声明 " + string.Join(" / ", mod.Loaders) + "，当前实例为 " + activeLoader + "。"));
            foreach (var dep in mod.Requires)
            {
                declarations.Add($"{mod.Name} → {dep.Id} {dep.Range}");
                if (dep.Id.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    var matches = MatchesMinecraft(mcVersion, dep.Range);
                    if (matches == false) issues.Add(new(mod.Name, "游戏版本不匹配", $"声明 Minecraft {dep.Range}，当前为 {mcVersion}。"));
                    else if (matches == null) issues.Add(new(mod.Name, "版本要求待核对", "Minecraft " + dep.Range + "；此范围语法暂不自动判定。"));
                }
                else if (dep.Id is "java" or "fabricloader" or "quilt_loader" or "forge" or "neoforge") { }
                else if (!installed.Contains(dep.Id)) issues.Add(new(mod.Name, "缺少声明依赖", dep.Id + " " + dep.Range + "（已禁用的文件不计入已安装依赖）"));
            }
        }
        return new(mcVersion, string.IsNullOrWhiteSpace(loader) ? "未识别 / 原版" : loader, enabled.Length, recognized,
            issues.Distinct().ToArray(), declarations.Distinct().ToArray());
    }
    private static JsonDocument? Json(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name); if (entry == null) return null;
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("元数据过大。");
        using var input = entry.Open(); return JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 32 });
    }
    private static string Text(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name); if (entry == null) return "";
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("元数据过大。");
        using var stream = entry.Open(); using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static Metadata? Read(ZipArchive zip, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Metadata? info = null;
        using var fabric = Json(zip, "fabric.mod.json");
        if (fabric != null)
        {
            var root = fabric.RootElement; info = new Metadata { Name = name, Id = GetString(root, "id") }; info.Loaders.Add("fabric");
            if (root.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
                info.Provides.AddRange(provides.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
            if (root.TryGetProperty("depends", out var deps) && deps.ValueKind == JsonValueKind.Object)
                foreach (var dep in deps.EnumerateObject()) info.Requires.Add(new(dep.Name, Range(dep.Value)));
        }
        using var quilt = Json(zip, "quilt.mod.json");
        if (quilt != null && quilt.RootElement.TryGetProperty("quilt_loader", out var q))
        {
            info ??= new Metadata { Name = name, Id = GetString(q, "id") }; info.Loaders.Add("quilt");
            if (q.TryGetProperty("depends", out var deps) && deps.ValueKind == JsonValueKind.Array)
                foreach (var dep in deps.EnumerateArray())
                    if (dep.ValueKind == JsonValueKind.Object && GetString(dep, "id") is { Length: > 0 } id && (!dep.TryGetProperty("optional", out var optional) || optional.ValueKind != JsonValueKind.True))
                        info.Requires.Add(new(id, dep.TryGetProperty("versions", out var versions) ? Range(versions) : "*"));
        }
        foreach (var (path, loader) in new[] { ("META-INF/mods.toml", "forge"), ("META-INF/neoforge.mods.toml", "neoforge") })
        {
            var text = Text(zip, path); if (text.Length == 0) continue;
            info ??= new Metadata { Name = name }; info.Loaders.Add(loader);
            if (loader == "forge") info.AmbiguousForgeDescriptor = true;
            var sections = Regex.Split(text, "(?m)^\\s*\\[\\[(?<section>[^\\]]+)\\]\\]\\s*$", RegexOptions.None, TimeSpan.FromSeconds(1));
            for (var i = 1; i + 1 < sections.Length; i += 2)
            {
                var values = TomlString.Matches(sections[i + 1]).Cast<Match>().GroupBy(m => m.Groups["key"].Value).ToDictionary(g => g.Key, g => g.First().Groups["value"].Value);
                if (sections[i].Trim() == "mods" && values.TryGetValue("modId", out var id)) { if (info.Id.Length == 0) info.Id = id; else info.Provides.Add(id); }
                if (sections[i].Trim().StartsWith("dependencies.", StringComparison.Ordinal) && values.TryGetValue("modId", out var dependency)
                    && (Regex.IsMatch(sections[i + 1], "(?m)^\\s*mandatory\\s*=\\s*true\\s*(?:#.*)?$", RegexOptions.None, TimeSpan.FromSeconds(1)) || values.GetValueOrDefault("type") == "required"))
                    info.Requires.Add(new(dependency, values.GetValueOrDefault("versionRange", "*")));
            }
        }
        if (info != null)
        {
            if (info.Requires.Any(r => r.Id == "neoforge"))
            { info.Loaders.Add("neoforge"); if (!info.Requires.Any(r => r.Id == "forge")) info.Loaders.Remove("forge"); info.AmbiguousForgeDescriptor = false; }
            else if (info.Requires.Any(r => r.Id == "forge")) info.AmbiguousForgeDescriptor = false;
            if (zip.GetEntry("META-INF/neoforge.mods.toml") != null) info.AmbiguousForgeDescriptor = false;
        }
        return info;
    }
    private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string Range(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "*" : value.ValueKind == JsonValueKind.Array
        ? string.Join(" || ", value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())) : "（复杂范围，需核对）";
    public static bool? MatchesMinecraft(string version, string range)
    {
        if (range is "*" or "") return true;
        if (!Version.TryParse(version, out var current)) return null;
        var alternatives = range.Split("||", StringSplitOptions.TrimEntries); var unknown = false;
        foreach (var alternative in alternatives)
        {
            var tokens = alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries); var matches = true; var known = true;
            if (tokens.Length == 0) { unknown = true; continue; }
            foreach (var token in tokens)
            {
                var match = Regex.Match(token, "^(?<op>>=|<=|>|<|=)?(?<v>[0-9]+(?:\\.[0-9]+){1,3})(?<wild>\\.[xX*])?$", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (!match.Success || !Version.TryParse(match.Groups["v"].Value, out var required)) { known = false; continue; }
                int Compare(Version a, Version b) => new Version(a.Major, a.Minor, Math.Max(0, a.Build), Math.Max(0, a.Revision)).CompareTo(new Version(b.Major, b.Minor, Math.Max(0, b.Build), Math.Max(0, b.Revision)));
                var cmp = Compare(current, required); var op = match.Groups["op"].Value;
                if (match.Groups["wild"].Success)
                { if (op.Length > 0) { known = false; continue; } matches &= current.Major == required.Major && current.Minor == required.Minor && (required.Build < 0 || current.Build == required.Build); }
                else matches &= op switch { ">=" => cmp >= 0, "<=" => cmp <= 0, ">" => cmp > 0, "<" => cmp < 0, _ => cmp == 0 };
            }
            if (known && matches) return true;
            if (!known && matches) unknown = true;
        }
        return unknown ? null : false;
    }
}

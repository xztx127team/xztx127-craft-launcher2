using System.IO;
using System.Text.Json.Nodes;

namespace XCL2.App.Services;

/// <summary>Builds a standalone profile without dropping loader or vanilla launch arguments.</summary>
internal static class ComponentProfileComposer
{
    internal sealed record PreparedProfile(JsonObject Json, string ClientJar);

    internal static PreparedProfile Compose(string root, string installedId, string targetId,
        string minecraftVersion, string loader, string? loaderVersion)
    {
        _ = ComponentProfileTransaction.VersionDirectory(root, targetId);
        var layers = new List<(string Id, string Directory, JsonObject Json)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(string id)
        {
            if (!seen.Add(id) || seen.Count > 64) throw new InvalidDataException("新版本的继承链存在循环或超过 64 层。");
            var directory = ComponentProfileTransaction.VersionDirectory(root, id);
            var path = ComponentProfileTransaction.FindProfile(directory, id)
                ?? throw new InvalidDataException("新版本缺少描述文件：" + id);
            var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("新版本描述文件不是有效对象：" + id);
            if (json["inheritsFrom"]?.GetValue<string>() is { Length: > 0 } parent) Read(parent);
            layers.Add((id, directory, json));
        }
        Read(installedId);
        // Existing loader installers produce independent profiles, but some profiles still only
        // contain the loader's libraries and arguments. Retain the verified vanilla base too.
        if (!seen.Contains(minecraftVersion) && loader != "Vanilla")
        {
            var directory = ComponentProfileTransaction.VersionDirectory(root, minecraftVersion);
            var path = ComponentProfileTransaction.FindProfile(directory, minecraftVersion)
                ?? throw new InvalidDataException("缺少原版底座，未应用修改。");
            layers.Insert(0, (minecraftVersion, directory, JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("原版底座信息无效。")));
        }

        var result = new JsonObject();
        var libraries = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        JsonArray game = new(), jvm = new();
        string? clientJar = null;
        foreach (var layer in layers)
        {
            foreach (var field in layer.Json)
                if (field.Key is not ("libraries" or "arguments" or "inheritsFrom" or "jar") && field.Value != null)
                    result[field.Key] = field.Value.DeepClone();
            foreach (var library in (layer.Json["libraries"] as JsonArray ?? new JsonArray()))
            {
                if (library?["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
                var parts = name.Split(':');
                var key = parts.Length >= 3 ? parts[0] + ":" + parts[1] + ":" + string.Join(':', parts.Skip(3)) : name;
                // Distinct rule variants of one Maven artifact are legitimate (OS/native variants).
                key += "|" + library["rules"]?.ToJsonString();
                var normalized = library.DeepClone();
                // Legacy Forge profiles contain legal bare Maven coordinates (for example
                // launchwrapper:1.12). Both the downloader and launcher need a repository URL
                // to resolve those entries. Classifier-only native aggregators have no main JAR.
                var downloads = normalized["downloads"] as JsonObject;
                var nativeOnly = downloads?["classifiers"] is JsonObject && downloads["artifact"] == null;
                if (!nativeOnly && downloads?["artifact"] == null && string.IsNullOrWhiteSpace(normalized["url"]?.GetValue<string>()))
                {
                    if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace))
                        throw new InvalidDataException("组件包含无效的依赖库坐标：" + name);
                    normalized["url"] = "https://libraries.minecraft.net/";
                }
                libraries[key] = normalized;
            }
            if (layer.Json["arguments"] is JsonObject arguments)
            {
                game = MergeArguments(game, arguments["game"] as JsonArray);
                jvm = MergeArguments(jvm, arguments["jvm"] as JsonArray);
            }
            var ownJar = ArchiveImportSafety.Resolve(layer.Directory, layer.Id + ".jar");
            if (File.Exists(ownJar)) clientJar = ownJar;
            if (layer.Json["jar"]?.GetValue<string>() is { Length: > 0 } jarId)
            {
                var jarDirectory = ComponentProfileTransaction.VersionDirectory(root, jarId);
                var pointedJar = ArchiveImportSafety.Resolve(jarDirectory, jarId + ".jar");
                if (!File.Exists(pointedJar)) throw new InvalidDataException("新版本引用的客户端文件不存在：" + jarId);
                clientJar = pointedJar;
            }
        }
        if (string.IsNullOrWhiteSpace(result["mainClass"]?.GetValue<string>()) || clientJar == null || new FileInfo(clientJar).Length == 0)
            throw new InvalidDataException("安装器没有生成完整的可启动版本，原实例未修改。");
        result["id"] = targetId;
        result["libraries"] = new JsonArray(libraries.Values.ToArray());
        if (game.Count > 0 || jvm.Count > 0) result["arguments"] = new JsonObject { ["game"] = game, ["jvm"] = jvm };
        result.Remove("inheritsFrom");
        result.Remove("jar");
        // Keep identity separate from the user's instance name, including names containing "Fabric".
        result["xclMinecraftVersion"] = minecraftVersion;
        result["xclComponentLoader"] = loader;
        result["xclComponentLoaderVersion"] = loaderVersion;
        return new(result, clientJar);
    }

    private static JsonArray MergeArguments(JsonArray parent, JsonArray? child)
    {
        if (child == null || child.Count == 0) return parent;
        // OptiFine and imported standalone profiles may already contain the complete base array.
        if (child.Count >= parent.Count && parent.Select((value, i) => JsonNode.DeepEquals(value, child[i])).All(equal => equal))
            return (JsonArray)child.DeepClone();
        var combined = (JsonArray)parent.DeepClone();
        foreach (var value in child) combined.Add(value?.DeepClone());
        return combined;
    }

    /// <summary>Official NeoForge versioning: 20.2.59 → 1.20.2; 26.1.0.5-beta → 26.1.</summary>
    internal static string? MinecraftForNeoForge(string version)
    {
        // https://docs.neoforged.net/docs/gettingstarted/versioning/
        // Snapshot builds need their exact game snapshot and must never be offered for a release.
        if (version.Contains('+') || version.Contains("alpha", StringComparison.OrdinalIgnoreCase)) return null;
        var components = version.Split('-')[0].Split('.');
        if (components.Length < 3 || components.Any(part => !int.TryParse(part, out _))) return null;
        var first = int.Parse(components[0]);
        if (first >= 26 && components.Length == 4)
            return components[0] + "." + components[1] + (components[2] == "0" ? "" : "." + components[2]);
        if (first is >= 20 and < 26 && components.Length == 3)
            return "1." + components[0] + (components[1] == "0" ? "" : "." + components[1]);
        return null;
    }
}

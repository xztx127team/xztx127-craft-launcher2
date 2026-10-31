using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace XCL2.App.Services;

internal sealed record InstalledComponent(string Name, string Version, bool IsMod = false);

/// <summary>Component detection is independent of the instance's display name.</summary>
internal static class InstalledComponentInventory
{
    internal static List<JsonObject> ReadProfiles(string root, string id)
    {
        var result = new List<JsonObject>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(string current)
        {
            if (!visited.Add(current) || visited.Count > 64) throw new InvalidDataException("版本继承出现循环或超过 64 层。");
            var directory = ComponentProfileTransaction.VersionDirectory(root, current);
            var file = ComponentProfileTransaction.FindProfile(directory, current)
                ?? throw new InvalidDataException("找不到唯一的版本描述文件：" + current);
            var json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException("版本描述文件无效。");
            if (json["inheritsFrom"]?.GetValue<string>() is { Length: > 0 } parent) Read(parent);
            result.Add(json);
        }
        Read(id);
        return result;
    }

    internal static IReadOnlyList<InstalledComponent> FromProfiles(IEnumerable<JsonObject> profiles)
    {
        var found = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);
        var layers = profiles.ToArray();
        // Legacy Fabric uses the normal net.fabricmc:fabric-loader artifact. The
        // legacy intermediary may be in a different inherited layer or appear after
        // the loader, and its version is the Minecraft version, not the loader's.
        var legacyFabric = layers.Any(profile =>
            profile["xclComponentLoader"]?.GetValue<string>() is "LegacyFabric" or "Legacy Fabric" ||
            (profile["libraries"] as JsonArray ?? new JsonArray()).Any(library =>
                library?["name"]?.GetValue<string>()?.StartsWith("net.legacyfabric:", StringComparison.OrdinalIgnoreCase) == true));
        if (legacyFabric) found["LegacyFabric"] = new("LegacyFabric", "版本未声明");

        // Metadata is a fallback. A real loader artifact below is authoritative;
        // dependency/mapping artifacts must never overwrite the loader version.
        foreach (var profile in layers)
        {
            if (profile["xclComponentLoader"]?.GetValue<string>() is { Length: > 0 } declared && declared != "Vanilla")
            {
                if (declared is "Legacy Fabric" || declared == "Fabric" && legacyFabric) declared = "LegacyFabric";
                found[declared] = new(declared, profile["xclComponentLoaderVersion"]?.GetValue<string>() ?? "版本未声明");
            }
        }
        foreach (var profile in layers)
        {
            foreach (var library in profile["libraries"] as JsonArray ?? new JsonArray())
            {
                if (library?["name"]?.GetValue<string>() is not { } coordinate) continue;
                var parts = coordinate.Split(':');
                if (parts.Length < 3) continue;
                var name = (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant()) switch
                {
                    ("net.fabricmc", "fabric-loader") => legacyFabric ? "LegacyFabric" : "Fabric",
                    ("net.legacyfabric", "fabric-loader") => "LegacyFabric",
                    ("org.quiltmc", "quilt-loader") => "Quilt",
                    ("net.minecraftforge", "forge") => "Forge",
                    ("net.neoforged", "neoforge" or "forge") => "NeoForge",
                    ("optifine", "optifine") => "OptiFine",
                    ("com.mumfrey", "liteloader") => "LiteLoader",
                    ("com.cleanroommc", _) => "Cleanroom",
                    _ => null
                };
                if (name != null) found[name] = new(name, parts[2]);
            }
            var text = profile["arguments"]?.ToJsonString() + " " + profile["minecraftArguments"]?.ToJsonString();
            if (text.Contains("optifine.OptiFineTweaker", StringComparison.OrdinalIgnoreCase) && !found.ContainsKey("OptiFine"))
                found["OptiFine"] = new("OptiFine", profile["xclComponentLoaderVersion"]?.GetValue<string>() ?? "版本未声明");
            if (text.Contains("com.mumfrey.liteloader", StringComparison.OrdinalIgnoreCase) && !found.ContainsKey("LiteLoader"))
                found["LiteLoader"] = new("LiteLoader", "版本未声明");
        }
        var main = layers.LastOrDefault()?["mainClass"]?.GetValue<string>();
        var knownMain = main is "net.minecraft.client.main.Main" or "net.minecraft.client.Minecraft" or "net.minecraft.client.MinecraftApplet" ||
            main?.StartsWith("net.fabricmc.loader.", StringComparison.Ordinal) == true ||
            main?.StartsWith("org.quiltmc.loader.", StringComparison.Ordinal) == true ||
            main?.StartsWith("net.neoforged.fml.startup.", StringComparison.Ordinal) == true ||
            main is "cpw.mods.modlauncher.Launcher" or "cpw.mods.bootstraplauncher.BootstrapLauncher" or "net.minecraftforge.bootstrap.ForgeBootstrap" ||
            main == "net.minecraft.launchwrapper.Launch" && found.Count > 0;
        if (!string.IsNullOrWhiteSpace(main) && !knownMain)
            found["自定义启动组件"] = new("自定义启动组件", main);
        return found.Values.ToArray();
    }

    internal static bool NeedsCompositeConfirmation(IReadOnlyList<InstalledComponent> components) =>
        components.Count(component => !component.IsMod) > 1 || components.Any(component => !component.IsMod &&
            component.Name is not ("Fabric" or "LegacyFabric" or "Forge" or "NeoForge" or "Quilt" or "OptiFine"));

    internal static IReadOnlyList<string> DirectDependents(string root, string versionId)
    {
        var versions = Path.Combine(root, "versions");
        if (!Directory.Exists(versions)) return Array.Empty<string>();
        var dependents = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(versions))
        {
            var id = Path.GetFileName(directory);
            if (id.StartsWith('.') || string.Equals(id, versionId, StringComparison.OrdinalIgnoreCase)) continue;
            ArchiveImportSafety.CheckLinks(directory);
            var file = ComponentProfileTransaction.FindProfile(directory, id);
            if (file == null) continue;
            JsonObject? profile;
            try { profile = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
            catch (System.Text.Json.JsonException ex)
            { throw new InvalidDataException("无法检查实例“" + id + "”的版本依赖，请先修复它的描述文件。", ex); }
            if (new[] { "inheritsFrom", "jar" }.Any(key =>
                profile?[key] is JsonValue value && value.TryGetValue<string>(out var parent) &&
                string.Equals(parent, versionId, StringComparison.OrdinalIgnoreCase))) dependents.Add(id);
        }
        return dependents;
    }

    internal static IReadOnlyList<InstalledComponent> FromMods(string gameDirectory, CancellationToken ct = default)
    {
        var result = new List<InstalledComponent>();
        var mods = Path.Combine(gameDirectory, "mods");
        if (!Directory.Exists(mods)) return result;
        foreach (var file in Directory.EnumerateFiles(mods))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                ArchiveImportSafety.CheckLinks(file);
                using var archive = ZipFile.OpenRead(file);
                var disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? " · 已禁用" : "";
                if (archive.GetEntry("optifine/OptiFineTweaker.class") != null || archive.GetEntry("optifine/Config.class") != null)
                {
                    // The filename is displayed verbatim; it is not treated as a trustworthy version number.
                    result.Add(new("OptiFine（Mod）", Path.GetFileName(file) + disabled, true));
                    continue;
                }
                var metadata = archive.GetEntry("fabric.mod.json");
                if (metadata is { Length: <= 1048576 })
                {
                    using var reader = new StreamReader(metadata.Open());
                    var json = JsonNode.Parse(reader.ReadToEnd());
                    var id = json?["id"]?.GetValue<string>();
                    if (id is "fabric-api" or "qsl" or "quilted_fabric_api")
                        result.Add(new(id == "fabric-api" ? "Fabric API（Mod）" : "QSL / QFAPI（Mod）",
                            (json?["version"]?.ToString() ?? "版本未声明") + disabled, true));
                }
                else if (archive.GetEntry("quilt.mod.json") is { Length: <= 1048576 } quiltMetadata)
                {
                    using var reader = new StreamReader(quiltMetadata.Open());
                    var loader = JsonNode.Parse(reader.ReadToEnd())?["quilt_loader"];
                    if (loader?["id"]?.GetValue<string>() is "qsl" or "quilted_fabric_api")
                        result.Add(new("QSL / QFAPI（Mod）", (loader?["version"]?.ToString() ?? "版本未声明") + disabled, true));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
            { /* One unreadable mod remains visible in Mod management and does not alter component choices. */ }
        }
        return result;
    }
}

using System.Text.Json.Nodes;
using System.IO;

namespace XCL2.App.Services;

/// <summary>Preserve user extensions and user arguments without reintroducing the replaced loader's bootstrap.</summary>
internal static class ComponentProfileCustomization
{
    private static readonly HashSet<string> OwnedFields = new(StringComparer.Ordinal)
    {
        "id", "mainClass", "inheritsFrom", "jar", "arguments", "minecraftArguments", "libraries", "downloads",
        "assetIndex", "assets", "javaVersion", "clientVersion", "type", "time", "releaseTime", "minimumLauncherVersion",
        "complianceLevel", "logging", "xclMinecraftVersion", "xclComponentLoader", "xclComponentLoaderVersion"
    };
    private static readonly HashSet<string> OwnedGameFlags = new(StringComparer.Ordinal)
    {
        "--username", "--version", "--gameDir", "--assetsDir", "--assetIndex", "--uuid", "--accessToken", "--clientId",
        "--xuid", "--userType", "--versionType", "--userProperties", "--width", "--height", "--demo",
        "--quickPlayPath", "--quickPlaySingleplayer", "--quickPlayMultiplayer", "--quickPlayRealms", "--tweakClass", "--launchTarget"
    };
    private static bool OwnedJvm(string value) => value is "-cp" or "-classpath" or "--class-path" or "-p" or "--module-path" or
        "--add-modules" or "--add-opens" or "--add-exports" ||
        value.StartsWith("-Djava.library.path=", StringComparison.Ordinal) || value.StartsWith("-Djava.class.path=", StringComparison.Ordinal) ||
        value.StartsWith("-Djava.system.class.loader=", StringComparison.Ordinal) ||
        value.StartsWith("-DlibraryDirectory=", StringComparison.Ordinal) || value.StartsWith("-DlegacyClassPath=", StringComparison.Ordinal) ||
        value.StartsWith("-DignoreList=", StringComparison.Ordinal) || value.StartsWith("-DmergeModules=", StringComparison.Ordinal) ||
        value.StartsWith("-Dfml.", StringComparison.Ordinal) || value.StartsWith("-Dforge.", StringComparison.Ordinal) ||
        value.StartsWith("--module-path=", StringComparison.Ordinal) || value.StartsWith("--add-modules=", StringComparison.Ordinal) ||
        value.StartsWith("--add-opens=", StringComparison.Ordinal) || value.StartsWith("--add-exports=", StringComparison.Ordinal) ||
        value.StartsWith("--enable-native-access", StringComparison.Ordinal) || value.StartsWith("--sun-misc-unsafe-memory-access", StringComparison.Ordinal);

    internal static void Preserve(JsonObject target, IEnumerable<JsonObject> oldProfiles)
    {
        foreach (var old in oldProfiles)
        {
            foreach (var field in old)
                if (!OwnedFields.Contains(field.Key) && field.Value != null) target[field.Key] = field.Value.DeepClone();
            if (old["arguments"] is JsonObject arguments)
            {
                var targetArguments = target["arguments"] as JsonObject;
                if (targetArguments == null) target["arguments"] = targetArguments = new JsonObject();
                foreach (var kind in new[] { "game", "jvm" })
                {
                    var kept = FilterUserArguments(arguments[kind] as JsonArray, kind == "jvm");
                    if (kept.Count == 0) continue;
                    if (kind == "game" && target["minecraftArguments"]?.GetValue<string>() is { Length: > 0 } legacyTarget &&
                        targetArguments["game"] is not JsonArray { Count: > 0 })
                    {
                        if (kept.Any(node => node is not JsonValue))
                            throw new InvalidDataException("目标旧版本使用旧式启动参数，无法无损转换当前的条件游戏参数。原实例未修改。");
                        AppendLegacyArguments(target, legacyTarget, string.Join(" ", kept.Select(node => QuoteLegacy(node!.GetValue<string>()))));
                        continue;
                    }
                    var current = targetArguments[kind] as JsonArray;
                    if (current == null) targetArguments[kind] = current = new JsonArray();
                    AppendDistinctGroups(current, kept);
                }
                foreach (var field in arguments.Where(field => field.Key is not ("game" or "jvm")))
                    if (field.Value != null) targetArguments[field.Key] = field.Value.DeepClone();
            }
            if (old["minecraftArguments"]?.GetValue<string>() is { Length: > 0 } legacy)
            {
                var tokens = ParseLegacyArguments(legacy).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray();
                var kept = FilterUserArguments(new JsonArray(tokens), jvm: false);
                if (kept.Count > 0 && target["minecraftArguments"]?.GetValue<string>() is { } targetLegacy)
                    AppendLegacyArguments(target, targetLegacy, string.Join(" ", kept.Select(node => QuoteLegacy(node!.GetValue<string>()))));
                else if (kept.Count > 0)
                {
                    var targetArguments = target["arguments"] as JsonObject;
                    if (targetArguments == null) target["arguments"] = targetArguments = new JsonObject();
                    var game = targetArguments["game"] as JsonArray;
                    if (game == null) targetArguments["game"] = game = new JsonArray();
                    AppendDistinctGroups(game, kept);
                }
            }
        }
    }

    internal static JsonArray FilterUserArguments(JsonArray? original, bool jvm)
    {
        var result = new JsonArray();
        if (original == null) return result;
        for (var index = 0; index < original.Count; index++)
        {
            var node = original[index];
            if (node is JsonObject conditional)
            {
                var values = conditional["value"] is JsonArray array ? array : new JsonArray(conditional["value"]?.DeepClone());
                var filtered = FilterUserArguments(values, jvm);
                if (filtered.Count > 0)
                {
                    var copy = (JsonObject)conditional.DeepClone();
                    copy["value"] = filtered;
                    result.Add(copy);
                }
                continue;
            }
            if (node is not JsonValue || !node.AsValue().TryGetValue<string>(out var value)) continue;
            var key = value.Trim('"', '\'').Split('=')[0];
            bool owned = jvm ? OwnedJvm(value) : OwnedGameFlags.Contains(key) || key.StartsWith("--fml.", StringComparison.Ordinal);
            if (!jvm && key is "${auth_player_name}" or "${auth_session}" or "${auth_access_token}" or "${auth_uuid}" or "${game_directory}") owned = true;
            // Keep each flag with its following value, so removing --version never leaves an orphan value.
            var group = new List<JsonNode?> { node };
            if (value.StartsWith('-') && !value.Contains('='))
                while (index + 1 < original.Count && original[index + 1] is JsonValue next && next.TryGetValue<string>(out var nextValue) &&
                    !nextValue.StartsWith('-')) group.Add(original[++index]);
            if (!owned) foreach (var member in group) result.Add(member?.DeepClone());
        }
        return result;
    }

    private static void AppendLegacyArguments(JsonObject target, string current, string additions)
    {
        if (!current.EndsWith(" " + additions, StringComparison.Ordinal) && current != additions)
            target["minecraftArguments"] = current + " " + additions;
    }

    private static string QuoteLegacy(string value)
    {
        if (value.Contains('"')) throw new InvalidDataException("旧式启动参数不能无损表达含字面双引号的参数，原实例未修改。");
        return value.Any(char.IsWhiteSpace) || value.Length == 0 ? "\"" + value + "\"" : value;
    }

    private static IEnumerable<string> ParseLegacyArguments(string input)
    {
        var values = new List<string>();
        var value = new System.Text.StringBuilder();
        bool quoted = false, hasContent = false;
        foreach (var character in input)
        {
            if (character == '"') { quoted = !quoted; hasContent = true; continue; }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (hasContent) { values.Add(value.ToString()); value.Clear(); hasContent = false; }
                continue;
            }
            value.Append(character); hasContent = true;
        }
        if (quoted) throw new InvalidDataException("原版本的旧式启动参数包含未闭合引号，原实例未修改。");
        if (hasContent) values.Add(value.ToString());
        return values;
    }

    private static void AppendDistinctGroups(JsonArray target, JsonArray additions)
    {
        // Preserve sequence/pairs. If the entire user sequence is already present (reinstall), do not duplicate it.
        for (var offset = 0; offset <= target.Count - additions.Count; offset++)
            if (additions.Select((node, index) => JsonNode.DeepEquals(node, target[offset + index])).All(equal => equal)) return;
        foreach (var node in additions) target.Add(node?.DeepClone());
    }
}

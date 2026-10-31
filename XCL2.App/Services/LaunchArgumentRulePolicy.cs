using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Mojang argument rules use last matching action, including arch and version regex.</summary>
internal static class LaunchArgumentRulePolicy
{
    internal static string CurrentArchitecture => MinecraftPlatformRulePolicy.CurrentArchitecture;

    internal static bool Allows(JsonElement entry, IReadOnlyDictionary<string, bool>? features = null,
        string? architecture = null, string? osVersion = null)
    {
        if (!entry.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) return true;
        var allow = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object) continue;
            var matches = true;
            if (rule.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.Object)
            {
                string? Read(string key)
                {
                    if (!os.TryGetProperty(key, out var item)) return null;
                    if (item.ValueKind == JsonValueKind.String) return item.GetString();
                    matches = false;
                    return null;
                }
                var name = Read("name"); var arch = Read("arch"); var version = Read("version");
                matches = matches && MinecraftPlatformRulePolicy.Matches(name, arch, version, architecture, osVersion);
            }
            if (matches && rule.TryGetProperty("features", out var required) && required.ValueKind == JsonValueKind.Object)
            {
                foreach (var feature in required.EnumerateObject())
                {
                    if (feature.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { matches = false; break; }
                    var actual = features != null && features.TryGetValue(feature.Name, out var value) && value;
                    if (actual != feature.Value.GetBoolean()) { matches = false; break; }
                }
            }
            if (matches) allow = rule.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String && action.GetString() == "allow";
        }
        return allow;
    }

}

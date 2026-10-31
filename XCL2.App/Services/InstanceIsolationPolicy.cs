using System.IO;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>One read-only isolation decision for launch, previews, mods and imported instances.</summary>
public static class InstanceIsolationPolicy
{
    public static bool Resolve(string root, string versionId, bool defaultIsolation, IReadOnlyDictionary<string, bool> overrides)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId is "." or ".." || versionId.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0)
            throw new InvalidDataException("实例名称不是有效的单级目录名称。");
        var directory = Path.Combine(root, "versions", versionId);
        var settings = Path.Combine(directory, "xcl", "settings.json");
        try
        {
            if (File.Exists(settings) && new FileInfo(settings).Length <= 1024 * 1024)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(settings));
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var property in json.RootElement.EnumerateObject())
                        if (string.Equals(property.Name, "IsolateVersion", StringComparison.OrdinalIgnoreCase) &&
                            property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            return property.Value.GetBoolean();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        // An imported copy's worlds and mods live in its version directory, even if the optional settings file is damaged.
        if (File.Exists(Path.Combine(directory, "xcl-import-origin.json"))) return true;
        return overrides.TryGetValue(versionId, out var value) ? value : defaultIsolation;
    }
}

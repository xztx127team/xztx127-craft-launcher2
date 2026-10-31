using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Validate returned versions locally; an API filter is not a compatibility guarantee.</summary>
internal static class ModUpdateSelectionPolicy
{
    private static readonly HashSet<string> ModLoaders = new(StringComparer.OrdinalIgnoreCase)
        { "fabric", "forge", "neoforge", "quilt", "liteloader" };

    internal static bool MatchesFilters(IEnumerable<string>? gameVersions, IEnumerable<string>? loaders,
        string? gameVersion, IReadOnlyCollection<string>? acceptedLoaders = null)
        => (string.IsNullOrWhiteSpace(gameVersion) || gameVersions?.Contains(gameVersion.Trim(), StringComparer.OrdinalIgnoreCase) == true)
           && (acceptedLoaders == null || acceptedLoaders.Count == 0 ||
               loaders?.Any(loader => acceptedLoaders.Contains(loader, StringComparer.OrdinalIgnoreCase)) == true);

    internal static string? ResolveLoader(JsonElement current, string? requested, bool isMod)
    {
        var normalized = requested?.Trim().ToLowerInvariant();
        if (!isMod) return normalized;
        var declared = Values(current, "loaders").Where(ModLoaders.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized != null && ModLoaders.Contains(normalized) && (declared.Length == 0 || declared.Contains(normalized, StringComparer.OrdinalIgnoreCase)))
            return normalized;
        // Connector/bridge instances may contain Fabric files alongside Forge files.
        // Preserve each file's own loader instead of converting it to another build.
        return declared.Length == 1 ? declared[0].ToLowerInvariant() : null;
    }

    internal static bool TrySelect(JsonElement current, JsonElement versions, string gameVersion,
        string? loader, bool isMod, out JsonElement selected)
    {
        selected = default;
        gameVersion = gameVersion?.Trim() ?? "";
        if (gameVersion.Length == 0 || versions.ValueKind != JsonValueKind.Array) return false;
        var project = Text(current, "project_id");
        var requiredLoader = ResolveLoader(current, loader, isMod);
        if (project.Length == 0 || isMod && string.IsNullOrEmpty(requiredLoader)) return false;
        foreach (var candidate in versions.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object || Text(candidate, "project_id") != project ||
                Text(candidate, "id").Length == 0 || !Values(candidate, "game_versions").Contains(gameVersion, StringComparer.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(requiredLoader) && !Values(candidate, "loaders").Contains(requiredLoader, StringComparer.OrdinalIgnoreCase) ||
                !candidate.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
                continue;
            if (selected.ValueKind == JsonValueKind.Undefined || Published(candidate) > Published(selected)) selected = candidate;
        }
        if (selected.ValueKind == JsonValueKind.Undefined || Text(selected, "id") == Text(current, "id")) return false;
        // A removed/unlisted current release must not silently turn an update into a downgrade.
        if (Values(current, "game_versions").Contains(gameVersion, StringComparer.OrdinalIgnoreCase) &&
            Published(current) != DateTimeOffset.MinValue && Published(selected) != DateTimeOffset.MinValue &&
            Published(selected) <= Published(current)) return false;
        return true;
    }

    private static IEnumerable<string> Values(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "")
            : Array.Empty<string>();
    private static string Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static DateTimeOffset Published(JsonElement value) =>
        DateTimeOffset.TryParse(Text(value, "date_published"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.MinValue;
}

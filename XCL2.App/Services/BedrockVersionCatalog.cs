using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

internal sealed record BedrockCatalogEntry(string Uuid, string Name, string? BaseVersion, DateTime? Date,
    bool Preview, string? Url = null, bool DirectUrlAvailable = false);

/// <summary>版本索引和缓存的纯解析器；坏条目单独跳过，渠道和 GDK 来源始终保留。</summary>
internal static class BedrockVersionCatalog
{
    private static readonly Regex VersionName = new(@"^\d+(?:\.\d+){1,3}(?:\s+(?:Preview|Beta))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static List<BedrockCatalogEntry> ParseMetadata(string json)
    {
        var result = new List<BedrockCatalogEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Property(root, "version", out var version)) root = version;
            if (!Property(root, "versions", out var versions)) return result;
            foreach (var family in new[] { "gdk", "uwp" })
            {
                if (!Property(versions, family, out var channels)) continue;
                foreach (var channel in new[] { "release", "preview" })
                {
                    if (!Property(channels, channel, out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    foreach (var value in list.EnumerateArray())
                    {
                        var name = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
                        if (name == null || !VersionName.IsMatch(name)) continue;
                        result.Add(new("", name, name, null, channel == "preview", DirectUrlAvailable: family == "gdk"));
                    }
                }
            }
        }
        catch (JsonException) { }
        return Normalize(result);
    }

    public static List<BedrockCatalogEntry> ParseLegacy(string json)
    {
        var result = new List<BedrockCatalogEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var value in doc.RootElement.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2) continue;
                var name = value[0].ValueKind == JsonValueKind.String ? value[0].GetString()?.Trim() : null;
                var uuid = value[1].ValueKind == JsonValueKind.String ? value[1].GetString() : null;
                if (name == null || !VersionName.IsMatch(name) || !Guid.TryParse(uuid, out _)) continue;
                var flag = 0;
                if (value.GetArrayLength() > 2 && (value[2].ValueKind != JsonValueKind.Number ||
                    !value[2].TryGetInt32(out flag) || flag < 0)) continue;
                var preview = flag > 0;
                result.Add(new(uuid!, name, name, null, preview));
            }
        }
        catch (JsonException) { }
        return Normalize(result);
    }

    public static List<BedrockCatalogEntry> ParseCache(string json)
    {
        var result = new List<BedrockCatalogEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = doc.RootElement;
            if (list.ValueKind == JsonValueKind.Object && Property(list, "versions", out var entries)) list = entries;
            if (list.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = Text(item, "Name");
                if (name == null || !VersionName.IsMatch(name)) continue;
                var uuid = Text(item, "Uuid") ?? "";
                if (uuid.Length != 0 && !Guid.TryParse(uuid, out _)) continue;
                var baseVersion = Text(item, "BaseVersion") ?? Text(item, "base") ?? name;
                var preview = Boolean(item, "Preview") || name.Contains("preview", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("beta", StringComparison.OrdinalIgnoreCase);
                if (Property(item, "Channel", out var channel))
                {
                    if (channel.ValueKind == JsonValueKind.Number && channel.TryGetInt32(out var number) && number is 0 or 1)
                        preview = number == 1;
                    else if (channel.ValueKind == JsonValueKind.String && channel.GetString() is { } text &&
                        (text.Equals("Preview", StringComparison.OrdinalIgnoreCase) || text.Equals("Stable", StringComparison.OrdinalIgnoreCase)))
                        preview = text.Equals("Preview", StringComparison.OrdinalIgnoreCase);
                    else continue;
                }
                DateTime? date = DateTime.TryParse(Text(item, "Date"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsedDate) ? parsedDate : null;
                var direct = Boolean(item, "DirectUrlAvailable") || Boolean(item, "directUrl");
                var url = Text(item, "Url");
                // Metadata-only UWP entries are real versions, but must never be given invented FE3 UUIDs.
                result.Add(new(uuid, name, baseVersion, date, preview, url, direct));
            }
        }
        catch (JsonException) { }
        return Normalize(result);
    }

    public static string SerializeCache(IEnumerable<BedrockCatalogEntry> versions)
        => JsonSerializer.Serialize(new { SchemaVersion = 2, FetchedAtUtc = DateTime.UtcNow, Versions = Normalize(versions) },
            new JsonSerializerOptions { WriteIndented = true });

    public static List<BedrockCatalogEntry> MergeMetadataAndLegacy(IEnumerable<BedrockCatalogEntry> metadata,
        IEnumerable<BedrockCatalogEntry> legacy) => Normalize(metadata.Concat(legacy));

    public static List<BedrockCatalogEntry> Normalize(IEnumerable<BedrockCatalogEntry> entries)
        => entries.GroupBy(entry => (entry.Preview, entry.Name.ToLowerInvariant()))
            .Select(group =>
            {
                var first = group.OrderByDescending(entry => entry.DirectUrlAvailable)
                    .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.Uuid)).First();
                return string.IsNullOrEmpty(first.Uuid) && group.FirstOrDefault(entry => !string.IsNullOrEmpty(entry.Uuid)) is { } identified
                    ? first with { Uuid = identified.Uuid } : first;
            })
            .OrderByDescending(entry => VersionKey(entry.BaseVersion ?? entry.Name))
            .ThenByDescending(entry => entry.Date ?? DateTime.MinValue).ToList();

    private static Version VersionKey(string name)
    {
        var number = Regex.Match(name, @"\d+(?:\.\d+){1,3}").Value;
        return Version.TryParse(number, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision))
            : new Version(0, 0, 0, 0);
    }

    private static bool Property(JsonElement item, string name, out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object)
            foreach (var property in item.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }
    private static string? Text(JsonElement item, string name)
        => Property(item, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Boolean(JsonElement item, string name)
        => Property(item, name, out var value) && (value.ValueKind == JsonValueKind.True ||
            value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var yes) && yes);
}

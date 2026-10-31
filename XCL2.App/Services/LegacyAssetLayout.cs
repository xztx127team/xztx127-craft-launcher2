using System.IO;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>旧游戏按文件名读取资源，不能只留下现代 assets/objects 中的散列文件。</summary>
internal static class LegacyAssetLayout
{
    internal static string GetGameAssetsDirectory(string minecraftDir, string indexId)
    {
        using var index = ReadIndex(minecraftDir, indexId);
        return index != null && IsEnabled(index.RootElement, "virtual")
            ? Path.Combine(minecraftDir, "assets", "virtual", indexId)
            : Path.Combine(minecraftDir, "assets");
    }

    internal static void PrepareForLaunch(string minecraftDir, string gameDir, string indexId)
    {
        using var index = ReadIndex(minecraftDir, indexId);
        if (index == null) return;
        var root = index.RootElement;
        var destinations = new List<string>();
        if (IsEnabled(root, "virtual")) destinations.Add(Path.Combine(minecraftDir, "assets", "virtual", indexId));
        if (IsEnabled(root, "map_to_resources")) destinations.Add(Path.Combine(gameDir, "resources"));
        if (destinations.Count == 0 || !root.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object) return;
        foreach (var item in objects.EnumerateObject())
        {
            if (!IsSafeRelativePath(item.Name)) throw new InvalidDataException("旧版资源索引包含无效文件路径。");
            if (!item.Value.TryGetProperty("hash", out var hashValue) || hashValue.ValueKind != JsonValueKind.String) continue;
            var hash = hashValue.GetString() ?? "";
            if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("旧版资源索引包含无效资源散列。");
            var source = Path.Combine(minecraftDir, "assets", "objects", hash[..2], hash);
            if (!File.Exists(source)) continue; // 缺失资源由已有的资源补全流程处理。
            var sourceLength = new FileInfo(source).Length;
            foreach (var destinationRoot in destinations)
            {
                var destination = Path.Combine(destinationRoot, item.Name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(destination) && new FileInfo(destination).Length == sourceLength) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }
    }

    private static JsonDocument? ReadIndex(string minecraftDir, string indexId)
    {
        if (string.IsNullOrWhiteSpace(indexId) || indexId is "." or ".." ||
            indexId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new InvalidDataException("旧版资源索引名称无效。");
        var path = Path.Combine(minecraftDir, "assets", "indexes", indexId + ".json");
        return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null;
    }

    private static bool IsEnabled(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsSafeRelativePath(string path)
        => !string.IsNullOrWhiteSpace(path) && !path.Contains('\\') && !path.Contains(':') && !path.StartsWith('/') &&
           path.Split('/').All(part => part.Length > 0 && part is not "." and not "..");
}

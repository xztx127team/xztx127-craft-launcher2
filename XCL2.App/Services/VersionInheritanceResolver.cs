using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>One validated inheritance chain shared by real launch construction and library checks.</summary>
internal static class VersionInheritanceResolver
{
    internal sealed record Layer(string VersionId, string Directory, VersionDetail Detail);
    internal sealed record Resolution(IReadOnlyList<Layer> Layers, VersionDetail Effective, Layer ClientLayer);

    internal static Resolution Load(string minecraftDirectory, string versionId,
        Func<string, string, string?> resolveJson, string? architecture = null)
    {
        var layers = new List<Layer>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = versionId;
        while (!string.IsNullOrWhiteSpace(current))
        {
            ValidateId(current);
            if (!visited.Add(current))
                throw new InvalidDataException("版本继承出现循环：" + string.Join(" → ", layers.Select(x => x.VersionId).Append(current)));
            if (layers.Count >= 64) throw new InvalidDataException("版本继承超过 64 层，请检查 inheritsFrom 配置。");
            var directory = Path.Combine(minecraftDirectory, "versions", current);
            var json = resolveJson(directory, current);
            if (json == null)
                throw new InvalidDataException(layers.Count == 0
                    ? $"找不到版本「{current}」的 version JSON。"
                    : $"版本「{layers[^1].VersionId}」需要的父版本「{current}」不存在，请先补全父版本。");
            VersionDetail detail;
            try
            {
                detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(json))
                    ?? throw new InvalidDataException("JSON 内容为空。");
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            { throw new InvalidDataException($"无法读取版本「{current}」的 JSON：{ex.Message}", ex); }
            if (!string.IsNullOrWhiteSpace(detail.Id)) ValidateId(detail.Id);
            detail.Libraries ??= new();
            layers.Add(new(current, directory, detail));
            current = detail.InheritsFrom ?? "";
        }
        if (layers.Count == 0) throw new InvalidDataException("版本名称不能为空。");
        layers.Reverse();
        var effective = new VersionDetail();
        var libraries = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
        var jvm = new List<object>();
        var game = new List<object>();
        var downloads = new Dictionary<string, DownloadArtifact>(StringComparer.Ordinal);
        var client = layers[0];
        foreach (var layer in layers)
        {
            var value = layer.Detail;
            if (!string.IsNullOrWhiteSpace(value.Id)) effective.Id = value.Id;
            if (!string.IsNullOrWhiteSpace(value.MainClass)) effective.MainClass = value.MainClass;
            if (value.MinecraftArgumentsLegacy != null) effective.MinecraftArgumentsLegacy = value.MinecraftArgumentsLegacy;
            if (value.Assets != null) effective.Assets = value.Assets;
            if (value.AssetIndex != null)
            {
                effective.AssetIndex = value.AssetIndex;
                if (value.Assets == null) effective.Assets = null;
            }
            if (value.ClientVersion != null) effective.ClientVersion = value.ClientVersion;
            if (value.JavaVersion != null) effective.JavaVersion = value.JavaVersion;
            if (value.Arguments?.Jvm != null) jvm.AddRange(value.Arguments.Jvm);
            if (value.Arguments?.Game != null) game.AddRange(value.Arguments.Game);
            // An inapplicable child entry must not erase a Windows library inherited from its parent.
            foreach (var library in value.Libraries.Where(library => library.IsApplicableToCurrentOs(architecture)))
                libraries[LibraryIdentity(library)] = library;
            if (value.Downloads != null)
            {
                foreach (var item in value.Downloads) downloads[item.Key] = item.Value;
                if (value.Downloads.ContainsKey("client")) client = layer;
            }
            if (!string.IsNullOrWhiteSpace(value.Jar))
            {
                ValidateId(value.Jar);
                var jarDirectory = Path.Combine(minecraftDirectory, "versions", value.Jar);
                var namedLayer = layers.FirstOrDefault(x => string.Equals(x.VersionId, value.Jar, StringComparison.OrdinalIgnoreCase));
                if (namedLayer != null) client = namedLayer;
                else
                {
                    var jarJson = resolveJson(jarDirectory, value.Jar);
                    var jarDetail = jarJson == null ? new VersionDetail { Id = value.Jar }
                        : JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(jarJson)) ?? new VersionDetail { Id = value.Jar };
                    if (!string.IsNullOrWhiteSpace(jarDetail.Id)) ValidateId(jarDetail.Id);
                    client = new Layer(value.Jar, jarDirectory, jarDetail);
                }
            }
        }
        effective.Libraries = libraries.Values.ToList();
        effective.Downloads = downloads.Count > 0 ? downloads : null;
        effective.Arguments = new ArgumentsSpec { Jvm = jvm, Game = game };
        return new(layers, effective, client);
    }

    private static string LibraryIdentity(LibraryEntry library)
    {
        var parts = library.Name.Split(':');
        return parts.Length >= 3 ? parts[0] + ":" + parts[1] + ":" + string.Join(":", parts.Skip(3)) : library.Name;
    }

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(new[] { '/', '\\', ':', '<', '>', '|', '"', '?', '*' }) >= 0)
            throw new InvalidDataException("版本继承包含无效的目录名称：" + id);
    }
}

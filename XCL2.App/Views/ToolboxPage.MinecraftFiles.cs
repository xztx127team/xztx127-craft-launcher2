using System.IO;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class ToolboxPage
{
    private Dictionary<string, VersionManifestEntry> _vanillaFileVersions = new(StringComparer.Ordinal);
    private int _loaderListRevision, _loaderBuildRevision;

    private async Task<List<string>> LoadVanillaFileVersionsAsync(int revision)
    {
        using var service = DownloadService.CreateFromConfig(_owner.ConfigService.Config);
        var manifest = await service.GetVersionManifestAsync();
        var entries = manifest.Versions.SortNewestFirst().ToList();
        if (revision == _loaderListRevision) _vanillaFileVersions = entries.GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return entries.Select(v => v.Id).Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task<string> DownloadVanillaFileAsync(VersionManifestEntry entry, string kind,
        IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        var directory = ResourceDownloadPath.ResolveChild(Path.Combine(GetLoaderDownloadDir(), "Minecraft"), entry.Id);
        Directory.CreateDirectory(directory);
        var metadata = Path.Combine(Path.GetTempPath(), "xcl-version-" + Guid.NewGuid().ToString("N") + ".json");
        bool preferMirror = _owner.ConfigService.Config.Source != DownloadSource.Official;
        var jsonDestination = ResourceDownloadPath.ResolveChild(directory, entry.Id + ".json");
        void ValidateJson(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("id", out var id) || id.GetString() != entry.Id)
                throw new InvalidDataException("Minecraft 版本 JSON 与所选版本不一致。");
        }
        try
        {
            var jsonPath = kind == "版本 JSON" ? jsonDestination : metadata;
            await DownloadEndpoints.DownloadFileWithFallbackAsync(_dlHttp, entry.Url, preferMirror, jsonPath,
                "Minecraft 版本 JSON 下载失败", progress, ct, expectedSha1: entry.Sha1, validateFile: ValidateJson);
            if (kind == "版本 JSON") return jsonPath;
            var detail = JsonSerializer.Deserialize<VersionDetail>(await File.ReadAllTextAsync(jsonPath, ct));
            var artifactName = kind == "服务端 JAR" ? "server" : "client";
            if (detail?.Downloads == null || !detail.Downloads.TryGetValue(artifactName, out var artifact))
                throw new InvalidOperationException($"版本 {entry.Id} 没有发布{kind}，可改选客户端 JAR 或版本 JSON。");
            var name = entry.Id + (artifactName == "server" ? "-server" : "") + ".jar";
            return await DownloadEndpoints.DownloadFileWithFallbackAsync(_dlHttp, artifact.Url, preferMirror,
                ResourceDownloadPath.ResolveChild(directory, name), "Minecraft JAR 下载失败", progress, ct,
                expectedSha1: artifact.Sha1, expectedSize: artifact.Size, validateFile: ValidateStandaloneLoaderJar);
        }
        finally
        {
            try { if (File.Exists(metadata)) File.Delete(metadata); }
            catch (IOException ex) { ErrorPresenter.LogFallback("清理版本下载缓存失败", ex); }
        }
    }
}

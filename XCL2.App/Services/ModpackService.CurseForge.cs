using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace XCL2.App.Services;

public partial class ModpackService
{
    internal sealed record CurseForgePackFile(string Name, string Url, long Size, string? Sha1, string? Md5);
    internal static bool IsCurseForgeManifest(ZipArchiveEntry entry)
    {
        try
        {
            using var document = JsonDocument.Parse(ArchiveImportSafety.ReadManifest(entry));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("minecraft", out _) && root.TryGetProperty("files", out _);
        }
        catch (JsonException) { return false; }
    }

    public static bool IsCurseForgePack(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.GetEntry("manifest.json") is { } entry && IsCurseForgeManifest(entry);
    }

    /// <summary>Resolve exact project/file IDs through the official API; never guess restricted CDN URLs.</summary>
    public async Task<MrpackImportResult> ImportCurseForgeAsync(string path, string destination,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var metadata = CurseForgeApiTransport.CreateClient();
        var key = new CurseForgeKeyService().TryGetKey();
        return await ImportCurseForgeCoreAsync(path, destination, async (project, file, token) =>
        {
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("此 CurseForge 整合包需要解析文件清单，请先在设置中配置可用的 CurseForge API Key；尚未写入内容。");
            var json = await CurseForgeApiTransport.GetStringAsync(metadata,
                $"https://api.curseforge.com/v1/mods/{project}/files/{file}", key, token);
            using var document = JsonDocument.Parse(json);
            var data = document.RootElement.GetProperty("data");
            if (data.GetProperty("id").GetInt32() != file || data.GetProperty("modId").GetInt32() != project)
                throw new InvalidDataException("CurseForge 返回了不匹配的项目或文件，未执行导入。");
            var name = data.GetProperty("fileName").GetString() ?? "";
            var url = data.TryGetProperty("downloadUrl", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() ?? "" : "";
            string? sha1 = null, md5 = null;
            if (data.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Array)
                foreach (var hash in hashes.EnumerateArray())
                {
                    var algorithm = hash.GetProperty("algo").GetInt32();
                    if (algorithm == 1) sha1 = hash.GetProperty("value").GetString();
                    if (algorithm == 2) md5 = hash.GetProperty("value").GetString();
                }
            return new CurseForgePackFile(name, url, data.GetProperty("fileLength").GetInt64(), sha1, md5);
        }, progress, ct);
    }

    internal async Task<MrpackImportResult> ImportCurseForgeCoreAsync(string path, string destination,
        Func<int, int, CancellationToken, Task<CurseForgePackFile>> resolve,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArchiveImportSafety.CheckLinks(path);
        using var archive = ZipFile.OpenRead(path);
        ArchiveImportSafety.ValidateArchive(archive, destination);
        var manifest = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("缺少 CurseForge manifest.json，未执行导入。");
        using var document = JsonDocument.Parse(ArchiveImportSafety.ReadManifest(manifest));
        var root = document.RootElement;
        if (!root.TryGetProperty("minecraft", out var minecraft) || !minecraft.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(version.GetString()) ||
            !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > ArchiveImportSafety.MaximumEntries)
            throw new InvalidDataException("CurseForge 清单缺少有效的 Minecraft 版本或文件清单，未执行导入。");
        var prefix = root.TryGetProperty("overrides", out var overrideNode) && overrideNode.ValueKind == JsonValueKind.String
            ? overrideNode.GetString() : "overrides";
        if (string.IsNullOrWhiteSpace(prefix)) throw new InvalidDataException("CurseForge overrides 路径无效。");
        prefix = prefix.Replace('\\', '/').TrimEnd('/');
        _ = ArchiveImportSafety.Resolve(destination, prefix);
        prefix += "/";
        var overrides = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name) && entry.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry => (Entry: entry, Relative: entry.FullName.Replace('\\', '/')[prefix.Length..])).ToArray();
        foreach (var entry in overrides) _ = ArchiveImportSafety.Resolve(destination, entry.Relative);
        var downloads = new List<(CurseForgePackFile File, string Destination)>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in files.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (reference.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.False) continue;
            if (!reference.TryGetProperty("projectID", out var project) || !project.TryGetInt32(out var projectId) || projectId <= 0 ||
                !reference.TryGetProperty("fileID", out var id) || !id.TryGetInt32(out var fileId) || fileId <= 0)
                throw new InvalidDataException("CurseForge 文件清单缺少有效 projectID/fileID，未写入内容。");
            progress?.Report($"解析 CurseForge 文件 {downloads.Count + 1}/{files.GetArrayLength()} ...");
            var file = await resolve(projectId, fileId, ct);
            if (string.IsNullOrWhiteSpace(file.Name) || file.Name != Path.GetFileName(file.Name) || file.Name.Contains('\\') || file.Name.Contains('/') ||
                !file.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("此 CurseForge 清单含无法确定目标分类的文件；仅支持明确的 .jar 模组清单，未写入内容。");
            if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
                throw new InvalidDataException($"CurseForge 未授权提供 {file.Name} 的可用下载地址，请从官方获取文件；未写入内容。");
            if (file.Size <= 0 || string.IsNullOrWhiteSpace(file.Sha1) && string.IsNullOrWhiteSpace(file.Md5))
                throw new InvalidDataException($"CurseForge 未提供 {file.Name} 的完整大小和校验信息；未写入内容。");
            var target = ArchiveImportSafety.Resolve(destination, "mods/" + file.Name);
            if (!targets.Add(target)) throw new InvalidDataException("CurseForge 清单存在同名目标文件，未写入内容。");
            downloads.Add((file, target));
        }
        if (downloads.Count == 0 && overrides.Length == 0) throw new InvalidDataException("此 CurseForge 包没有可导入的必需模组或附带文件。");
        // All required metadata and destinations are validated before touching instance content.
        foreach (var entry in overrides) ArchiveImportSafety.Extract(entry.Entry, destination, entry.Relative, ct);
        using var http = LauncherHttpClientFactory.Create(TimeSpan.FromMinutes(5));
        foreach (var (file, target) in downloads)
        {
            ct.ThrowIfCancellationRequested(); ArchiveImportSafety.CheckLinks(target);
            progress?.Report("下载 " + file.Name + " ...");
            await DownloadEndpoints.DownloadFileWithFallbackAsync(http, file.Url, DownloadEndpoints.PreferMirror, target,
                "CurseForge 文件下载失败：" + file.Name, ct: ct, expectedSha1: file.Sha1, expectedMd5: file.Md5, expectedSize: file.Size);
        }
        progress?.Report("导入完成；未选择清单中标为可选的文件");
        var name = root.TryGetProperty("name", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() ?? "CurseForge 整合包" : "CurseForge 整合包";
        return new MrpackImportResult(name, version.GetString(), new());
    }
}

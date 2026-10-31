using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XCL2.App.Services;

public partial class ModpackService
{
    private static readonly string[] IncludedFolders = { "mods", "config", "resourcepacks", "shaderpacks" };
    private const string ManifestFileName = "xclpack.json";

    /// <summary>
    /// 导出：把 versionDir 下 IncludedFolders 里存在的文件夹打包进 destZipPath。
    /// 打包前会写一个简单的 manifest（名称、版本号、加载器信息、打包时间），方便导入方核对内容。
    /// </summary>
    public void Export(string versionDir, string destZipPath, ModpackManifest manifest,
        IProgress<string>? progress = null)
        => Export(versionDir, destZipPath, manifest, progress == null ? null : new Progress<ProgressInfo>(p => progress.Report(p.Stage)));

    /// <summary>
    /// 导出（带真正的文件级进度）：IProgress&lt;ProgressInfo&gt; 版本，Done/Total 是已复制/总计
    /// 的文件数，CurrentFile 是正在复制的文件名——配合 ProgressDialog 可以显示真实的进度条和
    /// "当前正在导出哪个文件"，而不是旧版 IProgress&lt;string&gt; 那种只有几句阶段性文字、
    /// 复制大量文件时进度条长时间不动、看起来像卡死的问题。
    /// </summary>
    public void Export(string versionDir, string destZipPath, ModpackManifest manifest,
        IProgress<ProgressInfo>? progress)
    {

        var tmpDir = Path.Combine(Path.GetTempPath(), $"xcl2_pack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // 跟 ExportMrpack 同样的兜底：manifest 里的版本信息不可信时就地重新解析，
            // 保证 .xclpack 里的 xclpack.json 也带上正确的 MC 版本 / 加载器版本，
            // 否则本启动器自己再导入该包时同样读不到版本信息（"无法导入"的另一半成因）。
            manifest = NormalizeManifest(versionDir, manifest);

            File.WriteAllText(Path.Combine(tmpDir, ManifestFileName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            // 先扫一遍算总文件数，才能算出真实的 Done/Total 百分比——只扫存在的文件夹，
            // 跟下面实际复制时的过滤条件一致，避免总数和实际复制数对不上。
            var sourceFolders = IncludedFolders
                .Select(f => Path.Combine(versionDir, f))
                .Where(Directory.Exists)
                .ToList();
            var totalFiles = sourceFolders.Sum(CountFiles);
            var doneFiles = 0;

            if (sourceFolders.Count == 0)
                throw new InvalidOperationException("该版本下没有找到 mods/config/resourcepacks/shaderpacks 中任何一个文件夹，没有可打包的内容。");

            foreach (var folder in IncludedFolders)
            {
                var src = Path.Combine(versionDir, folder);
                if (!Directory.Exists(src)) continue;
                CopyDirectory(src, Path.Combine(tmpDir, folder), (file, name) =>
                {
                    doneFiles++;
                    progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Packaging_Files", "打包文件"), doneFiles, totalFiles, name));
                });
            }

            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Creating_Archive", "生成压缩包"), totalFiles, totalFiles, Path.GetFileName(destZipPath)));
            ArchiveImportSafety.ExportAtomically(destZipPath, temporary =>
                ZipFile.CreateFromDirectory(tmpDir, temporary, CompressionLevel.Optimal, includeBaseDirectory: false));
            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Export_Complete", "导出完成"), totalFiles, totalFiles, ""));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* 忽略临时目录清理失败 */ }
        }
    }

    /// <summary>
    /// 导入：把 zip 包内 IncludedFolders 对应的文件夹解压合并到 targetVersionDir。
    /// 默认采用"合并覆盖"策略——已存在同名文件会被整合包内容覆盖，不存在的文件保留，
    /// 这样用户可以把整合包"叠加"安装到一个已有版本上，而不是每次都完全清空重装。
    /// 如果用户想要"全新安装"，应该先自己新建一个空的版本目录再导入。
    /// </summary>
    public ModpackManifest? Import(string zipPath, string targetVersionDir, IProgress<string>? progress = null)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("找不到整合包文件。", zipPath);

        ArchiveImportSafety.CheckLinks(zipPath);
        using var archive = ZipFile.OpenRead(zipPath);
        ArchiveImportSafety.ValidateArchive(archive, targetVersionDir);
        if (archive.GetEntry("manifest.json") is { } cf && IsCurseForgeManifest(cf))
            throw new InvalidOperationException("这是 CurseForge 清单包，请使用联网整合包安装流程解析文件清单；未写入内容。");
        ModpackManifest? manifest = null;
        var manifestEntry = archive.GetEntry(ManifestFileName) ?? archive.GetEntry("xcl2-modpack.json");
        if (manifestEntry != null)
        {
            try { manifest = JsonSerializer.Deserialize<ModpackManifest>(ArchiveImportSafety.ReadManifest(manifestEntry)); }
            catch (JsonException) { /* Content-only imports may omit display metadata. */ }
        }
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name) &&
            IncludedFolders.Contains(entry.FullName.Replace('\\', '/').Split('/')[0])).ToArray();
        if (files.Length == 0)
            throw new InvalidOperationException("该压缩包里没有找到 mods/config/resourcepacks/shaderpacks 内容，未执行导入。");
        // Validate every selected destination before creating or overwriting any file.
        foreach (var entry in files) _ = ArchiveImportSafety.Resolve(targetVersionDir, entry.FullName.Replace('\\', '/'));
        progress?.Report("解压整合包 ...");
        foreach (var entry in files)
            ArchiveImportSafety.Extract(entry, targetVersionDir, entry.FullName.Replace('\\', '/'));
        progress?.Report("导入完成");
        return manifest;
    }

    /// <summary>
    /// 判断一个整合包 zip 是不是 Modrinth 的 .mrpack 格式：只要顶层有 modrinth.index.json 就是。
    /// .xclpack / 普通 .zip 整合包顶层是 xclpack.json 或者直接就是 mods/config 等文件夹，
    /// 不会有该文件，两者不会误判。
    /// </summary>
    public static bool IsMrpack(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.GetEntry("modrinth.index.json") != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 导入 Modrinth .mrpack 整合包：跟 .xclpack/.zip 完全不同的格式——.mrpack 本身不含 mod jar 文件，
    /// 只有一份 modrinth.index.json 清单（列出每个 mod 的下载直链 + SHA1），以及一个 overrides/
    /// 文件夹（config、资源包等直接文件）。导入需要：
    /// 1) 解析 modrinth.index.json，按每个 file 条目的 downloads 全部候选（含镜像与清单哈希）下载到 targetVersionDir 对应路径
    ///    （通常是 mods/xxx.jar，但清单里的 path 字段可能指向 config/ 等其它位置，照抄 path 用）。
    /// 2) 解压 overrides/（以及可选的 client-overrides/）目录内容到 targetVersionDir 根下。
    /// 3) 下载失败的单个 mod 不中止整个导入（部分整合包体积很大，某个源暂时挂了不该导致整体失败），
    ///    累计失败的文件名收集起来，导入完成后通过返回值告知调用方。
    /// </summary>
    // Preserve the original CLR signature for already compiled callers.
    public Task<MrpackImportResult> ImportMrpackAsync(string mrpackPath, string targetVersionDir,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => ImportMrpackAsync(mrpackPath, targetVersionDir, progress, ct, server: false);

    public async Task<MrpackImportResult> ImportMrpackAsync(string mrpackPath, string targetVersionDir,
        IProgress<string>? progress, CancellationToken ct, bool server)
    {
        if (!File.Exists(mrpackPath)) throw new FileNotFoundException("找不到整合包文件。", mrpackPath);
        ArchiveImportSafety.CheckLinks(mrpackPath);
        using var archive = ZipFile.OpenRead(mrpackPath);
        ArchiveImportSafety.ValidateArchive(archive, targetVersionDir);
        var indexEntry = archive.GetEntry("modrinth.index.json")
            ?? throw new InvalidOperationException("找不到 modrinth.index.json，未执行导入。");
        var index = JsonSerializer.Deserialize<MrpackIndex>(ArchiveImportSafety.ReadManifest(indexEntry))
            ?? throw new InvalidDataException("modrinth.index.json 解析失败。");
        if (index.Files == null || index.Files.Count > ArchiveImportSafety.MaximumEntries || index.Files.Any(file => file == null))
            throw new InvalidDataException("整合包文件清单无效或过大。");
        var files = index.Files.Where(file => file != null &&
            !string.Equals(server ? file.Env?.Server : file.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase)).ToArray();
        // Validate the complete selected manifest before an override or download
        // touches the instance. Missing hashes must not silently disable integrity.
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var target = ArchiveImportSafety.Resolve(targetVersionDir, file.Path);
            if (!targets.Add(target)) throw new InvalidDataException("整合包清单包含重复文件路径，未执行导入。");
            if (file.Hashes == null || !IsHash(file.Hashes.GetValueOrDefault("sha512"), 128) ||
                !IsHash(file.Hashes.GetValueOrDefault("sha1"), 40) || file.FileSize <= 0)
                throw new InvalidDataException("整合包清单缺少有效的 SHA-512、SHA-1 或文件大小，未执行导入。");
            if (file.Downloads == null || file.Downloads.Count is < 1 or > 16 ||
                file.Downloads.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0))
                throw new InvalidDataException("整合包包含无效或不安全的下载地址，未执行导入。");
        }
        var sidePrefix = server ? "server-overrides/" : "client-overrides/";
        var common = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name) && entry.FullName.Replace('\\', '/').StartsWith("overrides/", StringComparison.Ordinal));
        var specific = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name) && entry.FullName.Replace('\\', '/').StartsWith(sidePrefix, StringComparison.Ordinal));
        var overrides = common.Select(entry => (Entry: entry, Relative: entry.FullName.Replace('\\', '/')[10..]))
            .Concat(specific.Select(entry => (Entry: entry, Relative: entry.FullName.Replace('\\', '/')[sidePrefix.Length..]))).ToArray();
        foreach (var entry in overrides) _ = ArchiveImportSafety.Resolve(targetVersionDir, entry.Relative);
        progress?.Report(server ? "导入服务器附带文件 ..." : "导入客户端附带文件 ...");
        foreach (var entry in overrides) ArchiveImportSafety.Extract(entry.Entry, targetVersionDir, entry.Relative, ct);
        using var http = LauncherHttpClientFactory.Create(TimeSpan.FromMinutes(5));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Launcher/1.0 (+https://github.com/xztx127-craft/xcl2)");
        var failed = new List<string>();
        for (var i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); var file = files[i];
            if (file.Downloads == null || !file.Downloads.Any(url => !string.IsNullOrWhiteSpace(url)))
            { failed.Add(file.Path); continue; }
            var destination = ArchiveImportSafety.Resolve(targetVersionDir, file.Path);
            progress?.Report($"下载 {Path.GetFileName(file.Path)} ({i + 1}/{files.Length}) ...");
            try
            {
                ArchiveImportSafety.CheckLinks(destination);
                await DownloadEndpoints.DownloadFilesWithFallbackAsync(http, file.Downloads, DownloadEndpoints.PreferMirror,
                    destination, $"下载整合包文件 {file.Path} 失败", ct: ct,
                    expectedSha1: file.Hashes?.GetValueOrDefault("sha1"),
                    expectedSha512: file.Hashes?.GetValueOrDefault("sha512"),
                    expectedSize: file.FileSize > 0 ? file.FileSize : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not Plugins.PluginFlowCanceledException)
            { failed.Add(file.Path); }
        }
        progress?.Report(failed.Count == 0 ? "导入完成" : $"内容已导入，但 {failed.Count} 个文件下载失败");
        return new MrpackImportResult(index.Name, index.Dependencies?.Minecraft, failed);
    }

    internal static bool IsHash(string? value, int length) =>
        value != null && value.Length == length && value.All(Uri.IsHexDigit);

    public void ExportMrpack(string versionDir, string destMrpackPath, ModpackManifest manifest,
        IProgress<string>? progress = null)
        => ExportMrpack(versionDir, destMrpackPath, manifest, progress == null ? null : new Progress<ProgressInfo>(p => progress.Report(p.Stage)));

    
    public void ExportMrpack(string versionDir, string destMrpackPath, ModpackManifest manifest,
        IProgress<ProgressInfo>? progress)
    {

        var tmpDir = Path.Combine(Path.GetTempPath(), $"xcl2_mrpack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {

            var resolved = VersionInfoResolver.Resolve(versionDir, Path.GetFileName(versionDir.TrimEnd('\\', '/')), null);

            var mcVersion = VersionInfoResolver.LooksLikeMcVersion(manifest.McVersion)
                ? manifest.McVersion!.Trim()
                : resolved.McVersion;

            if (!VersionInfoResolver.LooksLikeMcVersion(mcVersion))
                throw new InvalidOperationException(
                    $"无法确定该版本对应的 Minecraft 版本号（解析结果：\"{mcVersion}\"）。" +
                    "导出的 .mrpack 必须在 dependencies.minecraft 里写一个合法的版本号，否则任何启动器都无法导入。" +
                    "请检查该版本目录下的 version json 是否完整。");

            var loader = string.IsNullOrWhiteSpace(manifest.ModLoader) ? resolved.ModLoader : manifest.ModLoader;
            var loaderVersion = string.IsNullOrWhiteSpace(manifest.ModLoaderVersion)
                ? resolved.ModLoaderVersion
                : manifest.ModLoaderVersion;
            if (string.IsNullOrWhiteSpace(loaderVersion)) loaderVersion = null;

            var index = new MrpackIndex
            {
                Name = string.IsNullOrWhiteSpace(manifest.Name) ? "XCL2 整合包" : manifest.Name,
                Dependencies = new MrpackDependencies
                {
                    Minecraft = mcVersion,
                    FabricLoader = string.Equals(loader, "Fabric", StringComparison.OrdinalIgnoreCase)
                        ? loaderVersion : null,
                    QuiltLoader = string.Equals(loader, "Quilt", StringComparison.OrdinalIgnoreCase)
                        ? loaderVersion : null,
                    Forge = string.Equals(loader, "Forge", StringComparison.OrdinalIgnoreCase)
                        ? loaderVersion : null,
                    NeoForge = string.Equals(loader, "NeoForge", StringComparison.OrdinalIgnoreCase)
                        ? loaderVersion : null,
                },
                
                Files = new List<MrpackFile>(),
            };

            progress?.Report(new ProgressInfo("生成索引文件", 0, 1, "modrinth.index.json"));
            var indexJson = JsonSerializer.Serialize(index, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });

            var indexObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(indexJson)!;
            var withHeader = new Dictionary<string, object?>
            {
                ["formatVersion"] = 1,
                ["game"] = "minecraft",
                // versionId 是"整合包自身的版本号"，不是导出时间。过去这里直接塞了
                // ExportedAtUtc（形如 2026-08-04T01:11:54.0000000Z），别的启动器会把这一长串
                // 原样当成整合包版本显示出来。改成基于 MC 版本 + 日期的短版本号。
                ["versionId"] = $"{mcVersion}-{DateTime.Now:yyyyMMdd}",
            };
            foreach (var kv in indexObj) withHeader[kv.Key] = kv.Value;
            File.WriteAllText(Path.Combine(tmpDir, "modrinth.index.json"),
                JsonSerializer.Serialize(withHeader, new JsonSerializerOptions { WriteIndented = true }));

            var overridesDir = Path.Combine(tmpDir, "overrides");
            var sourceFolders = IncludedFolders
                .Select(f => Path.Combine(versionDir, f))
                .Where(Directory.Exists)
                .ToList();
            var totalFiles = sourceFolders.Sum(CountFiles);
            var doneFiles = 0;

            if (sourceFolders.Count == 0)
                throw new InvalidOperationException("该版本下没有找到 mods/config/resourcepacks/shaderpacks 中任何一个文件夹，没有可打包的内容。");

            foreach (var folder in IncludedFolders)
            {
                var src = Path.Combine(versionDir, folder);
                if (!Directory.Exists(src)) continue;
                CopyDirectory(src, Path.Combine(overridesDir, folder), (file, name) =>
                {
                    doneFiles++;
                    progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Packaging_Files", "打包文件"), doneFiles, totalFiles, name));
                });
            }

            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Creating_Archive", "生成压缩包"), totalFiles, totalFiles, Path.GetFileName(destMrpackPath)));
            ArchiveImportSafety.ExportAtomically(destMrpackPath, temporary =>
                ZipFile.CreateFromDirectory(tmpDir, temporary, CompressionLevel.Optimal, includeBaseDirectory: false));
            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Export_Complete", "导出完成"), totalFiles, totalFiles, ""));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* 忽略临时目录清理失败 */ }
        }
    }

    private static int CountFiles(string dir)
    {
        ArchiveImportSafety.CheckLinks(dir);
        var count = Directory.GetFiles(dir).Length;
        foreach (var sub in Directory.GetDirectories(dir))
            count += CountFiles(sub);
        return count;
    }

    /// <summary>
    /// 统一把一个可能"脏"的 ModpackManifest 修正成带真实版本信息的版本。
    ///
    /// 调用方（ModManagerPage）填的是 GameVersion 上的字段，而 GameVersion 又来自
    /// FolderService.ScanVersions —— 那一段过去用的是 `InheritsFrom ?? Id` + 空字符串的
    /// 假实现，独立实例（inheritsFrom 被主动清空）下会把整个版本 ID 当成 MC 版本号。
    /// 这里再兜一层，即使上游还没改到位，导出的包也一定是对的。
    /// </summary>
    private static ModpackManifest NormalizeManifest(string versionDir, ModpackManifest manifest)
    {
        var versionId = Path.GetFileName(versionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var resolved = VersionInfoResolver.Resolve(versionDir, versionId, null);

        return new ModpackManifest
        {
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? versionId : manifest.Name,
            McVersion = VersionInfoResolver.LooksLikeMcVersion(manifest.McVersion)
                ? manifest.McVersion!.Trim()
                : resolved.McVersion,
            ModLoader = string.IsNullOrWhiteSpace(manifest.ModLoader) ? resolved.ModLoader : manifest.ModLoader,
            // 空字符串一律归一成 null：导入方靠 `!= null` 判断"有没有加载器版本信息"，
            // 空串会被当成"有，但是空的"，比缺失更难处理。
            ModLoaderVersion = string.IsNullOrWhiteSpace(manifest.ModLoaderVersion)
                ? resolved.ModLoaderVersion
                : manifest.ModLoaderVersion,
            ExportedAtUtc = manifest.ExportedAtUtc,
        };
    }

    /// <summary>onFile 在每复制完一个文件后回调一次(源文件全路径, 用于展示的文件名)，
    /// 供导出流程汇报真实的"已复制/总数 + 当前文件名"进度；不传则跟原来行为一致，
    /// 只是纯复制不汇报。</summary>
    private static void CopyDirectory(string sourceDir, string destDir, Action<string, string>? onFile = null)
    {
        ArchiveImportSafety.CheckLinks(sourceDir); ArchiveImportSafety.CheckLinks(destDir);
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ArchiveImportSafety.CheckLinks(file);
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            ArchiveImportSafety.CheckLinks(destFile);
            File.Copy(file, destFile, overwrite: true);
            onFile?.Invoke(file, Path.GetFileName(file));
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)), onFile);
    }
}

/// <summary>整合包内附带的元信息，导入方可以据此展示"该包是什么"，不影响实际导入逻辑。</summary>
public class ModpackManifest
{
    public string Name { get; set; } = "";
    public string? McVersion { get; set; }
    public string? ModLoader { get; set; }
    public string? ModLoaderVersion { get; set; }
    public string ExportedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}

/// <summary>Modrinth modrinth.index.json 的结构（只映射导入需要用到的字段）。</summary>
public class MrpackIndex
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dependencies")] public MrpackDependencies? Dependencies { get; set; }
    [JsonPropertyName("files")] public List<MrpackFile> Files { get; set; } = new();
}

public class MrpackDependencies
{
    [JsonPropertyName("minecraft")] public string? Minecraft { get; set; }
    [JsonPropertyName("fabric-loader")] public string? FabricLoader { get; set; }
    /// <summary>Quilt 的依赖键，Modrinth 规范里跟 fabric-loader 平级。原来漏了这一项，
    /// 导致 Quilt 实例导出的 mrpack 完全丢失加载器信息（导入方只知道 MC 版本、不知道要装 Quilt）。</summary>
    [JsonPropertyName("quilt-loader")] public string? QuiltLoader { get; set; }
    [JsonPropertyName("forge")] public string? Forge { get; set; }
    [JsonPropertyName("neoforge")] public string? NeoForge { get; set; }
}

public class MrpackFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = new();
    [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = new();
    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
    [JsonPropertyName("env")] public MrpackFileEnv? Env { get; set; }
}

public class MrpackFileEnv
{
    [JsonPropertyName("client")] public string? Client { get; set; }
    [JsonPropertyName("server")] public string? Server { get; set; }
}

/// <summary>mrpack 导入结果：整合包名字、清单里声明的 MC 版本(可能为空)、下载失败的文件路径列表。</summary>
public record MrpackImportResult(string Name, string? McVersion, List<string> FailedFiles);

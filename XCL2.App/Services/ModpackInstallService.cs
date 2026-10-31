using System.IO;
using System.IO.Compression;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

public class ModpackInstallService
{
    private readonly AppConfig _config;
    private readonly ModpackService _modpackService = new();

    public ModpackInstallService(AppConfig config) => _config = config;

    /// <summary>从整合包清单里读出来的环境要求。</summary>
    public sealed record ModpackRequirements(
        string? Name,
        string? McVersion,
        string? Loader,          // Fabric / Quilt / Forge / NeoForge / null
        string? LoaderVersion);

    /// <summary>
    /// 只读清单、不做任何安装，供界面在弹"起个实例名"的框之前先告诉用户
    /// "该包需要 Fabric 1.20.1"，让用户心里有数。
    /// </summary>
    public ModpackRequirements ReadRequirements(string modpackPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(modpackPath);

            // --- Modrinth .mrpack ---
            var mrIndex = archive.GetEntry("modrinth.index.json");
            if (mrIndex != null)
            {
                using var doc = JsonDocument.Parse(ArchiveImportSafety.ReadManifest(mrIndex));
                var root = doc.RootElement;

                string? name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? mc = null, loader = null, loaderVer = null;

                if (root.TryGetProperty("dependencies", out var deps))
                {
                    if (deps.TryGetProperty("minecraft", out var m)) mc = m.GetString();
                    if (deps.TryGetProperty("fabric-loader", out var f)) { loader = "Fabric"; loaderVer = f.GetString(); }
                    else if (deps.TryGetProperty("quilt-loader", out var q)) { loader = "Quilt"; loaderVer = q.GetString(); }
                    else if (deps.TryGetProperty("neoforge", out var nf)) { loader = "NeoForge"; loaderVer = nf.GetString(); }
                    else if (deps.TryGetProperty("forge", out var fg)) { loader = "Forge"; loaderVer = fg.GetString(); }
                }
                return new ModpackRequirements(name, mc, loader, loaderVer);
            }

            // --- XCL2 .xclpack ---
            var xclManifest = archive.GetEntry("xclpack.json") ?? archive.GetEntry("xcl2-modpack.json");
            if (xclManifest != null)
            {
                var m = JsonSerializer.Deserialize<ModpackManifest>(ArchiveImportSafety.ReadManifest(xclManifest));
                if (m != null)
                    return new ModpackRequirements(m.Name, m.McVersion, NormalizeLoader(m.ModLoader), m.ModLoaderVersion);
            }

            // --- CurseForge manifest.json ---
            var cfManifest = archive.GetEntry("manifest.json");
            if (cfManifest != null)
            {
                using var doc = JsonDocument.Parse(ArchiveImportSafety.ReadManifest(cfManifest));
                var root = doc.RootElement;
                string? name = root.TryGetProperty("name", out var n2) ? n2.GetString() : null;
                string? mc = null, loader = null, loaderVer = null;

                if (root.TryGetProperty("minecraft", out var mcNode))
                {
                    if (mcNode.TryGetProperty("version", out var v)) mc = v.GetString();
                    if (mcNode.TryGetProperty("modLoaders", out var loaders) &&
                        loaders.ValueKind == JsonValueKind.Array && loaders.GetArrayLength() > 0)
                    {
                        // CurseForge 的 id 形如 "forge-47.2.0" / "fabric-0.15.11" / "neoforge-21.1.66"
                        var selected = loaders.EnumerateArray().FirstOrDefault(item => item.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True);
                        if (selected.ValueKind == JsonValueKind.Undefined) selected = loaders[0];
                        var id = selected.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (!string.IsNullOrEmpty(id))
                        {
                            var dash = id.IndexOf('-');
                            var kind = dash > 0 ? id[..dash] : id;
                            loaderVer = dash > 0 ? id[(dash + 1)..] : null;
                            loader = kind.ToLowerInvariant() switch
                            {
                                "fabric" => "Fabric",
                                "quilt" => "Quilt",
                                "neoforge" => "NeoForge",
                                "forge" => "Forge",
                                _ => kind,
                            };
                        }
                    }
                }
                return new ModpackRequirements(name, mc, loader, loaderVer);
            }
        }
        catch
        {
            // 读不出来不算错误——退回"什么都不知道"，让调用方走"请用户手选版本"的路径。
        }

        return new ModpackRequirements(Path.GetFileNameWithoutExtension(modpackPath), null, null, null);
    }

    private static string? NormalizeLoader(string? loader) => string.IsNullOrWhiteSpace(loader) ? null : loader.Trim().ToLowerInvariant() switch
    { "fabric" => "Fabric", "quilt" => "Quilt", "forge" => "Forge", "neoforge" => "NeoForge", "vanilla" => null, _ => loader.Trim() };

    /// <summary>
    /// 把版本目录名规整成合法的文件夹名。用户输入的实例名可能带 \ / : * ? " &lt; &gt; |
    /// 这些 Windows 不允许的字符，直接拿去建目录会抛异常。
    /// </summary>
    public static string SanitizeInstanceName(string raw)
    {
        var name = (raw ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim(' ', '.');   // Windows 目录名不能以空格或点结尾
        if (string.IsNullOrEmpty(name)) name = "modpack";
        return name;
    }

    /// <summary>给一个不跟现有目录冲突的实例名（重名时自动加 (2)(3)…）。</summary>
    public static string MakeUniqueInstanceName(string minecraftDir, string desired)
    {
        var versionsDir = Path.Combine(minecraftDir, "versions");
        var name = SanitizeInstanceName(desired);
        if (!Directory.Exists(Path.Combine(versionsDir, name))) return name;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{name} ({i})";
            if (!Directory.Exists(Path.Combine(versionsDir, candidate))) return candidate;
        }
        return $"{name}_{DateTime.Now:yyyyMMddHHmmss}";
    }

    /// <summary>安装结果。</summary>
    public sealed record InstallResult(
        string InstanceId,
        string InstanceDir,
        string? McVersion,
        string? Loader,
        List<string> FailedFiles);

    /// <summary>
    /// 从零安装：新建实例目录 → 装原版 → 装加载器 → 解整合包内容。
    /// </summary>
    /// <param name="modpackPath">.mrpack / .xclpack / CurseForge .zip</param>
    /// <param name="minecraftDir">目标 .minecraft 根目录</param>
    /// <param name="instanceName">用户自定义的实例名（会被 SanitizeInstanceName 规整并去重）</param>
    /// <param name="javaExeForForge">Forge/NeoForge 安装器需要本地 Java 来跑；
    /// Fabric/Quilt 不需要，传 null 即可。没有 Java 又遇到 Forge 包时会抛出带说明的异常。</param>
    public async Task<InstallResult> InstallToNewInstanceAsync(
        string modpackPath,
        string minecraftDir,
        string instanceName,
        string? javaExeForForge,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct = default)
    {
        var req = ReadRequirements(modpackPath);
        if (string.IsNullOrWhiteSpace(req.McVersion))
            throw new InvalidOperationException("该整合包未声明 Minecraft 版本，无法新建可启动实例；请先创建匹配版本后导入内容。");

        if (req.Loader is not (null or "Fabric" or "Quilt" or "Forge" or "NeoForge"))
            throw new InvalidDataException($"尚不支持清单声明的加载器 {req.Loader}，未创建实例。");
        if ((req.Loader is "Forge" or "NeoForge") && string.IsNullOrWhiteSpace(req.LoaderVersion))
            throw new InvalidDataException($"整合包未提供 {req.Loader} 版本，未创建实例。");
        var instanceId = MakeUniqueInstanceName(minecraftDir, instanceName);
        var instanceDir = ArchiveImportSafety.Resolve(Path.Combine(minecraftDir, "versions"), instanceId);
        ArchiveImportSafety.CheckLinks(modpackPath);
        using (var archive = ZipFile.OpenRead(modpackPath)) ArchiveImportSafety.ValidateArchive(archive, instanceDir);
        await Plugins.PluginInterceptionRegistry.ValidateInstanceAsync(minecraftDir, instanceId, "modpack", ct);

        var createdDir = false;
        try
        {
            ArchiveImportSafety.CreateNewDirectory(instanceDir);
            createdDir = true;

            // ---------- 1. 原版本体 ----------
            // 整合包没写 MC 版本就没法往下走：装不了本体，装了加载器也没意义。
            // 这里明确报错让用户知道是包本身的问题，而不是默默装成一个跑不起来的空实例。
            if (string.IsNullOrWhiteSpace(req.McVersion))
                throw new InvalidOperationException(
                    "该整合包的清单里没有写它需要的 Minecraft 版本，无法自动从零安装。\n" +
                    "可以改用「安装到已有实例」的方式，自己先建好对应版本再导入。");

            progress?.Report(new ProgressInfo("正在下载游戏本体", 0, 1, req.McVersion!));

            using var downloader = DownloadService.CreateFromConfig(_config);
            var manifest = await downloader.GetVersionManifestAsync(ct);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == req.McVersion)
                ?? throw new InvalidOperationException(
                    $"在 Mojang 版本清单里找不到 Minecraft {req.McVersion}，无法安装该整合包。\n" +
                    "可能是整合包针对的是快照/预览版，或者版本号写得不规范。");

            await downloader.InstallVersionAsync(minecraftDir, entry, progress, ct);

            // ---------- 2. 加载器 ----------
            // 注意：加载器安装出来的是它自己的版本目录（fabric-loader-x-y 之类），
            // 跟我们该自定义名字的实例目录不是一个。装完之后要把加载器产出的
            // version json / jar 搬进我们的实例目录，并把 id 改成实例名，
            // 这样版本列表里显示的就是用户起的名字，而不是 fabric-loader-0.15.11-1.20.1。
            string? producedVersionId = null;
            if (!string.IsNullOrWhiteSpace(req.Loader))
            {
                progress?.Report(new ProgressInfo($"正在安装 {req.Loader}", 0, 1, req.LoaderVersion ?? ""));

                using var loaderService = new ClientLoaderInstallService(_config);

                switch (req.Loader)
                {
                    case "Fabric":
                    {
                        var lv = req.LoaderVersion;
                        if (string.IsNullOrWhiteSpace(lv))
                        {
                            var builds = await loaderService.GetFabricLoaderVersionsAsync(req.McVersion, ct);
                            lv = builds.FirstOrDefault(b => b.IsRecommended)?.DisplayVersion
                                 ?? builds.FirstOrDefault()?.DisplayVersion;
                        }
                        producedVersionId = await loaderService.InstallFabricClientAsync(
                            minecraftDir, req.McVersion!, lv!, progress, ct);
                        break;
                    }
                    case "Quilt":
                    {
                        var lv = req.LoaderVersion;
                        if (string.IsNullOrWhiteSpace(lv))
                        {
                            var builds = await loaderService.GetQuiltLoaderVersionsAsync(req.McVersion, ct);
                            lv = builds.FirstOrDefault(b => b.IsRecommended)?.DisplayVersion
                                 ?? builds.FirstOrDefault()?.DisplayVersion;
                        }
                        producedVersionId = await loaderService.InstallQuiltClientAsync(
                            minecraftDir, req.McVersion!, lv!, progress, ct);
                        break;
                    }
                    case "Forge":
                    case "NeoForge":
                    {
                        if (string.IsNullOrWhiteSpace(javaExeForForge) || !File.Exists(javaExeForForge))
                            throw new InvalidOperationException(
                                $"该整合包需要 {req.Loader}，而 {req.Loader} 的安装器必须用本地 Java 运行。\n" +
                                "启动器没有找到可用的 Java，请先到「设置 - Java」里配置一个，或让启动器自动下载一份便携版 Java 后再试。");

                        var full = req.Loader == "Forge"
                            ? $"{req.McVersion}-{req.LoaderVersion}"
                            : req.LoaderVersion!;
                        var coreType = req.Loader == "Forge" ? ServerCoreType.Forge : ServerCoreType.NeoForge;

                        producedVersionId = await loaderService.InstallForgeOrNeoForgeClientAsync(
                            minecraftDir, coreType, full, javaExeForForge!, progress, ct);
                        break;
                    }
                }
            }

            // ---------- 3. 把加载器产出的实例改名/搬进我们该自定义实例目录 ----------
            if (!string.IsNullOrEmpty(producedVersionId) &&
                !string.Equals(producedVersionId, instanceId, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new ProgressInfo("正在整理实例文件", 0, 1, instanceId));
                MergeProducedVersionInto(minecraftDir, producedVersionId!, instanceId, instanceDir);
            }
            else if (string.IsNullOrEmpty(producedVersionId))
            {
                // 纯原版整合包（没有加载器）：把原版目录的 json/jar 复制成实例自己的一份，
                // 让该实例独立可启动，而不是空目录。
                MergeProducedVersionInto(minecraftDir, req.McVersion!, instanceId, instanceDir, copyOnly: true);
            }

            if (!File.Exists(Path.Combine(instanceDir, instanceId + ".json")))
                throw new InvalidDataException("安装器未生成实例版本描述，未将整合包标为可启动。");

            // ---------- 4. 解整合包内容 ----------
            progress?.Report(new ProgressInfo("正在导入整合包内容", 0, 1, Path.GetFileName(modpackPath)));

            var failed = new List<string>();
            if (ModpackService.IsMrpack(modpackPath))
            {
                var r = await _modpackService.ImportMrpackAsync(modpackPath, instanceDir,
                    new Progress<string>(msg => progress?.Report(new ProgressInfo(msg, 0, 1, ""))), ct);
                failed.AddRange(r.FailedFiles);
            }
            else if (ModpackService.IsCurseForgePack(modpackPath))
            {
                var r = await _modpackService.ImportCurseForgeAsync(modpackPath, instanceDir,
                    new Progress<string>(msg => progress?.Report(new ProgressInfo(msg, 0, 1, ""))), ct);
                failed.AddRange(r.FailedFiles);
            }
            else
            {
                _modpackService.Import(modpackPath, instanceDir,
                    new Progress<string>(msg => progress?.Report(new ProgressInfo(msg, 0, 1, ""))));
            }

            // Content overrides can replace version metadata, so validate after importing as well as provisioning.
            var ready = VersionInheritanceResolver.Load(minecraftDir, instanceId, FolderService.ResolveVersionJson);
            var client = ready.ClientLayer;
            var jarName = string.IsNullOrEmpty(client.Detail.Id) ? client.VersionId : client.Detail.Id;
            var clientJar = LauncherService.ResolveVersionFile(client.Directory, jarName, "jar");
            if (string.IsNullOrWhiteSpace(ready.Effective.MainClass) || clientJar == null || new FileInfo(clientJar).Length == 0)
                throw new InvalidDataException("整合包导入后的版本描述或客户端主程序不完整，未将其标为可启动。");
            Plugins.PluginHostBridge.Current?.NotifyInstanceCreated(minecraftDir, instanceId);
            return new InstallResult(instanceId, instanceDir, req.McVersion, req.Loader, failed);
        }
        catch
        {
            // 失败就把半成品目录清掉：留着只会让用户在版本列表里看到一个点了就崩的实例。
            if (createdDir)
            {
                try { if (Directory.Exists(instanceDir)) Directory.Delete(instanceDir, recursive: true); }
                catch { /* 清理失败不掩盖原始异常 */ }
            }
            throw;
        }
    }

    /// <summary>
    /// 把加载器/原版安装产出的版本目录，搬（或复制）成我们该自定义名字的实例。
    ///
    /// 做法：把源目录里的 .json / .jar 拷进目标目录并改成目标名，同时改写 json 内部的 "id" 字段
    /// （version json 的 id 必须跟文件名/目录名一致，否则启动时找不到主类和库）。
    /// 其余文件（natives 等）原样复制。
    /// </summary>
    private static void MergeProducedVersionInto(string minecraftDir, string sourceVersionId,
        string targetVersionId, string targetDir, bool copyOnly = false)
    {
        var sourceDir = Path.Combine(minecraftDir, "versions", sourceVersionId);
        if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException("安装器没有生成预期版本目录，未将整合包标为可启动。");

        ArchiveImportSafety.CheckLinks(sourceDir); ArchiveImportSafety.CheckLinks(targetDir);
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ArchiveImportSafety.CheckLinks(file);
            var ext = Path.GetExtension(file);
            var isJson = string.Equals(Path.GetFileName(file), sourceVersionId + ".json", StringComparison.OrdinalIgnoreCase);
            var isJar = string.Equals(Path.GetFileName(file), sourceVersionId + ".jar", StringComparison.OrdinalIgnoreCase);

            var destName = (isJson || isJar) ? targetVersionId + ext : Path.GetFileName(file);
            var dest = Path.Combine(targetDir, destName);

            if (isJson)
            {
                // 改写 id 字段：WPF 侧启动逻辑按 id 找 jar，id 跟目录名对不上就启动不了。
                try
                {
                    var text = File.ReadAllText(file);
                    var node = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
                    if (node != null)
                    {
                        var rebuilt = new Dictionary<string, object?>();
                        foreach (var kv in node)
                            rebuilt[kv.Key] = kv.Key == "id" ? targetVersionId : (object)kv.Value;
                        if (!rebuilt.ContainsKey("id")) rebuilt["id"] = targetVersionId;

                        File.WriteAllText(dest, JsonSerializer.Serialize(rebuilt,
                            new JsonSerializerOptions { WriteIndented = true }));
                        continue;
                    }
                }
                catch (JsonException ex) { throw new InvalidDataException("安装器生成的版本描述无效，未将整合包标为可启动。", ex); }
            }

            File.Copy(file, dest, overwrite: true);
        }

        // 子目录（natives 之类）原样复制
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSub = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirRecursive(dir, destSub);
        }

        // Loader directories may have existed before this task or be referenced by other instances.
        // Keep the source; importing a pack must never remove an existing installed version.
    }

    private static void CopyDirRecursive(string source, string dest)
    {
        ArchiveImportSafety.CheckLinks(source); ArchiveImportSafety.CheckLinks(dest);
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(source))
        {
            ArchiveImportSafety.CheckLinks(f);
            File.Copy(f, ArchiveImportSafety.Resolve(dest, Path.GetFileName(f)), overwrite: true);
        }
        foreach (var d in Directory.GetDirectories(source))
            CopyDirRecursive(d, Path.Combine(dest, Path.GetFileName(d)));
    }
}

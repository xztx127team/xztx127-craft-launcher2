using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>一个可更新的候选文件（模组或资源包，判定逻辑完全一样，只是扫描的文件夹和后缀不同）。</summary>
public class UpdateCandidate
{
    public string CurrentFilePath { get; set; } = "";
    public string CurrentSha1 { get; set; } = "";
    public string CurrentFileName => Path.GetFileName(CurrentFilePath);
    public bool IsCurrentlyDisabled { get; set; }
    public string DisplayName { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string CurrentVersionName { get; set; } = "";
    public string NewVersionName { get; set; } = "";
    public string NewVersionId { get; set; } = "";
    public string NewFileName { get; set; } = "";
    public string NewDownloadUrl { get; set; } = "";
    public string NewSha1 { get; set; } = "";
    public long NewFileSize { get; set; }
    public string UpdateNotes { get; set; } = "";
    public string CompatibilitySummary { get; set; } = "";
    public string DependencySummary { get; set; } = "";

    /// <summary>用户在确认对话框里是否勾选了这一项参与本次批量升级。</summary>
    public bool Selected { get; set; } = true;
}

public class BatchUpdateService : IDisposable
{
    private const string ModrinthBaseUrl = "https://api.modrinth.com/v2";
    private readonly HttpClient _http;
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);

    public BatchUpdateService()
    {
        _http = LauncherHttpClientFactory.Create(TimeSpan.FromMinutes(3));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XCL2-Launcher", "1.0"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/xztx127-craft/xcl2)"));
    }

    private static string ComputeSha1(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// 扫描 folderPath 下所有匹配后缀的文件（mods 传 "*.jar"，resourcepacks 传 "*.zip"），
    /// 逐个查 Modrinth 是否有更新。已禁用的文件（.disabled 后缀）也会一并检查——用户很可能
    /// 就是先禁用了旧版本等着换新版本，不应该因为禁用状态被跳过。
    /// 单个文件查询失败（网络问题/该文件根本不是 Modrinth 上的资源）不会中断整体扫描，
    /// 直接跳过继续查下一个。
    /// </summary>
    public async Task<List<UpdateCandidate>> CheckAsync(string folderPath, string filePattern,
        string mcVersion, string? loader, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var result = new List<UpdateCandidate>();
        if (!Directory.Exists(folderPath)) return result;

        var files = Directory.GetFiles(folderPath, filePattern)
            .Concat(Directory.GetFiles(folderPath, filePattern + ".disabled"))
            .ToList();

        var checkedCount = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            checkedCount++;
            progress?.Report(new ProgressInfo("正在检查更新", checkedCount, files.Count, Path.GetFileName(file)));

            try
            {
                var candidate = await CheckOneAsync(file, mcVersion, loader, ct);
                if (candidate != null) result.Add(candidate);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // 单个文件识别失败（网络抖动/不是 Modrinth 资源/哈希查无结果）静默跳过，
                // 不能因为一个文件查询异常就让整批检查失败。
            }
        }

        return result;
    }

    private async Task<UpdateCandidate?> CheckOneAsync(string filePath, string mcVersion, string? loader, CancellationToken ct)
    {
        var isDisabled = filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var sha1 = await Task.Run(() => ComputeSha1(filePath), ct);

        // 第一步：哈希反查"这是 Modrinth 上哪个项目的哪个版本"。查不到说明该文件不是
        // （或不确定是）Modrinth 上的资源，直接放弃，不当作错误处理。
        using var lookupResp = await _http.GetAsync($"{ModrinthBaseUrl}/version_file/{sha1}?algorithm=sha1", ct);
        if (!lookupResp.IsSuccessStatusCode) return null;
        var lookupJson = await lookupResp.Content.ReadAsStringAsync(ct);
        using var lookupDoc = JsonDocument.Parse(lookupJson);
        var root = lookupDoc.RootElement;

        var projectId = root.GetProperty("project_id").GetString() ?? "";
        var currentVersionNumber = root.TryGetProperty("version_number", out var vn) ? vn.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(projectId)) return null;

        // 第二步：查该项目在目标游戏版本 + 加载器下，最新的一个版本是什么。
        var isMod = (isDisabled ? filePath[..^".disabled".Length] : filePath).EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        var effectiveLoader = ModUpdateSelectionPolicy.ResolveLoader(root, loader, isMod);
        if (string.IsNullOrWhiteSpace(mcVersion) || isMod && string.IsNullOrWhiteSpace(effectiveLoader)) return null;
        var loaderFilter = string.IsNullOrWhiteSpace(effectiveLoader) ? "" : "&loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { effectiveLoader }));
        var versionsUrl = $"{ModrinthBaseUrl}/project/{Uri.EscapeDataString(projectId)}/version?game_versions=" +
            Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mcVersion.Trim() })) + loaderFilter;
        using var versionsResp = await _http.GetAsync(versionsUrl, ct);
        if (!versionsResp.IsSuccessStatusCode) return null;
        var versionsJson = await versionsResp.Content.ReadAsStringAsync(ct);
        using var versionsDoc = JsonDocument.Parse(versionsJson);
        var versionsArr = versionsDoc.RootElement;
        if (!ModUpdateSelectionPolicy.TrySelect(root, versionsArr, mcVersion, loader, isMod, out var latest)) return null;
        var latestVersionId = latest.GetProperty("id").GetString() ?? "";
        var latestVersionNumber = latest.TryGetProperty("version_number", out var lvn) ? lvn.GetString() ?? "" : "";
        var currentVersionId = root.TryGetProperty("id", out var vid) ? vid.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(latestVersionId) || latestVersionId == currentVersionId) return null; // 已经是最新，没有更新

        var files = latest.GetProperty("files");
        JsonElement primaryFile = files[0];
        foreach (var f in files.EnumerateArray())
        {
            if (f.TryGetProperty("primary", out var isPrimary) && isPrimary.GetBoolean()) { primaryFile = f; break; }
        }

        using var projectResp = await _http.GetAsync($"{ModrinthBaseUrl}/project/{projectId}", ct);
        string displayName = projectId;
        if (projectResp.IsSuccessStatusCode)
        {
            using var projDoc = JsonDocument.Parse(await projectResp.Content.ReadAsStringAsync(ct));
            if (projDoc.RootElement.TryGetProperty("title", out var titleEl))
                displayName = titleEl.GetString() ?? projectId;
        }

        return new UpdateCandidate
        {
            CurrentFilePath = filePath,
            CurrentSha1 = sha1,
            IsCurrentlyDisabled = isDisabled,
            DisplayName = displayName,
            ProjectId = projectId,
            CurrentVersionName = currentVersionNumber,
            NewVersionName = latestVersionNumber,
            NewVersionId = latestVersionId,
            NewFileName = primaryFile.GetProperty("filename").GetString() ?? $"{projectId}.jar",
            NewDownloadUrl = primaryFile.GetProperty("url").GetString() ?? "",
            NewSha1 = primaryFile.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("sha1", out var h1) ? h1.GetString() ?? "" : "",
            NewFileSize = primaryFile.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0,
            UpdateNotes = latest.TryGetProperty("changelog", out var changeEl) && changeEl.ValueKind == JsonValueKind.String ? LimitPreview(changeEl.GetString() ?? "", 16000) : "此版本未提供更新说明。",
            CompatibilitySummary = "Minecraft：" + ArrayPreview(latest, "game_versions") + "；加载器：" + ArrayPreview(latest, "loaders"),
            DependencySummary = DependencyPreview(latest),
        };
    }

    private static string LimitPreview(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "\n（更新说明过长，已截取）";
    private static string ArrayPreview(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
        ? string.Join(" / ", value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Take(24).Select(v => v.GetString())) : "未提供";
    private static string DependencyPreview(JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Array) return "未提供依赖信息。";
        var rows = new List<string>();
        foreach (var dep in deps.EnumerateArray().Take(60))
        {
            var kind = dep.TryGetProperty("dependency_type", out var type) ? type.GetString() : "unknown";
            var label = kind switch { "required" => "必需", "optional" => "可选", "incompatible" => "不兼容", "embedded" => "已内置", _ => "其他" };
            var project = dep.TryGetProperty("project_id", out var projectId) && projectId.ValueKind == JsonValueKind.String ? projectId.GetString() : null;
            var version = dep.TryGetProperty("version_id", out var versionId) && versionId.ValueKind == JsonValueKind.String ? versionId.GetString() : null;
            var file = dep.TryGetProperty("file_name", out var fileName) && fileName.ValueKind == JsonValueKind.String ? fileName.GetString() : null;
            rows.Add(label + "：" + (project ?? file ?? "未标注项目") + (version == null ? "" : " · 版本 " + version));
        }
        return rows.Count == 0 ? "该版本未声明依赖。" : string.Join("\n", rows) + "\n仅展示声明，不会自动下载或启用依赖。";
    }

    /// <summary>
    /// 对用户勾选确认过的候选逐个执行升级：下载新文件、删除旧文件、保持原有的启用/禁用状态
    /// （旧文件是禁用状态，新文件下载后也加上 .disabled 后缀，不会因为升级把用户特意禁用的
    /// mod 意外唤醒）。单个下载失败不影响其它文件继续升级，失败的会在返回结果里标出来，
    /// 由调用方决定怎么提示用户（成功了几个/失败了哪几个）。
    /// </summary>
    public Task<(List<string> succeeded, List<(string name, string error)> failed)> ApplyAsync(
        string folderPath, List<UpdateCandidate> candidates,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        => Task.Run(() => ApplyCoreAsync(folderPath, candidates, progress, ct), ct);

    private async Task<(List<string> succeeded, List<(string name, string error)> failed)> ApplyCoreAsync(
        string folderPath, List<UpdateCandidate> candidates, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        await UpdateGate.WaitAsync(ct);
        try
        {
            var succeeded = new List<string>();
            var failed = new List<(string, string)>();

            var selected = candidates.Where(c => c.Selected).ToList();
            if (selected.Count == 0) return (succeeded, failed);
            folderPath = Path.GetFullPath(folderPath);
            // 所有目标先校验；绝不接受远程元数据把文件写出所选 mods / resourcepacks 目录。
            var names = new List<string>();
            foreach (var candidate in selected)
            {
                var old = ModUpdateSnapshotStore.ResolveFile(folderPath, candidate.CurrentFileName);
                if (!string.Equals(old, Path.GetFullPath(candidate.CurrentFilePath), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("更新候选不属于所选目录。");
                if (!File.Exists(old) || !string.IsNullOrEmpty(candidate.CurrentSha1) &&
                    !string.Equals(ComputeSha1(old), candidate.CurrentSha1, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("检查更新后原文件已改变或被移除，请重新检查更新。");
                ModUpdateSnapshotStore.ResolveFile(folderPath, candidate.NewFileName);
                var finalName = candidate.NewFileName + (candidate.IsCurrentlyDisabled ? ".disabled" : "");
                ModUpdateSnapshotStore.ResolveFile(folderPath, finalName);
                names.Add(candidate.CurrentFileName); names.Add(finalName);
            }
            var cfg = ConfigService.Active?.Config;
            var store = new ModUpdateSnapshotStore(Path.Combine(App.DataDir, "mod_update_snapshots"));
            ModUpdateSnapshot? snapshot = null;
            if (cfg?.SnapshotBeforeModUpdates != false)
                snapshot = await Task.Run(() => store.Create(folderPath, names,
                    Math.Clamp(cfg?.ModUpdateSnapshotRetentionCount ?? 10, 1, 100),
                    Math.Clamp(cfg?.ModUpdateSnapshotMaxMb ?? 1024, 64, 16384) * 1048576L, ct), ct);
            try
            {
                var done = 0;
                foreach (var c in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    done++;
                    progress?.Report(new ProgressInfo("正在升级", done, selected.Count, c.DisplayName));
                    var staging = Path.Combine(folderPath, ".xcl2-update-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        var finalName = c.IsCurrentlyDisabled ? c.NewFileName + ".disabled" : c.NewFileName;
                        var finalPath = ModUpdateSnapshotStore.ResolveFile(folderPath, finalName);
                        var oldPath = ModUpdateSnapshotStore.ResolveFile(folderPath, c.CurrentFileName);
                        string? Before(string name, string path) => snapshot == null ? ModUpdateSnapshotStore.Hash(path)
                            : snapshot.Files.Single(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase)).BeforeHash;
                        var oldHash = Before(c.CurrentFileName, oldPath); var targetHash = Before(finalName, finalPath);
                        if (!string.IsNullOrEmpty(c.CurrentSha1) &&
                            !string.Equals(ComputeSha1(oldPath), c.CurrentSha1, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("检查更新后原文件已改变，请重新检查更新。");
                        Directory.CreateDirectory(staging);
                        var tempPath = Path.Combine(staging, "download.bin");
                        await DownloadEndpoints.DownloadFileWithFallbackAsync(_http, c.NewDownloadUrl, DownloadEndpoints.PreferMirror,
                            tempPath, "更新文件下载失败", ct: ct, expectedSha1: c.NewSha1,
                            expectedSize: c.NewFileSize > 0 ? c.NewFileSize : null);
                        var newHash = ModUpdateSnapshotStore.Hash(tempPath);
                        ct.ThrowIfCancellationRequested();
                        if (ModUpdateSnapshotStore.Hash(oldPath) != oldHash || ModUpdateSnapshotStore.Hash(finalPath) != targetHash)
                            throw new IOException("下载期间原文件有后续修改，已停止替换。");
                        var renamed = !string.Equals(oldPath, finalPath, StringComparison.OrdinalIgnoreCase);
                        var displaced = Path.Combine(staging, "target-before.bin");
                        if (renamed && targetHash != null)
                        {
                            File.Copy(finalPath, displaced);
                            if (ModUpdateSnapshotStore.Hash(displaced) != targetHash) throw new IOException("目标文件在备份期间发生变化。");
                        }

                        // 新旧文件名可能不同（mod 作者改过命名规则），新文件落地成功后才删旧文件，
                        // 避免"新文件没下完/校验失败"却已经把旧文件删了、两头落空的中间状态。
                        File.Move(tempPath, finalPath, true);
                        if (File.Exists(oldPath) && renamed)
                        {
                            try
                            {
                                if (snapshot != null) store.RecordAfter(snapshot, finalName, newHash);
                                if (ModUpdateSnapshotStore.Hash(oldPath) != oldHash) throw new IOException("原文件在更新期间变化，保留原文件和快照供核对。");
                                File.Delete(oldPath);
                                if (snapshot != null) store.RecordAfter(snapshot, c.CurrentFileName, null);
                            }
                            catch
                            {
                                // 旧文件未能移除时撤回本次新文件，避免留下同一个模组的两个版本。
                                if (File.Exists(oldPath) && ModUpdateSnapshotStore.Hash(finalPath) == newHash)
                                {
                                    if (targetHash == null) File.Delete(finalPath);
                                    else File.Move(displaced, finalPath, true);
                                    if (snapshot != null) store.RecordAfter(snapshot, finalName, targetHash);
                                }
                                throw;
                            }
                        }
                        else if (snapshot != null) store.RecordAfter(snapshot, finalName, newHash);

                        succeeded.Add(c.DisplayName);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        failed.Add((c.DisplayName, ex.Message));
                    }
                    finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
                }
            }
            finally
            {
                if (snapshot != null)
                {
                    await Task.Run(() => store.Complete(snapshot));
                    ReminderCenterService.Publish("mod-snapshot-" + snapshot.Id, "更新前的文件快照已保存",
                        "模组 / 资源包更新前的原文件已保存。确认游戏运行正常后可按保留规则清理；需要时可查看并恢复。",
                        "查看快照", () =>
                        {
                            (System.Windows.Application.Current?.MainWindow as XCL2.App.Views.MainWindow)?.OpenModUpdateSnapshots();
                            return Task.CompletedTask;
                        });
                }
            }
            return (succeeded, failed);
        }
        finally { UpdateGate.Release(); }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

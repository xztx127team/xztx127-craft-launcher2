using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

public class InstanceIntegrityService
{
    public class IntegrityCheckResult
    {
        /// <summary>version json 本身缺失/解析失败——这种情况下面所有其它检查都无意义，直接判定不完整。</summary>
        public bool VersionJsonMissing { get; set; }
        /// <summary>client jar 缺失或 0 字节。按统一继承解析器找到实际 client 所属层，支持多层继承与 jar 重定向。</summary>
        public bool ClientJarMissing { get; set; }
        /// <summary>复用 LauncherService.CheckMissingLibraries 的结果，原样透传，不重复实现一遍。</summary>
        public List<string> MissingLibraries { get; set; } = new();
        /// <summary>assets 缺失的描述文本；Simple 模式下最多是一条"数量对不上"的汇总，Strict 模式下可能是很多条。</summary>
        public List<string> MissingAssets { get; set; } = new();

        public bool HasProblems => VersionJsonMissing || ClientJarMissing
            || MissingLibraries.Count > 0 || MissingAssets.Count > 0;

        /// <summary>解析出来的 VersionDetail，供调用方后续"继续启动并补全"时直接复用，不用重新读一次 json。</summary>
        public VersionDetail? Detail { get; set; }
        public VersionDetail? EffectiveDetail { get; set; }
        public VersionDetail? ClientDetail { get; set; }
        public string? ClientVersionId { get; set; }
    }

    private readonly LauncherService _launcherService;

    public InstanceIntegrityService(LauncherService launcherService)
    {
        _launcherService = launcherService;
    }

    public IntegrityCheckResult Check(string minecraftDir, string versionId, IntegrityCheckMode mode, CancellationToken ct = default)
        => Check(minecraftDir, versionId, mode, ct, null);

    public IntegrityCheckResult Check(string minecraftDir, string versionId, IntegrityCheckMode mode, CancellationToken ct,
        string? architecture)
    {
        ct.ThrowIfCancellationRequested();
        var result = new IntegrityCheckResult();
        VersionInheritanceResolver.Resolution inheritance;
        try
        {
            inheritance = VersionInheritanceResolver.Load(minecraftDir, versionId, FolderService.ResolveVersionJson, architecture);
        }
        catch
        {
            result.VersionJsonMissing = true;
            return result;
        }
        var detail = inheritance.Layers[^1].Detail;
        if (string.IsNullOrEmpty(detail.Id)) detail.Id = versionId;
        result.Detail = detail; // Keep the selected layer for callers which inspect inheritsFrom.
        result.EffectiveDetail = inheritance.Effective;
        if (string.IsNullOrWhiteSpace(inheritance.Effective.MainClass))
        {
            result.VersionJsonMissing = true;
            return result;
        }
        var client = inheritance.ClientLayer;
        result.ClientDetail = client.Detail;
        result.ClientVersionId = client.VersionId;
        var jarBaseName = string.IsNullOrEmpty(client.Detail.Id) ? client.VersionId : client.Detail.Id;
        var jar = LauncherService.ResolveVersionFile(client.Directory, jarBaseName, "jar");
        try { result.ClientJarMissing = jar == null || new FileInfo(jar).Length == 0; }
        catch (IOException) { result.ClientJarMissing = true; }
        catch (UnauthorizedAccessException) { result.ClientJarMissing = true; }
        ct.ThrowIfCancellationRequested();

        // libraries + natives：直接复用已有实现，不重复一遍同样的解析逻辑。
        try
        {
            result.MissingLibraries = _launcherService.CheckMissingLibraries(minecraftDir, versionId, architecture);
        }
        catch
        {
            // 复用的方法内部已经有自己的兜底(读取失败就返回空列表)，这里理论上不会抛，多包一层只是防御。
        }

        // assets：原来完全没有人检查过，这里是新增的部分。
        CheckAssets(minecraftDir, inheritance.Effective, mode, result.MissingAssets, ct);

        return result;
    }

    private static void CheckAssets(string minecraftDir, VersionDetail detail, IntegrityCheckMode mode, List<string> missingAssets, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (detail.AssetIndex == null) return; // 极少数远古版本没有独立资源索引，没有可查的东西

        var indexPath = Path.Combine(minecraftDir, "assets", "indexes", $"{detail.AssetIndex.Id}.json");
        if (!File.Exists(indexPath))
        {
            missingAssets.Add($"资源索引文件缺失: assets/indexes/{detail.AssetIndex.Id}.json");
            return;
        }

        AssetIndexFile index;
        try
        {
            index = JsonSerializer.Deserialize<AssetIndexFile>(File.ReadAllText(indexPath)) ?? new AssetIndexFile();
        }
        catch (Exception ex)
        {
            missingAssets.Add($"资源索引文件已损坏，无法解析: {indexPath}（{ex.Message}）");
            return;
        }

        var objectsDir = Path.Combine(minecraftDir, "assets", "objects");

        if (mode == IntegrityCheckMode.Strict)
        {
            // 严格模式：逐个核对每一个 object 是否存在、大小是否匹配。数量可能有几千个，
            // 调用方（UI 线程）应当在后台线程里跑该方法，避免卡界面。
            foreach (var (name, obj) in index.Objects)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(obj.Hash) || obj.Hash.Length < 2) continue;
                var path = Path.Combine(objectsDir, obj.Hash[..2], obj.Hash);
                if (!File.Exists(path))
                {
                    missingAssets.Add($"资源文件缺失: {name} ({obj.Hash})");
                }
                else if (new FileInfo(path).Length != obj.Size)
                {
                    missingAssets.Add($"资源文件大小不匹配(可能是半成品): {name} ({obj.Hash})");
                }
            }
        }
        else
        {
            // 简单模式：只按"实际已存在的 object 文件数量"和"索引里应该有的数量"做粗略比对，
            // 不逐个核对哈希/大小——几千个小文件的存在性检查本身也有一定 IO 开销，
            // 用总数对不上来快速判断"大概率没下完"，而不追求发现"个别文件损坏但数量凑巧还对"
            // 这种边缘情况（这种情况留给用户手动"重新安装"或以后切到 Strict 模式再查）。
            var expected = index.Objects.Count;
            if (expected == 0) return;

            var existing = 0;
            if (Directory.Exists(objectsDir))
            {
                foreach (var (_, obj) in index.Objects)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(obj.Hash) || obj.Hash.Length < 2) continue;
                    if (File.Exists(Path.Combine(objectsDir, obj.Hash[..2], obj.Hash))) existing++;
                }
            }

            if (existing < expected)
            {
                missingAssets.Add($"资源文件数量不足：索引要求 {expected} 个，实际找到 {existing} 个，" +
                    $"缺少 {expected - existing} 个（简单模式仅按数量粗查，不保证已存在的文件本身没有损坏）。");
            }
        }
    }

}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed record CachedModUpdateMetadata(DateTime CheckedUtc, IReadOnlyList<UpdateCandidate> Candidates);

/// <summary>Exact-version ignores are explicit user preferences; metadata cache is bounded to this session.</summary>
public static class ModUpdatePreferenceService
{
    private static readonly Dictionary<string, CachedModUpdateMetadata> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static string Scope(string gameDirectory) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(gameDirectory).TrimEnd('/', '\\').ToUpperInvariant())));
    private static string VersionKey(UpdateCandidate candidate) => !string.IsNullOrWhiteSpace(candidate.NewVersionId) ? candidate.NewVersionId : "sha1:" + candidate.NewSha1;
    public static bool IsIgnored(AppConfig cfg, string directory, UpdateCandidate candidate)
        => (cfg.Ux50IgnoredModUpdateVersions ?? new()).Any(rule => rule.Scope == Scope(directory) && rule.ProjectId == candidate.ProjectId && rule.VersionId == VersionKey(candidate));
    public static void Ignore(AppConfig cfg, string directory, UpdateCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.ProjectId) || string.IsNullOrWhiteSpace(candidate.NewVersionId) && string.IsNullOrWhiteSpace(candidate.NewSha1)) return;
        cfg.Ux50IgnoredModUpdateVersions ??= new();
        if (IsIgnored(cfg, directory, candidate)) return;
        if (cfg.Ux50IgnoredModUpdateVersions.Count >= 1000) throw new InvalidOperationException("忽略规则已达 1000 条，请先恢复不再需要的规则。");
        cfg.Ux50IgnoredModUpdateVersions.Add(new() { Scope = Scope(directory), ProjectId = candidate.ProjectId, VersionId = VersionKey(candidate), DisplayName = candidate.DisplayName, VersionName = candidate.NewVersionName });
    }
    public static IReadOnlyList<IgnoredModUpdateVersion> Ignored(AppConfig cfg, string directory)
        => (cfg.Ux50IgnoredModUpdateVersions ?? new()).Where(rule => rule.Scope == Scope(directory)).ToArray();
    public static void Remember(string directory, IEnumerable<UpdateCandidate> candidates)
    {
        var key = Scope(directory);
        if (Cache.Count >= 20 && !Cache.ContainsKey(key)) Cache.Remove(Cache.OrderBy(entry => entry.Value.CheckedUtc).First().Key);
        Cache[key] = new(DateTime.UtcNow, candidates.Take(1000).ToArray());
    }
    public static CachedModUpdateMetadata? GetCached(string directory) => Cache.TryGetValue(Scope(directory), out var value) ? value : null;
    public static string CacheLabel(CachedModUpdateMetadata value)
    {
        var age = DateTime.UtcNow - value.CheckedUtc;
        var elapsed = age.TotalMinutes < 1 ? "刚刚" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} 分钟前" : $"{(int)age.TotalHours} 小时前";
        return $"本次会话缓存 · 检查于 {value.CheckedUtc.ToLocalTime():MM-dd HH:mm}（{elapsed}）";
    }
}

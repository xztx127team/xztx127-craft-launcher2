using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Version 2 cache. Never imports provider-wide v1 omissions. Persist only successful repairs.</summary>
public sealed class AiModelParameterMemory
{
    private sealed record Entry(string ModelScope, DateTime VerifiedUtc, List<AiParameterRule> Rules);
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly object _gate;
    private Dictionary<string, Entry> _entries = new();
    public string? PersistenceWarning { get; private set; }
    public AiModelParameterMemory(string directory)
    {
        _path = Path.Combine(directory, "ai", "model-parameter-memory-v2.json");
        _gate = Gates.GetOrAdd(Path.GetFullPath(_path), _ => new object());
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string ModelScope(AiRequestTarget target) => Hash(JsonSerializer.Serialize(new[] {
        target.ProviderId, AiEndpointRouting.NormalizeBaseUrl(target.BaseUrl), target.ModelId, target.Protocol == AiApiProtocol.Anthropic ? "messages" : "chat/completions" }));
    public static string Scope(AiRequestTarget target, AiReasoningLevel level, AiModelRequestProfile profile, bool stream, bool tools)
        => Hash(JsonSerializer.Serialize(new object[] { ModelScope(target), level, profile, stream, tools }));
    public IReadOnlyList<AiParameterRule> Get(string scope)
    {
        lock (_gate) { Load(); return _entries.TryGetValue(scope, out var e) ? e.Rules.ToArray() : Array.Empty<AiParameterRule>(); }
    }
    public void Remember(string scope, string modelScope, IEnumerable<AiParameterRule> rules)
    {
        lock (_gate)
        {
            Load();
            var list = rules.ToList();
            if (AiModelParameterPolicy.Validate(new() { Rules = list }) != null) return;
            _entries[scope] = new(modelScope, DateTime.UtcNow, list);
            while (_entries.Count > 1024) _entries.Remove(_entries.MinBy(p => p.Value.VerifiedUtc).Key);
            Save();
        }
    }
    public void Clear(string modelScope)
    {
        lock (_gate) { Load(); foreach (var key in _entries.Where(p => p.Value.ModelScope == modelScope).Select(p => p.Key).ToArray()) _entries.Remove(key); Save(); }
    }
    private bool _dirty;
    private void Load()
    {
        if (_dirty) return;
        try
        {
            if (!File.Exists(_path)) return;
            using var file = File.OpenRead(_path);
            if (file.Length > 2 * 1024 * 1024) throw new IOException("Cache too large");
            var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(file) ?? new();
            _entries = saved.Where(p => p.Key.Length == 64 && p.Value != null &&
                DateTime.UtcNow - p.Value.VerifiedUtc < TimeSpan.FromDays(30) && p.Value.VerifiedUtc <= DateTime.UtcNow &&
                AiModelParameterPolicy.Validate(new() { Rules = p.Value.Rules }) == null).Take(1024).ToDictionary(p => p.Key, p => p.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { PersistenceWarning = "模型适配缓存无法读取，本次使用内存规则。"; }
    }
    private void Save()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
            File.Move(temp, _path, true); PersistenceWarning = null; _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _dirty = true; PersistenceWarning = "模型适配已在本次运行生效，缓存保存失败。"; }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

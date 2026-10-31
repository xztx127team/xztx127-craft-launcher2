using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>
/// Remembers optional request fields rejected by an API provider. Provider IDs and
/// normalized URLs (including tenant query parameters) are hashed together; the
/// persisted cache contains neither endpoint URLs, credentials nor model names.
/// A cache failure must never prevent a request from using the in-memory result.
/// </summary>
public sealed class AiProviderParameterMemory
{
    internal const string FileName = "provider-parameter-memory.json";
    private const int FormatVersion = 1;
    private const int MaximumScopes = 1024;
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly HashSet<string> AllowedParameters = new(StringComparer.Ordinal)
    {
        "temperature", "reasoning_effort", "stream_options", "stream", "tools",
        "max_tokens", "max_completion_tokens"
    };
    // Settings can create a separate service for model tests while the chat
    // service remains open. Serialize their refresh/save operations by cache path.
    private static readonly ConcurrentDictionary<string, object> CacheGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _omitted = new(StringComparer.Ordinal);
    private readonly string? _filePath;
    private string? _persistenceWarning;

    /// <summary>A credential-free diagnostic; null when the latest cache operation succeeded.</summary>
    public string? PersistenceWarning
    {
        get { lock (_gate) return _persistenceWarning; }
    }

    public AiProviderParameterMemory(string dataDirectory)
    {
        try
        {
            // Cache metadata deliberately lives outside ai_chat, whose JSON files
            // are independently enumerated as actual conversations.
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                _persistenceWarning = "AI 参数兼容设置已在内存中启用，但未提供有效的本地保存目录。";
                return;
            }
            _filePath = Path.GetFullPath(Path.Combine(dataDirectory, "ai", FileName));
            _gate = CacheGates.GetOrAdd(_filePath, _ => new object());
            lock (_gate) TryLoad();
        }
        catch (Exception error) when (IsCacheFailure(error))
        {
            _persistenceWarning = "无法读取 AI 参数兼容设置，将在本次运行中重新学习。";
        }
    }

    /// <summary>Returns a detached snapshot, shared by every model/key on this provider URL.</summary>
    public IReadOnlyCollection<string> GetOmittedParameters(string providerId, string baseUrl)
    {
        var scope = Scope(providerId, baseUrl);
        if (scope == null) return Array.Empty<string>();
        lock (_gate)
        {
            TryLoad();
            return _omitted.TryGetValue(scope, out var fields)
                ? fields.OrderBy(value => value, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
        }
    }

    /// <summary>
    /// Returns whether a newly rejected, allow-listed field was learned. A true
    /// result is effective immediately even if persistence is currently unavailable.
    /// </summary>
    public bool Remember(string providerId, string baseUrl, string parameter)
    {
        var field = (parameter ?? "").Trim().ToLowerInvariant();
        if (!AllowedParameters.Contains(field)) return false;
        var scope = Scope(providerId, baseUrl);
        if (scope == null) return false;
        lock (_gate)
        {
            // Merge on every read/write so a second service's learned fields are
            // immediately visible and two service instances cannot overwrite one
            // another's successful discoveries with stale local snapshots.
            TryLoad();
            if (!_omitted.TryGetValue(scope, out var fields))
            {
                // Bound both the persisted cache and live memory, including when
                // imported settings cycle through a large number of API addresses.
                if (_omitted.Count >= MaximumScopes) _omitted.Remove(_omitted.Keys.First());
                fields = new HashSet<string>(StringComparer.Ordinal);
                _omitted.Add(scope, fields);
            }
            var added = fields.Add(field);
            if (added || _persistenceWarning != null) Save();
            return added;
        }
    }

    private void TryLoad()
    {
        try { Load(); }
        catch (Exception error) when (IsCacheFailure(error))
        {
            _persistenceWarning = "无法读取 AI 参数兼容设置，将在本次运行中重新学习。";
        }
    }

    private static string? Scope(string providerId, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 16384
            || string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Length > 65536) return null;
        try
        {
            var provider = providerId.Trim().ToLowerInvariant();
            var url = AiEndpointRouting.NormalizeBaseUrl(baseUrl);
            // Length-prefixing avoids delimiter collisions in imported provider IDs.
            var identity = provider.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + provider + url;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
        catch (ArgumentException) { return null; }
    }

    private void Load()
    {
        if (_filePath == null || !File.Exists(_filePath)) return;
        using var source = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Read at most one extra byte, even if another process modifies the file
        // between its length check and this bounded read.
        if (source.Length > MaximumFileBytes) throw new InvalidDataException("Cache size limit.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (bytes.Length <= MaximumFileBytes)
        {
            var remaining = MaximumFileBytes + 1 - (int)bytes.Length;
            var read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            bytes.Write(buffer, 0, read);
        }
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Cache size limit.");
        using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != FormatVersion
            || !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array
            || entries.GetArrayLength() > MaximumScopes)
            throw new InvalidDataException("Unsupported parameter cache.");
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("scope", out var scopeValue) || scopeValue.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("omittedParameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array
                || parameters.GetArrayLength() > AllowedParameters.Count) continue;
            var scope = scopeValue.GetString();
            if (scope == null || scope.Length != 64 || scope.Any(character => !Uri.IsHexDigit(character))) continue;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in parameters.EnumerateArray())
            {
                if (parameter.ValueKind != JsonValueKind.String) continue;
                var field = parameter.GetString();
                if (field != null && AllowedParameters.Contains(field)) fields.Add(field);
            }
            if (fields.Count > 0)
            {
                scope = scope.ToUpperInvariant();
                if (_omitted.TryGetValue(scope, out var previous)) previous.UnionWith(fields);
                else
                {
                    if (_omitted.Count >= MaximumScopes) _omitted.Remove(_omitted.Keys.First());
                    _omitted.Add(scope, fields);
                }
            }
        }
    }

    // Called only under _gate: concurrent requests on one service cannot lose
    // newly learned fields or overwrite a newer cache snapshot with an older one.
    private void Save()
    {
        if (_filePath == null) return;
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".provider-parameters-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", FormatVersion);
                    writer.WriteStartArray("entries");
                    foreach (var pair in _omitted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("scope", pair.Key);
                        writer.WriteStartArray("omittedParameters");
                        foreach (var field in pair.Value.OrderBy(value => value, StringComparer.Ordinal)) writer.WriteStringValue(field);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                }
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _filePath, overwrite: true);
            temporary = null;
            _persistenceWarning = null;
        }
        catch (Exception error) when (IsCacheFailure(error))
        {
            _persistenceWarning = "AI 参数兼容设置已在内存中记住，但保存失败；重启后可能需要重新学习。";
        }
        finally
        {
            if (temporary != null)
                try { File.Delete(temporary); }
                catch (Exception error) when (IsCacheFailure(error)) { }
        }
    }

    private static bool IsCacheFailure(Exception error) => error is IOException
        or UnauthorizedAccessException or SecurityException or ArgumentException
        or NotSupportedException or JsonException or InvalidOperationException;
}

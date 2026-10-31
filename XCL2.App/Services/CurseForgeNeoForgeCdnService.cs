using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>Resolves a NeoForge installer to a CDN URL explicitly published by the official CurseForge API.</summary>
internal static class CurseForgeNeoForgeCdnService
{
    private const string ApiBase = "https://api.curseforge.com/v1/minecraft/modloader";
    private const int MaxMetadataCharacters = 2 * 1024 * 1024;
    private static readonly Lazy<Resolver> Shared = new(() =>
        new Resolver(CurseForgeApiTransport.CreateClient(), () => new CurseForgeKeyService().TryGetKey()));

    internal static Task<string?> TryResolveAsync(string officialInstallerUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return TryReadInstaller(officialInstallerUrl, out _) ? Shared.Value.TryResolveAsync(officialInstallerUrl, ct)
            : Task.FromResult<string?>(null);
    }

    internal sealed record InstallerRequest(string Version, string Filename);

    internal static bool TryReadInstaller(string url, out InstallerRequest request)
    {
        request = null!;
        if (!TryPublicHttps(url, out var uri) || !uri.Host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6 || parts[0] != "releases" || parts[1] != "net" || parts[2] != "neoforged" || parts[3] != "neoforge")
            return false;
        var version = Uri.UnescapeDataString(parts[4]);
        if (version.Length > 96 || !Regex.IsMatch(version, @"\A[0-9]+(?:\.[0-9]+){2,3}(?:[-+][A-Za-z0-9][A-Za-z0-9._+-]*)?\z"))
            return false;
        var filename = "neoforge-" + version + "-installer.jar";
        if (Uri.UnescapeDataString(parts[5]) != filename) return false;
        request = new InstallerRequest(version, filename);
        return true;
    }

    private static bool TryPublicHttps(string value, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool IsNumber(JsonElement value, string name, int expected) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var number) && number == expected;

    internal static string? ReadCdnUrl(string json, string canonicalName, InstallerRequest request)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.Object ||
            !IsNumber(value, "type", 6) || !IsNumber(value, "installMethod", 6) || Text(value, "name") != canonicalName ||
            Text(value, "filename") != request.Filename) return null;
        if (value.TryGetProperty("approved", out var approved) && approved.ValueKind != JsonValueKind.True) return null;
        var forgeVersion = Text(value, "forgeVersion");
        var mavenVersion = Text(value, "mavenVersionString");
        bool Matches(string? version) => version == request.Version || version == "neoforge-" + request.Version ||
            version == "net.neoforged:neoforge:" + request.Version;
        // At least one explicit version field must identify the artifact. Any other supplied
        // version field must agree, rather than silently falling back to a similar filename.
        if ((!Matches(forgeVersion) && !Matches(mavenVersion)) ||
            (!string.IsNullOrEmpty(forgeVersion) && !Matches(forgeVersion)) ||
            (!string.IsNullOrEmpty(mavenVersion) && !Matches(mavenVersion))) return null;
        var url = Text(value, "downloadUrl");
        if (url == null || !TryPublicHttps(url, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("edge.forgecdn.net" or "mediafilez.forgecdn.net" or "modloaders.forgecdn.net")) return null;
        var path = uri.AbsolutePath;
        if (!path.Contains('/') || Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]) != request.Filename) return null;
        // Preserve exactly the API-published address: no Maven prefix or file-ID guessing.
        return url;
    }

    internal sealed class Resolver
    {
        private sealed record CacheEntry(string? Url, DateTimeOffset Expires);
        private readonly HttpClient _api;
        private readonly Func<string?> _key;
        private readonly TimeSpan _timeout;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

        internal Resolver(HttpClient api, Func<string?> key, TimeSpan? timeout = null)
        {
            _api = api;
            _api.MaxResponseContentBufferSize = 4 * 1024 * 1024;
            _key = key;
            _timeout = timeout.HasValue && timeout.Value > TimeSpan.Zero && timeout.Value < TimeSpan.FromSeconds(5)
                ? timeout.Value : TimeSpan.FromSeconds(5);
        }

        internal async Task<string?> TryResolveAsync(string original, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryReadInstaller(original, out var request)) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_timeout);
            var entered = false;
            try
            {
                // Cache and API work share one small gate, including its wait in the deadline.
                // A rapid series of downloads must not multiply authenticated lookups.
                await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
                entered = true;
                if (_cache.TryGetValue(request.Version, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                    return cached.Url;
                var key = _key();
                if (string.IsNullOrWhiteSpace(key)) return null;
                // Ask for one exact canonical name, then verify the API's returned identity.
                // This avoids loading an ever-growing catalog of every Minecraft mod loader.
                // The CDN address itself is never constructed from the requested version.
                var name = "neoforge-" + request.Version;
                var json = await CurseForgeApiTransport.GetStringAsync(_api, ApiBase + "/" + Uri.EscapeDataString(name), key, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                return Remember(request.Version, json.Length > MaxMetadataCharacters ? null : ReadCdnUrl(json, name, request));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or JsonException or
                IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // This is an optional candidate. Official Maven remains available if the API
                // denies access, times out, changes schema or has no equivalent CDN artifact.
                return entered ? Remember(request.Version, null) : null;
            }
            finally { if (entered) _gate.Release(); }
        }

        private string? Remember(string version, string? url)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _cache.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                _cache.Remove(expired);
            if (!_cache.ContainsKey(version) && _cache.Count >= 64)
                _cache.Remove(_cache.MinBy(pair => pair.Value.Expires).Key);
            _cache[version] = new CacheEntry(url, now.Add(url == null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(30)));
            return url;
        }
    }
}

using System.Net.Http;

namespace XCL2.App.Services;

/// <summary>Credential endpoint and OAuth callback validation, without UI or network access.</summary>
internal static class AuthEndpointPolicy
{
    // A 307/308 can preserve a credential-bearing POST body across HTTPS origins.
    // Login endpoints must be explicit; do not follow redirects with passwords or tokens.
    internal static HttpClientHandler CreateCredentialHandler() => new() { AllowAutoRedirect = false };

    internal static bool MatchesRedirect(string? candidate, string expected)
    {
        return Uri.TryCreate(candidate, UriKind.Absolute, out var actual)
            && Uri.TryCreate(expected, UriKind.Absolute, out var target)
            && string.Equals(actual.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(actual.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase)
            && actual.Port == target.Port
            && string.Equals(actual.AbsolutePath, target.AbsolutePath, StringComparison.Ordinal)
            && string.IsNullOrEmpty(actual.UserInfo)
            && string.IsNullOrEmpty(actual.Fragment);
    }

    internal static IEnumerable<string> BuildApiRootCandidates(string? input)
    {
        var raw = input?.Trim() ?? "";
        const string dragPrefix = "authlib-injector:yggdrasil-server:";
        if (raw.StartsWith(dragPrefix, StringComparison.OrdinalIgnoreCase))
            raw = Uri.UnescapeDataString(raw[dragPrefix.Length..]);
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)) yield break;

        // An explicitly entered root may use any path; never silently discard it.
        raw = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (raw.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase)) raw = raw[..^11];
        var origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var candidates = new[] { raw, raw + "/api/yggdrasil", origin + "/api/yggdrasil", origin + "/authlib-injector/api" };
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal)) yield return candidate;
    }
}

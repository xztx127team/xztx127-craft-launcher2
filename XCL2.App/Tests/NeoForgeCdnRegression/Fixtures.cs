using System.Net;

namespace XCL2.App.Services;

// The resolver under test receives a fake HttpClient and a fixture key provider.
// Reaching these production factories would be an unintended live request.
internal static class LauncherHttpClientFactory
{
    public static HttpClient Create(TimeSpan? timeout = null, bool allowAutoRedirect = true,
        DecompressionMethods decompression = DecompressionMethods.None, bool useCookies = true)
        => throw new InvalidOperationException("Live HTTP is forbidden in this regression.");
    public static bool IsTlsFailure(Exception error) => false;
}

internal sealed class CurseForgeKeyService
{
    public string? TryGetKey() => throw new InvalidOperationException("Real API keys are forbidden in this regression.");
}

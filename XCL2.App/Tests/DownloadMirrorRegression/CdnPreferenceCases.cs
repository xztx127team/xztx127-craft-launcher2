using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class CdnPreferenceCases
{
    private const string CurseForgeFile = "https://edge.forgecdn.net/files/1234/567/NeoForge%20Mod.jar";
    private const string CurseForgeSibling = "https://mediafilez.forgecdn.net/files/1234/567/NeoForge%20Mod.jar";
    private const string ModrinthFile = "https://cdn.modrinth.com/data/project/versions/version/NeoForge.jar";
    private const string NeoForgeInstaller = "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.1/neoforge-21.1.1-installer.jar";
    private const string LoaderCdn = "https://modloaders.forgecdn.net/fixtures/neoforge-21.1.1-installer.jar";

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        var previous = ConfigService.Active;
        var previousResolver = CurseForgeNeoForgeCdnService.Resolver;
        ConfigService.Active = new ConfigService();
        var config = ConfigService.Active.Config.DownloadMirrors;
        var directory = Path.Combine(Path.GetTempPath(), "xcl-cdn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            DownloadEndpoints.ResetHealth();
            Require(config.PreferCurseForgeCdn && config.PreferModrinthCdn, "Official CDN preferences must default on.");
            Require(DownloadMirrorPolicy.OfficialCdnAlternatives(CurseForgeFile).Single() == CurseForgeSibling,
                "Known public file must preserve path/filename encoding on its official sibling.");
            Require(DownloadMirrorPolicy.OfficialCdnAlternatives(CurseForgeSibling).Single() == CurseForgeFile,
                "Both official file hosts must support symmetric fallback.");
            foreach (var excluded in new[]
            {
                "", "https://api.curseforge.com/v1/mods/1/files/2", "https://api.modrinth.com/v2/project/example",
                CurseForgeFile + "?signature=private", CurseForgeFile + "#private", CurseForgeFile.Replace("https:", "http:"),
                CurseForgeFile.Replace("edge.forgecdn.net", "edge.forgecdn.net.evil.invalid"),
                CurseForgeFile.Replace("edge.forgecdn.net", "user:secret@edge.forgecdn.net"),
                CurseForgeFile.Replace("/files/", "/avatars/"), CurseForgeFile.Replace("/1234/", "/unknown/"),
                "https://edge.forgecdn.net/files/1234/567/extra/file.jar", LoaderCdn, NeoForgeInstaller, ModrinthFile
            }) Require(DownloadMirrorPolicy.OfficialCdnAlternatives(excluded).Count == 0, "CDN fallback invented an unauthorized or unrelated path.");

            var candidates = DownloadEndpoints.Candidates(CurseForgeFile, preferMirror: true);
            Require(candidates[0] == CurseForgeFile && candidates[1] == CurseForgeSibling,
                "CurseForge preference must prioritize original and sibling over third-party mirrors.");
            Require(DownloadEndpoints.Candidates(ModrinthFile, true)[0] == ModrinthFile,
                "Modrinth preference must use exactly the returned CDN URL.");
            Require(DownloadMirrorPolicy.IsPreferredOfficialCdn(LoaderCdn, config) &&
                !DownloadMirrorPolicy.IsPreferredOfficialCdn("https://api.curseforge.com/v1/minecraft/modloader/neoforge-21.1.1", config),
                "Only file CDN hosts may enter the official tier; API hosts may not.");

            // A much faster third-party mirror may not silently override an explicit CDN preference.
            using (var client = new HttpClient(new Handler(async (request, ct) =>
            {
                Require(request.Method == HttpMethod.Head, "Preference probe unexpectedly fetched a body.");
                if (request.RequestUri!.Host == "edge.forgecdn.net") await Task.Delay(80, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })))
            {
                var ranked = await DownloadEndpoints.ProbeCandidatesAsync(client, candidates, CancellationToken.None);
                Require(ranked[0] == CurseForgeFile && ranked[1] == CurseForgeSibling,
                    "Probe latency undid original CDN priority.");
            }
            DownloadEndpoints.ReportFailure(CurseForgeFile);
            var unhealthy = DownloadEndpoints.Candidates(CurseForgeFile, true);
            Require(unhealthy[0] == CurseForgeSibling && unhealthy[^1] == CurseForgeFile,
                "Failed official CDN must yield to a healthy fallback.");
            DownloadEndpoints.ResetHealth();
            config.PreferCurseForgeCdn = config.PreferModrinthCdn = false;
            Require(new Uri(DownloadEndpoints.Candidates(CurseForgeFile, true)[0]).Host == "mod.mcimirror.top" &&
                new Uri(DownloadEndpoints.Candidates(ModrinthFile, true)[0]).Host == "mod.mcimirror.top",
                "Turning CDN preference off must restore mirror ordering.");
            Require(DownloadEndpoints.Candidates(CurseForgeFile, true).Contains(CurseForgeFile), "Original CDN fallback was lost.");
            config.PreferCurseForgeCdn = config.PreferModrinthCdn = true;

            var resolverCalls = 0;
            CurseForgeNeoForgeCdnService.Resolver = (url, _) =>
            {
                resolverCalls++;
                return Task.FromResult<string?>(url == NeoForgeInstaller ? LoaderCdn : null);
            };
            using (var client = new HttpClient(new Handler((_, _) => throw new Exception("Resolution used file transport."))))
            {
                var resolved = await DownloadEndpoints.ResolveCandidatesAsync(client, NeoForgeInstaller, true, "installer.jar", null, CancellationToken.None);
                Require(resolved[0] == LoaderCdn && resolved.Contains(NeoForgeInstaller),
                    "API-provided NeoForge CDN URL must precede original Maven fallback.");
                var before = resolverCalls;
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-only");
                Require((await DownloadEndpoints.ResolveCandidatesAsync(client, NeoForgeInstaller, true, "installer.jar", null, CancellationToken.None)).Single() == NeoForgeInstaller,
                    "Authenticated request crossed into CDN resolution.");
                client.DefaultRequestHeaders.Authorization = null;
                var signed = NeoForgeInstaller + "?signature=private";
                Require((await DownloadEndpoints.ResolveCandidatesAsync(client, signed, true, "installer.jar", null, CancellationToken.None)).Single() == signed && resolverCalls == before,
                    "Signed/authenticated URL was disclosed to CDN resolver.");
                config.PreferCurseForgeCdn = false;
                resolved = await DownloadEndpoints.ResolveCandidatesAsync(client, NeoForgeInstaller, true, "installer.jar", null, CancellationToken.None);
                Require(!resolved.Contains(LoaderCdn) && resolverCalls == before, "Disabled preference still queried CurseForge metadata.");
                config.PreferCurseForgeCdn = true;
            }
            CurseForgeNeoForgeCdnService.Resolver = null;

            var good = Encoding.UTF8.GetBytes("complete authorized CDN file");
            var expected = Convert.ToHexString(SHA256.HashData(good));
            var downloads = new List<string>();
            using (var client = new HttpClient(new Handler((request, _) =>
            {
                if (request.Method == HttpMethod.Head) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
                downloads.Add(request.RequestUri!.Host);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(request.RequestUri.Host == "edge.forgecdn.net" ? Encoding.UTF8.GetBytes("corrupt") : good)
                });
            })))
            {
                var destination = Path.Combine(directory, "mod.jar");
                await DownloadEndpoints.DownloadFileWithFallbackAsync(client, CurseForgeFile, true, destination, "fixture failed", expectedSha256: expected);
                Require(downloads.SequenceEqual(new[] { "edge.forgecdn.net", "mediafilez.forgecdn.net" }) && File.ReadAllBytes(destination).SequenceEqual(good),
                    "Corrupt primary CDN did not retry/verify its official sibling before publication.");
            }
        }
        finally
        {
            ConfigService.Active = previous;
            CurseForgeNeoForgeCdnService.Resolver = previousResolver;
            DownloadEndpoints.ResetHealth();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}

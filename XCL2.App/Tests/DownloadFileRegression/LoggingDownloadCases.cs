using System.Net;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class LoggingDownloadCases
{
    internal static async Task RunAsync(string root)
    {
        foreach (var (version, id) in new[] { ("2.0-beta9", "client-1.7.xml"), ("2.8.1", "client-1.12.xml") })
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "LoggingFixtures", id));
            var detail = new VersionDetail { Libraries = new() { new LibraryEntry { Name = "org.apache.logging.log4j:log4j-core:" + version } } };
            var config = LauncherLoggingPolicy.Resolve(detail)!;
            var target = LauncherLoggingPolicy.PathFor(root, config);
            var requests = 0;
            using (var client = new HttpClient(new FixtureHandler(request =>
            {
                if (request.Method == HttpMethod.Get) requests++;
                Require(request.RequestUri!.AbsolutePath.EndsWith("/" + id, StringComparison.Ordinal), "Wrong official logging file requested.");
                return bytes;
            })))
            using (var service = new DownloadService(DownloadSource.Official, client))
            {
                await service.DownloadLoggingConfigurationAsync(root, detail);
                Require(LauncherLoggingPolicy.IsVerified(target, config), "Official Log4j mitigation was not downloaded and verified.");
                await service.DownloadLoggingConfigurationAsync(root, detail);
                Require(requests == 1, "Already-verified logging configuration was unnecessarily downloaded again.");
            }
            var corrupt = bytes.ToArray(); corrupt[^1] ^= 1;
            File.WriteAllBytes(target, corrupt);
            using (var client = new HttpClient(new FixtureHandler(_ => corrupt)))
            using (var service = new DownloadService(DownloadSource.Official, client))
            {
                try { await service.DownloadLoggingConfigurationAsync(root, detail); throw new Exception("Wrong logging SHA1 was accepted."); }
                catch (IOException) { }
                Require(!LauncherLoggingPolicy.IsVerified(target, config), "Invalid logging configuration was considered verified.");
            }
            using (var client = new HttpClient(new FixtureHandler(_ => bytes)))
            using (var service = new DownloadService(DownloadSource.Official, client))
                await service.DownloadLoggingConfigurationAsync(root, detail);
            Require(LauncherLoggingPolicy.IsVerified(target, config), "Corrupted logging configuration could not be repaired.");
        }
        Console.WriteLine("Logging download security: official legacy XML, exact size/hash, cache reuse and corruption repair checked.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class FixtureHandler(Func<HttpRequestMessage, byte[]> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response(request)) });
    }
}

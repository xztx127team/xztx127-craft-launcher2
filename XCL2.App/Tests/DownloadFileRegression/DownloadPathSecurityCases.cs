using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class DownloadPathSecurityCases
{
    internal static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "path-security");
        Directory.CreateDirectory(root);
        var requests = 0;
        using var http = new HttpClient(new FixtureHandler(_ =>
        {
            requests++;
            return Encoding.UTF8.GetBytes("must not write outside the instance");
        }));
        using var service = new DownloadService(DownloadSource.Official, http);
        foreach (var id in new[] { "../escape", "..\\escape", "/absolute", "C:\\escape", "CON", "instance." })
        {
            await Reject(() => service.InstallVersionAsync(root,
                new VersionManifestEntry { Id = id, Url = "https://fixture.example/version.json" }, null));
            await Reject(() => service.DownloadLibrariesOnlyAsync(root, new VersionDetail { Id = id }, null));
        }
        foreach (var path in new[] { "../../escaped.bin", "..\\..\\escaped.bin", "/absolute.bin", "C:\\escaped.bin", "group/../escaped.bin", "group./escaped.bin", "NUL.bin" })
        {
            await Reject(() => service.DownloadLibrariesOnlyAsync(root, new VersionDetail
            {
                Id = "safe", Libraries = new() { new LibraryEntry
                {
                    Name = "safe:fixture:1", Downloads = new LibraryDownloads
                    { Artifact = new DownloadArtifact { Path = path, Url = "https://fixture.example/file.bin" } }
                } }
            }, null));
        }
        await Reject(() => service.DownloadLibrariesOnlyAsync(root, new VersionDetail
        {
            Id = "safe", Libraries = new() { new LibraryEntry { Name = "safe:../../escape:1", Url = "https://fixture.example/" } }
        }, null));
        await Reject(() => service.DownloadAssetsOnlyAsync(root, new VersionDetail
        { AssetIndex = new AssetIndexRef { Id = "../../escaped", Url = "https://fixture.example/index.json" } }, null));
        Require(requests == 0, "Invalid metadata reached the network before path validation.");
        Require(!File.Exists(Path.Combine(testRoot, "escaped.bin")), "Library path escaped the Minecraft root.");

        foreach (var hash in new[] { "", "a", "../../escaped", new string('z', 40) })
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new { objects = new Dictionary<string, object>
                { ["fixture"] = new { hash, size = 4 } } });
            var reads = 0;
            using var indexHttp = new HttpClient(new FixtureHandler(_ => { reads++; return json; }));
            using var indexService = new DownloadService(DownloadSource.Official, indexHttp);
            await Reject(() => indexService.DownloadAssetsOnlyAsync(root, new VersionDetail
                { AssetIndex = new AssetIndexRef { Id = "malformed", Url = "https://fixture.example/index.json" } }, null));
            Require(reads == 1 && !File.Exists(Path.Combine(root, "assets", "indexes", "malformed.json")),
                "An invalid resource hash was persisted or requested as a download path.");
        }

        var external = Path.Combine(testRoot, "external");
        Directory.CreateDirectory(external);
        var secret = Path.Combine(external, "private.bin");
        File.WriteAllText(secret, "private sentinel");
        var link = Path.Combine(root, "linked");
        // Linux fixtures exercise real links. Windows runs require the test user's symlink privilege.
        try { Directory.CreateSymbolicLink(link, external); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        { Console.WriteLine("Download path security: symlink cases skipped (Windows symlink privilege unavailable)."); return; }
        await Reject(async () => { using var lease = await DownloadTargetLock.AcquireAsync(Path.Combine(link, "file.bin"), default); });
        Require(!File.Exists(Path.Combine(external, "file.bin")), "Download ownership followed a directory link.");

        var target = Path.Combine(root, "target.bin");
        File.CreateSymbolicLink(target, secret);
        await Reject(async () => { using var lease = await DownloadTargetLock.AcquireAsync(target, default); });
        await Reject(() => DownloadTargetLock.WriteTextAsync(target, "overwritten", default));
        Require(File.ReadAllText(secret) == "private sentinel", "Metadata/download writer overwrote a linked private file.");
        File.Delete(target);

        File.CreateSymbolicLink(target + ".part", secret);
        var hashValue = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("downloaded bytes")));
        var operation = (Task)typeof(DownloadService).GetMethod("DownloadFileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object[] { "https://fixture.example/download.bin", target, hashValue, CancellationToken.None, true })!;
        await Reject(() => operation);
        Require(File.ReadAllText(secret) == "private sentinel" && !File.Exists(target), "Resume writer followed a partial-file symlink.");
        Require(requests == 0, "A linked partial file was accepted before starting network I/O.");
    }

    private static async Task Reject(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
        throw new Exception("Unsafe metadata or linked download path was accepted.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class FixtureHandler(Func<HttpRequestMessage, byte[]> bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes(request)) });
    }
}

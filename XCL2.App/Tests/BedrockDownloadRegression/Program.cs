using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(message);
}
async Task RejectAsync(Func<Task> action, string message)
{
    try { await action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(message);
}
byte[] Zip(params (string Path, byte[] Data)[] entries)
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var entry in entries)
        {
            using var stream = archive.CreateEntry(entry.Path, CompressionLevel.Optimal).Open();
            stream.Write(entry.Data);
        }
    return output.ToArray();
}
byte[] Manifest(string type) => Encoding.UTF8.GetBytes("""
    {"format_version":2,"header":{"name":"Regression pack","uuid":"172193b4-672e-4a37-b335-6a8bb2fd3fa8","version":[1,0,0]},
     "modules":[{"type":"TYPE","uuid":"91fa0ab2-adcb-4edf-9dd1-c1752587366b","version":[1,0,0]}]}
    """.Replace("TYPE", type));
void Validate(byte[] bytes, long expandedLimit = BedrockMarketplaceDownloadService.MaxExpandedBytes)
{
    using var input = new MemoryStream(bytes);
    BedrockMarketplaceDownloadService.ValidatePackage(input, CancellationToken.None, expandedLimit);
}
var good = Zip(("manifest.json", Manifest("resources")), ("textures/example.txt", Encoding.UTF8.GetBytes("asset")));
foreach (var type in new[] { "resources", "data", "script" })
{
    Validate(Zip(("pack/manifest.json", Manifest(type))));
    checks++;
}
Validate(Zip(("Resource pack.mcpack", good), ("README.txt", Encoding.UTF8.GetBytes("license"))));
checks++;
Validate(Zip(("release.mcaddon", Zip(("resources.mcpack", good), ("behavior.mcpack", Zip(("manifest.json", Manifest("data"))))))));
checks++;
Validate(Zip(("manifest.json", Encoding.UTF8.GetPreamble().Concat(Manifest("resources")).ToArray())));
checks++;
var previewManifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Manifest("script"))
    .Replace("\"format_version\":2", "\"format_version\":3").Replace("[1,0,0]", "\"1.0.0-beta.1+build.1\""));
Validate(Zip(("manifest.json", previewManifest)));
checks++;
Reject(() => Validate(Encoding.UTF8.GetBytes("<html>Not a download</html>")), "HTML accepted as package.");
Reject(() => Validate(Zip(("pack.mcmeta", Encoding.UTF8.GetBytes("{}")), ("assets/test.txt", new byte[1]))), "Java resource pack accepted.");
Reject(() => Validate(Zip(("manifest.json", Manifest("world_template")))), "Unrelated module type accepted.");
Reject(() => Validate(Zip(("manifest.json", Encoding.UTF8.GetBytes("{\"format_version\":2,\"modules\":[{\"type\":\"resources\"}]}")))), "Manifest without Bedrock UUID accepted.");
Reject(() => Validate(Zip(("manifest.json", Encoding.UTF8.GetBytes("[]")))), "Array manifest accepted.");
Reject(() => Validate(Zip(("manifest.json", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Manifest("resources"))
    .Replace("\"version\":[1,0,0]", "\"version\":[-1,0,0]"))))), "Invalid pack version accepted.");
Reject(() => Validate(Zip(("manifest.json", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Manifest("resources"))
    .Replace("91fa0ab2-adcb-4edf-9dd1-c1752587366b", "invalid"))))), "Invalid module UUID accepted.");
Reject(() => Validate(Zip(("manifest.json", Encoding.UTF8.GetBytes("{bad json")))), "Invalid JSON accepted.");
Reject(() => Validate(Zip(("../manifest.json", Manifest("data")))), "Traversal path accepted.");
Reject(() => Validate(Zip(("C:/manifest.json", Manifest("data")))), "Absolute path accepted.");
Reject(() => Validate(Zip(("manifest.json", new byte[256 * 1024 + 1]))), "Oversized manifest accepted.");
Reject(() => Validate(good, expandedLimit: 32), "Expanded budget ignored.");
Reject(() => Validate(Zip(("nested.mcaddon", Zip(("inner.mcaddon", Zip(("pack.mcpack", good))))))), "Third nested archive level accepted.");
var fakeEntryCount = good.ToArray();
BinaryPrimitives.WriteUInt16LittleEndian(fakeEntryCount.AsSpan(fakeEntryCount.Length - 22 + 10), ushort.MaxValue);
Reject(() => Validate(fakeEntryCount), "Entry count bomb accepted.");
var incorrectEntryCount = good.ToArray();
BinaryPrimitives.WriteUInt16LittleEndian(incorrectEntryCount.AsSpan(incorrectEntryCount.Length - 22 + 8), 1);
BinaryPrimitives.WriteUInt16LittleEndian(incorrectEntryCount.AsSpan(incorrectEntryCount.Length - 22 + 10), 1);
Reject(() => Validate(incorrectEntryCount), "Lying central directory count accepted.");
Reject(() => Validate(good[..^8]), "Truncated ZIP accepted.");

var url = "https://github.com/example/project/releases/download/v1/pack.mcpack";
foreach (var invalidUrl in new[]
{
    "file:///C:/pack.mcpack", "http://github.com/pack.mcpack", "https://127.0.0.1/pack.mcpack",
    "https://localhost/pack.mcpack", "https://github.com.evil.invalid/pack.mcpack", "https://forgecdn.net.evil.invalid/pack.mcpack",
    "https://user:password@github.com/pack.mcpack", "https://github.com:8080/pack.mcpack", url + "#secret"
}) Reject(() => BedrockMarketplaceDownloadService.ValidateDownloadUri(invalidUrl), "Unsafe URL accepted: " + invalidUrl);
foreach (var validUrl in new[] { url, "https://release-assets.githubusercontent.com/pack.mcpack?sig=signed", "https://mediafilez.forgecdn.net/files/1/2/pack.mcaddon" })
{
    Expect(BedrockMarketplaceDownloadService.ValidateDownloadUri(validUrl).Scheme == "https", "Official download rejected.");
}
foreach (var name in new[] { "../pack.mcpack", "..\\pack.mcpack", "C:pack.zip", "name.zip:stream", "NUL.zip", "COM1.mcpack", "pack.jar", "pack.mcpack ", "pack?.zip" })
    Reject(() => BedrockMarketplaceDownloadService.ValidateFileName(name), "Unsafe filename accepted: " + name);

BedrockMarketFile FileInfo(string name = "pack.mcpack", long size = 0, string? sha256 = null, string? sha1 = null, string? md5 = null) => new()
{
    Name = name, Version = "v1", Source = "GitHub", DownloadUrl = url,
    Size = size, Sha256 = sha256, Sha1 = sha1, Md5 = md5
};
HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
var root = Path.Combine(Path.GetTempPath(), "xcl-bedrock-download-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void NoPartial() => Expect(!Directory.EnumerateFiles(root, "*.part").Any(), "Staging file leaked.");
try
{
    // 已有文件与本次下载互不覆盖；所有来源校验值都要匹配。
    var existing = Path.Combine(root, "pack.mcpack");
    await File.WriteAllTextAsync(existing, "existing user file");
    using (var service = new BedrockMarketplaceDownloadService(new Handler((_, _) => Task.FromResult(Bytes(good)))))
    {
        var result = await service.DownloadAsync(FileInfo(size: good.Length,
            sha256: Convert.ToHexString(SHA256.HashData(good)), sha1: Convert.ToHexString(SHA1.HashData(good)),
            md5: Convert.ToHexString(MD5.HashData(good))), root);
        Expect(Path.GetFileName(result) == "pack (1).mcpack", "Duplicate must get a new filename.");
        Expect((await File.ReadAllBytesAsync(result)).SequenceEqual(good), "Wrong committed bytes.");
        Expect(await File.ReadAllTextAsync(existing) == "existing user file", "Existing file overwritten.");
        NoPartial();
        await RejectAsync(() => service.DownloadAsync(FileInfo(sha256: new string('0', 64)), root), "Bad hash accepted.");
        await RejectAsync(() => service.DownloadAsync(FileInfo(size: good.Length + 1), root), "Metadata length mismatch accepted.");
        await RejectAsync(() => service.DownloadAsync(FileInfo(sha1: "not-a-hash"), root), "Malformed source hash accepted.");
        NoPartial();
    }
    foreach (var body in new[] { Encoding.UTF8.GetBytes("<!DOCTYPE html><title>Gate</title>"), Zip(("fabric.mod.json", Encoding.UTF8.GetBytes("{}"))) })
    using (var service = new BedrockMarketplaceDownloadService(new Handler((_, _) => Task.FromResult(Bytes(body)))))
    {
        await RejectAsync(() => service.DownloadAsync(FileInfo(), root), "Wrong payload accepted.");
        NoPartial();
    }
    using (var service = new BedrockMarketplaceDownloadService(new Handler((_, _) =>
    {
        var response = Bytes(good);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        return Task.FromResult(response);
    })))
        await RejectAsync(() => service.DownloadAsync(FileInfo(), root), "HTML content type accepted.");

    using (var service = new BedrockMarketplaceDownloadService(new Handler((_, _) =>
    {
        var response = Bytes(good);
        response.Content.Headers.ContentLength = good.Length + 1;
        return Task.FromResult(response);
    })))
        await RejectAsync(() => service.DownloadAsync(FileInfo(), root), "Truncated transfer accepted.");
    NoPartial();

    using (var service = new BedrockMarketplaceDownloadService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
    {
        Content = new ByteArrayContent(good)
    }))))
    {
        try { await service.DownloadAsync(FileInfo(), root); throw new InvalidOperationException("Unexpected partial response accepted."); }
        catch (HttpRequestException) { checks++; }
    }
    NoPartial();

    var requests = new List<string>();
    using (var service = new BedrockMarketplaceDownloadService(new Handler((request, _) =>
    {
        requests.Add(request.RequestUri!.Host);
        Expect(!request.Headers.Contains("Authorization") && !request.Headers.Contains("x-api-key") && !request.Headers.Contains("Cookie"), "Credentials leaked to CDN.");
        if (request.RequestUri.Host == "github.com")
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/file.mcpack?sig=public-release");
            return Task.FromResult(response);
        }
        return Task.FromResult(Bytes(good));
    })))
    {
        await service.DownloadAsync(FileInfo(), root);
        Expect(requests.SequenceEqual(new[] { "github.com", "release-assets.githubusercontent.com" }), "Approved redirect was not followed.");
    }
    requests.Clear();
    using (var service = new BedrockMarketplaceDownloadService(new Handler((request, _) =>
    {
        requests.Add(request.RequestUri!.Host);
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri("https://127.0.0.1/private");
        return Task.FromResult(response);
    })))
    {
        await RejectAsync(() => service.DownloadAsync(FileInfo(), root), "Unsafe redirect followed.");
        Expect(requests.Count == 1, "Private redirect target received a request.");
    }

    using (var cancellation = new CancellationTokenSource())
    using (var service = new BedrockMarketplaceDownloadService(new Handler((_, _) => Task.FromResult(Bytes(good)))))
    {
        var before = Directory.GetFiles(root).Length;
        try
        {
            await service.DownloadAsync(FileInfo(), root, new InlineProgress(value =>
            {
                if (value.Stage == "正在下载基岩版资源") cancellation.Cancel();
            }), cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored.");
        }
        catch (OperationCanceledException) { checks++; }
        Expect(Directory.GetFiles(root).Length == before, "Cancelled download committed a file.");
        NoPartial();
    }
    using (var cancellation = new CancellationTokenSource())
    using (var service = new BedrockMarketplaceDownloadService(new Handler((_, _) => throw new InvalidOperationException("Already-cancelled operation reached network."))))
    {
        cancellation.Cancel();
        try { await service.DownloadAsync(FileInfo(), root, ct: cancellation.Token); throw new InvalidOperationException("Pre-cancellation ignored."); }
        catch (OperationCanceledException) { checks++; }
    }

    using (var service = new BedrockMarketplaceDownloadService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new BrokenStream(good))
    }))))
    {
        try { await service.DownloadAsync(FileInfo(), root); throw new InvalidOperationException("Stream failure ignored."); }
        catch (IOException) { checks++; }
        NoPartial();
    }
    using (var service = new BedrockMarketplaceDownloadService(new Handler((_, _) => Task.FromResult(Bytes(good)))))
    {
        var results = await Task.WhenAll(service.DownloadAsync(FileInfo(name: "race.mcpack"), root), service.DownloadAsync(FileInfo(name: "race.mcpack"), root));
        Expect(results.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "Concurrent downloads overwrote each other.");
        Expect(results.All(path => File.ReadAllBytes(path).SequenceEqual(good)), "Concurrent download lost its content.");
        NoPartial();
    }
    await BedrockMarketTransferCases.RunAsync(Expect, root, good);
    Console.WriteLine($"Bedrock download regression checks passed: {checks}");
}
finally { Directory.Delete(root, recursive: true); }

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

sealed class InlineProgress(Action<ProgressInfo> report) : IProgress<ProgressInfo>
{
    public void Report(ProgressInfo value) => report(value);
}

sealed class BrokenStream(byte[] body) : MemoryStream(body, writable: false)
{
    private bool _read;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_read) return ValueTask.FromException<int>(new IOException("Simulated interrupted body"));
        _read = true;
        return base.ReadAsync(buffer, cancellationToken);
    }
}

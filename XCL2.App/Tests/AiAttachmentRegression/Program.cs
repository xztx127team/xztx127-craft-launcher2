using System.IO.Compression;
using System.Text;
using XCL2.App.Models;
using XCL2.App.Services;

var testRoot = Path.Combine(Path.GetTempPath(), "xcl-upload-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int assertions = 0;
void Assert(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
async Task Reject(string path, string message)
{
    bool rejected = false;
    try { var imported = await AiAttachmentService.ImportAsync(path); AiAttachmentService.RemovePending(imported); }
    catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, message);
}
string Zip(string name, params (string Path, string Content, int Attributes)[] entries)
{
    var file = Path.Combine(testRoot, name + ".zip");
    using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
    foreach (var entry in entries)
    {
        var target = archive.CreateEntry(entry.Path); target.ExternalAttributes = entry.Attributes;
        using var writer = new StreamWriter(target.Open(), Encoding.UTF8); writer.Write(entry.Content);
    }
    return file;
}
try
{
    var safe = await AiAttachmentService.ImportAsync(Zip("safe", ("nested/readme.txt", "safe data", 0)));
    Assert(safe.Kind == AiAttachmentKind.Archive && safe.ExtractedFileCount == 1, "managed ZIP extraction");
    var file = Path.Combine(safe.TemporaryDirectory!, "nested", "readme.txt");
    Assert(!AiAttachmentService.IsReadOnlyAttachmentPath(file), "importing alone grants no ambient read authority");
    using (AiAttachmentService.BeginReadScope(AiAttachmentService.GetValidatedRoots(new[] { safe })))
    {
        Assert(File.ReadAllText(file) == "safe data" && AiAttachmentService.IsReadOnlyAttachmentPath(file), "only the request's uploaded file is in its read-only tool scope");
        Assert(!AiAttachmentService.IsReadOnlyAttachmentPath(safe.TemporaryDirectory + "-sibling"), "sibling prefixes are outside the scope");
        Assert(!AiAttachmentService.IsReadOnlyAttachmentPath(testRoot), "upload source directory is never implicitly granted");
    }
    Assert(!AiAttachmentService.IsReadOnlyAttachmentPath(file), "disposing a request revokes its ambient read scope");
    AiAttachmentService.RemovePending(safe);
    Assert(!AiAttachmentService.IsReadOnlyAttachmentPath(file), "removing an unsent upload revokes its read scope");
    await Reject(Zip("traversal", ("../outside.txt", "must never escape", 0)), "path traversal denied");
    await Reject(Zip("backslash", ("nested\\..\\..\\outside.txt", "escape", 0)), "Windows path traversal denied");
    await Reject(Zip("absolute", ("/outside.txt", "escape", 0)), "absolute path denied");
    await Reject(Zip("drive", ("C:/outside.txt", "escape", 0)), "drive path denied");
    await Reject(Zip("stream", ("normal.txt:payload", "escape", 0)), "alternate data stream denied");
    await Reject(Zip("device", ("nested/CON.txt", "device", 0)), "Windows device names denied");
    await Reject(Zip("duplicate", ("Readme.txt", "one", 0), ("readme.txt", "two", 0)), "case-fold duplicate denied");
    await Reject(Zip("symlink", ("link", "../../outside", unchecked((int)0xA1FF0000))), "symlink entries denied");
    await Reject(Zip("bomb", ("expanded.txt", new string('x', 2 * 1024 * 1024), 0)), "extreme compression ratio denied");
    Assert(!File.Exists(Path.Combine(testRoot, "outside.txt")), "malicious ZIP wrote no external file");
    var pngPath = Path.Combine(testRoot, "valid.png");
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6UQkAAAAASUVORK5CYII=");
    File.WriteAllBytes(pngPath, png);
    var image = await AiAttachmentService.ImportAsync(pngPath);
    Assert(image.Kind == AiAttachmentKind.Image && image.MediaType == "image/png" && Convert.FromBase64String(image.ImageBase64!).SequenceEqual(png), "image bytes retained for API/history");
    var restored = image.Clone(); restored.TemporaryDirectory = testRoot;
    AiAttachmentService.RegisterRestored(new[] { restored });
    Assert(AiAttachmentService.GetReadOnlyRoots().Count == 0, "listing/restoring history does not authorize any attachment");
    Assert(!AiAttachmentService.IsReadOnlyAttachmentPath(testRoot), "history paths cannot authorize arbitrary directories");
    AiAttachmentService.RemovePending(image);
    File.WriteAllText(pngPath, "This is not an image");
    await Reject(pngPath, "invalid images denied");
    File.WriteAllBytes(pngPath, new byte[AiAttachmentService.MaxImageBytes + 1]);
    await Reject(pngPath, "image byte limit enforced");
    var rar = Path.Combine(testRoot, "sample.rar"); File.WriteAllText(rar, "RAR");
    await Reject(rar, "unsupported archives do not invoke shell");
    var a = await AiAttachmentService.ImportAsync(Zip("scope-a", ("a.txt", "A private data", 0)));
    var b = await AiAttachmentService.ImportAsync(Zip("scope-b", ("b.txt", "B private data", 0)));
    var fileA = Path.Combine(a.TemporaryDirectory!, "a.txt");
    var fileB = Path.Combine(b.TemporaryDirectory!, "b.txt");
    AiAttachmentService.RegisterRestored(new[] { a, b });
    using (AiAttachmentService.BeginReadScope(AiAttachmentService.GetValidatedRoots(new[] { a })))
    {
        Assert(AiAttachmentService.IsReadOnlyAttachmentPath(fileA) && !AiAttachmentService.IsReadOnlyAttachmentPath(fileB), "session A cannot inherit restored B roots");
        using (AiAttachmentService.BeginReadScope(AiAttachmentService.GetValidatedRoots(new[] { b })))
            Assert(!AiAttachmentService.IsReadOnlyAttachmentPath(fileA) && AiAttachmentService.IsReadOnlyAttachmentPath(fileB), "nested B scope replaces A instead of widening it");
        Assert(AiAttachmentService.IsReadOnlyAttachmentPath(fileA) && !AiAttachmentService.IsReadOnlyAttachmentPath(fileB), "scope exit restores the parent request only");
    }
    await Task.WhenAll(new[] { a, b }.Select(async attachment =>
    {
        using var scope = AiAttachmentService.BeginReadScope(AiAttachmentService.GetValidatedRoots(new[] { attachment }));
        await Task.Yield();
        Assert(AiAttachmentService.GetReadOnlyRoots().SequenceEqual(new[] { attachment.TemporaryDirectory! }), "concurrent request scopes remain independent across awaits");
    }));
    Assert(AiAttachmentService.GetReadOnlyRoots().Count == 0, "concurrent scopes cannot leak grants into their caller");
    AiAttachmentService.ReleaseDeletedSessionAttachments(new[] { a }, new[] { a.Clone() });
    Assert(File.Exists(fileA), "another saved conversation retains a shared attachment");
    using (AiAttachmentService.PinAttachments(new[] { a }))
    {
        AiAttachmentService.ReleaseDeletedSessionAttachments(new[] { a }, Array.Empty<AiAttachment>());
        Assert(File.Exists(fileA), "an open draft/request prevents deletion of a shared attachment");
        Assert(!AiAttachmentService.IsReadOnlyAttachmentPath(fileA), "a preservation pin never grants read permission");
    }
    Assert(!File.Exists(fileA), "the final draft/request release collects an unreferenced attachment");
    AiAttachmentService.RemovePending(b);
    Assert(!File.Exists(fileB), "ordinary unused uploads are still collected");
    Console.WriteLine($"PASS: {assertions} upload and safe extraction assertions");
}
finally { Directory.Delete(testRoot, true); }

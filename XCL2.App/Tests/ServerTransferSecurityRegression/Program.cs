using System.IO.Compression;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var root = Path.Combine(Path.GetTempPath(), "xcl-server-security-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
void Reject(Action action)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new Exception("Unsafe archive accepted");
}
string Zip(params (string Name, string Content)[] entries)
{
    var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (name, content) in entries)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }
    return path;
}
try
{
    var service = new ServerInstanceTransferService();
    var target = Path.Combine(root, "server");
    Directory.CreateDirectory(target);
    var original = Path.Combine(target, "server.jar");
    File.WriteAllText(original, "original");
    foreach (var evil in new[] { "../server-other/payload", "..\\server-other\\payload", "C:/payload", "server.jar:payload", "NUL.txt", "dir./payload" })
    {
        var zip = Zip(("content/server.jar", "replacement"), ("content/" + evil, "payload"));
        Reject(() => service.Import(zip, target));
        Check(File.ReadAllText(original) == "original", "Archive rejected after overwriting valid entry");
    }
    Check(!Directory.Exists(Path.Combine(root, "server-other")), "Sibling-prefix traversal wrote outside server");
    var launch = Zip(("content/server.jar", "replacement"), ("xcl2-server-manifest.json",
        JsonSerializer.Serialize(new ServerInstanceManifest { LaunchTarget = "../outside.cmd", LaunchTargetIsScript = true })));
    Reject(() => service.Import(launch, target));
    Check(File.ReadAllText(original) == "original", "External launch path changed existing server");

    var outside = Path.Combine(root, "private");
    Directory.CreateDirectory(outside);
    var secret = Path.Combine(outside, "secret.txt");
    File.WriteAllText(secret, "private-value");
    var link = Path.Combine(target, "linked");
    var export = Path.Combine(root, "export.zip");
    File.WriteAllText(export, "previous-backup");
    var canCreateLinks = true;
    try { Directory.CreateSymbolicLink(link, outside); }
    catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
    {
        canCreateLinks = false;
        Console.WriteLine("SKIP: link cases require Windows Developer Mode or symbolic-link permission.");
    }
    if (canCreateLinks)
    {
        Reject(() => ContentArchiveInstaller.Merge(Zip(("linked/secret.txt", "changed")), target));
        Check(File.ReadAllText(secret) == "private-value", "component installer followed link outside its destination");
        Reject(() => service.Import(Zip(("content/linked/secret.txt", "changed")), target));
        Check(File.ReadAllText(secret) == "private-value", "Import followed junction outside target");
        Reject(() => service.Export(new ServerInstance { Directory = target }, export));
        Check(File.ReadAllText(export) == "previous-backup", "Failed export destroyed previous backup");
        Directory.Delete(link);
        File.CreateSymbolicLink(Path.Combine(target, "linked-file"), secret);
        Reject(() => service.Export(new ServerInstance { Directory = target }, export));
        File.Delete(Path.Combine(target, "linked-file"));
    }

    Directory.CreateDirectory(Path.Combine(target, "world"));
    File.WriteAllText(Path.Combine(target, "world", "level.dat"), "world-data");
    service.Export(new ServerInstance { Directory = target, DisplayName = "Test server" }, export);
    var restored = Path.Combine(root, "restored");
    var manifest = service.Import(export, restored);
    Check(manifest?.DisplayName == "Test server", "Server manifest did not round-trip");
    Check(File.ReadAllText(Path.Combine(restored, "world", "level.dat")) == "world-data", "Server content did not round-trip");

    var content = Path.Combine(root, "content");
    var malformed = Zip(("level.dat", "good"), ("../outside.txt", "evil"));
    Reject(() => ContentArchiveInstaller.ExtractNew(malformed, content));
    Check(!Directory.Exists(content) && !File.Exists(Path.Combine(root, "outside.txt")), "Bedrock import partially accepted traversal");
    var symlinkZip = Zip(("link", "../private"));
    using (var archive = ZipFile.Open(symlinkZip, ZipArchiveMode.Update))
        archive.GetEntry("link")!.ExternalAttributes = unchecked((int)0xA1FF0000);
    Reject(() => ContentArchiveInstaller.ExtractNew(symlinkZip, content));
    foreach (var name in new[] { "CONIN$", "CONOUT$.txt", "COM¹.txt", "LPT²", "COM³" })
        Reject(() => ContentArchiveInstaller.ExtractNew(Zip((name, "device")), content));
    ContentArchiveInstaller.ExtractNew(Zip(("level.dat", "world")), content);
    Check(File.ReadAllText(Path.Combine(content, "level.dat")) == "world", "Valid Bedrock content rejected");
    ContentArchiveInstaller.Merge(Zip(("level.dat", "new"), ("resource.txt", "resource")), content,
        (entry, path) => entry.Name == "level.dat" && File.Exists(path));
    Check(File.ReadAllText(Path.Combine(content, "level.dat")) == "world", "Preserve rule ignored");
    Check(File.ReadAllText(Path.Combine(content, "resource.txt")) == "resource", "Merge skipped new content");
    Console.WriteLine($"Server/content security: {checks} checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

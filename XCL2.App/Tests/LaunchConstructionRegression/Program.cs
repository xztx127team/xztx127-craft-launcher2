using System.Reflection;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; }
void Reject(Action action, string contains)
{
    try { action(); }
    catch (Exception error) when (error.Message.Contains(contains, StringComparison.Ordinal)) { count++; return; }
    throw new Exception("Expected rejection: " + contains);
}
List<string> Preview(LauncherService.LaunchOptions options)
{
    try
    {
        var method = typeof(LauncherService).GetMethod("BuildArguments", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = ((string Main, List<string> Args))method.Invoke(new LauncherService(), new object[] { options, true })!;
        return result.Args;
    }
    catch (TargetInvocationException error) { throw error.InnerException!; }
}
var root = Path.Combine(Path.GetTempPath(), "XCL2-launch-construction-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    void Write(string id, object value, bool jar = false)
    {
        var dir = Path.Combine(root, "versions", id); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(value));
        if (jar) File.WriteAllText(Path.Combine(dir, id + ".jar"), "client fixture");
    }
    void Library(string relative)
    { var file = Path.Combine(root, "libraries", relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "library fixture"); }
    Write("base", new
    {
        id = "1.21.1", mainClass = "example.Main", assets = "modern", clientVersion = "1.21.1",
        downloads = new { client = new { url = "https://example.invalid/client" } },
        libraries = new[] { new { name = "test:lib:2", downloads = new { artifact = new { path = "test/lib/2/lib-2.jar" } } } },
        arguments = new
        {
            jvm = new object[] { "-Dbase=yes",
                new { rules = new[] { new { action = "allow", os = new { arch = "^x86$" } } }, value = "-Donly32=yes" },
                new { rules = new[] { new { action = "allow", os = new { arch = "^amd64$" } } }, value = "-Donly64=yes" } },
            game = new[] { "--base", "yes" }
        }
    }, jar: true);
    Write("loader", new
    {
        id = "loader", inheritsFrom = "base", mainClass = "example.Loader",
        libraries = new[] { new { name = "test:lib:1", rules = new[] { new { action = "allow", os = new { arch = "^x86$" } } }, downloads = new { artifact = new { path = "test/lib/1/lib-1.jar" } } } },
        arguments = new { jvm = new[] { "-Dloader=yes" }, game = new[] { "--loader", "yes" } }
    });
    Write("custom", new { id = "custom", inheritsFrom = "loader", arguments = new { jvm = new[] { "-Dcustom=yes" } } });
    Library("test/lib/1/lib-1.jar");
    Library("test/lib/2/lib-2.jar");
    var java32 = Path.Combine(root, "java32.exe");
    using (var writer = new BinaryWriter(File.Create(java32)))
    {
        writer.Write(new byte[256]); writer.BaseStream.Position = 0; writer.Write((ushort)0x5a4d);
        writer.BaseStream.Position = 0x3c; writer.Write(128u);
        writer.BaseStream.Position = 128; writer.Write(0x00004550u); writer.Write((ushort)0x014c);
    }
    var native32 = NativeDirectoryPolicy.Resolve(Path.Combine(root, "versions", "base"), "x86");
    Directory.CreateDirectory(native32); File.Copy(java32, Path.Combine(native32, "fixture.dll"));
    Check(NativeDirectoryPolicy.HasCompatibleDll(native32, "x86") && !NativeDirectoryPolicy.HasCompatibleDll(native32, "amd64"),
        "native presence checks compare the DLL's actual PE architecture");
    var options = new LauncherService.LaunchOptions { MinecraftDir = root, VersionId = "custom", JavaPath = java32 };
    var launchArguments = Preview(options);
    Check(launchArguments.Contains("example.Loader"), "nearest inherited main class is used by the real builder");
    Check(launchArguments.Contains("-Dbase=yes") && launchArguments.Contains("-Dloader=yes") && launchArguments.Contains("-Dcustom=yes"), "all JVM ancestors reach the real command");
    Check(launchArguments.IndexOf("-Dbase=yes") < launchArguments.IndexOf("-Dloader=yes") && launchArguments.IndexOf("-Dloader=yes") < launchArguments.IndexOf("-Dcustom=yes"), "ancestor argument order is root to child");
    Check(launchArguments.Contains("-Donly32=yes") && !launchArguments.Contains("-Donly64=yes"), "real builder evaluates arch rules against the selected Java PE, not launcher bitness");
    Check(launchArguments.Contains("--base") && launchArguments.Contains("--loader"), "all game argument ancestors are merged");
    Check(launchArguments.Contains("-Djava.library.path=" + NativeDirectoryPolicy.Resolve(Path.Combine(root, "versions", "base"), "x86")), "natives belong to actual client owner and selected Java architecture");
    var cp = launchArguments[launchArguments.IndexOf("-cp") + 1];
    Check(cp.Contains("lib-1.jar") && !cp.Contains("lib-2.jar") && cp.Contains("base.jar"), "child library overrides parent version and client resolves through grandparent");
    Check(new LauncherService().CheckMissingLibraries(root, "custom", "x86").Count == 0, "preflight uses the same architecture and merged libraries");
    var resolution = VersionInheritanceResolver.Load(root, "custom", FolderService.ResolveVersionJson, "x86");
    var repairPlan = VersionRepairPlan.Create(resolution);
    Check(repairPlan.ClientVersionId == "base" && repairPlan.ClientJarBaseName == "1.21.1" && repairPlan.Libraries.Id == "base"
        && resolution.Effective.Id == "custom", "repair separates renamed client folder, original JSON ID and natives target without mutating metadata");
    var resolution64 = VersionInheritanceResolver.Load(root, "custom", FolderService.ResolveVersionJson, "amd64");
    Check(resolution64.Effective.Libraries.Single().Name == "test:lib:2", "an x86-only child library cannot erase the inherited amd64 library");
    Write("external", new { id = "original-client-id", mainClass = "example.Main", downloads = new { client = new { url = "https://example.invalid/external" } } }, jar: true);
    Write("redirect", new { id = "redirect", inheritsFrom = "custom", jar = "external" });
    var redirected = VersionRepairPlan.Create(VersionInheritanceResolver.Load(root, "redirect", FolderService.ResolveVersionJson, "x86"));
    Check(redirected.ClientVersionId == "external" && redirected.ClientJarBaseName == "original-client-id" && redirected.Client.Downloads?.ContainsKey("client") == true,
        "explicit jar redirection retains the external client's download metadata and target names");
    Check(!Directory.Exists(Path.Combine(root, "versions", "custom", "mods")), "preview does not create game data");
    options.ExtraGameArgs = "--custom-name \"two words\""; options.LaunchFullscreen = true;
    launchArguments = Preview(options);
    Check(launchArguments.Contains("--fullscreen") && launchArguments.Contains("two words"), "explicit session fullscreen and game values reach real command");
    options.AutoJoinServerAddress = "example.org:25565"; options.RequireAutoJoinServerSupport = true;
    Reject(() => Preview(options), "Quick Play");
    Write("quick", new { id = "quick", inheritsFrom = "base", arguments = new { game = new object[] { new { rules = new[] { new { action = "allow", features = new { is_quick_play_multiplayer = true } } }, value = new[] { "--quickPlayMultiplayer", "${quickPlayMultiplayer}" } } } } });
    options.VersionId = "quick";
    launchArguments = Preview(options);
    Check(launchArguments[launchArguments.IndexOf("--quickPlayMultiplayer") + 1] == "example.org:25565" && launchArguments.Count(x => x == "--quickPlayMultiplayer") == 1, "explicit server activates declared feature and appears once");
    Write("quick-child", new { id = "quick-child", inheritsFrom = "quick", arguments = new { game = new[] { "--quickPlayMultiplayer", "${quickPlayMultiplayer}" } } });
    options.VersionId = "quick-child";
    Check(Preview(options).Count(x => x == "--quickPlayMultiplayer") == 1, "a child repeating its parent's server option does not duplicate Quick Play");
    Write("missing", new { id = "missing", inheritsFrom = "not-installed" }); options.VersionId = "missing";
    Reject(() => Preview(options), "父版本");
    Write("cycle-a", new { id = "cycle-a", inheritsFrom = "cycle-b" }); Write("cycle-b", new { id = "cycle-b", inheritsFrom = "cycle-a" }); options.VersionId = "cycle-a";
    Reject(() => Preview(options), "循环");
    Write("escape", new { id = "escape", inheritsFrom = "../outside" }); options.VersionId = "escape";
    Reject(() => Preview(options), "无效");
    using var rules = JsonDocument.Parse("""{"rules":[{"action":"allow","os":{"name":"windows","arch":"^(amd64|x86_64)$","version":"^10\\."}}]}""");
    Check(LaunchArgumentRulePolicy.Allows(rules.RootElement, architecture: "amd64", osVersion: "10.0.22631"), "arch and OS regex match together");
    Check(!LaunchArgumentRulePolicy.Allows(rules.RootElement, architecture: "x86", osVersion: "10.0.22631"), "different architecture is excluded");
    Check(!LaunchArgumentRulePolicy.Allows(rules.RootElement, architecture: "amd64", osVersion: "6.1.7601"), "different Windows version is excluded");
    using var last = JsonDocument.Parse("""{"rules":[{"action":"allow"},{"action":"disallow","os":{"arch":"^x86$"}}]}""");
    Check(!LaunchArgumentRulePolicy.Allows(last.RootElement, architecture: "x86"), "last matching disallow wins");
    using var invalid = JsonDocument.Parse("""{"rules":[{"action":"allow","os":{"arch":"["}}]}""");
    Check(!LaunchArgumentRulePolicy.Allows(invalid.RootElement), "malformed regex is bounded and fails closed");
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"PASS {count} production launch construction checks");

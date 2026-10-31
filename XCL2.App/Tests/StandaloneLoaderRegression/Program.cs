using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new Exception(message);
}
VersionDetail Parse(string json) => JsonSerializer.Deserialize<VersionDetail>(json)!;
var root = Path.Combine(Path.GetTempPath(), "XCL2-standalone-loader-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var vanilla = Parse("""
        {
          "id":"1.20.1", "mainClass":"net.minecraft.client.main.Main", "clientVersion":"1.20.1",
          "downloads":{"client":{"url":"https://example.invalid/client.jar","size":42,"sha1":"1111111111111111111111111111111111111111"}},
          "assetIndex":{"id":"5","url":"https://example.invalid/assets.json"}, "assets":"5",
          "javaVersion":{"majorVersion":17},
          "logging":{"client":{"argument":"-Dlog4j.configurationFile=${path}","type":"log4j2-xml","file":{"id":"client.xml","url":"https://example.invalid/client.xml"}}},
          "arguments":{"game":["--username","${auth_player_name}"],"jvm":["-cp","${classpath}"]},
          "libraries":[
            {"name":"net.sf.jopt-simple:jopt-simple:5.0.4","downloads":{"artifact":{"path":"net/sf/jopt-simple/jopt-simple/5.0.4/jopt-simple-5.0.4.jar","url":"https://example.invalid/jopt.jar"}}},
            {"name":"org.lwjgl:lwjgl:3.3.1","natives":{"windows":"natives-windows-${arch}"},"downloads":{"classifiers":{"natives-windows-64":{"path":"lwjgl-native.jar","url":"https://example.invalid/native.jar"}}}},
            {"name":"test:override:1"},
            {"name":"test:windows32:1","rules":[{"action":"allow","os":{"name":"windows","arch":"^x86$"}}]},
            {"name":"test:linux:1","rules":[{"action":"allow","os":{"name":"linux"}}]}
          ]
        }
        """);
    foreach (var loader in new[] { "Fabric", "Quilt", "Legacy Fabric" })
    {
        var coordinate = loader == "Quilt" ? "org.quiltmc:quilt-loader:0.27.0" : "net.fabricmc:fabric-loader:0.16.10";
        var mapping = loader == "Legacy Fabric" ? "net.legacyfabric:intermediary:1.20.1" : "net.fabricmc:intermediary:1.20.1";
        var child = new VersionDetail
        {
            Id = "loader-profile", InheritsFrom = "1.20.1", MainClass = "loader.KnotClient",
            Libraries = new() { new() { Name = coordinate }, new() { Name = mapping }, new() { Name = "test:override:2" } },
            Arguments = new ArgumentsSpec { Jvm = new() { "-Dloader=true" }, Game = new() { "--loader", "yes" } }
        };
        var original = JsonSerializer.Serialize(child);
        var complete = StandaloneLoaderProfile.Compose(root, "custom-instance", "1.20.1", loader, "0.16.10", vanilla, child);
        Check(complete.InheritsFrom == null && complete.Jar == null, "Standalone result still depends on parent folder.");
        Check(JsonSerializer.Serialize(child) == original, "Composition mutated the original profile before publication.");
        Check(complete.MainClass == child.MainClass && complete.Id == "custom-instance", "Loader main class or custom name was lost.");
        Check(complete.Libraries.Any(l => l.Name == "net.sf.jopt-simple:jopt-simple:5.0.4"), "joptsimple.OptionSpec dependency lost.");
        Check(complete.Libraries.Any(l => l.Natives != null), "Vanilla native downloads lost.");
        Check(complete.Libraries.Any(l => l.Name == "test:windows32:1") && complete.Libraries.Any(l => l.Name == "test:linux:1"), "Platform variants removed from saved profile.");
        Check(complete.Arguments?.Jvm?.Count == 3 && complete.Arguments.Game?.Count == 4, "Parent JVM/game arguments lost or duplicated.");
        Check(complete.AssetIndex?.Id == "5" && complete.Assets == "5" && complete.Logging?.Client?.File?.Id == "client.xml", "Assets or logging configuration lost.");
        Check(complete.XclMinecraftVersion == "1.20.1" && complete.XclComponentLoaderVersion == "0.16.10", "Independent game/loader version metadata lost.");
        var plan64 = StandaloneLoaderProfile.CreateRepairPlan(root, complete, "amd64");
        Check(plan64.ClientVersionId == "custom-instance" && plan64.ClientJarBaseName == "custom-instance", "Client repair escapes standalone directory.");
        Check(plan64.Libraries.Libraries.Count(l => l.Name.StartsWith("test:override:")) == 1 && plan64.Libraries.Libraries.Any(l => l.Name == "test:override:2"), "Runtime library override downloaded twice.");
        Check(!plan64.Libraries.Libraries.Any(l => l.Name is "test:windows32:1" or "test:linux:1"), "Wrong platform library selected.");
        var plan32 = StandaloneLoaderProfile.CreateRepairPlan(root, complete, "x86");
        Check(plan32.Libraries.Libraries.Any(l => l.Name == "test:windows32:1"), "Switching Java architecture lost required library.");
        Check(StandaloneLoaderProfile.GetRepairTarget(child) == null, "Healthy inherited profile selected for repair.");
        child.InheritsFrom = null;
        var target = StandaloneLoaderProfile.GetRepairTarget(child);
        Check(target?.MinecraftVersion == "1.20.1" && target.Loader == loader, "Broken renamed instance not recognized via intermediary coordinate.");
        Check(StandaloneLoaderProfile.GetRepairTarget(complete) == null, "Already repaired profile selected again.");

        var directory = Path.Combine(root, "versions", "custom-instance");
        Directory.CreateDirectory(directory);
        var json = Path.Combine(directory, "custom-instance.json");
        File.WriteAllText(json, JsonSerializer.Serialize(complete));
        var loaded = VersionInheritanceResolver.Load(root, "custom-instance", (dir, id) =>
        {
            var path = Path.Combine(dir, id + ".json");
            return File.Exists(path) ? path : null;
        }, "amd64");
        Check(loaded.Layers.Count == 1 && loaded.Effective.Libraries.Any(l => l.Name.StartsWith("net.sf.jopt-simple:")), "Standalone launch needs absent parent metadata.");
    }
    var legacyVanilla = Parse(JsonSerializer.Serialize(vanilla));
    legacyVanilla.Arguments = null;
    legacyVanilla.ClientVersion = null;
    legacyVanilla.MinecraftArgumentsLegacy = "--username ${auth_player_name} --version ${version_name}";
    var legacyProfile = new VersionDetail { Id = "legacy", MainClass = "legacy.KnotClient", Libraries = new() };
    var legacyComplete = StandaloneLoaderProfile.Compose(root, "legacy-custom", "1.20.1", "Legacy Fabric", "1", legacyVanilla, legacyProfile);
    Check(legacyComplete.MinecraftArgumentsLegacy == legacyVanilla.MinecraftArgumentsLegacy && legacyComplete.ClientVersion == null,
        "Legacy arguments lost or old client incorrectly classified as modern.");
    Reject(() => StandaloneLoaderProfile.Compose(root, "../outside", "1.20.1", "Fabric", "1", vanilla, legacyProfile), "Unsafe instance path accepted.");
    legacyProfile.InheritsFrom = "1.19.4";
    Reject(() => StandaloneLoaderProfile.Compose(root, "safe", "1.20.1", "Fabric", "1", vanilla, legacyProfile), "Wrong game/profile combination accepted.");
    Console.WriteLine($"Standalone loader regression: {checks} checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

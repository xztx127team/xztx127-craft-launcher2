using System.Security.Cryptography;
using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XCL2.App.Models;
using XCL2.App.Services;

if (args.Contains("--lease-child")) { Thread.Sleep(800); return; }

int checks = 0;
void Check(bool pass, string message) { checks++; if (!pass) throw new Exception(message); }
void Reject(Action action, string message)
{
    try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or OperationCanceledException)
    { Check(true, message); return; }
    Check(false, message);
}
var root = Path.Combine(Path.GetTempPath(), "xcl-components-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string Put(string relative, string content)
{
    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    return path;
}
string Profile(string id, string main = "old.Main") => JsonSerializer.Serialize(new { id, mainClass = main });
try
{
    Check(ClientLoaderProfileEndpoints.ClientProfile("https://meta.fabricmc.net/v2", "1.20.1", "0.16.0") ==
        "https://meta.fabricmc.net/v2/versions/loader/1.20.1/0.16.0/profile/json", "Fabric client profile uses official game/loader route without installer version");
    Check(ClientLoaderProfileEndpoints.ClientProfile("https://meta.legacyfabric.net/v2", "1.8.9", "0.16.0") ==
        "https://meta.legacyfabric.net/v2/versions/loader/1.8.9/0.16.0/profile/json", "Legacy Fabric client profile uses advertised route");
    Check(ClientLoaderProfileEndpoints.ClientProfile("https://meta.fabricmc.net/v2/", "snapshot+test", "loader+build") ==
        "https://meta.fabricmc.net/v2/versions/loader/snapshot%2Btest/loader%2Bbuild/profile/json", "Loader URL segments are encoded independently");
    foreach (var invalid in new[] { "..", "../escape", "a/b", "a\\b", "a:", "CON", "bad.", "bad ", "x\n" })
        Reject(() => ComponentProfileTransaction.VersionDirectory(root, invalid), "Reject unsafe instance ID " + invalid);

    Put("versions/Fabric 生存/Fabric 生存.json", Profile("Fabric 生存"));
    Put("versions/Fabric 生存/Fabric 生存.jar", "OLD-CLIENT");
    Put("versions/Fabric 生存/config.json", "{\"userSetting\":true}");
    Put("versions/Fabric 生存/custom-tool.jar", "USER-TOOL");
    Put("versions/Fabric 生存/mods/example.jar", "USER-MOD");
    Put("versions/Fabric 生存/saves/World/level.dat", "WORLD");
    Put("versions/Fabric 生存/config/settings.toml", "enabled=true");
    var prepared = Put("stage/profile.json", Profile("Fabric 生存", "new.Main"));
    var client = Put("stage/client.jar", "NEW-CLIENT");
    var target = Path.Combine(root, "versions", "Fabric 生存");
    var oldProfile = File.ReadAllText(Path.Combine(target, "Fabric 生存.json"));
    Check(!ComponentProfileTransaction.HasRecoveryRecord(root, "Fabric 生存"), "Intact instance has no pending recovery");
    Check(!Directory.Exists(Path.Combine(root, ".xcl-component-transactions")), "Read-only recovery probe creates no transaction directory");

    Reject(() => ComponentProfileTransaction.Commit(root, "Fabric 生存", prepared, client,
        afterMutation: count => { if (count == 1) throw new IOException("Injected failure after client replacement"); }), "Failure rolls back");
    Check(File.ReadAllText(Path.Combine(target, "Fabric 生存.jar")) == "OLD-CLIENT", "Rollback restores original client");
    Check(File.ReadAllText(Path.Combine(target, "Fabric 生存.json")) == oldProfile, "Rollback preserves profile");

    using (var canceled = new CancellationTokenSource())
    {
        Reject(() => ComponentProfileTransaction.Commit(root, "Fabric 生存", prepared, client, canceled.Token,
            count => { if (count == 2) canceled.Cancel(); }), "Cancel after both writes still rolls back");
    }
    Check(File.ReadAllText(Path.Combine(target, "Fabric 生存.jar")) == "OLD-CLIENT", "Cancellation restores client");
    Check(File.ReadAllText(Path.Combine(target, "Fabric 生存.json")) == oldProfile, "Cancellation restores matching profile");
    ComponentProfileTransaction.Commit(root, "Fabric 生存", prepared, client);
    Check(File.ReadAllText(Path.Combine(target, "Fabric 生存.jar")) == "NEW-CLIENT", "Commit installs new client");
    Check(File.ReadAllText(Path.Combine(target, "config.json")) == "{\"userSetting\":true}", "Unrelated top-level JSON retained");
    Check(File.ReadAllText(Path.Combine(target, "custom-tool.jar")) == "USER-TOOL", "Unrelated top-level JAR retained");
    Check(File.ReadAllText(Path.Combine(target, "mods/example.jar")) == "USER-MOD", "Mods retained");
    Check(File.ReadAllText(Path.Combine(target, "saves/World/level.dat")) == "WORLD", "Saves retained");
    Check(File.ReadAllText(Path.Combine(target, "config/settings.toml")) == "enabled=true", "Config retained");
    Check(File.Exists(client), "Prepared source is never recursively deleted by transaction");

    Put("versions/Fabric 生存/natives/old.dll", "OLD-DLL");
    Put("stage/natives/new.dll", "NEW-DLL");
    Reject(() => ComponentProfileTransaction.Commit(root, "Fabric 生存", prepared, client,
        afterMutation: count => { if (count == 2) throw new IOException("Failure after native replacement"); },
        preparedNatives: Path.Combine(root, "stage/natives")), "Native transaction failure rolls back");
    Check(File.ReadAllText(Path.Combine(target, "natives/old.dll")) == "OLD-DLL", "Old native DLL restored");
    Check(!File.Exists(Path.Combine(target, "natives/new.dll")), "Uncommitted native DLL removed");
    ComponentProfileTransaction.Commit(root, "Fabric 生存", prepared, client, preparedNatives: Path.Combine(root, "stage/natives"));
    Check(File.ReadAllText(Path.Combine(target, "natives/new.dll")) == "NEW-DLL" && !File.Exists(Path.Combine(target, "natives/old.dll")),
        "Native replacement removes stale DLLs and installs matching ones");

    // Renamed/imported instances can have an old profile name. Normalize just those launch files.
    Put("versions/renamed/original.json", Profile("original"));
    Put("versions/renamed/original.jar", "OLD-RENAMED");
    Put("versions/renamed/manifest.json", "{\"formatVersion\":1}");
    var renamedPrepared = Put("stage/renamed.json", Profile("renamed", "new.Main"));
    Reject(() => ComponentProfileTransaction.Commit(root, "renamed", renamedPrepared, client,
        afterMutation: count => { if (count == 3) throw new IOException("After removal"); }), "Failure while removing old name rolls back");
    Check(File.Exists(Path.Combine(root, "versions/renamed/original.json")), "Old profile filename restored");
    Check(!File.Exists(Path.Combine(root, "versions/renamed/renamed.json")), "New filename removed on rollback");
    ComponentProfileTransaction.Commit(root, "renamed", renamedPrepared, client);
    Check(!File.Exists(Path.Combine(root, "versions/renamed/original.json")) && File.Exists(Path.Combine(root, "versions/renamed/renamed.json")), "Successful commit normalizes filename");
    Check(File.Exists(Path.Combine(root, "versions/renamed/manifest.json")), "Imported modpack manifest retained");

    // Journal metadata must never collide with an instance literally named "journal".
    Put("versions/journal/journal.json", Profile("journal"));
    Put("versions/journal/journal.jar", "JOURNAL-OLD");
    var journalPrepared = Put("stage/journal.json", Profile("journal", "new.Main"));
    Reject(() => ComponentProfileTransaction.Commit(root, "journal", journalPrepared, client,
        afterMutation: _ => throw new IOException("fail")), "Reserved journal filename does not corrupt snapshots");
    Check(File.ReadAllText(Path.Combine(root, "versions/journal/journal.json")) == Profile("journal"), "journal instance original profile restored");

    // Simulate termination after a write but before completion: durable original files are recovered.
    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("JOURNAL")));
    var recovery = ".xcl-component-transactions/" + key;
    Put(recovery + "/original/journal.json", Profile("journal", "recover.Main"));
    Put(recovery + "/original/journal.jar", "RECOVERED");
    Put(recovery + "/journal.json", JsonSerializer.Serialize(new { VersionId = "journal", OriginalProfile = "journal.json", Committed = false, Files = new[] {
        new { Name = "journal.jar", Existed = true }, new { Name = "journal.json", Existed = true } } }));
    Check(ComponentProfileTransaction.HasRecoveryRecord(root, "journal"), "Interrupted transaction is detected before requiring maintenance");
    ComponentProfileTransaction.Recover(root, "journal");
    Check(!ComponentProfileTransaction.HasRecoveryRecord(root, "journal"), "Completed recovery no longer requires maintenance");
    Check(File.ReadAllText(Path.Combine(root, "versions/journal/journal.jar")) == "RECOVERED", "Interrupted operation recovers client");
    Check(File.ReadAllText(Path.Combine(root, "versions/journal/journal.json")) == Profile("journal", "recover.Main"), "Interrupted operation recovers profile");
    Put("versions/journal/config.json", "USER-CONFIG");
    Put(recovery + "/original/journal.json", Profile("journal"));
    Put(recovery + "/journal.json", JsonSerializer.Serialize(new { VersionId = "journal", OriginalProfile = "journal.json", Committed = false, Files = new[] {
        new { Name = "journal.json", Existed = true }, new { Name = "config.json", Existed = false } } }));
    Reject(() => ComponentProfileTransaction.Recover(root, "journal"), "Corrupt journal cannot delete unrelated JSON");
    Check(File.ReadAllText(Path.Combine(root, "versions/journal/config.json")) == "USER-CONFIG", "Corrupt journal leaves user configuration intact");
    Directory.Delete(Path.Combine(root, recovery), recursive: true);
    Directory.CreateDirectory(Path.Combine(root, recovery, "journal.json"));
    Reject(() => ComponentProfileTransaction.HasRecoveryRecord(root, "journal"), "Directory in place of recovery record is not treated as absent");
    Directory.Delete(Path.Combine(root, recovery), recursive: true);

    // Composition flattens inheritance while preserving the parent directory and both argument sets.
    Put("versions/1.20.1/1.20.1.json", """
        {"id":"1.20.1","mainClass":"vanilla.Main","arguments":{"game":["--username","${auth_player_name}"],"jvm":["-Dvanilla=true"]},
         "libraries":[{"name":"example:base:1"}],"assetIndex":{"id":"5"}}
        """);
    Put("versions/1.20.1/1.20.1.jar", "PARENT-CLIENT");
    Put("versions/loader/loader.json", """
        {"id":"loader","inheritsFrom":"1.20.1","mainClass":"loader.Main","arguments":{"game":["--loader","on"],"jvm":["-Dloader=true"]},
         "libraries":[{"name":"example:base:2"},{"name":"net.fabricmc:fabric-loader:0.16.0"}]}
        """);
    var composed = ComponentProfileComposer.Compose(root, "loader", "Fabric 生存", "1.20.1", "Fabric", "0.16.0");
    Check(composed.Json["inheritsFrom"] == null && composed.Json["jar"] == null, "Profile is independent");
    Check(composed.Json["id"]!.GetValue<string>() == "Fabric 生存", "Target ID preserved");
    Check(composed.Json["arguments"]!["game"]!.AsArray().Count == 4, "Base and loader game arguments retained");
    Check(composed.Json["arguments"]!["jvm"]!.AsArray().Count == 2, "Base and loader JVM arguments retained");
    Check(composed.Json["libraries"]!.AsArray().Count == 2, "Loader library version overrides vanilla coordinate");
    Check(File.ReadAllText(composed.ClientJar) == "PARENT-CLIENT", "Inherited client selected");
    Check(File.Exists(Path.Combine(root, "versions/1.20.1/1.20.1.jar")), "Parent version remains unchanged");

    Put("versions/standalone/standalone.json", """
        {"id":"standalone","mainClass":"optifine.Main","arguments":{"game":["--username","${auth_player_name}","--tweakClass","optifine.Tweak"],"jvm":["-Dvanilla=true"]},"libraries":[]}
        """);
    Put("versions/standalone/standalone.jar", "PATCHED");
    var standalone = ComponentProfileComposer.Compose(root, "standalone", "Fabric 生存", "1.20.1", "OptiFine", "HD_U");
    Check(standalone.Json["arguments"]!["game"]!.AsArray().Count == 4, "Already flattened arguments are not duplicated");
    Check(File.ReadAllText(standalone.ClientJar) == "PATCHED", "Patched client wins over vanilla client");
    Put("versions/old-forge/old-forge.json", """
        {"id":"old-forge","inheritsFrom":"1.20.1","mainClass":"net.minecraft.launchwrapper.Launch",
         "libraries":[{"name":"net.minecraft:launchwrapper:1.12"},{"name":"org.lwjgl:native-only:1","downloads":{"classifiers":{"natives-windows":{"url":"https://example.invalid/native.jar"}}}}]}
        """);
    var oldForge = ComponentProfileComposer.Compose(root, "old-forge", "Fabric 生存", "1.20.1", "Forge", "test");
    var oldForgeLibraries = oldForge.Json["libraries"]!.AsArray();
    Check(oldForgeLibraries.First(node => node!["name"]!.GetValue<string>() == "net.minecraft:launchwrapper:1.12")!["url"]!.GetValue<string>() == "https://libraries.minecraft.net/",
        "Bare Maven launchwrapper receives a repository so downloader and classpath cannot silently skip it");
    Check(oldForgeLibraries.First(node => node!["name"]!.GetValue<string>() == "org.lwjgl:native-only:1")!["url"] == null,
        "Classifier-only native library does not gain a nonexistent main JAR");

    Put("versions/cycle/cycle.json", "{\"id\":\"cycle\",\"inheritsFrom\":\"cycle\"}");
    Reject(() => ComponentProfileComposer.Compose(root, "cycle", "target", "1.20.1", "Fabric", "0.16"), "Inheritance cycle rejected");
    Put("versions/missing/missing.json", "{\"id\":\"missing\",\"inheritsFrom\":\"absent\"}");
    Reject(() => ComponentProfileComposer.Compose(root, "missing", "target", "1.20.1", "Fabric", "0.16"), "Missing inherited profile rejected");

    var vanilla = ComponentProfileComposer.Compose(root, "1.20.1", "Fabric 生存", "1.20.1", "Vanilla", null);
    var vanillaDetail = JsonSerializer.Deserialize<VersionDetail>(vanilla.Json.ToJsonString())!;
    var resolved = VersionInfoResolver.Resolve(target, "Fabric 生存", vanillaDetail);
    Check(resolved.ModLoader == null && resolved.McVersion == "1.20.1", "Removing loader is not undone by a loader word in the instance name");

    // Official Mojang metadata retains the prefix in both the manifest and profile ID:
    // packages/44f6969326bd45aa00dcd3c4ca3a7c05ebb24c04/b1.7.3.json
    // packages/1c888e4d8aed380db25aeb3835f5918297bb5e3a/a1.2.6.json
    foreach (var historicalId in new[] { "b1.7.3", "a1.2.6", "b1.5_01", "a1.2.3_04", "a1.2.2b", "c0.0.13a_03", "inf-20100618", "rd-132211" })
    {
        Check(VersionInfoResolver.LooksLikeMcVersion(historicalId), "Official historical ID is accepted without dropping its prefix: " + historicalId);
        Check(VersionInfoResolver.ExtractAnyVersion("archive-" + historicalId) == historicalId, "Historical extraction preserves the complete official ID: " + historicalId);
        var historicalDetail = new VersionDetail { Id = historicalId, MainClass = "net.minecraft.launchwrapper.Launch" };
        Check(VersionInfoResolver.Resolve(root, "archive-1.20.1", historicalDetail).McVersion == historicalId,
            "Exact profile ID wins over misleading numbers in a renamed instance folder: " + historicalId);
    }
    foreach (var invalidVersion in new[] { "fabric-loader-0.16.0-b1.7.3", "fabric-loader-0.16.0-1.20.1", "b9.9.9", "a1.2.999", "b1.7.3madeup", "1.2.3.4" })
        Check(!VersionInfoResolver.LooksLikeMcVersion(invalidVersion), "Loader IDs and invented historical IDs are not game versions: " + invalidVersion);
    Check(VersionInfoResolver.ExtractAnyVersion("b9.9.9") == null && VersionInfoResolver.ExtractAnyVersion("a1.2.999") == null,
        "Unrecognized historical-looking text cannot be truncated into a release version");
    var inheritedBeta = new VersionDetail { Id = "modded-beta", InheritsFrom = "b1.7.3", MainClass = "net.minecraft.launchwrapper.Launch" };
    Check(VersionInfoResolver.Resolve(root, "modded-beta", inheritedBeta).McVersion == "b1.7.3", "Historical inherited MC remains the exact catalog ID");
    Check(VersionInfoResolver.Resolve(root, "fabric-loader-0.16.0-b1.7.3", new VersionDetail { Id = "fabric-loader-0.16.0-b1.7.3" }).McVersion == "b1.7.3",
        "Loader instance naming resolves historical MC suffix without accepting the whole loader ID as a version");
    foreach (var modernId in new[] { "1.20.1", "26.1", "27.1", "27.1.2", "24w14a", "1.20.5-rc1" })
        Check(VersionInfoResolver.LooksLikeMcVersion(modernId) && VersionInfoResolver.ExtractAnyVersion(modernId) == modernId,
            "Modern version recognition is retained: " + modernId);
    var forgePriority = VersionInfoResolver.Resolve(root, "renamed-forge", JsonSerializer.Deserialize<VersionDetail>("""
        {"id":"1.20.1","libraries":[{"name":"net.minecraftforge:forge:1.12.2-14.23.5.2860"}]}
        """));
    Check(forgePriority.McVersion == "1.12.2" && forgePriority.ModLoader == "Forge" && forgePriority.ModLoaderVersion == "14.23.5.2860",
        "Forge library MC evidence keeps priority over an inconsistent plain profile ID");
    var neoPriority = VersionInfoResolver.Resolve(root, "renamed-neo", JsonSerializer.Deserialize<VersionDetail>("""
        {"id":"1.20.1","inheritsFrom":"1.21.1","libraries":[{"name":"net.neoforged:neoforge:21.1.66"}]}
        """));
    Check(neoPriority.McVersion == "1.21.1" && neoPriority.ModLoader == "NeoForge" && neoPriority.ModLoaderVersion == "21.1.66",
        "NeoForge inheritance keeps priority over a plain profile ID");
    var fabricSuffix = VersionInfoResolver.Resolve(root, "fabric-loader-0.16.0-1.20.1", JsonSerializer.Deserialize<VersionDetail>("""
        {"id":"fabric-loader-0.16.0-1.20.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.0"}]}
        """));
    Check(fabricSuffix.McVersion == "1.20.1" && fabricSuffix.ModLoader == "Fabric" && fabricSuffix.ModLoaderVersion == "0.16.0",
        "Fabric suffix resolves MC before loader-version digits are considered");
    Check(ComponentProfileComposer.MinecraftForNeoForge("21.1.66") == "1.21.1", "Historical NeoForge version mapping");
    Check(ComponentProfileComposer.MinecraftForNeoForge("21.0.10-beta") == "1.21", "Zero legacy MC patch omitted");
    Check(ComponentProfileComposer.MinecraftForNeoForge("26.1.0.5-beta") == "26.1", "Year-based NeoForge version mapping");
    Check(ComponentProfileComposer.MinecraftForNeoForge("26.1.2.114") == "26.1.2", "Year-based game patch preserved");
    Check(ComponentProfileComposer.MinecraftForNeoForge("26.1.0.0-alpha.2+snapshot-1") == null, "Snapshot never offered as a release component");
    Check(ComponentProfileComposer.MinecraftForNeoForge("garbage") == null, "Malformed NeoForge version rejected");

    var customized = JsonNode.Parse("""
        {"id":"custom","mainClass":"old.Main","customIntegration":{"keep":true},
         "arguments":{"jvm":["-Djava.library.path=${natives_directory}","-Xmx6G","-Dmy.setting=yes","--module-path","old-loader-path"],
                      "game":["--username","${auth_player_name}","--launchTarget","forgeclient","--fml.mcVersion","1.20.1","--server","localhost","--customFlag","customValue"]}}
        """)!.AsObject();
    ComponentProfileCustomization.Preserve(vanilla.Json, new[] { customized });
    Check(vanilla.Json["customIntegration"]!["keep"]!.GetValue<bool>(), "Unknown non-component JSON fields preserved");
    var jvmText = vanilla.Json["arguments"]!["jvm"]!.ToJsonString();
    var gameText = vanilla.Json["arguments"]!["game"]!.ToJsonString();
    Check(jvmText.Contains("-Xmx6G") && jvmText.Contains("-Dmy.setting=yes") && !jvmText.Contains("old-loader-path"), "Custom JVM options preserved without stale loader module path");
    Check(gameText.Contains("--server") && gameText.Contains("localhost") && gameText.Contains("customValue") && !gameText.Contains("forgeclient") && !gameText.Contains("--fml"), "User game flags kept; old loader flags removed with their values");
    var beforeReapply = vanilla.Json.ToJsonString();
    ComponentProfileCustomization.Preserve(vanilla.Json, new[] { customized });
    Check(vanilla.Json.ToJsonString() == beforeReapply, "Reinstall does not duplicate custom argument sequence");
    var composite = JsonNode.Parse("""
        {"id":"combo","mainClass":"net.minecraft.launchwrapper.Launch","libraries":[{"name":"net.minecraftforge:forge:1.12.2-14.23.5.2860"},{"name":"optifine:OptiFine:1.12.2_HD_U_G5"}]}
        """)!.AsObject();
    var components = InstalledComponentInventory.FromProfiles(new[] { composite });
    Check(components.Count == 2 && components.Any(component => component.Name == "OptiFine"), "Mixed Forge/OptiFine profile lists every component");
    Check(InstalledComponentInventory.NeedsCompositeConfirmation(components), "Mixed profile requires explicit complete replacement acknowledgement");

    // Identity fields and the two relevant coordinates from the actual API response:
    // https://meta.legacyfabric.net/v2/versions/loader/1.8.9/0.20.1/profile/json (2026-10-25).
    // The official ID and loader artifact still say Fabric; intermediary is MC 1.8.9.
    var legacyFabricProfile = JsonNode.Parse("""
        {"id":"fabric-loader-0.20.1-1.8.9","inheritsFrom":"1.8.9","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
         "libraries":[{"name":"net.legacyfabric:intermediary:1.8.9","url":"https://maven.legacyfabric.net/"},
                      {"name":"net.fabricmc:fabric-loader:0.20.1","url":"https://maven.fabricmc.net/"}]}
        """)!.AsObject();
    foreach (var reverse in new[] { false, true })
    {
        var profile = (JsonObject)legacyFabricProfile.DeepClone();
        if (reverse) profile["libraries"] = new JsonArray(profile["libraries"]!.AsArray().Reverse().Select(node => node!.DeepClone()).ToArray());
        var installedLegacy = InstalledComponentInventory.FromProfiles(new[] { profile });
        Check(installedLegacy.Count == 1 && installedLegacy[0].Name == "LegacyFabric" && installedLegacy[0].Version == "0.20.1",
            "Legacy Fabric is one loader with the actual loader version regardless of library order");
        Check(!InstalledComponentInventory.NeedsCompositeConfirmation(installedLegacy), "Legacy intermediary is not an additional component requiring mixed replacement");
        var resolvedLegacy = VersionInfoResolver.Resolve(root, "renamed-instance", JsonSerializer.Deserialize<VersionDetail>(profile.ToJsonString()));
        Check(resolvedLegacy.ModLoader == "Legacy Fabric" && resolvedLegacy.ModLoaderVersion == "0.20.1" && resolvedLegacy.McVersion == "1.8.9",
            "Renamed Legacy instance selects the Legacy catalog and keeps intermediary MC separate from loader version");
    }
    legacyFabricProfile["xclComponentLoader"] = "LegacyFabric";
    legacyFabricProfile["xclComponentLoaderVersion"] = "0.19.0";
    Check(InstalledComponentInventory.FromProfiles(new[] { legacyFabricProfile }).Single().Version == "0.20.1",
        "Actual Legacy loader artifact takes precedence over stale component metadata");
    var inheritedFabricLoader = JsonNode.Parse("""
        {"id":"loader-base","xclComponentLoader":"Fabric","xclComponentLoaderVersion":"0.20.1",
         "libraries":[{"name":"net.fabricmc:fabric-loader:0.20.1"}]}
        """)!.AsObject();
    var legacyMappingChild = JsonNode.Parse("""
        {"id":"legacy-child","inheritsFrom":"loader-base","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
         "libraries":[{"name":"net.legacyfabric:intermediary:1.8.9"}]}
        """)!.AsObject();
    var inheritedLegacy = InstalledComponentInventory.FromProfiles(new[] { inheritedFabricLoader, legacyMappingChild });
    Check(inheritedLegacy.Count == 1 && inheritedLegacy[0].Name == "LegacyFabric" && inheritedLegacy[0].Version == "0.20.1",
        "Legacy evidence across inherited layers normalizes a Fabric marker and loader to one identity");
    legacyMappingChild["xclComponentLoader"] = "LegacyFabric";
    legacyMappingChild["xclComponentLoaderVersion"] = "0.20.1";
    Check(InstalledComponentInventory.FromProfiles(new[] { legacyMappingChild }).Single().Version == "0.20.1",
        "Intermediary Minecraft version never overwrites a declared Legacy loader version");
    var ordinaryFabric = InstalledComponentInventory.FromProfiles(new[] { inheritedFabricLoader });
    Check(ordinaryFabric.Count == 1 && ordinaryFabric[0].Name == "Fabric", "Ordinary Fabric without Legacy evidence retains its identity");

    var optiFineProfile = JsonNode.Parse("""
        {"id":"opti","mainClass":"net.minecraft.launchwrapper.Launch",
         "libraries":[{"name":"optifine:OptiFine:1.20.4_HD_U_I9"},{"name":"optifine:launchwrapper:2.4"}]}
        """)!.AsObject();
    foreach (var reverse in new[] { false, true })
    {
        var profile = (JsonObject)optiFineProfile.DeepClone();
        if (reverse) profile["libraries"] = new JsonArray(profile["libraries"]!.AsArray().Reverse().Select(node => node!.DeepClone()).ToArray());
        var installedOptiFine = InstalledComponentInventory.FromProfiles(new[] { profile });
        Check(installedOptiFine.Count == 1 && installedOptiFine[0].Name == "OptiFine" && installedOptiFine[0].Version == "1.20.4_HD_U_I9",
            "OptiFine launchwrapper dependency cannot overwrite the actual component version in either order");
    }
    optiFineProfile["libraries"]!.AsArray().RemoveAt(0);
    Check(!InstalledComponentInventory.FromProfiles(new[] { optiFineProfile }).Any(component => component.Name == "OptiFine"),
        "OptiFine launchwrapper alone is not an installed OptiFine component");

    composite["mainClass"] = "example.custom.Bootstrap";
    Check(InstalledComponentInventory.FromProfiles(new[] { composite }).Any(component => component.Name == "自定义启动组件"),
        "Known loader plus custom wrapper is explicitly listed");
    var unsupported = InstalledComponentInventory.FromProfiles(new[] { JsonNode.Parse("""
        {"id":"lite","mainClass":"net.minecraft.launchwrapper.Launch","libraries":[{"name":"com.mumfrey:liteloader:1.12.2"}]}
        """)!.AsObject() });
    Check(InstalledComponentInventory.NeedsCompositeConfirmation(unsupported), "LiteLoader cannot be silently replaced as vanilla");
    var modPath = Path.Combine(root, "versions/Fabric 生存/mods/OptiFine.jar");
    using (var archive = ZipFile.Open(modPath, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(archive.CreateEntry("optifine/OptiFineTweaker.class").Open());
        writer.Write("test");
    }
    var modComponents = InstalledComponentInventory.FromMods(target);
    Check(modComponents.Single(component => component.Name.StartsWith("OptiFine")).IsMod, "OptiFine in mods is managed as a mod, not a profile component");
    Check(!InstalledComponentInventory.NeedsCompositeConfirmation(modComponents), "Mod form is preserved without pretending it is removed by profile maintenance");

    Check(InstalledComponentInventory.DirectDependents(root, "1.20.1").Contains("loader"), "Inherited base identifies dependent instances before it can be changed");
    Put("versions/jar-child/jar-child.json", "{\"id\":\"jar-child\",\"mainClass\":\"net.minecraft.client.main.Main\",\"jar\":\"1.20.1\"}");
    Check(InstalledComponentInventory.DirectDependents(root, "1.20.1").Contains("jar-child"), "Explicit inherited client also protects base version");
    var bootArgs = ComponentProfileCustomization.FilterUserArguments(JsonNode.Parse("""
        ["-Djava.system.class.loader=com.example.OldLoader","--enable-native-access=ALL-UNNAMED","--sun-misc-unsafe-memory-access=allow","-Duser.choice=kept"]
        """)!.AsArray(), jvm: true).ToJsonString();
    Check(bootArgs.Contains("user.choice") && !bootArgs.Contains("OldLoader") && !bootArgs.Contains("native-access") && !bootArgs.Contains("unsafe-memory"),
        "Changing loader or downgrading Java cannot retain old bootstrap or newer-JDK-only switches");
    var oldTarget = JsonNode.Parse("{\"id\":\"legacy\",\"mainClass\":\"net.minecraft.client.Minecraft\",\"minecraftArguments\":\"${auth_player_name} ${auth_session}\"}")!.AsObject();
    ComponentProfileCustomization.Preserve(oldTarget, new[] { customized });
    Check(oldTarget["arguments"]?["game"] is not JsonArray { Count: > 0 } && oldTarget["minecraftArguments"]!.GetValue<string>().Contains("--server localhost"),
        "Modern custom flags appended to legacy template preserve positional identity mode");

    var locks = Path.Combine(root, "locks");
    using (var first = ComponentsOperationLock.AcquireForLaunch(root, "Fabric 生存", locks))
    {
        Check(!ComponentProfileTransaction.HasRecoveryRecord(root, "Fabric 生存"), "Read-only probe remains available during an active launch lease");
        using var second = ComponentsOperationLock.AcquireForLaunch(root, "Fabric 生存", locks);
        Reject(() => { using var blocked = ComponentsOperationLock.AcquireForMaintenance(root, "Fabric 生存", locks); },
            "Multiple shared launches are allowed while exclusive maintenance is blocked");
    }
    using (var exclusive = ComponentsOperationLock.AcquireForMaintenance(root, "Fabric 生存", locks))
        Reject(() => { using var blocked = ComponentsOperationLock.AcquireForLaunch(root, "Fabric 生存", locks); }, "Maintenance blocks launch");
    using (var inheritedLease = ComponentsOperationLock.AcquireForLaunch(root, "loader", locks))
        Reject(() => { using var blocked = ComponentsOperationLock.AcquireForMaintenance(root, "1.20.1", locks); }, "Running child protects inherited base across instances");
    using (var jarLease = ComponentsOperationLock.AcquireForLaunch(root, "jar-child", locks))
        Reject(() => { using var blocked = ComponentsOperationLock.AcquireForMaintenance(root, "1.20.1", locks); }, "Explicit client reference is also held by shared launch lease");
    var pendingKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("FABRIC 生存")));
    var pendingDirectory = ".xcl-component-transactions/" + pendingKey;
    Put(pendingDirectory + "/journal.json", "{\"Committed\":false}");
    Reject(() => { using var blocked = ComponentsOperationLock.AcquireForLaunch(root, "Fabric 生存", locks); }, "Unfinished transaction blocks ordinary launch until recovery");
    Directory.Delete(Path.Combine(root, pendingDirectory), recursive: true);

    var childStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        childStart.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    childStart.ArgumentList.Add("--lease-child");
    using (var child = Process.Start(childStart)!)
    {
        using (var transferred = ComponentsOperationLock.AcquireForLaunch(root, "Fabric 生存", locks)) transferred.TransferTo(child);
        Reject(() => { using var blocked = ComponentsOperationLock.AcquireForMaintenance(root, "Fabric 生存", locks); }, "Transferred lease survives local using scope while process runs");
        child.WaitForExit();
        // Exited callbacks are asynchronous; an exited process cannot hold the lease indefinitely.
        bool released = SpinWait.SpinUntil(() =>
        {
            try { using var acquired = ComponentsOperationLock.AcquireForMaintenance(root, "Fabric 生存", locks); return true; }
            catch (IOException) { return false; }
        }, TimeSpan.FromSeconds(3));
        Check(released, "Process exit releases transferred launch lease");
    }

    if (!OperatingSystem.IsWindows())
    {
        Directory.CreateSymbolicLink(Path.Combine(root, "versions/link"), target);
        Reject(() => ComponentProfileTransaction.VersionDirectory(root, "link"), "Linked instance target rejected");
        Directory.Delete(Path.Combine(root, "versions/link"));
        Directory.CreateSymbolicLink(Path.Combine(root, recovery), target);
        Reject(() => ComponentProfileTransaction.HasRecoveryRecord(root, "journal"), "Linked recovery directory cannot bypass recovery checks");
        Directory.Delete(Path.Combine(root, recovery));
    }
    Console.WriteLine($"Component maintenance regression checks passed: {checks}.");
}
finally { Directory.Delete(root, recursive: true); }

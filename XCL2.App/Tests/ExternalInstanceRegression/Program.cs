using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using XCL2.App.Services;

var root = Path.Combine(Path.GetTempPath(), "xcl-external-instance-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0, skipped = 0;
void Check(bool pass, string name) { checks++; if (!pass) throw new Exception(name); }
void Reject(Action action, string name)
{
    try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or OperationCanceledException)
    { Check(true, name); return; }
    Check(false, name);
}
string Put(string relative, string content)
{
    var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, content); return file;
}
string Dir(string relative) { var path = Path.Combine(root, relative); Directory.CreateDirectory(path); return path; }
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
try
{
    var prism = Dir("Prism/instances/生存实例/minecraft");
    Put("Prism/instances/生存实例/instance.cfg", "[General]\nname=生存游戏 世界\n");
    var mmc = Put("Prism/instances/生存实例/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.20.1"},{"uid":"org.lwjgl3","version":"3.3.1"},{"uid":"net.fabricmc.fabric-loader","version":"0.16.0"}]}""");
    var before = Hash(mmc);
    var a = ExternalInstanceDiscovery.Scan(Path.Combine(root, "Prism"));
    Check(a.Instances.Count == 1 && a.Instances[0].CanImport, "Prism launcher root discovers instance");
    Check(a.Instances[0].Name == "生存游戏 世界" && a.Instances[0].GameDirectory == prism, "Prism name and minecraft game directory");
    Check(a.Instances[0].MinecraftVersion == "1.20.1" && a.Instances[0].Loader == "Fabric" && a.Instances[0].LoaderVersion == "0.16.0", "Exact Prism versions");
    Check(Hash(mmc) == before, "Discovery leaves source metadata unchanged");
    Check(ExternalInstanceDiscovery.Scan(prism).Instances.Count == 1, "Selecting minecraft child discovers enclosing Prism instance");

    Dir("Prism/instances/Second/.minecraft");
    Put("Prism/instances/Second/mmc-pack.json", """{"components":[{"uid":"net.minecraft","version":"1.12.2"},{"uid":"net.minecraftforge","version":"14.23.5.2860"}]}""");
    Check(ExternalInstanceDiscovery.Scan(Path.Combine(root, "Prism/instances")).Instances.Count == 2, "instances container discovers both modern and dot-minecraft layouts");
    Check(ExternalInstanceDiscovery.Scan(Path.Combine(root, "Prism/instances/Second")).Instances.Count == 1, "Single instance never imports siblings");
    Put("Prism/instances/Second/patches/net.minecraft.json", "{}");
    Check(!ExternalInstanceDiscovery.Scan(Path.Combine(root, "Prism/instances/Second")).Instances.Single().CanImport, "Custom patches cannot be silently discarded");
    Put("Prism/instances/Unknown/mmc-pack.json", """{"components":[{"uid":"net.minecraft","version":"1.20.1"},{"uid":"custom.loader","version":"1"}]}""");
    Dir("Prism/instances/Unknown/.minecraft");
    Check(!ExternalInstanceDiscovery.Scan(Path.Combine(root, "Prism/instances/Unknown")).Instances.Single().CanImport, "Unknown components rejected");
    Put("Prism/instances/Bad/mmc-pack.json", "{invalid");
    Check(ExternalInstanceDiscovery.Scan(Path.Combine(root, "Prism/instances")).Warnings.Count > 0, "Malformed metadata reported without losing other instances");
    Put("Prism/instances/Oversized/mmc-pack.json", new string(' ', ArchiveImportSafety.MaximumManifestBytes + 1));
    var boundedScan = ExternalInstanceDiscovery.Scan(Path.Combine(root, "Prism/instances"));
    Check(boundedScan.Warnings.Any(w => w.Contains("4 MB")) && boundedScan.Instances.Any(i => i.CanImport),
        "Oversized or unsafe metadata reports a warning without aborting valid sibling discovery");

    Put("Curse/minecraft/Instances/Pack/minecraftinstance.json", """{"name":"科技包","gameVersion":"1.20.1","baseModLoader":{"name":"forge-47.2.0","minecraftVersion":"1.20.1"}}""");
    var cf = ExternalInstanceDiscovery.Scan(Path.Combine(root, "Curse/minecraft")).Instances.Single();
    Check(cf.CanImport && cf.Loader == "Forge" && cf.LoaderVersion == "47.2.0", "CurseForge loader recognized");
    Check(cf.SharedRoot == Path.Combine(root, "Curse/minecraft/Install"), "CurseForge shared root mapped");
    Put("Curse/minecraft/Instances/Fabric/minecraftinstance.json", """{"name":"Fabric Pack","gameVersion":"1.21.1","baseModLoader":{"name":"fabric-0.16.9-1.21.1"}}""");
    Check(ExternalInstanceDiscovery.Scan(Path.Combine(root, "Curse/minecraft/Instances/Fabric")).Instances.Single().LoaderVersion == "0.16.9", "CurseForge fabric game suffix stripped");

    Put("OldModrinth/profiles/Profile/profile.json", """{"metadata":{"name":"旧Modrinth","game_version":"1.20.1","loader":"fabric","loader_version":{"id":"0.15.11"}}}""");
    var old = ExternalInstanceDiscovery.Scan(Path.Combine(root, "OldModrinth")).Instances.Single();
    Check(old.CanImport && old.Loader == "Fabric" && old.LoaderVersion == "0.15.11", "Old Modrinth nested profile metadata");
    Put("OldModrinth/profiles/Missing/profile.json", """{"metadata":{"name":"缺版本","loader":"forge"}}""");
    Check(!ExternalInstanceDiscovery.Scan(Path.Combine(root, "OldModrinth/profiles/Missing")).Instances.Single().CanImport, "Missing metadata never represented as vanilla ready");

    var app = Dir("ModrinthApp"); Dir("ModrinthApp/profiles/One"); Dir("ModrinthApp/profiles/Two");
    var db = Path.Combine(app, "app.db");
    using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString()))
    {
        connection.Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE profiles(path TEXT, name TEXT, game_version TEXT, mod_loader TEXT, mod_loader_version TEXT);" +
            "INSERT INTO profiles VALUES ('One','数据库实例','1.21.1','fabric','0.16.9'), ('Two','原版','1.20.1','vanilla',NULL), ('../escape','越界','1.20.1','vanilla',NULL);";
        cmd.ExecuteNonQuery();
    }
    SqliteConnection.ClearAllPools(); before = Hash(db);
    var modern = ExternalInstanceDiscovery.Scan(app);
    Check(modern.Instances.Count == 2 && modern.Instances.All(c => c.CanImport), "Modern Modrinth SQLite read with escaping row excluded");
    Check(ExternalInstanceDiscovery.Scan(Path.Combine(app, "profiles/One")).Instances.Single().Name == "数据库实例", "Database single-instance selection scoped correctly");
    Check(Hash(db) == before, "Read-only SQLite database bytes unchanged");
    Check(!Directory.Exists(Path.Combine(root, "escape")), "Metadata traversal creates nothing");

    Put("OldModrinth/profiles/UnknownLoader/profile.json", "{\"metadata\":{\"game_version\":\"1.20.1\"}}");
    Check(!ExternalInstanceDiscovery.Scan(Path.Combine(root, "OldModrinth/profiles/UnknownLoader")).Instances.Single().CanImport, "Missing loader is not assumed to be vanilla");
    var largeApp = Dir("LargeModrinth"); Dir("LargeModrinth/profiles/P2100");
    using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(largeApp, "app.db") }.ToString()))
    {
        connection.Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE profiles(path TEXT, name TEXT, game_version TEXT, mod_loader TEXT, mod_loader_version TEXT);" +
            "WITH RECURSIVE nums(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM nums WHERE x<2100) INSERT INTO profiles SELECT 'P'||x,'Pack'||x,'1.20.1','vanilla',NULL FROM nums;";
        cmd.ExecuteNonQuery();
    }
    SqliteConnection.ClearAllPools();
    Check(ExternalInstanceDiscovery.Scan(Path.Combine(largeApp, "profiles/P2100")).Instances.Single().CanImport, "Single database profile outside first 2000 still discoverable");
    Check(ExternalInstanceDiscovery.Scan(largeApp).Warnings.Any(w => w.Contains("2000")), "Oversized database scan is bounded and reported");

    var source = Dir("content");
    Put("content/mods/mod.jar", "MOD"); Put("content/saves/World/level.dat", "WORLD"); Put("content/options.txt", "lang:zh_cn");
    Put("content/resourcepacks/pack.zip", "PACK"); Put("content/custompack/scripts/main.js", "custom");
    Put("content/launcher_accounts.json", "PRIVATE"); Put("content/libraries/old.jar", "OLD"); Put("content/natives/old.dll", "OLD");
    var target = Path.Combine(root, "copy"); ExternalInstanceContent.Copy(source, target);
    Check(File.ReadAllText(Path.Combine(target, "saves/World/level.dat")) == "WORLD", "World data copied");
    Check(File.Exists(Path.Combine(target, "mods/mod.jar")) && File.Exists(Path.Combine(target, "custompack/scripts/main.js")), "Mods and custom game data copied");
    Check(File.ReadAllText(Path.Combine(target, "options.txt")) == "lang:zh_cn" && File.Exists(Path.Combine(target, "resourcepacks/pack.zip")), "Options and resource packs copied");
    Check(!File.Exists(Path.Combine(target, "launcher_accounts.json")) && !Directory.Exists(Path.Combine(target, "libraries")) && !Directory.Exists(Path.Combine(target, "natives")), "Launcher secrets and stale binary caches excluded");
    Check(File.ReadAllText(Path.Combine(source, "saves/World/level.dat")) == "WORLD", "Source world remains intact");
    Reject(() => ExternalInstanceContent.Copy(source, target), "Existing destination is not overwritten");
    Reject(() => ExternalInstanceContent.Copy(source, Path.Combine(source, "copy")), "Recursive self-copy rejected");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    Reject(() => ExternalInstanceContent.Copy(source, Path.Combine(root, "canceled"), ct: canceled.Token), "Cancellation before writes");
    Check(!Directory.Exists(Path.Combine(root, "canceled")), "Canceled copy creates no destination");
    Reject(() => ExternalInstanceDiscovery.Scan(app, canceled.Token), "Scan cancellation");
    var invalid = ExternalInstanceDiscovery.Validate(new("Unsafe", "Test", source, source, "../escape", null, null, null));
    Check(!invalid.CanImport, "Metadata version path traversal rejected");
    var isolationRoot = Dir("isolated-root");
    var otherRoot = Dir("other-root");
    var overrides = new Dictionary<string, bool> { ["SameName"] = false };
    Put("isolated-root/versions/SameName/xcl/settings.json", "{\"IsolateVersion\":true}");
    Put("isolated-root/versions/SameName/xcl-import-origin.json", "{}");
    Check(InstanceIsolationPolicy.Resolve(isolationRoot, "SameName", false, overrides), "Imported local isolation beats disabled global default");
    Check(!InstanceIsolationPolicy.Resolve(otherRoot, "SameName", false, overrides), "Same name in another root remains unchanged");
    Put("isolated-root/versions/SameName/xcl/settings.json", "{broken");
    Check(InstanceIsolationPolicy.Resolve(isolationRoot, "SameName", false, overrides), "Corrupt optional settings cannot detach imported worlds");
    Put("isolated-root/versions/SameName/xcl/settings.json", "{\"IsolateVersion\":false}");
    Check(!InstanceIsolationPolicy.Resolve(isolationRoot, "SameName", true, overrides), "Explicit local disable still respected");
    Reject(() => InstanceIsolationPolicy.Resolve(isolationRoot, "../escape", false, overrides), "Isolation read validates instance id");
    Check(!Directory.Exists(Path.Combine(otherRoot, "versions")), "Isolation read does not create settings or directories");
    try
    {
        var links = Dir("linked-content"); Directory.CreateSymbolicLink(Path.Combine(links, "saves"), source);
        Reject(() => ExternalInstanceContent.Copy(links, Path.Combine(root, "linked-copy")), "Symlink game data rejected");
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
    { skipped++; Console.WriteLine("SKIP symlink creation unavailable: " + ex.GetType().Name); }
    Console.WriteLine($"PASS {checks} external instance checks; skipped {skipped}");
}
finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch { } }

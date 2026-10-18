using System.Text.Json;
using XCL2.App.Services;

var passed = 0;
void Check(bool success, string name)
{
    if (!success) throw new InvalidOperationException(name);
    passed++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (InvalidDataException) { passed++; return; }
    throw new InvalidOperationException(name);
}

const string release10 = "${auth_player_name} ${auth_session} --gameDir ${game_directory} --assetsDir ${game_assets}";
const string modernLegacy = "--username ${auth_player_name} --session ${auth_session} --gameDir ${game_directory}";
Check(LegacyMinecraftCompatibility.UsesPositionalIdentity(release10), "official 1.0 keeps positional identity");
Check(LegacyMinecraftCompatibility.UsesPositionalIdentity(" \t" + release10 + " --tweakClass net.minecraft.launchwrapper.AlphaVanillaTweaker"), "Alpha tweaker keeps positional identity");
Check(!LegacyMinecraftCompatibility.UsesPositionalIdentity(modernLegacy), "1.6+ named identity keeps existing modern path");
Check(!LegacyMinecraftCompatibility.UsesPositionalIdentity(null), "no absent-template positional detection");
Check(!LegacyMinecraftCompatibility.UsesPositionalIdentity("${auth_player_name} --session ${auth_session}"), "do not infer positional mode from mere presence of auth placeholders");
foreach (var (version, java) in new (string, int)[]
{
    ("1.0", 8), ("1.5.2", 8), ("1.7.10", 8), ("1.16.5", 8), ("1.17", 16), ("1.17.1", 16),
    ("1.18.2", 17), ("1.20.1", 17), ("1.20.4", 17), ("1.20.5", 21), ("1.21.1", 21), ("26.2", 25)
}) Check(LegacyMinecraftCompatibility.EstimateJavaMajor(version) == java, "fallback Java for " + version);
Check(LegacyMinecraftCompatibility.EstimateJavaMajor("24w14a") == null, "unknown snapshot is not assigned an arbitrary Java");
Check(LegacyMinecraftCompatibility.EstimateJavaMajor("renamed-instance") == null, "renamed folder does not invent requirement");
Check(LegacyMinecraftCompatibility.BuildAuthSession("abcd-ef01", "token-value", true) == "token:token-value:abcdef01", "legacy authenticated session includes token and compact UUID");
Check(LegacyMinecraftCompatibility.BuildAuthSession("abcd-ef01", "token-value", false) == "0", "offline session is zero");
Check(LegacyMinecraftCompatibility.BuildAuthSession("abcd", "0", true) == "0", "missing online token is not fabricated");

byte[] Pe(ushort machine)
{
    var bytes = new byte[256];
    using var writer = new BinaryWriter(new MemoryStream(bytes));
    writer.Write((ushort)0x5a4d); writer.BaseStream.Position = 0x3c; writer.Write(128u);
    writer.BaseStream.Position = 128; writer.Write(0x00004550u); writer.Write(machine);
    return bytes;
}
Check(JavaExecutableArchitecture.ReadBitness(new MemoryStream(Pe(0x14c))) == 32, "x86 PE Java is 32 bit even on 64 bit host");
Check(JavaExecutableArchitecture.ReadBitness(new MemoryStream(Pe(0x8664))) == 64, "AMD64 PE Java");
Check(JavaExecutableArchitecture.ReadBitness(new MemoryStream(Pe(0xaa64))) == 64, "ARM64 PE Java");
Check(JavaExecutableArchitecture.ReadBitness(new MemoryStream(Pe(0))) == null, "unknown PE machine does not imply 64 bit");
Check(JavaExecutableArchitecture.ReadBitness(new MemoryStream(new byte[7])) == null, "short executable safely ignored");
var corruptOffset = Pe(0x14c); Array.Fill(corruptOffset, (byte)0xff, 0x3c, 4);
Check(JavaExecutableArchitecture.ReadBitness(new MemoryStream(corruptOffset)) == null, "invalid PE offset is not read");

var temporary = Path.Combine(Path.GetTempPath(), "XCL2-legacy-launch-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var indexes = Path.Combine(temporary, "assets", "indexes"); Directory.CreateDirectory(indexes);
    var hash = new string('a', 40);
    var objectDir = Path.Combine(temporary, "assets", "objects", "aa"); Directory.CreateDirectory(objectDir);
    File.WriteAllText(Path.Combine(objectDir, hash), "audio fixture");
    void Index(string id, bool virtualFiles, bool resourceFiles, string name = "sound/test.ogg")
        => File.WriteAllText(Path.Combine(indexes, id + ".json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["virtual"] = virtualFiles, ["map_to_resources"] = resourceFiles,
            ["objects"] = new Dictionary<string, object> { [name] = new { hash, size = 13 } }
        }));
    Index("pre-1.6", false, true);
    var isolated = Path.Combine(temporary, "versions", "renamed 旧版本");
    LegacyAssetLayout.PrepareForLaunch(temporary, isolated, "pre-1.6");
    Check(File.ReadAllText(Path.Combine(isolated, "resources", "sound", "test.ogg")) == "audio fixture", "pre-1.6 resources follow isolated game directory");
    Check(!Directory.Exists(Path.Combine(temporary, "resources")), "isolated legacy launch does not write shared resource directory");
    Index("legacy", true, false);
    LegacyAssetLayout.PrepareForLaunch(temporary, isolated, "legacy");
    var virtualRoot = Path.Combine(temporary, "assets", "virtual", "legacy");
    Check(LegacyAssetLayout.GetGameAssetsDirectory(temporary, "legacy") == virtualRoot, "legacy game_assets points to named virtual assets");
    Check(File.ReadAllText(Path.Combine(virtualRoot, "sound", "test.ogg")) == "audio fixture", "legacy named assets actually materialized");
    File.WriteAllText(Path.Combine(virtualRoot, "sound", "test.ogg"), "bad");
    LegacyAssetLayout.PrepareForLaunch(temporary, isolated, "legacy");
    Check(File.ReadAllText(Path.Combine(virtualRoot, "sound", "test.ogg")) == "audio fixture", "truncated mapped resource repaired from object cache");
    Index("modern", false, false);
    LegacyAssetLayout.PrepareForLaunch(temporary, isolated, "modern");
    Check(!Directory.Exists(Path.Combine(temporary, "assets", "virtual", "modern")), "modern indices cause no legacy copy");
    Index("unsafe", true, false, "../../escape");
    Reject(() => LegacyAssetLayout.PrepareForLaunch(temporary, isolated, "unsafe"), "asset traversal rejected");
    Reject(() => LegacyAssetLayout.GetGameAssetsDirectory(temporary, "../escape"), "index traversal rejected");
    Check(JavaExecutableArchitecture.TryReadBitness(Path.Combine(temporary, "missing.exe")) == null, "missing executable returns unknown bitness");
}
finally { Directory.Delete(temporary, recursive: true); }
Console.WriteLine($"PASS {passed} legacy launch checks");

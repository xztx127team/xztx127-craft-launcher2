using XCL2.App.Models;
using XCL2.App.Services;

var assertions = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); assertions++; }
var root = Path.Combine(Path.GetTempPath(), "xcl-java-discovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var environmentName = "XCL2_JAVA_DISCOVERY_REGRESSION_ROOT";
try
{
    var bin = Path.Combine(root, "a java", "bin"); Directory.CreateDirectory(bin);
    File.WriteAllText(Path.Combine(bin, "javaw.exe"), "fixture");
    Environment.SetEnvironmentVariable(environmentName, bin);
    var found = JavaEnvironmentPaths.FindExecutables($";\"{bin}\";;%{environmentName}%;\"{bin}\";relative;").ToArray();
    Check(found.Length == 1 && found[0] == Path.Combine(bin, "javaw.exe"), "quoted, expanded and duplicate PATH entries resolve once");
    Check(JavaEnvironmentPaths.NormalizeDirectory(" \"" + bin + "\" ") == bin, "quoted JAVA_HOME is normalized");
    Check(JavaEnvironmentPaths.NormalizeDirectory("relative") == null && JavaEnvironmentPaths.NormalizeDirectory("\"\"") == null, "empty/relative PATH does not probe working directory");
    Check(JavaEnvironmentPaths.NormalizeDirectory("\"" + bin) == null, "unmatched quote is not accepted");
    Check(JavaVersionOutputPolicy.ParseMajorValue("1.8.0_412") == 8 && JavaVersionOutputPolicy.ParseMajorValue("21.0.5+11-LTS") == 21, "extracted Java versions parse without invented banner");
    Check(JavaVersionOutputPolicy.ParseMajorValue("garbage21") == null && JavaVersionOutputPolicy.ParseMajorValue("21\njava version") == null, "malformed extracted version is rejected");
    Check(JavaVersionOutputPolicy.ParseMajor("notice version \"21\"") == null, "process banner validation remains strict");
    var entries = new List<InstalledJava>
    {
        new() { Id = "unchanged-id", JavawPath = "updated", Name = "My runtime", MajorVersion = 8, Priority = 7 },
        new() { Id = "missing-id", JavawPath = "missing", Name = "Java 17", MajorVersion = 17 },
        new() { Id = "offline-id", JavawPath = "unscanned", Name = "External disk", MajorVersion = 21 },
        new() { Id = "default-id", JavawPath = "default", Name = "Java 17", MajorVersion = 17 }
    };
    var changed = JavaRegistrationRefreshPolicy.RefreshExisting(entries,
        new (string, string?)[] { ("updated", "25.0.1"), ("default", "21.0.5") }, path => path != "missing");
    Check(changed == 3 && entries[0].MajorVersion == 25, "in-place upgrade replaces cached major");
    Check(entries[0].Id == "unchanged-id" && entries[0].Name == "My runtime" && entries[0].Priority == 7, "refresh preserves instance reference and custom name/priority");
    Check(entries[1].MajorVersion == null && entries[1].Id == "missing-id", "removed executable loses stale metadata but retains reference");
    Check(entries[2].MajorVersion == 21, "a runtime outside discovery results is not assumed removed");
    Check(entries[3].Name == "Java 21", "generated default name follows new major");
    var java = Path.Combine(bin, "javaw.exe");
    var release = Path.Combine(Directory.GetParent(bin)!.FullName, "release");
    File.WriteAllText(release, "JAVA_VERSION=\"21.0.5\"\r\n");
    Check(JavaRuntimeCompatibility.FileEncodingArgument(java, new[] { "org.lwjgl.lwjgl:lwjgl:2.9.4-nightly-20150209" }) == "-Dfile.encoding=COMPAT", "LWJGL 2 with JDK18+ retains native path encoding");
    Check(JavaRuntimeCompatibility.FileEncodingArgument(java, new[] { "org.lwjgl:lwjgl:3.3.3" }) == "-Dfile.encoding=UTF-8", "LWJGL 3 does not get legacy encoding workaround");
    var aliasDirectory = Path.Combine(root, "javapath"); Directory.CreateDirectory(aliasDirectory);
    try
    {
        var alias = Path.Combine(aliasDirectory, "javaw.exe"); File.CreateSymbolicLink(alias, java);
        Check(JavaRuntimeCompatibility.FileEncodingArgument(alias, new[] { "org.lwjgl.lwjgl:lwjgl:2.9.4" }) == "-Dfile.encoding=COMPAT", "javapath symlink uses real runtime release metadata");
    }
    catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
    { Console.WriteLine("SKIP symlink fixture: Windows symbolic-link privilege unavailable"); }
    File.WriteAllText(release, "JAVA_VERSION=\"1.8.0_412\"\n");
    Check(JavaRuntimeCompatibility.FileEncodingArgument(java, new[] { "org.lwjgl.lwjgl:lwjgl:2.9.4" }) == "-Dfile.encoding=UTF-8", "JDK8 does not receive the unsupported COMPAT option");
    if (OperatingSystem.IsWindows())
        Check(JavaEnvironmentPaths.NormalizeDirectory(@"C:relative") == null && JavaEnvironmentPaths.NormalizeDirectory(@"\root-relative") == null, "drive-relative Windows paths never use an implicit working directory");
    var candidates = new (string, int?, string?)[] { ("old8", 8, "amd64"), ("wrongArm64", 21, "aarch64"), ("wrong32", 21, "x86"), ("right21", 21, "amd64"), ("unknown", 21, null) };
    Check(JavaEnvironmentPaths.SelectRuntime(candidates, 21, "amd64") == "right21", "MSI follow-up cannot return unrelated existing Java or wrong architecture");
    Check(JavaEnvironmentPaths.SelectRuntime(candidates, 25, "amd64") == null, "missing requested runtime is reported instead of silent fallback");
}
finally { Environment.SetEnvironmentVariable(environmentName, null); Directory.Delete(root, true); }
Console.WriteLine($"PASS {assertions} Java discovery/refresh assertions");

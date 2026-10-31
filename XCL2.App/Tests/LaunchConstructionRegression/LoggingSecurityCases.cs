using System.Reflection;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class LoggingSecurityCases
{
    internal static void Run(string root)
    {
        foreach (var (version, expectedId) in new[] { ("2.0-beta9", "client-1.7.xml"), ("2.8.1", "client-1.12.xml"), ("2.14.1", "client-1.12.xml") })
        {
            var parentId = "logging-" + version;
            var libraryPath = "org/apache/logging/log4j/log4j-core/" + version + "/log4j-core.jar";
            var parent = new VersionDetail
            {
                Id = parentId, MainClass = "example.Main", Libraries = new() { new LibraryEntry
                {
                    Name = "org.apache.logging.log4j:log4j-core:" + version,
                    Downloads = new LibraryDownloads { Artifact = new DownloadArtifact { Path = libraryPath } }
                } }
            };
            WriteVersion(root, parentId, parent, true);
            var library = Path.Combine(root, "libraries", libraryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(library)!); File.WriteAllText(library, "library fixture");
            WriteVersion(root, parentId + "-loader", new VersionDetail { Id = parentId + "-loader", InheritsFrom = parentId }, false);
            var options = new LauncherService.LaunchOptions
            {
                MinecraftDir = root, VersionId = parentId + "-loader",
                CustomJvmArgs = "-Dlog4j.configurationFile=https://unsafe.example/config.xml -Dlog4j2.configurationFile=old.xml -Dlog4j2.formatMsgNoLookups=false"
            };
            var policy = LauncherLoggingPolicy.Resolve(parent)!;
            Require(policy.File!.Id == expectedId, "Wrong Log4j mitigation selected for " + version);
            var destination = LauncherLoggingPolicy.PathFor(root, policy);
            if (File.Exists(destination)) File.Delete(destination);
            Reject(() => Preview(options));
            Require(new LauncherService().CheckMissingLibraries(root, options.VersionId).Any(x => x.Contains("Log4j")),
                "Preflight failed to offer repair for the missing security configuration.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "LoggingFixtures", expectedId), destination, true);
            var arguments = Preview(options);
            var configurationArgument = "-Dlog4j.configurationFile=" + Path.GetFullPath(destination);
            Require(arguments.Contains(configurationArgument) && arguments.IndexOf(configurationArgument) < arguments.IndexOf("-cp"),
                "Verified inherited logging configuration is absent from the JVM command.");
            Require(arguments.Count(LauncherLoggingPolicy.IsProtectedArgument) == 2 &&
                arguments.Contains("-Dlog4j2.formatMsgNoLookups=true") && !arguments.Any(x => x.Contains("unsafe.example")),
                "Custom parameters disabled the enforced Log4j mitigation.");
            options.JavaPath = "java.exe";
            var exported = File.ReadAllText(new LauncherService().ExportLaunchScript(options));
            var encoded = exported.Split(" -EncodedCommand ", StringSplitOptions.None)[1].Split('\r', '\n')[0];
            var verifier = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
            Require(verifier.Contains(policy.File.Sha1, StringComparison.OrdinalIgnoreCase) && verifier.Contains("ComputeHash"),
                "Offline launch script omitted the expected logging hash check.");
            var broken = File.ReadAllBytes(destination); broken[broken.Length - 1] ^= 1; File.WriteAllBytes(destination, broken);
            Reject(() => Preview(options));
            if (OperatingSystem.IsWindows())
            {
                var receipt = Path.Combine(root, "unsafe-logging-process-must-not-start.json");
                var script = Path.Combine(root, "tampered-logging-probe.bat");
                LaunchScriptExportPolicy.Write(script, LaunchScriptExportPolicy.Offline(Environment.ProcessPath!, root,
                    LaunchScriptPrivacyCases.ProbeArguments(receipt, new[] { configurationArgument }), false));
                Require(LaunchScriptPrivacyCases.RunBatch(script, root) != 0, "Batch accepted a tampered logging XML.");
                Require(!File.Exists(receipt), "Batch launched its process after logging XML was tampered with.");
            }
            File.Copy(Path.Combine(AppContext.BaseDirectory, "LoggingFixtures", expectedId), destination, true);
        }

        var mixedCore = new VersionDetail { Libraries = new()
        {
            new LibraryEntry { Name = "org.apache.logging.log4j:log4j-core:2.22.1" },
            new LibraryEntry { Name = "org.apache.logging.log4j:log4j-core:2.0-beta9" }
        } };
        Require(LauncherLoggingPolicy.Resolve(mixedCore)?.File?.Id == "client-1.7.xml", "A newer first library hid a vulnerable second library.");
        foreach (var relative in new[] { "../outside.jar", @"\\attacker.invalid\share\outside.jar", "C:/outside.jar" })
        {
            var unsafeLibrary = new VersionDetail { Id = "unsafe-library", MainClass = "example.Main", Libraries = new()
            { new LibraryEntry { Name = "fixture:library:1", Downloads = new LibraryDownloads { Artifact = new DownloadArtifact { Path = relative } } } } };
            WriteVersion(root, unsafeLibrary.Id, unsafeLibrary, true);
            Reject(() => new LauncherService().CheckMissingLibraries(root, unsafeLibrary.Id));
            Reject(() => Preview(new LauncherService.LaunchOptions { MinecraftDir = root, VersionId = unsafeLibrary.Id }));
        }
        WriteVersion(root, "unsafe-assets", new VersionDetail { Id = "unsafe-assets", MainClass = "example.Main", Assets = "../../escape" }, true);
        Reject(() => Preview(new LauncherService.LaunchOptions { MinecraftDir = root, VersionId = "unsafe-assets" }));
        Reject(() => Preview(new LauncherService.LaunchOptions { MinecraftDir = root, VersionId = "../outside-instance" }));

        var modern = new VersionDetail
        {
            Id = "logging-modern", MainClass = "example.Main", Logging = new LoggingConfiguration
            {
                Client = new LoggingClientConfiguration { Argument = LauncherLoggingPolicy.ArgumentTemplate, Type = "log4j2-xml",
                    File = new LoggingFile { Id = "client-1.21.2.xml", Size = 1073,
                        Sha1 = "39384bd14c0606d812afec88d8aff595b2587dd9",
                        Url = "https://piston-data.mojang.com/v1/objects/39384bd14c0606d812afec88d8aff595b2587dd9/client-1.21.2.xml" } }
            }
        };
        WriteVersion(root, modern.Id, modern, true);
        WriteVersion(root, "logging-child", new VersionDetail { Id = "logging-child", InheritsFrom = modern.Id }, false);
        var resolution = VersionInheritanceResolver.Load(root, "logging-child", FolderService.ResolveVersionJson);
        Require(resolution.Effective.Logging?.Client?.File?.Id == "client-1.21.2.xml" &&
            VersionRepairPlan.Create(resolution).Libraries.Logging?.Client?.File?.Id == "client-1.21.2.xml",
            "Inheritance or repair discarded official logging metadata.");
        var modernConfig = LauncherLoggingPolicy.Resolve(resolution.Effective)!;
        var modernPath = LauncherLoggingPolicy.PathFor(root, modernConfig);
        Directory.CreateDirectory(Path.GetDirectoryName(modernPath)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "LoggingFixtures", modernConfig.File!.Id), modernPath, true);
        Require(Preview(new LauncherService.LaunchOptions { MinecraftDir = root, VersionId = "logging-child" })
            .Contains("-Dlog4j.configurationFile=" + modernPath), "Modern official logging metadata was ignored.");
        foreach (var invalidId in new[] { "../outside.xml", "..\\outside.xml", "C:outside.xml", "NUL.xml" })
        {
            modernConfig.File.Id = invalidId;
            Reject(() => LauncherLoggingPolicy.PathFor(root, modernConfig));
        }
        modernConfig.File.Id = "safe.xml";
        modernConfig.Argument = "-javaagent=${path}";
        Reject(() => LauncherLoggingPolicy.Validate(modernConfig));
        modernConfig.Argument = LauncherLoggingPolicy.ArgumentTemplate;
        modernConfig.File.Url = "https://attacker.example/config.xml";
        Reject(() => LauncherLoggingPolicy.Validate(modernConfig));
        foreach (var key in new[] { "-Dlog4j.configurationFile", "-Dlog4j2.configurationFile", "-Dlog4j2.formatMsgNoLookups" })
            Require(LauncherLoggingPolicy.IsProtectedArgument(key) && LauncherLoggingPolicy.IsProtectedArgument(key + "=") &&
                !LauncherLoggingPolicy.IsProtectedArgument(key + "Unrelated=value"), "Security property boundary is incorrect.");
        Console.WriteLine("Logging security: legacy configurations, integrity enforcement, inheritance, argument overrides and unsafe metadata checked.");
    }

    private static void WriteVersion(string root, string id, VersionDetail detail, bool jar)
    {
        var directory = Path.Combine(root, "versions", id); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(detail));
        if (jar) File.WriteAllText(Path.Combine(directory, id + ".jar"), "client fixture");
    }
    private static List<string> Preview(LauncherService.LaunchOptions options)
    {
        try
        {
            var result = ((string, List<string>))typeof(LauncherService).GetMethod("BuildArguments", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(new LauncherService(), new object[] { options, true })!;
            return result.Item2;
        }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or MissingLibrariesException) { return; }
        throw new Exception("Unsafe or missing logging configuration was accepted.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}

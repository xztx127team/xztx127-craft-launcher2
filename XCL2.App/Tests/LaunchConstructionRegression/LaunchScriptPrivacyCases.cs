using System.Diagnostics;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class LaunchScriptPrivacyCases
{
    internal static void Run(string root)
    {
        var launcher = new LauncherService();
        foreach (var type in new[] { AccountType.Microsoft, AccountType.AuthServer })
        {
            var options = new LauncherService.LaunchOptions
            {
                MinecraftDir = root, VersionId = "script-auth-" + type,
                // Missing metadata and an invalid Java path must not prevent removal
                // of the stale session script. Neither is used for a notice export.
                JavaPath = "private-java-path\r\nMUST_NOT_EXECUTE",
                CustomJvmArgs = "-Dcredential=private_custom_fixture",
                Account = new Account { Type = type, Username = "private_account_fixture", MinecraftAccessToken = "private_access_fixture" }
            };
            var path = Path.Combine(root, "versions", options.VersionId, "xcl", "launch.bat");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "--accessToken stale_access_fixture");
            Require(launcher.ExportLaunchScript(options) == path, "Authenticated notice path changed.");
            var script = File.ReadAllText(path);
            Require(script == LaunchScriptExportPolicy.ProtectedNotice(), "Authenticated export was not a fixed safe notice.");
            foreach (var secret in new[] { "private_account_fixture", "private_access_fixture", "stale_access_fixture", "private_custom_fixture", "private-java-path" })
                Require(!script.Contains(secret), "Authenticated export persisted private data.");
            Require(File.ReadAllBytes(path)[0] == (byte)'@', "Batch starts with a BOM instead of its echo command.");
        }

        VerifyExplicitTokenExport(root, launcher);
        VerifyTokenExportConsent();

        var id = "script-offline";
        var directory = Path.Combine(root, "versions", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(new { id, mainClass = "example.Main", assets = "fixture" }));
        File.WriteAllText(Path.Combine(directory, id + ".jar"), "client fixture");
        var offline = new LauncherService.LaunchOptions
        {
            MinecraftDir = root, VersionId = id, JavaPath = "java.exe",
            Account = new Account { Type = AccountType.Offline, Username = "A&B|C%PATH%!D", MinecraftAccessToken = "unused_offline_token_fixture" }
        };
        var exported = launcher.ExportLaunchScript(offline);
        var content = File.ReadAllText(exported);
        Require(content.Contains("\"A&B|C%%PATH%%!D\"") && content.Contains("setlocal DisableDelayedExpansion"),
            "CMD metacharacters were not quoted/escaped on the actual export path.");
        Require(!content.Contains("unused_offline_token_fixture"), "Offline export accidentally included a cached online token.");
        foreach (var unsafeLabel in new[] { "first\r\nSCRIPT_INJECTION_FIXTURE", "first\"&SCRIPT_INJECTION_FIXTURE" })
        {
            offline.VersionTypeLabel = unsafeLabel;
            try { launcher.ExportLaunchScript(offline); throw new Exception("Unsafe batch data was accepted."); }
            catch (InvalidDataException) { }
            Require(File.ReadAllText(exported) == LaunchScriptExportPolicy.ProtectedNotice(),
                "An export failure retained an older executable/session script.");
        }
        if (OperatingSystem.IsWindows()) VerifyCmdRoundTrip(root);
        Console.WriteLine("Launch script privacy: default protection, explicit token exports, offline quoting, and unsafe input checked.");
    }

    private static void VerifyExplicitTokenExport(string root, LauncherService launcher)
    {
        foreach (var type in new[] { AccountType.Microsoft, AccountType.AuthServer })
        {
            var id = "script-explicit-" + type;
            var directory = Path.Combine(root, "versions", id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(new { id, mainClass = "example.Main", assets = "fixture" }));
            File.WriteAllText(Path.Combine(directory, id + ".jar"), "client fixture");
            var token = "explicit_access_fixture_" + type + "%literal!tail";
            var account = new Account
            {
                Type = type,
                Username = "explicit_player_fixture",
                Uuid = "12345678-90ab-cdef-1234-567890abcdef",
                MinecraftAccessToken = token,
                MsRefreshToken = "never_export_refresh_fixture",
                AuthServerClientToken = "never_export_client_fixture"
            };
            var options = new LauncherService.LaunchOptions
                { MinecraftDir = root, VersionId = id, JavaPath = "java.exe", Account = account };
            var defaultPath = launcher.ExportLaunchScript(options);
            var defaultContent = File.ReadAllText(defaultPath);
            var explicitPath = launcher.ExportLaunchScript(options, includeAccessToken: true);
            Require(explicitPath == Path.Combine(directory, "xcl", "launch-with-token.bat") && explicitPath != defaultPath,
                "The explicit token export did not use its separate filename.");
            var script = File.ReadAllText(explicitPath);
            Require(script.Contains("\"--accessToken\" " + LaunchScriptExportPolicy.Quote(token, nativeArgument: true)),
                "The explicitly selected access token was missing or incorrectly quoted.");
            Require(script.Contains("\"explicit_player_fixture\"") && script.Contains("\"1234567890abcdef1234567890abcdef\""),
                "The authenticated export lost its player identity.");
            foreach (var secret in new[] { account.MsRefreshToken!, account.AuthServerClientToken! })
                Require(!script.Contains(secret), "The explicit export included a credential that is not needed to launch.");
            Require(File.ReadAllText(defaultPath) == defaultContent,
                "Explicit token export changed the default protected script.");

            // The choice belongs to this single operation, never to the options/account
            // or a launcher-wide setting reused by the automatic export path.
            Require(launcher.ExportLaunchScript(options) == defaultPath && File.ReadAllText(defaultPath) == LaunchScriptExportPolicy.ProtectedNotice(),
                "Explicit token consent leaked into a later default export.");
            Require(File.ReadAllText(explicitPath) == script,
                "A default export unexpectedly overwrote the separate explicitly requested script.");
            Require(account.MinecraftAccessToken == token && account.Type == type,
                "Script export changed the selected account or its session.");

            options.VersionTypeLabel = "first\r\nSCRIPT_INJECTION_FIXTURE";
            try { launcher.ExportLaunchScript(options, includeAccessToken: true); throw new Exception("Unsafe explicit batch data was accepted."); }
            catch (InvalidDataException) { }
            Require(File.ReadAllText(explicitPath) == LaunchScriptExportPolicy.ProtectedNotice(),
                "A failed explicit export left the old access token on disk.");
            Require(File.ReadAllText(defaultPath) == defaultContent,
                "A failed explicit export modified the default script.");

            options.VersionTypeLabel = null;
            foreach (var missingToken in new string?[] { null, "", " \t " })
            {
                // Seed an earlier successful export to verify replacement happens even
                // when the newly selected account no longer has an exportable token.
                File.WriteAllText(explicitPath, script);
                account.MinecraftAccessToken = missingToken;
                var rejected = false;
                try { launcher.ExportLaunchScript(options, includeAccessToken: true); }
                catch (InvalidOperationException) { rejected = true; }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "An authenticated explicit export accepted an absent access token.");
                Require(File.ReadAllText(explicitPath) == LaunchScriptExportPolicy.ProtectedNotice(),
                    "Missing-token rejection retained a previous exported session.");
            }
        }
    }

    private static void VerifyTokenExportConsent()
    {
        foreach (var first in new bool?[] { false, null, true })
        foreach (var second in new bool?[] { false, null, true })
        {
            var prompts = new List<(string Message, string Title, string Button)>();
            Task<bool?> Confirm(string message, string title, string button)
            {
                prompts.Add((message, title, button));
                return Task.FromResult(prompts.Count == 1 ? first : second);
            }
            var accepted = LaunchScriptExportPolicy.ConfirmTokenExportAsync(Confirm).GetAwaiter().GetResult();
            Require(accepted == (first == true && second == true), "Token export bypassed one of its two confirmations.");
            Require(prompts.Count == (first == true ? 2 : 1), "Cancellation did not stop token export confirmations.");
            Require(prompts[0].Title.Contains("1/2") && prompts[0].Message.Contains("明文"),
                "The first warning did not explain plaintext token exposure.");
            if (prompts.Count == 2)
                Require(prompts[1].Title.Contains("2/2") && prompts[1].Message.Contains("勿分享") && prompts[1].Button == "确认导出令牌",
                    "The final confirmation did not explain sharing risks or explicit consent.");
        }
    }

    private static void VerifyCmdRoundTrip(string root)
    {
        var captured = Path.Combine(root, "batch-captured-arguments.json");
        var injected = Path.Combine(root, "BATCH_INJECTION_MUST_NOT_EXIST.txt");
        var values = new[] { "two words", "a&echo injected>" + injected, "a|b", "%PATH%", "!TEMP!", "a^b", "(a<b)", @"ends-in-\", "" };
        var script = Path.Combine(root, "script-argument-probe.bat");
        LaunchScriptExportPolicy.Write(script, LaunchScriptExportPolicy.Offline(Environment.ProcessPath!, root,
            ProbeArguments(captured, values), false));
        Require(RunBatch(script, root) == 0, "The batch argument fixture exited unsuccessfully.");
        Require(!File.Exists(injected), "A quoted batch argument executed as a CMD command.");
        Require(File.Exists(captured), "The batch did not start the managed argument fixture.");
        var actual = JsonSerializer.Deserialize<string[]>(File.ReadAllText(captured))!;
        Require(actual.SequenceEqual(values), "Batch parsing changed literal argument bytes.");
    }

    internal static string[] ProbeArguments(string receipt, IEnumerable<string> values)
    {
        var prefix = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            prefix.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        prefix.Add("--script-argument-probe"); prefix.Add(receipt); prefix.AddRange(values);
        return prefix.ToArray();
    }

    internal static int RunBatch(string script, string directory)
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var start = new ProcessStartInfo(cmd) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = directory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new Exception("Unable to run the batch regression fixture.");
        process.StandardInput.WriteLine(); process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); throw new Exception("Batch regression fixture timed out."); }
        Task.WaitAll(stdout, stderr);
        return process.ExitCode;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}

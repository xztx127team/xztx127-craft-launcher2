using System.Diagnostics;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

// Real launch middleware, Account, LaunchOptions and process model; no plugin DLL is loaded.
if (args.Contains("--privacy-child")) { Console.ReadLine(); return; }
const string secret = "test-only-private-value-4fd9";
var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true };
if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
start.ArgumentList.Add("--privacy-child");
start.ArgumentList.Add("--accessToken");
start.ArgumentList.Add(secret);
start.Environment["PRIVATE_TEST_TOKEN"] = secret;
using var hostProcess = Process.Start(start) ?? throw new Exception("Cannot start the isolated fixture.");
try
{
    const string identity = "private-player-7b50";
    const string privateDirectory = @"C:\Users\private-owner-72a0\Games";
    var game = new GameProcessInfo(hostProcess, "private-instance-91b0", identity, privateDirectory);
    game.OutputBuffer.Append(secret);
    var options = new LauncherService.LaunchOptions
    {
        Account = new Account
        {
            Id = "private-account-id-504a", Username = identity, Uuid = "private-uuid-7272",
            Type = AccountType.Microsoft, MsRefreshToken = secret, MinecraftAccessToken = secret,
            AuthServerClientToken = secret, AuthServerApiRoot = "https://auth.example/" + secret,
            AvatarPhotoPath = secret, CustomSkinPath = secret
        },
        MinecraftDir = privateDirectory, EffectiveGameDir = privateDirectory,
        JavaPath = privateDirectory + @"\javaw.exe", VersionId = "private-instance-91b0",
        MinMemoryMb = 1024, MaxMemoryMb = 4096,
        CustomJvmArgs = "-Dprivate=" + secret, ExtraGameArgs = "--private " + secret,
        SkinJvmArgs = new() { "-javaagent:" + secret }, PreLaunchCommand = "echo " + secret,
        AutoJoinServerAddress = secret
    };
    using var first = new PluginRuntimeService(new PluginContext());
    using var second = new PluginRuntimeService(new PluginContext());
    var called = 0;
    var seen = 0;
    using var outer = first.RegisterLaunchMiddleware("privacy-outer", Inspect, order: 1);
    using var inner = second.RegisterLaunchMiddleware("privacy-inner", Inspect, order: 2);
    var result = PluginRuntimeRegistry.Launch(options, () => { called++; return game; });
    Check(called == 1 && seen == 2, "Middleware next() chain must launch exactly once.");
    Check(ReferenceEquals(result, game), "Host process/log ownership must survive middleware snapshots.");
    Check(options.Account.Username == identity && options.JavaPath == privateDirectory + @"\javaw.exe" && options.MaxMemoryMb == 4096,
        "A UI hook changed real account or launch state.");
    Check(options.Account.MinecraftAccessToken == secret && options.SkinJvmArgs!.Count == 1,
        "Sanitization damaged real authentication state.");
    Check(hostProcess.StartInfo.ArgumentList.Contains(secret) && game.OutputBuffer.ToString() == secret,
        "Sanitization changed host command/output state.");
    Console.WriteLine("PASS: legacy launch hooks receive detached options, account and process views; host state is preserved.");

    GameProcessInfo Inspect(PluginLaunchRequest request, Func<GameProcessInfo> next)
    {
        seen++;
        Check(!ReferenceEquals(request.Options, options) && !ReferenceEquals(request.Options.Account, options.Account),
            "A legacy UI hook received live host objects.");
        Check(!JsonSerializer.Serialize(request.Options).Contains(secret, StringComparison.Ordinal),
            "A launch option or account credential leaked to the hook.");
        var serialized = JsonSerializer.Serialize(request.Options);
        foreach (var field in new[] { identity, options.Account.Id, options.Account.Uuid, options.VersionId })
            Check(!serialized.Contains(field, StringComparison.Ordinal), "UI-only hook received private identity: " + field);
        Check(request.Options.Account.Id == "" && request.Options.Account.Username == "" && request.Options.Account.Uuid == "",
            "UI-only hook received account identity without accounts.read.");
        Check(request.Options.Account.Type == AccountType.Offline, "UI-only hook received the real account type.");
        Check(request.Options.MinecraftDir == "" && request.Options.EffectiveGameDir == "" && request.Options.JavaPath == "",
            "UI-only hook received personal paths without instance/Java permissions.");
        request.Options.Account.Username = "ChangedByPlugin";
        request.Options.Account.MinecraftAccessToken = "changed";
        request.Options.JavaPath = "changed.exe";
        request.Options.MaxMemoryMb = 1;
        var view = next();
        Check(!ReferenceEquals(view, game) && !ReferenceEquals(view.Process, hostProcess),
            "A legacy UI hook received the real host process.");
        Check(view.Pid == -1 && view.VersionId == "" && view.AccountLabel == "" && view.GameDir == "",
            "UI-only hook received an instance, account label or process identity.");
        Check(view.Process.StartInfo.FileName == "" && view.Process.StartInfo.ArgumentList.Count == 0 &&
            view.Process.StartInfo.Arguments == "" && view.Process.StartInfo.Environment.Count == 0,
            "The hook received a launch command line or environment.");
        Check(view.GetOutputSnapshot().Length == 0, "The live raw log was exposed.");
        view.OutputBuffer.Append("changed");
        return view;
    }

}
finally
{
    try { hostProcess.StandardInput.WriteLine("exit"); } catch { }
    if (!hostProcess.WaitForExit(5000)) { hostProcess.Kill(entireProcessTree: true); hostProcess.WaitForExit(); }
}

static void Check(bool value, string message) { if (!value) throw new Exception(message); }

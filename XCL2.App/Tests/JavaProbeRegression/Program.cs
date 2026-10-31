using System.Diagnostics;
using XCL2.App.Services;

// The test's apphost doubles as a deterministic Java-shaped process fixture.
if (args.Contains("-version"))
{
    var mode = Environment.GetEnvironmentVariable("XCL2_JAVA_PROBE_TEST_MODE");
    var pidFile = Environment.GetEnvironmentVariable("XCL2_JAVA_PROBE_PID_FILE");
    if (pidFile != null) File.WriteAllText(pidFile, Environment.ProcessId.ToString());
    if (mode == "hang") { await Task.Delay(TimeSpan.FromMinutes(2)); return; }
    Console.Error.WriteLine("openjdk version \"21.0.1\"");
    Console.Out.Write(new string('o', 1024 * 1024));
    Console.Error.Write(new string('e', 1024 * 1024));
    return;
}
var count = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
Check(JavaVersionOutputPolicy.ParseMajor("Picked up JAVA_TOOL_OPTIONS: \"some notice\"\nopenjdk version \"21.0.1\" 2023-10-17") == 21,
    "environment notices do not displace the actual quoted Java version");
Check(JavaVersionOutputPolicy.ParseMajor("java version \"1.8.0_412\"") == 8 && JavaVersionOutputPolicy.ParseMajor("openjdk 26+35") == 26,
    "old and unquoted modern Java version formats parse correctly");
Check(JavaVersionOutputPolicy.ParseMajor("download page version \"21\"") == null, "unrelated quoted text is not accepted as Java");
var executable = Environment.ProcessPath!;
if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    executable = Path.Combine(AppContext.BaseDirectory, "JavaProbeRegression" + (OperatingSystem.IsWindows() ? ".exe" : ""));
Check(File.Exists(executable), "self-contained executable fixture exists");
var pidPath = Path.Combine(Path.GetTempPath(), "XCL2-probe-pid-" + Guid.NewGuid().ToString("N"));
try
{
    Environment.SetEnvironmentVariable("XCL2_JAVA_PROBE_TEST_MODE", "both-pipes");
    var result = await JavaProcessProbe.ReadVersionOutputAsync(executable, TimeSpan.FromSeconds(10));
    Check(result?.Contains("openjdk version \"21.0.1\"") == true, "both output pipes are drained without a pipe-buffer deadlock");
    Check(result!.Length <= 128 * 1024 + 1, "verbose output is bounded while excess bytes are still drained");
    Environment.SetEnvironmentVariable("XCL2_JAVA_PROBE_TEST_MODE", "hang");
    Environment.SetEnvironmentVariable("XCL2_JAVA_PROBE_PID_FILE", pidPath);
    var timer = Stopwatch.StartNew();
    result = await JavaProcessProbe.ReadVersionOutputAsync(executable, TimeSpan.FromMilliseconds(600));
    Check(result == null && timer.Elapsed < TimeSpan.FromSeconds(5), "deadline includes pipe reads and returns promptly");
    if (File.Exists(pidPath))
    {
        var exited = false;
        try { using var process = Process.GetProcessById(int.Parse(File.ReadAllText(pidPath))); exited = process.HasExited || process.WaitForExit(2000); }
        catch (ArgumentException) { exited = true; }
        Check(exited, "timed-out executable does not keep running");
    }
    else throw new Exception("hung fixture did not start before the deadline");
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    try { await JavaProcessProbe.ReadVersionOutputAsync(executable, TimeSpan.FromSeconds(10), cancel.Token); throw new Exception("cancellation was swallowed"); }
    catch (OperationCanceledException) { count++; }
}
finally
{
    Environment.SetEnvironmentVariable("XCL2_JAVA_PROBE_TEST_MODE", null);
    Environment.SetEnvironmentVariable("XCL2_JAVA_PROBE_PID_FILE", null);
    if (File.Exists(pidPath)) File.Delete(pidPath);
}
Console.WriteLine($"PASS {count} production Java probe checks");

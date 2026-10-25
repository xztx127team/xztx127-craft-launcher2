using System.Diagnostics;
using System.Reflection;
using XCL2.App.Services;

if (args.Contains("--installer-child"))
{
    Console.WriteLine("ready");
    if (!args.Contains("--exit")) await Task.Delay(TimeSpan.FromMinutes(5));
    return;
}

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

Process StartChild(bool exit)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process executable.");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--installer-child");
    if (exit) start.ArgumentList.Add("--exit");
    return Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture process.");
}

var work = Path.Combine(Path.GetTempPath(), "XclInstallerLifetimeTest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var original = Path.Combine(work, "existing-world.txt");
File.WriteAllText(original, "original");
try
{
    using (var normal = StartChild(exit: true))
    {
        await InstallerProcessLifetime.WaitForExitAsync(normal, TimeSpan.FromSeconds(10), "fixture", work, CancellationToken.None);
        Check(normal.HasExited && normal.ExitCode == 0, "Natural completion must await the child exit.");
    }

    using (var canceled = StartChild(exit: false))
    {
        try
        {
            Check(await canceled.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready", "Cancellation fixture started.");
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            var caught = false;
            try
            {
                await InstallerProcessLifetime.WaitForExitAsync(canceled, TimeSpan.FromMinutes(1), "fixture", work, stop.Token);
            }
            catch (OperationCanceledException error)
            {
                caught = true;
                Check(error.CancellationToken == stop.Token, "Caller cancellation token is preserved.");
                Check(InstallerProcessLifetime.RequiresPreservedDirectory(error), "Cancellation retains the workspace guard through its inner exception.");
                Check(error.Message.Contains(work) && error.Message.Contains(canceled.Id.ToString()), "Cancellation reveals retained directory and PID.");
            }
            Check(caught && canceled.HasExited, "Cancellation cannot complete while the parent installer is still running.");
        }
        finally { if (!canceled.HasExited) { canceled.Kill(entireProcessTree: true); canceled.WaitForExit(5000); } }
    }

    using (var timedOut = StartChild(exit: false))
    {
        try
        {
            Check(await timedOut.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready", "Timeout fixture started.");
            var caught = false;
            try
            {
                await InstallerProcessLifetime.WaitForExitAsync(timedOut, TimeSpan.FromMilliseconds(50), "fixture", work, CancellationToken.None);
            }
            catch (InstallerProcessCleanupDeferredException error)
            {
                caught = true;
                Check(error.ParentExited && error.ProcessId == timedOut.Id, "Timeout waits for the parent and records its PID.");
                Check(error.Message.Contains("超时") && error.WorkingDirectory == work, "Timeout is distinct from a user cancellation.");
            }
            Check(caught && timedOut.HasExited, "Timeout cannot leave the parent installer running silently.");
        }
        finally { if (!timedOut.HasExited) { timedOut.Kill(entireProcessTree: true); timedOut.WaitForExit(5000); } }
    }
    Check(File.ReadAllText(original) == "original", "Lifecycle handling never modifies or deletes existing files.");
    Check(!InstallerProcessLifetime.RequiresPreservedDirectory(new IOException("ordinary error")), "Ordinary errors do not invent a live installer.");
}
finally { Directory.Delete(work, recursive: true); }

Console.WriteLine($"InstallerProcessLifetimeRegression: {checks} checks passed.");

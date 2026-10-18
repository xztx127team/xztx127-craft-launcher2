using XCL2.App.Services;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
}

var current = new Version(2, 2, 22, 0);
Check(LauncherUpdatePolicy.ExtractVersion("v2.2.22") == current, "release tags and assembly versions normalize identically");
Check(!LauncherUpdatePolicy.IsNewer(new Version(2, 2, 22), current), "same version never prompts again due to omitted revision");
Check(!LauncherUpdatePolicy.IsNewer(new Version(2, 2, 21, 99), current), "older release does not downgrade installation");
Check(LauncherUpdatePolicy.IsNewer(LauncherUpdatePolicy.ExtractVersion("2.2.23-fix1")!, current), "new release with suffix is detected");
Check(LauncherUpdatePolicy.IsNewer(new Version(2, 2, 22, 1), current), "revision updates are detected");
Check(LauncherUpdatePolicy.IsNewer(new Version(3, 0), current), "major updates support two-component versions");
Check(LauncherUpdatePolicy.ExtractVersion(null) == null && LauncherUpdatePolicy.ExtractVersion("latest") == null,
    "missing or invalid API tag does not masquerade as up-to-date");

var assets = new[] { "launcher-x86.exe", "source-x64.zip", "launcher-x64.EXE" };
Check(LauncherUpdatePolicy.PickAssetName(assets, true) == "launcher-x64.EXE", "64-bit system chooses executable for the correct architecture");
Check(LauncherUpdatePolicy.PickAssetName(assets, false) == "launcher-x86.exe", "32-bit system selects x86");
Check(LauncherUpdatePolicy.PickAssetName(new[] { "launcher-x86.exe" }, true) == null,
    "missing x64 attachment never falls back to another architecture");
Check(LauncherUpdatePolicy.PickAssetName(null, true) == null &&
      LauncherUpdatePolicy.PickAssetName(new[] { "source-x64.zip" }, true) == null,
    "empty or non-executable assets cannot be installed");

Check(!LauncherUpdatePolicy.ShouldCheckOnStartup(false), "disabling startup checks prevents the automatic request");
Check(LauncherUpdatePolicy.ShouldCheckOnStartup(true), "existing enabled startup behavior is retained");
Check(!LauncherUpdatePolicy.CanInstall(false, true), "disabled updates cannot install even after confirmation");
Check(!LauncherUpdatePolicy.CanInstall(true, false), "enabled updates still require explicit user confirmation");
Check(LauncherUpdatePolicy.CanInstall(true, true), "installation proceeds only with both permission and confirmation");
void Reject<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { checks++; return; }
    throw new Exception("FAIL: " + name);
}
byte[] Executable(bool x64, int marker)
{
    // Minimal real PE structure, not a renamed text file. It is intentionally not
    // a working launcher, so a Windows start probe must roll it back.
    var bytes = new byte[1024];
    using var memory = new MemoryStream(bytes); using var writer = new BinaryWriter(memory);
    writer.Write((ushort)0x5a4d); memory.Position = 0x3c; writer.Write(0x80);
    memory.Position = 0x80; writer.Write(0x00004550); writer.Write((ushort)(x64 ? 0x8664 : 0x014c));
    writer.Write((ushort)1); writer.Write(marker); writer.Write(0); writer.Write(0);
    writer.Write((ushort)(x64 ? 240 : 224)); writer.Write((ushort)0x0022);
    var optional = memory.Position;
    writer.Write((ushort)(x64 ? 0x020b : 0x010b));
    memory.Position = optional + 16; writer.Write(0x1000); writer.Write(0x1000);
    if (x64) writer.Write(0x140000000L); else { writer.Write(0x2000); writer.Write(0x00400000); }
    memory.Position = optional + 32; writer.Write(0x1000); writer.Write(0x200);
    memory.Position = optional + 40; writer.Write((ushort)6);
    memory.Position = optional + 48; writer.Write((ushort)6);
    memory.Position = optional + 56; writer.Write(0x2000); writer.Write(0x200);
    memory.Position = optional + 68; writer.Write((ushort)2);
    memory.Position = optional + (x64 ? 108 : 92); writer.Write(16);
    memory.Position = optional + (x64 ? 240 : 224);
    writer.Write(Encoding.ASCII.GetBytes(".text\0\0\0")); writer.Write(1); writer.Write(0x1000); writer.Write(0x200); writer.Write(0x200);
    memory.Position += 12; writer.Write(0x60000020);
    bytes[0x200] = 0xc3;
    return bytes;
}
LauncherUpdateMetadata Metadata(byte[] bytes) => LauncherUpdateMetadata.FromAsset(bytes.Length,
    "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)));
async Task<int> RunUpdater(LauncherUpdatePlan plan)
{
    using var process = Process.Start(LauncherUpdatePackage.CreateStartInfo(plan))!;
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); return process.ExitCode; }
    finally
    {
        if (!process.HasExited)
        { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
void AuthorizeFixture(LauncherUpdatePlan plan)
{
    using var fixtureProcess = Process.GetCurrentProcess();
    LauncherUpdatePackage.AuthorizeOnClosed(plan, fixtureProcess);
}

var directory = Path.Combine(Path.GetTempPath(), "XCL updater %PATH% & [literal] ' 中文 " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    Reject<InvalidDataException>(() => LauncherUpdateMetadata.FromAsset(0, "sha256:" + new string('a', 64)), "empty size rejects automatic application");
    Reject<InvalidDataException>(() => LauncherUpdateMetadata.FromAsset(512, null), "missing GitHub digest rejects automatic application");
    Reject<InvalidDataException>(() => LauncherUpdateMetadata.FromAsset(512, "sha256:" + new string('z', 64)), "invalid SHA256 alphabet rejected");
    Reject<InvalidDataException>(() => LauncherUpdateMetadata.FromAsset(512, "md5:" + new string('a', 64)), "wrong digest algorithm rejected");
    var old = Executable(true, 1); var next = Executable(true, 2);
    var installed = Path.Combine(directory, "launcher %PATH% & [quoted] '.exe");
    var downloaded = Path.Combine(directory, "download.exe");
    await File.WriteAllBytesAsync(installed, old); await File.WriteAllBytesAsync(downloaded, next);
    var metadata = Metadata(next);
    LauncherUpdatePackage.Verify(downloaded, metadata, true); checks++;
    Reject<InvalidDataException>(() => LauncherUpdatePackage.Verify(downloaded, metadata, false), "architecture mismatch rejected even with correct hash");
    Reject<InvalidDataException>(() => LauncherUpdatePackage.Verify(downloaded, metadata with { Size = metadata.Size + 1 }, true), "incorrect release size rejected");
    Reject<InvalidDataException>(() => LauncherUpdatePackage.Verify(downloaded, metadata with { Sha256 = new string('0', 64) }, true), "incorrect release digest rejected");
    var html = Encoding.UTF8.GetBytes("<!doctype html><h1>download error</h1>");
    await File.WriteAllBytesAsync(downloaded, html);
    Reject<BadImageFormatException>(() => LauncherUpdatePackage.Verify(downloaded, Metadata(html), true), "hash-matching HTML still rejected as non-PE");
    await File.WriteAllBytesAsync(downloaded, next);
    var plan = await LauncherUpdatePackage.PrepareAsync(downloaded, installed, metadata, true, Path.Combine(directory, "update.log"), directory);
    Check(File.ReadAllBytes(installed).SequenceEqual(old), "preparation never overwrites the installed executable");
    Check(Path.GetDirectoryName(plan.StagePath) == directory && Path.GetDirectoryName(plan.BackupPath) == directory, "stage and backup are on the target volume");
    Check(plan.CurrentPath == installed, "renamed launchers preserve their shortcut path");
    var script = LauncherUpdatePackage.BuildScript(plan);
    Check(!script.Contains(installed, StringComparison.Ordinal), "special path punctuation remains serialized data, never PowerShell source");
    var data = Regex.Match(script, "FromBase64String\\('([^']+)'\\)").Groups[1].Value;
    Check(JsonSerializer.Deserialize<LauncherUpdatePlan>(Convert.FromBase64String(data)) == plan, "serialized argument data round-trips exact paths");
    var start = LauncherUpdatePackage.CreateStartInfo(plan);
    Check(!start.UseShellExecute && start.Arguments.Length == 0 && start.ArgumentList.Contains("-EncodedCommand"), "updater uses argument-list encoded PowerShell without cmd interpolation");
    Check(!OperatingSystem.IsWindows() || Path.IsPathFullyQualified(start.FileName), "updater resolves PowerShell through the protected system directory rather than PATH");
    Check(Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList.Last())) == script, "encoded command uses exact production transaction");

    if (OperatingSystem.IsWindows())
    {
        var waiting = await LauncherUpdatePackage.PrepareAsync(downloaded, installed, metadata, true, Path.Combine(directory, "waiting.log"), directory);
        using (var process = Process.Start(LauncherUpdatePackage.CreateStartInfo(waiting))!)
        {
            try
            {
                await LauncherUpdatePackage.WaitUntilReadyAsync(process, waiting);
                await Task.Delay(300);
                Check(!process.HasExited && File.ReadAllBytes(installed).SequenceEqual(old), "helper stays ready while its real parent is still alive and has not closed");
                LauncherUpdatePackage.Abort(waiting);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(process.ExitCode != 0, "pending parent-close wait observes cancellation");
            }
            finally
            {
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }
        var unauthorized = await LauncherUpdatePackage.PrepareAsync(downloaded, installed, metadata, true, Path.Combine(directory, "no-authorization.log"), directory);
        var failedAbort = unauthorized with { ParentProcessId = int.MaxValue, AbortPath = Path.Combine(directory, "missing-directory", "abort") };
        Reject<DirectoryNotFoundException>(() => LauncherUpdatePackage.Abort(failedAbort), "simulate an unwritable cancel signal");
        Check(await RunUpdater(failedAbort) != 0, "missing positive Closed authorization refuses application even when Abort could not be written");
        Check(File.ReadAllBytes(installed).SequenceEqual(old) && !File.Exists(failedAbort.BackupPath), "ordinary later parent exit cannot apply a cancelled update");

        // Run the production PowerShell transaction against disposable actual files.
        // The parent id is deliberately absent, so the helper may apply immediately.
        var runnable = plan with { ParentProcessId = int.MaxValue, ParentStartedUtcTicks = 0 };
        AuthorizeFixture(runnable);
        Check(await RunUpdater(runnable) != 0, "non-launcher PE fails the actual startup probe");
        Check(File.ReadAllBytes(installed).SequenceEqual(old), "failed real startup probe rolls back the installed bytes");
        Check(File.ReadAllBytes(plan.BackupPath).SequenceEqual(old), "rollback retains the original backup");

        var canceled = await LauncherUpdatePackage.PrepareAsync(downloaded, installed, metadata, true, Path.Combine(directory, "cancel.log"), directory);
        LauncherUpdatePackage.Abort(canceled);
        Check(await RunUpdater(canceled with { ParentProcessId = int.MaxValue }) != 0, "exit cancellation aborts updater");
        Check(File.ReadAllBytes(installed).SequenceEqual(old) && !File.Exists(canceled.BackupPath), "cancel cannot replace the installed executable");

        var blocked = await LauncherUpdatePackage.PrepareAsync(downloaded, installed, metadata, true, Path.Combine(directory, "blocked.log"), directory);
        AuthorizeFixture(blocked);
        using (var held = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check(await RunUpdater(blocked with { ParentProcessId = int.MaxValue }) != 0, "replacement failure is reported");
        Check(File.ReadAllBytes(installed).SequenceEqual(old), "failed atomic replacement preserves the old executable");

        var stale = await LauncherUpdatePackage.PrepareAsync(downloaded, installed, metadata, true, Path.Combine(directory, "stale.log"), directory);
        AuthorizeFixture(stale);
        var otherUpdate = Executable(true, 3); await File.WriteAllBytesAsync(installed, otherUpdate);
        Check(await RunUpdater(stale with { ParentProcessId = int.MaxValue }) != 0, "stale updater refuses another process's replacement");
        Check(File.ReadAllBytes(installed).SequenceEqual(otherUpdate) && !File.Exists(stale.BackupPath), "stale updater cannot downgrade or erase the newer target");

        var tampered = await LauncherUpdatePackage.PrepareAsync(downloaded, installed, metadata, true, Path.Combine(directory, "tampered.log"), directory);
        AuthorizeFixture(tampered);
        await File.WriteAllTextAsync(tampered.StagePath, "changed after parent verification");
        Check(await RunUpdater(tampered with { ParentProcessId = int.MaxValue }) != 0, "helper rechecks stage bytes before waiting or committing");
        Check(File.ReadAllBytes(installed).SequenceEqual(otherUpdate), "tampered stage cannot replace the installed executable");
    }
    else Console.WriteLine("SKIPPED: Windows PowerShell application/rollback cases; metadata, PE, preparation and payload cases ran.");
}
finally
{
    // A restored PE may have just been started; wait briefly for its intentionally
    // short-lived fixture process before removing the disposable test directory.
    for (var attempt = 0; ; attempt++)
    {
        try { Directory.Delete(directory, recursive: true); break; }
        catch (Exception ex) when (attempt < 49 && (ex is IOException or UnauthorizedAccessException)) { await Task.Delay(100); }
    }
}
Console.WriteLine($"PASS: {checks} launcher update regression checks (Windows transaction cases require Windows)");

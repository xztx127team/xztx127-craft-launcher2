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

await HandoffCases.RunAsync(Check);

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

LauncherReleaseVersion Release(string text) => LauncherUpdatePolicy.ParseReleaseVersion(text)
    ?? throw new InvalidOperationException("Invalid version fixture: " + text);
Check(Release("v2.2.25-rc1") == Release("2.2.25-rc(1)") &&
      Release("2.2.25-rc（1）") == Release("2.2.25-rc.1"), "RC bracket and dot spellings normalize identically");
Check(LauncherUpdatePolicy.IsNewer(Release("2.2.25-rc1"), Release("2.2.24")), "higher-core RC is an update from current final");
Check(!LauncherUpdatePolicy.IsNewer(Release("2.2.24-rc99"), Release("2.2.24")), "same-core RC cannot replace an installed final");
Check(!LauncherUpdatePolicy.IsNewer(Release("2.2.23-rc99"), Release("2.2.24")), "lower-core RC cannot downgrade");
Check(LauncherUpdatePolicy.IsNewer(Release("2.2.25-rc10"), Release("2.2.25-rc2")), "RC ordinal comparison is numeric");
Check(!LauncherUpdatePolicy.IsNewer(Release("2.2.25-rc2"), Release("2.2.25-rc10")), "lower RC ordinal cannot replace higher RC");
Check(LauncherUpdatePolicy.IsNewer(Release("2.2.25"), Release("2.2.25-rc10")), "final release upgrades every same-core RC");
Check(!LauncherUpdatePolicy.IsNewer(Release("2.2.25-rc1+build.a"), Release("v2.2.25-rc1+build.b")), "build metadata does not create updates");
Check(LauncherUpdatePolicy.ParseReleaseVersion("2.2.25-rc") == null &&
      LauncherUpdatePolicy.ParseReleaseVersion("2.2.25-rc(1）") == null &&
      LauncherUpdatePolicy.ParseReleaseVersion("2.2.25-beta1") == null &&
      LauncherUpdatePolicy.ParseReleaseVersion("2.2.25-rc999999999999999999999") == null,
    "malformed or unknown prereleases cannot masquerade as finals");
Check(Release("v2.2.25-fix1") == Release("2.2.25"), "historical stable fix tags remain supported");
Check(LauncherUpdatePolicy.ShouldCheckOnStartup(false, true, false), "preview opt-in checks even with stable startup checks disabled");
Check(!LauncherUpdatePolicy.ShouldCheckOnStartup(true, true, true), "explicit skip-update-check suppresses both channels");
Check(!LauncherUpdatePolicy.ShouldCheckOnStartup(false, false, false), "both preferences off avoids startup network request");
var dismissed = new[] { "v2.2.25-rc1" };
Check(LauncherUpdatePolicy.IsSuppressedPreviewTag("V2.2.25-RC1", dismissed), "preview dismissal matches exact tag ignoring case");
Check(!LauncherUpdatePolicy.IsSuppressedPreviewTag("v2.2.25-rc2", dismissed) &&
      !LauncherUpdatePolicy.IsSuppressedPreviewTag("v2.2.26-rc1", dismissed), "dismissal never suppresses a different preview version");
var releases = new[]
{
    ("v2.2.25-rc99", true, false), ("v2.2.25", false, false),
    ("v2.2.26-rc2", true, false), ("v2.2.26-rc10", false, false),
    ("v9.0.0-rc1", true, true), ("v10.0.0-beta1", false, false)
};
Check(LauncherUpdatePolicy.PickReleaseTag(releases, false) == "v2.2.25", "stable channel excludes RC even when publisher forgot prerelease flag");
Check(LauncherUpdatePolicy.PickReleaseTag(releases, true) == "v2.2.26-rc10", "manual check picks highest valid full version, ignoring drafts and unsupported prereleases");
Check(LauncherUpdatePolicy.PickReleaseTag(releases, true, true) == "v2.2.26-rc10", "preview channel selects numerically highest RC");
Check(LauncherUpdatePolicy.PickReleaseTag(new[] { ("2.2.25-rc99", true, false), ("2.2.25", false, false) }, true, true) == null,
    "published final suppresses obsolete RC startup reminders of the same or older core");
// GitHub's official prerelease flag also classifies numeric and non-RC snapshot tags.
var flagged = LauncherUpdatePolicy.ParseReleaseVersion("v2.3.0", true)!.Value;
Check(flagged.IsPreview && !LauncherUpdatePolicy.ParseReleaseVersion("v2.3.0", false)!.Value.IsPreview,
    "GitHub prerelease flag classifies otherwise numeric tags");
Check(LauncherUpdatePolicy.IsNewer(Release("2.3.0"), flagged), "published final supersedes a same-core GitHub snapshot");
Check(LauncherUpdatePolicy.PickReleaseTag(new[] { ("v2.3.0", true, false), ("v2.2.25", false, false) }, false) == "v2.2.25",
    "stable channel excludes GitHub numeric snapshots");
Check(LauncherUpdatePolicy.PickReleaseTag(new[] { ("v2.3.0", true, false), ("v2.2.25", false, false) }, true, true) == "v2.3.0",
    "preview channel includes GitHub numeric snapshots");
Check(LauncherUpdatePolicy.ParseReleaseVersion("v2.3.0-snapshot.5", true) is { IsPreview: true },
    "GitHub snapshot suffixes can be used without inventing a final version");
Check(LauncherUpdatePolicy.ParseReleaseVersion("latest", true) == null, "channel flag does not invent a missing version");
Check(LauncherUpdatePolicy.IsNewer(LauncherUpdatePolicy.ParseReleaseVersion("v2.3.0-snapshot.10", true)!.Value,
    LauncherUpdatePolicy.ParseReleaseVersion("v2.3.0-snapshot.2", true)!.Value), "snapshot ordinal compares numerically");
Check(LauncherUpdatePolicy.IsNewer(LauncherUpdatePolicy.ParseReleaseVersion("v2.3.0-beta2", true)!.Value,
    LauncherUpdatePolicy.ParseReleaseVersion("v2.3.0-beta1", true)!.Value), "same-core GitHub beta progression is detected");
var today = new DateOnly(2026, 10, 22);
Check(LauncherReleaseInstructions.Parse(null) == LauncherReleaseInstructions.Default,
    "missing setup.ini preserves previous behavior");
Check(LauncherReleaseInstructions.Parse("b=off\nc=no\nd=on=invalid") == LauncherReleaseInstructions.Default,
    "disabled and invalid directives preserve previous behavior");
var minor = LauncherReleaseInstructions.Parse("\uFEFF; comment\n[update]\nb=on\nc=off");
Check(minor.Minor && !minor.IsRequired(today), "minor updates do not become mandatory");
Check(LauncherReleaseInstructions.Parse("b=on\nc=on\nd=on=2030/01/01").IsRequired(today),
    "immediate requirement takes precedence over minor and future deadline");
var deadline = LauncherReleaseInstructions.Parse("d=on=2026/10/22");
Check(!deadline.IsRequired(today) && deadline.IsRequired(today.AddDays(1)),
    "deadline permits installation through the specified local calendar date");
Check(LauncherReleaseInstructions.Parse("d=on=2026/02/30").Deadline == null,
    "invalid calendar dates do not lock launcher");
Check(LauncherReleaseInstructions.Parse("d=off=2026/10/22").Deadline == null,
    "disabled deadline never locks launcher");
var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
var snooze = new LauncherReminderPreference(LauncherReminderMode.Snooze, now.AddHours(-1), now.AddHours(1));
Check(snooze.SuppressAutomaticReminder(now, minor, today), "snooze suppresses optional reminder");
Check(!snooze.SuppressAutomaticReminder(now, new(Required: true), today), "mandatory update overrides snooze");
Check(!snooze.SuppressAutomaticReminder(now, deadline, today.AddDays(1)), "expired deadline overrides snooze");
Check(snooze.EffectiveMode(now.AddHours(1)) == LauncherReminderMode.Normal,
    "snooze expires exactly at restore instant");
var badge = new LauncherReminderPreference(LauncherReminderMode.BadgeOnly, now.AddHours(1), now.AddHours(2));
Check(badge.EffectiveMode(now) == LauncherReminderMode.Normal && badge.EffectiveMode(now.AddMinutes(90)) == LauncherReminderMode.BadgeOnly,
    "badge-only schedule honors both interval boundaries");
Check(!badge.SuppressAutomaticReminder(now.AddMinutes(90), minor, today), "badge-only mode retains notification badge");
Check(LauncherUpdatePolicy.ShouldPromptForStableUpdate(true, LauncherReminderMode.Normal, null, now),
    "a stable update found on startup opens a confirmation by default");
Check(LauncherUpdatePolicy.ShouldPromptForStableUpdate(true, LauncherReminderMode.Normal, now, now),
    "a stable update prompts again exactly when its snooze expires");
foreach (var mode in new[] { LauncherReminderMode.Snooze, LauncherReminderMode.Paused, LauncherReminderMode.BadgeOnly })
{
    Check(!LauncherUpdatePolicy.ShouldPromptForStableUpdate(true, mode, null, now),
        "explicit automatic reminder preference remains respected: " + mode);
    Check(LauncherUpdatePolicy.ShouldPromptForStableUpdate(false, mode, now.AddDays(1), now),
        "manual stable checks still open confirmation: " + mode);
}
Check(!LauncherUpdatePolicy.ShouldPromptForStableUpdate(true, LauncherReminderMode.Normal, now.AddMinutes(1), now),
    "per-version snooze postpones only automatic stable prompts");

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
byte[] RestartProbeExecutable()
{
    // A GUI PE that calls kernel32!Sleep(15000), then ExitProcess(0).
    // Its finite lifetime also prevents an orphan fixture if the test runner is interrupted.
    var bytes = Executable(true, 4);
    using var memory = new MemoryStream(bytes);
    using var writer = new BinaryWriter(memory);
    memory.Position = 0x80 + 24 + 72;
    writer.Write(0x100000L); writer.Write(0x1000L); // stack reserve/commit
    writer.Write(0x100000L); writer.Write(0x1000L); // heap reserve/commit
    memory.Position = 0x80 + 24 + 240 + 8;
    writer.Write(0x200); // virtual section extent includes the import tables below
    memory.Position = 0x80 + 24 + 112 + 8; // PE32+ import data directory
    writer.Write(0x1100); writer.Write(40);
    memory.Position = 0x300; // IMAGE_IMPORT_DESCRIPTOR followed by a zero descriptor
    writer.Write(0x1140); writer.Write(0); writer.Write(0); writer.Write(0x1180); writer.Write(0x1160);
    memory.Position = 0x340; writer.Write(0x11a0L); writer.Write(0x11b0L); writer.Write(0L);
    memory.Position = 0x360; writer.Write(0x11a0L); writer.Write(0x11b0L); writer.Write(0L);
    memory.Position = 0x380; writer.Write(Encoding.ASCII.GetBytes("KERNEL32.dll\0"));
    memory.Position = 0x3a0; writer.Write((ushort)0); writer.Write(Encoding.ASCII.GetBytes("Sleep\0"));
    memory.Position = 0x3b0; writer.Write((ushort)0); writer.Write(Encoding.ASCII.GetBytes("ExitProcess\0"));
    memory.Position = 0x200;
    writer.Write(new byte[] { 0x48, 0x83, 0xec, 0x28, 0xb9 }); // shadow space + mov ecx, milliseconds
    writer.Write(15000);
    writer.Write(new byte[] { 0xff, 0x15 }); writer.Write(0x1160 - 0x100f); // call [RIP + Sleep IAT]
    writer.Write(new byte[] { 0x31, 0xc9, 0xff, 0x15 }); writer.Write(0x1168 - 0x1017); // ExitProcess(0)
    writer.Write((byte)0xcc);
    return bytes;
}
async Task<int> RunUpdater(LauncherUpdatePlan plan)
{
    using var process = Process.Start(LauncherUpdatePackage.CreateStartInfo(plan))!;
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); return process.ExitCode; }
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
    Check(!start.UseShellExecute && start.CreateNoWindow && start.WindowStyle == ProcessWindowStyle.Hidden &&
        start.Arguments.Length == 0 && start.ArgumentList.Contains("-File") && start.ArgumentList.Last() == plan.ScriptPath,
        "updater runs the saved script hidden with literal arguments and no command shell");
    Check(!OperatingSystem.IsWindows() || Path.IsPathFullyQualified(start.FileName), "updater resolves PowerShell through the protected system directory rather than PATH");
    Check(File.ReadAllText(plan.ScriptPath, Encoding.UTF8) == script &&
        File.ReadAllBytes(plan.ScriptPath).AsSpan(0, 3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }),
        "saved UTF-8 BOM script contains the exact transaction, including special-character paths");
    var longPayload = plan with { LogPath = new string('x', 16000) };
    var longStart = LauncherUpdatePackage.CreateStartInfo(longPayload);
    Check(longStart.ArgumentList.Last() == plan.ScriptPath && longStart.ArgumentList.Sum(value => value.Length) < 2000 &&
        File.ReadAllText(plan.ScriptPath).Contains(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(longPayload))),
        "large transaction metadata stays on disk rather than exceeding the Windows command-line limit");

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

        // A disposable GUI-subsystem native PE stays alive through the start probe.
        // It exercises the real successful replacement/start path without an installed launcher or network.
        if (Environment.Is64BitOperatingSystem)
        {
            var healthy = RestartProbeExecutable();
            await File.WriteAllBytesAsync(downloaded, healthy);
            var success = await LauncherUpdatePackage.PrepareAsync(downloaded, installed, Metadata(healthy), true,
                Path.Combine(directory, "successful-restart.log"), directory);
            var parentProbePath = Path.Combine(directory, "parent-probe.exe");
            await File.WriteAllBytesAsync(parentProbePath, healthy);
            using var parentProbe = Process.Start(new ProcessStartInfo(parentProbePath) { UseShellExecute = false, CreateNoWindow = true })!;
            Process? child = null;
            Process? helper = null;
            try
            {
                success = success with { ParentProcessId = parentProbe.Id, ParentStartedUtcTicks = parentProbe.StartTime.ToUniversalTime().Ticks };
                AuthorizeFixture(success);
                using (var held = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    helper = Process.Start(LauncherUpdatePackage.CreateStartInfo(success))!;
                    await LauncherUpdatePackage.WaitUntilReadyAsync(helper, success);
                    Check(!helper.HasExited && !parentProbe.HasExited,
                        "an authorized helper still waits for the captured parent process to exit");
                    parentProbe.Kill();
                    await parentProbe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.Delay(750);
                    Check(!helper.HasExited && File.ReadAllBytes(installed).SequenceEqual(otherUpdate),
                        "temporary executable lock keeps the silent updater waiting without damaging the installed bytes");
                }
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Check(helper.ExitCode == 0 && File.ReadAllBytes(installed).SequenceEqual(healthy),
                    "releasing the file lock allows atomic replacement and a successful five-second restart probe");
                Check(File.ReadAllBytes(success.BackupPath).SequenceEqual(otherUpdate),
                    "successful update retains the exact previous executable as backup");
                var childId = Regex.Match(File.ReadAllText(success.LogPath), @"Started updated launcher PID=(\d+)");
                Check(childId.Success, "the actual restarted process is recorded for diagnostics");
                child = Process.GetProcessById(int.Parse(childId.Groups[1].Value));
                Check(!child.HasExited && !File.Exists(success.ScriptPath),
                    "the restarted launcher survives and the completed script cleans itself up");
            }
            finally
            {
                if (!parentProbe.HasExited) { parentProbe.Kill(); await parentProbe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                if (helper != null)
                {
                    using (helper)
                    {
                        if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    }
                }
                if (child == null && File.Exists(success.LogPath))
                {
                    var childId = Regex.Match(File.ReadAllText(success.LogPath), @"Started updated launcher PID=(\d+)");
                    if (childId.Success) { try { child = Process.GetProcessById(int.Parse(childId.Groups[1].Value)); } catch (ArgumentException) { } }
                }
                if (child != null)
                {
                    using (child)
                    {
                        if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    }
                }
            }
        }
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

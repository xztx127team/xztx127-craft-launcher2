using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

internal sealed record LauncherUpdateMetadata(long Size, string Sha256)
{
    internal static LauncherUpdateMetadata FromAsset(long size, string? digest)
    {
        const string prefix = "sha256:";
        if (size <= 0 || string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            digest.Length != prefix.Length + 64 || !digest[prefix.Length..].All(Uri.IsHexDigit))
            throw new InvalidDataException("发布附件缺少有效的大小或 SHA256 摘要，已拒绝自动应用更新；当前版本保持不变。请从正式发布页面确认附件后手动更新。");
        return new(size, digest[prefix.Length..].ToUpperInvariant());
    }
}

internal sealed record LauncherUpdatePlan(string CurrentPath, string StagePath, string BackupPath, string FailedPath,
    string RestorePath, string LogPath, string ReadyPath, string AbortPath, string AuthorizePath, string AuthorizationToken, long ExpectedSize, string Sha256,
    string CurrentSha256, int ParentProcessId, long ParentStartedUtcTicks, ushort Machine)
{
    public string ScriptPath => Path.Combine(Path.GetDirectoryName(ReadyPath)!,
        Path.GetFileNameWithoutExtension(StagePath) + ".ps1");
}

/// <summary>Verifies the exact published package and prepares a literal-data update transaction.</summary>
internal static class LauncherUpdatePackage
{
    internal static void Verify(string path, LauncherUpdateMetadata metadata, bool wantX64)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != metadata.Size) throw new InvalidDataException("更新包大小与发布附件信息不一致，未替换当前版本。");
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        if (!digest.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包 SHA256 与发布附件信息不一致，未替换当前版本。");
        stream.Position = 0;
        using var pe = new PEReader(stream);
        var headers = pe.PEHeaders;
        var expectedMachine = wantX64 ? Machine.Amd64 : Machine.I386;
        var expectedMagic = wantX64 ? PEMagic.PE32Plus : PEMagic.PE32;
        if (headers.PEHeader is not { } header || headers.CoffHeader.Machine != expectedMachine ||
            header.Magic != expectedMagic || (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0 ||
            (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0 || header.AddressOfEntryPoint == 0 ||
            header.SizeOfHeaders <= 0 || header.SizeOfHeaders > metadata.Size || header.SizeOfImage <= 0 ||
            headers.SectionHeaders.Length == 0 || headers.SectionHeaders.Any(section => section.PointerToRawData < 0 ||
                section.SizeOfRawData < 0 || (long)section.PointerToRawData + section.SizeOfRawData > metadata.Size))
            throw new InvalidDataException("更新包不是当前架构的有效 Windows EXE，未替换当前版本。");
    }

    internal static async Task<LauncherUpdatePlan> PrepareAsync(string downloadedPath, string currentPath,
        LauncherUpdateMetadata metadata, bool wantX64, string logPath, string transactionDirectory, CancellationToken ct = default)
    {
        currentPath = Path.GetFullPath(currentPath);
        if (!File.Exists(currentPath) || Path.GetFileNameWithoutExtension(currentPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前不是可直接替换的启动器 EXE，已拒绝自动应用更新。");
        Verify(downloadedPath, metadata, wantX64);
        var directory = Path.GetDirectoryName(currentPath)!;
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(directory, ".xcl2-update-" + id + ".exe");
        try
        {
            await using (var source = File.OpenRead(downloadedPath))
            await using (var destination = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await source.CopyToAsync(destination, ct); await destination.FlushAsync(ct); destination.Flush(flushToDisk: true); }
            ct.ThrowIfCancellationRequested();
            Verify(stage, metadata, wantX64);
            string currentSha256;
            using (var installed = File.OpenRead(currentPath)) currentSha256 = Convert.ToHexString(SHA256.HashData(installed));
            using var parent = Process.GetCurrentProcess();
            return new(currentPath, stage, Path.Combine(directory, ".xcl2-backup-" + id + ".exe"),
                Path.Combine(directory, ".xcl2-failed-" + id + ".exe"), Path.Combine(directory, ".xcl2-restore-" + id + ".exe"),
                logPath, Path.Combine(transactionDirectory, "updater-ready"), Path.Combine(transactionDirectory, "updater-abort"),
                Path.Combine(directory, ".xcl2-authorize-" + id), Guid.NewGuid().ToString("N"),
                metadata.Size, metadata.Sha256, currentSha256, Environment.ProcessId, parent.StartTime.ToUniversalTime().Ticks,
                wantX64 ? (ushort)0x8664 : (ushort)0x014c);
        }
        catch { TryDelete(stage); throw; }
    }

    internal static ProcessStartInfo CreateStartInfo(LauncherUpdatePlan plan)
    {
        // Windows PowerShell 5.1 reads UTF-8 scripts reliably with a BOM. Only the
        // short literal script path is passed on the command line, regardless of payload size.
        Directory.CreateDirectory(Path.GetDirectoryName(plan.ScriptPath)!);
        File.WriteAllText(plan.ScriptPath, BuildScript(plan), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(plan.ScriptPath)!
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                     "-ExecutionPolicy", "Bypass", "-File", plan.ScriptPath }) info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task WaitUntilReadyAsync(Process updater, LauncherUpdatePlan plan, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(plan.ReadyPath))
        {
            ct.ThrowIfCancellationRequested();
            if (updater.HasExited) throw new IOException("更新辅助进程未准备成功，当前版本继续运行。请查看更新日志。");
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("更新辅助进程启动超时，当前版本继续运行。");
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    internal static void Abort(LauncherUpdatePlan plan)
    {
        File.WriteAllText(plan.AbortPath, "cancel");
    }
    internal static void AuthorizeOnClosed(LauncherUpdatePlan plan, Process updater)
    {
        if (updater.HasExited) throw new IOException("更新辅助进程已经退出，未授权替换。");
        using var file = new FileStream(plan.AuthorizePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(Encoding.UTF8.GetBytes(plan.AuthorizationToken));
        file.Flush(flushToDisk: true);
    }
    internal static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    internal static string BuildScript(LauncherUpdatePlan plan)
    {
        // Only base64 alphabet is inserted into code; paths and punctuation stay JSON data.
        var data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(plan));
        return "$d = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json\n" + ScriptBody;
    }

    private const string ScriptBody = """
$ErrorActionPreference = 'Stop'
$replaced = $false
$parentExited = $false
$authorized = $false
$child = $null
$parent = $null
$ownership = $null
function Log([string]$message) {
    try { [IO.File]::AppendAllText($d.LogPath, ('[' + [DateTime]::Now.ToString('o') + '] ' + $message + [Environment]::NewLine), [Text.Encoding]::UTF8) } catch {}
}
function VerifyStage {
    $item = Get-Item -LiteralPath $d.StagePath -ErrorAction Stop
    if ($item.Length -ne $d.ExpectedSize) { throw 'Staged update size mismatch.' }
    if ((Get-FileHash -LiteralPath $d.StagePath -Algorithm SHA256).Hash -ne $d.Sha256) { throw 'Staged update SHA256 mismatch.' }
}
function InvokeWithFileRetry([scriptblock]$operation, [int]$seconds = 30) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try { return (& $operation) }
        catch {
            $cause = $_.Exception
            while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
            # File locks can outlive the parent by a short interval (scanner/indexer/other instance).
            # Errors in validation or script logic must not be retried as file-access failures.
            $retryable = $cause -is [IO.IOException] -or $cause -is [UnauthorizedAccessException] -or
                ($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -in @(5, 32, 33))
            if (!$retryable -or $clock.Elapsed.TotalSeconds -ge $seconds) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}
function StartLauncher([string]$argument) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $d.CurrentPath
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($d.CurrentPath)
    $start.UseShellExecute = $false
    $start.Arguments = $argument
    return InvokeWithFileRetry { [Diagnostics.Process]::Start($start) }
}
try {
    VerifyStage
    try { $parent = [Diagnostics.Process]::GetProcessById([int]$d.ParentProcessId) }
    catch {
        if (!($_.Exception.GetBaseException() -is [ArgumentException])) { throw }
    }
    if ($null -ne $parent) {
        if ($parent.StartTime.ToUniversalTime().Ticks -ne [long]$d.ParentStartedUtcTicks) {
            $parent.Dispose()
            $parent = $null
        } else {
            # Open the stable process handle before reporting ready. Re-reading StartTime
            # while the launcher exits can throw exactly when replacement should begin.
            $null = $parent.Handle
        }
    }
    [IO.File]::WriteAllText($d.ReadyPath, 'ready')
    while ($true) {
        if (Test-Path -LiteralPath $d.AbortPath) { throw 'Update exit was cancelled; installed executable was preserved.' }
        if ($null -eq $parent) { break }
        if ($parent.WaitForExit(200)) { break }
    }
    if (Test-Path -LiteralPath $d.AbortPath) { throw 'Update cancelled before replacement.' }
    $parentExited = $true
    if (!(Test-Path -LiteralPath $d.AuthorizePath) -or [IO.File]::ReadAllText($d.AuthorizePath) -cne $d.AuthorizationToken) {
        throw 'No positive authorization from the actual update close; installed executable preserved.'
    }
    $authorized = $true
    # A fixed shared file serializes updater processes without evaluating the executable path as code.
    $lockPath = [IO.Path]::Combine([IO.Path]::GetDirectoryName($d.CurrentPath), '.xcl2-update-apply.lock')
    $ownership = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    InvokeWithFileRetry {
        if ((Get-FileHash -LiteralPath $d.CurrentPath -Algorithm SHA256).Hash -ne $d.CurrentSha256) {
            throw 'Installed executable changed during preparation; refusing to overwrite another update.'
        }
        VerifyStage
        [IO.File]::Replace($d.StagePath, $d.CurrentPath, $d.BackupPath, $true)
    }
    $replaced = $true
    Log 'Executable replaced atomically; previous executable retained in the unique backup.'
    # The replacement opens normally without a second update/result/changelog prompt.
    $child = StartLauncher '--skip-update-check'
    if ($null -eq $child) { throw 'The new executable did not start.' }
    Log ('Started updated launcher PID=' + $child.Id)
    if ($child.WaitForExit(5000)) { throw ('New process exited during the five-second start probe: ' + $child.ExitCode) }
    Log 'New process survived the five-second probe. This is a liveness probe, not complete health verification. Backup retained.'
} catch {
    Log ('Update failed: ' + $_.Exception.Message)
    if ($replaced) {
        try {
            if ($null -ne $child) { try { if (!$child.HasExited) { $child.Kill(); $child.WaitForExit(5000) | Out-Null } } catch {} }
            InvokeWithFileRetry { [IO.File]::Copy($d.BackupPath, $d.RestorePath, $true) }
            InvokeWithFileRetry {
                if (Test-Path -LiteralPath $d.CurrentPath) { [IO.File]::Replace($d.RestorePath, $d.CurrentPath, $d.FailedPath, $true) }
                else { [IO.File]::Move($d.RestorePath, $d.CurrentPath) }
            }
            Log 'Previous executable restored; backup retained.'
            $old = StartLauncher '--skip-update-check'
            if ($null -ne $old) { $old.Dispose() }
        } catch { Log ('Rollback needs manual recovery from ' + $d.BackupPath + ': ' + $_.Exception.Message) }
    } elseif ($parentExited -and $authorized -and !(Test-Path -LiteralPath $d.AbortPath)) {
        try {
            if ((Get-FileHash -LiteralPath $d.CurrentPath -Algorithm SHA256).Hash -eq $d.CurrentSha256) {
                $old = StartLauncher '--skip-update-check'
                if ($null -ne $old) { $old.Dispose() }
            }
        } catch { Log ('Original executable was preserved but could not be restarted: ' + $_.Exception.Message) }
    }
    exit 1
} finally {
    if ($null -ne $parent) { $parent.Dispose() }
    if ($null -ne $child) { $child.Dispose() }
    if ($null -ne $ownership) { $ownership.Dispose() }
    foreach ($path in @($d.StagePath, $d.RestorePath, $d.ReadyPath, $d.AbortPath, $d.AuthorizePath, $d.ScriptPath)) {
        try { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } } catch {}
    }
}
""";
}

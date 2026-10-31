param(
    [ValidateSet("Debug", "Release")][string]$Configuration = "Release",
    [switch]$SkipRegression,
    [ValidateRange(10, 3600)][int]$BuildTimeoutSeconds = 300,
    [ValidateRange(10, 3600)][int]$TestTimeoutSeconds = 180
)

# Windows PowerShell 5.1 and PowerShell 7. Every child process has a deadline.
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$projectRoot = Split-Path $PSScriptRoot -Parent
$reportPath = Join-Path $PSScriptRoot "ux30-windows-results.json"
$logDirectory = Join-Path $PSScriptRoot "windows-test-logs"
$results = [System.Collections.Generic.List[object]]::new()
$buildResult = [ordered]@{ name = "WPF build"; status = "not_run"; reason = "awaiting execution" }
$results.Add($buildResult)
$testProjects = @(Get-ChildItem $PSScriptRoot -Filter *.csproj -Recurse | Sort-Object FullName)
foreach ($test in $testProjects) {
    $results.Add([ordered]@{ name = $test.FullName.Substring($PSScriptRoot.Length + 1); status = "not_run"; reason = "awaiting build" })
}
$results.Add([ordered]@{
    name = "Windows visual and interaction acceptance"; status = "manual_pending"
    reason = "Build and console tests do not verify WPF rendering, DPI, touch or actual game launch."
    instructions = @("Docs/审查问题统一修复与验证.md", "Docs/弹窗与彩蛋修复验收.md")
})
$startedAt = [DateTime]::UtcNow.ToString("o")
$script:runState = "in_progress"
function Save-Report {
    $temporary = $reportPath + ".tmp"
    [ordered]@{
        kind = "Executed Windows build/regressions; manual acceptance is separate"
        startedAtUtc = $startedAt; updatedAtUtc = [DateTime]::UtcNow.ToString("o")
        status = $script:runState; buildTimeoutSeconds = $BuildTimeoutSeconds
        testTimeoutSeconds = $TestTimeoutSeconds; results = @($results.ToArray())
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $reportPath -Force
}
function Stop-ProcessTree($Process) {
    # Never pass a PID to taskkill after its original process has already exited.
    try { if ($Process.HasExited) { return $false } } catch { return $false }
    $treeRequested = $false
    try { $Process.Kill($true); $treeRequested = $true } catch { }
    if (-not $treeRequested) {
        # .NET Framework has no Kill(entireProcessTree). Bound taskkill itself:
        # a hung termination utility must not disable the test deadline.
        $killer = [System.Diagnostics.Process]::new()
        try {
            $processId = $Process.Id
            $killer.StartInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $killer.StartInfo.FileName = Join-Path ([Environment]::SystemDirectory) "taskkill.exe"
            $killer.StartInfo.Arguments = "/PID $processId /T /F"
            $killer.StartInfo.UseShellExecute = $false
            $killer.StartInfo.CreateNoWindow = $true
            $killer.StartInfo.RedirectStandardOutput = $true
            $killer.StartInfo.RedirectStandardError = $true
            if ($killer.Start()) {
                $killOutput = $killer.StandardOutput.ReadToEndAsync()
                $killErrors = $killer.StandardError.ReadToEndAsync()
                if ($killer.WaitForExit(5000)) { $treeRequested = $killer.ExitCode -eq 0 }
                else { try { $killer.Kill(); [void]$killer.WaitForExit(1000) } catch { } }
            }
        }
        catch { }
        finally { $killer.Dispose() }
    }
    try {
        if (-not $Process.HasExited) { $Process.Kill() }
        $exited = $Process.WaitForExit(5000)
        return ($exited -and $treeRequested)
    }
    catch { return $false }
}
function Invoke-BoundedDotnet($Arguments, $Result, [int]$TimeoutSeconds, [string]$LogName) {
    $Result["status"] = "running"; $Result["reason"] = $null
    Save-Report
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $process.StartInfo.FileName = $script:dotnetPath
    # Windows filesystem paths cannot contain a double quote. Every path is
    # quoted as one argument; Configuration is restricted by ValidateSet.
    $process.StartInfo.Arguments = $Arguments -join " "
    $process.StartInfo.WorkingDirectory = $projectRoot
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    # Do not leave reusable build/compiler servers detached from a timed-out run.
    $process.StartInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1"
    $process.StartInfo.EnvironmentVariables["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1"
    $process.StartInfo.EnvironmentVariables["UseSharedCompilation"] = "false"
    $stdout = $null; $stderr = $null
    try {
        if (-not $process.Start()) { throw "dotnet process did not start" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $Result["terminationConfirmed"] = Stop-ProcessTree $process
            $Result["status"] = "timed_out"
            $Result["reason"] = "Exceeded $TimeoutSeconds seconds; process tree termination requested."
            if (-not $Result["terminationConfirmed"]) { $Result["reason"] += " Termination could not be confirmed; inspect remaining dotnet/test processes." }
        }
        else {
            $Result["exitCode"] = $process.ExitCode
            $Result["status"] = $(if ($process.ExitCode -eq 0) { "passed" } else { "failed" })
        }
    }
    catch {
        $Result["status"] = "failed"; $Result["reason"] = $_.Exception.Message
        $Result["terminationConfirmed"] = Stop-ProcessTree $process
    }
    finally {
        $timer.Stop()
        $Result["durationSeconds"] = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
        $captured = [System.Text.StringBuilder]::new()
        foreach ($read in @($stdout, $stderr)) {
            if ($null -eq $read) { continue }
            try {
                if ($read.Wait(5000)) { [void]$captured.AppendLine($read.GetAwaiter().GetResult()) }
                else { [void]$captured.AppendLine("[Output capture did not finish within 5 seconds.]") }
            }
            catch { [void]$captured.AppendLine("[Output capture failed: $($_.Exception.Message)]") }
        }
        $Result["log"] = Join-Path $logDirectory ($LogName + ".log")
        try { $captured.ToString() | Set-Content -LiteralPath ($Result["log"]) -Encoding UTF8 }
        catch {
            $Result["status"] = "failed"
            $Result["reason"] = "$($Result['reason']) Output log could not be saved: $($_.Exception.Message)".Trim()
            Write-Warning ($Result["reason"])
        }
        finally { $process.Dispose() }
        Write-Host $captured.ToString()
        Write-Host "$($Result.name): $($Result.status)"
        Save-Report
    }
}

Save-Report
try {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        foreach ($result in $results) {
            if ($result.status -eq "not_run") { $result.reason = "No .NET 8 SDK found. Install the SDK and rerun this script." }
        }
        throw "未找到 .NET 8 SDK。结果已记录为 not_run。"
    }
    $script:dotnetPath = $command.Source
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $mainProject = Join-Path $projectRoot "XCL2.App.csproj"
    Invoke-BoundedDotnet @("build", "`"$mainProject`"", "--configuration", $Configuration) $buildResult $BuildTimeoutSeconds "00-build"
    for ($index = 0; $index -lt $testProjects.Count; $index++) {
        $result = $results[$index + 1]
        if ($SkipRegression) { $result.status = "skipped"; $result.reason = "Requested with -SkipRegression"; continue }
        if ($buildResult.status -ne "passed") { $result.reason = "WPF build did not pass"; continue }
        Invoke-BoundedDotnet @("run", "--project", "`"$($testProjects[$index].FullName)`"", "--configuration", $Configuration) $result $TestTimeoutSeconds ("{0:D2}-regression" -f ($index + 1))
    }
    $script:runState = "completed"
}
catch {
    $script:runState = "failed"
    Write-Warning $_.Exception.Message
}
finally {
    try { Save-Report; Write-Host "验证结果：$reportPath" }
    catch { $script:runState = "failed"; Write-Warning "验证报告无法保存：$($_.Exception.Message)" }
}
if ($script:runState -eq "failed" -or @($results | Where-Object { $_.status -in @("failed", "timed_out") }).Count -gt 0) { exit 1 }
Write-Host "已执行项目的状态与日志已保存。界面、DPI、触屏和游戏启动仍需手工验收。"

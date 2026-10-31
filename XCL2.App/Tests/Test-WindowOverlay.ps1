param(
    [switch]$NativeWindows,
    [switch]$BuildApp,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 300
)

# Windows PowerShell 5.1 / PowerShell 7. Native rendering remains a manual check.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$reportPath = Join-Path $PSScriptRoot 'window-overlay-results.json'
$logDirectory = Join-Path $PSScriptRoot 'window-overlay-logs'
$results = [System.Collections.Generic.List[object]]::new()
$startedAt = [DateTime]::UtcNow.ToString('o')
$script:status = 'running'

function Save-Report {
    [ordered]@{
        startedAtUtc = $startedAt
        updatedAtUtc = [DateTime]::UtcNow.ToString('o')
        status = $script:status
        results = @($results.ToArray())
        manualAcceptance = [ordered]@{
            status = 'not_executed_by_script'
            checks = @(
                'Fresh launcher configuration opens at 1125x720 DIP; resize to 700x450, close/reopen, and verify the saved size and position.',
                'At compact size, navigate every page and open dialogs; scroll to all controls. Resizing must preserve the current page and unsaved settings.',
                'Restore saved maximized state; F11 round trip, close in F11, minimize/tray, mixed-DPI and unplugged monitors must preserve normal geometry.',
                'Windows 11: cold start, minimize/taskbar restore, maximize/restore, Alt+F4 and close; repeat rapidly.',
                'Cancel each close confirmation and verify the window stays visible, correctly sized and interactive.',
                'Turn off Windows animation effects and app animations; transitions must remain immediate and functional.',
                'Fresh configuration starts without click-through or a settings gear; metrics begin at the top without a blank heading. Saved choices survive restart.',
                'With click-through enabled, another process receives clicks across the entire overlay; only the 14-DIP vector settings control intercepts. Disable it and the gear disappears.',
                'The small settings control opens the same menu as right-click; move the overlay with its handle and with click-through disabled.',
                'Check lock/unlock, scrolling, saving position, reopen, mixed-DPI/negative-coordinate monitors and display disconnect.',
                'Edit hardware display metadata, cancel/save, restart, and test missing driver data; manual values must not be presented as measured values.',
                'With a custom performance template, open hardware settings, add suggested missing fields, cancel/save, and check that existing text and escaped braces are retained.',
                'Close/disable the overlay while its menu is open; no orphan settings control or background sampling remains.',
                'In a legacy GLFW game, hold movement and jump while dragging the camera with a third finger; lift each finger separately, change focus, and verify no stuck or prematurely released keys.',
                'Compare legacy camera pacing on the same physical touch hardware; test menu dragging, gameplay, and switching between them. Synthetic input is not a real-game result.',
                'Server probe: leave the port empty, use host:port and [IPv6]:port, cancel a stalled server, and separately detect local link speed without a reachable server.',
                'Port probe: find the new toolbox tool, enter multiple IPv4/IPv6 addresses, check range and per-IP maximum, reject invalid input, and verify deduplicated totals.',
                'Port probe: scan a local listener, cancel and restart, switch away/back, resize to 500x320, copy discovered endpoints, and distinguish TCP openness from Minecraft service detection.'
            )
        }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}

function Invoke-Check([string]$Name, [string[]]$Arguments) {
    $result = [ordered]@{ name = $Name; status = 'running'; exitCode = $null; elapsedSeconds = $null; detail = $null }
    $results.Add($result)
    Save-Report
    $process = [System.Diagnostics.Process]::new()
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $process.StartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $process.StartInfo.FileName = $script:dotnetPath
        # Project paths cannot contain quotes on Windows. Quote paths at call sites.
        $process.StartInfo.Arguments = $Arguments -join ' '
        $process.StartInfo.WorkingDirectory = $projectRoot
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $false
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        $process.StartInfo.EnvironmentVariables['MSBUILDDISABLENODEREUSE'] = '1'
        $process.StartInfo.EnvironmentVariables['DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER'] = '1'
        if (-not $process.Start()) { throw 'Failed to start dotnet.' }
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $result.status = 'timeout'
            # Process.Kill(true) exists in PowerShell 7; taskkill covers Windows PowerShell.
            try { $process.Kill($true) }
            catch {
                if (-not $process.HasExited) {
                    $killer = [System.Diagnostics.Process]::new()
                    try {
                        $killer.StartInfo.FileName = Join-Path ([Environment]::SystemDirectory) 'taskkill.exe'
                        $killer.StartInfo.Arguments = '/PID ' + $process.Id + ' /T /F'
                        $killer.StartInfo.UseShellExecute = $false
                        $killer.StartInfo.CreateNoWindow = $true
                        if ($killer.Start() -and -not $killer.WaitForExit(5000)) { $killer.Kill() }
                    }
                    finally { $killer.Dispose() }
                }
            }
            [void]$process.WaitForExit(5000)
            throw "$Name exceeded $TimeoutSeconds seconds."
        }
        $result.exitCode = $process.ExitCode
        $text = $output.GetAwaiter().GetResult() + $errors.GetAwaiter().GetResult()
        $text | Set-Content -LiteralPath (Join-Path $logDirectory ($Name + '.log')) -Encoding UTF8
        Write-Host $text
        if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)." }
        $result.status = 'passed'
    }
    catch {
        if ($result.status -ne 'timeout') { $result.status = 'failed' }
        $result.detail = $_.Exception.Message
        throw
    }
    finally {
        $clock.Stop()
        $result.elapsedSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2)
        $process.Dispose()
        Save-Report
    }
}

try {
    $script:dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    foreach ($name in @('PerformanceMetricsRegression', 'HardwareTerracottaRegression', 'HardwareDetailsRegression', 'MemoryMetadataRegression', 'WindowPlacementMathRegression', 'PresentationPreferencesRegression', 'ServerPingRegression', 'ServerPortScanRegression', 'FeatureSearchRegression', 'LegacyTouchRegression', 'LegacyModeRegression', 'LegacyCursorRegression', 'InputExperienceRegression', 'LegacyLaunchRegression')) {
        $project = Join-Path $PSScriptRoot "$name/$name.csproj"
        if (-not (Test-Path -LiteralPath $project)) { throw "Missing regression project: $project" }
        Invoke-Check $name @('run', '--project', ('"' + $project + '"'), '-c', $Configuration, '--disable-build-servers')
    }
    if ($NativeWindows) {
        if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'NativeWindows requires an interactive Windows desktop.' }
        foreach ($name in @('WindowLifecycleRegression', 'WindowPlacementNativeRegression', 'OverlayLifecycleRegression', 'PerformanceOverlayInputRegression', 'LegacyPointerRegression')) {
            $project = Join-Path $PSScriptRoot "$name/$name.csproj"
            Invoke-Check $name @('run', '--project', ('"' + $project + '"'), '-c', $Configuration, '--disable-build-servers')
        }
    }
    else { $results.Add([ordered]@{ name = 'Native Windows regressions'; status = 'skipped'; detail = 'Pass -NativeWindows on an interactive Windows desktop.' }) }
    if ($BuildApp) {
        # Preserve the existing real-key validation. Do not synthesize keys or disable that target.
        Invoke-Check 'WPF-build' @('build', ('"' + (Join-Path $projectRoot 'XCL2.App.csproj') + '"'), '-c', $Configuration, '-p:SkipNativePreview=true', '--disable-build-servers')
    }
    else { $results.Add([ordered]@{ name = 'WPF build'; status = 'skipped'; detail = 'Pass -BuildApp with the required real project keys configured.' }) }
    $script:status = 'selected_checks_passed_manual_acceptance_pending'
    Write-Host 'Selected automated checks passed. Compact layout, native animation, cross-process pointer behavior and real-game touch pacing still require the manual checklist.'
}
catch {
    $script:status = 'failed'
    $results.Add([ordered]@{ name = 'Runner'; status = 'failed'; detail = $_.Exception.Message })
    throw
}
finally { Save-Report }

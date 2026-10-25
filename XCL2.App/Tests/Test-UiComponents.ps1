param(
    [switch]$NativeWindows,
    [switch]$BuildApp,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 300
)

# Windows PowerShell 5.1 / PowerShell 7. Native rendering remains a manual check.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$reportPath = Join-Path $PSScriptRoot 'ui-components-results.json'
$logDirectory = Join-Path $PSScriptRoot 'ui-components-logs'
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
                'Legacy appearance: original navigation text, download categories, filters and actions remain present; the four instance-conflict buttons are centered.',
                'Sidebar: left/right, 160/360 widths, icons, 11/22 font size, long names, restart and reset; check 420, 760, 1280 and 1912 DIP windows.',
                'Sidebar on the right: AI drawer, reminder panel, debug panel and compact drawer stay inside the correct content area; edits remain intact.',
                'Preview downloads: every category, search, filters, refresh, paging, favorites, details, install, queue and cancellation; toggle preview repeatedly with results and details open.',
                'Preview layout: widths 320/348/640/900, short height, 100/150 percent scaling, long project titles and large covers; all actions remain reachable.',
                'Mods: no instance, missing mods folder, genuinely empty, installed, disabled-only, filtered-empty, malformed jar and access error; externally add/remove/rename the mods directory.',
                'Add mods: selected instance version/loader and real isolated/shared installation directory agree; no download starts merely by opening the page.',
                'Motion: taskbar and tray restore, maximize/restore, F11, rapid repeated activation, close cancellation, secondary/tertiary forward/back, restored list focus.',
                'Notifications: repeated tooltip/menu/dialog/toast; 0.2 seconds and intensity 30, edited values, preview cancellation, restart, reduced motion and animations off.',
                'Components on a disposable instance: each supported loader add/update/downgrade/remove, Minecraft replacement, missing Java and incompatible build; validate a real launch after success.',
                'Maintenance: preserve identity/worlds/mods/config; cancel downloads and interrupt a transaction; reopen for recovery; inspect the complete backup directory.',
                'Concurrency: same instance launched twice, parent and explicit-jar dependencies, second launcher maintenance, running game and pending recovery record; protected files must remain untouched.',
                'Composite components require explicit whole-profile replacement; Mod-shaped API/QSL/OptiFine entries navigate to Mod management, including from nested instance settings.'
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
    foreach ($name in @('SidebarPreferencesRegression', 'AnimationPreferencesRegression', 'ModManagementRegression', 'ComponentMaintenanceRegression', 'LaunchConstructionRegression', 'PreviewNavigationRegression', 'InstanceUxRegression', 'LegacyLaunchRegression')) {
        $project = Join-Path $PSScriptRoot "$name/$name.csproj"
        if (-not (Test-Path -LiteralPath $project)) { throw "Missing regression project: $project" }
        Invoke-Check $name @('run', '--project', ('"' + $project + '"'), '-c', $Configuration, '--disable-build-servers')
    }
    if ($NativeWindows) {
        if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'NativeWindows requires an interactive Windows desktop.' }
        foreach ($name in @('SidebarLayoutRegression', 'PreviewVisualRegression', 'PopupAnimationRegression', 'ToastMotionRegression', 'WindowLifecycleRegression', 'OverlayLifecycleRegression')) {
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
    Write-Host 'Selected automated checks passed. Real WPF appearance, DWM motion and real Minecraft installation/launch still require the manual checklist.'
}
catch {
    $script:status = 'failed'
    $results.Add([ordered]@{ name = 'Runner'; status = 'failed'; detail = $_.Exception.Message })
    throw
}
finally { Save-Report }

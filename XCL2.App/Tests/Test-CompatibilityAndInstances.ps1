param(
    [switch]$NativeWindows,
    [switch]$BuildApp,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 300
)

# Windows PowerShell 5.1 / PowerShell 7. Native rendering remains a manual check.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$reportPath = Join-Path $PSScriptRoot 'compatibility-instances-results.json'
$logDirectory = Join-Path $PSScriptRoot 'compatibility-instances-logs'
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
                'Build on Windows with the real project keys; do not mark source-only checks as a successful WPF build.',
                'Runtime boundary: .NET 8 does not officially support Vista/7/8/8.1. Test a known working runtime separately; API fallbacks cannot repair a runtime that cannot start.',
                'Compatibility: automatic/forced/disabled, Vista/7 full layer, 8/8.1 standard layer, Windows 10/11; settings persist after restart.',
                'Prompts: startup reminder says 正在打开兼容模式; disabling below 8.1 warns about unavailable functions; forcing on 10+ warns about performance.',
                'Theme: build 17762 and earlier warn when system-auto mode is enabled; 17763+ responds to light/dark changes; absent or invalid registry data does not crash.',
                'Network on Windows 7: CurseForge list/details/download; valid/invalid API key, HTTP 401/403/429, TLS failure, proxy and cancellation. Never disable certificate validation.',
                'Instances: standalone .minecraft, Prism/MultiMC instances root and one instance, CurseForge Instances, legacy Modrinth profile.json and current app.db.',
                'Import uses the correct game directory, loader and Minecraft version, preserves source files and saves, handles name conflicts and rejects malformed/path-traversal metadata.',
                'Launch each imported loader on a disposable instance; verify source saves/mods/config remain intact after cancellation, maintenance and removal.',
                'Native dialogs/window/input/toast on old Windows fail gracefully when an API is absent; cancellation, resize and shutdown remain usable.'
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
    # Overwrite extraction can retain deleted C# partials. Report them before a
    # build instead of renaming current members or deleting unknown local code.
    $viewFiles = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Views') -Filter '*.cs' -File -Recurse |
        Where-Object { $_.FullName -ne (Join-Path $projectRoot 'Views/InstalledVersionComponentsDialog.cs') }
    $sourceText = ($viewFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    foreach ($member in @('NavigateToModDownloads', 'ManageComponents_Click')) {
        $pattern = '(?m)^\s*(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?(?:void|Task)\s+' + $member + '\s*\('
        $count = [regex]::Matches($sourceText, $pattern).Count
        if ($count -ne 1) { throw "Expected one $member definition, found $count. Extract the package into an empty directory or use tools/upgrade_source.py after reviewing its dry-run." }
    }
    $dialogCount = [regex]::Matches($sourceText, '(?m)^\s*public\s+(?:(?:sealed|partial)\s+)*class\s+InstalledVersionComponentsDialog\b').Count
    if ($dialogCount -ne 1) { throw "Expected one active InstalledVersionComponentsDialog source declaration, found $dialogCount. Use a clean source directory." }
    $results.Add([ordered]@{ name = 'Source-layout-preflight'; status = 'passed'; detail = 'Known retired dialog excluded; current navigation and dialog handler each have one declaration.' })
    $script:dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    foreach ($name in @('WindowsCompatibilityRegression', 'SystemThemeDetectionRegression', 'LegacyNetworkRegression', 'ExternalInstanceRegression', 'InstanceMutationRegression', 'WorldArchiveRegression', 'InstallerProcessLifetimeRegression', 'DownloadQueueRegression', 'InstanceUxRegression', 'InstanceExperienceRegression', 'ModManagementRegression', 'WebView2Regression', 'ComponentMaintenanceRegression', 'DownloadFileRegression', 'ModpackImportRegression', 'LaunchConstructionRegression', 'LegacyLaunchRegression')) {
        $project = Join-Path $PSScriptRoot "$name/$name.csproj"
        if (-not (Test-Path -LiteralPath $project)) { throw "Missing regression project: $project" }
        Invoke-Check $name @('run', '--project', ('"' + $project + '"'), '-c', $Configuration, '--disable-build-servers')
    }
    if ($NativeWindows) {
        if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'NativeWindows requires an interactive Windows desktop.' }
        foreach ($name in @('WindowTouchOverlayRegression', 'WindowLifecycleRegression', 'WindowPlacementNativeRegression', 'ToastMotionRegression', 'OverlayLifecycleRegression')) {
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
    Write-Host 'Selected automated checks passed. The report lists any skipped build/native checks; old-Windows networking and imported-instance launch require manual acceptance.'
}
catch {
    $script:status = 'failed'
    $results.Add([ordered]@{ name = 'Runner'; status = 'failed'; detail = $_.Exception.Message })
    throw
}
finally { Save-Report }

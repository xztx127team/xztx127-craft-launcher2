param(
    [switch]$BuildApp,
    [switch]$SkipWindows,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 300,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'pcl200-results')
)

# PowerShell 5.1/7. Every project gets a result; failures do not hide later tests.
# This executes code. Static source checks alone do not count as a passing test.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$startedAt = [DateTime]::UtcNow.ToString('o')
$results = [System.Collections.Generic.List[object]]::new()
$projects = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.csproj' -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Sort-Object FullName)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$reportPath = Join-Path $OutputDirectory 'results.json'

function Save-Report {
    [ordered]@{
        startedAtUtc = $startedAt
        updatedAtUtc = [DateTime]::UtcNow.ToString('o')
        hostIsWindows = $onWindows
        configuration = $Configuration
        results = @($results.ToArray())
        limitations = @(
            'Passing unit regressions does not establish Windows UI, network provider, or real Minecraft acceptance.',
            'The application build requires the real XCL2_PRE_KEY and XCL2_REL_KEY; this script never substitutes keys.',
            'A skipped project is not a passing project.'
        )
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
        # Every generated argument is constant or a quoted project path. Windows paths cannot contain quotes.
        $process.StartInfo.Arguments = $Arguments -join ' '
        $process.StartInfo.WorkingDirectory = $projectRoot
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        $process.StartInfo.EnvironmentVariables['MSBUILDDISABLENODEREUSE'] = '1'
        $process.StartInfo.EnvironmentVariables['DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER'] = '1'
        if (-not $process.Start()) { throw 'Failed to start dotnet.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $result.status = 'timeout'
            try { $process.Kill($true) }
            catch {
                if ($onWindows -and -not $process.HasExited) {
                    $killer = [System.Diagnostics.Process]::new()
                    try {
                        $killer.StartInfo.FileName = Join-Path ([Environment]::SystemDirectory) 'taskkill.exe'
                        $killer.StartInfo.Arguments = '/PID ' + $process.Id + ' /T /F'
                        $killer.StartInfo.UseShellExecute = $false
                        if ($killer.Start() -and -not $killer.WaitForExit(5000)) { $killer.Kill() }
                    }
                    finally { $killer.Dispose() }
                }
                elseif (-not $process.HasExited) { $process.Kill() }
            }
            [void]$process.WaitForExit(5000)
            throw "$Name exceeded $TimeoutSeconds seconds."
        }
        $result.exitCode = $process.ExitCode
        # An orphaned child may keep redirected pipe handles open after dotnet exits.
        # Do not turn the outer process timeout into an unbounded stream wait.
        $drains = [System.Threading.Tasks.Task[]]@($stdout, $stderr)
        if (-not [System.Threading.Tasks.Task]::WaitAll($drains, 5000)) {
            throw "$Name exited but its output pipes did not close within five seconds."
        }
        ($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()) |
            Set-Content -LiteralPath (Join-Path $OutputDirectory ($Name + '.log')) -Encoding UTF8
        if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)." }
        $result.status = 'passed'
    }
    catch {
        if ($result.status -ne 'timeout') { $result.status = 'failed' }
        $result.detail = $_.Exception.Message
        Write-Warning $result.detail
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
    Invoke-Check 'DotnetEnvironment' @('--info')
    if ($results[$results.Count - 1].status -ne 'passed') {
        throw 'dotnet cannot start. Regression execution is blocked, not passed.'
    }
    foreach ($project in $projects) {
        [xml]$definition = Get-Content -LiteralPath $project.FullName -Raw
        $frameworks = @($definition.SelectNodes('//TargetFramework|//TargetFrameworks') | ForEach-Object { $_.InnerText }) -join ';'
        $windowsOnly = $frameworks -match '(?i)-windows'
        if ($windowsOnly -and (-not $onWindows -or $SkipWindows)) {
            $results.Add([ordered]@{ name = $project.BaseName; status = 'skipped'; detail = 'Windows-targeted project requires Windows and no -SkipWindows flag.' })
            Save-Report
            continue
        }
        Invoke-Check $project.BaseName @('run', '--project', ('"' + $project.FullName + '"'), '-c', $Configuration, '--disable-build-servers')
    }
    if ($BuildApp) {
        if (-not $onWindows) {
            $results.Add([ordered]@{ name = 'ApplicationBuild'; status = 'skipped'; detail = 'WPF application acceptance requires Windows.' })
        }
        else {
            Invoke-Check 'ApplicationBuild' @('build', ('"' + (Join-Path $projectRoot 'XCL2.App.csproj') + '"'), '-c', $Configuration, '-p:SkipNativePreview=true', '--disable-build-servers')
        }
    }
    else {
        $results.Add([ordered]@{ name = 'ApplicationBuild'; status = 'skipped'; detail = 'Use -BuildApp with the genuine project keys to build the WPF application.' })
    }
}
catch {
    $results.Add([ordered]@{ name = 'Runner'; status = 'failed'; detail = $_.Exception.Message })
    Write-Warning $_.Exception.Message
    foreach ($project in $projects) {
        if (@($results | Where-Object { $_.name -eq $project.BaseName }).Count -eq 0) {
            $results.Add([ordered]@{ name = $project.BaseName; status = 'blocked'; detail = 'Runner/environment failure prevented execution.' })
        }
    }
}
finally { Save-Report }

$failed = @($results | Where-Object { $_.status -in @('failed', 'timeout', 'running', 'blocked') }).Count
$passed = @($results | Where-Object { $_.status -eq 'passed' }).Count
$skipped = @($results | Where-Object { $_.status -eq 'skipped' }).Count
Write-Host "Passed: $passed; failed/timeout: $failed; skipped: $skipped. Report: $reportPath"
if ($failed -gt 0) { exit 1 }
exit 0

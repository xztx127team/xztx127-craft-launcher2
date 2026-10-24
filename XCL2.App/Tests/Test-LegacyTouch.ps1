param([switch]$NativeWindowsInput, [switch]$BuildApp)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$null = Get-Command dotnet -ErrorAction Stop

# Pure input policy tests; no game installation or external test framework is required.
$projects = @('LegacyTouchRegression', 'LegacyModeRegression', 'LegacyCursorRegression', 'InputExperienceRegression',
    'LegacyLaunchRegression', 'WorkbenchRegression')
foreach ($project in $projects) {
    Write-Host "Running $project"
    & dotnet run --project (Join-Path $PSScriptRoot "$project/$project.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "$project failed with exit code $LASTEXITCODE" }
}

if ($NativeWindowsInput) {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'NativeWindowsInput requires an interactive Windows desktop.'
    }
    Write-Host 'Native input test: keep the test game in front and do not move the mouse or touch the screen.'
    & dotnet run --project (Join-Path $PSScriptRoot 'LegacyPointerRegression/LegacyPointerRegression.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw "LegacyPointerRegression failed with exit code $LASTEXITCODE" }
}

if ($BuildApp) {
    # Preserve the project's existing key checks and WinUI publishing requirements.
    & dotnet build (Join-Path $projectRoot 'XCL2.App.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "App build failed with exit code $LASTEXITCODE" }
}
Write-Host 'All selected legacy touch checks passed.'

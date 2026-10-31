param([switch]$BuildLauncher, [switch]$IncludeWindowsUi)
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $PSScriptRoot -Parent
foreach ($name in @('SummaryTranslationRegression', 'LauncherExperienceRegression', 'MiniWorldRegression',
    'MiniWorldCdkRegression', 'AnimationPreferencesRegression', 'AgentAnimationRegression')) {
    Write-Host "Running $name"
    & dotnet run --project (Join-Path $PSScriptRoot "$name/$name.csproj") --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "$name failed ($LASTEXITCODE)." }
}
if ($IncludeWindowsUi) {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Requires a Windows desktop.' }
    & dotnet run --project (Join-Path $PSScriptRoot 'WindowPreferencesRegression/WindowPreferencesRegression.csproj') --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Window preference checks failed.' }
}
if ($BuildLauncher) {
    # Preserve the source project's existing Bedrock key requirements.
    & dotnet build (Join-Path $appRoot 'XCL2.App.csproj') --configuration Release -p:SkipNativePreview=true
    if ($LASTEXITCODE -ne 0) { throw "Launcher build failed ($LASTEXITCODE)." }
}
Write-Host 'All selected checks passed.'

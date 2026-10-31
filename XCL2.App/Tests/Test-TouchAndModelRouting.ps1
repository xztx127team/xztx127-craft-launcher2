param([switch]$BuildApp)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projects = @('LegacyTouchRegression', 'AiHttpRegression', 'WorkbenchRegression', 'AiActivityRegression')
foreach ($project in $projects) {
    Write-Host "Running $project"
    & dotnet run --project (Join-Path $PSScriptRoot "$project/$project.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "$project failed with exit code $LASTEXITCODE" }
}
if ($BuildApp) {
    # Keep the original project's signing/configuration and native preview gates intact.
    & dotnet build (Join-Path $projectRoot 'XCL2.App.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "App build failed with exit code $LASTEXITCODE" }
}
Write-Host 'All requested checks passed.'

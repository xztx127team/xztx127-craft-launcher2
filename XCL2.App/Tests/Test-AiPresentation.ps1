param([switch]$BuildLauncher)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Requires a Windows desktop and .NET 8 SDK.' }
foreach ($name in @('AiPresentationRegression', 'PreviewVisualRegression', 'AiSideDrawerRegression', 'AiActivityRegression')) {
    Write-Host "Running $name"
    & dotnet run --project (Join-Path $PSScriptRoot "$name/$name.csproj") --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "$name failed ($LASTEXITCODE)." }
}
if ($BuildLauncher) {
    & dotnet build (Join-Path (Split-Path $PSScriptRoot -Parent) 'XCL2.App.csproj') --configuration Release -p:SkipNativePreview=true
    if ($LASTEXITCODE -ne 0) { throw "Launcher build failed ($LASTEXITCODE)." }
}
Write-Host 'All selected AI presentation checks passed.'

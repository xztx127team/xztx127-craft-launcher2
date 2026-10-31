param([switch]$IncludeWindowsUi, [switch]$BuildLauncher)
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $PSScriptRoot -Parent

function Invoke-Regression([string]$name, [string[]]$extraArgs = @()) {
    $project = Join-Path $PSScriptRoot "$name/$name.csproj"
    Write-Host "Running $name"
    & dotnet run --project $project --configuration Release -- @extraArgs
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)." }
}

foreach ($name in @('SettingsValidationRegression', 'AiMarkdownCopyRegression',
    'StickyNoteOptionsRegression', 'ServerPingRegression', 'WorldMapRegression',
    'HongheAssetsRegression', 'ExternalLinkRegression', 'FeatureSearchRegression')) {
    Invoke-Regression $name
}
Invoke-Regression 'UtilityToolsRegression' @($appRoot)

if ($IncludeWindowsUi) {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Windows UI regression tests require an interactive Windows desktop.'
    }
    foreach ($name in @('AiTextMenuRegression', 'StickyNoteWindowRegression',
        'PerformanceOverlayInputRegression', 'WebView2Regression')) {
        Invoke-Regression $name
    }
}

if ($BuildLauncher) {
    # Existing build-key validation is intentionally preserved. Use your own configured keys.
    & dotnet build (Join-Path $appRoot 'XCL2.App.csproj') --configuration Release -p:SkipNativePreview=true
    if ($LASTEXITCODE -ne 0) { throw "Launcher build failed (exit $LASTEXITCODE)." }
}
Write-Host 'All requested checks selected for this run passed.'

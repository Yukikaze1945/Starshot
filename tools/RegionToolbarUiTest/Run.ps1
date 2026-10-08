param([string]$Configuration = 'Debug', [string]$ReportDirectory, [switch]$HdrDiagnostics, [string]$DotNetPath = 'dotnet', [string]$PublishedApp, [string]$ReferenceApp)
$ErrorActionPreference = 'Stop'
$toolbarRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$toolbarSdk = $DotNetPath
$toolbarApp = Join-Path $toolbarRoot "build/toolbar-$($Configuration.ToLowerInvariant())"
if ($PublishedApp) { $toolbarApp = Join-Path $toolbarRoot 'build/toolbar-published' }
if (-not $ReportDirectory) { $ReportDirectory = Join-Path $toolbarRoot 'docs/reports/region-toolbar-20261009' }
New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
# Use the real assembly/resources in an independent test application whose launch is
# overridden, so the installed application's singleton/config/tray are never involved.
if ($PublishedApp) {
    $toolbarPublish = (Resolve-Path -LiteralPath $PublishedApp).Path
    if (-not (Test-Path -LiteralPath (Join-Path $toolbarPublish 'Starshot.dll'))) { throw 'Published Starshot.dll is missing' }
    New-Item -ItemType Directory -Force -Path $toolbarApp | Out-Null
    Copy-Item -Path (Join-Path $toolbarPublish '*') -Destination $toolbarApp -Recurse -Force
} else {
    & $toolbarSdk build (Join-Path $toolbarRoot 'src/Starshot/Starshot.csproj') -c $Configuration -p:Platform=x64 "-p:OutputPath=$toolbarApp/" --no-restore -v:quiet
    if ($LASTEXITCODE) { throw 'Starshot build failed' }
}
$toolbarReference = Join-Path $toolbarApp 'Starshot.dll'
# ILLink rewrites runtime assembly references to System.Private.CoreLib. Compile
# against the matching pre-trim assembly; execution still uses the copied publish.
if ($ReferenceApp) { $toolbarReference = (Resolve-Path -LiteralPath $ReferenceApp).Path }
& $toolbarSdk build $PSScriptRoot -c $Configuration "-p:OutputPath=$toolbarApp/" "-p:StarshotTestAppPath=$toolbarReference" -p:RestoreSources=https://api.nuget.org/v3/index.json -v:quiet
if ($LASTEXITCODE) { throw 'UI harness build failed' }
Copy-Item -LiteralPath (Join-Path $toolbarApp 'Starshot.pri') -Destination (Join-Path $toolbarApp 'resources.pri') -Force
Copy-Item -LiteralPath (Join-Path $toolbarApp 'Starshot.pri') -Destination (Join-Path $toolbarApp 'RegionToolbarUiTest.pri') -Force
& (Join-Path $toolbarRoot 'tools/LightweightCaptureTest/Build-GpuMemory.ps1') -OutputDirectory $toolbarApp
$toolbarSavedHook = $env:DOTNET_STARTUP_HOOKS
$toolbarSavedReport = $env:STARSHOT_TOOLBAR_UI_REPORT
$toolbarSavedDiagnostics = $env:STARSHOT_HDR_DIAGNOSTICS
$toolbarProcess = $null
try {
    Remove-Item -LiteralPath (Join-Path $ReportDirectory 'xaml-interaction.log') -ErrorAction SilentlyContinue
    $env:DOTNET_STARTUP_HOOKS = $null
    $env:STARSHOT_TOOLBAR_UI_REPORT = [IO.Path]::GetFullPath($ReportDirectory)
    $env:STARSHOT_HDR_DIAGNOSTICS = $(if ($HdrDiagnostics) { '1' } else { $null })
    $toolbarProcess = Start-Process -FilePath (Join-Path $toolbarApp 'RegionToolbarUiTest.exe') -WindowStyle Hidden -PassThru
    # Debug FP16 conversion is intentionally unoptimized; allow startup + five synthetic
    # 4K checks and native XAML shutdown without turning a completed test into a false timeout.
    if (-not $toolbarProcess.WaitForExit(60000)) { throw 'Synthetic UI test timed out' }
    Get-Content (Join-Path $ReportDirectory 'xaml-interaction.log')
    if ($toolbarProcess.ExitCode) { throw "Synthetic UI test failed: $($toolbarProcess.ExitCode)" }
} finally {
    if ($toolbarProcess -and -not $toolbarProcess.HasExited) { Stop-Process -Id $toolbarProcess.Id }
    $env:DOTNET_STARTUP_HOOKS = $toolbarSavedHook
    $env:STARSHOT_TOOLBAR_UI_REPORT = $toolbarSavedReport
    $env:STARSHOT_HDR_DIAGNOSTICS = $toolbarSavedDiagnostics
}

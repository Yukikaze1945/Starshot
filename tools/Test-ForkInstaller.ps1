[CmdletBinding()]
param([Parameter(Mandatory)][string]$SetupPath, [Parameter(Mandatory)][string]$Version, [switch]$InspectUi)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$reportDir = Join-Path $projectRoot "build\fork-release\$Version\validation"
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryBase "Starshot-installer-$([guid]::NewGuid().ToString('N'))"
$installRoot = Join-Path $testRoot 'app'
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$steps = [Collections.Generic.List[string]]::new()
function Install-TestPackage([string]$LogName) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=', "/DIR=`"$installRoot`"", "/LOG=`"$(Join-Path $reportDir $LogName)`"")
    $installer = Start-Process -FilePath $setup -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($installer.ExitCode -ne 0) { throw "Installer failed: $($installer.ExitCode)" }
}
try {
    if (Get-Process Starshot -ErrorAction SilentlyContinue | Where-Object { $_.Path }) { throw 'Exit existing Starshot before isolated installation tests.' }
    Install-TestPackage 'install.log'
    if (!(Test-Path -LiteralPath (Join-Path $installRoot "app-$Version\WebUI\index.html"))) { throw 'WebUI is missing from installed payload.' }
    if ((Get-Content -LiteralPath (Join-Path $installRoot 'version.ini') -Raw).Trim() -ne "version=$Version") { throw 'Launcher version is incorrect.' }
    $steps.Add('PASS per-user isolated installation, version.ini and local WebUI')
    $configPath = Join-Path $installRoot 'config.sjson'
    @{Theme='1';EnableAutoUpdateCheck='False';LogFolder=(Join-Path $installRoot 'cache');ValidationSentinel='keep-me'} | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
    $before = (Get-FileHash -LiteralPath $configPath).Hash
    Install-TestPackage 'reinstall.log'
    if ((Get-FileHash -LiteralPath $configPath).Hash -ne $before) { throw 'Reinstall overwrote configuration.' }
    $steps.Add('PASS covering installation preserves existing configuration byte-for-byte')
    Start-Process -FilePath (Join-Path $installRoot 'Starshot.exe') -WindowStyle Hidden
    Start-Sleep -Seconds 8
    $process = Get-Process Starshot -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase) }
    if (!$process -or $process.Count -ne 1) { throw 'Installed launcher did not start the expected main process.' }
    $steps.Add("PASS root launcher starts app-$Version; PID $($process.Id)")
    @{installRoot=$installRoot;pid=$process.Id;reportDir=$reportDir} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportDir 'ready.json') -Encoding utf8
    if ($InspectUi) {
        $deadline = [DateTime]::UtcNow.AddMinutes(5)
        while (!(Test-Path -LiteralPath (Join-Path $reportDir 'finish-ui')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 2 }
    }
} finally {
    Get-Process Starshot -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force
    if (Test-Path -LiteralPath (Join-Path $installRoot 'unins000.exe')) {
        $uninstall = Start-Process -FilePath (Join-Path $installRoot 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru -Wait -WindowStyle Hidden
        if ($uninstall.ExitCode -ne 0) { throw "Uninstall failed: $($uninstall.ExitCode)" }
        if ((Test-Path -LiteralPath (Join-Path $installRoot "app-$Version")) -or (Test-Path -LiteralPath (Join-Path $installRoot 'Starshot.exe'))) { throw 'Uninstall left program binaries behind.' }
        if (!(Test-Path -LiteralPath (Join-Path $installRoot 'config.sjson'))) { throw 'Uninstall removed user configuration.' }
        $steps.Add('PASS uninstall removes binaries and preserves user configuration')
    }
    $steps | Set-Content -LiteralPath (Join-Path $reportDir 'results.txt') -Encoding utf8
    $resolvedTest = [IO.Path]::GetFullPath($testRoot)
    if (!$resolvedTest.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedTest) -notlike 'Starshot-installer-*') { throw 'Unsafe temporary cleanup path.' }
    if (Test-Path -LiteralPath $resolvedTest) { Remove-Item -LiteralPath $resolvedTest -Recurse -Force }
}
$steps | Write-Host

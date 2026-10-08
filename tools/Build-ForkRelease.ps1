[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-preview\.\d+)?$')][string]$Version,
    [Parameter(Mandatory)][string]$LauncherPath,
    [Parameter(Mandatory)][string]$RuntimeInstaller,
    [string]$DotNetPath = 'dotnet',
    [string]$IsccPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    [switch]$NoRestore,
    [string]$PublishedApp
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$releaseRoot = Join-Path $projectRoot "build\fork-release\$Version"
$stageRoot = Join-Path $releaseRoot "work-$([guid]::NewGuid().ToString('N'))"
$payloadRoot = Join-Path $stageRoot 'payload'
$appRoot = Join-Path $payloadRoot "app-$Version"
$launcher = (Resolve-Path -LiteralPath $LauncherPath).Path
$runtime = (Resolve-Path -LiteralPath $RuntimeInstaller).Path
$compiler = (Resolve-Path -LiteralPath $IsccPath).Path
$runtimeSignature = Get-AuthenticodeSignature -LiteralPath $runtime
if ($runtimeSignature.Status -ne 'Valid' -or $runtimeSignature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    throw 'The offline WebView2 Runtime must have a valid Microsoft signature.'
}
if ((Get-Item -LiteralPath $runtime).Length -lt 50MB) { throw 'Expected the full offline WebView2 installer, not a network bootstrapper.' }
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
$publishArgs = @('publish', (Join-Path $projectRoot 'src\Starshot\Starshot.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', "-p:Version=$Version", '-o', $appRoot)
if ($NoRestore) { $publishArgs += '--no-restore' }
if ($PublishedApp) {
    $published = (Resolve-Path -LiteralPath $PublishedApp).Path
    if ((Get-Item -LiteralPath (Join-Path $published 'Starshot.dll')).VersionInfo.ProductVersion -ne $Version) {
        throw 'Existing application publish has a different version.'
    }
    Copy-Item -Path (Join-Path $published '*') -Destination $appRoot -Recurse
} else {
    & $DotNetPath @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
}
Copy-Item -LiteralPath $launcher -Destination (Join-Path $payloadRoot 'Starshot.exe')
[IO.File]::WriteAllText((Join-Path $payloadRoot 'version.ini'), "version=$Version`r`n", [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $payloadRoot 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot 'third_party\libultrahdr\v2.0.2\LICENSE') -Destination (Join-Path $payloadRoot 'libultrahdr-LICENSE.txt')
$unexpected = Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | Where-Object {
    $_.Name -match '(^config\.|\.sjson$|\.dpapi$|StarshotDatabase|\.log$)' -or $_.FullName -match '\\(cache|EBWebView|User Data)\\'
}
if ($unexpected) { throw "User data detected in release payload: $($unexpected.FullName -join ', ')" }
$uhdrHash = (Get-FileHash -LiteralPath (Join-Path $appRoot 'uhdr.dll') -Algorithm SHA256).Hash
& (Join-Path $projectRoot 'scripts\Restore-VideoEncoder.ps1') -VerifyOnly (Join-Path $appRoot 'VideoEncoder')
if ($uhdrHash -ne '415EE12ED6E979D1A96A495AFCB634D54FBACF69E5B9E45C95C38793C737384B') { throw 'Published libultrahdr does not match the verified custom DLL.' }
$zipPath = Join-Path $releaseRoot "Starshot-$Version-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) { throw "Release archive already exists: $zipPath. Use a new version or remove that exact staging artifact." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($payloadRoot, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
& $compiler "/DReleaseVersion=$Version" "/DPayloadDir=$payloadRoot" "/DRuntimeInstaller=$runtime" "/DOutputDir=$releaseRoot" (Join-Path $projectRoot 'packaging\StarshotFork.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setupPath = Join-Path $releaseRoot "Starshot-$Version-setup-x64.exe"
$checksums = @($zipPath, $setupPath) | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))"
}
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $checksums, [Text.Encoding]::ASCII)
$provenance = [ordered]@{
    version = $Version
    platform = 'win-x64'
    launcherSha256 = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    webViewRuntimeSha256 = (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
    webViewRuntimePublisher = $runtimeSignature.SignerCertificate.Subject
    webViewRuntimeVersion = (Get-Item -LiteralPath $runtime).VersionInfo.FileVersion
    uhdrSha256 = $uhdrHash
    payload = $payloadRoot
}
$provenance | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseRoot 'provenance.json') -Encoding utf8
Write-Host "Release artifacts: $releaseRoot"

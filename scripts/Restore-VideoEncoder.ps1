[CmdletBinding()]
param([string]$Destination, [string]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Destination) { $Destination = Join-Path $root 'third_party\ffmpeg\7.1.1\win-x64' }
function Get-Hash([string]$file) {
    $stream = [IO.File]::OpenRead($file)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
$canonical = Join-Path $root 'third_party\ffmpeg\7.1.1\win-x64\manifest.json'
$manifest = Get-Content -LiteralPath $canonical -Raw | ConvertFrom-Json
function Test-Components([string]$folder) {
    foreach ($entry in $manifest.files.PSObject.Properties) {
        if ([IO.Path]::GetFileName($entry.Name) -ne $entry.Name) { throw 'Unsafe component manifest path.' }
        $file = Join-Path $folder $entry.Name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $false }
        if ((Get-Hash $file) -ne $entry.Value) { return $false }
    }
    return $true
}
if ($VerifyOnly) {
    if (-not (Test-Components $VerifyOnly)) { throw 'Packaged video encoder hashes do not match the canonical manifest.' }
    if ((Get-Hash (Join-Path $VerifyOnly 'manifest.json')) -ne (Get-Hash $canonical)) { throw 'Packaged video encoder manifest differs.' }
    Write-Host 'Video encoder verified.'
    exit 0
}
if (-not (Test-Components $Destination)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $cache = Join-Path $root 'build\video-encoder-cache'
    New-Item -ItemType Directory -Force -Path $cache,$Destination | Out-Null
    $archive = Join-Path $cache 'ffmpeg-7.1.1-full_build-shared.zip'
    if (-not (Test-Path -LiteralPath $archive) -or (Get-Hash $archive) -ne $manifest.packageSha256) {
        Invoke-WebRequest -UseBasicParsing -Uri $manifest.packageUrl -OutFile $archive
    }
    if ((Get-Hash $archive) -ne $manifest.packageSha256) { throw 'Video encoder archive hash mismatch.' }
    $stage = Join-Path $cache ('extract-' + [guid]::NewGuid().ToString('N'))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $stage)
    $package = Join-Path $stage 'ffmpeg-7.1.1-full_build-shared'
    foreach ($entry in $manifest.files.PSObject.Properties) {
        $source = Join-Path (Join-Path $package 'bin') $entry.Name
        if (-not (Test-Path -LiteralPath $source)) { $source = Join-Path $package $entry.Name }
        Copy-Item -LiteralPath $source -Destination (Join-Path $Destination $entry.Name) -Force
    }
    Copy-Item -LiteralPath $canonical -Destination (Join-Path $Destination 'manifest.json') -Force -ErrorAction SilentlyContinue
    if (-not (Test-Components $Destination)) { throw 'Restored video encoder hashes do not match.' }
}
$encoders = (& (Join-Path $Destination 'ffmpeg.exe') -hide_banner -encoders 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $encoders -notmatch 'libx265' -or $encoders -notmatch 'libsvtav1') { throw 'HEVC or AV1 encoder missing.' }
Write-Host 'Pinned HEVC / AV1 video encoder ready.'

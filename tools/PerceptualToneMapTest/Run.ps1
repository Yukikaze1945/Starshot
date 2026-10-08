[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [switch]$Performance,
    [string]$PublishedPath
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$output = Join-Path $repo 'build\perceptual-test'
$null = New-Item -ItemType Directory -Path $output -Force
$buildLog = Join-Path $repo 'build\perceptual-test-build.log'
$runLog = Join-Path $repo 'build\perceptual-test-run.log'
$project = Join-Path $PSScriptRoot 'PerceptualToneMapTest.csproj'

& $Dotnet build $project -c Release --source https://api.nuget.org/v3/index.json -o $output -p:Platform=x64 *> $buildLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $buildLog -Tail 30; exit $LASTEXITCODE }

$uhdrHash = '415EE12ED6E979D1A96A495AFCB634D54FBACF69E5B9E45C95C38793C737384B'
$uhdrSource = Join-Path $repo 'third_party\libultrahdr\v2.0.2\win-x64\uhdr.dll'
$gpuOutput = Join-Path $repo 'build\lightweight-native-test'
$gpuDll = Join-Path $gpuOutput 'GpuMemory.dll'
if (-not (Test-Path -LiteralPath $gpuDll -PathType Leaf)) {
    & (Join-Path $repo 'tools\LightweightCaptureTest\Build-GpuMemory.ps1') -OutputDirectory $gpuOutput
    if ($LASTEXITCODE -ne 0) { throw 'Building the GPU counter helper failed.' }
}

Copy-Item -LiteralPath $uhdrSource -Destination (Join-Path $output 'uhdr.dll') -Force
Copy-Item -LiteralPath $gpuDll -Destination (Join-Path $output 'GpuMemory.dll') -Force
& (Join-Path $repo 'scripts\Verify-UhdrDll.ps1') -Path (Join-Path $output 'uhdr.dll') -ExpectedSha256 $uhdrHash
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$runArguments = @()
if ($Performance) { $runArguments += '--perf' }
if ($PublishedPath) {
    if (-not [IO.Path]::IsPathRooted($PublishedPath)) { throw '-PublishedPath must be absolute.' }
    $runArguments += "--published=$PublishedPath"
}
$savedErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& (Join-Path $output 'PerceptualToneMapTest.exe') @runArguments *> $runLog
$runCode = $LASTEXITCODE
$ErrorActionPreference = $savedErrorActionPreference
Get-Content -LiteralPath $runLog -Tail 25
exit $runCode

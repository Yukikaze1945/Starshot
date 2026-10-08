[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '../../build/lightweight-native-test' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$vs = & 'C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe' -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$version = (Get-Content -LiteralPath (Join-Path $vs 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$vc = Join-Path $vs "VC/Tools/MSVC/$version"
$sdk = 'C:/Program Files (x86)/Windows Kits/10'
$sdkVersion = '10.0.26100.0'
$arguments = @('/nologo', '/LD', '/MT', '/O2', '/W4', '/D_WIN32_WINNT=0x0A00', '/DWINVER=0x0A00',
    "/I$vc/include", "/I$sdk/Include/$sdkVersion/shared", "/I$sdk/Include/$sdkVersion/um", "/I$sdk/Include/$sdkVersion/ucrt",
    (Join-Path $PSScriptRoot 'GpuMemory.cpp'), "/Fo$output/GpuMemory.obj", '/link',
    "/LIBPATH:$vc/lib/x64", "/LIBPATH:$sdk/Lib/$sdkVersion/um/x64", "/LIBPATH:$sdk/Lib/$sdkVersion/ucrt/x64",
    'gdi32.lib', "/OUT:$output/GpuMemory.dll", "/IMPLIB:$output/GpuMemory.lib")
& (Join-Path $vc 'bin/Hostx64/x64/cl.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'GPU counter helper build failed' }

param([string]$WorkRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'StarshotHeic-build'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$prefix = Join-Path $WorkRoot 'stage'
New-Item -ItemType Directory -Force $prefix | Out-Null
# Official source releases; build from source instead of downloading executable codecs.
$sources = @(
    @{name='x265'; folder='x265_4.2'; url='https://download.videolan.org/pub/videolan/x265/x265_4.2.tar.gz'; sha='40B1EA0453E0309F0EBA934E0DDF533F8F6295966679E8894E8F1C1C8D5E1210'},
    @{name='heif'; folder='libheif-1.23.6'; url='https://github.com/strukturag/libheif/releases/download/v1.23.6/libheif-1.23.6.tar.gz'; sha='4484346DC5995319DBC11E3A1C35D0A2EC46511CE370900869337FD2C7033125'},
    @{name='de265'; folder='libde265-1.1.3'; url='https://github.com/strukturag/libde265/releases/download/v1.1.3/libde265-1.1.3.tar.gz'; sha='554228BD17788C99A7E63B37AB5634722190E6E2BF60C1DCB01CEF328E133905'}
)
foreach ($source in $sources) {
    $archive = Join-Path $WorkRoot ($source.name + '.tar.gz')
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest $source.url -OutFile $archive }
    if ((Get-FileHash -Algorithm SHA256 $archive).Hash -ne $source.sha) { throw "Source hash mismatch: $($source.name)" }
    if (-not (Test-Path -LiteralPath (Join-Path $WorkRoot $source.folder))) { & tar -xf $archive -C $WorkRoot; if ($LASTEXITCODE) { throw 'Source extraction failed' } }
}
# One Main10 x265 build handles both SDR and PQ images. No 8/10/12-bit session pools.
$common = @('-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',"-DCMAKE_INSTALL_PREFIX=$prefix")
function Run-Cmake([string[]]$Arguments) { & cmake @Arguments; if ($LASTEXITCODE) { throw 'Native configure/build failed' } }
Run-Cmake (@('-S',"$WorkRoot/x265_4.2/source",'-B',"$WorkRoot/x265-build") + $common + @('-DCMAKE_POLICY_VERSION_MINIMUM=3.5','-DHIGH_BIT_DEPTH=ON','-DMAIN12=OFF','-DENABLE_SHARED=OFF','-DENABLE_CLI=OFF','-DENABLE_ASSEMBLY=OFF','-DSTATIC_LINK_CRT=ON'))
Run-Cmake -Arguments @('--build',"$WorkRoot/x265-build",'--parallel','6')
Run-Cmake -Arguments @('--install',"$WorkRoot/x265-build")
Run-Cmake (@('-S',"$WorkRoot/libde265-1.1.3",'-B',"$WorkRoot/de265-build") + $common + @('-DBUILD_SHARED_LIBS=OFF','-DENABLE_DECODER=OFF','-DENABLE_SDL=OFF','-DENABLE_AVX512=OFF'))
Run-Cmake -Arguments @('--build',"$WorkRoot/de265-build",'--parallel','6')
Run-Cmake -Arguments @('--install',"$WorkRoot/de265-build")
Run-Cmake (@('-S',"$repo/tools/HeicCodec",'-B',"$WorkRoot/codec-build") + $common + @("-DCMAKE_PREFIX_PATH=$prefix","-DSTARSHOT_HEIF_SOURCE=$WorkRoot/libheif-1.23.6","-DX265_LIBRARY=$prefix/lib/x265-static.lib","-DX265_INCLUDE_DIR=$prefix/include"))
Run-Cmake -Arguments @('--build',"$WorkRoot/codec-build",'--parallel','6')
$worker = Join-Path $WorkRoot 'codec-build/StarshotHeic.exe'
$capabilities = & $worker --version
if ($LASTEXITCODE -or $capabilities -notmatch 'hevcEncoder=1 hevcDecoder=1') { throw 'HEIC worker must provide both HEVC encoding and decoding' }
$destination = Join-Path $repo 'third_party/heic/win-x64'
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item -LiteralPath $worker -Destination $destination
Copy-Item -LiteralPath "$WorkRoot/libheif-1.23.6/COPYING" -Destination (Join-Path $destination 'COPYING-libheif.txt')
Copy-Item -LiteralPath "$WorkRoot/libde265-1.1.3/COPYING" -Destination (Join-Path $destination 'COPYING-libde265.txt')
Copy-Item -LiteralPath "$WorkRoot/x265_4.2/COPYING" -Destination (Join-Path $destination 'COPYING-x265.txt')
$notice = Join-Path $repo 'third_party/heic/README.md'
if (-not (Test-Path -LiteralPath $notice)) { $notice = Join-Path $repo 'README.md' }
Copy-Item -LiteralPath $notice -Destination (Join-Path $destination 'SOURCES.md')
if (-not (Test-Path -LiteralPath "$destination/LICENSE-worker.txt")) {
    Copy-Item -LiteralPath "$repo/LICENSE" -Destination "$destination/LICENSE-worker.txt"
}
# Keep complete corresponding sources and build instructions with the worker.
# The source ZIP is also copied to portable / installer publish outputs.
$sourceTemp = Join-Path ([System.IO.Path]::GetTempPath()) ('StarshotHeic-source-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$sourceTemp/tools/HeicCodec", "$sourceTemp/sources" | Out-Null
try {
    Copy-Item -LiteralPath "$PSScriptRoot/Build-HeicCodec.ps1" -Destination "$sourceTemp/tools"
    Copy-Item -LiteralPath "$PSScriptRoot/HeicCodec/main.cpp", "$PSScriptRoot/HeicCodec/CMakeLists.txt" -Destination "$sourceTemp/tools/HeicCodec"
    Copy-Item -LiteralPath $notice -Destination "$sourceTemp/README.md"
    Copy-Item -LiteralPath "$destination/LICENSE-worker.txt" -Destination "$sourceTemp/LICENSE"
    foreach ($source in $sources) { Copy-Item -LiteralPath (Join-Path $WorkRoot ($source.name + '.tar.gz')) -Destination "$sourceTemp/sources" }
    Compress-Archive -Path "$sourceTemp/*" -DestinationPath (Join-Path $destination 'StarshotHeic-source.zip') -Force
}
finally {
    if ([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($sourceTemp)) -ne [System.IO.Path]::GetTempPath().TrimEnd('\','/')) { throw 'Unexpected source temp directory' }
    Remove-Item -LiteralPath $sourceTemp -Recurse -Force
}
$sha = (Get-FileHash -Algorithm SHA256 (Join-Path $destination 'StarshotHeic.exe')).Hash
@{ version=1; libheif='1.23.6'; x265='4.2'; libde265='1.1.3'; sha256=$sha } | ConvertTo-Json | Set-Content (Join-Path $destination 'manifest.json')
Get-FileHash -Algorithm SHA256 (Join-Path $destination 'StarshotHeic.exe')

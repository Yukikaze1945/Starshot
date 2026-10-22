param([string]$Destination, [string]$Archive, [ValidateSet('cpu','vulkan')][string]$Backend='cpu')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$canonical=Join-Path $repo $(if($Backend -eq 'vulkan'){'third_party/local-inference/manifest-vulkan.json'}else{'third_party/local-inference/manifest.json'})
$manifest=Get-Content -LiteralPath $canonical -Raw | ConvertFrom-Json
if(-not $Destination){$Destination=Join-Path $repo $(if($Backend -eq 'vulkan'){'third_party/local-inference/win-vulkan-x64'}else{'third_party/local-inference/win-x64'})}
function Hash([string]$path){$stream=[IO.File]::OpenRead($path);$sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}}
$outputManifest=Join-Path $Destination 'manifest.json'
if(Test-Path -LiteralPath $outputManifest){
    $current=Get-Content -LiteralPath $outputManifest -Raw | ConvertFrom-Json
    $valid=$current.version -eq $manifest.version -and $current.packageSha256 -eq $manifest.packageSha256
    foreach($file in $current.files.PSObject.Properties){
        if([IO.Path]::GetFileName($file.Name) -ne $file.Name){throw 'Invalid inference manifest path'}
        $path=Join-Path $Destination $file.Name
        if(-not (Test-Path -LiteralPath $path) -or (Hash $path) -ne $file.Value){$valid=$false;break}
    }
    if($valid -and (Test-Path -LiteralPath (Join-Path $Destination 'llama-server.exe'))){
        Copy-Item -LiteralPath (Join-Path $repo 'third_party/local-inference/LICENSE'),(Join-Path $repo 'third_party/local-inference/SOURCES.md'),(Join-Path $repo 'third_party/local-inference/LLVM-LICENSE.txt'),(Join-Path $repo 'third_party/local-inference/CPPHTTPLIB-LICENSE'),(Join-Path $repo 'third_party/local-inference/NLOHMANN-LICENSE') -Destination $Destination
        Write-Host "Local $Backend inference runtime verified.";exit 0
    }
}
$cache=Join-Path $repo 'build/local-inference-cache'
New-Item -ItemType Directory -Force $cache,$Destination | Out-Null
if(-not $Archive){$Archive=Join-Path $cache "llama-b11429-bin-win-$Backend-x64.zip"}
if(-not (Test-Path -LiteralPath $Archive)){
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $manifest.packageUrl -OutFile $Archive -TimeoutSec 300
}
if((Hash $Archive) -ne $manifest.packageSha256){throw 'Local inference archive SHA-256 mismatch'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($Archive)
$files=[ordered]@{}
try{
    foreach($entry in $zip.Entries){
        $name=[IO.Path]::GetFileName($entry.FullName)
        # Fixed official runtime, not arbitrary model-provided executables.
        if($name -notmatch '^(llama-server\.exe|llama-server-impl\.dll|llama-common\.dll|llama\.dll|ggml[^/\\]*\.dll|libomp\.dll|mtmd\.dll|vulkan-1\.dll)$'){continue}
        $target=Join-Path $Destination $name
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$true)
        $files[$name]=Hash $target
    }
}finally{$zip.Dispose()}
if(-not $files.Contains('llama-server.exe')){throw 'No llama-server in verified archive'}
@{version=$manifest.version;packageSha256=$manifest.packageSha256;source=$manifest.source;files=$files}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $outputManifest
Copy-Item -LiteralPath (Join-Path $repo 'third_party/local-inference/LICENSE'),(Join-Path $repo 'third_party/local-inference/SOURCES.md'),(Join-Path $repo 'third_party/local-inference/LLVM-LICENSE.txt'),(Join-Path $repo 'third_party/local-inference/CPPHTTPLIB-LICENSE'),(Join-Path $repo 'third_party/local-inference/NLOHMANN-LICENSE') -Destination $Destination
Write-Host "Pinned local $Backend inference runtime ready."

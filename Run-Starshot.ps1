[CmdletBinding()]
param([switch]$BuildOnly, [string]$DotnetPath)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$output = Join-Path $repo 'build/source-run/app'
$app = Join-Path $output 'Starshot.exe'

function Find-Dotnet10 {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($DotnetPath) { $candidates.Add($DotnetPath) }
    else {
        foreach ($folder in @($env:DOTNET10_ROOT, $env:DOTNET_ROOT, (Join-Path $repo '.dotnet'), (Join-Path (Split-Path $repo -Parent) 'dotnet-sdk10'))) {
            if ($folder) { $candidates.Add((Join-Path $folder 'dotnet.exe')) }
        }
        foreach ($command in @(Get-Command dotnet.exe -All -ErrorAction SilentlyContinue)) { $candidates.Add($command.Source) }
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and @($sdks | Where-Object { $_ -match '^10\.0\.\d+\s' }).Count -gt 0) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    throw '找不到 .NET 10 SDK。请安装 SDK，或使用 -DotnetPath 指定 dotnet.exe。'
}

function Run-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "构建命令失败，退出码 $LASTEXITCODE。请查看上方错误。" }
}

Push-Location $repo
try {
    # A running source build holds the output DLLs open. Do not kill a capture,
    # recording or text editor, or let an installed singleton take over launch.
    $running = @(Get-Process -Name Starshot -ErrorAction SilentlyContinue)
    foreach ($process in $running) {
        if (-not $BuildOnly -or $process.Path -eq $app) {
            throw "Starshot 正在运行（PID $($process.Id)）。请先从托盘退出，再运行此脚本。"
        }
    }
    $dotnet = Find-Dotnet10
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $node -or -not $npm) { throw '找不到 Node.js / npm，请安装 Node.js 22.12 或更新的 LTS 版本。' }
    $nodeVersion = [Version]((& $node.Source --version).TrimStart('v'))
    if ($nodeVersion.Major -lt 22 -or ($nodeVersion.Major -eq 22 -and $nodeVersion.Minor -lt 12)) { throw 'Node.js 版本过旧，请使用 22.12 或更新的 LTS 版本。' }
    $env:PATH = "$env:DOTNET_ROOT;$(Split-Path $node.Source -Parent);$env:PATH"
    Write-Host "源码版 · Debug / Windows x64`n.NET: $dotnet`n输出: $output" -ForegroundColor Cyan
    # Explicit source avoids stale machine-wide local NuGet feeds; never changes
    # the user's NuGet settings. MSBuild handles npm and verified codec assets.
    Run-Checked $dotnet @('restore', 'src/Starshot/Starshot.csproj', '--source', 'https://api.nuget.org/v3/index.json', '-p:Platform=x64')
    Run-Checked $dotnet @('build', 'src/Starshot/Starshot.csproj', '-c', 'Debug', '--no-restore', '-p:Platform=x64', ('-p:OutputPath=' + $output.Replace('\','/') + '/'))
    if (-not (Test-Path -LiteralPath $app)) { throw '未找到编译后的 Starshot.exe。' }
    Write-Host "`n构建完成。源码版配置保存在 build/source-run/config.sjson。" -ForegroundColor Green
    if (-not $BuildOnly) {
        # Recheck after build in case another instance was opened meanwhile.
        if (Get-Process -Name Starshot -ErrorAction SilentlyContinue) { throw '检测到另一个 Starshot，请先从托盘退出再启动源码版。' }
        Start-Process -FilePath $app -WorkingDirectory $output
        Write-Host '源码版已启动。下次改完代码后，再运行此脚本即可。'
    }
}
catch { Write-Host "`n$($_.Exception.Message)" -ForegroundColor Red; exit 1 }
finally { Pop-Location }

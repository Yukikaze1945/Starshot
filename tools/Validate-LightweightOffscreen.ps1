[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuiltApp,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [string]$DotNetPath = 'D:/coding/dotnet-sdk10/dotnet.exe',
    [switch]$SoftwareWebView2,
    [switch]$RetiredWindowRoot,
    [switch]$LibraryFixtures,
    [switch]$PublishedSmoke
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$source = (Resolve-Path -LiteralPath $BuiltApp).Path
$reports = [IO.Path]::GetFullPath($ReportDirectory)
if (Get-Process Starshot -ErrorAction SilentlyContinue) { throw 'An existing Starshot is running; no existing process will be stopped.' }
New-Item -ItemType Directory -Path $reports -Force | Out-Null
& $DotNetPath build (Join-Path $PSScriptRoot 'LightweightCaptureIntegrationTest') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Integration hook build failed' }
& (Join-Path $PSScriptRoot 'LightweightCaptureTest/Build-GpuMemory.ps1')
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$profile = Join-Path $temporaryBase ('Starshot-offscreen-' + [guid]::NewGuid().ToString('N'))
$application = Join-Path $profile 'app'
$owned = $null
try {
    New-Item -ItemType Directory -Path $application -Force | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $application -Recurse
    $hookPath = Join-Path $PSScriptRoot 'LightweightCaptureIntegrationTest/bin/Release/net10.0-windows10.0.26100.0/LightweightCaptureIntegrationTest.dll'
    # Startup hooks are disabled in production; enable only in the disposable copy.
    $runtimeFile = Join-Path $application 'Starshot.runtimeconfig.json'
    $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
    if (!$PublishedSmoke) {
        $runtime.runtimeOptions.configProperties.'System.StartupHookProvider.IsSupported' = $true
        $runtime | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $runtimeFile -Encoding utf8
    }
    [ordered]@{ScreenCaptureMode='0'; Theme='1'; EnableAutoUpdateCheck='False'; EnablePreReleaseUpdateCheck='False';
        AutoCopyScreenshotToClipboard='False'; AutoCopyOcrText='False'; LogFolder=$profile;
        ScreenshotFolder=(Join-Path $profile 'empty-library'); ExtraScreenshotFolders='[]'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'config.sjson') -Encoding utf8
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $application 'Starshot.exe'), '--hide')
    $start.WorkingDirectory = $application; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    if (!$PublishedSmoke) { $start.Environment['DOTNET_STARTUP_HOOKS'] = $hookPath }
    $start.Environment['STARSHOT_LIGHTWEIGHT_REPORT'] = $reports
    $start.Environment['STARSHOT_GPU_COUNTER_DLL'] = Join-Path $repository 'build/lightweight-native-test/GpuMemory.dll'
    if ($SoftwareWebView2) { $start.Environment['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = '--disable-gpu' }
    if ($RetiredWindowRoot) { $start.Environment['STARSHOT_RETIRED_ROOT'] = '1' }
    if ($LibraryFixtures) { $start.Environment['STARSHOT_LIBRARY_FIXTURES'] = '1' }
    $owned = [Diagnostics.Process]::Start($start)
    if ($PublishedSmoke) {
        # The unmodified trimmed runtime excludes testing-only reflection APIs.
        # Sample the real published process externally; do not enable hooks or
        # replace its entry assembly/runtime. This checks startup, not capture.
        $previousCounter = $env:STARSHOT_GPU_COUNTER_DLL
        try {
            $env:STARSHOT_GPU_COUNTER_DLL = $start.Environment['STARSHOT_GPU_COUNTER_DLL']
            $counter = Join-Path $PSScriptRoot 'LightweightCaptureTest/bin/Release/net10.0-windows10.0.26100.0/LightweightCaptureTest.dll'
            foreach ($delay in @(2,5,10)) {
                Start-Sleep -Seconds $delay; $owned.Refresh()
                if ($owned.HasExited) { throw "Published app exited during hidden startup: $($owned.ExitCode)" }
                $sample = & $DotNetPath $counter --pid $owned.Id | ConvertFrom-Json
                if ($LASTEXITCODE -ne 0) { throw 'External published-process GPU query failed' }
                [pscustomobject]@{timestamp=[DateTimeOffset]::UtcNow.ToString('O'); pid=$owned.Id;
                    dedicatedResidentMiB=$sample.DedicatedResident/1048576;
                    dedicatedCommittedMiB=$sample.DedicatedCommitted/1048576;
                    sharedResidentMiB=$sample.SharedResident/1048576;
                    sharedCommittedMiB=$sample.SharedCommitted/1048576;
                    privateMiB=$owned.PrivateMemorySize64/1048576;
                    residentQueries=$sample.ResidentQueries; committedQueries=$sample.CommittedQueries;
                    queryFailures=$sample.FailedQueries} |
                    Export-Csv -LiteralPath (Join-Path $reports 'published-hidden-startup.csv') -NoTypeInformation -Append
            }
            'PASS: original trimmed published assembly/runtime starts hidden and remains running; no GUI/capture test or input. No hooks.' |
                Set-Content -LiteralPath (Join-Path $reports 'published-smoke-pass.log')
            Write-Output 'Published hidden-startup smoke passed; no input or capture.'
            return
        } finally { $env:STARSHOT_GPU_COUNTER_DLL = $previousCounter }
    }
    Write-Output "Isolated offscreen test PID $($owned.Id); 20 synthetic 4K cycles; no desktop input or capture."
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    $rootCollected = $false
    while (!$owned.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if ($RetiredWindowRoot -and !$rootCollected -and (Test-Path -LiteralPath (Join-Path $reports 'retired-root-ready.log'))) {
            $rootCollected = $true
            $diagnosticTool = Get-ChildItem -LiteralPath (Join-Path $repository 'build/diagnostics') -Filter 'dotnet-dump.dll' -Recurse | Select-Object -First 1 -ExpandProperty FullName
            if (!$diagnosticTool) { throw 'Install dotnet-dump in build/diagnostics before the optional root diagnostic' }
            $dump = Join-Path $profile 'retired-window.dmp'
            & $DotNetPath --roll-forward Major $diagnosticTool collect -p $owned.Id --type Heap -o $dump |
                Set-Content -LiteralPath (Join-Path $reports 'retired-root-collect.log')
            if ($LASTEXITCODE -ne 0) { throw 'Owned test-process dump collection failed' }
            $heap = & $DotNetPath --roll-forward Major $diagnosticTool analyze $dump -c 'dumpheap -type Starshot.Features.ViewHost.MainWindow' -c 'exit'
            $heap | Set-Content -LiteralPath (Join-Path $reports 'retired-window-heap.log')
            foreach ($line in $heap) {
                if ($line -match '^\s*([0-9a-f]{8,16})\s+[0-9a-f]{8,16}\s+\d+\s*$') {
                    $address = $Matches[1]
                    & $DotNetPath --roll-forward Major $diagnosticTool analyze $dump -c "gcroot $address" -c 'exit' |
                        Add-Content -LiteralPath (Join-Path $reports 'retired-window-roots.log')
                }
            }
            Set-Content -LiteralPath (Join-Path $reports 'retired-root-done.log') -Value 'Root analysis complete. Dump removed with disposable profile.'
        }
        Start-Sleep -Seconds 2; $owned.Refresh()
    }
    if (!$owned.HasExited) { throw 'Integration test timed out' }
    if ($owned.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $reports 'integration-pass.log'))) {
        if (Test-Path -LiteralPath (Join-Path $reports 'integration-error.log')) { Get-Content -LiteralPath (Join-Path $reports 'integration-error.log') }
        throw 'Integration test failed'
    }
    Get-Content -LiteralPath (Join-Path $reports 'integration-pass.log')
} finally {
    if ($owned -and !$owned.HasExited) {
        $expected = [IO.Path]::GetFullPath((Join-Path $application 'Starshot.exe'))
        if ([IO.Path]::GetFullPath($owned.Path) -ne $expected) { throw 'Refusing to stop an unrelated process' }
        $owned.Kill(); $owned.WaitForExit(5000) | Out-Null
    }
    if (Test-Path -LiteralPath (Join-Path $profile 'log')) {
        Copy-Item -LiteralPath (Join-Path $profile 'log') -Destination $reports -Recurse -Force
    }
    if (Test-Path -LiteralPath (Join-Path $application 'log')) {
        Copy-Item -LiteralPath (Join-Path $application 'log') -Destination (Join-Path $reports 'early-app-log') -Recurse -Force
    }
    $resolved = [IO.Path]::GetFullPath($profile)
    if (!$resolved.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'Starshot-offscreen-*') { throw 'Unsafe test cleanup target' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    Write-Output 'Disposable app/cache/profile cleaned; only logs and CSV retained.'
}

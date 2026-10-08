[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedApp,
    [Parameter(Mandatory)][string]$ReportDirectory
)
$ErrorActionPreference = 'Stop'
if (Get-Process Starshot -ErrorAction SilentlyContinue) { throw 'Exit the existing Starshot first.' }
$applicationFile = (Resolve-Path -LiteralPath (Join-Path $PublishedApp 'Starshot.exe')).Path
$profileRoot = Split-Path (Split-Path $applicationFile) -Parent
$reportRoot = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$imageRoot = Join-Path $temporaryBase ('Starshot-capture-modes-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $imageRoot -Force | Out-Null
$configFile = Join-Path $profileRoot 'config.sjson'
if (Test-Path -LiteralPath $configFile) { throw 'Use an isolated publish with no existing configuration.' }
$finishFile = Join-Path $reportRoot ('finish-' + [guid]::NewGuid().ToString('N'))
$testProcess = $null
try {
    [ordered]@{
        Theme='1'; LogFolder=$reportRoot; ScreenshotFolder=$imageRoot
        ScreenshotSubfolderEnabled='False'; AutoCopyScreenshotToClipboard='False'; AutoCopyOcrText='False'
        EnableAutoUpdateCheck='False'; EnablePreReleaseUpdateCheck='False'
        ScreenCaptureSDRFormat='0'; ScreenCaptureHDRFormat='0'; ScreenCaptureEncodeQuality='1'
        AutoSaveUltraHDRJpeg='True'; EnableScreenshotColorManagement='True'; DeleteHDRIfSDRContent='False'
        UhdrCapacityManual='True'; UhdrCapacityValue='14'; OcrEngine='1'
        ScreenshotFileNamePattern='MODE_{timestamp}_{width}x{height}'
        RegionScreenshotFileNamePattern='MODE_region_{timestamp}_{width}x{height}'
    } | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding utf8
    $testProcess = Start-Process -FilePath $applicationFile -WorkingDirectory (Split-Path $applicationFile) -PassThru -WindowStyle Hidden
    @{pid=$testProcess.Id; imageRoot=$imageRoot; reportRoot=$reportRoot; profileRoot=$profileRoot; finishFile=$finishFile} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportRoot 'ready.json') -Encoding utf8
    Write-Output ('Test process PID: ' + $testProcess.Id)
    Write-Output ('Temporary capture directory: ' + $imageRoot)
    $testDeadline = [DateTime]::UtcNow.AddMinutes(20)
    while (!(Test-Path -LiteralPath $finishFile) -and !$testProcess.HasExited -and [DateTime]::UtcNow -lt $testDeadline) {
        Start-Sleep -Seconds 2
        $testProcess.Refresh()
    }
} finally {
    if ($testProcess -and !$testProcess.HasExited -and $testProcess.Path -eq $applicationFile) {
        $testProcess.Kill()
        $testProcess.WaitForExit(5000) | Out-Null
    }
    $resolvedImages = [IO.Path]::GetFullPath($imageRoot)
    if (!$resolvedImages.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedImages) -notlike 'Starshot-capture-modes-*') { throw 'Unsafe capture cleanup path.' }
    # The harness disables subfolders. Delete only the known image types created
    # by this test, then remove the empty directory (never a recursive deletion).
    foreach ($file in [IO.Directory]::EnumerateFiles($resolvedImages)) {
        if ([IO.Path]::GetExtension($file) -notin '.png','.avif','.jpg','.jpeg','.jxl','.gif') {
            throw "Unexpected file in temporary image directory: $file"
        }
        [IO.File]::Delete($file)
    }
    [IO.Directory]::Delete($resolvedImages, $false)
    [IO.File]::Delete($configFile)
    Write-Output 'Temporary images cleaned in finally.'
}

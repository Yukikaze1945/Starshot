param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$ExpectedSha256
)

$ErrorActionPreference = 'Stop'

try {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing uhdr.dll: $Path"
    }

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -le 0) {
        throw "Empty uhdr.dll: $Path"
    }

    $stream = [System.IO.File]::OpenRead($file.FullName)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        if ($stream.Length -lt 0x40 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "Not a PE DLL (missing MZ header): $Path"
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt $stream.Length - 6) {
            throw "Invalid PE offset: $Path"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "Not an x64 PE DLL: $Path"
        }
    }
    finally {
        $stream.Dispose()
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hashStream = [System.IO.File]::OpenRead($file.FullName)
    try {
        $actualSha256 = [System.BitConverter]::ToString($sha.ComputeHash($hashStream)).Replace('-', '')
    }
    finally {
        $hashStream.Dispose()
        $sha.Dispose()
    }
    if ($actualSha256 -ine $ExpectedSha256) {
        throw "Wrong uhdr.dll SHA-256 at $Path. Expected $ExpectedSha256; actual $actualSha256"
    }

    Write-Host "Verified x64 uhdr.dll: $($file.FullName) ($($file.Length) bytes, SHA-256 $actualSha256)"
}
catch {
    Write-Error $_
    exit 1
}

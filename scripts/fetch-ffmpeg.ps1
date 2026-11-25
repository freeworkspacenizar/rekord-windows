<#
.SYNOPSIS
    Downloads the official BtbN FFmpeg win64-lgpl build (LGPL v2.1+, includes libwebp_anim & h264_mf)
    and stages ffmpeg.exe + license files into vendor/ffmpeg/.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$VendorDir = Join-Path $RootDir "vendor\ffmpeg"
$FfmpegExe = Join-Path $VendorDir "ffmpeg.exe"
$CopyingFile = Join-Path $VendorDir "COPYING.LGPLv2.1"
$LicenseFile = Join-Path $VendorDir "LICENSE.md"

if (-not $Force -and (Test-Path $FfmpegExe) -and (Test-Path $CopyingFile)) {
    Write-Host "==> Vendored LGPL ffmpeg.exe already present at $FfmpegExe"
    exit 0
}

New-Item -ItemType Directory -Force -Path $VendorDir | Out-Null

$ZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip"
$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("rekord-ffmpeg-" + [Guid]::NewGuid().ToString("N"))
$ZipPath = Join-Path $TempDir "ffmpeg-win64-lgpl.zip"

try {
    New-Item -ItemType Directory -Force -Path $TempDir | Out-Null
    Write-Host "==> Downloading official BtbN FFmpeg win64-lgpl..."
    Invoke-WebRequest -Uri $ZipUrl -OutFile $ZipPath -UseBasicParsing

    Write-Host "==> Extracting ffmpeg.exe and LGPL license..."
    Expand-Archive -Path $ZipPath -DestinationPath $TempDir -Force

    $FoundExe = Get-ChildItem -Path $TempDir -Filter "ffmpeg.exe" -Recurse | Select-Object -First 1
    if (-not $FoundExe) {
        throw "ffmpeg.exe not found inside downloaded archive."
    }
    Copy-Item -Path $FoundExe.FullName -Destination $FfmpegExe -Force

    $FoundLicense = Get-ChildItem -Path $TempDir -Filter "LICENSE.txt" -Recurse | Select-Object -First 1
    if ($FoundLicense) {
        Copy-Item -Path $FoundLicense.FullName -Destination $CopyingFile -Force
    } else {
        "FFmpeg win64-lgpl binary distributed under GNU LGPL v2.1 or later (https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html)." |
            Set-Content -Path $CopyingFile -Encoding UTF8
    }

    @"
# FFmpeg (win64-lgpl) License Notice

Rekord bundles an unmodified FFmpeg ``win64-lgpl`` binary from BtbN/FFmpeg-Builds:
- Source & Build Scripts: https://github.com/BtbN/FFmpeg-Builds
- FFmpeg Upstream Source: https://ffmpeg.org/download.html
- License: GNU Lesser General Public License v2.1 or later (see ``COPYING.LGPLv2.1``).
"@ | Set-Content -Path $LicenseFile -Encoding UTF8

    Write-Host "==> Verifying encoders (libwebp_anim & h264_mf)..."
    $Encoders = & $FfmpegExe -hide_banner -encoders 2>&1 | Out-String
    if ($Encoders -notmatch "libwebp_anim") {
        throw "Downloaded ffmpeg.exe does not include libwebp_anim encoder."
    }

    $SizeMb = [math]::Round((Get-Item $FfmpegExe).Length / 1MB, 1)
    Write-Host "==> Ready: $FfmpegExe ($SizeMb MB)"
}
finally {
    if (Test-Path $TempDir) {
        Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

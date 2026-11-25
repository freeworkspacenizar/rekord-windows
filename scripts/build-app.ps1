<#
.SYNOPSIS
    Runs RekordCore.Tests, fetches LGPL ffmpeg.exe if needed, publishes self-contained win-x64 Rekord.exe,
    bundles ffmpeg.exe + LGPL license notices into dist/Rekord/, and packs dist/Rekord-win-x64-portable.zip.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$AppDir = Join-Path $RootDir "app"
$VendorDir = Join-Path $RootDir "vendor\ffmpeg"
$FfmpegExe = Join-Path $VendorDir "ffmpeg.exe"
$CopyingFile = Join-Path $VendorDir "COPYING.LGPLv2.1"
$LicenseFile = Join-Path $VendorDir "LICENSE.md"
$DistDir = Join-Path $RootDir "dist"
$OutAppDir = Join-Path $DistDir "Rekord"
$PortableZip = Join-Path $DistDir "Rekord-win-x64-portable.zip"
$IconPath = Join-Path $DistDir "Rekord.ico"

if (-not (Test-Path $FfmpegExe)) {
    Write-Host "==> Vendored ffmpeg.exe not found; fetching official BtbN win64-lgpl..."
    & (Join-Path $PSScriptRoot "fetch-ffmpeg.ps1")
}

if (-not $SkipTests) {
    Write-Host "==> Running RekordCore.Tests..."
    dotnet test (Join-Path $AppDir "tests\RekordCore.Tests\RekordCore.Tests.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }
}

Write-Host "==> Generating Rekord.ico..."
& (Join-Path $PSScriptRoot "generate-icon.ps1") -OutputPath $IconPath

Write-Host "==> Publishing Rekord (self-contained win-x64)..."
if (Test-Path $OutAppDir) { Remove-Item -Path $OutAppDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutAppDir | Out-Null

dotnet publish (Join-Path $AppDir "src\Rekord\Rekord.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:ApplicationIcon="$IconPath" `
    -o "$OutAppDir"

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "==> Bundling LGPL ffmpeg.exe and license documents..."
Copy-Item -Path $FfmpegExe -Destination (Join-Path $OutAppDir "ffmpeg.exe") -Force
$LicensesDir = Join-Path $OutAppDir "ffmpeg-licenses"
New-Item -ItemType Directory -Force -Path $LicensesDir | Out-Null
if (Test-Path $CopyingFile) { Copy-Item -Path $CopyingFile -Destination (Join-Path $LicensesDir "COPYING.LGPLv2.1") -Force }
if (Test-Path $LicenseFile) { Copy-Item -Path $LicenseFile -Destination (Join-Path $LicensesDir "LICENSE.md") -Force }

Write-Host "==> Creating portable archive: $PortableZip"
if (Test-Path $PortableZip) { Remove-Item -Path $PortableZip -Force }
Compress-Archive -Path (Join-Path $OutAppDir "*") -DestinationPath $PortableZip -CompressionLevel Optimal

Write-Host "==> Done!"
Write-Host "    Portable folder : $OutAppDir"
Write-Host "    Portable zip    : $PortableZip"

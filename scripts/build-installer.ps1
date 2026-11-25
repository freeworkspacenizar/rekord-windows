<#
.SYNOPSIS
    Compiles scripts/Rekord.iss with Inno Setup (ISCC.exe) to produce dist/Rekord-Setup.exe.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$DistAppDir = Join-Path $RootDir "dist\Rekord"
$IssFile = Join-Path $PSScriptRoot "Rekord.iss"

if (-not (Test-Path (Join-Path $DistAppDir "Rekord.exe"))) {
    Write-Host "==> dist/Rekord not found; running build-app.ps1 first..."
    & (Join-Path $PSScriptRoot "build-app.ps1")
}

$IsccCandidates = @(
    (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) }

$Iscc = $IsccCandidates | Select-Object -First 1
if (-not $Iscc) {
    throw "Inno Setup 6 (ISCC.exe) tidak ditemukan. Pasang via: winget install JRSoftware.InnoSetup"
}

Write-Host "==> Building Windows installer with $Iscc..."
& $Iscc $IssFile
if ($LASTEXITCODE -ne 0) { throw "ISCC compilation failed." }

$SetupExe = Join-Path $RootDir "dist\Rekord-Setup.exe"
$SizeMb = [math]::Round((Get-Item $SetupExe).Length / 1MB, 1)
Write-Host "==> Installer ready: $SetupExe ($SizeMb MB)"

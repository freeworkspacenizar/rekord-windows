<#
.SYNOPSIS
    Generates a crisp multi-resolution Windows Rekord.ico (16, 32, 48, 64, 128, 256 px PNG-encoded ICO)
    matching the macOS Rekord squircle + viewfinder brackets + coral-red record dot design.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Render-IconPng([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $inset = [float]($s * 0.08)
    $rectW = [float]($s - $inset * 2.0)
    $radius = [float]($rectW * 0.225)
    $squircle = New-RoundedRectPath $inset $inset $rectW $rectW $radius

    # Charcoal -> midnight gradient
    $gradRect = [System.Drawing.RectangleF]::new($inset, $inset, $rectW, $rectW)
    $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $gradRect,
        [System.Drawing.Color]::FromArgb(255, 36, 38, 48),
        [System.Drawing.Color]::FromArgb(255, 18, 20, 28),
        [float]45.0
    )
    $g.FillPath($grad, $squircle)

    # Subtle rim
    $rimWidth = [float][Math]::Max(1.0, $s * 0.015)
    $rimPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(46, 255, 255, 255), $rimWidth)
    $g.DrawPath($rimPen, $squircle)

    # 4 white corner brackets
    $fInset = [float]($s * 0.24)
    $fSize = [float]($s - $fInset * 2.0)
    $arm = [float]($fSize * 0.26)
    $bWidth = [float][Math]::Max(1.8, $s * 0.042)
    $bPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(235, 255, 255, 255), $bWidth)
    $bPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $bPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $bPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $x0 = [float]$fInset; $y0 = [float]$fInset; $x1 = [float]($fInset + $fSize); $y1 = [float]($fInset + $fSize)
    $armF = [float]$arm
    $g.DrawLines($bPen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($x0, $y0 + $armF),
        [System.Drawing.PointF]::new($x0, $y0),
        [System.Drawing.PointF]::new($x0 + $armF, $y0)
    ))
    $g.DrawLines($bPen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($x1 - $armF, $y0),
        [System.Drawing.PointF]::new($x1, $y0),
        [System.Drawing.PointF]::new($x1, $y0 + $armF)
    ))
    $g.DrawLines($bPen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($x0, $y1 - $armF),
        [System.Drawing.PointF]::new($x0, $y1),
        [System.Drawing.PointF]::new($x0 + $armF, $y1)
    ))
    $g.DrawLines($bPen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($x1 - $armF, $y1),
        [System.Drawing.PointF]::new($x1, $y1),
        [System.Drawing.PointF]::new($x1, $y1 - $armF)
    ))

    # Glowing coral-red record dot
    $dotR = [float]($s * 0.145)
    $glowR = [float]($dotR * 1.35)
    $half = [float]($s / 2.0)
    $glowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(80, 255, 62, 52))
    $g.FillEllipse($glowBrush, $half - $glowR, $half - $glowR, $glowR * 2.0, $glowR * 2.0)

    $dotBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 250, 66, 58))
    $g.FillEllipse($dotBrush, $half - $dotR, $half - $dotR, $dotR * 2.0, $dotR * 2.0)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()

    $ms.Dispose(); $dotBrush.Dispose(); $glowBrush.Dispose(); $bPen.Dispose(); $rimPen.Dispose(); $grad.Dispose(); $squircle.Dispose(); $g.Dispose(); $bmp.Dispose()
    return $bytes
}

$Sizes = @(16, 32, 48, 64, 128, 256)
$Images = @()
foreach ($sz in $Sizes) {
    $Images += ,@{ Size = $sz; Data = (Render-IconPng $sz) }
}

$OutDir = Split-Path -Parent $OutputPath
if ($OutDir) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

$fs = [System.IO.File]::Create($OutputPath)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    # ICONDIR header
    $bw.Write([UInt16]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]$Images.Count)

    $offset = 6 + (16 * $Images.Count)
    foreach ($img in $Images) {
        $dimByte = if ($img.Size -ge 256) { [byte]0 } else { [byte]$img.Size }
        $bw.Write($dimByte)
        $bw.Write($dimByte)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([UInt16]1)
        $bw.Write([UInt16]32)
        $bw.Write([UInt32]$img.Data.Length)
        $bw.Write([UInt32]$offset)
        $offset += $img.Data.Length
    }

    foreach ($img in $Images) {
        $bw.Write($img.Data)
    }
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}

Write-Host "==> Generated Windows icon at $OutputPath"

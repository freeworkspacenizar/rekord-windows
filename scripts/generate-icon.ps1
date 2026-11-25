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
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $inset = $s * 0.08
    $rectW = $s - $inset * 2
    $radius = $rectW * 0.225
    $squircle = New-RoundedRectPath $inset $inset $rectW $rectW $radius

    # Charcoal -> midnight gradient
    $gradRect = New-Object System.Drawing.RectangleF($inset, $inset, $rectW, $rectW)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $gradRect,
        [System.Drawing.Color]::FromArgb(255, 36, 38, 48),
        [System.Drawing.Color]::FromArgb(255, 18, 20, 28),
        45.0
    )
    $g.FillPath($grad, $squircle)

    # Subtle rim
    $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(46, 255, 255, 255), [Math]::Max(1.0, $s * 0.015))
    $g.DrawPath($rimPen, $squircle)

    # 4 white corner brackets
    $fInset = $s * 0.24
    $fSize = $s - $fInset * 2
    $arm = $fSize * 0.26
    $bPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235, 255, 255, 255), [Math]::Max(1.8, $s * 0.042))
    $bPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $bPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $bPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $x0 = $fInset; $y0 = $fInset; $x1 = $fInset + $fSize; $y1 = $fInset + $fSize
    $g.DrawLines($bPen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF($x0, $y0 + $arm)), (New-Object System.Drawing.PointF($x0, $y0)), (New-Object System.Drawing.PointF($x0 + $arm, $y0))))
    $g.DrawLines($bPen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF($x1 - $arm, $y0)), (New-Object System.Drawing.PointF($x1, $y0)), (New-Object System.Drawing.PointF($x1, $y0 + $arm))))
    $g.DrawLines($bPen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF($x0, $y1 - $arm)), (New-Object System.Drawing.PointF($x0, $y1)), (New-Object System.Drawing.PointF($x0 + $arm, $y1))))
    $g.DrawLines($bPen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF($x1 - $arm, $y1)), (New-Object System.Drawing.PointF($x1, $y1)), (New-Object System.Drawing.PointF($x1, $y1 - $arm))))

    # Glowing coral-red record dot
    $dotR = $s * 0.145
    $glowR = $dotR * 1.35
    $glowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80, 255, 62, 52))
    $g.FillEllipse($glowBrush, $s / 2 - $glowR, $s / 2 - $glowR, $glowR * 2, $glowR * 2)

    $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 250, 66, 58))
    $g.FillEllipse($dotBrush, $s / 2 - $dotR, $s / 2 - $dotR, $dotR * 2, $dotR * 2)

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

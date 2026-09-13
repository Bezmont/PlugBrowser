<#
.SYNOPSIS
    Regenerates the app icon assets from icon.png at the repository root.

.DESCRIPTION
    Writes into src/PlugBrowser.App/Assets:
      icon.ico     every size Windows asks for — the exe and its shortcuts (16-48 px in lists and
                   the taskbar at 100-200% scaling, 64-256 px for large and extra-large icon views)
      icon-64.png  the caption-bar icon, small enough to scale down cleanly at any DPI

    Each size is resampled from the full-resolution source rather than from the next size up, so
    small icons do not accumulate blur. Entries are stored PNG-compressed, which every Windows
    version since Vista reads at any size.

.EXAMPLE
    .\tools\make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\icon.png'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\PlugBrowser.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function Resize([System.Drawing.Image]$image, [int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

        # Without TileFlipXY the bicubic filter samples beyond the source edge and leaves a faint
        # dark fringe around the icon.
        $attributes = New-Object System.Drawing.Imaging.ImageAttributes
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

        $target = New-Object System.Drawing.Rectangle 0, 0, $size, $size
        $graphics.DrawImage($image, $target, 0, 0, $image.Width, $image.Height,
            [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function PngBytes([System.Drawing.Bitmap]$bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$stream.ToArray()
}

$Source = (Resolve-Path $Source).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$image = [System.Drawing.Image]::FromFile($Source)
try {
    if ($image.Width -ne $image.Height) {
        throw "icon.png must be square; it is $($image.Width)x$($image.Height)."
    }
    if ($image.Width -lt 256) {
        throw "icon.png must be at least 256x256 for the largest icon size; it is $($image.Width)x$($image.Height)."
    }

    $frames = foreach ($size in $Sizes) {
        $bitmap = Resize $image $size
        try { [pscustomobject]@{ Size = $size; Data = (PngBytes $bitmap) } }
        finally { $bitmap.Dispose() }
    }

    # ICO container: ICONDIR (6 bytes), one 16-byte ICONDIRENTRY per frame, then the image data.
    $icoPath = Join-Path $OutDir 'icon.ico'
    $file = [System.IO.File]::Create($icoPath)
    $writer = New-Object System.IO.BinaryWriter $file
    try {
        $writer.Write([uint16]0)               # reserved
        $writer.Write([uint16]1)               # type: icon
        $writer.Write([uint16]$frames.Count)

        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }   # 0 means 256
            $writer.Write([byte]$dimension)    # width
            $writer.Write([byte]$dimension)    # height
            $writer.Write([byte]0)             # palette size
            $writer.Write([byte]0)             # reserved
            $writer.Write([uint16]1)           # colour planes
            $writer.Write([uint16]32)          # bits per pixel
            $writer.Write([uint32]$frame.Data.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Data.Length
        }
        foreach ($frame in $frames) { $writer.Write($frame.Data) }
    }
    finally {
        $writer.Dispose()
    }

    $caption = Resize $image 64
    try { $caption.Save((Join-Path $OutDir 'icon-64.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $caption.Dispose() }
}
finally {
    $image.Dispose()
}

Write-Host "Wrote $icoPath ($($Sizes -join ', ') px) and icon-64.png"

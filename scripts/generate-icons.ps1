param(
    [string]$AppAssets = (Join-Path $PSScriptRoot '..\src\MediaCatcher.App\Assets'),
    [string]$ExtensionIcons = (Join-Path $PSScriptRoot '..\extension\icons')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-MediaCatcherBitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(1, [int]($Size * 0.06))
    $rect = New-Object System.Drawing.Rectangle($inset, $inset, ($Size - 2 * $inset), ($Size - 2 * $inset))
    $radius = [Math]::Max(2, [int]($Size * 0.22))

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $gradientBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 14, 116, 233),
        [System.Drawing.Color]::FromArgb(255, 8, 43, 128),
        45.0
    )
    $graphics.FillPath($gradientBrush, $path)

    $arrowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(1.5, $Size * 0.082))
    $arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $centerX = $Size / 2.0
    $shaftTop = $Size * 0.24
    $shaftBottom = $Size * 0.56
    $graphics.DrawLine($arrowPen, $centerX, $shaftTop, $centerX, $shaftBottom)

    $headWidth = $Size * 0.13
    $graphics.DrawLine($arrowPen, ($centerX - $headWidth), ($shaftBottom - $headWidth * 0.65), $centerX, $shaftBottom)
    $graphics.DrawLine($arrowPen, ($centerX + $headWidth), ($shaftBottom - $headWidth * 0.65), $centerX, $shaftBottom)

    $graphics.DrawLine($arrowPen, ($Size * 0.30), ($Size * 0.72), ($Size * 0.70), ($Size * 0.72))

    $arrowPen.Dispose()
    $gradientBrush.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function Save-PngIcon {
    param([int]$Size, [string]$Path)
    $bitmap = New-MediaCatcherBitmap -Size $Size
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

New-Item -ItemType Directory -Path $AppAssets -Force | Out-Null
New-Item -ItemType Directory -Path $ExtensionIcons -Force | Out-Null

$pngPath = Join-Path $AppAssets 'mediacatcher.png'
$bitmap = New-MediaCatcherBitmap -Size 256
$bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$iconStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($iconStream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]1)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]32)
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)
$writer.Write($pngBytes)
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $AppAssets 'mediacatcher.ico'), $iconStream.ToArray())
$writer.Dispose()
$iconStream.Dispose()

foreach ($size in 16, 32, 48, 128) {
    Save-PngIcon -Size $size -Path (Join-Path $ExtensionIcons "icon$size.png")
}

Write-Output "Generated app icon and extension icons."

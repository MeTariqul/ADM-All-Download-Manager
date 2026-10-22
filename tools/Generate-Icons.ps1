Add-Type -AssemblyName System.Drawing

function New-AdmBitmap([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(94, 147, 255),
        [System.Drawing.Color]::FromArgb(52, 94, 210),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)

    $radius = [Math]::Max(2, [int]($size * 0.22))
    $diameter = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc($size - $diameter, 0, $diameter, $diameter, 270, 90)
    $path.AddArc($size - $diameter, $size - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc(0, $size - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $graphics.FillPath($brush, $path)

    $font = New-Object System.Drawing.Font("Segoe UI", [Math]::Max(8, $size * 0.62), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $graphics.DrawString([char]0x2193, $font, [System.Drawing.Brushes]::White, $textRect, $format)

    $font.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    return $bitmap
}

$root = Split-Path -Parent $PSScriptRoot
$extensionIcons = Join-Path $root 'extension\icons'
New-Item -ItemType Directory -Force $extensionIcons | Out-Null

foreach ($size in 16, 32, 48, 128) {
    $bitmap = New-AdmBitmap $size
    $bitmap.Save((Join-Path $extensionIcons "icon$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# Multi-resolution .ico with PNG-compressed frames (Windows Vista and later).
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = @()
foreach ($size in $sizes) {
    $bitmap = New-AdmBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $bitmap.Dispose()
}

$iconPath = Join-Path $root 'src\ADM.App\app.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) {
    $writer.Write($frame.Bytes)
}

$writer.Flush()
$writer.Dispose()
$file.Dispose()

Write-Output "Icons written to $extensionIcons"
Write-Output "Application icon written to $iconPath"

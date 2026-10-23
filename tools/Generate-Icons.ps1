Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $root 'assets\adm-final-logo.jpg'
if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "The final logo source is missing: $sourcePath"
}

function New-AdmBitmap([int]$size) {
    $source = [System.Drawing.Image]::FromFile($sourcePath)
    try {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

            # Carve transparent rounded corners around every derived logo.
            # This keeps the final mark clean in the taskbar, tray, shortcuts,
            # installer and browser toolbar instead of showing a square image.
            $radius = [Math]::Max(2, [int]($size * 0.16))
            $diameter = $radius * 2
            $clip = New-Object System.Drawing.Drawing2D.GraphicsPath
            try {
                $clip.AddArc(0, 0, $diameter, $diameter, 180, 90)
                $clip.AddArc($size - $diameter, 0, $diameter, $diameter, 270, 90)
                $clip.AddArc($size - $diameter, $size - $diameter, $diameter, $diameter, 0, 90)
                $clip.AddArc(0, $size - $diameter, $diameter, $diameter, 90, 90)
                $clip.CloseFigure()
                $graphics.SetClip($clip)
                $graphics.DrawImage($source, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
            }
            finally {
                $clip.Dispose()
            }
        }
        finally {
            $graphics.Dispose()
        }

        return $bitmap
    }
    finally {
        $source.Dispose()
    }
}

$extensionIcons = Join-Path $root 'extension\icons'
New-Item -ItemType Directory -Force $extensionIcons | Out-Null

foreach ($size in 16, 32, 48, 128) {
    $bitmap = New-AdmBitmap $size
    $bitmap.Save((Join-Path $extensionIcons "icon$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

$brandPath = Join-Path $root 'src\ADM.App\brand.png'
$brandBitmap = New-AdmBitmap 128
$brandBitmap.Save($brandPath, [System.Drawing.Imaging.ImageFormat]::Png)
$brandBitmap.Dispose()

# Multi-resolution .ico with PNG frames for Windows, shortcuts, taskbar and tray.
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

Write-Output "ADM final-logo assets written to $extensionIcons"
Write-Output "ADM application logo written to $brandPath"
Write-Output "ADM application icon written to $iconPath"

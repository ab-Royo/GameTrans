$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\GameTrans\app.ico'

function New-IconBitmap([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [single]($size * 0.16)
    $d = $r * 2
    $w = $size - 1
    $h = $size - 1
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $h - $d, $d, $d, 0, 90)
    $path.AddArc(0, $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, $size, $size),
        [System.Drawing.Color]::FromArgb(255, 36, 38, 48),
        [System.Drawing.Color]::FromArgb(255, 16, 17, 22),
        [single]60)
    $g.FillPath($bg, $path)

    $penW = [single][Math]::Max(1, $size * 0.06)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 124, 107, 255), $penW)
    $g.DrawPath($pen, $path)

    if ($size -ge 20) {
        $f = [System.Drawing.Font]::new('Microsoft YaHei', [single]($size * 0.52),
            [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $sf = [System.Drawing.StringFormat]::new()
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString('译', $f, [System.Drawing.Brushes]::White,
            [System.Drawing.PointF]::new([single]($size / 2), [single]($size / 2)), $sf)
        $f.Dispose()
        $sf.Dispose()
    }

    $g.Dispose()
    $bg.Dispose()
    $pen.Dispose()
    $path.Dispose()
    return $bmp
}

# ICO DIB：40 字节 BITMAPINFOHEADER + 自底向上 32bpp BGRA 像素 + AND 掩码
function New-IconDib([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $rect = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
    $locked = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $locked.Stride
    $pixels = New-Object byte[] ($stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($locked)

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([int]40)
    $bw.Write([int]$size)
    $bw.Write([int]($size * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int]0)
    $bw.Write([int]($stride * $size))
    $bw.Write([int]0)
    $bw.Write([int]0)
    $bw.Write([int]0)
    $bw.Write([int]0)

    for ($y = $size - 1; $y -ge 0; $y--) {
        $bw.Write($pixels, $y * $stride, $stride)
    }

    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $size)))

    $bw.Flush()
    $arr = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()
    return , $arr
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $data = New-IconDib $bmp
    $entries += , @{ Size = $s; Data = $data }
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = 0
    if ($e.Size -lt 256) { $dim = $e.Size }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$e.Data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $bw.Write($e.Data) }
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

$icon = [System.Drawing.Icon]::new($out)
Write-Host ("app.ico: {0} 字节 / {1} 个尺寸 / 默认 {2}x{3}" -f `
    (Get-Item $out).Length, $sizes.Count, $icon.Width, $icon.Height) -ForegroundColor Green
$icon.Dispose()

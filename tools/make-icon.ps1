# Generates the application icon (heart + pulse line) as a multi-size .ico.
# Own artwork: drawn from primitives here, so the provenance question is closed.
Add-Type -AssemblyName System.Drawing

$OutIco = $args[0]
if (-not $OutIco) { throw "usage: make-icon.ps1 <out.ico>" }

function New-HeartBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 100.0
    function P([double]$x, [double]$y) {
        New-Object System.Drawing.PointF(([float]($x * $s)), ([float]($y * $s)))
    }

    # Heart outline: bottom tip -> left lobe -> centre notch -> right lobe -> back to tip.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddBezier((P 50 92), (P 14 62), (P 6 38), (P 22 25))
    $path.AddBezier((P 22 25), (P 35 15), (P 47 20), (P 50 31))
    $path.AddBezier((P 50 31), (P 53 20), (P 65 15), (P 78 25))
    $path.AddBezier((P 78 25), (P 94 38), (P 86 62), (P 50 92))
    $path.CloseFigure()

    $rect = New-Object System.Drawing.RectangleF(0, 0, [float]$size, [float]$size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 232, 79, 58),
        [System.Drawing.Color]::FromArgb(255, 198, 42, 32),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($brush, $path)

    # ECG pulse across the heart, cut in white. Below 32px the spikes smear into a blob
    # and the icon reads as a heart with a hole in it - there the plain heart is the better mark.
    if ($size -ge 32) {
        $pulse = @((P 17 55), (P 33 55), (P 40 42), (P 48 70), (P 57 48), (P 64 55), (P 83 55))
        $penWidth = [Math]::Max(2.4, $size * 0.075)
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 255, 255), [float]$penWidth)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawLines($pen, [System.Drawing.PointF[]]$pulse)
        $pen.Dispose()
    }

    $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

# Render each size on its own canvas: downscaling one big bitmap smears the pulse line at 16px.
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($sz in $sizes) {
    $bmp = New-HeartBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $sz; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)                  # reserved
$bw.Write([UInt16]1)                  # type: icon
$bw.Write([UInt16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = $f.Size
    if ($dim -ge 256) { $dim = 0 }
    $bw.Write([byte]$dim)             # width
    $bw.Write([byte]$dim)             # height
    $bw.Write([byte]0)                # palette
    $bw.Write([byte]0)                # reserved
    $bw.Write([UInt16]1)              # colour planes
    $bw.Write([UInt16]32)             # bits per pixel
    $bw.Write([UInt32]$f.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutIco, $out.ToArray())
$bw.Dispose(); $out.Dispose()

Write-Output "written: $OutIco ($((Get-Item $OutIco).Length) bytes, $($frames.Count) frames)"

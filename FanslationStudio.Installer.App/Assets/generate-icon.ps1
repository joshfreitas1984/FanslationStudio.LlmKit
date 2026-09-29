# Regenerates FanslationStudio.png (256px) and FanslationStudio.ico (16-256px) from the vector drawing below.
# Run with: pwsh ./generate-icon.ps1
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

# Draws the icon on a 256x256 design grid scaled to $size.
function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $violet = [System.Drawing.Color]::FromArgb(255, 109, 40, 217)
    $deepViolet = [System.Drawing.Color]::FromArgb(255, 88, 28, 180)
    $fuchsia = [System.Drawing.Color]::FromArgb(255, 192, 38, 211)
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 8, 8), (New-Object System.Drawing.PointF 248, 248), $deepViolet, $fuchsia)

    # Tile
    $tile = New-RoundedRect 8 8 240 240 56
    $g.FillPath($gradient, $tile)

    $white = [System.Drawing.Brushes]::White
    $navy = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 16, 101))

    function New-Pt([float]$x, [float]$y) { New-Object System.Drawing.PointF $x, $y }

    # Back bubble (source language): white, with a Chinese character drawn as strokes
    $g.FillPath($white, (New-RoundedRect 30 34 124 96 24))
    $g.FillPolygon($white, [System.Drawing.PointF[]]@((New-Pt 52 122), (New-Pt 52 164), (New-Pt 92 126)))

    $ink = New-Object System.Drawing.Pen $violet, 9
    $ink.StartCap = 'Round'; $ink.EndCap = 'Round'; $ink.LineJoin = 'Round'
    $g.DrawLine($ink, 92, 50, 92, 62)          # top tick
    $g.DrawLine($ink, 60, 72, 124, 72)         # bar
    $g.DrawLine($ink, 78, 72, 122, 114)        # diagonal
    $g.DrawLine($ink, 106, 72, 62, 114)        # diagonal

    # Front bubble (target language): navy with a white outline to separate it, holding a Latin "A"
    $outline = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 7
    $outline.LineJoin = 'Round'
    $front = New-RoundedRect 102 110 124 96 24
    $frontTail = [System.Drawing.PointF[]]@((New-Pt 204 200), (New-Pt 204 234), (New-Pt 166 204))
    $g.DrawPath($outline, $front)
    $g.DrawPolygon($outline, $frontTail)
    $g.FillPath($navy, $front)
    $g.FillPolygon($navy, $frontTail)

    $letter = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 10
    $letter.StartCap = 'Round'; $letter.EndCap = 'Round'; $letter.LineJoin = 'Round'
    $g.DrawLine($letter, 164, 128, 137, 188)   # A left leg
    $g.DrawLine($letter, 164, 128, 191, 188)   # A right leg
    $g.DrawLine($letter, 147, 170, 181, 170)   # A crossbar

    $g.Dispose()
    $bmp
}

function ConvertTo-PngBytes($bitmap) {
    $ms = New-Object System.IO.MemoryStream
    $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

$here = $PSScriptRoot
$sizes = 16, 24, 32, 48, 64, 128, 256

$png = @{}
foreach ($s in $sizes) { $bmp = New-IconBitmap $s; $png[$s] = ConvertTo-PngBytes $bmp; if ($s -eq 256) { $bmp.Save((Join-Path $here 'FanslationStudio.png'), [System.Drawing.Imaging.ImageFormat]::Png) }; $bmp.Dispose() }

# ICO container with PNG-compressed images (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$png[$s].Length); $w.Write([uint32]$offset)
    $offset += $png[$s].Length
}
foreach ($s in $sizes) { $w.Write($png[$s]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $here 'FanslationStudio.ico'), $out.ToArray())
Write-Host "Wrote FanslationStudio.png and FanslationStudio.ico"

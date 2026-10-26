param([string]$OutIco, [string]$PreviewPng)
Add-Type -AssemblyName System.Drawing

function P($x, $y, $k) { New-Object System.Drawing.PointF ([float]($x * $k)), ([float]($y * $k)) }
function C($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function ButtonPath($k) {
    # Same outline as LeagueAutoAccept's button (measured from its icon): flat top,
    # sides flaring outward, then one broad curve to the bottom center.
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddLine((P 22 35 $k), (P 234 35 $k))
    $p.AddLine((P 234 35 $k), (P 252 104 $k))
    $p.AddBezier((P 252 104 $k), (P 216 134 $k), (P 166 158 $k), (P 128 158 $k))
    $p.AddBezier((P 128 158 $k), (P 90 158 $k), (P 40 134 $k), (P 4 104 $k))
    $p.CloseFigure()
    $p
}

function ShieldPath($cx, $top, $w, $h, $k) {
    $l = $cx - $w / 2; $r = $cx + $w / 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddBezier((P $l ($top + 8) $k), (P ($cx - $w / 4) ($top + 8) $k), (P ($cx - 6) ($top + 2) $k), (P $cx $top $k))
    $p.AddBezier((P $cx $top $k), (P ($cx + 6) ($top + 2) $k), (P ($cx + $w / 4) ($top + 8) $k), (P $r ($top + 8) $k))
    $p.AddLine((P $r ($top + 8) $k), (P $r ($top + $h * 0.45) $k))
    $p.AddBezier((P $r ($top + $h * 0.45) $k), (P $r ($top + $h * 0.75) $k), (P ($cx + $w * 0.25) ($top + $h * 0.9) $k), (P $cx ($top + $h) $k))
    $p.AddBezier((P $cx ($top + $h) $k), (P ($cx - $w * 0.25) ($top + $h * 0.9) $k), (P $l ($top + $h * 0.75) $k), (P $l ($top + $h * 0.45) $k))
    $p.CloseFigure()
    $p
}

function DrawShield($g, $cx, $top, $w, $h, $k) {
    $outline = ShieldPath $cx $top $w $h $k
    $rect = $outline.GetBounds()
    $gold = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (C '#FBE48A'), (C '#A8741E'), 60
    $g.FillPath($gold, $outline)
    $g.DrawPath((New-Object System.Drawing.Pen (C '#2B1D06'), ([float](7 * $k))), $outline)

    # Inner bevel
    $inner = ShieldPath $cx ($top + 11) ($w - 24) ($h - 22) $k
    $g.FillPath((New-Object System.Drawing.SolidBrush (C '#C8962F')), $inner)
    $g.DrawPath((New-Object System.Drawing.Pen (C '#FFF1B8'), ([float](3 * $k))), $inner)

    # Red X
    $cy = $top + $h * 0.46; $a = $w * 0.2
    foreach ($pass in @(@('#3A0A06', 22), @('#E0402F', 13))) {
        $pen = New-Object System.Drawing.Pen (C $pass[0]), ([float]($pass[1] * $k))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawLine($pen, (P ($cx - $a) ($cy - $a) $k), (P ($cx + $a) ($cy + $a) $k))
        $g.DrawLine($pen, (P ($cx + $a) ($cy - $a) $k), (P ($cx - $a) ($cy + $a) $k))
    }
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 256.0

    if ($size -le 24) {
        # Too small for text: just the shield, filling the icon.
        DrawShield $g 128 10 220 236 $k
    }
    else {
        $btn = ButtonPath $k
        $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((P 0 35 $k)), ((P 0 158 $k)), (C '#215F7E'), (C '#0A2738')
        $g.FillPath($fill, $btn)
        $g.DrawPath((New-Object System.Drawing.Pen (C '#79CDEB'), ([float](5 * $k))), $btn)

        $font = New-Object System.Drawing.Font 'Segoe UI', ([float](46 * $k)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
        $box = New-Object System.Drawing.RectangleF ([float](4 * $k)), ([float](54 * $k)), ([float](248 * $k)), ([float](70 * $k))
        $g.DrawString('DECLINE', $font, (New-Object System.Drawing.SolidBrush (C '#DDF5FF')), $box, $fmt)

        DrawShield $g 188 126 116 122 $k
    }
    $g.Dispose()
    $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = Render $s; $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray()
}

# ICO container with PNG-compressed entries.
$out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[IO.File]::WriteAllBytes($OutIco, $out.ToArray())

# Preview: 256 on dark and light, plus every size at 1:1.
$prev = New-Object System.Drawing.Bitmap 760, 300
$g = [System.Drawing.Graphics]::FromImage($prev); $g.Clear((C '#202020'))
$g.FillRectangle((New-Object System.Drawing.SolidBrush (C '#F3F3F3')), 270, 0, 270, 300)
$big = Render 256; $g.DrawImage($big, 8, 22); $g.DrawImage($big, 278, 22)
$x = 560; $y = 10
foreach ($s in $sizes[0..4]) { $b = Render $s; $g.DrawImage($b, $x, $y, $s, $s); $y += $s + 8 }
$g.Dispose(); $prev.Save($PreviewPng)
"wrote $OutIco ($((Get-Item $OutIco).Length) bytes)"

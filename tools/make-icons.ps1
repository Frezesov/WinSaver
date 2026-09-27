param([string]$OutDir = (Join-Path $PSScriptRoot '..\Assets'))

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-IconBitmap([int]$s, [System.Drawing.Color]$tile) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $tilePath = New-RoundedPath 0 0 $s $s ([Math]::Max(2, $s * 0.22))
    $g.FillPath((New-Object System.Drawing.SolidBrush($tile)), $tilePath)

    $white = [System.Drawing.Color]::White
    $small = $s -le 24

    # corners of a selection frame
    $pw = [Math]::Max(1.5, $s * ($(if ($small) { 0.09 } else { 0.075 })))
    $pen = New-Object System.Drawing.Pen($white, [float]$pw)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $m = $s * ($(if ($small) { 0.22 } else { 0.24 }))
    $len = $s * ($(if ($small) { 0.2 } else { 0.17 }))
    $a = $m; $b = $s - $m
    foreach ($corner in @(@($a, $a, 1, 1), @($b, $a, -1, 1), @($a, $b, 1, -1), @($b, $b, -1, -1))) {
        $x = [float]$corner[0]; $y = [float]$corner[1]; $dx = [float]($corner[2] * $len); $dy = [float]($corner[3] * $len)
        $pts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF($x, ($y + $dy))),
            (New-Object System.Drawing.PointF($x, $y)),
            (New-Object System.Drawing.PointF(($x + $dx), $y)))
        $g.DrawLines($pen, $pts)
    }

    # the captured spot
    $r = $s * ($(if ($small) { 0.1 } else { 0.085 }))
    $c = $s / 2
    $g.FillEllipse((New-Object System.Drawing.SolidBrush($white)), [float]($c - $r), [float]($c - $r), [float](2 * $r), [float](2 * $r))

    $g.Dispose()
    $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write([int]($s * $s * 4 + $maskStride * $s))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $p = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$p.B); $bw.Write([byte]$p.G); $bw.Write([byte]$p.R); $bw.Write([byte]$p.A)
        }
    }
    $bw.Write((New-Object byte[] ($maskStride * $s)))
    $bw.Flush()
    , $ms.ToArray()
}

function Write-Ico([string]$path, [System.Drawing.Color]$tile) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 256
    $images = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($s in $sizes) {
        $bmp = New-IconBitmap $s $tile
        if ($s -eq 256) {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $images.Add($ms.ToArray())
        } else {
            $images.Add([byte[]](Get-DibBytes $bmp))
        }
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $images[$i].Length
        $dim = [byte]($(if ($s -ge 256) { 0 } else { $s }))
        $bw.Write($dim); $bw.Write($dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$len); $bw.Write([int]$offset)
        $offset += $len
    }
    foreach ($img in $images) { $bw.Write($img) }
    $bw.Close()
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$blue = [System.Drawing.ColorTranslator]::FromHtml('#0078D4')
Write-Ico (Join-Path $OutDir 'app.ico') $blue
if ($env:ICON_PREVIEW) {
    $sheet = New-Object System.Drawing.Bitmap(600, 300)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(32, 32, 32))
    $g.InterpolationMode = 'NearestNeighbor'
    $g.DrawImage((New-IconBitmap 256 $blue), 0, 0)
    $x = 272
    foreach ($s in 16, 24, 32) {
        $g.DrawImage((New-IconBitmap $s $blue), $x, 16, $s * 4, $s * 4)
        $g.FillRectangle([System.Drawing.Brushes]::White, $x, 160, $s * 4, $s * 4)
        $g.DrawImage((New-IconBitmap $s $blue), $x, 160, $s * 4, $s * 4)
        $x += $s * 4 + 8
    }
    $g.Dispose()
    $sheet.Save($env:ICON_PREVIEW, [System.Drawing.Imaging.ImageFormat]::Png)
}
"Icons written to $OutDir"

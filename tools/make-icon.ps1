<#
    Генерирует иконку приложения: тепловое пятно в палитре ironbow под
    прицелом-крестом — то же, что приложение рисует поверх кадра.

    Результат:
        src/ThermalApp/appicon.ico   (16…256 px, PNG внутри ICO)
        docs/icon.png                (256 px, для README)

    Запуск:
        pwsh -File tools\make-icon.ps1
#>
param(
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256),
    [int]$Supersample = 4
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$icoPath = Join-Path $root 'src\ThermalApp\appicon.ico'
$pngPath = Join-Path $root 'docs\icon.png'
New-Item -ItemType Directory -Force -Path (Split-Path $pngPath) | Out-Null

# палитра ironbow: позиция (0 = центр пятна, 1 = край) -> цвет
$stops = @(
    @{ p = 0.00; c = @(255, 255, 255) },
    @{ p = 0.16; c = @(255, 236, 170) },
    @{ p = 0.34; c = @(255, 160, 32)  },
    @{ p = 0.54; c = @(226, 74, 58)   },
    @{ p = 0.72; c = @(146, 30, 132)  },
    @{ p = 0.88; c = @(48, 20, 92)    },
    @{ p = 1.00; c = @(22, 22, 32)    }
)

function Get-HeatColor([double]$t) {
    if ($t -lt 0) { $t = 0 } elseif ($t -gt 1) { $t = 1 }
    for ($i = 0; $i -lt $stops.Count - 1; $i++) {
        $a = $stops[$i]; $b = $stops[$i + 1]
        if ($t -le $b.p) {
            $span = $b.p - $a.p
            $f = if ($span -le 0) { 0 } else { ($t - $a.p) / $span }
            return @(
                [int][math]::Round($a.c[0] + ($b.c[0] - $a.c[0]) * $f),
                [int][math]::Round($a.c[1] + ($b.c[1] - $a.c[1]) * $f),
                [int][math]::Round($a.c[2] + ($b.c[2] - $a.c[2]) * $f)
            )
        }
    }
    return $stops[-1].c
}

function New-IconBitmap([int]$size) {
    $big = $size * $Supersample
    $bmp = New-Object System.Drawing.Bitmap $big, $big, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $c = ($big - 1) / 2.0
    $corner = $big * 0.24          # радиус скругления квадрата
    $half = $big / 2.0
    $flat = $half - $corner
    $bloom = $big * 0.66           # радиус теплового пятна
    # пятно смещено от центра — так это читается как горячий объект, а не мишень.
    # на мелких размерах смещение только ломает симметрию, поэтому его нет
    $shift = if ($size -le 32) { 0.0 } else { 1.0 }
    $bx = $c + $big * 0.10 * $shift
    $by = $c + $big * 0.08 * $shift

    # тепловое пятно + маска скруглённого квадрата, по пикселям
    $rect = New-Object System.Drawing.Rectangle 0, 0, $big, $big
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $buf = New-Object byte[] ($stride * $big)

    # предрасчёт градиента, чтобы не считать интерполяцию для каждого пикселя
    $lutSize = 512
    $lut = New-Object 'byte[,]' $lutSize, 3
    for ($i = 0; $i -lt $lutSize; $i++) {
        $rgb = Get-HeatColor ($i / ($lutSize - 1))
        $lut[$i, 0] = [byte]$rgb[0]; $lut[$i, 1] = [byte]$rgb[1]; $lut[$i, 2] = [byte]$rgb[2]
    }

    for ($y = 0; $y -lt $big; $y++) {
        $row = $y * $stride
        $dyc = $y - $c
        $dyb = $y - $by
        for ($x = 0; $x -lt $big; $x++) {
            $dxc = $x - $c
            $dxb = $x - $bx

            # signed distance до скруглённого квадрата
            $qx = [math]::Abs($dxc) - $flat; if ($qx -lt 0) { $qx = 0 }
            $qy = [math]::Abs($dyc) - $flat; if ($qy -lt 0) { $qy = 0 }
            $inside = ([math]::Sqrt($qx * $qx + $qy * $qy) - $corner) -le 0

            $o = $row + $x * 4
            if ($inside) {
                $t = [math]::Sqrt($dxb * $dxb + $dyb * $dyb) / $bloom
                if ($t -gt 1) { $t = 1 }
                $idx = [int]($t * ($lutSize - 1))
                $buf[$o + 0] = $lut[$idx, 2]   # B
                $buf[$o + 1] = $lut[$idx, 1]   # G
                $buf[$o + 2] = $lut[$idx, 0]   # R
                $buf[$o + 3] = 255
            }
        }
    }
    [System.Runtime.InteropServices.Marshal]::Copy($buf, 0, $data.Scan0, $buf.Length)
    $bmp.UnlockBits($data)

    # прицел, как маркер измерения в приложении
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    # на мелких размерах линии надо утолщать, иначе прицел исчезает
    if ($size -le 24)     { $thick = $big * 0.085; $ring = $big * 0.185; $arm = $big * 0.44 }
    elseif ($size -le 32) { $thick = $big * 0.065; $ring = $big * 0.160; $arm = $big * 0.42 }
    elseif ($size -le 48) { $thick = $big * 0.050; $ring = $big * 0.140; $arm = $big * 0.39 }
    else                  { $thick = $big * 0.038; $ring = $big * 0.125; $arm = $big * 0.36 }
    $thick = [math]::Max(1.0, $thick)
    $gap = $ring * 1.65

    # на 16-24 px тень только мешает
    $pens = @()
    if ($size -ge 32) {
        $shadow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 0, 0, 0)), ($thick * 2.1)
        $shadow.StartCap = $shadow.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pens += $shadow
    }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), $thick
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pens += $pen

    foreach ($p in $pens) {
        $g.DrawEllipse($p, [single]($bx - $ring), [single]($by - $ring), [single]($ring * 2), [single]($ring * 2))
        $g.DrawLine($p, [single]($bx - $arm), [single]$by, [single]($bx - $gap), [single]$by)
        $g.DrawLine($p, [single]($bx + $gap), [single]$by, [single]($bx + $arm), [single]$by)
        $g.DrawLine($p, [single]$bx, [single]($by - $arm), [single]$bx, [single]($by - $gap))
        $g.DrawLine($p, [single]$bx, [single]($by + $gap), [single]$bx, [single]($by + $arm))
    }
    foreach ($p in $pens) { $p.Dispose() }
    $g.Dispose()

    if ($big -eq $size) { return $bmp }

    # уменьшаем с супервыборкой — так получаются гладкие углы и линии
    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g2.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    $g2.Dispose(); $bmp.Dispose()
    return $out
}

# рендерим все размеры и пакуем PNG-ы в ICO
$pngs = @{}
foreach ($s in $Sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    if ($s -eq 256) { $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose(); $ms.Dispose()
    Write-Host ("  {0,3}x{0,-3} {1,6} байт" -f $s, $pngs[$s].Length)
}

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type: icon
$bw.Write([uint16]$Sizes.Count)

$offset = 6 + 16 * $Sizes.Count
foreach ($s in $Sizes) {
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))   # width
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))   # height
    $bw.Write([byte]0)               # цветов в палитре
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # planes
    $bw.Write([uint16]32)            # бит на пиксель
    $bw.Write([uint32]$pngs[$s].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $Sizes) { $bw.Write($pngs[$s]) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host "Готово: $icoPath ($((Get-Item $icoPath).Length) байт)"
Write-Host "        $pngPath"


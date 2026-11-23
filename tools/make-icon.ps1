# Dessine l'icône (cible rouge) et l'écrit dans src\app.ico
# Toutes les tailles en bitmap 32 bits : lisible par Windows et par .NET Framework.
Add-Type -AssemblyName System.Drawing
$out   = Join-Path $PSScriptRoot '..\src\app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function New-IconImage([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $m = [Math]::Max(0.5, $s * 0.03)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 20, 60))), $m, $m, $s - 2 * $m, $s - 2 * $m)
    if ($s -ge 24) {
        $w = $s * 0.09
        $r = $s * 0.27
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $w)
        $g.DrawEllipse($pen, $s / 2 - $r, $s / 2 - $r, 2 * $r, 2 * $r)
    }
    $d = $s * 0.13
    if ($s -lt 24) { $d = $s * 0.18 }
    $g.FillEllipse([System.Drawing.Brushes]::White, $s / 2 - $d, $s / 2 - $d, 2 * $d, 2 * $d)
    $g.Dispose()

    # Pixels BGRA de bas en haut + masque AND vide (la transparence vient du canal alpha)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $data = $bmp.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = $s * 4
    $pixels = New-Object byte[] ($row * $s)
    for ($y = 0; $y -lt $s; $y++) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $pixels, ($s - 1 - $y) * $row, $row)
    }
    $bmp.UnlockBits($data)
    $bmp.Dispose()
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int](2 * $s))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($pixels.Length + $maskRow * $s))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    $bw.Write($pixels)
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush()
    return , $ms.ToArray()
}

$images = foreach ($s in $sizes) { , (New-IconImage $s) }
$fs = [System.IO.File]::Create((Resolve-Path (Split-Path $out)).Path + '\app.ico')
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$images[$i].Length); $bw.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
$bw.Close()
Write-Host "Icône écrite : $out"

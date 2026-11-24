# Draws SshNotepad\notepad.ico (an original spiral-notepad icon) at several sizes.
param([string]$Preview)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
function New-Rounded([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath; $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return , $p
}
function New-Color([string]$hex) { [Drawing.ColorTranslator]::FromHtml($hex) }
$images = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 128, 256) {
    $b = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 64, $size / 64) # drawn on a 64 x 64 grid
    $g.FillPath((New-Object Drawing.SolidBrush (New-Color '#2F7FB8')), (New-Rounded 11 9 44 53 5))   # back cover
    $page = New-Object Drawing.Drawing2D.LinearGradientBrush ((New-Object Drawing.Point 0, 8), (New-Object Drawing.Point 0, 60), (New-Color '#FFFFFF'), (New-Color '#DCEFFA'))
    $g.FillPath($page, (New-Rounded 9 7 44 52 4))
    $g.SetClip((New-Rounded 9 7 44 52 4))
    $g.FillRectangle((New-Object Drawing.SolidBrush (New-Color '#4FB3E8')), 9, 7, 44, 10)           # header band
    $g.ResetClip()
    $w = if ($size -lt 32) { 4.0 } else { 2.6 }
    $pen = New-Object Drawing.Pen (New-Color '#7FA9C4'), $w; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $rows = if ($size -lt 32) { 27, 37, 47 } else { 26, 34, 42, 50 }
    foreach ($y in $rows) { $end = if ($y -eq $rows[-1]) { 36 } else { 46 }; $g.DrawLine($pen, 16, $y, $end, $y) }
    $ring = New-Object Drawing.Pen (New-Color '#35566B'), $(if ($size -lt 32) { 4.5 } else { 3 }); $ring.StartCap = 'Round'; $ring.EndCap = 'Round'
    $xs = if ($size -lt 32) { 18, 31, 44 } else { 16, 23.5, 31, 38.5, 46 }
    foreach ($x in $xs) { $g.DrawLine($ring, [single]$x, 3, [single]$x, 11) }
    $g.Dispose()
    $m = New-Object IO.MemoryStream; $b.Save($m, 'Png')
    if ($Preview -and $size -eq 256) { $b.Save($Preview) }
    [pscustomobject]@{ Size = $size; Data = $m.ToArray() }
}
$out = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $bw.Write([byte]($i.Size % 256)); $bw.Write([byte]($i.Size % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$i.Data.Length); $bw.Write([uint32]$offset)
    $offset += $i.Data.Length
}
foreach ($i in $images) { $bw.Write([byte[]]$i.Data) }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'SshNotepad\notepad.ico'), $out.ToArray())

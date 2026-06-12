# Generates the hMailServer Control Panel application icon (.ico with
# PNG-compressed 256/64/32/16 px images of the gradient brand tile).
Add-Type -AssemblyName System.Drawing

function New-TilePng {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'

    $rect = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(47, 129, 247),
        [System.Drawing.Color]::FromArgb(163, 113, 247),
        45)

    $radius = [int]($Size * 0.22)
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($Size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($Size - $d - 1, $Size - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $Size - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($grad, $path)

    $fontSize = [single]($Size * 0.34)
    $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold)
    $text = 'hM'
    $measured = $g.MeasureString($text, $font)
    $x = ($Size - $measured.Width) / 2
    $y = ($Size - $measured.Height) / 2
    $g.DrawString($text, $font, [System.Drawing.Brushes]::White, $x, $y)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

$sizes = 256, 64, 32, 16
$images = @{}
foreach ($s in $sizes) { $images[$s] = New-TilePng -Size $s }

$out = Join-Path $PSScriptRoot '..\hmailserver\source\Tools\ControlPanel\app.ico'
$stream = [System.IO.File]::Create($out)
$writer = New-Object System.IO.BinaryWriter($stream)

# ICONDIR
$writer.Write([uint16]0)            # reserved
$writer.Write([uint16]1)            # type: icon
$writer.Write([uint16]([uint16]$sizes.Count)) # image count

[uint32]$offset = [uint32](6 + 16 * $sizes.Count)
foreach ($s in $sizes) {
    $bytes = $images[$s]
    $writer.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # width
    $writer.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # height
    $writer.Write([byte]0)           # palette
    $writer.Write([byte]0)           # reserved
    $writer.Write([uint16]1)         # planes
    $writer.Write([uint16]32)        # bpp
    $writer.Write([uint32]($bytes.Length))
    $writer.Write([uint32]$offset)
    $offset = $offset + [uint32]$bytes.Length
}
foreach ($s in $sizes) { $writer.Write([byte[]]$images[$s]) }

$writer.Close()
$info = Get-Item $out
Write-Host "wrote $out ($($info.Length) bytes)"

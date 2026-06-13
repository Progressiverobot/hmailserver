# Generates modern InnoSetup wizard bitmaps for the hMailServer installer.
# setup.bmp        164 x 314  (left banner)
# setup-small.bmp   55 x 55   (top-right corner image)
Add-Type -AssemblyName System.Drawing

function New-Banner {
    param([string]$Path, [int]$W, [int]$H, [bool]$Small)

    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'ClearTypeGridFit'

    # Deep space gradient
    $rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(7, 11, 18),
        [System.Drawing.Color]::FromArgb(18, 24, 38),
        65)
    $g.FillRectangle($grad, $rect)

    # Accent glow orbs
    $orb = New-Object System.Drawing.Drawing2D.GraphicsPath
    $orb.AddEllipse(-$W * 0.4, -$H * 0.18, $W * 1.1, $W * 1.1)
    $orbBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($orb)
    $orbBrush.CenterColor = [System.Drawing.Color]::FromArgb(110, 54, 194, 255)
    $orbBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 54, 194, 255))
    $g.FillPath($orbBrush, $orb)

    $orb2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $orb2.AddEllipse($W * 0.25, $H * 0.62, $W * 1.0, $W * 1.0)
    $orb2Brush = New-Object System.Drawing.Drawing2D.PathGradientBrush($orb2)
    $orb2Brush.CenterColor = [System.Drawing.Color]::FromArgb(90, 124, 92, 255)
    $orb2Brush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 124, 92, 255))
    $g.FillPath($orb2Brush, $orb2)

    # Subtle grid
    $gridPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(14, 255, 255, 255))
    for ($x = 0; $x -lt $W; $x += 14) { $g.DrawLine($gridPen, $x, 0, $x, $H) }
    for ($y = 0; $y -lt $H; $y += 14) { $g.DrawLine($gridPen, 0, $y, $W, $y) }

    # Logo tile
    $tile = if ($Small) { 26 } else { 44 }
    $tx = if ($Small) { ($W - $tile) / 2 } else { 18 }
    $ty = if ($Small) { ($H - $tile) / 2 - 4 } else { 26 }
    $tileRect = New-Object System.Drawing.Rectangle($tx, $ty, $tile, $tile)
    $tileGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $tileRect,
        [System.Drawing.Color]::FromArgb(54, 194, 255),
        [System.Drawing.Color]::FromArgb(124, 92, 255),
        45)
    $tilePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = if ($Small) { 7 } else { 11 }
    $d = $r * 2
    $tilePath.AddArc($tileRect.X, $tileRect.Y, $d, $d, 180, 90)
    $tilePath.AddArc($tileRect.Right - $d, $tileRect.Y, $d, $d, 270, 90)
    $tilePath.AddArc($tileRect.Right - $d, $tileRect.Bottom - $d, $d, $d, 0, 90)
    $tilePath.AddArc($tileRect.X, $tileRect.Bottom - $d, $d, $d, 90, 90)
    $tilePath.CloseFigure()
    $g.FillPath($tileGrad, $tilePath)

    $logoFontSize = if ($Small) { 11 } else { 17 }
    $logoFont = New-Object System.Drawing.Font('Segoe UI', $logoFontSize, [System.Drawing.FontStyle]::Bold)
    $white = [System.Drawing.Brushes]::White
    $logoSize = $g.MeasureString('hM', $logoFont)
    $g.DrawString('hM', $logoFont, $white,
        $tileRect.X + ($tile - $logoSize.Width) / 2,
        $tileRect.Y + ($tile - $logoSize.Height) / 2 + 1)

    if (-not $Small) {
        # Product name + tagline
        $nameFont = New-Object System.Drawing.Font('Segoe UI', 15, [System.Drawing.FontStyle]::Bold)
        $g.DrawString('hMailServer', $nameFont, $white, 14, 84)

        $verFont = New-Object System.Drawing.Font('Segoe UI', 10)
        $accentBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(54, 194, 255))
        $g.DrawString('6', $verFont, $accentBrush, 17, 112)

        $tagFont = New-Object System.Drawing.Font('Segoe UI', 8)
        $mutedBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(139, 148, 158))
        $g.DrawString("Open source email server`nfor Windows", $tagFont, $mutedBrush, 17, 140)

        $sep = ' ' + [char]0x00B7 + ' '
        $g.DrawString("SMTP$($sep)IMAP$($sep)POP3`nMTA-STS$($sep)DANE$($sep)ARC`nACME$($sep)REST API", $tagFont, $mutedBrush, 17, 226)

        # Accent base line
        $lineY = $H - 4
        $lineRect = New-Object System.Drawing.Rectangle(0, $lineY, $W, 4)
        $lineGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $lineRect,
            [System.Drawing.Color]::FromArgb(54, 194, 255),
            [System.Drawing.Color]::FromArgb(124, 92, 255),
            [single]0)
        $g.FillRectangle($lineGrad, $lineRect)
    }

    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
    Write-Host "wrote $Path"
}

$dir = Join-Path $PSScriptRoot '..\hmailserver\installation'
New-Banner -Path (Join-Path $dir 'setup.bmp') -W 164 -H 314 -Small $false
New-Banner -Path (Join-Path $dir 'setup-small.bmp') -W 55 -H 55 -Small $true

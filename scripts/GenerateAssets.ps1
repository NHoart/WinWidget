[CmdletBinding()]
param(
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptRoot = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptRoot)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrEmpty($OutputRoot)) {
    $OutputRoot = Join-Path $scriptRoot '..\src\WidgetProvider\Assets'
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

function New-RoundedPath {
    param([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = [Math]::Min($Radius * 2, [Math]::Min($Width, $Height))
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Icon {
    param(
        [string]$FileName,
        [int]$Size,
        [string]$Color1,
        [string]$Color2,
        [ValidateSet('Note', 'Bars', 'Mark')]
        [string]$Kind
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $path = New-RoundedPath 0 0 $Size $Size ($Size * 0.22)
    $rect = [System.Drawing.Rectangle]::new(0, 0, $Size, $Size)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect,
        [System.Drawing.ColorTranslator]::FromHtml($Color1),
        [System.Drawing.ColorTranslator]::FromHtml($Color2),
        45.0)
    $graphics.FillPath($brush, $path)

    $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
    $soft = [System.Drawing.Color]::FromArgb(210, 255, 255, 255)

    switch ($Kind) {
        'Note' {
            $font = [System.Drawing.Font]::new('Segoe UI Symbol', $Size * 0.55, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
            $format = [System.Drawing.StringFormat]::new()
            $format.Alignment = [System.Drawing.StringAlignment]::Center
            $format.LineAlignment = [System.Drawing.StringAlignment]::Center
            $textBrush = [System.Drawing.SolidBrush]::new($white)
            $graphics.DrawString([char]0x266A, $font, $textBrush, [System.Drawing.RectangleF]::new(0, 0, $Size, $Size * 0.98), $format)
            $textBrush.Dispose()
            $format.Dispose()
            $font.Dispose()
        }
        'Bars' {
            $barWidth = $Size * 0.12
            $gap = $Size * 0.09
            $baseY = $Size * 0.72
            $heights = @(($Size * 0.22), ($Size * 0.40), ($Size * 0.58))
            $colors = @(
                [System.Drawing.Color]::FromArgb(170, 255, 255, 255),
                [System.Drawing.Color]::FromArgb(215, 255, 255, 255),
                [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
            )
            for ($i = 0; $i -lt 3; $i++) {
                $x = $Size * 0.28 + $i * ($barWidth + $gap)
                $barPath = New-RoundedPath $x ($baseY - $heights[$i]) $barWidth $heights[$i] ($barWidth / 2)
                $barBrush = [System.Drawing.SolidBrush]::new($colors[$i])
                $graphics.FillPath($barBrush, $barPath)
                $barBrush.Dispose()
                $barPath.Dispose()
            }
        }
        'Mark' {
            $cell = $Size * 0.19
            $gap = $Size * 0.07
            $start = ($Size - ($cell * 2 + $gap)) / 2
            $alpha = @(255, 190, 190, 255)
            $index = 0
            for ($row = 0; $row -lt 2; $row++) {
                for ($column = 0; $column -lt 2; $column++) {
                    $x = $start + $column * ($cell + $gap)
                    $y = $start + $row * ($cell + $gap)
                    $cellPath = New-RoundedPath $x $y $cell $cell ($cell * 0.24)
                    $cellBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb($alpha[$index], 255, 255, 255))
                    $graphics.FillPath($cellBrush, $cellPath)
                    $cellBrush.Dispose()
                    $cellPath.Dispose()
                    $index++
                }
            }
        }
    }

    $destination = Join-Path $OutputRoot $FileName
    $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)

    $brush.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Host "Created $FileName"
}

function Draw-Text {
    param(
        [System.Drawing.Graphics]$Graphics,
        [string]$Text,
        [System.Drawing.Font]$Font,
        [System.Drawing.Brush]$Brush,
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [string]$Align = 'Near'
    )

    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::$Align
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $format.Trimming = [System.Drawing.StringTrimming]::EllipsisCharacter
    $format.FormatFlags = [System.Drawing.StringFormatFlags]::NoWrap
    $rect = [System.Drawing.RectangleF]::new($X, $Y, $Width, $Height)
    $Graphics.DrawString($Text, $Font, $Brush, $rect, $format)
    $format.Dispose()
}

function New-Screenshot {
    param(
        [string]$FileName,
        [bool]$Dark,
        [ValidateSet('NowPlaying', 'System')]
        [string]$Kind
    )

    $width = 300
    $height = 304
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)

    if ($Dark) {
        $cardColor = [System.Drawing.ColorTranslator]::FromHtml('#1F1F1F')
        $primary = [System.Drawing.ColorTranslator]::FromHtml('#FFFFFF')
        $secondary = [System.Drawing.ColorTranslator]::FromHtml('#A6A6A6')
        $track = [System.Drawing.ColorTranslator]::FromHtml('#3A3A3A')
        $border = [System.Drawing.Color]::FromArgb(40, 255, 255, 255)
    }
    else {
        $cardColor = [System.Drawing.ColorTranslator]::FromHtml('#F7F7F7')
        $primary = [System.Drawing.ColorTranslator]::FromHtml('#1B1B1B')
        $secondary = [System.Drawing.ColorTranslator]::FromHtml('#5C5C5C')
        $track = [System.Drawing.ColorTranslator]::FromHtml('#DEDEDE')
        $border = [System.Drawing.Color]::FromArgb(30, 0, 0, 0)
    }

    $cardPath = New-RoundedPath 0.5 0.5 ($width - 1) ($height - 1) 18
    $cardBrush = [System.Drawing.SolidBrush]::new($cardColor)
    $graphics.FillPath($cardBrush, $cardPath)
    $borderPen = [System.Drawing.Pen]::new($border, 1)
    $graphics.DrawPath($borderPen, $cardPath)

    $primaryBrush = [System.Drawing.SolidBrush]::new($primary)
    $secondaryBrush = [System.Drawing.SolidBrush]::new($secondary)
    $accentBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#7C6CF2'))

    if ($Kind -eq 'NowPlaying') {
        $labelFont = [System.Drawing.Font]::new('Segoe UI', 10, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $titleFont = [System.Drawing.Font]::new('Segoe UI', 18, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $bodyFont = [System.Drawing.Font]::new('Segoe UI', 13, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $smallFont = [System.Drawing.Font]::new('Segoe UI', 11, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

        Draw-Text $graphics 'JETZT LÄUFT' $labelFont $accentBrush 20 20 260 18
        Draw-Text $graphics 'Midnight City' $titleFont $primaryBrush 20 52 260 30
        Draw-Text $graphics 'M83' $bodyFont $secondaryBrush 20 84 260 20

        $artRect = [System.Drawing.Rectangle]::new(20, 120, 104, 104)
        $artBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $artRect,
            [System.Drawing.ColorTranslator]::FromHtml('#6C4CF1'),
            [System.Drawing.ColorTranslator]::FromHtml('#2CB7C9'),
            35.0)
        $artPath = New-RoundedPath 20 120 104 104 10
        $graphics.FillPath($artBrush, $artPath)

        Draw-Text $graphics 'Hurry Up, We''re Dreaming' $bodyFont $primaryBrush 20 242 260 22
        Draw-Text $graphics 'Album · 2011' $smallFont $secondaryBrush 20 264 260 18
        Draw-Text $graphics 'Wiedergabe läuft' $smallFont $secondaryBrush 20 282 260 18

        $artBrush.Dispose()
        $artPath.Dispose()
        $labelFont.Dispose()
        $titleFont.Dispose()
        $bodyFont.Dispose()
        $smallFont.Dispose()
    }
    else {
        $titleFont = [System.Drawing.Font]::new('Segoe UI', 18, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $smallFont = [System.Drawing.Font]::new('Segoe UI', 11, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $valueFont = [System.Drawing.Font]::new('Segoe UI', 26, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $labelFont = [System.Drawing.Font]::new('Segoe UI', 10, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

        Draw-Text $graphics 'System' $titleFont $primaryBrush 20 22 260 26
        Draw-Text $graphics 'Aktualisiert um 18:42' $smallFont $secondaryBrush 20 50 260 18

        $values = @('23 %', '61 %', '87 %')
        $labels = @('CPU', 'Arbeitsspeicher', 'Akku')
        $details = @('16 Kerne', '19,5 / 32 GB', 'Lädt')

        for ($i = 0; $i -lt 3; $i++) {
            $x = 20 + $i * 88
            Draw-Text $graphics $values[$i] $valueFont $primaryBrush $x 90 82 36
            Draw-Text $graphics $labels[$i] $labelFont $secondaryBrush $x 128 82 16

            $trackPath = New-RoundedPath $x 154 74 5 2.5
            $trackBrush = [System.Drawing.SolidBrush]::new($track)
            $graphics.FillPath($trackBrush, $trackPath)
            $trackBrush.Dispose()
            $trackPath.Dispose()

            $fillWidths = @(17, 45, 64)
            $fillPath = New-RoundedPath $x 154 $fillWidths[$i] 5 2.5
            $accent = @('#7C6CF2', '#2CB7C9', '#59C27A')[$i]
            $fillBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($accent))
            $graphics.FillPath($fillBrush, $fillPath)
            $fillBrush.Dispose()
            $fillPath.Dispose()

            Draw-Text $graphics $details[$i] $smallFont $secondaryBrush $x 172 82 18
        }

        $dividerPen = [System.Drawing.Pen]::new($border, 1)
        $graphics.DrawLine($dividerPen, 20, 218, 280, 218)
        $dividerPen.Dispose()

        Draw-Text $graphics 'Kleine und mittlere Größe' $smallFont $secondaryBrush 20 232 260 18
        Draw-Text $graphics 'Aktualisierung bei Bedarf' $smallFont $secondaryBrush 20 252 260 18

        $titleFont.Dispose()
        $smallFont.Dispose()
        $valueFont.Dispose()
        $labelFont.Dispose()
    }

    $destination = Join-Path $OutputRoot $FileName
    $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)

    $accentBrush.Dispose()
    $secondaryBrush.Dispose()
    $primaryBrush.Dispose()
    $borderPen.Dispose()
    $cardBrush.Dispose()
    $cardPath.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Host "Created $FileName"
}

New-Icon -FileName 'ProviderIcon.png' -Size 64 -Color1 '#4C6FFF' -Color2 '#6C4CF1' -Kind Mark
New-Icon -FileName 'NowPlayingIcon.png' -Size 64 -Color1 '#6C4CF1' -Color2 '#A84CF1' -Kind Note
New-Icon -FileName 'SystemIcon.png' -Size 64 -Color1 '#1E9F8A' -Color2 '#2CB7C9' -Kind Bars
New-Icon -FileName 'StoreLogo.png' -Size 50 -Color1 '#4C6FFF' -Color2 '#6C4CF1' -Kind Mark
New-Icon -FileName 'Square44x44Logo.png' -Size 44 -Color1 '#4C6FFF' -Color2 '#6C4CF1' -Kind Mark
New-Icon -FileName 'Square150x150Logo.png' -Size 150 -Color1 '#4C6FFF' -Color2 '#6C4CF1' -Kind Mark

New-Screenshot -FileName 'NowPlayingScreenshot.png' -Dark $true -Kind NowPlaying
New-Screenshot -FileName 'NowPlayingScreenshotDark.png' -Dark $true -Kind NowPlaying
New-Screenshot -FileName 'NowPlayingScreenshotLight.png' -Dark $false -Kind NowPlaying
New-Screenshot -FileName 'SystemScreenshot.png' -Dark $true -Kind System
New-Screenshot -FileName 'SystemScreenshotDark.png' -Dark $true -Kind System
New-Screenshot -FileName 'SystemScreenshotLight.png' -Dark $false -Kind System

Write-Host "Assets written to $OutputRoot"

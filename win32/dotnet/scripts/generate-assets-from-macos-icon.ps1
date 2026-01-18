param(
    [Parameter(Mandatory = $false)]
    [string]$SourceIcon = "src\Easydict.WinUI\Assets\macos\white-black-icon.appiconset\icon_512x512@2x.png",

    [Parameter(Mandatory = $false)]
    [string]$AssetsDir = "src\Easydict.WinUI\Assets"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-IconBitmap {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Image]$Source,

        [Parameter(Mandatory = $true)]
        [int]$TargetWidth,

        [Parameter(Mandatory = $true)]
        [int]$TargetHeight,

        [Parameter(Mandatory = $true)]
        [double]$Scale
    )

    $bitmap = New-Object System.Drawing.Bitmap $TargetWidth, $TargetHeight, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $maxSide = [Math]::Min($TargetWidth, $TargetHeight)
        $iconSide = [int][Math]::Round($maxSide * $Scale)

        # Preserve source aspect ratio (should be square, but keep safe)
        $ratio = [Math]::Min($iconSide / $Source.Width, $iconSide / $Source.Height)
        $drawW = [int][Math]::Round($Source.Width * $ratio)
        $drawH = [int][Math]::Round($Source.Height * $ratio)

        $x = [int][Math]::Round(($TargetWidth - $drawW) / 2.0)
        $y = [int][Math]::Round(($TargetHeight - $drawH) / 2.0)

        $destRect = New-Object System.Drawing.Rectangle $x, $y, $drawW, $drawH
        $graphics.DrawImage($Source, $destRect)

        return $bitmap
    }
    finally {
        $graphics.Dispose()
    }
}

$dotnetRoot = Split-Path -Parent $PSScriptRoot

$assetsFull = (Resolve-Path -LiteralPath (Join-Path $dotnetRoot $AssetsDir)).Path
$sourceFull = (Resolve-Path -LiteralPath (Join-Path $dotnetRoot $SourceIcon)).Path

Write-Host "Source icon: $sourceFull"
Write-Host "Assets dir : $assetsFull"

$sourceImage = [System.Drawing.Image]::FromFile($sourceFull)
try {
    $targets = Get-ChildItem -Path $assetsFull -Filter *.png -File | Where-Object {
        # Exclude copied macOS assets folder (keep them as source/materials)
        $_.FullName -notmatch ([Regex]::Escape([System.IO.Path]::Combine($assetsFull, 'macos')))
    }

    foreach ($file in $targets) {
        $existing = [System.Drawing.Image]::FromFile($file.FullName)
        try {
            $w = $existing.Width
            $h = $existing.Height
        }
        finally {
            $existing.Dispose()
        }

        # Heuristics: wide/splash assets usually look better with padding
        $scale = 1.0
        if ($file.Name -match 'Wide|SplashScreen') {
            $scale = 0.70
        }

        $newBitmap = New-IconBitmap -Source $sourceImage -TargetWidth $w -TargetHeight $h -Scale $scale
        try {
            $tmp = "$($file.FullName).tmp"
            $newBitmap.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png)
            Move-Item -Force -LiteralPath $tmp -Destination $file.FullName
            Write-Host ("Updated {0} -> {1}x{2} (scale {3})" -f $file.Name, $w, $h, $scale)
        }
        finally {
            $newBitmap.Dispose()
        }
    }
}
finally {
    $sourceImage.Dispose()
}

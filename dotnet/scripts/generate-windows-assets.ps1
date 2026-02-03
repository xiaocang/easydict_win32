# PowerShell script to generate multi-scale Windows assets from high-resolution icon
# Generates scale-100, scale-125, scale-150, scale-175, and scale-200 variants
# Also generates targetsize variants for taskbar/Start menu

param(
    [string]$SourceIcon = "screenshot/icon_512x512@2x.png",
    [string]$UnplatedIcon = "dotnet/src/Easydict.WinUI/Assets/icon_unplated_1024.png",
    [string]$OutputDir = "dotnet/src/Easydict.WinUI/Assets"
)

# Add System.Drawing assembly
Add-Type -AssemblyName System.Drawing

Write-Host "Generating multi-scale Windows assets..." -ForegroundColor Cyan
Write-Host "Source: $SourceIcon" -ForegroundColor Gray
Write-Host "Output: $OutputDir" -ForegroundColor Gray
Write-Host ""

# Function to resize image with high quality
function Resize-Image {
    param(
        [string]$SourcePath,
        [string]$OutputPath,
        [int]$Width,
        [int]$Height
    )

    try {
        $sourceImage = [System.Drawing.Image]::FromFile($SourcePath)
        $destImage = New-Object System.Drawing.Bitmap($Width, $Height)
        $graphics = [System.Drawing.Graphics]::FromImage($destImage)

        # Use high-quality settings
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $graphics.DrawImage($sourceImage, 0, 0, $Width, $Height)

        # Save with high quality
        $destImage.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)

        $graphics.Dispose()
        $destImage.Dispose()
        $sourceImage.Dispose()

        Write-Host "  Created: $OutputPath ($Width x $Height)" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "  Failed: $OutputPath - $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Function to resize icon centered on a wide transparent canvas
function Resize-ImageWide {
    param(
        [string]$SourcePath,
        [string]$OutputPath,
        [int]$Width,
        [int]$Height
    )

    try {
        $sourceImage = [System.Drawing.Image]::FromFile($SourcePath)

        # Fit icon to canvas height, center horizontally
        $iconSize = $Height
        $xOffset = [int](($Width - $iconSize) / 2)

        $destImage = New-Object System.Drawing.Bitmap($Width, $Height)
        $graphics = [System.Drawing.Graphics]::FromImage($destImage)

        # Clear with transparent background
        $graphics.Clear([System.Drawing.Color]::Transparent)

        # Use high-quality settings
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $graphics.DrawImage($sourceImage, $xOffset, 0, $iconSize, $iconSize)

        $destImage.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)

        $graphics.Dispose()
        $destImage.Dispose()
        $sourceImage.Dispose()

        Write-Host "  Created: $OutputPath ($Width x $Height)" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "  Failed: $OutputPath - $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Asset definitions with scale variants
$assets = @(
    @{
        Name = "Square44x44Logo"
        Scales = @{
            100 = 44
            125 = 55
            150 = 66
            175 = 77
            200 = 88
        }
    },
    @{
        Name = "Square150x150Logo"
        Scales = @{
            100 = 150
            125 = 188
            150 = 225
            175 = 263
            200 = 300
        }
    },
    @{
        Name = "Wide310x150Logo"
        IsWide = $true
        Scales = @{
            100 = @{W=310; H=150}
            125 = @{W=388; H=188}
            150 = @{W=465; H=225}
            175 = @{W=543; H=263}
            200 = @{W=620; H=300}
        }
    },
    @{
        Name = "SplashScreen"
        IsWide = $true
        Scales = @{
            100 = @{W=620; H=300}
            125 = @{W=775; H=375}
            150 = @{W=930; H=450}
            175 = @{W=1085; H=525}
            200 = @{W=1240; H=600}
        }
    },
    @{
        Name = "LockScreenLogo"
        Scales = @{
            100 = 24
            125 = 30
            150 = 36
            175 = 42
            200 = 48
        }
    },
    @{
        Name = "StoreLogo"
        Scales = @{
            100 = 50
            125 = 63
            150 = 75
            175 = 88
            200 = 100
        }
    }
)

if (-not (Test-Path $SourceIcon)) {
    Write-Host "ERROR: Source icon not found: $SourceIcon" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $UnplatedIcon)) {
    Write-Host "WARNING: Unplated icon not found: $UnplatedIcon" -ForegroundColor Yellow
    Write-Host "  Unplated variants will use the same source as plated variants." -ForegroundColor Yellow
    Write-Host "  To generate a proper unplated icon (transparent background), run:" -ForegroundColor Yellow
    Write-Host "    python3 scripts/generate-unplated-icon.py" -ForegroundColor Yellow
    $UnplatedIcon = $SourceIcon
}

Write-Host "Using source icon: $SourceIcon (1024x1024)" -ForegroundColor Cyan
Write-Host "Using unplated icon: $UnplatedIcon" -ForegroundColor Cyan
Write-Host ""

$successCount = 0
$failCount = 0

# Generate scale variants
foreach ($asset in $assets) {
    Write-Host "Generating: $($asset.Name)" -ForegroundColor Yellow

    foreach ($scale in $asset.Scales.Keys | Sort-Object) {
        $scaleValue = $asset.Scales[$scale]
        $outputFileName = "$($asset.Name).scale-$scale.png"
        $outputPath = Join-Path $OutputDir $outputFileName

        if ($asset.IsWide) {
            $width = $scaleValue.W
            $height = $scaleValue.H
            if (Resize-ImageWide -SourcePath $SourceIcon -OutputPath $outputPath -Width $width -Height $height) {
                $successCount++
            } else {
                $failCount++
            }
        } else {
            $size = $scaleValue
            if (Resize-Image -SourcePath $SourceIcon -OutputPath $outputPath -Width $size -Height $size) {
                $successCount++
            } else {
                $failCount++
            }
        }
    }

    Write-Host ""
}

# Generate targetsize variants for Square44x44Logo (used by taskbar, Start menu, etc.)
Write-Host "Generating: Square44x44Logo targetsize variants" -ForegroundColor Yellow
$targetSizes = @(16, 24, 32, 48, 256)
foreach ($size in $targetSizes) {
    # Plated version
    $outputPath = Join-Path $OutputDir "Square44x44Logo.targetsize-$size.png"
    if (Resize-Image -SourcePath $SourceIcon -OutputPath $outputPath -Width $size -Height $size) {
        $successCount++
    } else {
        $failCount++
    }

    # Unplated version (transparent background, no plate)
    $outputPath = Join-Path $OutputDir "Square44x44Logo.targetsize-${size}_altform-unplated.png"
    if (Resize-Image -SourcePath $UnplatedIcon -OutputPath $outputPath -Width $size -Height $size) {
        $successCount++
    } else {
        $failCount++
    }
}
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Asset generation complete!" -ForegroundColor Green
Write-Host "  Successful: $successCount" -ForegroundColor Green
Write-Host "  Failed: $failCount" -ForegroundColor $(if ($failCount -gt 0) { "Red" } else { "Gray" })
Write-Host "========================================" -ForegroundColor Cyan

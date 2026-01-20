# PowerShell script to generate multi-scale Windows assets from macOS high-resolution icons
# Generates scale-100, scale-125, scale-150, scale-175, and scale-200 variants

param(
    [string]$SourceDir = "dotnet/src/Easydict.WinUI/Assets/macos/white-black-icon.appiconset",
    [string]$OutputDir = "dotnet/src/Easydict.WinUI/Assets"
)

# Add System.Drawing assembly
Add-Type -AssemblyName System.Drawing

Write-Host "Generating multi-scale Windows assets..." -ForegroundColor Cyan
Write-Host "Source: $SourceDir" -ForegroundColor Gray
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

        Write-Host "  ✓ Created: $OutputPath ($Width x $Height)" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "  ✗ Failed: $OutputPath - $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Asset definitions
# Format: @{ Name = "AssetName"; Base = @{100% = WxH, 125% = WxH, ...} }
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
    }
)

# Use the highest resolution source icon (512x512@2x = 1024x1024)
$sourceIcon512x2 = Join-Path $SourceDir "icon_512x512@2x.png"
$sourceIcon256x2 = Join-Path $SourceDir "icon_256x256@2x.png"

if (-not (Test-Path $sourceIcon512x2)) {
    Write-Host "ERROR: Source icon not found: $sourceIcon512x2" -ForegroundColor Red
    exit 1
}

Write-Host "Using source icon: $sourceIcon512x2 (1024x1024)" -ForegroundColor Cyan
Write-Host ""

$successCount = 0
$failCount = 0

# Generate assets
foreach ($asset in $assets) {
    Write-Host "Generating: $($asset.Name)" -ForegroundColor Yellow

    foreach ($scale in $asset.Scales.Keys | Sort-Object) {
        $scaleValue = $asset.Scales[$scale]
        $outputFileName = "$($asset.Name).scale-$scale.png"
        $outputPath = Join-Path $OutputDir $outputFileName

        # Skip if it's the 200% scale (already exists)
        if ($scale -eq 200 -and (Test-Path $outputPath)) {
            Write-Host "  ⊚ Skipped: $outputFileName (already exists)" -ForegroundColor DarkGray
            continue
        }

        # Determine dimensions
        if ($asset.IsWide) {
            $width = $scaleValue.W
            $height = $scaleValue.H
        } else {
            $width = $scaleValue
            $height = $scaleValue
        }

        # Use 512x512@2x for larger sizes, 256x256@2x for smaller
        $sourceFile = if ($width -gt 256 -or $height -gt 256) { $sourceIcon512x2 } else { $sourceIcon256x2 }

        if (Resize-Image -SourcePath $sourceFile -OutputPath $outputPath -Width $width -Height $height) {
            $successCount++
        } else {
            $failCount++
        }
    }

    Write-Host ""
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Asset generation complete!" -ForegroundColor Green
Write-Host "  ✓ Successful: $successCount" -ForegroundColor Green
Write-Host "  ✗ Failed: $failCount" -ForegroundColor $(if ($failCount -gt 0) { "Red" } else { "Gray" })
Write-Host "========================================" -ForegroundColor Cyan

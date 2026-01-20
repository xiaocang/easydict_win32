<#
.SYNOPSIS
    Converts all service icons from macOS to Windows multi-scale assets.

.DESCRIPTION
    Extracts service icons from macOS .imageset folders and generates
    Windows scale variants (100%, 125%, 150%, 175%, 200%) for all 28 services.

.PARAMETER SourceDir
    Path to macOS service-icon directory

.PARAMETER OutputDir
    Output directory for Windows service icons
#>

param(
    [string]$SourceDir = "../../../Easydict/App/Assets.xcassets/service-icon",
    [string]$OutputDir = "../src/Easydict.WinUI/Assets/ServiceIcons"
)

# Scale factors: 100%, 125%, 150%, 175%, 200%
$scales = @(100, 125, 150, 175, 200)

# Base size for service icons (at 100% scale)
$baseSize = 32

Write-Host "[Service Icon Converter] Starting conversion..." -ForegroundColor Cyan

# Resolve paths
$sourceDirPath = Join-Path $PSScriptRoot $SourceDir | Resolve-Path -ErrorAction Stop
$outputDirPath = Join-Path $PSScriptRoot $OutputDir

if (-not (Test-Path $outputDirPath)) {
    New-Item -ItemType Directory -Path $outputDirPath -Force | Out-Null
    Write-Host "[Service Icon Converter] Created output directory: $outputDirPath" -ForegroundColor Gray
}

Write-Host "[Service Icon Converter] Source: $sourceDirPath" -ForegroundColor Gray
Write-Host "[Service Icon Converter] Output: $outputDirPath" -ForegroundColor Gray

# Load System.Drawing
Add-Type -AssemblyName System.Drawing

# Find all .imageset directories
$imagesets = Get-ChildItem -Path $sourceDirPath -Directory -Filter "*.imageset"

Write-Host "`n[Service Icon Converter] Found $($imagesets.Count) service icons to convert" -ForegroundColor Green

$totalIcons = 0
$processedIcons = 0

foreach ($imageset in $imagesets) {
    $serviceName = $imageset.Name -replace '\.imageset$', ''
    
    # Find the @2x PNG file (highest quality source)
    $sourceFiles = Get-ChildItem -Path $imageset.FullName -Filter "*@2x.png"
    
    if ($sourceFiles.Count -eq 0) {
        # Fallback: try non-@2x PNG
        $sourceFiles = Get-ChildItem -Path $imageset.FullName -Filter "*.png"
    }
    
    if ($sourceFiles.Count -eq 0) {
        Write-Host "  [SKIP] $serviceName - No PNG file found" -ForegroundColor Yellow
        continue
    }
    
    $sourceFile = $sourceFiles[0]
    Write-Host "`n[Service Icon Converter] Processing: $serviceName" -ForegroundColor Yellow
    Write-Host "  Source file: $($sourceFile.Name)" -ForegroundColor Gray
    
    # Load source image
    $sourceImage = [System.Drawing.Image]::FromFile($sourceFile.FullName)
    
    foreach ($scale in $scales) {
        $targetSize = [int]($baseSize * $scale / 100)
        $outputFileName = "$serviceName.scale-$scale.png"
        $outputPath = Join-Path $outputDirPath $outputFileName
        
        # Create bitmap with target size
        $bitmap = New-Object System.Drawing.Bitmap($targetSize, $targetSize)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        
        # Set high-quality rendering
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        
        # Draw resized image
        $graphics.DrawImage($sourceImage, 0, 0, $targetSize, $targetSize)
        
        # Save as PNG
        $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
        
        # Cleanup
        $graphics.Dispose()
        $bitmap.Dispose()
        
        $processedIcons++
    }
    
    $sourceImage.Dispose()
    $totalIcons++
    Write-Host "  Generated 5 scale variants for $serviceName" -ForegroundColor Gray
}

Write-Host "`n[Service Icon Converter] ✅ Conversion complete!" -ForegroundColor Green
Write-Host "[Service Icon Converter] Processed: $totalIcons services" -ForegroundColor Cyan
Write-Host "[Service Icon Converter] Generated: $processedIcons icon files" -ForegroundColor Cyan
Write-Host "[Service Icon Converter] Output directory: $outputDirPath" -ForegroundColor Cyan

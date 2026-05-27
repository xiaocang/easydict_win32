[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ScreenshotRoot,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactName,

    [string]$Title = "UI automation screenshots",

    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY,

    [int]$MaxInlineBytes = 600000,

    [int]$MaxListedScreenshots = 120
)

$ErrorActionPreference = "Stop"

$summary = [System.Collections.Generic.List[string]]::new()

function Add-SummaryLine {
    param([string]$Line)
    $summary.Add($Line) | Out-Null
}

function Get-RelativeDisplayPath {
    param(
        [string]$Root,
        [string]$Path
    )

    return [System.IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Get-ScreenshotReviewCategory {
    param(
        [string]$RelativePath,
        [string]$FileName,
        [object]$Width,
        [object]$Height
    )

    $path = $RelativePath.ToLowerInvariant()
    $name = $FileName.ToLowerInvariant()

    if ($path.StartsWith("visual-diffs/", [StringComparison]::Ordinal) -or
        $name.EndsWith("_diff.png", [StringComparison]::Ordinal)) {
        return [pscustomobject]@{ Name = "visual diff"; Rank = 0 }
    }

    if ($null -ne $Width -and $null -ne $Height -and
        ([int]$Width -lt 64 -or [int]$Height -lt 64)) {
        return [pscustomobject]@{ Name = "suspicious screenshot dimensions"; Rank = 1 }
    }

    if ($path.StartsWith("baseline-candidates/", [StringComparison]::Ordinal)) {
        return [pscustomobject]@{ Name = "baseline candidate"; Rank = 2 }
    }

    if ($name -match "(not_found|failed|failure|missing|error|navigation_failed)") {
        return [pscustomobject]@{ Name = "diagnostic failure snapshot"; Rank = 3 }
    }

    return [pscustomobject]@{ Name = "regular screenshot"; Rank = 4 }
}

function Get-ScreenshotDimensions {
    param([System.IO.FileInfo]$File)

    try {
        Add-Type -AssemblyName System.Drawing
        $image = [System.Drawing.Image]::FromFile($File.FullName)
        try {
            return [pscustomobject]@{
                Width = [int]$image.Width
                Height = [int]$image.Height
                Display = "$($image.Width)x$($image.Height)"
            }
        }
        finally {
            $image.Dispose()
        }
    }
    catch {
        return [pscustomobject]@{
            Width = $null
            Height = $null
            Display = "unreadable"
        }
    }
}

function Save-ScreenshotGallery {
    param(
        [System.IO.FileInfo[]]$Images,
        [string]$Root,
        [string]$OutputPath,
        [int]$ThumbWidth,
        [int]$ThumbHeight,
        [int]$LabelHeight,
        [long]$JpegQuality
    )

    if ($Images.Count -eq 0) {
        return
    }

    Add-Type -AssemblyName System.Drawing

    $columns = [Math]::Min(4, [Math]::Max(1, $Images.Count))
    $padding = 12
    $rows = [int][Math]::Ceiling($Images.Count / [double]$columns)
    $sheetWidth = ($columns * $ThumbWidth) + (($columns + 1) * $padding)
    $sheetHeight = ($rows * ($LabelHeight + $ThumbHeight + $padding)) + $padding

    $sheet = [System.Drawing.Bitmap]::new($sheetWidth, $sheetHeight)
    $graphics = [System.Drawing.Graphics]::FromImage($sheet)
    $font = [System.Drawing.Font]::new("Segoe UI", 8)
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(32, 36, 43))
    $tileBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(245, 247, 250))
    $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(205, 213, 224))

    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

        for ($i = 0; $i -lt $Images.Count; $i++) {
            $row = [int][Math]::Floor($i / $columns)
            $column = $i % $columns
            $x = $padding + ($column * ($ThumbWidth + $padding))
            $y = $padding + ($row * ($LabelHeight + $ThumbHeight + $padding))
            $tile = [System.Drawing.Rectangle]::new($x, $y, $ThumbWidth, $LabelHeight + $ThumbHeight)
            $graphics.FillRectangle($tileBrush, $tile)
            $graphics.DrawRectangle($borderPen, $tile)

            $name = Get-RelativeDisplayPath -Root $Root -Path $Images[$i].FullName
            $graphics.DrawString($name, $font, $brush, [System.Drawing.RectangleF]::new($x + 6, $y + 6, $ThumbWidth - 12, $LabelHeight - 8))

            try {
                $image = [System.Drawing.Image]::FromFile($Images[$i].FullName)
                try {
                    $scale = [Math]::Min(($ThumbWidth - 12) / [double]$image.Width, ($ThumbHeight - 12) / [double]$image.Height)
                    $drawWidth = [int]($image.Width * $scale)
                    $drawHeight = [int]($image.Height * $scale)
                    $drawX = $x + [int](($ThumbWidth - $drawWidth) / 2)
                    $drawY = $y + $LabelHeight + [int](($ThumbHeight - $drawHeight) / 2)
                    $graphics.DrawImage($image, $drawX, $drawY, $drawWidth, $drawHeight)
                }
                finally {
                    $image.Dispose()
                }
            }
            catch {
                $message = "Could not load image: $($_.Exception.Message)"
                $graphics.DrawString($message, $font, $brush, [System.Drawing.RectangleF]::new($x + 6, $y + $LabelHeight + 6, $ThumbWidth - 12, $ThumbHeight - 12))
            }
        }

        $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
            Where-Object { $_.MimeType -eq "image/jpeg" } |
            Select-Object -First 1
        if ($null -eq $codec) {
            throw "JPEG encoder is not available."
        }

        $encoderParameters = [System.Drawing.Imaging.EncoderParameters]::new(1)
        $encoderParameters.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new(
            [System.Drawing.Imaging.Encoder]::Quality,
            $JpegQuality)
        try {
            $sheet.Save($OutputPath, $codec, $encoderParameters)
        }
        finally {
            $encoderParameters.Dispose()
        }
    }
    finally {
        $borderPen.Dispose()
        $tileBrush.Dispose()
        $brush.Dispose()
        $font.Dispose()
        $graphics.Dispose()
        $sheet.Dispose()
    }
}

Add-SummaryLine "## $Title"
Add-SummaryLine ""
Add-SummaryLine "Artifact: ``$ArtifactName``"
Add-SummaryLine "Path inside artifact: ``.``"

$resolvedRoot = Resolve-Path -LiteralPath $ScreenshotRoot -ErrorAction SilentlyContinue
if ($null -eq $resolvedRoot) {
    Add-SummaryLine ""
    Add-SummaryLine "No screenshot directory was produced."
}
else {
    $root = $resolvedRoot.Path
    $rawScreenshots = @(
        Get-ChildItem -LiteralPath $root -Recurse -Filter "*.png" -File |
            Where-Object { $_.Name -notlike "*gallery*" } |
            Sort-Object FullName
    )
    $screenshotEntries = @(
        $rawScreenshots | ForEach-Object {
            $relativePath = Get-RelativeDisplayPath -Root $root -Path $_.FullName
            $dimensions = Get-ScreenshotDimensions -File $_
            $category = Get-ScreenshotReviewCategory `
                -RelativePath $relativePath `
                -FileName $_.Name `
                -Width $dimensions.Width `
                -Height $dimensions.Height
            [pscustomobject]@{
                File = $_
                RelativePath = $relativePath
                Dimensions = $dimensions.Display
                Category = $category.Name
                Rank = $category.Rank
            }
        } | Sort-Object Rank, RelativePath
    )
    $screenshots = @($screenshotEntries | ForEach-Object { $_.File })

    Add-SummaryLine ""
    Add-SummaryLine "Generated **$($screenshots.Count)** screenshot(s)."

    if ($screenshots.Count -gt 0) {
        $galleryPath = Join-Path $root "ui-screenshot-gallery.jpg"

        try {
            Save-ScreenshotGallery `
                -Images $screenshots `
                -Root $root `
                -OutputPath $galleryPath `
                -ThumbWidth 200 `
                -ThumbHeight 135 `
                -LabelHeight 50 `
                -JpegQuality 76

            if ((Get-Item -LiteralPath $galleryPath).Length -gt $MaxInlineBytes) {
                Save-ScreenshotGallery `
                    -Images $screenshots `
                    -Root $root `
                    -OutputPath $galleryPath `
                    -ThumbWidth 145 `
                    -ThumbHeight 100 `
                    -LabelHeight 42 `
                    -JpegQuality 65
            }

            Add-SummaryLine ""
            Add-SummaryLine "### Gallery"

            $galleryBytes = [System.IO.File]::ReadAllBytes($galleryPath)
            if ($galleryBytes.Length -le $MaxInlineBytes) {
                $galleryBase64 = [Convert]::ToBase64String($galleryBytes)
                Add-SummaryLine "<img alt=""UI automation screenshot gallery"" src=""data:image/jpeg;base64,$galleryBase64"" />"
            }
            else {
                $relativeGalleryPath = Get-RelativeDisplayPath -Root $root -Path $galleryPath
                Add-SummaryLine "Gallery image was generated at ``$relativeGalleryPath`` in the artifact."
            }
        }
        catch {
            Add-SummaryLine ""
            Add-SummaryLine "Could not generate inline gallery: $($_.Exception.Message)"
        }

        $priorityEntries = @($screenshotEntries | Where-Object { $_.Rank -lt 4 })
        if ($priorityEntries.Count -gt 0) {
            Add-SummaryLine ""
            Add-SummaryLine "### Review priority"
            foreach ($entry in $priorityEntries) {
                Add-SummaryLine "- **$($entry.Category)**: ``$($entry.RelativePath)`` ($($entry.Dimensions))"
            }
        }

        Add-SummaryLine ""
        Add-SummaryLine "### Screenshot files"
        foreach ($entry in @($screenshotEntries | Select-Object -First $MaxListedScreenshots)) {
            Add-SummaryLine "- ``$($entry.RelativePath)`` ($($entry.Dimensions))"
        }

        if ($screenshots.Count -gt $MaxListedScreenshots) {
            Add-SummaryLine "- ... $($screenshots.Count - $MaxListedScreenshots) more screenshot(s)"
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
    $summaryParent = Split-Path -Parent $SummaryPath
    if (-not [string]::IsNullOrWhiteSpace($summaryParent)) {
        New-Item -ItemType Directory -Force -Path $summaryParent | Out-Null
    }

    $summary | Out-File -FilePath $SummaryPath -Append -Encoding utf8
}
else {
    $summary | ForEach-Object { Write-Host $_ }
}

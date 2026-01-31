<#
.SYNOPSIS
    Synchronize store listing metadata to Microsoft Partner Center using the msstore CLI.

.DESCRIPTION
    Reads listing JSON files from .winstore/listings/ and updates the Microsoft Store
    listing for each configured language via the Microsoft Store Developer CLI (msstore).

    This script can:
    - Update store listings (title, description, features, keywords) for all languages
    - Validate listing files before submission
    - Preview changes without submitting (dry-run mode)

.PARAMETER Mode
    Operation mode: 'validate', 'preview', or 'submit'.
    - validate: Check listing files for completeness and format issues
    - preview: Show what would be submitted without making changes
    - submit: Submit listing updates to Partner Center

.PARAMETER Languages
    Comma-separated list of languages to process (e.g., 'en-us,zh-cn').
    If not specified, all languages in store-config.json are processed.

.EXAMPLE
    # Validate all listings
    .\Sync-StoreListings.ps1 -Mode validate

    # Preview changes for English only
    .\Sync-StoreListings.ps1 -Mode preview -Languages en-us

    # Submit all listings
    .\Sync-StoreListings.ps1 -Mode submit
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('validate', 'preview', 'submit')]
    [string]$Mode,

    [Parameter(Mandatory = $false)]
    [string]$Languages = ""
)

$ErrorActionPreference = 'Stop'

# Resolve paths
$winStorePath = Split-Path -Parent $PSScriptRoot
$listingsPath = Join-Path $winStorePath "listings"
$configPath = Join-Path $winStorePath "store-config.json"

# Load store configuration
if (-not (Test-Path $configPath)) {
    Write-Error "Store configuration not found: $configPath"
    exit 1
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json
$appId = $config.app.id

Write-Host "=== Easydict Store Listing Sync ===" -ForegroundColor Cyan
Write-Host "App ID: $appId"
Write-Host "Mode: $Mode"
Write-Host ""

# Determine languages to process
if ($Languages -ne "") {
    $targetLanguages = $Languages -split ','
} else {
    $targetLanguages = $config.listing.languages
}

Write-Host "Languages: $($targetLanguages -join ', ')"
Write-Host ""

# Validation constraints (Microsoft Store limits)
$limits = @{
    TitleMaxLength = 256
    ShortDescriptionMaxLength = 100
    DescriptionMaxLength = 10000
    FeatureMaxLength = 200
    FeatureMaxCount = 20
    KeywordMaxLength = 40
    KeywordMaxCount = 7
    ReleaseNotesMaxLength = 1500
}

function Test-Listing {
    param([PSCustomObject]$listing, [string]$lang)

    $errors = @()
    $warnings = @()

    # Required fields
    if ([string]::IsNullOrWhiteSpace($listing.title)) {
        $errors += "[$lang] Missing required field: title"
    }
    if ([string]::IsNullOrWhiteSpace($listing.description)) {
        $errors += "[$lang] Missing required field: description"
    }
    if ([string]::IsNullOrWhiteSpace($listing.shortDescription)) {
        $warnings += "[$lang] Missing recommended field: shortDescription"
    }

    # Length checks
    if ($listing.title.Length -gt $limits.TitleMaxLength) {
        $errors += "[$lang] Title exceeds max length ($($listing.title.Length)/$($limits.TitleMaxLength))"
    }
    if ($listing.shortDescription -and $listing.shortDescription.Length -gt $limits.ShortDescriptionMaxLength) {
        $errors += "[$lang] Short description exceeds max length ($($listing.shortDescription.Length)/$($limits.ShortDescriptionMaxLength))"
    }
    if ($listing.description.Length -gt $limits.DescriptionMaxLength) {
        $errors += "[$lang] Description exceeds max length ($($listing.description.Length)/$($limits.DescriptionMaxLength))"
    }

    # Features
    if ($listing.features) {
        if ($listing.features.Count -gt $limits.FeatureMaxCount) {
            $errors += "[$lang] Too many features ($($listing.features.Count)/$($limits.FeatureMaxCount))"
        }
        foreach ($feature in $listing.features) {
            if ($feature.Length -gt $limits.FeatureMaxLength) {
                $errors += "[$lang] Feature exceeds max length ($($feature.Length)/$($limits.FeatureMaxLength)): $($feature.Substring(0, 50))..."
            }
        }
    }

    # Keywords
    if ($listing.keywords) {
        if ($listing.keywords.Count -gt $limits.KeywordMaxCount) {
            $warnings += "[$lang] Too many keywords ($($listing.keywords.Count)/$($limits.KeywordMaxCount)), only first $($limits.KeywordMaxCount) will be used"
        }
        foreach ($keyword in $listing.keywords) {
            if ($keyword.Length -gt $limits.KeywordMaxLength) {
                $errors += "[$lang] Keyword exceeds max length ($($keyword.Length)/$($limits.KeywordMaxLength)): $keyword"
            }
        }
    }

    # Release notes
    if ($listing.releaseNotes -and $listing.releaseNotes.Length -gt $limits.ReleaseNotesMaxLength) {
        $errors += "[$lang] Release notes exceed max length ($($listing.releaseNotes.Length)/$($limits.ReleaseNotesMaxLength))"
    }

    return @{
        Errors = $errors
        Warnings = $warnings
    }
}

# Process each language
$allErrors = @()
$allWarnings = @()
$processedCount = 0

foreach ($lang in $targetLanguages) {
    $listingFile = Join-Path $listingsPath "$lang.json"

    if (-not (Test-Path $listingFile)) {
        Write-Warning "Listing file not found for language: $lang (expected: $listingFile)"
        $allWarnings += "[$lang] Listing file not found"
        continue
    }

    Write-Host "--- Processing: $lang ---" -ForegroundColor Yellow
    $listing = Get-Content $listingFile -Raw -Encoding UTF8 | ConvertFrom-Json

    # Always validate
    $result = Test-Listing -listing $listing -lang $lang
    $allErrors += $result.Errors
    $allWarnings += $result.Warnings

    if ($result.Errors.Count -gt 0) {
        Write-Host "  ERRORS:" -ForegroundColor Red
        $result.Errors | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    }
    if ($result.Warnings.Count -gt 0) {
        Write-Host "  WARNINGS:" -ForegroundColor DarkYellow
        $result.Warnings | ForEach-Object { Write-Host "    - $_" -ForegroundColor DarkYellow }
    }

    if ($Mode -eq 'validate') {
        if ($result.Errors.Count -eq 0) {
            Write-Host "  OK: Listing is valid" -ForegroundColor Green
            Write-Host "    Title: $($listing.title)"
            Write-Host "    Short: $($listing.shortDescription)"
            Write-Host "    Description: $($listing.description.Length) chars"
            Write-Host "    Features: $($listing.features.Count)"
            Write-Host "    Keywords: $($listing.keywords.Count)"
        }
    }
    elseif ($Mode -eq 'preview') {
        Write-Host "  Title: $($listing.title)" -ForegroundColor White
        Write-Host "  Short Description: $($listing.shortDescription)" -ForegroundColor White
        Write-Host "  Description ($($listing.description.Length) chars):" -ForegroundColor White
        Write-Host "    $($listing.description.Substring(0, [Math]::Min(200, $listing.description.Length)))..." -ForegroundColor Gray
        Write-Host "  Features ($($listing.features.Count)):" -ForegroundColor White
        $listing.features | ForEach-Object { Write-Host "    - $_" -ForegroundColor Gray }
        Write-Host "  Keywords: $($listing.keywords -join ', ')" -ForegroundColor White
    }
    elseif ($Mode -eq 'submit') {
        if ($result.Errors.Count -gt 0) {
            Write-Warning "Skipping $lang due to validation errors"
            continue
        }

        Write-Host "  Submitting listing update for $lang..." -ForegroundColor Cyan

        # Build the msstore update command arguments
        # The msstore CLI uses Partner Center API to update listings
        $tempFile = $null
        try {
            # Create a temporary JSON payload for msstore
            $payload = @{
                listings = @{
                    $lang = @{
                        baseListing = @{
                            title = $listing.title
                            shortTitle = $listing.shortTitle
                            description = $listing.description
                            shortDescription = $listing.shortDescription
                            features = @($listing.features)
                            keywords = @($listing.keywords | Select-Object -First $limits.KeywordMaxCount)
                            copyrightAndTrademarkInfo = $listing.copyrightAndTrademarkInfo
                            developedBy = $listing.developedBy
                        }
                    }
                }
            }

            if ($listing.releaseNotes -and $listing.releaseNotes -ne "") {
                $payload.listings.$lang.baseListing.releaseNotes = $listing.releaseNotes
            }

            $payloadJson = $payload | ConvertTo-Json -Depth 10
            $tempFile = [System.IO.Path]::GetTempFileName() + ".json"
            $payloadJson | Set-Content -Path $tempFile -Encoding UTF8

            # Use msstore CLI to update the listing
            Write-Host "  Updating via msstore CLI..." -ForegroundColor Cyan
            $msstoreArgs = @("submission", "update", $appId, "--jsonPayload", $tempFile)
            & msstore @msstoreArgs

            if ($LASTEXITCODE -ne 0) {
                Write-Error "msstore submission update failed for $lang (exit code: $LASTEXITCODE)"
            } else {
                Write-Host "  Successfully updated listing for $lang" -ForegroundColor Green
            }
        }
        catch {
            Write-Error "Failed to update listing for $lang : $_"
        }
        finally {
            if ($tempFile -and (Test-Path $tempFile)) {
                Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
            }
        }
    }

    $processedCount++
    Write-Host ""
}

# Summary
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "Processed: $processedCount language(s)"
Write-Host "Errors: $($allErrors.Count)"
Write-Host "Warnings: $($allWarnings.Count)"

if ($allErrors.Count -gt 0) {
    Write-Host ""
    Write-Host "All Errors:" -ForegroundColor Red
    $allErrors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

if ($allWarnings.Count -gt 0) {
    Write-Host ""
    Write-Host "All Warnings:" -ForegroundColor DarkYellow
    $allWarnings | ForEach-Object { Write-Host "  - $_" -ForegroundColor DarkYellow }
}

Write-Host ""
Write-Host "Done!" -ForegroundColor Green

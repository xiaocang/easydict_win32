# Easydict WinUI Release Script
# Creates a git tag, pushes it to origin, and creates a GitHub release using gh CLI.

param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$FromTag,

    [switch]$PreRelease
)

$ErrorActionPreference = "Stop"

# Validate gh CLI is available
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host "Error: gh CLI is not installed. Install from https://cli.github.com/" -ForegroundColor Red
    exit 1
}

# Validate we're in a git repo
if (-not (git rev-parse --is-inside-work-tree 2>$null)) {
    Write-Host "Error: Not inside a git repository." -ForegroundColor Red
    exit 1
}

# Generate changelog if FromTag is specified
$notes = $Tag
if ($FromTag) {
    # Validate FromTag exists
    $tagExists = git tag -l $FromTag
    if (-not $tagExists) {
        Write-Host "Error: Tag '$FromTag' does not exist." -ForegroundColor Red
        exit 1
    }

    $commits = git log "$FromTag..HEAD" --oneline --no-merges 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Failed to get commit log." -ForegroundColor Red
        exit 1
    }

    # Build changelog grouped by type
    $features = @()
    $fixes = @()
    $refactors = @()
    $tests = @()
    $chores = @()
    $other = @()

    foreach ($line in $commits -split "`n") {
        $line = $line.Trim()
        if (-not $line) { continue }

        # Strip commit hash prefix
        $msg = $line -replace '^\w+\s+', ''

        if ($msg -match '^feat[\(:]') { $features += $msg }
        elseif ($msg -match '^fix[\(:]') { $fixes += $msg }
        elseif ($msg -match '^refactor[\(:]') { $refactors += $msg }
        elseif ($msg -match '^test[\(:]') { $tests += $msg }
        elseif ($msg -match '^chore[\(:]') { $chores += $msg }
        else { $other += $msg }
    }

    $sections = @()
    if ($features.Count -gt 0) {
        $sections += "### Features`n" + ($features | ForEach-Object { "- $_" } | Out-String).TrimEnd()
    }
    if ($fixes.Count -gt 0) {
        $sections += "### Bug Fixes`n" + ($fixes | ForEach-Object { "- $_" } | Out-String).TrimEnd()
    }
    if ($refactors.Count -gt 0) {
        $sections += "### Refactoring`n" + ($refactors | ForEach-Object { "- $_" } | Out-String).TrimEnd()
    }
    if ($tests.Count -gt 0) {
        $sections += "### Tests`n" + ($tests | ForEach-Object { "- $_" } | Out-String).TrimEnd()
    }
    if ($chores.Count -gt 0) {
        $sections += "### Chores`n" + ($chores | ForEach-Object { "- $_" } | Out-String).TrimEnd()
    }
    if ($other.Count -gt 0) {
        $sections += "### Other`n" + ($other | ForEach-Object { "- $_" } | Out-String).TrimEnd()
    }

    $notes = "## What's Changed ($FromTag → $Tag)`n`n" + ($sections -join "`n`n")
}

$releaseType = if ($PreRelease) { "pre-release" } else { "release" }

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Easydict WinUI Release" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Tag:  $Tag"
Write-Host "Type: $releaseType"
if ($FromTag) { Write-Host "From: $FromTag" }
Write-Host ""
Write-Host "--- Changelog Preview ---" -ForegroundColor DarkGray
Write-Host $notes
Write-Host "-------------------------" -ForegroundColor DarkGray
Write-Host ""

# Create tag
Write-Host "Creating tag '$Tag'..." -ForegroundColor Yellow
git tag $Tag
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Failed to create tag." -ForegroundColor Red
    exit 1
}

# Push tag to origin
Write-Host "Pushing tag to origin..." -ForegroundColor Yellow
git push origin $Tag
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Failed to push tag." -ForegroundColor Red
    exit 1
}

# Create GitHub release
Write-Host "Creating GitHub $releaseType..." -ForegroundColor Yellow
$ghArgs = @("release", "create", $Tag, "--title", $Tag, "--notes", $notes)
if ($PreRelease) { $ghArgs += "--prerelease" }
& gh @ghArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Failed to create release." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Release Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "Tag:     $Tag"
Write-Host "Release: $Tag ($releaseType)"

# Easydict WinUI Release Script
# Creates a git tag, pushes it to origin, and creates a GitHub pre-release using gh CLI.

param(
    [Parameter(Mandatory = $true)]
    [string]$Tag
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

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Easydict WinUI Release" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Tag: $Tag"
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

# Create GitHub pre-release
Write-Host "Creating GitHub pre-release..." -ForegroundColor Yellow
gh release create $Tag --title $Tag --notes $Tag --prerelease
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Failed to create release." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Release Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "Tag:     $Tag"
Write-Host "Release: $Tag (pre-release)"

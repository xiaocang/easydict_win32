#!/usr/bin/env pwsh
# Sign and install an existing Easydict MSIX/MSIX bundle

param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$CertPath = ".\certs\dev-signing.pfx",
    [string]$CertPassword = $(if ($env:CERT_PASSWORD) { $env:CERT_PASSWORD } else { "password" })
)

$ErrorActionPreference = "Stop"

# Resolve paths
$PackagePath = Resolve-Path $PackagePath
$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent $scriptDir
$CertPath = Join-Path $dotnetDir $CertPath

if (-not (Test-Path $PackagePath)) {
    Write-Host "Error: Package not found: $PackagePath" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $CertPath)) {
    Write-Host "Error: Certificate not found: $CertPath" -ForegroundColor Red
    exit 1
}

Write-Host "=== Easydict Sign and Install ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Package: $PackagePath"
Write-Host "Cert:    $CertPath"
Write-Host ""

# Step 1: Sign
Write-Host "[1/2] Signing package..." -ForegroundColor Yellow
winapp sign $PackagePath $CertPath --password $CertPassword --verbose
if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
Write-Host "Package signed successfully" -ForegroundColor Green
Write-Host ""

# Step 2: Reinstall
Write-Host "[2/2] Reinstalling app..." -ForegroundColor Yellow

# Remove existing installation
Write-Host "  - Removing existing installation..." -ForegroundColor Gray
$existingPackage = Get-AppxPackage -Name "*Easydict*" -ErrorAction SilentlyContinue
if ($existingPackage) {
    Remove-AppxPackage -Package $existingPackage.PackageFullName
    Write-Host "  - Removed: $($existingPackage.Name)" -ForegroundColor Gray
} else {
    Write-Host "  - No existing installation found" -ForegroundColor Gray
}

# Install new package
Write-Host "  - Installing new package..." -ForegroundColor Gray
Add-AppxPackage -Path $PackagePath
if ($LASTEXITCODE -ne 0) { throw "Installation failed" }
Write-Host "App installed successfully" -ForegroundColor Green
Write-Host ""

# Show completion info
Write-Host "=== Installation Complete ===" -ForegroundColor Cyan
Write-Host ""

$package = Get-AppxPackage -Name "xiaocang.EasydictforWindows"
if ($package) {
    Write-Host "Package Family Name: $($package.PackageFamilyName)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "To launch the app:" -ForegroundColor Yellow
    Write-Host "  1. Open Start Menu and search for 'Easydict'" -ForegroundColor Gray
    Write-Host "  2. Or press Win+R and run: shell:AppsFolder\$($package.PackageFamilyName)!App" -ForegroundColor Gray
    Write-Host ""
}

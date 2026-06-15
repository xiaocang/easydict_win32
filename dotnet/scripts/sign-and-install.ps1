#!/usr/bin/env pwsh
# Sign and install an existing Easydict MSIX/MSIX bundle

param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$CertPath = ".\certs\dev-signing.pfx",
    [string]$CertPassword = $(if ($env:CERT_PASSWORD) { $env:CERT_PASSWORD } else { "password" }),
    [string]$RuntimeProfile = ""
)

$ErrorActionPreference = "Stop"

function Get-ValidatorRuntimeProfile {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $normalized = $Value.Trim().ToLowerInvariant().Replace("_", "-")
    if ($normalized -eq "hybrid") {
        return "hybrid"
    }
    if ($normalized -eq "rust-only" -or $normalized -eq "rustonly") {
        return ""
    }

    throw "RuntimeProfile '$Value' is not supported. Use Hybrid for retained .NET payload validation, or omit it for the Rust-only validator default."
}

# Resolve paths
$PackagePath = (Resolve-Path $PackagePath).Path
$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent $scriptDir
$repoRoot = Split-Path -Parent $dotnetDir
$cargoManifest = Join-Path $repoRoot "rs\Cargo.toml"
if ([System.IO.Path]::IsPathRooted($CertPath)) {
    $CertPath = [System.IO.Path]::GetFullPath($CertPath)
} else {
    $CertPath = Join-Path $dotnetDir $CertPath
}

if (-not (Test-Path $PackagePath)) {
    Write-Host "Error: Package not found: $PackagePath" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $CertPath)) {
    Write-Host "Error: Certificate not found: $CertPath" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $cargoManifest)) {
    Write-Host "Error: Rust workspace manifest not found: $cargoManifest" -ForegroundColor Red
    exit 1
}

Write-Host "=== Easydict Sign and Install ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Package: $PackagePath"
Write-Host "Cert:    $CertPath"
Write-Host ""

# Step 1: Sign
Write-Host "[1/3] Signing package..." -ForegroundColor Yellow
winapp sign $PackagePath $CertPath --password $CertPassword --verbose
if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
Write-Host "Package signed successfully" -ForegroundColor Green
Write-Host ""

# Step 2: Validate payload before touching the installed app
Write-Host "[2/3] Validating package payload..." -ForegroundColor Yellow
$validatorArgs = @(
    "run",
    "--manifest-path",
    $cargoManifest,
    "-p",
    "easydict_msix_validate",
    "--",
    $PackagePath
)
$validatorRuntimeProfile = Get-ValidatorRuntimeProfile $RuntimeProfile
if ($validatorRuntimeProfile -eq "hybrid") {
    $validatorArgs += @("--runtime-profile", "hybrid")
}

& cargo @validatorArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Package payload validated successfully" -ForegroundColor Green
Write-Host ""

# Step 3: Reinstall
Write-Host "[3/3] Reinstalling app..." -ForegroundColor Yellow

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

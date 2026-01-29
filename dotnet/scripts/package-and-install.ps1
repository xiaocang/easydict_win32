#!/usr/bin/env pwsh
# Package, sign, reinstall, and run Easydict MSIX

param(
    [string]$Version = "0.2.0",
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$CertPath = ".\.certsmatching.pfx",
    [string]$CertPassword = "password"
)

$ErrorActionPreference = "Stop"

# Change to dotnet directory
$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent $scriptDir
Push-Location $dotnetDir

try {
    Write-Host "=== Easydict MSIX Package and Install Script ===" -ForegroundColor Cyan
    Write-Host ""

    # Step 1: Build
    Write-Host "[1/5] Building project..." -ForegroundColor Yellow
    dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj -c $Configuration -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    Write-Host "Build completed successfully" -ForegroundColor Green

    # Verify resources.pri was generated
    $priPath = "src/Easydict.WinUI/bin/$Platform/$Configuration/net8.0-windows10.0.19041.0/win-$Platform/resources.pri"
    if (Test-Path $priPath) {
        Write-Host "  ✓ resources.pri found (localization will work)" -ForegroundColor Green
    } else {
        Write-Host "  ⚠ resources.pri NOT found! Localization may not work in MSIX" -ForegroundColor Yellow
        Write-Host "    Expected path: $priPath" -ForegroundColor Gray
    }
    Write-Host ""

    # Step 2: Create output directory
    Write-Host "[2/5] Creating MSIX output directory..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path "msix" | Out-Null
    Write-Host "Directory ready" -ForegroundColor Green
    Write-Host ""

    # Step 3: Package
    Write-Host "[3/5] Packaging MSIX..." -ForegroundColor Yellow
    $binPath = "src/Easydict.WinUI/bin/$Platform/$Configuration/net8.0-windows10.0.19041.0/win-$Platform"
    $msixPath = ".\msix\Easydict-v$Version-$Platform.msix"
    $manifestPath = "src/Easydict.WinUI/Package.appxmanifest"

    winapp package $binPath --output $msixPath --manifest $manifestPath
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed" }
    Write-Host "Package created: $msixPath" -ForegroundColor Green
    Write-Host ""

    # Step 4: Sign
    Write-Host "[4/5] Signing MSIX..." -ForegroundColor Yellow
    winapp sign $msixPath $CertPath --password $CertPassword
    if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
    Write-Host "Package signed successfully" -ForegroundColor Green
    Write-Host ""

    # Step 5: Reinstall
    Write-Host "[5/5] Reinstalling app..." -ForegroundColor Yellow

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
    Add-AppxPackage -Path $msixPath
    if ($LASTEXITCODE -ne 0) { throw "Installation failed" }
    Write-Host "App installed successfully" -ForegroundColor Green
    Write-Host ""

    # Step 6: Show completion info
    Write-Host "=== Installation Complete ===" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Package location: $msixPath" -ForegroundColor White

    # Get the actual package family name
    $package = Get-AppxPackage -Name "xiaocang.EasydictforWindows"
    if ($package) {
        Write-Host "Package Family Name: $($package.PackageFamilyName)" -ForegroundColor Gray
    }

    Write-Host ""
    Write-Host "To launch the app:" -ForegroundColor Yellow
    Write-Host "  1. Open Start Menu and search for 'Easydict'" -ForegroundColor Gray
    Write-Host "  2. Or press Win+R and run: shell:AppsFolder\$($package.PackageFamilyName)!App" -ForegroundColor Gray
    Write-Host ""

} catch {
    Write-Host ""
    Write-Host "ERROR: $_" -ForegroundColor Red
    exit 1
} finally {
    Pop-Location
}

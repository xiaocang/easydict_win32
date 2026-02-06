#!/usr/bin/env pwsh
# Package, sign, reinstall, and run Easydict MSIX

param(
    [string]$Version = "0.3.2",
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$CertPath = ".\certs\dev-signing.pfx",
    [string]$CertPassword = $(if ($env:CERT_PASSWORD) { $env:CERT_PASSWORD } else { "password" })
)

$ErrorActionPreference = "Stop"

# Change to dotnet directory
$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent $scriptDir
Push-Location $dotnetDir

try {
    Write-Host "=== Easydict MSIX Package and Install Script ===" -ForegroundColor Cyan
    Write-Host ""

    # Step 1: Publish (self-contained)
    Write-Host "[1/5] Publishing project..." -ForegroundColor Yellow
    $publishDir = "./publish/$Platform"
    dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj `
        -c $Configuration `
        --runtime "win-$Platform" `
        --self-contained true `
        --output $publishDir `
        -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
    Write-Host "Publish completed successfully" -ForegroundColor Green

    # dotnet publish names the PRI file after the assembly (Easydict.WinUI.pri),
    # but MSIX packaged mode requires it to be named resources.pri.
    $assemblyPri = Join-Path $publishDir "Easydict.WinUI.pri"
    $resourcesPri = Join-Path $publishDir "resources.pri"
    if (Test-Path $assemblyPri) {
        Copy-Item $assemblyPri $resourcesPri -Force
        Write-Host "  resources.pri created from Easydict.WinUI.pri (localization will work)" -ForegroundColor Green
    } elseif (Test-Path $resourcesPri) {
        Write-Host "  resources.pri found (localization will work)" -ForegroundColor Green
    } else {
        Write-Host "  WARNING: No PRI file found! Localization will show keys instead of values" -ForegroundColor Yellow
    }
    Write-Host ""

    # Step 2: Create output directory
    Write-Host "[2/5] Creating MSIX output directory..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path "msix" | Out-Null
    Write-Host "Directory ready" -ForegroundColor Green
    Write-Host ""

    # Step 3: Package
    Write-Host "[3/5] Packaging MSIX..." -ForegroundColor Yellow
    $msixPath = ".\msix\Easydict-v$Version-$Platform.msix"
    $manifestPath = "src/Easydict.WinUI/Package.appxmanifest"

    # Create temp manifest with correct architecture and version
    $tempManifest = [System.IO.Path]::GetTempFileName()
    try {
        $manifestContent = Get-Content $manifestPath -Raw
        $manifestContent = $manifestContent -replace 'ProcessorArchitecture="[^"]*"', "ProcessorArchitecture=`"$Platform`""
        $msixVersion = "$Version.0"
        $manifestContent = $manifestContent -replace 'Version="[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+"', "Version=`"$msixVersion`""
        Set-Content $tempManifest $manifestContent

        winapp package $publishDir --output $msixPath --manifest $tempManifest --skip-pri --verbose
        if ($LASTEXITCODE -ne 0) { throw "Packaging failed" }
    } finally {
        Remove-Item $tempManifest -ErrorAction SilentlyContinue
    }
    Write-Host "Package created: $msixPath" -ForegroundColor Green
    Write-Host ""

    # Step 4: Sign
    Write-Host "[4/5] Signing MSIX..." -ForegroundColor Yellow
    winapp sign $msixPath $CertPath --password $CertPassword --verbose
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

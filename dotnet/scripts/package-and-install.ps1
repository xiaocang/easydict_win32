#!/usr/bin/env pwsh
# Package, sign, reinstall, and run Easydict MSIX

param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [ValidateSet("Hybrid", "RustOnly")]
    [string]$RuntimeProfile = "Hybrid",
    [string]$CertPath = ".\certs\dev-signing.pfx",
    [string]$CertPassword = $(if ($env:CERT_PASSWORD) { $env:CERT_PASSWORD } else { "password" }),
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"

# Change to dotnet directory
$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent $scriptDir
Push-Location $dotnetDir

try {
    Write-Host "=== Easydict MSIX Package and Install Script ===" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Runtime profile: $RuntimeProfile" -ForegroundColor Gray
    $isRustOnlyRuntime = $RuntimeProfile -eq "RustOnly"
    $validatorRuntimeProfile = if ($isRustOnlyRuntime) { "rust-only" } else { "hybrid" }

    # Auto-detect version from csproj if not provided
    if (-not $Version) {
        $csprojPath = "src/Easydict.WinUI/Easydict.WinUI.csproj"
        $versionLines = & dotnet msbuild $csprojPath -nologo -getProperty:Version -p:Configuration=$Configuration
        if ($LASTEXITCODE -ne 0) { throw "Could not extract version from csproj. Please specify -Version." }
        $versionValue = $versionLines | Where-Object { $_ -and $_.Trim() } | Select-Object -Last 1
        $Version = if ($versionValue) { $versionValue.Trim() } else { "" }
        if (-not $Version) {
            throw "Could not extract version from csproj. Please specify -Version."
        }
        Write-Host "Auto-detected version: $Version" -ForegroundColor Gray
    }

    # MSIX requires 4-part version (X.Y.Z.0)
    $msixVersion = "$Version.0"

    # Step 1: Publish (self-contained, MSIX mode — without bundled Windows App SDK)
    Write-Host "[1/6] Publishing project..." -ForegroundColor Yellow
    $publishDir = "./publish-msix/$Platform"
    dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj `
        -c $Configuration `
        --runtime "win-$Platform" `
        --self-contained true `
        --output $publishDir `
        -p:Version=$Version `
        -p:Platform=$Platform `
        -p:BuildWorkerOutputs=false `
        -p:EnableInProcLongDocFallback=false `
        -p:RuntimeProfile=$RuntimeProfile `
        -p:PublishTrimmed=false `
        -p:PublishReadyToRun=false `
        -p:WindowsAppSDKSelfContained=false
    if ($LASTEXITCODE -ne 0) { throw "WinUI publish failed" }

    # Build Rust-owned helper executables and copy them beside the app.
    # Rust desktop actions resolve these names from the package/app directory.
    Write-Host "  Publishing Rust helper executables..." -ForegroundColor Gray
    & "$scriptDir/Build-RustHelpers.ps1" `
        -Platform $Platform `
        -Configuration $Configuration `
        -OutputDir $publishDir

    if ($isRustOnlyRuntime) {
        Write-Host "  Skipping retained .NET workers and bundled worker runtime for RustOnly profile." -ForegroundColor Yellow
    } elseif ($Platform -ne "x86") {
        Write-Host "  Publishing remaining .NET workers..." -ForegroundColor Gray
        dotnet publish src/Easydict.Workers.LongDoc/Easydict.Workers.LongDoc.csproj `
            -c $Configuration `
            --runtime "win-$Platform" `
            --no-self-contained `
            --output "$publishDir/workers/longdoc" `
            -p:Platform=$Platform `
            -p:PublishTrimmed=false `
            -p:WindowsAppSDKSelfContained=false
        if ($LASTEXITCODE -ne 0) { throw "LongDoc worker publish failed" }

        dotnet publish src/Easydict.Workers.LocalAi/Easydict.Workers.LocalAi.csproj `
            -c $Configuration `
            --runtime "win-$Platform" `
            --no-self-contained `
            --output "$publishDir/workers/localai" `
            -p:Platform=$Platform `
            -p:PublishTrimmed=false `
            -p:WindowsAppSDKSelfContained=false
        if ($LASTEXITCODE -ne 0) { throw "LocalAI worker publish failed" }

        & "$scriptDir/Dedupe-WorkerSharedFiles.ps1" -PublishDir $publishDir
        & "$scriptDir/Extract-DotnetRuntime.ps1" `
            -Rid "win-$Platform" `
            -OutputDir "$publishDir/dotnet"
    } else {
        Write-Host "  Skipping .NET workers for x86; worker projects do not support win-x86." -ForegroundColor Yellow
    }

    Write-Host "Publish completed successfully" -ForegroundColor Green
    Write-Host ""

    # Step 2: Create output directory
    Write-Host "[2/6] Creating MSIX output directory..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path "msix" | Out-Null
    Write-Host "Directory ready" -ForegroundColor Green
    Write-Host ""

    # Step 3: Package
    Write-Host "[3/6] Packaging MSIX..." -ForegroundColor Yellow
    $msixPath = ".\msix\Easydict-v$Version-$Platform.msix"
    $manifestPath = "src/Easydict.WinUI/Package.appxmanifest"
    & "$scriptDir/Package-Msix.ps1" `
        -Platform $Platform `
        -PublishDir $publishDir `
        -ManifestPath $manifestPath `
        -OutputMsixPath $msixPath `
        -MsixVersion $msixVersion `
        -VerifyTargetsizeIcons
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed" }
    Write-Host "Package created: $msixPath" -ForegroundColor Green
    Write-Host ""

    # Step 4: Validate package payload before signing/installing
    Write-Host "[4/6] Validating MSIX..." -ForegroundColor Yellow
    cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- `
        $msixPath `
        --runtime-profile $validatorRuntimeProfile `
        --allow-unsigned
    if ($LASTEXITCODE -ne 0) { throw "MSIX validation failed" }
    Write-Host "Package validation succeeded" -ForegroundColor Green
    Write-Host ""

    # Step 5: Sign
    Write-Host "[5/6] Signing MSIX..." -ForegroundColor Yellow
    winapp sign $msixPath $CertPath --password $CertPassword --verbose
    if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
    Write-Host "Package signed successfully" -ForegroundColor Green
    Write-Host ""

    # Step 6: Reinstall
    if ($SkipInstall) {
        Write-Host "[6/6] Skipping local install (-SkipInstall)" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "=== Build Complete ===" -ForegroundColor Cyan
        Write-Host "Signed MSIX: $msixPath" -ForegroundColor White
        return
    }
    Write-Host "[6/6] Reinstalling app..." -ForegroundColor Yellow

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

    # Show completion info
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

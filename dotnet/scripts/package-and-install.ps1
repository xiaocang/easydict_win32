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
        [xml]$csproj = Get-Content $csprojPath -Raw
        $Version = $csproj.Project.PropertyGroup[0].Version
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
    Write-Host "[2/6] Creating MSIX output directory..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path "msix" | Out-Null
    Write-Host "Directory ready" -ForegroundColor Green
    Write-Host ""

    # Step 3: Package
    Write-Host "[3/6] Packaging MSIX..." -ForegroundColor Yellow
    $msixPath = ".\msix\Easydict-v$Version-$Platform.msix"
    $manifestPath = "src/Easydict.WinUI/Package.appxmanifest"

    # Create temp manifest with correct architecture and version
    $tempManifest = [System.IO.Path]::GetTempFileName()
    try {
        $manifestContent = Get-Content $manifestPath -Raw
        $manifestContent = $manifestContent -replace 'ProcessorArchitecture="[^"]*"', "ProcessorArchitecture=`"$Platform`""
        # Anchor the rewrite to the <Identity> element so unrelated Version-bearing
        # attributes elsewhere in the manifest aren't clobbered. A previous
        # `(?<!Min)Version="..."` only excluded `MinVersion`, but still matched
        # `MaxVersionTested="..."` (the lookbehind sees `x`, not `Min`) — which
        # would rewrite the Windows AI capability gate to the app version.
        $manifestContent = $manifestContent -replace '(<Identity\b[^>]*?\sVersion=")[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+(")', "`${1}$msixVersion`${2}"
        Set-Content $tempManifest $manifestContent

        winapp package $publishDir --output $msixPath --manifest $tempManifest --skip-pri --verbose
        if ($LASTEXITCODE -ne 0) { throw "Packaging failed" }
    } finally {
        Remove-Item $tempManifest -ErrorAction SilentlyContinue
    }
    Write-Host "Package created: $msixPath" -ForegroundColor Green
    Write-Host ""

    # Step 4: Fix MinVersion inside MSIX (WindowsAppSDK #5598 workaround)
    Write-Host "[4/6] Verifying MSIX manifest MinVersion..." -ForegroundColor Yellow
    & "$scriptDir/Fix-MsixMinVersion.ps1" -MsixPath $msixPath
    Write-Host ""

    # Step 5: Validate package payload before signing/installing
    Write-Host "[5/7] Validating MSIX..." -ForegroundColor Yellow
    cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- `
        $msixPath `
        --runtime-profile $validatorRuntimeProfile `
        --allow-unsigned
    if ($LASTEXITCODE -ne 0) { throw "MSIX validation failed" }
    Write-Host "Package validation succeeded" -ForegroundColor Green
    Write-Host ""

    # Step 6: Sign
    Write-Host "[6/7] Signing MSIX..." -ForegroundColor Yellow
    winapp sign $msixPath $CertPath --password $CertPassword --verbose
    if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
    Write-Host "Package signed successfully" -ForegroundColor Green
    Write-Host ""

    # Step 7: Reinstall
    if ($SkipInstall) {
        Write-Host "[7/7] Skipping local install (-SkipInstall)" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "=== Build Complete ===" -ForegroundColor Cyan
        Write-Host "Signed MSIX: $msixPath" -ForegroundColor White
        return
    }
    Write-Host "[7/7] Reinstalling app..." -ForegroundColor Yellow

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

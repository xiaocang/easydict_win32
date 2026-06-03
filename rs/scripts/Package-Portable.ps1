#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Builds the first Rust-only portable Easydict package.

.DESCRIPTION
  The Rust package is intentionally portable-only for the first release and is
  named separately from the .NET package so both versions can coexist.
#>

param(
    [ValidateSet("x64", "x86", "arm64")]
    [string]$Platform = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputRoot,

    [string]$PackageVersion,

    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$rsRoot = Split-Path -Parent $scriptDir
$cargoManifest = Join-Path $rsRoot "Cargo.toml"

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $rsRoot "dist"
}

$cargoTarget = switch ($Platform) {
    "x64" { "x86_64-pc-windows-msvc" }
    "x86" { "i686-pc-windows-msvc" }
    "arm64" { "aarch64-pc-windows-msvc" }
}

$profileDir = if ($Configuration -eq "Release") { "release" } else { "debug" }
$packageName = if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    "easydict-rs-portable-win-$Platform"
} else {
    "easydict-rs-portable-$PackageVersion-win-$Platform"
}
$packageDir = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot "$packageName.zip"

if (-not (Test-Path $cargoManifest)) {
    throw "Rust workspace not found at $rsRoot"
}

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "cargo was not found; install the Rust toolchain before packaging"
}

if (Get-Command rustup -ErrorAction SilentlyContinue) {
    Write-Host "Ensuring Rust target $cargoTarget is installed..." -ForegroundColor Gray
    & rustup target add $cargoTarget
    if ($LASTEXITCODE -ne 0) {
        throw "rustup target add $cargoTarget failed"
    }
}

if (Test-Path $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

$profileArgs = @()
if ($Configuration -eq "Release") {
    $profileArgs += "--release"
}

Push-Location $rsRoot
try {
    Write-Host "Building Rust preview app for $Platform ($cargoTarget)..." -ForegroundColor Green
    & cargo build -p easydict_preview_iced --target $cargoTarget @profileArgs
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build failed for easydict_preview_iced"
    }

    Write-Host "Building Rust helper executables for $Platform ($cargoTarget)..." -ForegroundColor Green
    & cargo build -p easydict_app --target $cargoTarget `
        --bin easydict-native-bridge `
        --bin easydict_browser_registrar `
        --bin easydict_cli `
        --bin easydict_long_doc `
        @profileArgs
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build failed for Rust helper executables"
    }
} finally {
    Pop-Location
}

$builtDir = Join-Path $rsRoot "target\$cargoTarget\$profileDir"
$previewExe = Join-Path $builtDir "easydict_preview_iced.exe"
if (-not (Test-Path $previewExe)) {
    throw "Rust preview executable was not produced: $previewExe"
}

Copy-Item -LiteralPath $previewExe -Destination (Join-Path $packageDir "Easydict.Rust.exe") -Force

$helperExecutables = @(
    "easydict-native-bridge.exe",
    "easydict_browser_registrar.exe",
    "easydict_cli.exe",
    "easydict_long_doc.exe"
)

foreach ($exeName in $helperExecutables) {
    $source = Join-Path $builtDir $exeName
    if (-not (Test-Path $source)) {
        throw "Rust helper executable was not produced: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageDir $exeName) -Force
}

Copy-Item `
    -LiteralPath (Join-Path $builtDir "easydict_browser_registrar.exe") `
    -Destination (Join-Path $packageDir "BrowserHostRegistrar.exe") `
    -Force

$readme = @"
Easydict Rust portable preview
==============================

Entry point: Easydict.Rust.exe

This first Rust package is portable-only and intentionally named separately from
the .NET package so both versions can coexist on the same machine.

This package does not include MSIX metadata, an installer, retained .NET workers,
or a bundled .NET runtime.
"@
Set-Content -Path (Join-Path $packageDir "README-portable.txt") -Value $readme -Encoding UTF8

if (-not $NoZip) {
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    & cargo run --manifest-path $cargoManifest -p easydict_packager -- `
        zip-directory `
        --source $packageDir `
        --destination $zipPath
    if ($LASTEXITCODE -ne 0) {
        throw "Rust portable ZIP creation failed"
    }
    Write-Host "Created Rust portable ZIP: $zipPath" -ForegroundColor Green
}

$files = Get-ChildItem -Path $packageDir -Recurse -File
$totalSize = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host "Rust portable package: $packageDir" -ForegroundColor Green
Write-Host "Files: $($files.Count)"
Write-Host "Size:  $([math]::Round($totalSize / 1MB, 2)) MB"

#!/usr/bin/env pwsh

param(
    [ValidateSet("x64", "x86", "arm64")]
    [string]$Platform = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $PSCommandPath
$DotnetDir = Split-Path -Parent $ScriptDir
$RepoRoot = Split-Path -Parent $DotnetDir
$RustWorkspace = Join-Path $RepoRoot "rs"

if (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDir))
} else {
    $OutputPath = [System.IO.Path]::GetFullPath($OutputDir)
}

$CargoTarget = switch ($Platform) {
    "x64" { "x86_64-pc-windows-msvc" }
    "x86" { "i686-pc-windows-msvc" }
    "arm64" { "aarch64-pc-windows-msvc" }
}

$ProfileDir = if ($Configuration -eq "Release") { "release" } else { "debug" }

if (-not (Test-Path (Join-Path $RustWorkspace "Cargo.toml"))) {
    throw "Rust workspace not found at $RustWorkspace"
}

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "cargo was not found; install the Rust toolchain before packaging Rust helpers"
}

if (Get-Command rustup -ErrorAction SilentlyContinue) {
    Write-Host "Ensuring Rust target $CargoTarget is installed..." -ForegroundColor Gray
    & rustup target add $CargoTarget
    if ($LASTEXITCODE -ne 0) {
        throw "rustup target add $CargoTarget failed"
    }
}

New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

$CargoArgs = @(
    "build",
    "-p", "easydict_app",
    "--target", $CargoTarget,
    "--bin", "easydict-native-bridge",
    "--bin", "easydict_browser_registrar",
    "--bin", "easydict_cli",
    "--bin", "easydict_long_doc"
)

if ($Configuration -eq "Release") {
    $CargoArgs += "--release"
}

Write-Host "Building Rust helper executables for $Platform ($CargoTarget)..." -ForegroundColor Green
Push-Location $RustWorkspace
try {
    & cargo @CargoArgs
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build failed for Rust helper executables"
    }
} finally {
    Pop-Location
}

$BuiltDir = Join-Path $RustWorkspace "target\$CargoTarget\$ProfileDir"
$HelperExecutables = @(
    "easydict-native-bridge.exe",
    "easydict_browser_registrar.exe",
    "easydict_cli.exe",
    "easydict_long_doc.exe"
)

foreach ($ExeName in $HelperExecutables) {
    $Source = Join-Path $BuiltDir $ExeName
    if (-not (Test-Path $Source)) {
        throw "Rust helper executable was not produced: $Source"
    }

    Copy-Item $Source -Destination (Join-Path $OutputPath $ExeName) -Force
    Write-Host "Copied $ExeName to $OutputPath" -ForegroundColor Green
}

$RegistrarSource = Join-Path $BuiltDir "easydict_browser_registrar.exe"
$LegacyRegistrarName = "BrowserHostRegistrar.exe"
Copy-Item $RegistrarSource -Destination (Join-Path $OutputPath $LegacyRegistrarName) -Force
Write-Host "Copied $LegacyRegistrarName to $OutputPath" -ForegroundColor Green

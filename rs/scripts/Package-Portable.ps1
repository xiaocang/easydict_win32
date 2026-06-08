#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Builds the first Rust-only portable Easydict package.

.DESCRIPTION
  The Rust package is intentionally portable-only for the first release and is
  named separately from the .NET package so both versions can coexist. The
  staging, ZIP creation, and retained .NET payload validation are owned by the
  Rust easydict_packager crate; this script is only a compatibility shim.
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

if (-not (Test-Path $cargoManifest)) {
    throw "Rust workspace not found at $rsRoot"
}

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "cargo was not found; install the Rust toolchain before packaging"
}

$arguments = @(
    "run",
    "--manifest-path", $cargoManifest,
    "-p", "easydict_packager",
    "--",
    "pack-rs-portable",
    "--workspace", $rsRoot,
    "--platform", $Platform,
    "--configuration", $Configuration,
    "--output-root", $OutputRoot
)

if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
    $arguments += @("--package-version", $PackageVersion)
}

if ($NoZip) {
    $arguments += "--no-zip"
}

$previousRuntimeProfile = $env:EASYDICT_RUNTIME_PROFILE
$previousGenericRuntimeProfile = $env:RUNTIME_PROFILE
$env:EASYDICT_RUNTIME_PROFILE = "rust-only"
$env:RUNTIME_PROFILE = "rust-only"
try {
    & cargo @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Rust portable package creation failed"
    }
}
finally {
    if ($null -eq $previousRuntimeProfile) {
        Remove-Item Env:EASYDICT_RUNTIME_PROFILE -ErrorAction SilentlyContinue
    }
    else {
        $env:EASYDICT_RUNTIME_PROFILE = $previousRuntimeProfile
    }

    if ($null -eq $previousGenericRuntimeProfile) {
        Remove-Item Env:RUNTIME_PROFILE -ErrorAction SilentlyContinue
    }
    else {
        $env:RUNTIME_PROFILE = $previousGenericRuntimeProfile
    }
}

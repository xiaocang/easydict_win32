#!/usr/bin/env pwsh

param(
    [ValidateSet("x64", "x86", "arm64")]
    [string]$Platform = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [switch]$IncludeLegacyRegistrarAlias
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent $scriptDir
$repoRoot = Split-Path -Parent $dotnetDir
$rustWorkspace = Join-Path $repoRoot "rs"
$cargoManifest = Join-Path $rustWorkspace "Cargo.toml"

$arguments = @(
    "run",
    "--manifest-path",
    $cargoManifest,
    "-p",
    "easydict_packager",
    "--",
    "build-rust-helpers",
    "--workspace",
    $rustWorkspace,
    "--platform",
    $Platform,
    "--configuration",
    $Configuration,
    "--output-dir",
    $OutputDir
)

if ($IncludeLegacyRegistrarAlias) {
    $arguments += "--include-legacy-registrar-alias"
}

$previousRuntimeProfile = $env:EASYDICT_RUNTIME_PROFILE
$previousGenericRuntimeProfile = $env:RUNTIME_PROFILE
$env:EASYDICT_RUNTIME_PROFILE = "rust-only"
$env:RUNTIME_PROFILE = "rust-only"
$exitCode = 0
try {
    & cargo @arguments
    $exitCode = $LASTEXITCODE
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

if ($exitCode -ne 0) {
    exit $exitCode
}

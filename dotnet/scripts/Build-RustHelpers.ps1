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

& cargo @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

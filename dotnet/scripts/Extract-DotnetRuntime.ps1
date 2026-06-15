#!/usr/bin/env pwsh

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$Version = "8.0.11",

    [string]$RuntimeProfile = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$cargoManifest = Join-Path $repoRoot "rs\Cargo.toml"

if ([string]::IsNullOrWhiteSpace($RuntimeProfile)) {
    throw "Extract-DotnetRuntime.ps1 requires -RuntimeProfile Hybrid for retained-worker packaging; rs packages must not bundle .NET runtime"
}

$validRuntimeProfiles = @("Hybrid", "hybrid")
if ($validRuntimeProfiles -notcontains $RuntimeProfile) {
    throw "Extract-DotnetRuntime.ps1 only supports -RuntimeProfile Hybrid for retained-worker packaging; Rust-only rs packages must use rs\scripts\Package-Portable.ps1 and must not bundle .NET runtime"
}

$arguments = @(
    "run",
    "--manifest-path",
    $cargoManifest,
    "-p",
    "easydict_packager",
    "--features",
    "hybrid-dotnet-runtime-packaging",
    "--",
    "extract-dotnet-runtime",
    "--rid",
    $Rid,
    "--output-dir",
    $OutputDir,
    "--version",
    $Version,
    "--runtime-profile",
    $RuntimeProfile
)

& cargo @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

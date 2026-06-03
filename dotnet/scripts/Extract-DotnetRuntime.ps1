#!/usr/bin/env pwsh

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$Version = "8.0.11"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$cargoManifest = Join-Path $repoRoot "rs\Cargo.toml"

$arguments = @(
    "run",
    "--manifest-path",
    $cargoManifest,
    "-p",
    "easydict_packager",
    "--",
    "extract-dotnet-runtime",
    "--rid",
    $Rid,
    "--output-dir",
    $OutputDir,
    "--version",
    $Version
)

& cargo @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

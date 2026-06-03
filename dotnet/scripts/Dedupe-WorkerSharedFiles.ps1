#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Compatibility shim for Rust worker shared-file dedupe.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent $scriptDir
$repoRoot = Split-Path -Parent $dotnetDir
$cargoManifest = Join-Path $repoRoot "rs/Cargo.toml"

if (-not (Test-Path $cargoManifest)) {
    Write-Error "Rust workspace manifest not found: $cargoManifest"
    exit 1
}

cargo run --manifest-path $cargoManifest -p easydict_msix_validate -- `
    dedupe-worker-shared `
    $PublishDir

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

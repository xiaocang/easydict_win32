#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Compatibility shim for Rust worker shared-file dedupe.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [string]$RuntimeProfile = ""
)

$ErrorActionPreference = "Stop"

function Test-RustOnlyRuntimeProfile {
    param([string]$Value)
    $normalized = $Value.Trim().ToLowerInvariant().Replace("_", "-")
    return $normalized -eq "rust-only" -or $normalized -eq "rustonly"
}

function Test-HybridRuntimeProfile {
    param([string]$Value)
    return $Value.Trim().ToLowerInvariant() -eq "hybrid"
}

if ([string]::IsNullOrWhiteSpace($RuntimeProfile)) {
    throw "RuntimeProfile must be explicitly set to Hybrid for Dedupe-WorkerSharedFiles.ps1. Retained worker shared-file dedupe is legacy/hybrid packaging only; the first rs release is portable-only."
}
if (Test-RustOnlyRuntimeProfile $RuntimeProfile) {
    throw "RuntimeProfile '$RuntimeProfile' is not supported by Dedupe-WorkerSharedFiles.ps1. Retained worker shared-file dedupe is legacy/hybrid packaging only; the first rs release is portable-only."
}
if (-not (Test-HybridRuntimeProfile $RuntimeProfile)) {
    throw "RuntimeProfile '$RuntimeProfile' is not supported by Dedupe-WorkerSharedFiles.ps1. Only Hybrid is supported for retained worker shared-file dedupe."
}

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
    $PublishDir `
    --runtime-profile hybrid

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

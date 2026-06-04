<#
.SYNOPSIS
    Synchronize store listing metadata through the Rust store-listings CLI.

.DESCRIPTION
    This shim preserves the existing PowerShell entry point while delegating YAML
    parsing, validation, preview, payload generation, and GitHub summary support
    to rs/crates/easydict_store_listings. Submit mode still uses the external
    Microsoft Store Developer CLI (msstore) for Partner Center API calls.
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('validate', 'preview', 'submit')]
    [string]$Mode,

    [Parameter(Mandatory = $false)]
    [string]$Languages = ""
)

$ErrorActionPreference = 'Stop'

$winStorePath = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $winStorePath
$cargoManifest = Join-Path $repoRoot "rs\Cargo.toml"

$arguments = @(
    "run",
    "--manifest-path", $cargoManifest,
    "-p", "easydict_store_listings",
    "--",
    $Mode,
    "--winstore-root", $winStorePath
)

if (-not [string]::IsNullOrWhiteSpace($Languages)) {
    $arguments += @("--languages", $Languages)
}

& cargo @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

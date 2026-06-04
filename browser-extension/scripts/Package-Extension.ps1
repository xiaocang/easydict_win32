<#
.SYNOPSIS
    Packages the Easydict OCR browser extension through the Rust packager.

.DESCRIPTION
    This shim preserves the existing PowerShell entry point while delegating
    manifest JSON editing and ZIP/XPI creation to rs/crates/easydict_packager.
#>
param(
    [ValidateSet("Chrome", "Firefox", "All")]
    [string]$Target = "All",

    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$extensionDir = Split-Path -Parent $scriptDir
$repoRoot = Split-Path -Parent $extensionDir
$cargoManifest = Join-Path $repoRoot "rs\Cargo.toml"

$arguments = @(
    "run",
    "--manifest-path", $cargoManifest,
    "-p", "easydict_packager",
    "--",
    "package-browser-extension",
    "--extension-dir", $extensionDir,
    "--target", $Target
)

if (-not [string]::IsNullOrWhiteSpace($OutputDir)) {
    $arguments += @("--output-dir", $OutputDir)
}

& cargo @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

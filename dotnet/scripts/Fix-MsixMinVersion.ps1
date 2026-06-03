<#
.SYNOPSIS
    Verifies and fixes the TargetDeviceFamily MinVersion inside an MSIX package.

.DESCRIPTION
    Compatibility shim for the Rust implementation in easydict_msix_validate.
    The winapp CLI can emit an incorrect TargetDeviceFamily MinVersion; the
    Rust fixer extracts the MSIX, updates Dependencies/TargetDeviceFamily only
    when the bundled MinVersion is below the requirement, and re-packs via
    MakeAppx.exe without signing.

.PARAMETER MsixPath
    Path to the MSIX file to verify/fix.

.PARAMETER MinVersion
    Required minimum version. Defaults to 10.0.19041.0.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsixPath,

    [Parameter(Mandatory = $false)]
    [string]$MinVersion = "10.0.19041.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $MsixPath)) {
    Write-Error "MSIX file not found: $MsixPath"
    exit 1
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
    fix-minversion `
    $MsixPath `
    --min-version $MinVersion

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

<#
.SYNOPSIS
    Converts macOS service icon .imageset folders to Windows multi-scale PNGs.

.DESCRIPTION
    Compatibility shim for the Rust implementation in easydict_icon_generator.
    Keeps the historical script entry point while using Rust image processing.

.PARAMETER SourceDir
    Path to macOS service-icon directory. Relative paths are resolved from this
    script directory to preserve the old script behavior.

.PARAMETER OutputDir
    Output directory for Windows service icons. Relative paths are resolved from
    this script directory to preserve the old script behavior.
#>
[CmdletBinding()]
param(
    [string]$SourceDir = "../../../Easydict/App/Assets.xcassets/service-icon",
    [string]$OutputDir = "../src/Easydict.WinUI/Assets/ServiceIcons"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent $scriptDir
$repoRoot = Split-Path -Parent $dotnetDir
$cargoManifest = Join-Path $repoRoot "rs/Cargo.toml"

function Resolve-ScriptPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return (Join-Path $scriptDir $Path)
}

if (-not (Test-Path $cargoManifest)) {
    Write-Error "Rust workspace manifest not found: $cargoManifest"
    exit 1
}

$sourceFull = Resolve-ScriptPath $SourceDir
$outputFull = Resolve-ScriptPath $OutputDir

& cargo run --manifest-path $cargoManifest -p easydict_icon_generator -- `
    service-icons `
    --source-dir $sourceFull `
    --output-dir $outputFull

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

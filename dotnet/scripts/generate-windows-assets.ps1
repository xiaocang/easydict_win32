<#
.SYNOPSIS
    Generates multi-scale Windows assets from high-resolution icon PNGs.

.DESCRIPTION
    Compatibility shim for the Rust implementation in easydict_icon_generator.
    Keeps the historical script entry point while using Rust image processing.
#>
[CmdletBinding()]
param(
    [string]$SourceIcon = "screenshot/icon_512x512@2x.png",
    [string]$UnplatedIcon = "dotnet/src/Easydict.WinUI/Assets/icon_unplated_1024.png",
    [string]$OutputDir = "dotnet/src/Easydict.WinUI/Assets"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent $scriptDir
$repoRoot = Split-Path -Parent $dotnetDir
$cargoManifest = Join-Path $repoRoot "rs/Cargo.toml"

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return (Join-Path $repoRoot $Path)
}

if (-not (Test-Path $cargoManifest)) {
    Write-Error "Rust workspace manifest not found: $cargoManifest"
    exit 1
}

$sourceFull = Resolve-RepoPath $SourceIcon
$unplatedFull = Resolve-RepoPath $UnplatedIcon
$outputFull = Resolve-RepoPath $OutputDir

& cargo run --manifest-path $cargoManifest -p easydict_icon_generator -- `
    windows-assets `
    --source-icon $sourceFull `
    --unplated-icon $unplatedFull `
    --output-dir $outputFull

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

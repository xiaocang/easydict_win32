<#
.SYNOPSIS
    Refreshes existing WinUI PNG assets from the macOS source icon.

.DESCRIPTION
    Compatibility shim for the Rust implementation in easydict_icon_generator.
    Keeps existing asset dimensions and filenames while using Rust image processing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$SourceIcon = "src\Easydict.WinUI\Assets\macos\white-black-icon.appiconset\icon_512x512@2x.png",

    [Parameter(Mandatory = $false)]
    [string]$AssetsDir = "src\Easydict.WinUI\Assets"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetRoot = Split-Path -Parent $scriptDir
$repoRoot = Split-Path -Parent $dotnetRoot
$cargoManifest = Join-Path $repoRoot "rs/Cargo.toml"

function Resolve-DotnetPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return (Join-Path $dotnetRoot $Path)
}

if (-not (Test-Path $cargoManifest)) {
    Write-Error "Rust workspace manifest not found: $cargoManifest"
    exit 1
}

$sourceFull = Resolve-DotnetPath $SourceIcon
$assetsFull = Resolve-DotnetPath $AssetsDir

& cargo run --manifest-path $cargoManifest -p easydict_icon_generator -- `
    refresh-assets-from-macos-icon `
    --source-icon $sourceFull `
    --assets-dir $assetsFull

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

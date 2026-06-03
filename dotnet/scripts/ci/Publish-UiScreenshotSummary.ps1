<#
.SYNOPSIS
    Publishes a GitHub Actions summary for UI automation screenshots.

.DESCRIPTION
    Compatibility shim for the Rust implementation in easydict_ui_parity_analyzer.
    Keeps the historical workflow entry point while Rust reads screenshot
    dimensions and generates the inline gallery.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ScreenshotRoot,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactName,

    [string]$Title = "UI automation screenshots",

    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY,

    [int]$MaxInlineBytes = 600000,

    [int]$MaxListedScreenshots = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent (Split-Path -Parent $scriptDir)
$repoRoot = Split-Path -Parent $dotnetDir
$cargoManifest = Join-Path $repoRoot "rs/Cargo.toml"

if (-not (Test-Path $cargoManifest)) {
    Write-Error "Rust workspace manifest not found: $cargoManifest"
    exit 1
}

$arguments = @(
    "run",
    "--manifest-path", $cargoManifest,
    "-p", "easydict_ui_parity_analyzer",
    "--",
    "screenshot-summary",
    "--screenshot-root", $ScreenshotRoot,
    "--artifact-name", $ArtifactName,
    "--title", $Title,
    "--max-inline-bytes", $MaxInlineBytes.ToString(),
    "--max-listed-screenshots", $MaxListedScreenshots.ToString()
)

if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
    $arguments += @("--summary-path", $SummaryPath)
}

& cargo @arguments

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

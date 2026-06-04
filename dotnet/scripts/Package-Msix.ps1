#!/usr/bin/env pwsh
<#!
.SYNOPSIS
  Shared MSIX packaging helper used by CI workflows.

.DESCRIPTION
  Reuses the Release workflow packaging logic:
  - prepare package inputs through the Rust MSIX helper
  - run winapp package
  - fix MinVersion via Fix-MsixMinVersion.ps1
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("x64", "x86", "arm64")]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputMsixPath,

    [string]$MsixVersion = "",

    [switch]$VerifyTargetsizeIcons
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PublishDir)) {
    throw "PublishDir not found: $PublishDir"
}
if (-not (Test-Path $ManifestPath)) {
    throw "Manifest not found: $ManifestPath"
}

Write-Host "[MSIX] PublishDir: $PublishDir"
Write-Host "[MSIX] Platform: $Platform"
Write-Host "[MSIX] Output: $OutputMsixPath"

$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } elseif ($env:TEMP) { $env:TEMP } else { "." }
$tempManifest = Join-Path $tempRoot "Package.$Platform.appxmanifest"
$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$cargoManifest = Join-Path $repoRoot "rs\Cargo.toml"
$prepareArgs = @(
    "run",
    "--manifest-path",
    $cargoManifest,
    "-p",
    "easydict_msix_validate",
    "--",
    "prepare-package-inputs",
    "--platform",
    $Platform,
    "--publish-dir",
    $PublishDir,
    "--manifest",
    $ManifestPath,
    "--output-manifest",
    $tempManifest
)

if ($MsixVersion) {
    $prepareArgs += @("--msix-version", $MsixVersion)
}
if ($VerifyTargetsizeIcons) {
    $prepareArgs += "--verify-targetsize-icons"
}

& cargo @prepareArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

# Package
$outputDir = Split-Path -Parent $OutputMsixPath
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

winapp package $PublishDir --output $OutputMsixPath --manifest $tempManifest --skip-pri --verbose

# MinVersion fix
& (Join-Path $scriptDir "Fix-MsixMinVersion.ps1") -MsixPath $OutputMsixPath

Write-Host "[MSIX] Packaging finished: $OutputMsixPath"

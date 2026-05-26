#!/usr/bin/env pwsh
<#!
.SYNOPSIS
  Shared MSIX packaging helper used by CI workflows.

.DESCRIPTION
  Reuses the Release workflow packaging logic:
  - verify required MSIX assets and targetsize icons
  - normalize resources.pri from Easydict.WinUI.pri
  - patch manifest architecture/version in a temp manifest
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

# Verify required assets
$requiredAssets = @(
    "Assets/SplashScreen.scale-100.png",
    "Assets/LockScreenLogo.scale-100.png",
    "Assets/Square150x150Logo.scale-100.png",
    "Assets/Square44x44Logo.scale-100.png",
    "Assets/Wide310x150Logo.scale-100.png",
    "Assets/StoreLogo.png"
)

$missing = @()
foreach ($asset in $requiredAssets) {
    $path = Join-Path $PublishDir $asset
    if (-not (Test-Path $path)) {
        $missing += $asset
    }
}

if ($missing.Count -gt 0) {
    Write-Error "Missing required MSIX assets:"
    $missing | ForEach-Object { Write-Error "  - $_" }
    exit 1
}
Write-Host "[MSIX] Required assets verified"

if ($VerifyTargetsizeIcons) {
    $targetsize = Get-ChildItem (Join-Path $PublishDir "Assets") -Filter "*targetsize*.png" -ErrorAction SilentlyContinue
    Write-Host "[MSIX] Found $($targetsize.Count) targetsize icons"
    if ($targetsize.Count -lt 10) {
        Write-Error "Expected >=10 targetsize icons, found $($targetsize.Count)."
        exit 1
    }
}

# Fix PRI name for MSIX
$sourcePri = Join-Path $PublishDir "Easydict.WinUI.pri"
$targetPri = Join-Path $PublishDir "resources.pri"
if (Test-Path $sourcePri) {
    Copy-Item -Path $sourcePri -Destination $targetPri -Force
    Write-Host "[MSIX] Copied Easydict.WinUI.pri -> resources.pri"
} elseif (Test-Path $targetPri) {
    Write-Host "[MSIX] resources.pri already exists"
} else {
    Write-Warning "[MSIX] No PRI file found; localization may be incomplete"
}

# Generate temporary manifest with architecture/version overrides. Keep the
# version write scoped to <Identity>; TargetDeviceFamily MinVersion and
# MaxVersionTested must remain the OS compatibility values from the source
# manifest.
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$tempManifest = Join-Path $tempRoot "Package.$Platform.appxmanifest"
[xml]$manifest = Get-Content $ManifestPath -Raw
$manifest.Package.Identity.ProcessorArchitecture = $Platform
if ($MsixVersion) {
    $manifest.Package.Identity.Version = $MsixVersion
}

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$settings.Indent = $true
$writer = [System.Xml.XmlWriter]::Create($tempManifest, $settings)
try {
    $manifest.Save($writer)
} finally {
    $writer.Dispose()
}

# Package
$outputDir = Split-Path -Parent $OutputMsixPath
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

winapp package $PublishDir --output $OutputMsixPath --manifest $tempManifest --skip-pri --verbose

# MinVersion fix
$scriptDir = Split-Path -Parent $PSCommandPath
& (Join-Path $scriptDir "Fix-MsixMinVersion.ps1") -MsixPath $OutputMsixPath

Write-Host "[MSIX] Packaging finished: $OutputMsixPath"

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

# Copy the source publish tree to an isolated staging directory. The source tree
# retains its symbols for Store upload assembly; the customer-facing MSIX never does.
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$stageId = [guid]::NewGuid().ToString("N")
$stagingDir = Join-Path $tempRoot "Easydict-msix-$stageId"
$tempManifest = Join-Path $tempRoot "Package.$Platform.$stageId.appxmanifest"
$sourcePublishDir = (Resolve-Path $PublishDir).Path

try {
    New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
    Get-ChildItem -LiteralPath $sourcePublishDir -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stagingDir -Recurse -Force
    }

    Get-ChildItem -LiteralPath $stagingDir -Filter "*.pdb" -File -Recurse |
        Remove-Item -Force

    # Fix PRI name in the staging tree only.
    $sourcePri = Join-Path $stagingDir "Easydict.WinUI.pri"
    $targetPri = Join-Path $stagingDir "resources.pri"
    if (Test-Path $sourcePri) {
        Copy-Item -Path $sourcePri -Destination $targetPri -Force
        Write-Host "[MSIX] Copied Easydict.WinUI.pri -> resources.pri in staging"
    } elseif (Test-Path $targetPri) {
        Write-Host "[MSIX] resources.pri already exists in staging"
    } else {
        Write-Warning "[MSIX] No PRI file found; localization may be incomplete"
    }

    # Generate temporary manifest with architecture/version overrides. Keep the
    # version write scoped to <Identity>; TargetDeviceFamily MinVersion and
    # MaxVersionTested must remain the OS compatibility values from the source
    # manifest.
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

    $outputDir = Split-Path -Parent $OutputMsixPath
    if (-not (Test-Path $outputDir)) {
        New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    }

    winapp package $stagingDir --output $OutputMsixPath --manifest $tempManifest --skip-pri --verbose
    if ($LASTEXITCODE -ne 0) {
        throw "winapp package failed with exit code $LASTEXITCODE"
    }

    $scriptDir = Split-Path -Parent $PSCommandPath
    & (Join-Path $scriptDir "Fix-MsixMinVersion.ps1") -MsixPath $OutputMsixPath

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $OutputMsixPath))
    try {
        $symbolEntry = $archive.Entries |
            Where-Object { $_.FullName.EndsWith(".pdb", [System.StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -First 1
        if ($symbolEntry) {
            throw "Customer MSIX contains a PDB entry: $($symbolEntry.FullName)"
        }
    } finally {
        $archive.Dispose()
    }

    Write-Host "[MSIX] Packaging finished: $OutputMsixPath"
} finally {
    if (Test-Path $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force
    }
    if (Test-Path $tempManifest) {
        Remove-Item -LiteralPath $tempManifest -Force
    }
}

<#
.SYNOPSIS
    Packages the Easydict OCR browser extension for Chrome/Edge and Firefox.

.DESCRIPTION
    Creates distributable zip/xpi packages from the browser-extension source files.
    - Chrome/Edge: .zip with Manifest V3
    - Firefox: .xpi with Manifest V2

.PARAMETER Target
    Which package to build: Chrome, Firefox, or All (default: All).

.PARAMETER OutputDir
    Directory to place the output packages. Default: browser-extension/dist/

.EXAMPLE
    .\Package-Extension.ps1
    .\Package-Extension.ps1 -Target Chrome
    .\Package-Extension.ps1 -Target Firefox -OutputDir C:\builds
#>
param(
    [ValidateSet("Chrome", "Firefox", "All")]
    [string]$Target = "All",

    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$extensionDir = Split-Path -Parent $scriptDir

if (-not $OutputDir) {
    $OutputDir = Join-Path $extensionDir "dist"
}

# Read version from manifest.json
$manifest = Get-Content (Join-Path $extensionDir "manifest.json") -Raw | ConvertFrom-Json
$version = $manifest.version

Write-Host "Packaging Easydict OCR Browser Extension v$version" -ForegroundColor Cyan
Write-Host "Source:  $extensionDir"
Write-Host "Output:  $OutputDir"
Write-Host ""

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# Common files included in both packages
$commonFiles = @(
    "background.js"
    "icons\icon16.png"
    "icons\icon48.png"
    "icons\icon128.png"
    "_locales\en\messages.json"
    "_locales\zh_CN\messages.json"
)

# Verify all common files exist
foreach ($file in $commonFiles) {
    $fullPath = Join-Path $extensionDir $file
    if (-not (Test-Path $fullPath)) {
        Write-Error "Missing required file: $file"
        exit 1
    }
}

function New-ExtensionPackage {
    param(
        [string]$ManifestFile,
        [string]$OutputFile,
        [string]$Label
    )

    $manifestPath = Join-Path $extensionDir $ManifestFile
    if (-not (Test-Path $manifestPath)) {
        Write-Error "Manifest not found: $ManifestFile"
        return
    }

    # Remove existing package
    if (Test-Path $OutputFile) {
        Remove-Item $OutputFile -Force
    }

    # Create a temp staging directory
    $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "easydict-ext-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

    try {
        # Copy manifest as manifest.json
        Copy-Item $manifestPath (Join-Path $stagingDir "manifest.json")

        # Copy common files preserving directory structure
        foreach ($file in $commonFiles) {
            $src = Join-Path $extensionDir $file
            $dst = Join-Path $stagingDir $file
            $dstDir = Split-Path -Parent $dst
            if (-not (Test-Path $dstDir)) {
                New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
            }
            Copy-Item $src $dst
        }

        # Create the zip/xpi using ZipFile API to ensure forward-slash entry paths
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::Open($OutputFile, 'Create')
        try {
            Get-ChildItem $stagingDir -Recurse -File | ForEach-Object {
                $entryName = $_.FullName.Substring($stagingDir.Length + 1).Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entryName) | Out-Null
            }
        } finally {
            $zip.Dispose()
        }

        $size = (Get-Item $OutputFile).Length
        $sizeKB = [math]::Round($size / 1024, 1)
        Write-Host "  OK  $Label -> $(Split-Path -Leaf $OutputFile) (${sizeKB} KB)" -ForegroundColor Green
    }
    finally {
        Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Build Chrome/Edge package (.zip with Manifest V3)
if ($Target -eq "Chrome" -or $Target -eq "All") {
    $chromeOut = Join-Path $OutputDir "easydict-ocr-chrome-v$version.zip"
    New-ExtensionPackage -ManifestFile "manifest.json" -OutputFile $chromeOut -Label "Chrome/Edge (MV3)"
}

# Build Firefox package (.xpi with Manifest V2)
if ($Target -eq "Firefox" -or $Target -eq "All") {
    $firefoxOut = Join-Path $OutputDir "easydict-ocr-firefox-v$version.xpi"
    New-ExtensionPackage -ManifestFile "manifest.v2.json" -OutputFile $firefoxOut -Label "Firefox (MV2)"
}

Write-Host ""
Write-Host "Done! Packages are in: $OutputDir" -ForegroundColor Cyan

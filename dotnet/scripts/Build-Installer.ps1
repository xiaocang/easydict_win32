# Build-Installer.ps1
# Builds an EXE installer for Easydict using Inno Setup.
#
# Prerequisites:
#   - Inno Setup 6.x installed (https://jrsoftware.org/isinfo.php)
#   - A completed dotnet publish output
#
# Usage:
#   .\Build-Installer.ps1 -Platform x64
#   .\Build-Installer.ps1 -Platform arm64 -Version 1.0.0
#   .\Build-Installer.ps1 -Platform x64 -IsccPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

param(
    [ValidateSet("x64", "x86", "arm64")]
    [string]$Platform = "x64",

    [string]$Version = "",

    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionDir = Split-Path -Parent $ScriptDir
$IssFile = Join-Path $SolutionDir "installer\Easydict.iss"
$PublishDir = Join-Path $SolutionDir "publish\$Platform"

# Auto-detect version from csproj if not provided
if (-not $Version) {
    $csprojPath = Join-Path $SolutionDir "src\Easydict.WinUI\Easydict.WinUI.csproj"
    [xml]$csproj = Get-Content $csprojPath -Raw
    $Version = $csproj.Project.PropertyGroup[0].Version
    if (-not $Version) {
        Write-Error "Could not extract version from csproj. Please specify -Version."
        exit 1
    }
    Write-Host "Auto-detected version: $Version"
}

# Find Inno Setup compiler (ISCC.exe)
if (-not $IsccPath) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            $IsccPath = $candidate
            break
        }
    }
}

if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    Write-Error @"
Inno Setup compiler (ISCC.exe) not found.
Install Inno Setup 6 from: https://jrsoftware.org/isdl.php
Or specify the path: -IsccPath "C:\path\to\ISCC.exe"
"@
    exit 1
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Easydict EXE Installer Builder" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Platform:    $Platform"
Write-Host "Version:     $Version"
Write-Host "Publish Dir: $PublishDir"
Write-Host "ISCC:        $IsccPath"
Write-Host ""

# Verify publish directory exists
if (-not (Test-Path $PublishDir)) {
    Write-Error "Publish directory not found: $PublishDir`nRun 'dotnet publish' or 'make publish-$Platform' first."
    exit 1
}

# Verify main exe exists
$mainExe = Join-Path $PublishDir "Easydict.WinUI.exe"
if (-not (Test-Path $mainExe)) {
    Write-Error "Easydict.WinUI.exe not found in: $PublishDir"
    exit 1
}

# Create output directory
$outputDir = Join-Path $SolutionDir "installer-output"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# Build the installer
Write-Host "Building installer..." -ForegroundColor Green
& $IsccPath `
    /DAppVersion=$Version `
    /DPlatform=$Platform `
    "/DPublishDir=$PublishDir" `
    $IssFile

if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
    exit 1
}

# Show result
$outputFile = Join-Path $outputDir "Easydict-v${Version}-${Platform}-setup.exe"
if (Test-Path $outputFile) {
    $sizeMB = [math]::Round((Get-Item $outputFile).Length / 1MB, 2)
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "Installer Created!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "File: $outputFile"
    Write-Host "Size: $sizeMB MB"
    Write-Host ""
    Write-Host "Install:         $outputFile"
    Write-Host "Silent install:  $outputFile /SILENT"
    Write-Host "Very silent:     $outputFile /VERYSILENT /SUPPRESSMSGBOXES"
} else {
    Write-Error "Expected output file not found: $outputFile"
    exit 1
}

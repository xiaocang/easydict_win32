#!/usr/bin/env pwsh
<#!
.SYNOPSIS
  Bundle a .NET 8 runtime into the MSIX package as a shared runtime for the
  out-of-process workers.

.DESCRIPTION
  The host (Easydict.WinUI.exe) is self-contained — it carries the .NET 8 runtime
  files flat in its publish output. The workers (Easydict.Workers.LongDoc /
  Easydict.Workers.LocalAi) used to publish self-contained too, which duplicated
  the entire .NET 8 runtime (~30 MB per worker per arch). To save MSIX size, the
  workers now publish framework-dependent and find the runtime via DOTNET_ROOT
  pointing at the directory this script produces.

  Output layout (matches the standard layout the .NET host loader expects):

      $OutputDir/
        host/
          fxr/{version}/hostfxr.dll
        shared/
          Microsoft.NETCore.App/{version}/
            coreclr.dll, System.*.dll, ...

  Net savings: ~25 MB per arch (one runtime copy avoided across both workers).

  The runtime is downloaded from Microsoft's official CDN so cross-arch publish
  (x64 runner producing arm64 MSIX) works without needing the target arch's
  runtime installed locally.

.PARAMETER Rid
  Target runtime identifier. One of: win-x64, win-arm64.

.PARAMETER OutputDir
  Output directory. Created if missing. The host/fxr and shared/Microsoft.NETCore.App
  subdirectories are placed directly here.

.PARAMETER Version
  Concrete .NET 8 runtime version. Defaults to a recent 8.0.x; bump alongside
  global.json when raising the SDK floor.

.EXAMPLE
  ./Extract-DotnetRuntime.ps1 -Rid win-x64 -OutputDir ./publish-msix/x64/dotnet
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$Version = "8.0.11"
)

$ErrorActionPreference = "Stop"

# URL pattern documented at
# https://learn.microsoft.com/en-us/dotnet/core/install/windows#scripted-install
# Microsoft maintains the CDN host for the LTS lifetime of .NET 8.
$url = "https://builds.dotnet.microsoft.com/dotnet/Runtime/$Version/dotnet-runtime-$Version-$Rid.zip"
$tmpZip = Join-Path $env:TEMP ("dotnet-runtime-$Version-$Rid-" + [System.IO.Path]::GetRandomFileName() + ".zip")

Write-Host "[ExtractDotnetRuntime] Downloading $url"
try {
    # UseBasicParsing avoids the IE engine dependency on Windows Server SKUs.
    Invoke-WebRequest -Uri $url -OutFile $tmpZip -UseBasicParsing -TimeoutSec 300
} catch {
    throw "Failed to download .NET $Version runtime for $Rid: $($_.Exception.Message)"
}

if (-not (Test-Path $tmpZip) -or (Get-Item $tmpZip).Length -lt 1MB) {
    throw "Downloaded runtime archive is empty or too small (got $((Get-Item $tmpZip).Length) bytes)"
}

Write-Host "[ExtractDotnetRuntime] Extracting to $OutputDir"
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# The ZIP root contains host/, shared/, LICENSE.txt, ThirdPartyNotices.txt directly.
# Extract into $OutputDir so DOTNET_ROOT=$OutputDir works out of the box.
Expand-Archive -Path $tmpZip -DestinationPath $OutputDir -Force
Remove-Item $tmpZip -Force

# Strip license/notice files — they're already in the host's publish output (the
# self-contained host ships its own copies) and the MSIX duplicate-file dedup
# at packaging time would otherwise flag them.
$licenseFiles = @(
    "LICENSE.txt",
    "ThirdPartyNotices.txt"
)
foreach ($f in $licenseFiles) {
    $p = Join-Path $OutputDir $f
    if (Test-Path $p) {
        Remove-Item $p -Force
    }
}

# Sanity check: the standard layout must include both shared\ and host\.
$expected = @(
    "shared/Microsoft.NETCore.App",
    "host/fxr"
)
foreach ($e in $expected) {
    $p = Join-Path $OutputDir $e
    if (-not (Test-Path $p)) {
        throw "Expected directory missing after extraction: $p"
    }
}

# Print the bundled version so CI logs the exact runtime we shipped.
$bundledVersion = (Get-ChildItem (Join-Path $OutputDir "shared/Microsoft.NETCore.App") -Directory |
                   Select-Object -First 1).Name
Write-Host "[ExtractDotnetRuntime] Bundled runtime version: $bundledVersion"

$totalBytes = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host "[ExtractDotnetRuntime] Bundle size: $([math]::Round($totalBytes / 1MB, 1)) MB"

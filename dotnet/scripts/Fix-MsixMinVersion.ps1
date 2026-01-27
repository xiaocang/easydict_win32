<#
.SYNOPSIS
    Verifies and fixes the TargetDeviceFamily MinVersion inside an MSIX package.

.DESCRIPTION
    The winapp CLI overrides TargetDeviceFamily MinVersion in the MSIX manifest
    with the app version instead of preserving the value from Package.appxmanifest.
    This is a known upstream bug (WindowsAppSDK #5598).

    This script extracts the MSIX, checks MinVersion, and re-packs if needed.

.PARAMETER MsixPath
    Path to the MSIX file to verify/fix.

.PARAMETER MinVersion
    Required minimum version. Defaults to 10.0.19041.0.

.EXAMPLE
    .\Fix-MsixMinVersion.ps1 -MsixPath ./msix/Easydict-x64.msix
    .\Fix-MsixMinVersion.ps1 -MsixPath ./msix/Easydict-x64.msix -MinVersion 10.0.19041.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsixPath,

    [Parameter(Mandatory = $false)]
    [string]$MinVersion = "10.0.19041.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $MsixPath)) {
    Write-Error "MSIX file not found: $MsixPath"
    exit 1
}

$extractDir = Join-Path ([System.IO.Path]::GetTempPath()) "msix-minversion-fix-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"

try {
    # Extract MSIX (ZIP format)
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    Expand-Archive -Path $MsixPath -DestinationPath $extractDir -Force

    $manifestPath = Join-Path $extractDir "AppxManifest.xml"
    if (-not (Test-Path $manifestPath)) {
        Write-Error "AppxManifest.xml not found in MSIX package"
        exit 1
    }

    [xml]$manifest = Get-Content $manifestPath -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
    $ns.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")

    $tdf = $manifest.SelectSingleNode("//f:Dependencies/f:TargetDeviceFamily", $ns)
    if (-not $tdf) {
        Write-Error "TargetDeviceFamily not found in AppxManifest.xml"
        exit 1
    }

    $currentMin = $tdf.GetAttribute("MinVersion")
    Write-Host "Current MinVersion in MSIX: $currentMin"
    Write-Host "Required MinVersion: $MinVersion"

    if ([version]$currentMin -ge [version]$MinVersion) {
        Write-Host "MinVersion is OK: $currentMin >= $MinVersion"
        exit 0
    }

    Write-Host "::warning::MinVersion $currentMin is too low, fixing to $MinVersion"
    $tdf.SetAttribute("MinVersion", $MinVersion)
    $manifest.Save($manifestPath)

    # Find MakeAppx.exe from Windows SDK
    $makeAppx = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\MakeAppx.exe" -ErrorAction SilentlyContinue |
                Sort-Object { [version]($_.Directory.Parent.Name) } -Descending |
                Select-Object -First 1

    if (-not $makeAppx) {
        Write-Error "MakeAppx.exe not found - cannot re-pack MSIX"
        exit 1
    }

    # Re-pack
    Remove-Item $MsixPath -Force
    & $makeAppx.FullName pack /d $extractDir /p $MsixPath /o
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to re-pack MSIX with fixed MinVersion"
        exit 1
    }
    Write-Host "Re-packed MSIX with MinVersion=$MinVersion"
}
finally {
    if (Test-Path $extractDir) {
        Remove-Item -Path $extractDir -Recurse -Force
    }
}

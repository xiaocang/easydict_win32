#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Remote-side installer. Runs ON the QDC machine after being copied there by
  Deploy-ToQdc.ps1. Imports the developer cert into Trusted People and installs
  the signed MSIX.

.DESCRIPTION
  Not intended to be invoked manually from the dev machine. Deploy-ToQdc.ps1
  copies this script + cert + MSIX to a staging dir on the remote, then runs it
  over SSH.

  Imports the developer cert into Cert:\LocalMachine\TrustedPeople (admin
  required — Add-AppxPackage's deployment service runs under a security context
  that only consults the machine store, so CurrentUser\TrustedPeople would be
  ignored even if it succeeded). Failures here usually mean the SSH session
  is not elevated.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$CertPath,

    [Parameter(Mandatory = $true)]
    [string]$MsixPath,

    [string]$PackageName = "xiaocang.EasydictforWindows",

    [switch]$LaunchApp
)

$ErrorActionPreference = "Stop"

Write-Host "=== Easydict QDC Remote Install ===" -ForegroundColor Cyan
Write-Host "Cert : $CertPath"
Write-Host "Msix : $MsixPath"
Write-Host "Name : $PackageName"
Write-Host ""

if (-not (Test-Path $CertPath)) { throw "Cert not found: $CertPath" }
if (-not (Test-Path $MsixPath)) { throw "MSIX not found: $MsixPath" }

# Step 1: Import cert
# Add-AppxPackage requires the cert in LocalMachine\TrustedPeople (or LM\Root).
# CurrentUser\TrustedPeople is NOT sufficient -- the deployment service runs
# under a different security context and consults the machine store.
Write-Host "[1/4] Importing certificate to LocalMachine\TrustedPeople..." -ForegroundColor Yellow
try {
    $imported = Import-Certificate `
        -FilePath $CertPath `
        -CertStoreLocation "Cert:\LocalMachine\TrustedPeople"
    Write-Host "  Imported into Cert:\LocalMachine\TrustedPeople" -ForegroundColor Gray
    Write-Host "  Thumbprint: $($imported.Thumbprint)" -ForegroundColor Gray
    Write-Host "  Subject:    $($imported.Subject)" -ForegroundColor Gray
} catch {
    Write-Host "  Failed to import to LocalMachine\TrustedPeople: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "  This usually means the SSH session is not running as admin." -ForegroundColor Yellow
    Write-Host "  Workarounds:" -ForegroundColor Yellow
    Write-Host "    1. Ensure '$env:USERNAME' is in the local Administrators group on this device" -ForegroundColor Gray
    Write-Host "    2. RDP in and run this script (or just the Import-Certificate line) in an elevated PowerShell" -ForegroundColor Gray
    Write-Host "    3. Manually import: certutil -addstore -f TrustedPeople `"$CertPath`"" -ForegroundColor Gray
    throw
}
Write-Host ""

# Step 2: Ensure WindowsAppRuntime framework dep is present
# The MSIX declares PackageDependency on Microsoft.WindowsAppRuntime.2 >= 2.0.1.0.
# Fresh QDC devices typically don't ship with it -- Add-AppxPackage will fail
# with HRESULT 0x80073CF3 if missing. winget covers the install idempotently.
Write-Host "[2/4] Ensuring Microsoft.WindowsAppRuntime.2 (2.0.x) is installed..." -ForegroundColor Yellow
$wingetId = "Microsoft.WindowsAppRuntime.2.0"
$alreadyInstalled = Get-AppxPackage -Name "Microsoft.WindowsAppRuntime.2.*" -ErrorAction SilentlyContinue
if ($alreadyInstalled) {
    Write-Host "  Already present:" -ForegroundColor Gray
    $alreadyInstalled | ForEach-Object { Write-Host "    $($_.PackageFullName)" -ForegroundColor Gray }
} else {
    Write-Host "  Not installed -- running: winget install $wingetId" -ForegroundColor Gray
    & winget install --id $wingetId `
        --accept-source-agreements --accept-package-agreements `
        --disable-interactivity `
        --silent
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  winget install exit code $LASTEXITCODE" -ForegroundColor Yellow
        Write-Host "  Continuing anyway -- Add-AppxPackage will report if the runtime is still missing." -ForegroundColor Gray
    }
}
Write-Host ""

# Step 3: Remove existing package (best-effort)
Write-Host "[3/4] Removing prior installation (if any)..." -ForegroundColor Yellow
$existing = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if ($existing) {
    foreach ($p in $existing) {
        Write-Host "  Removing $($p.PackageFullName)" -ForegroundColor Gray
        try {
            Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop
        } catch {
            Write-Host "  WARNING: Remove-AppxPackage failed: $_" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  No prior installation found" -ForegroundColor Gray
}
Write-Host ""

# Step 4: Install
# Try the clean path first (Add-AppxPackage, user scope). On SSH sessions that
# is a non-interactive logon, PLM (Package Lifecycle Manager) refuses to init
# and Add-AppxPackage fails with HRESULT 0x80070005 even when Developer Mode
# and admin/HIGH integrity are present. Fall back to Add-AppxProvisionedPackage
# (machine scope, doesn't need PLM) + Add-AppxPackage -Register (binds the
# staged package to the current user; reports a spurious 0x80070005 but
# Get-AppxPackage afterwards shows Status=Ok).
Write-Host "[4/4] Installing MSIX..." -ForegroundColor Yellow
$installError = $null
Add-AppxPackage -Path $MsixPath `
    -ForceApplicationShutdown -ForceUpdateFromAnyVersion `
    -ErrorAction SilentlyContinue -ErrorVariable installError

$installed = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if ($installError -and -not $installed) {
    $msg = ($installError[0].Exception.Message -replace "`r?`n", ' | ')
    if ($msg -match '0x80070005') {
        Write-Host "  Add-AppxPackage failed with 0x80070005 (typical for SSH sessions)." -ForegroundColor Yellow
        Write-Host "  Falling back to Add-AppxProvisionedPackage + Register..." -ForegroundColor Yellow

        Add-AppxProvisionedPackage -Online -PackagePath $MsixPath -SkipLicense -ErrorAction Stop | Out-Null
        Write-Host "  Provisioned (machine scope)" -ForegroundColor Gray

        $prov = Get-AppxProvisionedPackage -Online |
            Where-Object DisplayName -eq $PackageName |
            Select-Object -First 1
        if ($prov) {
            $manifest = "C:\Program Files\WindowsApps\$($prov.PackageName)\AppxManifest.xml"
            if (Test-Path $manifest) {
                # Register reports a spurious 0x80070005 but still binds the package.
                Add-AppxPackage -Register $manifest -DisableDevelopmentMode -ErrorAction SilentlyContinue
            }
        }

        $installed = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
        if ($installed) {
            Write-Host "  Registered for $env:USERNAME (Status=$($installed.Status))" -ForegroundColor Gray
        }
    } else {
        Write-Host "ERROR: $msg" -ForegroundColor Red
        exit 1
    }
}

if (-not $installed) {
    Write-Host "ERROR: Get-AppxPackage shows nothing after install -- something is off." -ForegroundColor Red
    exit 2
}
Write-Host "Install succeeded" -ForegroundColor Green
Write-Host ""

Write-Host "Installed package:" -ForegroundColor Cyan
Write-Host "  Name              : $($installed.Name)" -ForegroundColor Gray
Write-Host "  Version           : $($installed.Version)" -ForegroundColor Gray
Write-Host "  Architecture      : $($installed.Architecture)" -ForegroundColor Gray
Write-Host "  PackageFamilyName : $($installed.PackageFamilyName)" -ForegroundColor Gray
Write-Host "  InstallLocation   : $($installed.InstallLocation)" -ForegroundColor Gray
Write-Host ""
Write-Host "Manual launch command:" -ForegroundColor Yellow
Write-Host "  explorer.exe shell:AppsFolder\$($installed.PackageFamilyName)!App" -ForegroundColor Gray

if ($LaunchApp) {
    Write-Host ""
    Write-Host "Launching app..." -ForegroundColor Yellow
    Start-Process "explorer.exe" -ArgumentList "shell:AppsFolder\$($installed.PackageFamilyName)!App"
}

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

    [string]$RuntimeProfile = "rust-only",

    [string]$ValidatorPath = "",

    [string]$PackageName = "xiaocang.EasydictforWindows",

    [switch]$LaunchApp
)

$ErrorActionPreference = "Stop"

function Normalize-RuntimeProfile {
    param([string]$Value)

    $normalized = if ([string]::IsNullOrWhiteSpace($Value)) {
        "rust-only"
    } else {
        $Value.Trim().ToLowerInvariant().Replace("_", "-")
    }

    if ($normalized -eq "rustonly") { return "rust-only" }
    if ($normalized -eq "rust-only" -or $normalized -eq "hybrid") { return $normalized }

    throw "RuntimeProfile '$Value' is not supported. Use 'rust-only' (default) or explicit 'hybrid'."
}

function Find-CargoManifest {
    $scriptDir = Split-Path -Parent $PSCommandPath
    $candidates = @(
        (Join-Path $scriptDir "..\..\..\rs\Cargo.toml"),
        (Join-Path (Get-Location).Path "rs\Cargo.toml")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

function Invoke-MsixValidator {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Profile
    )

    $validatorArgs = @(
        $Path,
        "--runtime-profile",
        $Profile,
        "--allow-unsigned"
    )

    if (-not [string]::IsNullOrWhiteSpace($ValidatorPath)) {
        if (-not (Test-Path $ValidatorPath)) {
            throw "MSIX validator not found: $ValidatorPath"
        }

        & $ValidatorPath @validatorArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        return
    }

    $cargoManifest = Find-CargoManifest
    if (-not $cargoManifest) {
        throw "MSIX validator unavailable. Pass -ValidatorPath or run from a checkout with rs\Cargo.toml."
    }

    & cargo run --manifest-path $cargoManifest -p easydict_msix_validate -- @validatorArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$RuntimeProfile = Normalize-RuntimeProfile $RuntimeProfile

Write-Host "=== Easydict QDC Remote Install ===" -ForegroundColor Cyan
Write-Host "Cert : $CertPath"
Write-Host "Msix : $MsixPath"
Write-Host "Runtime profile : $RuntimeProfile"
if ($ValidatorPath) {
    Write-Host "Validator : $ValidatorPath"
}
Write-Host "Name : $PackageName"
Write-Host ""

if (-not (Test-Path $CertPath)) { throw "Cert not found: $CertPath" }
if (-not (Test-Path $MsixPath)) { throw "MSIX not found: $MsixPath" }

# Step 1: Validate MSIX
Write-Host "[1/5] Validating MSIX runtime payload..." -ForegroundColor Yellow
Invoke-MsixValidator -Path $MsixPath -Profile $RuntimeProfile
Write-Host "  MSIX validation succeeded" -ForegroundColor Green
Write-Host ""

# Step 2: Import cert
# Add-AppxPackage requires the cert in LocalMachine\TrustedPeople (or LM\Root).
# CurrentUser\TrustedPeople is NOT sufficient -- the deployment service runs
# under a different security context and consults the machine store.
Write-Host "[2/5] Importing certificate to LocalMachine\TrustedPeople..." -ForegroundColor Yellow
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

# Step 3: Ensure WindowsAppRuntime framework dep is present
# The MSIX declares PackageDependency on Microsoft.WindowsAppRuntime.2 >= 2.0.1.0.
# Fresh QDC devices typically don't ship with it -- Add-AppxPackage will fail
# with HRESULT 0x80073CF3 if missing. winget covers the install idempotently when
# it works; on SSH-only sessions winget itself often refuses to launch
# ("Access is denied") because App Installer isn't initialized for this logon.
Write-Host "[3/5] Ensuring Microsoft.WindowsAppRuntime.2 (2.0.x) is installed..." -ForegroundColor Yellow
$wingetId = "Microsoft.WindowsAppRuntime.2.0"
# Check both user-scope (Get-AppxPackage) and machine-scope (Get-AppxProvisionedPackage).
# If a sysadmin installed via RDP with a different user, the package may be provisioned
# without being registered for hcktest -- Add-AppxPackage will still find the dependency.
$userRuntime = Get-AppxPackage -Name "Microsoft.WindowsAppRuntime.2.*" -ErrorAction SilentlyContinue
$provRuntime = Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue |
    Where-Object DisplayName -like "Microsoft.WindowsAppRuntime.2.*"
if ($userRuntime) {
    Write-Host "  Already present (user scope):" -ForegroundColor Gray
    $userRuntime | ForEach-Object { Write-Host "    $($_.PackageFullName)" -ForegroundColor Gray }
} elseif ($provRuntime) {
    Write-Host "  Provisioned (machine scope, not registered for $env:USERNAME):" -ForegroundColor Gray
    $provRuntime | ForEach-Object { Write-Host "    $($_.PackageName)" -ForegroundColor Gray }
    Write-Host "  Add-AppxPackage should still resolve it as a dependency." -ForegroundColor Gray
} else {
    Write-Host "  Not installed -- attempting: winget install $wingetId" -ForegroundColor Gray
    try {
        & winget install --id $wingetId `
            --accept-source-agreements --accept-package-agreements `
            --disable-interactivity `
            --silent
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  winget install exit code $LASTEXITCODE" -ForegroundColor Yellow
        }
    } catch {
        # winget.exe failed to launch (typical on fresh QDC SSH sessions).
        Write-Host "  winget could not launch: $($_.Exception.Message -replace ""`r?`n"", ' | ')" -ForegroundColor Yellow
        Write-Host "  Workaround: RDP in (mstsc /v:localhost:5555) and run:" -ForegroundColor Yellow
        Write-Host "    winget install --id $wingetId --accept-source-agreements --accept-package-agreements" -ForegroundColor Gray
    }
    Write-Host "  Continuing anyway -- Add-AppxPackage will report if the runtime is still missing." -ForegroundColor Gray
}
Write-Host ""

# Step 4: Remove existing package (best-effort)
# Two scopes to clear:
#   (a) user-scope registration (Get/Remove-AppxPackage)
#   (b) machine-scope provisioned copy (Get/Remove-AppxProvisionedPackage)
# When the previous deploy fell back to Add-AppxProvisionedPackage + Register
# (the SSH/PLM 0x80070005 path), only clearing (a) leaves the staged binary
# behind. A subsequent Add-AppxPackage of a rebuild with the same version then
# fails 0x80073CFB ("same identity, different contents") because the
# provisioned copy has different bits.
Write-Host "[4/5] Removing prior installation (if any)..." -ForegroundColor Yellow
$existing = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if ($existing) {
    foreach ($p in $existing) {
        Write-Host "  Removing user registration: $($p.PackageFullName)" -ForegroundColor Gray
        try {
            Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop
        } catch {
            Write-Host "  WARNING: Remove-AppxPackage failed: $_" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  No prior user registration" -ForegroundColor Gray
}
$provisioned = Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue |
    Where-Object DisplayName -eq $PackageName
if ($provisioned) {
    foreach ($p in $provisioned) {
        Write-Host "  Removing provisioned: $($p.PackageName)" -ForegroundColor Gray
        try {
            Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName -ErrorAction Stop | Out-Null
        } catch {
            Write-Host "  WARNING: Remove-AppxProvisionedPackage failed: $_" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  No prior provisioned package" -ForegroundColor Gray
}
Write-Host ""

# Step 5: Install
# Try the clean path first (Add-AppxPackage, user scope). On SSH sessions that
# is a non-interactive logon, PLM (Package Lifecycle Manager) refuses to init
# and Add-AppxPackage fails with HRESULT 0x80070005 even when Developer Mode
# and admin/HIGH integrity are present. Fall back to Add-AppxProvisionedPackage
# (machine scope, doesn't need PLM) + Add-AppxPackage -Register (binds the
# staged package to the current user; reports a spurious 0x80070005 but
# Get-AppxPackage afterwards shows Status=Ok).
Write-Host "[5/5] Installing MSIX..." -ForegroundColor Yellow
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
            # On a fresh QDC reservation, the manifest can pass Test-Path before
            # the provisioned package is fully staged. Retry Register a few times,
            # surfacing errors instead of silently swallowing them.
            for ($attempt = 1; $attempt -le 4; $attempt++) {
                if (-not (Test-Path $manifest)) {
                    Write-Host "  Manifest not yet at $manifest (attempt $attempt) -- waiting 3s..." -ForegroundColor Yellow
                    Start-Sleep -Seconds 3
                    continue
                }
                $regError = $null
                # Register reports a spurious 0x80070005 but still binds the package
                # in the success case; treat that one error code as success and
                # confirm via Get-AppxPackage.
                Add-AppxPackage -Register $manifest -DisableDevelopmentMode `
                    -ErrorAction SilentlyContinue -ErrorVariable regError
                $installed = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
                if ($installed) { break }
                if ($regError) {
                    $regMsg = ($regError[0].Exception.Message -replace "`r?`n", ' | ')
                    Write-Host "  Register attempt $attempt failed: $regMsg" -ForegroundColor Yellow
                }
                Start-Sleep -Seconds 3
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

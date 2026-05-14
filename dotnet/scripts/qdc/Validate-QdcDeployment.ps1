#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Remote-side validation. Runs ON the QDC machine after install. Verifies that
  the package is registered, prints OS/CPU info, and flags the Phi Silica build
  gate.

.DESCRIPTION
  Ground truth for Phi Silica readiness lives inside the app itself
  (PhiSilicaAvailability.GetReadyState()). This script only validates the
  static prerequisites that a fresh QDC machine could fail on:
    - signing cert is in Trusted People
    - MSIX package is registered
    - OS build is >= 26100 (Windows 11 24H2)
    - CPU arch is ARM64 (Snapdragon X) -- warning if not
#>

param(
    [string]$PackageName = "xiaocang.EasydictforWindows",
    [string]$CertSubject = "CN=33FC47D7-8283-45FC-BB5D-297D1476BB29"
)

$ErrorActionPreference = "Continue"
$failed = $false

Write-Host "=== Easydict QDC Deployment Validation ===" -ForegroundColor Cyan
Write-Host ""

# 1. Package registered?
Write-Host "[1/4] Package registration..." -ForegroundColor Yellow
$pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if (-not $pkg) {
    Write-Host "  FAIL: package '$PackageName' not registered for $env:USERNAME" -ForegroundColor Red
    $failed = $true
} else {
    Write-Host "  OK   Name              : $($pkg.Name)" -ForegroundColor Green
    Write-Host "       Version           : $($pkg.Version)" -ForegroundColor Gray
    Write-Host "       Architecture      : $($pkg.Architecture)" -ForegroundColor Gray
    Write-Host "       PackageFamilyName : $($pkg.PackageFamilyName)" -ForegroundColor Gray
    Write-Host "       Publisher         : $($pkg.Publisher)" -ForegroundColor Gray
    Write-Host "       Status            : $($pkg.Status)" -ForegroundColor Gray
}
Write-Host ""

# 2. Cert in Trusted People?
Write-Host "[2/4] Signing cert trust..." -ForegroundColor Yellow
$cuFound = Get-ChildItem "Cert:\CurrentUser\TrustedPeople" -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $CertSubject }
$lmFound = Get-ChildItem "Cert:\LocalMachine\TrustedPeople" -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $CertSubject }
if ($cuFound) {
    Write-Host "  OK   Found in CurrentUser\TrustedPeople" -ForegroundColor Green
    Write-Host "       Thumbprint: $($cuFound.Thumbprint)" -ForegroundColor Gray
} elseif ($lmFound) {
    Write-Host "  OK   Found in LocalMachine\TrustedPeople" -ForegroundColor Green
    Write-Host "       Thumbprint: $($lmFound.Thumbprint)" -ForegroundColor Gray
} else {
    Write-Host "  FAIL: cert with subject '$CertSubject' not in any TrustedPeople store" -ForegroundColor Red
    $failed = $true
}
Write-Host ""

# 3. OS build gate for Phi Silica (24H2 = 26100)
Write-Host "[3/4] OS build (Phi Silica needs >= 26100)..." -ForegroundColor Yellow
$os = Get-CimInstance Win32_OperatingSystem
$build = [Environment]::OSVersion.Version.Build
Write-Host "       OS      : $($os.Caption)" -ForegroundColor Gray
Write-Host "       Version : $($os.Version) (Build $build)" -ForegroundColor Gray
if ($build -ge 26100) {
    Write-Host "  OK   Build $build meets the Phi Silica floor" -ForegroundColor Green
} else {
    Write-Host "  WARN Build $build is below 26100 -- Phi Silica will be unavailable" -ForegroundColor Yellow
}
Write-Host ""

# 4. CPU arch (should be ARM64 on QDC Snapdragon X)
Write-Host "[4/4] CPU / arch..." -ForegroundColor Yellow
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$envArch = $env:PROCESSOR_ARCHITECTURE
Write-Host "       CPU  : $($cpu.Name.Trim())" -ForegroundColor Gray
Write-Host "       Arch : $envArch" -ForegroundColor Gray
if ($envArch -eq "ARM64") {
    Write-Host "  OK   ARM64 -- eligible for NPU-backed Phi Silica" -ForegroundColor Green
    if ($pkg -and $pkg.Architecture -ne "Arm64" -and $pkg.Architecture -ne "Neutral") {
        Write-Host "  WARN Installed package arch is $($pkg.Architecture); it will run under x64 emulation. Native ARM64 is required for the NPU path." -ForegroundColor Yellow
    }
} else {
    Write-Host "  WARN Not ARM64 -- Phi Silica path will not exercise an NPU on this device" -ForegroundColor Yellow
}
Write-Host ""

if ($pkg) {
    Write-Host "Launch:" -ForegroundColor Cyan
    Write-Host "  explorer.exe shell:AppsFolder\$($pkg.PackageFamilyName)!App" -ForegroundColor Gray
    Write-Host ""
}

if ($failed) {
    Write-Host "Validation FAILED." -ForegroundColor Red
    exit 1
}
Write-Host "Validation passed." -ForegroundColor Cyan
exit 0

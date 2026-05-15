#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Verify SSH access to a Qualcomm Device Cloud (QDC) Windows machine and dump
  basic environment info (OS build, CPU, arch, free disk).

.DESCRIPTION
  QDC Windows Compute devices are accessed via RDP by default. To use SSH you
  must first enable OpenSSH Server inside the QDC machine (once per session):

    # Run inside QDC over RDP, in an admin PowerShell:
    Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
    Start-Service sshd
    Set-Service -Name sshd -StartupType Automatic
    New-NetFirewallRule -Name sshd -DisplayName 'OpenSSH Server (sshd)' `
      -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22

  After that, this script can connect over the network.

  QDC reaches the Windows box via a jump host (sshtunnel@ssh.qdc.qualcomm.com)
  because the device's hostname is only resolvable inside the QDC cluster. The
  QDC jump host only allows explicit -L port forwarding and rejects the
  direct-tcpip channel that OpenSSH's -J ProxyJump uses, so the canonical
  workflow is:

    1. Open a persistent local forward with Start-QdcTunnel.ps1 (once per
       session). It maps localhost:2222 -> <DeviceHost>:22 through the jump
       host and records the PID for cleanup.
    2. Run this script against localhost:2222 (the -ProxyJump parameter still
       exists for the rare direct-public-IP case, but the QDC jump host is not
       a valid value for it).

.EXAMPLE
  # Typical QDC: run Start-QdcTunnel.ps1 first, then probe via the local forward.
  ./Start-QdcTunnel.ps1 `
      -IdentityFile "C:\Users\johnn\Downloads\qdc_id_2026-5-14_1534.pem" `
      -DeviceHost sa590782.sa.svc.cluster.local
  ./Test-QdcConnection.ps1 `
      -RemoteHost localhost -Port 2222 -User HCKTest `
      -IdentityFile "C:\Users\johnn\Downloads\qdc_id_2026-5-14_1534.pem"

.EXAMPLE
  # Direct (rare -- only if the device has a reachable IP)
  ./Test-QdcConnection.ps1 -RemoteHost 10.0.0.5 -User HCKTest -IdentityFile ~/.ssh/qdc_key
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$RemoteHost,

    [Parameter(Mandatory = $true)]
    [string]$User,

    [string]$IdentityFile = "",

    [int]$Port = 22,

    [string]$ProxyJump = ""
)

$ErrorActionPreference = "Stop"

function Get-SshArgs {
    # NoHostKeyCheck is on by default for QDC: every session re-maps localhost:2222
    # to a different physical device, so accept-new pollutes known_hosts and
    # subsequent runs trip the "REMOTE HOST IDENTIFICATION HAS CHANGED" gate.
    $a = @(
        "-o", "StrictHostKeyChecking=no",
        "-o", "UserKnownHostsFile=NUL",
        "-o", "ConnectTimeout=15",
        "-p", "$Port"
    )
    if ($IdentityFile) {
        $resolved = (Resolve-Path $IdentityFile).Path
        $a += @("-i", $resolved)
    }
    if ($ProxyJump) {
        $a += @("-J", $ProxyJump)
    }
    return $a
}

Write-Host "=== QDC Connection Test ===" -ForegroundColor Cyan
Write-Host "Host : $User@${RemoteHost}:$Port"
if ($IdentityFile) { Write-Host "Key  : $IdentityFile" }
if ($ProxyJump)    { Write-Host "Jump : $ProxyJump" }
Write-Host ""

$sshArgs = Get-SshArgs
$target = "$User@$RemoteHost"

# Probe 1: trivial command to test connectivity
Write-Host "[1/2] Probing SSH..." -ForegroundColor Yellow
$probe = & ssh @sshArgs $target "echo OK" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "SSH connection failed." -ForegroundColor Red
    Write-Host $probe
    Write-Host ""
    Write-Host "Likely causes:" -ForegroundColor Yellow
    Write-Host "  - OpenSSH Server is not enabled inside the QDC machine."
    Write-Host "    Connect via RDP first, then run (admin PowerShell):"
    Write-Host ""
    Write-Host "      Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0" -ForegroundColor Gray
    Write-Host "      Start-Service sshd" -ForegroundColor Gray
    Write-Host "      Set-Service -Name sshd -StartupType Automatic" -ForegroundColor Gray
    Write-Host "      New-NetFirewallRule -Name sshd -DisplayName 'OpenSSH Server (sshd)' ``" -ForegroundColor Gray
    Write-Host "        -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  - Network reachability (firewall / VPN / wrong host)"
    Write-Host "  - Wrong username, password, or key not authorized"
    exit 1
}
Write-Host "Connectivity OK" -ForegroundColor Green
Write-Host ""

# Probe 2: dump environment via PowerShell over SSH
Write-Host "[2/2] Reading remote environment..." -ForegroundColor Yellow
$remotePs = @'
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'"
$arch = $env:PROCESSOR_ARCHITECTURE
$build = [Environment]::OSVersion.Version.Build
$pwshAvail = if (Get-Command pwsh -ErrorAction SilentlyContinue) { "yes" } else { "no" }
"User       : $env:USERNAME"
"Hostname   : $env:COMPUTERNAME"
"OS         : $($os.Caption)"
"Version    : $($os.Version) (Build $build)"
"Arch       : $arch"
"CPU        : $($cpu.Name.Trim())"
"DiskFreeGB : {0:N1}" -f ($disk.FreeSpace / 1GB)
"PwshAvail  : $pwshAvail"
'@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remotePs))
$envInfo = & ssh @sshArgs $target "powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Remote environment query failed." -ForegroundColor Red
    Write-Host $envInfo
    exit 1
}

$envInfo | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
Write-Host ""

# Flag Phi Silica build gate
$buildLine = ($envInfo | Where-Object { $_ -match '^Version' })
if ($buildLine -match 'Build (\d+)') {
    $build = [int]$Matches[1]
    if ($build -lt 26100) {
        Write-Host "Note: OS build $build is below 26100 (Windows 11 24H2). Phi Silica is unavailable." -ForegroundColor Yellow
    } else {
        Write-Host "OS build $build meets the Phi Silica floor (>= 26100)." -ForegroundColor Green
    }
}

$archLine = ($envInfo | Where-Object { $_ -match '^Arch' })
if ($archLine -match 'Arch\s*:\s*(\S+)') {
    $remoteArch = $Matches[1]
    if ($remoteArch -ne "ARM64") {
        Write-Host "Note: remote arch is $remoteArch (not ARM64). QDC's Snapdragon X devices are ARM64; if you got x86_64 here, you may be on a non-Snapdragon device and Phi Silica will not exercise the NPU." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Connection test passed." -ForegroundColor Cyan

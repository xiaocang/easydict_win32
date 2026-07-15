#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Start (or stop) a nested SSH tunnel that forwards the QDC device's RDP port
  (3389) to localhost:<LocalPort>, riding inside the existing Start-QdcTunnel
  forward on localhost:2222.

.DESCRIPTION
  After Start-QdcTunnel.ps1 has set up localhost:2222 -> <device>:22, this
  script opens a second ssh that connects to that local port and adds
  -L <LocalPort>:localhost:3389 so the device's RDP service is reachable as
  localhost:<LocalPort> on the dev machine. Then:
    mstsc /v:localhost:<LocalPort>

  The PID is recorded in $env:TEMP\qdc-rdp-<port>.pid so -Stop can clean up.

.EXAMPLE
  ./Start-QdcRdpForward.ps1 -IdentityFile "C:\Users\johnn\Downloads\qdc_id_2026-5-14_1534.pem"
  mstsc /v:localhost:5555

.EXAMPLE
  ./Start-QdcRdpForward.ps1 -Stop
#>

param(
    [string]$IdentityFile = "",
    [int]$LocalPort       = 5555,
    [int]$RemoteRdpPort   = 3389,
    [int]$TunnelPort      = 2222,
    [string]$User         = "hcktest",
    [switch]$Stop,
    [switch]$Status
)

$ErrorActionPreference = "Continue"

$pidFile = Join-Path $env:TEMP "qdc-rdp-$LocalPort.pid"
$logFile = Join-Path $env:TEMP "qdc-rdp-$LocalPort.log"

function Get-RdpProcess {
    if (-not (Test-Path $pidFile)) { return $null }
    $recorded = Get-Content $pidFile -ErrorAction SilentlyContinue
    if (-not $recorded) { return $null }
    $proc = Get-Process -Id ([int]$recorded) -ErrorAction SilentlyContinue
    if ($proc -and $proc.ProcessName -eq "ssh") { return $proc }
    return $null
}

if ($Status) {
    $proc = Get-RdpProcess
    if ($proc) {
        Write-Host "RDP forward ALIVE on localhost:$LocalPort (PID $($proc.Id), started $($proc.StartTime))" -ForegroundColor Green
    } else {
        Write-Host "No RDP forward on localhost:$LocalPort" -ForegroundColor Yellow
    }
    return
}

if ($Stop) {
    $proc = Get-RdpProcess
    if ($proc) {
        Stop-Process -Id $proc.Id -Force
        Write-Host "Stopped RDP forward PID $($proc.Id) on localhost:$LocalPort" -ForegroundColor Green
    } else {
        Write-Host "No live RDP forward on localhost:$LocalPort" -ForegroundColor Yellow
    }
    if (Test-Path $pidFile) { Remove-Item $pidFile -Force }
    return
}

if (-not $IdentityFile) { throw "-IdentityFile is required" }

$existing = Get-RdpProcess
if ($existing) {
    Write-Host "RDP forward already running on localhost:$LocalPort (PID $($existing.Id))" -ForegroundColor Yellow
    Write-Host "  mstsc /v:localhost:$LocalPort" -ForegroundColor Gray
    return
}

$IdentityFile = (Resolve-Path $IdentityFile).Path

Write-Host "=== Start QDC RDP Forward ===" -ForegroundColor Cyan
Write-Host "Local : localhost:$LocalPort"
Write-Host "Remote: <device>:$RemoteRdpPort (via localhost:$TunnelPort)"
Write-Host "Key   : $IdentityFile"
Write-Host ""

$sshArgs = @(
    "-i", $IdentityFile,
    "-N",
    # Each QDC reservation re-maps localhost:$TunnelPort to a different physical
    # device with its own host key. UserKnownHostsFile=NUL + StrictHostKeyChecking=no
    # is not enough on Windows OpenSSH -- it still flags "REMOTE HOST IDENTIFICATION
    # HAS CHANGED" and DISABLES port forwarding (the -L is then useless). For the
    # localhost hop, NoHostAuthenticationForLocalhost=yes is the documented
    # opt-out and skips host-key auth entirely.
    "-o", "NoHostAuthenticationForLocalhost=yes",
    "-o", "StrictHostKeyChecking=no",
    "-o", "UserKnownHostsFile=NUL",
    "-o", "ServerAliveInterval=60",
    "-o", "ServerAliveCountMax=3",
    "-o", "ExitOnForwardFailure=yes",
    "-p", "$TunnelPort",
    "-L", "${LocalPort}:localhost:${RemoteRdpPort}",
    "${User}@localhost"
)

$proc = Start-Process -FilePath "ssh" `
    -ArgumentList $sshArgs `
    -WindowStyle Hidden `
    -PassThru `
    -RedirectStandardError $logFile

Start-Sleep -Seconds 3

if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) {
    Write-Host "Tunnel process died within 3s." -ForegroundColor Red
    if (Test-Path $logFile) {
        Get-Content $logFile | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    exit 1
}

$proc.Id | Out-File $pidFile -Encoding ASCII

$listening = Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue
if ($listening) {
    Write-Host "RDP forward UP -- localhost:$LocalPort -> remote:$RemoteRdpPort (PID $($proc.Id))" -ForegroundColor Green
} else {
    Write-Host "Process alive but port $LocalPort not listening yet. Check log: $logFile" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Connect with:" -ForegroundColor Gray
Write-Host "  mstsc /v:localhost:$LocalPort" -ForegroundColor Gray
Write-Host ""
Write-Host "When done:" -ForegroundColor Gray
Write-Host "  .\Start-QdcRdpForward.ps1 -Stop -LocalPort $LocalPort" -ForegroundColor Gray

#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Start (or stop) a background SSH tunnel from localhost:<LocalPort> to a QDC
  Windows device's port 22, via the QDC jump host.

.DESCRIPTION
  QDC's jump host (ssh.qdc.qualcomm.com) only allows explicit -L port
  forwarding -- it rejects the direct-tcpip channel that OpenSSH's -J ProxyJump
  uses. So we open a persistent local forward in the background, then all
  subsequent ssh/scp calls target localhost:<LocalPort> with -i <pem>
  -User hcktest. The same pem authenticates both the jump and the device,
  because QDC pre-installs the public key into hcktest's authorized_keys.

  The tunnel process PID is recorded in $env:TEMP\qdc-tunnel-<port>.pid so
  -Stop can find and kill it later. Re-running -Start when a tunnel is already
  alive is a no-op.

.EXAMPLE
  # Start tunnel (typical first step in a deploy session)
  ./Start-QdcTunnel.ps1 `
      -IdentityFile "C:\Users\johnn\Downloads\qdc_id_2026-5-14_1534.pem" `
      -DeviceHost sa590782.sa.svc.cluster.local

.EXAMPLE
  # Stop tunnel
  ./Start-QdcTunnel.ps1 -Stop
#>

param(
    [string]$IdentityFile = "",

    [string]$DeviceHost = "",

    [int]$DevicePort = 22,

    [int]$LocalPort = 2222,

    [string]$JumpUser = "sshtunnel",

    [string]$JumpHost = "ssh.qdc.qualcomm.com",

    [int]$JumpPort = 22,

    [switch]$Stop,

    [switch]$Status
)

$ErrorActionPreference = "Stop"

$pidFile = Join-Path $env:TEMP "qdc-tunnel-$LocalPort.pid"

function Get-TunnelProcess {
    if (-not (Test-Path $pidFile)) { return $null }
    $recorded = Get-Content $pidFile -ErrorAction SilentlyContinue
    if (-not $recorded) { return $null }
    $proc = Get-Process -Id ([int]$recorded) -ErrorAction SilentlyContinue
    if ($proc -and $proc.ProcessName -eq "ssh") { return $proc }
    return $null
}

if ($Status) {
    $proc = Get-TunnelProcess
    if ($proc) {
        Write-Host "Tunnel ALIVE on localhost:$LocalPort (PID $($proc.Id), started $($proc.StartTime))" -ForegroundColor Green
    } else {
        Write-Host "No tunnel on localhost:$LocalPort" -ForegroundColor Yellow
    }
    return
}

if ($Stop) {
    $proc = Get-TunnelProcess
    if ($proc) {
        Stop-Process -Id $proc.Id -Force
        Write-Host "Stopped tunnel PID $($proc.Id) on localhost:$LocalPort" -ForegroundColor Green
    } else {
        Write-Host "No live tunnel on localhost:$LocalPort" -ForegroundColor Yellow
    }
    if (Test-Path $pidFile) { Remove-Item $pidFile -Force }
    return
}

# Start mode
if (-not $IdentityFile) { throw "-IdentityFile is required to start a tunnel" }
if (-not $DeviceHost)   { throw "-DeviceHost is required to start a tunnel (e.g. sa590782.sa.svc.cluster.local)" }

$existing = Get-TunnelProcess
if ($existing) {
    Write-Host "Tunnel already running on localhost:$LocalPort (PID $($existing.Id))" -ForegroundColor Yellow
    Write-Host "Use -Stop to kill it, or -Status to inspect." -ForegroundColor Gray
    return
}

$IdentityFile = (Resolve-Path $IdentityFile).Path

Write-Host "=== Start QDC Tunnel ===" -ForegroundColor Cyan
Write-Host "Local : localhost:$LocalPort"
Write-Host "Device: ${DeviceHost}:${DevicePort}"
Write-Host "Jump  : ${JumpUser}@${JumpHost}:${JumpPort}"
Write-Host "Key   : $IdentityFile"
Write-Host ""

$sshArgs = @(
    "-i", $IdentityFile,
    "-N",
    "-o", "StrictHostKeyChecking=no",
    "-o", "UserKnownHostsFile=NUL",
    "-o", "ServerAliveInterval=60",
    "-o", "ServerAliveCountMax=3",
    "-o", "ExitOnForwardFailure=yes",
    "-p", "$JumpPort",
    "-L", "${LocalPort}:${DeviceHost}:${DevicePort}",
    "${JumpUser}@${JumpHost}"
)

# Redirect ssh's stderr/stdout to a log file so we can see why it died if it does
$logFile = Join-Path $env:TEMP "qdc-tunnel-$LocalPort.log"
$proc = Start-Process -FilePath "ssh" `
    -ArgumentList $sshArgs `
    -WindowStyle Hidden `
    -PassThru `
    -RedirectStandardError $logFile

# Give ssh a moment to either complete the handshake or fall over
Start-Sleep -Seconds 3

if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) {
    Write-Host "Tunnel process died within 3s." -ForegroundColor Red
    if (Test-Path $logFile) {
        Write-Host "ssh stderr:" -ForegroundColor Red
        Get-Content $logFile | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    exit 1
}

$proc.Id | Out-File $pidFile -Encoding ASCII

# Confirm the local port is actually listening
$listening = Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue
if ($listening) {
    Write-Host "Tunnel UP -- localhost:$LocalPort -> ${DeviceHost}:${DevicePort} (PID $($proc.Id))" -ForegroundColor Green
} else {
    Write-Host "Process alive but port $LocalPort not listening yet. Check log: $logFile" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Now run, for example:" -ForegroundColor Gray
Write-Host "  .\Test-QdcConnection.ps1 -RemoteHost localhost -Port $LocalPort -User hcktest -IdentityFile `"$IdentityFile`"" -ForegroundColor Gray
Write-Host ""
Write-Host "When done:" -ForegroundColor Gray
Write-Host "  .\Start-QdcTunnel.ps1 -Stop -LocalPort $LocalPort" -ForegroundColor Gray

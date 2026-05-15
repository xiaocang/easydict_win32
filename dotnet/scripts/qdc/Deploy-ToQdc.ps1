#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Deploy a signed Easydict MSIX to a Qualcomm Device Cloud (QDC) Windows
  machine over SSH.

.DESCRIPTION
  Local-side orchestrator:
    1. Sanity-check local files (cer, signed MSIX), read the MSIX's
       ProcessorArchitecture from its embedded AppxManifest.xml.
    2. scp cert + msix + remote scripts to a staging dir on the remote.
    3. ssh -> run Install-OnQdc.ps1 (import cert, install MSIX).
    4. ssh -> run Validate-QdcDeployment.ps1 (verify install + Phi Silica gates).

  Prereqs inside the QDC machine:
    1. OpenSSH Server enabled (see Test-QdcConnection.ps1 synopsis).
    2. Your public key dropped into
       C:\Users\<user>\.ssh\authorized_keys
       OR be prepared to type the device password for every ssh/scp call.

  QDC reaches the Windows box via a jump host because the device's hostname is
  only resolvable inside the QDC cluster. Use -ProxyJump to route every
  ssh/scp call through it (uses OpenSSH's -J flag).

.EXAMPLE
  # Typical QDC: routed through the QDC jump host
  ./Deploy-ToQdc.ps1 `
      -RemoteHost sa590782.sa.svc.cluster.local `
      -User HCKTest `
      -IdentityFile "C:\Users\johnn\Downloads\qdc_id_2026-5-14_1534.pem" `
      -ProxyJump sshtunnel@ssh.qdc.qualcomm.com `
      -MsixPath ..\..\msix\Easydict-v0.7.6-x64.msix

.EXAMPLE
  # Direct (rare -- only if the device has a reachable IP)
  ./Deploy-ToQdc.ps1 -RemoteHost 10.0.0.5 -User HCKTest `
      -MsixPath ..\..\msix\Easydict-v0.7.6-x64.msix `
      -SkipValidate -LaunchApp
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$RemoteHost,

    [Parameter(Mandatory = $true)]
    [string]$User,

    [Parameter(Mandatory = $true)]
    [string]$MsixPath,

    [string]$CertPath = "",

    [string]$IdentityFile = "",

    [int]$Port = 22,

    [string]$ProxyJump = "",

    [string]$RemoteStagingDir = "",

    [switch]$SkipValidate,

    [switch]$LaunchApp,

    [switch]$Machine,

    [switch]$ScpVerbose
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$dotnetDir = Split-Path -Parent (Split-Path -Parent $scriptDir)

# --- 1. Resolve local files -------------------------------------------------

if (-not $CertPath) {
    $CertPath = Join-Path $dotnetDir "certs\dev-signing.cer"
}

if (-not (Test-Path $MsixPath)) { throw "MSIX not found: $MsixPath" }
if (-not (Test-Path $CertPath)) {
    throw "Cert not found: $CertPath`nExpected dev-signing.cer; export it from the pfx if missing: " +
          "`n  `$pfx = 'dotnet/certs/dev-signing.pfx'" +
          "`n  `$pwd = ConvertTo-SecureString 'password' -AsPlainText -Force" +
          "`n  Export-Certificate -Cert (Get-PfxCertificate -FilePath `$pfx) -FilePath 'dotnet/certs/dev-signing.cer'"
}

$MsixPath = (Resolve-Path $MsixPath).Path
$CertPath = (Resolve-Path $CertPath).Path
$InstallScript = Join-Path $scriptDir "Install-OnQdc.ps1"
$ValidateScript = Join-Path $scriptDir "Validate-QdcDeployment.ps1"
if (-not (Test-Path $InstallScript)) { throw "Install script missing: $InstallScript" }
if (-not (Test-Path $ValidateScript)) { throw "Validate script missing: $ValidateScript" }

# Read MSIX architecture from the embedded AppxManifest.xml
function Get-MsixArchitecture {
    param([string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'AppxManifest.xml' } | Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try {
            $xml = [xml]$reader.ReadToEnd()
            return $xml.Package.Identity.ProcessorArchitecture
        } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
}

$msixArch = Get-MsixArchitecture $MsixPath
$msixSize = [math]::Round((Get-Item $MsixPath).Length / 1MB, 1)

Write-Host "=== Easydict QDC Deploy ===" -ForegroundColor Cyan
Write-Host "Local:"
Write-Host "  MSIX : $MsixPath  (${msixSize} MB, arch=$msixArch)" -ForegroundColor Gray
Write-Host "  Cert : $CertPath" -ForegroundColor Gray
Write-Host "Remote:"
Write-Host "  Host : $User@${RemoteHost}:$Port" -ForegroundColor Gray
if ($IdentityFile) {
    $IdentityFile = (Resolve-Path $IdentityFile).Path
    Write-Host "  Key  : $IdentityFile" -ForegroundColor Gray
}
if ($ProxyJump) {
    Write-Host "  Jump : $ProxyJump" -ForegroundColor Gray
}
Write-Host ""

if ($msixArch -and $msixArch -ne "arm64" -and $msixArch -ne "neutral") {
    Write-Host "WARNING: MSIX arch is '$msixArch'. QDC Snapdragon X devices are ARM64; " -ForegroundColor Yellow -NoNewline
    Write-Host "this package will install under x64 emulation and CANNOT exercise the NPU for Phi Silica." -ForegroundColor Yellow
    Write-Host "         For Phi Silica testing, build an ARM64 MSIX." -ForegroundColor Yellow
    Write-Host ""
}

# --- 2. SSH/SCP helpers -----------------------------------------------------

function Get-SshArgs {
    # See Test-QdcConnection.ps1 for the rationale on disabling host key check:
    # QDC's localhost:2222 -> $deviceHost mapping changes every session.
    $a = @(
        "-o", "StrictHostKeyChecking=no",
        "-o", "UserKnownHostsFile=NUL",
        "-o", "ConnectTimeout=15",
        "-p", "$Port"
    )
    if ($IdentityFile) { $a += @("-i", $IdentityFile) }
    if ($ProxyJump)    { $a += @("-J", $ProxyJump) }
    return $a
}

function Get-ScpArgs {
    # scp uses -P (capital) for port, not -p
    $a = @(
        "-o", "StrictHostKeyChecking=no",
        "-o", "UserKnownHostsFile=NUL",
        "-o", "ConnectTimeout=15",
        "-P", "$Port"
    )
    if ($IdentityFile) { $a += @("-i", $IdentityFile) }
    if ($ProxyJump)    { $a += @("-J", $ProxyJump) }
    if ($ScpVerbose)   { $a += "-v" }
    return $a
}

function To-ScpLocalPath {
    param([string]$WinPath)
    # scp on Windows accepts forward slashes; backslashes confuse the parser
    return $WinPath -replace '\\', '/'
}

$sshArgs = Get-SshArgs
$scpArgs = Get-ScpArgs
$target = "$User@$RemoteHost"

# --- 3. Determine remote home + staging dir --------------------------------

Write-Host "[1/5] Probing remote home directory..." -ForegroundColor Yellow
# Drop ssh stderr in success path -- "Permanently added (host key)" warnings come
# through as ErrorRecord objects that break .Trim(). On failure we re-run with
# stderr visible.
$probeOutput = & ssh @sshArgs $target "powershell.exe -NoProfile -Command `$env:USERPROFILE" 2>$null
if ($LASTEXITCODE -ne 0 -or -not $probeOutput) {
    Write-Host "SSH probe failed. Re-running with stderr to diagnose:" -ForegroundColor Red
    & ssh @sshArgs $target "powershell.exe -NoProfile -Command `$env:USERPROFILE" 2>&1 |
        ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
$remoteHome = "$probeOutput".Trim()
Write-Host "  Remote USERPROFILE = $remoteHome" -ForegroundColor Gray

if (-not $RemoteStagingDir) {
    $RemoteStagingDir = "$remoteHome\AppData\Local\Temp\EasydictDeploy"
}
$RemoteStagingDir = $RemoteStagingDir -replace '/', '\'
Write-Host "  Staging            = $RemoteStagingDir" -ForegroundColor Gray
Write-Host ""

# Create staging dir on remote
$mkdirCmd = "powershell.exe -NoProfile -Command `"New-Item -ItemType Directory -Force -Path '$RemoteStagingDir' | Out-Null`""
& ssh @sshArgs $target $mkdirCmd 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to create remote staging dir" }

# --- 4. Copy files ----------------------------------------------------------

Write-Host "[2/5] Copying files to remote..." -ForegroundColor Yellow
# scp destination: passing literal quotes as part of the path breaks because
# PowerShell quotes the whole arg again when forwarding to scp.exe, so scp
# sees `"path"` (with literal quotes) as the filename. Use forward slashes
# instead -- scp on Windows accepts them and they avoid local shell parsing.
$remoteDestPath = $RemoteStagingDir -replace '\\', '/'
$remoteDest = "${target}:$remoteDestPath"

$files = @(
    @{ Local = $CertPath;        Label = "cert" },
    @{ Local = $InstallScript;   Label = "install script" },
    @{ Local = $ValidateScript;  Label = "validate script" },
    @{ Local = $MsixPath;        Label = "MSIX" }
)
foreach ($f in $files) {
    $localScp = To-ScpLocalPath $f.Local
    $size = [math]::Round((Get-Item $f.Local).Length / 1MB, 1)
    Write-Host "  -> $($f.Label) (${size} MB)..." -ForegroundColor Gray
    & scp @scpArgs $localScp $remoteDest
    if ($LASTEXITCODE -ne 0) { throw "scp failed for $($f.Local)" }
}
Write-Host ""

# --- 5. Remote install ------------------------------------------------------

Write-Host "[3/5] Installing on remote..." -ForegroundColor Yellow
$remoteCertPath = Join-Path $RemoteStagingDir (Split-Path -Leaf $CertPath)
$remoteMsixPath = Join-Path $RemoteStagingDir (Split-Path -Leaf $MsixPath)
$remoteInstallScript = Join-Path $RemoteStagingDir (Split-Path -Leaf $InstallScript)

$installFlags = ""
if ($Machine)   { $installFlags += " -Machine" }
if ($LaunchApp) { $installFlags += " -LaunchApp" }

$installCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$remoteInstallScript`"" `
    + " -CertPath `"$remoteCertPath`" -MsixPath `"$remoteMsixPath`"$installFlags"

& ssh @sshArgs $target $installCmd
if ($LASTEXITCODE -ne 0) {
    Write-Host "Remote install failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host ""

# --- 6. Validate ------------------------------------------------------------

if ($SkipValidate) {
    Write-Host "[4/5] Skipping validation (-SkipValidate)" -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Host "[4/5] Validating remote deployment..." -ForegroundColor Yellow
    $remoteValidateScript = Join-Path $RemoteStagingDir (Split-Path -Leaf $ValidateScript)
    $validateCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$remoteValidateScript`""
    & ssh @sshArgs $target $validateCmd
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Validation reported failures (exit $LASTEXITCODE)." -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host ""
}

Write-Host "[5/5] Done." -ForegroundColor Yellow
Write-Host ""
Write-Host "=== Deploy complete ===" -ForegroundColor Cyan
Write-Host "Remote staging: $RemoteStagingDir" -ForegroundColor Gray
Write-Host "To clean up staging files later:" -ForegroundColor Gray
$cleanupSsh = "ssh"
if ($IdentityFile) { $cleanupSsh += " -i `"$IdentityFile`"" }
if ($ProxyJump)    { $cleanupSsh += " -J $ProxyJump" }
if ($Port -ne 22)  { $cleanupSsh += " -p $Port" }
Write-Host "  $cleanupSsh $target `"powershell.exe -NoProfile -Command Remove-Item -Recurse -Force '$RemoteStagingDir'`"" -ForegroundColor Gray

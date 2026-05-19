#!/usr/bin/env pwsh
<#!
.SYNOPSIS
  Run the Windows App Certification Kit (WACK) against a built MSIX package.

.DESCRIPTION
  WACK ships with the Windows SDK's "App Certification Kit" component and is
  required for Microsoft Store certification. This wraps the appcert.exe CLI so
  CI and local runs share the same invocation and exit-code semantics.

  Test execution time is ~5-15 minutes per package depending on which tests fire.
  Cross-arch validation (e.g. x64 host validating an arm64 MSIX) is supported
  for static checks — appcert auto-skips tests that need the package to launch.

  WACK requires a SIGNED MSIX whose signer matches Identity@Publisher in the
  appxmanifest. The release pipeline ships unsigned MSIX (Microsoft re-signs at
  Store submission), so this script:
    1. Reads the Publisher CN from the package's AppxManifest.xml
    2. Generates a temporary self-signed cert with that exact subject
    3. Trusts it under LocalMachine\TrustedPeople so sideloading works
    4. Signs a COPY of the input MSIX with signtool
    5. Runs WACK against the signed copy
    6. Removes the cert, trust entry, and signed copy on exit

  The original (unsigned) MSIX is left untouched so it can still be uploaded to
  the Store.

  Exit codes:
    0  - all tests passed
    >0 - one or more tests failed (see the report XML for which ones)

.PARAMETER MsixPath
  Path to the (unsigned) .msix package to validate.

.PARAMETER Arch
  Target architecture of the package: x64, x86, arm64. Passed to appcert via -arch.

.PARAMETER ReportPath
  Output path for the WACK XML report. Parent directory is created if missing.

.PARAMETER AppCertPath
  Override the appcert.exe location. Defaults to the standard SDK install path.

.EXAMPLE
  ./Run-Wack.ps1 -MsixPath ./msix/Easydict-v0.5.0-x64.msix -Arch x64 -ReportPath ./msix/wack-x64.xml
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$MsixPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet("x64", "x86", "arm64")]
    [string]$Arch,

    [Parameter(Mandatory = $true)]
    [string]$ReportPath,

    [string]$AppCertPath = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $MsixPath)) {
    throw "MSIX package not found: $MsixPath"
}
$msixAbs = (Resolve-Path $MsixPath).Path

# ---------------------------------------------------------------------------
# 1. Resolve required SDK tools.
# ---------------------------------------------------------------------------

if (-not $AppCertPath) {
    $candidate = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" -ErrorAction SilentlyContinue |
                 Select-Object -First 1
    if (-not $candidate) {
        $candidate = Get-ChildItem "C:\Program Files\Windows Kits\10\App Certification Kit\appcert.exe" -ErrorAction SilentlyContinue |
                     Select-Object -First 1
    }
    if (-not $candidate) {
        throw @"
appcert.exe not found. The Windows SDK 'App Certification Kit' component is required.
On GitHub Actions windows-latest runners this is typically pre-installed.
For local runs install it via Visual Studio Installer → Modify → Individual components → 'Windows App Certification Kit'.
"@
    }
    $AppCertPath = $candidate.FullName
}

# signtool.exe — pick the highest 10.x SDK to match the latest available signing flags.
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
if (-not $signtool) {
    throw "signtool.exe not found under Windows Kits\10\bin. Install the Windows SDK."
}

# makeappx.exe — used to extract AppxManifest.xml from the input MSIX.
$makeAppx = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\MakeAppx.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
if (-not $makeAppx) {
    throw "MakeAppx.exe not found under Windows Kits\10\bin. Install the Windows SDK."
}

Write-Host "[Run-Wack] appcert:   $AppCertPath"
Write-Host "[Run-Wack] signtool:  $($signtool.FullName)"
Write-Host "[Run-Wack] makeappx:  $($makeAppx.FullName)"
Write-Host "[Run-Wack] package:   $msixAbs"
Write-Host "[Run-Wack] arch:      $Arch"

# ---------------------------------------------------------------------------
# 2. Read Publisher CN from the manifest inside the MSIX.
# ---------------------------------------------------------------------------

$workDir = Join-Path $env:TEMP ("wack-" + [System.IO.Path]::GetRandomFileName().Replace(".", ""))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null
$extractDir = Join-Path $workDir "extracted"

Write-Host "[Run-Wack] Extracting manifest to read Publisher..."
& $makeAppx.FullName unpack /p $msixAbs /d $extractDir /o /nv | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx unpack failed (exit $LASTEXITCODE)"
}

$manifestPath = Join-Path $extractDir "AppxManifest.xml"
if (-not (Test-Path $manifestPath)) {
    throw "AppxManifest.xml missing inside MSIX"
}
[xml]$manifest = Get-Content $manifestPath -Raw
$publisher = $manifest.Package.Identity.Publisher
if (-not $publisher) {
    throw "Could not read Identity@Publisher from manifest"
}
Write-Host "[Run-Wack] Publisher:  $publisher"

# ---------------------------------------------------------------------------
# 3. Generate ephemeral self-signed cert + trust + sign a copy of the MSIX.
# ---------------------------------------------------------------------------

# 7 days is comfortably longer than any single CI run; cert is removed in the
# finally block anyway.
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $publisher `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -NotAfter (Get-Date).AddDays(7) `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

$pfxPassword = "wack-" + [Guid]::NewGuid().ToString("N").Substring(0, 12)
$pfxPath = Join-Path $workDir "wack-cert.pfx"
$pwd = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
Export-PfxCertificate -Cert "Cert:\LocalMachine\My\$($cert.Thumbprint)" -FilePath $pfxPath -Password $pwd | Out-Null

# TrustedPeople lets the local machine accept sideload-signed packages without
# Developer Mode toggled separately for the WACK install probe.
$trustedCert = Import-PfxCertificate -FilePath $pfxPath -Password $pwd -CertStoreLocation "Cert:\LocalMachine\TrustedPeople"

$signedMsix = Join-Path $workDir "wack-signed.msix"
Copy-Item $msixAbs $signedMsix -Force

try {
    Write-Host "[Run-Wack] Signing MSIX copy with ephemeral cert..."
    & $signtool.FullName sign /fd SHA256 /f $pfxPath /p $pfxPassword $signedMsix
    if ($LASTEXITCODE -ne 0) {
        throw "signtool sign failed (exit $LASTEXITCODE)"
    }

    # -----------------------------------------------------------------------
    # 4. Run WACK against the signed copy.
    # -----------------------------------------------------------------------

    $reportDir = Split-Path $ReportPath -Parent
    if ($reportDir -and -not (Test-Path $reportDir)) {
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    }
    $reportAbs = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $ReportPath))

    Write-Host "[Run-Wack] Resetting appcert state..."
    & $AppCertPath reset
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "appcert reset returned exit code $LASTEXITCODE (continuing)"
    }

    Write-Host "[Run-Wack] Running tests (this can take 5-15 minutes)..."
    & $AppCertPath test `
        -appxpackagepath $signedMsix `
        -reportoutputpath $reportAbs `
        -arch $Arch
    $script:wackExit = $LASTEXITCODE
    Write-Host "[Run-Wack] appcert exit code: $script:wackExit"

    # -----------------------------------------------------------------------
    # 5. Surface a human-readable summary so CI logs flag failures up-front.
    # -----------------------------------------------------------------------

    if (Test-Path $reportAbs) {
        try {
            [xml]$xml = Get-Content $reportAbs -Raw
            $overall = $xml.SelectSingleNode("//REPORT/@OVERALL_RESULT")
            if (-not $overall) {
                $overall = $xml.SelectSingleNode("//*[@OVERALL_RESULT]/@OVERALL_RESULT")
            }
            if ($overall) {
                Write-Host "[Run-Wack] Overall result: $($overall.Value)"
            }

            $failed = $xml.SelectNodes("//TEST[@RESULT='FAIL']")
            if ($failed -and $failed.Count -gt 0) {
                Write-Host ""
                Write-Host "Failed tests:"
                foreach ($t in $failed) {
                    $name = $t.GetAttribute("NAME")
                    $msg = $t.SelectSingleNode("MESSAGES/MESSAGE")
                    $msgText = if ($msg) { $msg.InnerText } else { "(no message)" }
                    Write-Host "  - $name"
                    Write-Host "      $msgText"
                }
            }
        } catch {
            Write-Warning "Failed to parse WACK report XML: $($_.Exception.Message)"
        }
    } else {
        Write-Warning "WACK report was not produced at $reportAbs"
    }
} finally {
    # -----------------------------------------------------------------------
    # 6. Cleanup: remove the trust entry, the cert from My, and the work dir.
    # Catch-and-warn — partial cleanup is fine; the temp paths and certs are
    # tied to this runner instance, not the released artifact.
    # -----------------------------------------------------------------------

    try {
        if ($trustedCert) {
            Remove-Item "Cert:\LocalMachine\TrustedPeople\$($trustedCert.Thumbprint)" -Force -ErrorAction SilentlyContinue
        }
        Remove-Item "Cert:\LocalMachine\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Warning "Failed to clean up WACK cert: $($_.Exception.Message)"
    }
    try {
        Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Warning "Failed to remove work dir: $($_.Exception.Message)"
    }
}

if ($script:wackExit -ne 0) {
    Write-Error "WACK validation failed (exit $script:wackExit). See $ReportPath for details."
    exit $script:wackExit
}

Write-Host "[Run-Wack] PASS"

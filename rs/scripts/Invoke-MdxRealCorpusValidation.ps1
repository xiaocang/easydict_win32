#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Runs optional real-corpus MDX/MDD validation for the Rust-native reader.

.DESCRIPTION
  Uses RS_MDICT_TEST_MDX and RS_MDICT_TEST_MDD when provided. If they are not
  set, tries the local Collins COBUILD English Usage corpus under the current
  user's Downloads directory. When no corpus is configured or discovered, this
  script exits successfully after printing a skip message.
#>

[CmdletBinding()]
param(
    [string]$MdxPath = $env:RS_MDICT_TEST_MDX,

    [string]$MddPath = $env:RS_MDICT_TEST_MDD,

    [string]$Query = $env:RS_MDICT_TEST_QUERY,

    [string]$MddResource = $env:RS_MDICT_TEST_MDD_RESOURCE,

    [string]$MddResourceMime = $env:RS_MDICT_TEST_MDD_RESOURCE_MIME,

    [int]$MddResourceMinBytes = 0
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Resolve-Path -LiteralPath (Join-Path $scriptDir "..\..")

$explicitMdxPath = -not [string]::IsNullOrWhiteSpace($MdxPath)
$explicitMddPath = -not [string]::IsNullOrWhiteSpace($MddPath)

if (-not $explicitMdxPath -or -not $explicitMddPath) {
    $downloads = if ([string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $null
    }
    else {
        Join-Path $env:USERPROFILE "Downloads\collins-cobuild-english-usage"
    }

    if ($null -ne $downloads) {
        if (-not $explicitMdxPath) {
            $candidate = Join-Path $downloads "Collins COBUILD English Usage.mdx"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $MdxPath = $candidate
            }
        }

        if (-not $explicitMddPath) {
            $candidate = Join-Path $downloads "Collins COBUILD English Usage.mdd"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $MddPath = $candidate
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($MdxPath) -or [string]::IsNullOrWhiteSpace($MddPath)) {
    Write-Host "Skipping real-corpus MDX/MDD validation; RS_MDICT_TEST_MDX/RS_MDICT_TEST_MDD are not configured and the local Collins corpus was not found."
    exit 0
}

if (-not (Test-Path -LiteralPath $MdxPath -PathType Leaf)) {
    throw "Configured real-corpus MDX path was not found: $MdxPath"
}

if (-not (Test-Path -LiteralPath $MddPath -PathType Leaf)) {
    throw "Configured real-corpus MDD path was not found: $MddPath"
}

if ([string]::IsNullOrWhiteSpace($Query)) {
    $Query = "ability"
}

if ([string]::IsNullOrWhiteSpace($MddResource)) {
    $MddResource = "\cceu.css"
}

if ([string]::IsNullOrWhiteSpace($MddResourceMime)) {
    $MddResourceMime = "text/css"
}

if ($MddResourceMinBytes -le 0) {
    $MddResourceMinBytes = 1
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Command
    )

    $program = $Command[0]
    $arguments = @($Command | Select-Object -Skip 1)
    Write-Host "Running real-corpus validation: $($Command -join ' ')"
    $global:LASTEXITCODE = 0
    & $program @arguments
    $commandSucceeded = $?
    $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
    if (-not $commandSucceeded -and $exitCode -eq 0) {
        $exitCode = 1
    }
    if ($exitCode -ne 0) {
        throw "Real-corpus validation failed with exit code $exitCode`: $($Command -join ' ')"
    }
}

$previousMdx = $env:RS_MDICT_TEST_MDX
$previousMdd = $env:RS_MDICT_TEST_MDD
$previousQuery = $env:RS_MDICT_TEST_QUERY
$previousMddResource = $env:RS_MDICT_TEST_MDD_RESOURCE
$previousMddResourceMime = $env:RS_MDICT_TEST_MDD_RESOURCE_MIME
$previousMddResourceMinBytes = $env:RS_MDICT_TEST_MDD_RESOURCE_MIN_BYTES

try {
    Set-Location $repoRoot
    $env:RS_MDICT_TEST_MDX = (Resolve-Path -LiteralPath $MdxPath).Path
    $env:RS_MDICT_TEST_MDD = (Resolve-Path -LiteralPath $MddPath).Path
    $env:RS_MDICT_TEST_QUERY = $Query
    $env:RS_MDICT_TEST_MDD_RESOURCE = $MddResource
    $env:RS_MDICT_TEST_MDD_RESOURCE_MIME = $MddResourceMime
    $env:RS_MDICT_TEST_MDD_RESOURCE_MIN_BYTES = [string]$MddResourceMinBytes

    Invoke-Checked @(
        "cargo", "test",
        "--manifest-path", "lib\rs-mdict\Cargo.toml",
        "--features", "real-corpus-tests",
        "--test", "integration_test",
        "test_mdx_lookup_primary_query",
        "--", "--exact", "--nocapture"
    )
    Invoke-Checked @(
        "cargo", "test",
        "--manifest-path", "lib\rs-mdict\Cargo.toml",
        "--features", "real-corpus-tests",
        "--test", "integration_test",
        "test_mdd_locate_configured_resource",
        "--", "--exact", "--nocapture"
    )
    Invoke-Checked @(
        "cargo", "test",
        "--manifest-path", "rs\Cargo.toml",
        "-p", "easydict_app",
        "--test", "mdx_native_behavior",
        "real_corpus",
        "--", "--nocapture"
    )
}
finally {
    if ($null -eq $previousMdx) { Remove-Item Env:RS_MDICT_TEST_MDX -ErrorAction SilentlyContinue } else { $env:RS_MDICT_TEST_MDX = $previousMdx }
    if ($null -eq $previousMdd) { Remove-Item Env:RS_MDICT_TEST_MDD -ErrorAction SilentlyContinue } else { $env:RS_MDICT_TEST_MDD = $previousMdd }
    if ($null -eq $previousQuery) { Remove-Item Env:RS_MDICT_TEST_QUERY -ErrorAction SilentlyContinue } else { $env:RS_MDICT_TEST_QUERY = $previousQuery }
    if ($null -eq $previousMddResource) { Remove-Item Env:RS_MDICT_TEST_MDD_RESOURCE -ErrorAction SilentlyContinue } else { $env:RS_MDICT_TEST_MDD_RESOURCE = $previousMddResource }
    if ($null -eq $previousMddResourceMime) { Remove-Item Env:RS_MDICT_TEST_MDD_RESOURCE_MIME -ErrorAction SilentlyContinue } else { $env:RS_MDICT_TEST_MDD_RESOURCE_MIME = $previousMddResourceMime }
    if ($null -eq $previousMddResourceMinBytes) { Remove-Item Env:RS_MDICT_TEST_MDD_RESOURCE_MIN_BYTES -ErrorAction SilentlyContinue } else { $env:RS_MDICT_TEST_MDD_RESOURCE_MIN_BYTES = $previousMddResourceMinBytes }
}

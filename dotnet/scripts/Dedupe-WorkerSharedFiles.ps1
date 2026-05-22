#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Moves identical worker DLLs into workers/shared for MSIX packaging.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir
)

$ErrorActionPreference = "Stop"

$workersDir = Join-Path $PublishDir "workers"
if (-not (Test-Path $workersDir)) {
    Write-Host "[DedupeWorkerShared] No workers directory found: $workersDir"
    exit 0
}

$workerDirs = @("longdoc", "localai", "ocr") |
    ForEach-Object { Join-Path $workersDir $_ } |
    Where-Object { Test-Path $_ }

if ($workerDirs.Count -lt 2) {
    Write-Host "[DedupeWorkerShared] Fewer than two worker dirs found; skipping."
    exit 0
}

$allowList = @(
    "Microsoft.Windows.SDK.NET.dll",
    "WinRT.Runtime.dll",
    "Microsoft.Windows.UI.Xaml.dll",
    "Microsoft.WinUI.dll",
    "Microsoft.InteractiveExperiences.Projection.dll",
    "Microsoft.Web.WebView2.Core.Projection.dll"
)

$sharedDir = Join-Path $workersDir "shared"
New-Item -ItemType Directory -Path $sharedDir -Force | Out-Null

function Get-Sha256([string]$Path) {
    (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$movedCount = 0
$savedBytes = 0L

foreach ($fileName in $allowList) {
    $matches = @()
    foreach ($dir in $workerDirs) {
        $candidate = Join-Path $dir $fileName
        if (Test-Path $candidate) {
            $matches += (Get-Item $candidate)
        }
    }

    if ($matches.Count -lt 2) {
        continue
    }

    $hashes = $matches | ForEach-Object { Get-Sha256 $_.FullName } | Select-Object -Unique
    if ($hashes.Count -ne 1) {
        Write-Host "[DedupeWorkerShared] Skipping $fileName because hashes differ."
        continue
    }

    $sharedPath = Join-Path $sharedDir $fileName
    Copy-Item -LiteralPath $matches[0].FullName -Destination $sharedPath -Force

    foreach ($match in $matches) {
        Remove-Item -LiteralPath $match.FullName -Force
    }

    $movedCount++
    $savedBytes += [Math]::Max(0, $matches.Count - 1) * $matches[0].Length
    Write-Host "[DedupeWorkerShared] Shared $fileName from $($matches.Count) workers."
}

Write-Host "[DedupeWorkerShared] Moved $movedCount shared files; estimated uncompressed savings: $([Math]::Round($savedBytes / 1MB, 1)) MB"

Write-Host "[DedupeWorkerShared] Worker size summary:"
Get-ChildItem $workersDir -Directory | Sort-Object Name | ForEach-Object {
    $bytes = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("  {0,-8} {1,8} MB" -f $_.Name, [Math]::Round($bytes / 1MB, 1))
}

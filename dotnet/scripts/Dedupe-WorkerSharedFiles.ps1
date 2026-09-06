#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Reuses identical host DLLs, or moves identical worker DLLs into workers/shared.
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

if ($workerDirs.Count -eq 0) {
    Write-Host "[DedupeWorkerShared] No worker dirs found; skipping."
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
    $sharedPath = Join-Path $sharedDir $fileName
    # LocalAI's generated C#/WinRT module initializer runs before Main installs
    # the shared resolver. Its WinRT bootstrap assembly must remain app-local.
    $localAiDir = Join-Path $workersDir "localai"
    if ($fileName -eq "WinRT.Runtime.dll" -and (Test-Path -LiteralPath $localAiDir)) {
        $bootstrapPath = Join-Path $localAiDir $fileName
        if (-not (Test-Path -LiteralPath $bootstrapPath) -and (Test-Path -LiteralPath $sharedPath)) {
            # Repair the layout produced by the earlier worker-only dedupe.
            Copy-Item -LiteralPath $sharedPath -Destination $bootstrapPath
            $savedBytes -= (Get-Item -LiteralPath $bootstrapPath).Length
        }
    }

    $matches = @()
    foreach ($dir in $workerDirs) {
        if ($fileName -eq "WinRT.Runtime.dll" -and $dir -eq $localAiDir) {
            continue
        }
        $candidate = Join-Path $dir $fileName
        if (Test-Path $candidate) {
            $matches += (Get-Item $candidate)
        }
    }

    # Include an earlier dedupe output so rerunning on a staged package is safe.
    if (Test-Path -LiteralPath $sharedPath) {
        $matches += Get-Item -LiteralPath $sharedPath
    }

    if ($matches.Count -eq 0) {
        continue
    }

    $hashes = @($matches | ForEach-Object { Get-Sha256 $_.FullName } | Select-Object -Unique)
    if ($hashes.Count -ne 1) {
        Write-Host "[DedupeWorkerShared] Skipping $fileName because hashes differ."
        continue
    }

    $hostPath = Join-Path $PublishDir $fileName
    if ((Test-Path -LiteralPath $hostPath) -and (Get-Sha256 $hostPath) -eq $hashes[0]) {
        foreach ($match in $matches) {
            $savedBytes += $match.Length
            Remove-Item -LiteralPath $match.FullName -Force
        }
        $movedCount++
        Write-Host "[DedupeWorkerShared] Reused host $fileName; removed $($matches.Count) identical copies."
        continue
    }

    # A different host version must not replace worker assemblies. Keep the
    # original worker-only sharing layout and let workers prefer it at runtime.
    if ($matches.Count -lt 2) {
        continue
    }
    if (-not (Test-Path -LiteralPath $sharedPath)) {
        Copy-Item -LiteralPath $matches[0].FullName -Destination $sharedPath
    }

    foreach ($match in $matches) {
        if ($match.FullName -ne [System.IO.Path]::GetFullPath($sharedPath)) {
            Remove-Item -LiteralPath $match.FullName -Force
        }
    }

    $movedCount++
    $savedBytes += [Math]::Max(0, $matches.Count - 1) * $matches[0].Length
    Write-Host "[DedupeWorkerShared] Shared $fileName from $($matches.Count) workers."
}

Write-Host "[DedupeWorkerShared] Deduplicated $movedCount assemblies; estimated uncompressed savings: $([Math]::Round($savedBytes / 1MB, 1)) MB"

Write-Host "[DedupeWorkerShared] Worker size summary:"
Get-ChildItem $workersDir -Directory | Sort-Object Name | ForEach-Object {
    $bytes = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("  {0,-8} {1,8} MB" -f $_.Name, [Math]::Round($bytes / 1MB, 1))
}

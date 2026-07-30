#!/usr/bin/env pwsh

param(
    [Parameter(Mandatory = $true)]
    [string]$BundlePath,

    [Parameter(Mandatory = $true)]
    [string]$X64SymbolsPath,

    [Parameter(Mandatory = $true)]
    [string]$Arm64SymbolsPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-RelativeFilePath([string]$rootPath, [string]$filePath) {
    $normalizedRoot = [System.IO.Path]::GetFullPath($rootPath).TrimEnd("\", "/")
    $rootUri = New-Object System.Uri("$normalizedRoot$([System.IO.Path]::DirectorySeparatorChar)")
    $fileUri = New-Object System.Uri([System.IO.Path]::GetFullPath($filePath))
    return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($fileUri).ToString()).Replace("/", [System.IO.Path]::DirectorySeparatorChar)
}

function Get-SymbolFiles([string]$rootPath, [string]$architecture) {
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) {
        throw "$architecture symbol root not found: $rootPath"
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $rootPath).Path
    $pdbs = @(Get-ChildItem -LiteralPath $resolvedRoot -Filter "*.pdb" -File -Recurse)
    if ($pdbs.Count -eq 0) {
        throw "$architecture symbol root contains no PDBs: $resolvedRoot"
    }
    if (-not ($pdbs | Where-Object { $_.Name -eq "Easydict.WinUI.pdb" })) {
        throw "$architecture symbol root lacks Easydict.WinUI.pdb: $resolvedRoot"
    }

    return [pscustomobject]@{
        Root = $resolvedRoot
        Files = $pdbs
    }
}

function Copy-SymbolFiles($symbolSet, [string]$destinationRoot, [string]$architecture) {
    $architectureRoot = Join-Path $destinationRoot $architecture
    foreach ($pdb in $symbolSet.Files) {
        $relativePath = Get-RelativeFilePath $symbolSet.Root $pdb.FullName
        $destination = Join-Path $architectureRoot $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $pdb.FullName -Destination $destination -Force
    }
}

if (-not (Test-Path -LiteralPath $BundlePath -PathType Leaf)) {
    throw "Bundle not found: $BundlePath"
}

$bundle = (Resolve-Path -LiteralPath $BundlePath).Path
$x64Symbols = Get-SymbolFiles $X64SymbolsPath "x64"
$arm64Symbols = Get-SymbolFiles $Arm64SymbolsPath "arm64"
$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$stageRoot = Join-Path $tempRoot "Easydict-store-upload-$([guid]::NewGuid().ToString('N'))"
$symbolsRoot = Join-Path $stageRoot "symbols"
$appxSymPath = Join-Path $stageRoot "Easydict.appxsym"
$uploadRoot = Join-Path $stageRoot "upload"

try {
    New-Item -ItemType Directory -Force -Path $symbolsRoot | Out-Null
    Copy-SymbolFiles $x64Symbols $symbolsRoot "x64"
    Copy-SymbolFiles $arm64Symbols $symbolsRoot "arm64"

    [System.IO.Compression.ZipFile]::CreateFromDirectory($symbolsRoot, $appxSymPath)

    $appxSymArchive = [System.IO.Compression.ZipFile]::OpenRead($appxSymPath)
    try {
        $symbolEntries = @($appxSymArchive.Entries)
        if ($symbolEntries.Count -eq 0 -or ($symbolEntries | Where-Object {
            -not $_.FullName.EndsWith(".pdb", [System.StringComparison]::OrdinalIgnoreCase)
        })) {
            throw "Easydict.appxsym must contain only PDB entries"
        }

        foreach ($architecture in @("x64", "arm64")) {
            if (-not ($symbolEntries | Where-Object {
                $_.FullName.Replace("\", "/") -match "^$architecture/(?:.*/)?Easydict\.WinUI\.pdb$"
            })) {
                throw "Easydict.appxsym lacks $architecture/Easydict.WinUI.pdb"
            }
        }
    } finally {
        $appxSymArchive.Dispose()
    }

    New-Item -ItemType Directory -Force -Path $uploadRoot | Out-Null
    $bundleName = [System.IO.Path]::GetFileName($bundle)
    Copy-Item -LiteralPath $bundle -Destination (Join-Path $uploadRoot $bundleName) -Force
    Copy-Item -LiteralPath $appxSymPath -Destination (Join-Path $uploadRoot "Easydict.appxsym") -Force

    if (Test-Path -LiteralPath $OutputPath) {
        Remove-Item -LiteralPath $OutputPath -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($uploadRoot, $OutputPath)

    $uploadArchive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $OutputPath))
    try {
        $expectedEntries = @($bundleName, "Easydict.appxsym")
        $actualEntries = @($uploadArchive.Entries | ForEach-Object { $_.FullName })
        if ($actualEntries.Count -ne 2 `
            -or @($actualEntries | Where-Object { $_ -notin $expectedEntries }).Count -ne 0 `
            -or @($expectedEntries | Where-Object { $_ -notin $actualEntries }).Count -ne 0) {
            throw "MSIX upload must contain exactly $bundleName and Easydict.appxsym"
        }
    } finally {
        $uploadArchive.Dispose()
    }

    Write-Host "[StoreUpload] Created $OutputPath"
} finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}

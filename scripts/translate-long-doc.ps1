param(
    [string]$InputFile,

    [string]$TargetLanguage,

    [string]$EnvFile,
    [string]$SourceLanguage = "auto",
    [string]$OutputFile,
    [string]$ResultJsonPath,
    [string]$ServiceId,
    [string]$OutputMode,
    [string]$Layout,
    [string]$PdfExportMode,
    [int]$Page,
    [string]$PageRange,
    [int]$MaxConcurrency,
    [string]$VisionEndpoint,
    [string]$VisionApiKey,
    [string]$VisionModel,
    [string]$AppDir,
    [string]$RustHelperPath,
    [switch]$UseCargo,
    [switch]$UseDotnetLegacy,
    [switch]$ListServices
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$rsRoot = Join-Path $repoRoot "rs"
$scriptParameters = $PSBoundParameters

function Test-Provided {
    param([string]$Name)

    if (-not $scriptParameters.ContainsKey($Name)) {
        return $false
    }

    $value = $scriptParameters[$Name]
    if ($null -eq $value) {
        return $false
    }

    if ($value -is [string]) {
        return $value.Length -gt 0
    }

    return $true
}

function Assert-RequestArguments {
    if ($ListServices) {
        return
    }

    if (-not $InputFile) {
        throw "InputFile is required unless -ListServices is used."
    }

    if (-not $TargetLanguage) {
        throw "TargetLanguage is required unless -ListServices is used."
    }

    if ($scriptParameters.ContainsKey("Page") -and $PageRange) {
        throw "Use either -Page or -PageRange, not both."
    }

    if ($scriptParameters.ContainsKey("Page") -and $Page -lt 1) {
        throw "Page must be >= 1."
    }

    if ($scriptParameters.ContainsKey("MaxConcurrency") -and $MaxConcurrency -lt 1) {
        throw "MaxConcurrency must be >= 1."
    }
}

function New-RustLongDocArguments {
    $longDocArguments = @()

    if ($ListServices) {
        $longDocArguments += "--list-services"
        if ($AppDir) {
            $longDocArguments += @("--app-dir", $AppDir)
        }

        return $longDocArguments
    }

    $longDocArguments += @("--input", $InputFile, "--target-language", $TargetLanguage)

    if ($SourceLanguage) { $longDocArguments += @("--from", $SourceLanguage) }
    if ($EnvFile) { $longDocArguments += @("--env-file", $EnvFile) }
    if ($OutputFile) { $longDocArguments += @("--output", $OutputFile) }
    if ($ResultJsonPath) { $longDocArguments += @("--result-json", $ResultJsonPath) }
    if ($ServiceId) { $longDocArguments += @("--service", $ServiceId) }
    if ($OutputMode) { $longDocArguments += @("--output-mode", $OutputMode) }
    if ($Layout) { $longDocArguments += @("--layout", $Layout) }
    if ($PdfExportMode) { $longDocArguments += @("--pdf-export-mode", $PdfExportMode) }
    if ($scriptParameters.ContainsKey("Page")) { $longDocArguments += @("--page", $Page) }
    if ($PageRange) { $longDocArguments += @("--page-range", $PageRange) }
    if ($scriptParameters.ContainsKey("MaxConcurrency")) { $longDocArguments += @("--max-concurrency", $MaxConcurrency) }
    if ($VisionEndpoint) { $longDocArguments += @("--vision-endpoint", $VisionEndpoint) }
    if ($VisionApiKey) { $longDocArguments += @("--vision-api-key", $VisionApiKey) }
    if ($VisionModel) { $longDocArguments += @("--vision-model", $VisionModel) }
    if ($AppDir) { $longDocArguments += @("--app-dir", $AppDir) }

    return $longDocArguments
}

function Resolve-RustHelper {
    if ($RustHelperPath) {
        if (-not (Test-Path -LiteralPath $RustHelperPath -PathType Leaf)) {
            throw "Rust helper not found at '$RustHelperPath'."
        }

        return (Resolve-Path -LiteralPath $RustHelperPath).Path
    }

    $candidatePaths = @()
    if ($AppDir) {
        $candidatePaths += Join-Path $AppDir "easydict_long_doc.exe"
    }

    $candidatePaths += Join-Path $rsRoot "target\debug\easydict_long_doc.exe"
    $candidatePaths += Join-Path $rsRoot "target\release\easydict_long_doc.exe"

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidatePath).Path
        }
    }

    $pathCommand = Get-Command "easydict_long_doc.exe" -ErrorAction SilentlyContinue
    if ($pathCommand) {
        return $pathCommand.Source
    }

    return $null
}

function Invoke-RustHelper {
    param([string[]]$LongDocArguments)

    $helperPath = Resolve-RustHelper
    if ($helperPath) {
        & $helperPath @LongDocArguments
        exit $LASTEXITCODE
    }

    if (-not (Test-Path -LiteralPath (Join-Path $rsRoot "Cargo.toml") -PathType Leaf)) {
        throw "Could not find easydict_long_doc.exe. Pass -RustHelperPath, pass -AppDir, place it on PATH, or run from a source checkout with rs/Cargo.toml for cargo development mode."
    }

    Invoke-RustCargo -LongDocArguments $LongDocArguments
}

function Invoke-RustCargo {
    param([string[]]$LongDocArguments)

    $cargoArguments = @(
        "run",
        "-p", "easydict_app",
        "--bin", "easydict_long_doc",
        "--"
    ) + $LongDocArguments

    Push-Location $rsRoot
    try {
        & cargo @cargoArguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    exit $exitCode
}

if ($UseDotnetLegacy) {
    throw "-UseDotnetLegacy has been retired. scripts/translate-long-doc.ps1 is Rust-only; use -UseCargo for source checkout development mode, or pass -RustHelperPath/-AppDir to select easydict_long_doc.exe."
}

Assert-RequestArguments

$rustArguments = New-RustLongDocArguments
if ($UseCargo) {
    Invoke-RustCargo -LongDocArguments $rustArguments
}

Invoke-RustHelper -LongDocArguments $rustArguments

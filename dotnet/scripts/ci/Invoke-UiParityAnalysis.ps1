[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ScreenshotRoot,

    [string]$OutputDir,

    [string]$CargoManifestPath,

    [string]$AnalyzerPackage = "easydict_ui_parity_analyzer",

    [switch]$FailOnThreshold,

    [string[]]$ScoreGate = @(),

    [switch]$UseDefaultScoreGates,

    [double]$MinCoveragePercent = -1,

    [double]$MinCriticalCoveragePercent = -1,

    [switch]$FailOnCriticalCoverageMissing,

    [switch]$RequireManifest,

    [switch]$ManifestOnly,

    [switch]$SkipSelfTest,

    [int]$MaxSummaryLines = 160
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ScreenshotRoot)) {
    Write-Host "UI parity analysis skipped: screenshot root does not exist: $ScreenshotRoot"
    return
}

$resolvedRoot = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $resolvedRoot "ui-parity"
}

if ([string]::IsNullOrWhiteSpace($CargoManifestPath)) {
    $CargoManifestPath = Join-Path $PSScriptRoot "..\..\..\rs\Cargo.toml"
}

$resolvedCargoManifest = (Resolve-Path -LiteralPath $CargoManifestPath).Path

if (-not $SkipSelfTest) {
    Write-Host "Running UI parity analyzer self-test."
    $selfTestArguments = @(
        "run",
        "--manifest-path",
        $resolvedCargoManifest,
        "-p",
        $AnalyzerPackage,
        "--",
        "--self-test"
    )
    & cargo @selfTestArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$referenceSuffix = "-dotnet-winui-reference.png"
$candidateSuffix = "-rust-win-fluent-iced.png"
$manifestPath = Join-Path $resolvedRoot "ui-parity-manifest.json"
$hasManifest = Test-Path -LiteralPath $manifestPath

if ($RequireManifest -and -not $hasManifest) {
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ""
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "## UI parity analysis"
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ""
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value 'Failed: `-RequireManifest` was set but `ui-parity-manifest.json` was not found.'
    }
    Write-Host "UI parity analysis failed: -RequireManifest was set but ui-parity-manifest.json was not found under $resolvedRoot"
    exit 1
}

$pairs = @(
    Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Filter "*$referenceSuffix" -File |
        Where-Object {
            $scenario = $_.Name.Substring(0, $_.Name.Length - $referenceSuffix.Length)
            $candidate = Join-Path $_.DirectoryName "$scenario$candidateSuffix"
            Test-Path -LiteralPath $candidate
        }
)

if ($pairs.Count -eq 0 -and -not $hasManifest) {
    Write-Host "UI parity analysis skipped: no dotnet/rust screenshot pairs or manifest found under $resolvedRoot"
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ""
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "## UI parity analysis"
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ""
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "Skipped: no dotnet/rust screenshot pairs or ui-parity-manifest.json found in this shard."
    }
    return
}

if ($hasManifest) {
    Write-Host "Running UI parity analysis from manifest: $manifestPath"
} else {
    Write-Host "Running UI parity analysis for $($pairs.Count) screenshot pair(s)."
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$arguments = @(
    "run",
    "--manifest-path",
    $resolvedCargoManifest,
    "-p",
    $AnalyzerPackage,
    "--",
    "--screenshot-root",
    $resolvedRoot,
    "--output-dir",
    $OutputDir
)

if ($hasManifest) {
    $arguments += @("--manifest", $manifestPath)
}

if ($FailOnThreshold) {
    $arguments += "--fail-on-threshold"
}

$defaultScoreGates = @(
    "final_effect/main.*=90,78",
    "final_effect/settings.*=78,62",
    "final_effect/mini.*=85,70",
    "final_effect/fixed.*=85,70",
    "final_effect/long-doc.*=82,68",
    "final_effect/effects.*=80,66",
    "iced_backend/ocr.*=80,65",
    "window_runtime/popbutton.*=80,65"
)

$effectiveScoreGates = @()
if ($UseDefaultScoreGates) {
    $effectiveScoreGates += $defaultScoreGates
}
$effectiveScoreGates += $ScoreGate

foreach ($gate in $effectiveScoreGates) {
    if (-not [string]::IsNullOrWhiteSpace($gate)) {
        $arguments += @("--score-gate", $gate)
    }
}

if ($MinCoveragePercent -ge 0) {
    $arguments += @("--min-coverage", $MinCoveragePercent.ToString([System.Globalization.CultureInfo]::InvariantCulture))
}

if ($MinCriticalCoveragePercent -ge 0) {
    $arguments += @("--min-critical-coverage", $MinCriticalCoveragePercent.ToString([System.Globalization.CultureInfo]::InvariantCulture))
}

if ($FailOnCriticalCoverageMissing) {
    $arguments += "--fail-on-critical-coverage-missing"
}

if ($RequireManifest) {
    $arguments += "--require-manifest"
}

if ($ManifestOnly) {
    $arguments += "--manifest-only"
}

& cargo @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

function Add-MarkdownFileToStepSummary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [int]$MaxLines,

        [string]$TruncationMessage = "_Report truncated in job summary; download the screenshot artifact for the full parity report._"
    )

    if (-not (Test-Path -LiteralPath $Path) -or [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        return
    }

    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ""
    $reportLines = Get-Content -LiteralPath $Path
    if ($reportLines.Count -gt $MaxLines) {
        $reportLines = $reportLines[0..($MaxLines - 1)] + "" + $TruncationMessage
    }

    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $reportLines
}

$reportPath = Join-Path $OutputDir "ui-parity-report.md"
Add-MarkdownFileToStepSummary -Path $reportPath -MaxLines $MaxSummaryLines

$coveragePath = Join-Path $OutputDir "ui-parity-coverage.md"
Add-MarkdownFileToStepSummary `
    -Path $coveragePath `
    -MaxLines ([Math]::Max(40, [int]($MaxSummaryLines / 2))) `
    -TruncationMessage "_Coverage report truncated in job summary; download the screenshot artifact for the full parity coverage matrix._"

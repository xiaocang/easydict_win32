[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$ReferenceRoot,
    [string]$Executable,
    [switch]$Build,
    [switch]$SkipBuild,
    [switch]$RunAnalyzer,
    [switch]$UseDefaultScoreGates,
    [switch]$SkipAnalyzerSelfTest,
    [ValidateSet("all", "base", "hover", "pressed", "mouse-hover")]
    [string[]]$State = @("all"),
    [int]$SettlingMilliseconds = 1800,
    [int]$InterScenarioDelayMilliseconds = 500,
    [string]$Theme = "system",
    [string]$UiLanguage = "zh-CN",
    [double]$MaxSurfaceDeltaRgb = 3.0,
    [double]$MaxBoundsDriftDips = 0.5,
    [switch]$FailOnSurfaceDrift
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$rsRoot = Resolve-Path (Join-Path $scriptRoot "..")
$repoRoot = Resolve-Path (Join-Path $rsRoot "..")
$matrixScript = Join-Path $scriptRoot "Capture-PreviewParityMatrix.ps1"
$measureScript = Join-Path $scriptRoot "Measure-SettingsServicesExpanderColors.ps1"

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputRoot = Join-Path $repoRoot "artifacts\ui-screenshots\services-expanded-bar-states-$timestamp"
}

$OutputRoot = (New-Item -ItemType Directory -Force -Path $OutputRoot).FullName
$catalogRoot = Join-Path $OutputRoot "_scenario-catalog"
New-Item -ItemType Directory -Force -Path $catalogRoot | Out-Null

$catalogParams = @{
    OutputRoot = $catalogRoot
    ListScenarios = $true
    Matrix = @("settings")
    Theme = $Theme
    UiLanguage = $UiLanguage
}
& $matrixScript @catalogParams | Out-Host

$catalogPath = Join-Path $catalogRoot "rust-preview-parity-scenarios.json"
if (-not (Test-Path -LiteralPath $catalogPath)) {
    throw "Scenario catalog was not generated: $catalogPath"
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$wantedStates = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($value in @($State)) {
    if ($value -eq "all") {
        foreach ($stateName in @("base", "hover", "pressed", "mouse-hover")) {
            $wantedStates.Add($stateName) | Out-Null
        }
    } else {
        $wantedStates.Add($value) | Out-Null
    }
}

function Get-SettingsServiceExpandedState {
    param(
        [string]$ScenarioId
    )

    if ($ScenarioId -match '-bar-mouse-hover$') {
        return "mouse-hover"
    }
    if ($ScenarioId -match '-bar-hover$') {
        return "hover"
    }
    if ($ScenarioId -match '-bar-pressed$') {
        return "pressed"
    }
    return "base"
}

$scenarios = [string[]]@(
    $catalog.scenarios |
        Where-Object {
            $_.ScenarioId -match '^parity-settings-services-.+-expanded-.+' -and
                $wantedStates.Contains((Get-SettingsServiceExpandedState -ScenarioId ([string]$_.ScenarioId)))
        } |
        Sort-Object ScenarioId |
        ForEach-Object { [string]$_.ScenarioId }
)

if ($scenarios.Count -eq 0) {
    throw "No Settings Services expanded scenarios matched states: $($State -join ', ')"
}

Write-Host "Selected $($scenarios.Count) Settings Services expanded scenario(s): $($State -join ', ')"

$captureParams = @{
    OutputRoot = $OutputRoot
    Scenario = $scenarios
    Theme = $Theme
    UiLanguage = $UiLanguage
    SettlingMilliseconds = $SettlingMilliseconds
    InterScenarioDelayMilliseconds = $InterScenarioDelayMilliseconds
}
if (-not [string]::IsNullOrWhiteSpace($ReferenceRoot)) {
    $captureParams["ReferenceRoot"] = $ReferenceRoot
}
if (-not [string]::IsNullOrWhiteSpace($Executable)) {
    $captureParams["Executable"] = $Executable
}
if ($Build) {
    $captureParams["Build"] = $true
}
if ($SkipBuild) {
    $captureParams["SkipBuild"] = $true
}
if ($RunAnalyzer) {
    $captureParams["RunAnalyzer"] = $true
}
if ($UseDefaultScoreGates) {
    $captureParams["UseDefaultScoreGates"] = $true
}
if ($SkipAnalyzerSelfTest) {
    $captureParams["SkipAnalyzerSelfTest"] = $true
}

& $matrixScript @captureParams
if (-not $?) {
    exit 1
}

$measureParams = @{
    ArtifactRoot = $OutputRoot
    MaxSurfaceDeltaRgb = $MaxSurfaceDeltaRgb
    MaxBoundsDriftDips = $MaxBoundsDriftDips
}
if ($FailOnSurfaceDrift) {
    $measureParams["FailOnSurfaceDrift"] = $true
}

& $measureScript @measureParams
if (-not $?) {
    exit 1
}

Write-Host "Settings Services expanded parity artifacts: $OutputRoot"

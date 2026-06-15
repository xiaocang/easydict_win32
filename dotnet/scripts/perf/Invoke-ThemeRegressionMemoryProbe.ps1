[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$Configuration = "Debug",
    [string]$TestFilter = "FullyQualifiedName~ThemeContrastTests.ThemeMatrix_LightAndDarkAppThemes_OnLightAndDarkWindowsThemes_ShouldCaptureNamedScreenshots",
    [string]$AppExePath,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$workspaceRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspaceRoot "artifacts\theme-regression-memory"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDirectory = Join-Path $OutputDirectory $stamp
$screenshotsRoot = Join-Path $runDirectory "screenshots"
$resultsDirectory = Join-Path $runDirectory "test-results"
$summaryPath = Join-Path $runDirectory "theme-memory-summary.json"
$trxPath = Join-Path $resultsDirectory "theme-regression-memory.trx"

New-Item -ItemType Directory -Force -Path $screenshotsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $resultsDirectory | Out-Null

$env:SCREENSHOT_OUTPUT_DIR = $screenshotsRoot
$env:EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE = "1"

$RustOnlyMsBuildProperties = @(
    "-p:RuntimeProfile=rust-only",
    "-p:BuildWorkerOutputs=false",
    "-p:EnableInProcLongDocFallback=false"
)

if (-not [string]::IsNullOrWhiteSpace($AppExePath)) {
    $resolvedAppExe = Resolve-Path $AppExePath
    $env:EASYDICT_EXE_PATH = $resolvedAppExe.Path
    $env:EASYDICT_UIA_ALLOW_EXE_FALLBACK = "1"
}

$testProject = Join-Path $workspaceRoot "tests\Easydict.UIAutomation.Tests\Easydict.UIAutomation.Tests.csproj"
$dotnetArgs = @(
    "test",
    $testProject,
    "-c",
    $Configuration,
    "--filter",
    $TestFilter,
    "--logger",
    "trx;LogFileName=$(Split-Path -Leaf $trxPath)",
    "--results-directory",
    $resultsDirectory
)
$dotnetArgs += $RustOnlyMsBuildProperties

if ($NoBuild) {
    $dotnetArgs += "--no-build"
}

Write-Host "Running theme regression memory probe"
Write-Host "Run directory: $runDirectory"
Write-Host "Screenshot root: $screenshotsRoot"

& dotnet @dotnetArgs
$testExitCode = $LASTEXITCODE

$matrixDirectory = Join-Path $screenshotsRoot "theme-contrast-regression\theme-matrix"
$memoryCsv = Join-Path $matrixDirectory "theme-contrast_memory.csv"
$screenshotCount = 0
if (Test-Path -LiteralPath $matrixDirectory) {
    $screenshotCount = @(Get-ChildItem -LiteralPath $matrixDirectory -Filter "theme-contrast_*.png" -ErrorAction SilentlyContinue).Count
}

$rows = @()
if (Test-Path -LiteralPath $memoryCsv) {
    $rows = @(Import-Csv -LiteralPath $memoryCsv)
}

$cases = @($rows |
    Group-Object case |
    ForEach-Object {
        $samples = @($_.Group)
        if ($samples.Count -eq 0) {
            return
        }

        $first = $samples[0]
        $last = $samples[$samples.Count - 1]
        $workingSets = @($samples | ForEach-Object { [double]::Parse($_.workingSetMb, [Globalization.CultureInfo]::InvariantCulture) })
        $privates = @($samples | ForEach-Object { [double]::Parse($_.privateMb, [Globalization.CultureInfo]::InvariantCulture) })
        $firstWs = [double]::Parse($first.workingSetMb, [Globalization.CultureInfo]::InvariantCulture)
        $lastWs = [double]::Parse($last.workingSetMb, [Globalization.CultureInfo]::InvariantCulture)
        $firstPrivate = [double]::Parse($first.privateMb, [Globalization.CultureInfo]::InvariantCulture)
        $lastPrivate = [double]::Parse($last.privateMb, [Globalization.CultureInfo]::InvariantCulture)

        [pscustomobject]@{
            case = $_.Name
            sampleCount = $samples.Count
            firstMarker = $first.marker
            lastMarker = $last.marker
            firstWorkingSetMb = [Math]::Round($firstWs, 1)
            lastWorkingSetMb = [Math]::Round($lastWs, 1)
            peakWorkingSetMb = [Math]::Round(($workingSets | Measure-Object -Maximum).Maximum, 1)
            deltaWorkingSetMb = [Math]::Round($lastWs - $firstWs, 1)
            firstPrivateMb = [Math]::Round($firstPrivate, 1)
            lastPrivateMb = [Math]::Round($lastPrivate, 1)
            peakPrivateMb = [Math]::Round(($privates | Measure-Object -Maximum).Maximum, 1)
            deltaPrivateMb = [Math]::Round($lastPrivate - $firstPrivate, 1)
        }
    } |
    Sort-Object case)

[pscustomobject]@{
    testFilter = $TestFilter
    screenshotRoot = $screenshotsRoot
    matrixDirectory = $matrixDirectory
    screenshotCount = $screenshotCount
    memoryCsv = if (Test-Path -LiteralPath $memoryCsv) { $memoryCsv } else { $null }
    sampleCount = $rows.Count
    cases = $cases
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host "Summary: $summaryPath"
if ($cases.Count -gt 0) {
    $cases | Format-Table -AutoSize | Out-String | Write-Host
}
else {
    Write-Host "No theme memory samples found."
}

exit $testExitCode

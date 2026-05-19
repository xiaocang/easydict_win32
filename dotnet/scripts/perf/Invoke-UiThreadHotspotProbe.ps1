[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [int]$ThresholdMs = 4,
    [string]$Configuration = "Debug",
    [string]$TestFilter = "FullyQualifiedName~UiThreadHotspotProbeTests.MainSettingsModesAndFloatingWindows_ShouldEmitUiHotspots",
    [string]$AppExePath,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$workspaceRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspaceRoot "artifacts\ui-thread-hotspots"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logPath = Join-Path $OutputDirectory "ui-hotspots-$stamp.log"
$summaryPath = Join-Path $OutputDirectory "ui-hotspot-summary-$stamp.json"
$trxPath = Join-Path $OutputDirectory "ui-hotspot-$stamp.trx"

$env:EASYDICT_DEBUG_UI_THREAD_HOTSPOTS = "1"
$env:EASYDICT_DEBUG_UI_THREAD_HOTSPOT_THRESHOLD_MS = [string]$ThresholdMs
$env:EASYDICT_UI_THREAD_HOTSPOT_LOG_PATH = $logPath
$env:EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE = "1"

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
    $OutputDirectory
)

if ($NoBuild) {
    $dotnetArgs += "--no-build"
}

Write-Host "Running UI hotspot probe with threshold ${ThresholdMs}ms"
Write-Host "Hotspot log: $logPath"

& dotnet @dotnetArgs
$testExitCode = $LASTEXITCODE

# Give the app-side background log writer a brief chance to flush after UIA closes the app.
Start-Sleep -Milliseconds 750

$events = @()
if (Test-Path -LiteralPath $logPath) {
    $events = Get-Content -LiteralPath $logPath |
        ForEach-Object {
            if ($_ -match "\[UIHotspot\]\s+kind=duration\s+op=([^\s]+)\s+elapsedMs=([0-9.]+)") {
                [pscustomobject]@{
                    operation = $matches[1]
                    elapsedMs = [double]::Parse($matches[2], [Globalization.CultureInfo]::InvariantCulture)
                    raw = $_
                }
            }
        }
}

$summary = @($events |
    Group-Object operation |
    ForEach-Object {
        $values = @($_.Group | ForEach-Object { $_.elapsedMs })
        [pscustomobject]@{
            operation = $_.Name
            count = $_.Count
            maxMs = [Math]::Round(($values | Measure-Object -Maximum).Maximum, 2)
            avgMs = [Math]::Round(($values | Measure-Object -Average).Average, 2)
            totalMs = [Math]::Round(($values | Measure-Object -Sum).Sum, 2)
        }
    } |
    Sort-Object -Property maxMs -Descending)

[pscustomobject]@{
    thresholdMs = $ThresholdMs
    logPath = $logPath
    testFilter = $TestFilter
    eventCount = @($events).Count
    operations = $summary
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host "Summary: $summaryPath"
if ($summary.Count -gt 0) {
    $summary | Format-Table -AutoSize | Out-String | Write-Host
}
else {
    Write-Host "No [UIHotspot] duration events met the threshold."
}

exit $testExitCode

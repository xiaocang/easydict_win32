param(
    [string]$AppExePath = "dotnet/publish/x64/Easydict.WinUI.exe",
    [string]$TestProject = "dotnet/tests/Easydict.UIAutomation.Tests/Easydict.UIAutomation.Tests.csproj",
    [string]$Configuration = "Release",
    [string]$TestFilter = "FullyQualifiedName~Easydict.UIAutomation.Tests.Tests.MemoryGateTests.PrMemoryGate_LightweightWindowAndSelectionScenario",
    [string]$OutputDir = "artifacts/memory-gate/pr",
    [int]$InitialIdleSeconds = 30,
    [int]$PostCloseIdleSeconds = 15,
    [double]$ThresholdPercent = 10,
    [string]$DiagnosticsToolVersion = "8.*",
    [switch]$SkipBuild,
    [switch]$SkipToolInstall,
    [switch]$RunRealTranslation
)

$ErrorActionPreference = "Stop"

function Get-FullPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function New-Directory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Install-DotnetTool([string]$PackageName, [string]$ToolPath, [string]$ToolDir) {
    if (Test-Path -LiteralPath $ToolPath) {
        return
    }

    if ($SkipToolInstall) {
        throw "Required tool '$PackageName' was not found at '$ToolPath' and -SkipToolInstall was specified."
    }

    & dotnet tool install $PackageName --tool-path $ToolDir --version $DiagnosticsToolVersion
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install dotnet tool '$PackageName'."
    }
}

function Wait-TargetProcess([string]$ProcessName, [string]$ExpectedPath, [int]$TimeoutSeconds, $GuardProcess) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $processes = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
        if ($ExpectedPath) {
            $processes = @($processes | Where-Object {
                try {
                    $_.Path -and ([string]::Equals($_.Path, $ExpectedPath, [StringComparison]::OrdinalIgnoreCase))
                }
                catch {
                    $false
                }
            })
        }

        if ($processes.Count -gt 0) {
            return $processes | Sort-Object StartTime -Descending | Select-Object -First 1
        }

        if ($null -ne $GuardProcess -and $GuardProcess.HasExited) {
            throw "Scenario process exited before '$ProcessName' was observable."
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for process '$ProcessName'."
}

function Wait-File([string]$Path, [int]$TimeoutSeconds, $GuardProcess) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            return $true
        }

        if ($null -ne $GuardProcess -and $GuardProcess.HasExited) {
            return $false
        }

        Start-Sleep -Milliseconds 250
    }

    return $false
}

function Get-ProcessCounterInstance([int]$ProcessId) {
    $counter = Get-Counter "\Process(*)\ID Process"
    foreach ($sample in $counter.CounterSamples) {
        if ([int]$sample.CookedValue -ne $ProcessId) {
            continue
        }

        if ($sample.Path -match "\\Process\((?<name>.+)\)\\ID Process$") {
            return $Matches["name"]
        }
    }

    throw "Could not resolve performance counter instance for process $ProcessId."
}

function Stop-IfRunning($Process) {
    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            $Process.WaitForExit(5000)
        }
    }
    catch {
        Write-Warning "Failed to stop process $($Process.Id): $($_.Exception.Message)"
    }
}

function Get-CsvColumn($Rows, [string]$Suffix) {
    if ($Rows.Count -eq 0) {
        return $null
    }

    return $Rows[0].PSObject.Properties.Name |
        Where-Object { $_ -like "*$Suffix" } |
        Select-Object -First 1
}

function Convert-ToDouble([object]$Value) {
    $text = [string]$Value
    $number = 0.0
    if ([double]::TryParse(
        $text,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$number)) {
        return $number
    }

    return $null
}

function Get-NumericSeries($Rows, [string]$ColumnName) {
    $values = New-Object System.Collections.Generic.List[double]
    foreach ($row in $Rows) {
        $value = Convert-ToDouble $row.$ColumnName
        if ($null -ne $value) {
            $values.Add($value)
        }
    }

    return $values.ToArray()
}

function Get-SampleAt([double[]]$Values, [int]$Index) {
    if ($Values.Count -eq 0) {
        return $null
    }

    if ($Index -lt 0) {
        $Index = 0
    }

    if ($Index -ge $Values.Count) {
        $Index = $Values.Count - 1
    }

    return $Values[$Index]
}

function Get-TailAverage([double[]]$Values, [int]$Count) {
    if ($Values.Count -eq 0) {
        return $null
    }

    $start = [Math]::Max(0, $Values.Count - $Count)
    $sum = 0.0
    $actual = 0
    for ($i = $start; $i -lt $Values.Count; $i++) {
        $sum += $Values[$i]
        $actual++
    }

    return $sum / [Math]::Max(1, $actual)
}

function Get-TailSlope([double[]]$Values, [int]$Count) {
    if ($Values.Count -lt 2) {
        return 0.0
    }

    $start = [Math]::Max(0, $Values.Count - $Count)
    $n = $Values.Count - $start
    if ($n -lt 2) {
        return 0.0
    }

    $sumX = 0.0
    $sumY = 0.0
    $sumXY = 0.0
    $sumX2 = 0.0
    for ($i = 0; $i -lt $n; $i++) {
        $x = [double]$i
        $y = $Values[$start + $i]
        $sumX += $x
        $sumY += $y
        $sumXY += $x * $y
        $sumX2 += $x * $x
    }

    $denominator = ($n * $sumX2) - ($sumX * $sumX)
    if ([Math]::Abs($denominator) -lt 0.000001) {
        return 0.0
    }

    return (($n * $sumXY) - ($sumX * $sumY)) / $denominator
}

function Read-DotnetCounterValues([string]$JsonPath) {
    if (-not (Test-Path -LiteralPath $JsonPath)) {
        return @()
    }

    $text = Get-Content -LiteralPath $JsonPath -Raw
    $values = New-Object System.Collections.Generic.List[double]
    $patterns = @(
        '"(?:Name|CounterName|DisplayName)"\s*:\s*"[^"]*(?:GC Heap Size|gc-heap-size)[^"]*".{0,800}?"(?:Mean|Value|CounterValue)"\s*:\s*(?<value>-?\d+(?:\.\d+)?)',
        '"(?:Mean|Value|CounterValue)"\s*:\s*(?<value>-?\d+(?:\.\d+)?).{0,800}?"(?:Name|CounterName|DisplayName)"\s*:\s*"[^"]*(?:GC Heap Size|gc-heap-size)[^"]*"'
    )

    foreach ($pattern in $patterns) {
        foreach ($match in [regex]::Matches($text, $pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
            $value = Convert-ToDouble $match.Groups["value"].Value
            if ($null -ne $value) {
                $values.Add($value)
            }
        }
    }

    return $values.ToArray()
}

function Invoke-Gcdump([string]$GcdumpTool, [int]$ProcessId, [string]$DumpPath, [string]$ReportPath, [string]$LogPath) {
    $target = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $target) {
        Write-Warning "Skipping gcdump '$DumpPath' because process $ProcessId is no longer running."
        return $false
    }

    & $GcdumpTool collect -p $ProcessId -o $DumpPath *>&1 | Tee-Object -FilePath $LogPath
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "dotnet-gcdump collect failed for '$DumpPath'."
        return $false
    }

    & $GcdumpTool report $DumpPath *>&1 | Tee-Object -FilePath $ReportPath
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "dotnet-gcdump report failed for '$DumpPath'."
        return $false
    }

    return $true
}

$OutputDir = Get-FullPath $OutputDir
$ToolDir = Join-Path $OutputDir ".tools"
New-Directory $OutputDir
New-Directory $ToolDir

$AppExePath = Get-FullPath $AppExePath
$TestProject = Get-FullPath $TestProject
if (-not (Test-Path -LiteralPath $AppExePath)) {
    throw "App executable not found: $AppExePath"
}

if (-not (Test-Path -LiteralPath $TestProject)) {
    throw "Test project not found: $TestProject"
}

$dotnetCounters = Join-Path $ToolDir "dotnet-counters.exe"
$dotnetGcdump = Join-Path $ToolDir "dotnet-gcdump.exe"
Install-DotnetTool "dotnet-counters" $dotnetCounters $ToolDir
Install-DotnetTool "dotnet-gcdump" $dotnetGcdump $ToolDir

if (-not $SkipBuild) {
    & dotnet build $TestProject -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "UIAutomation test build failed."
    }
}

$env:EASYDICT_EXE_PATH = $AppExePath
$env:EASYDICT_UIA_ALLOW_EXE_FALLBACK = "1"
$env:EASYDICT_MEMORY_GATE_INITIAL_IDLE_SECONDS = [string]$InitialIdleSeconds
$env:EASYDICT_MEMORY_GATE_POST_CLOSE_IDLE_SECONDS = [string]$PostCloseIdleSeconds
$env:EASYDICT_MEMORY_GATE_RUN_TRANSLATION = if ($RunRealTranslation) { "1" } else { "0" }
$env:EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE = "1"

$typeperfCsv = Join-Path $OutputDir "typeperf.csv"
$counterJson = Join-Path $OutputDir "dotnet-counters.json"
$markerDir = Join-Path $OutputDir "markers"
$closedMarker = Join-Path $markerDir "main-window-closed.marker"
$releaseMarker = Join-Path $markerDir "release.marker"
$baselineGcdump = Join-Path $OutputDir "baseline.gcdump"
$baselineHeapstat = Join-Path $OutputDir "baseline.heapstat.txt"
$finalGcdump = Join-Path $OutputDir "final.gcdump"
$finalHeapstat = Join-Path $OutputDir "final.heapstat.txt"
$testOut = Join-Path $OutputDir "dotnet-test.out.log"
$testErr = Join-Path $OutputDir "dotnet-test.err.log"
New-Directory $markerDir
Remove-Item -LiteralPath $closedMarker, $releaseMarker -Force -ErrorAction SilentlyContinue
$env:EASYDICT_MEMORY_GATE_CLOSED_MARKER_PATH = $closedMarker
$env:EASYDICT_MEMORY_GATE_RELEASE_MARKER_PATH = $releaseMarker

$testArgs = @(
    "test", $TestProject,
    "-c", $Configuration,
    "--no-build",
    "--filter", $TestFilter,
    "--results-directory", $OutputDir,
    "--logger", "trx;LogFileName=memory-gate.trx",
    "--logger", "console;verbosity=detailed",
    "-p:Platform=x64"
)

Write-Host "Starting memory gate scenario..."
$testProcess = Start-Process -FilePath "dotnet" -ArgumentList $testArgs -RedirectStandardOutput $testOut -RedirectStandardError $testErr -PassThru -WindowStyle Hidden
$appProcess = Wait-TargetProcess "Easydict.WinUI" $AppExePath 120 $testProcess
$processId = $appProcess.Id
$instance = Get-ProcessCounterInstance $processId
Write-Host "Monitoring process $processId ($instance)"

$typeperfCounters = @(
    "\Process($instance)\Private Bytes",
    "\Process($instance)\Handle Count",
    "\Process($instance)\Working Set",
    "\Process($instance)\Thread Count"
)
$quotedCounters = $typeperfCounters | ForEach-Object { '"' + $_ + '"' }
$typeperfArgs = ($quotedCounters + @("-si", "1", "-f", "CSV", "-o", '"' + $typeperfCsv + '"')) -join " "
$typeperfProcess = Start-Process -FilePath "typeperf.exe" -ArgumentList $typeperfArgs -RedirectStandardOutput (Join-Path $OutputDir "typeperf.out.log") -RedirectStandardError (Join-Path $OutputDir "typeperf.err.log") -PassThru -WindowStyle Hidden

$counterArgs = "collect --process-id $processId --counters System.Runtime --format json --output `"$counterJson`""
$counterProcess = Start-Process -FilePath $dotnetCounters -ArgumentList $counterArgs -RedirectStandardOutput (Join-Path $OutputDir "dotnet-counters.out.log") -RedirectStandardError (Join-Path $OutputDir "dotnet-counters.err.log") -PassThru -WindowStyle Hidden

Start-Sleep -Seconds ([Math]::Max(0, $InitialIdleSeconds))
Invoke-Gcdump $dotnetGcdump $processId $baselineGcdump $baselineHeapstat (Join-Path $OutputDir "baseline.gcdump.log") | Out-Null

$closedObserved = Wait-File $closedMarker ([Math]::Max(30, $InitialIdleSeconds + $PostCloseIdleSeconds + 120)) $testProcess
if ($closedObserved) {
    try {
        Invoke-Gcdump $dotnetGcdump $processId $finalGcdump $finalHeapstat (Join-Path $OutputDir "final.gcdump.log") | Out-Null
    }
    finally {
        Set-Content -LiteralPath $releaseMarker -Value (Get-Date).ToUniversalTime().ToString("O")
    }
}
else {
    Write-Warning "Main-window closed marker was not observed before the scenario exited or timed out."
}

$testProcess.WaitForExit()
$testExitCode = $testProcess.ExitCode

if (-not $closedObserved -and -not (Test-Path -LiteralPath $finalGcdump)) {
    Invoke-Gcdump $dotnetGcdump $processId $finalGcdump $finalHeapstat (Join-Path $OutputDir "final.gcdump.log") | Out-Null
}

Stop-IfRunning $counterProcess
Stop-IfRunning $typeperfProcess

if ($testExitCode -ne 0) {
    throw "Memory gate UIAutomation scenario failed with exit code $testExitCode. See '$testOut' and '$testErr'."
}

if (-not (Test-Path -LiteralPath $typeperfCsv)) {
    throw "typeperf did not produce '$typeperfCsv'."
}

$rows = @(Import-Csv -LiteralPath $typeperfCsv)
$privateColumn = Get-CsvColumn $rows "\Private Bytes"
$handleColumn = Get-CsvColumn $rows "\Handle Count"
$workingSetColumn = Get-CsvColumn $rows "\Working Set"
if (-not $privateColumn -or -not $handleColumn) {
    throw "typeperf output is missing Private Bytes or Handle Count columns."
}

$privateBytes = Get-NumericSeries $rows $privateColumn
$handles = Get-NumericSeries $rows $handleColumn
$workingSet = if ($workingSetColumn) { Get-NumericSeries $rows $workingSetColumn } else { @() }
$baselineIndex = [Math]::Min([Math]::Max(0, $InitialIdleSeconds), [Math]::Max(0, $privateBytes.Count - 1))
$baselinePrivate = Get-SampleAt $privateBytes $baselineIndex
$finalPrivate = Get-TailAverage $privateBytes 5
$baselineHandles = Get-SampleAt $handles $baselineIndex
$finalHandles = Get-TailAverage $handles 5
$handleTailSlope = Get-TailSlope $handles 10
$baselineWorkingSet = Get-SampleAt $workingSet $baselineIndex
$finalWorkingSet = Get-TailAverage $workingSet 5

$gcHeap = Read-DotnetCounterValues $counterJson
$baselineGcHeap = Get-SampleAt $gcHeap $baselineIndex
$finalGcHeap = Get-TailAverage $gcHeap 5

$failures = New-Object System.Collections.Generic.List[string]
$privateLimit = $baselinePrivate * (1.0 + ($ThresholdPercent / 100.0))
if ($finalPrivate -gt $privateLimit) {
    $failures.Add(("Private Bytes exceeded threshold: baseline={0:N0}, final={1:N0}, limit={2:N0}" -f $baselinePrivate, $finalPrivate, $privateLimit))
}

if ($handleTailSlope -gt 0.25 -and $finalHandles -gt $baselineHandles) {
    $failures.Add(("Handle Count is still growing: baseline={0:N1}, final={1:N1}, tailSlope={2:N3} handles/sample" -f $baselineHandles, $finalHandles, $handleTailSlope))
}

if ($null -ne $baselineGcHeap -and $null -ne $finalGcHeap -and $gcHeap.Count -gt 0) {
    $gcLimit = $baselineGcHeap * (1.0 + ($ThresholdPercent / 100.0))
    if ($finalGcHeap -gt $gcLimit) {
        $failures.Add(("GC Heap Size exceeded threshold: baseline={0:N2}, final={1:N2}, limit={2:N2}" -f $baselineGcHeap, $finalGcHeap, $gcLimit))
    }
}
else {
    Write-Warning "Could not parse GC Heap Size from dotnet-counters JSON; threshold check skipped for that metric."
}

$summary = [pscustomobject]@{
    scenario = "pr-memory-gate"
    processId = $processId
    thresholdPercent = $ThresholdPercent
    sampleCount = $privateBytes.Count
    baselineIndex = $baselineIndex
    privateBytes = [pscustomobject]@{
        baseline = $baselinePrivate
        finalTailAverage = $finalPrivate
        limit = $privateLimit
    }
    handleCount = [pscustomobject]@{
        baseline = $baselineHandles
        finalTailAverage = $finalHandles
        tailSlope = $handleTailSlope
    }
    workingSet = [pscustomobject]@{
        baseline = $baselineWorkingSet
        finalTailAverage = $finalWorkingSet
    }
    gcHeapSize = [pscustomobject]@{
        parsedSamples = $gcHeap.Count
        baseline = $baselineGcHeap
        finalTailAverage = $finalGcHeap
    }
    artifacts = [pscustomobject]@{
        typeperfCsv = $typeperfCsv
        dotnetCountersJson = $counterJson
        baselineGcdump = $baselineGcdump
        baselineHeapstat = $baselineHeapstat
        finalGcdump = $finalGcdump
        finalHeapstat = $finalHeapstat
    }
    failures = $failures.ToArray()
}

$summaryPath = Join-Path $OutputDir "summary.json"
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host "Memory gate summary written to $summaryPath"

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure
    }
    throw "Memory gate failed."
}

Write-Host "Memory gate passed."

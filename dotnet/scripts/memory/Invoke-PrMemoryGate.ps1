param(
    [string]$AppExePath = "dotnet/publish/x64/Easydict.WinUI.exe",
    [string]$TestProject = "dotnet/tests/Easydict.UIAutomation.Tests/Easydict.UIAutomation.Tests.csproj",
    [string]$Configuration = "Release",
    [string]$TestFilter = "FullyQualifiedName~Easydict.UIAutomation.Tests.Tests.MemoryGateTests.PrMemoryGate_LightweightWindowAndSelectionScenario",
    [string]$OutputDir = "artifacts/memory-gate/pr",
    [int]$InitialIdleSeconds = 30,
    [int]$PostCloseIdleSeconds = 15,
    [double]$ThresholdPercent = 10,
    [int]$PrivateBytesAbsoluteAllowanceMB = 160,
    [int]$ManagedHeapAbsoluteAllowanceMB = 16,
    [int]$GcHeapAbsoluteAllowanceMB = 16,
    [int]$HandleCountPostCloseGrowthAllowance = 8,
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
            $Process.WaitForExit(5000) | Out-Null
        }
    }
    catch {
        Write-Warning "Failed to stop process $($Process.Id): $($_.Exception.Message)"
    }
}

function Normalize-TypeperfCounters([object]$Counters) {
    $items = @($Counters)
    if ($items.Count -eq 1 -and $items[0] -is [string] -and $items[0] -match "\s+\\Process\(") {
        $items = [regex]::Split([string]$items[0], "\s+(?=\\Process\()") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }

    return [string[]]$items
}

function Start-TypeperfJob([string[]]$Counters, [string]$CsvPath, [string]$OutLogPath, [string]$ErrLogPath) {
    Remove-Item -LiteralPath $CsvPath -Force -ErrorAction SilentlyContinue
    $counterList = Normalize-TypeperfCounters $Counters
    $countersJson = ConvertTo-Json -InputObject @($counterList) -Compress

    return Start-Job -ScriptBlock {
        param(
            [string]$CountersJson,
            [string]$CsvPath,
            [string]$OutLogPath,
            [string]$ErrLogPath
        )

        $parsedCounters = ConvertFrom-Json $CountersJson
        $Counters = [string[]]@($parsedCounters)
        if ($Counters.Count -eq 1 -and $Counters[0] -match "\s+\\Process\(") {
            $Counters = [string[]]([regex]::Split($Counters[0], "\s+(?=\\Process\()") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        }

        $quotedCounters = $Counters | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
        $quotedCsvPath = '"' + $CsvPath.Replace('"', '\"') + '"'
        $command = (@("typeperf.exe") + $quotedCounters + @("-si", "1", "-f", "CSV", "-y", "-o", $quotedCsvPath)) -join " "
        Set-Content -LiteralPath $OutLogPath -Value "COMMAND: $command"
        cmd.exe /d /c $command >> $OutLogPath 2> $ErrLogPath
    } -ArgumentList $countersJson, $CsvPath, $OutLogPath, $ErrLogPath
}

function Stop-JobIfRunning($Job) {
    if ($null -eq $Job) {
        return
    }

    try {
        if ($Job.State -eq "Running") {
            Stop-Job -Job $Job -ErrorAction SilentlyContinue
        }

        Receive-Job -Job $Job -ErrorAction SilentlyContinue | Out-Null
    }
    finally {
        Remove-Job -Job $Job -Force -ErrorAction SilentlyContinue
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

function Get-SeriesFromIndex([double[]]$Values, [int]$StartIndex) {
    if ($Values.Count -eq 0) {
        return @()
    }

    $start = [Math]::Max(0, [Math]::Min($StartIndex, $Values.Count - 1))
    $items = New-Object System.Collections.Generic.List[double]
    for ($i = $start; $i -lt $Values.Count; $i++) {
        $items.Add($Values[$i])
    }

    return $items.ToArray()
}

function Test-GcHeapSizeCounterName([string]$Name) {
    return $Name -match "^(GC Heap Size(?: \(MB\))?|gc-heap-size)$"
}

function Read-DotnetCounterValues([string]$JsonPath) {
    if (-not (Test-Path -LiteralPath $JsonPath)) {
        return @()
    }

    $text = Get-Content -LiteralPath $JsonPath -Raw
    $values = New-Object System.Collections.Generic.List[double]
    try {
        $json = ConvertFrom-Json $text
        foreach ($event in @($json.Events)) {
            if ($null -eq $event) {
                continue
            }

            $name = [string]$event.name
            if ([string]::IsNullOrWhiteSpace($name)) {
                $name = [string]$event.Name
            }

            if (-not (Test-GcHeapSizeCounterName $name)) {
                continue
            }

            $rawValue = if ($null -ne $event.value) { $event.value } else { $event.Value }
            $value = Convert-ToDouble $rawValue
            if ($null -ne $value) {
                $values.Add($value)
            }
        }
    }
    catch {
        $values.Clear()
    }

    if ($values.Count -gt 0) {
        return $values.ToArray()
    }

    $patterns = @(
        '\{[^{}]*"(?:Name|name|CounterName|counterName|DisplayName|displayName)"\s*:\s*"(?<name>[^"]+)"[^{}]*"(?:Mean|mean|Value|value|CounterValue|counterValue)"\s*:\s*(?<value>-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)',
        '\{[^{}]*"(?:Mean|mean|Value|value|CounterValue|counterValue)"\s*:\s*(?<value>-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)[^{}]*"(?:Name|name|CounterName|counterName|DisplayName|displayName)"\s*:\s*"(?<name>[^"]+)"'
    )

    foreach ($pattern in $patterns) {
        $options = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
        foreach ($match in [regex]::Matches($text, $pattern, $options)) {
            if (-not (Test-GcHeapSizeCounterName $match.Groups["name"].Value)) {
                continue
            }

            $value = Convert-ToDouble $match.Groups["value"].Value
            if ($null -ne $value) {
                $values.Add($value)
            }
        }
    }

    return $values.ToArray()
}

function Read-GcdumpHeapBytes([string]$ReportPath) {
    if (-not (Test-Path -LiteralPath $ReportPath)) {
        return $null
    }

    foreach ($line in Get-Content -LiteralPath $ReportPath) {
        if ($line -match "^\s*(?<bytes>[\d,]+)\s+GC Heap bytes\s*$") {
            $text = $Matches["bytes"].Replace(",", "")
            $value = 0.0
            if ([double]::TryParse(
                $text,
                [System.Globalization.NumberStyles]::Float,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [ref]$value)) {
                return $value
            }
        }
    }

    return $null
}

function Convert-TypeperfTimestampToUtc([object]$Value) {
    $text = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $styles = [System.Globalization.DateTimeStyles]::AssumeLocal
    $formats = @(
        "MM/dd/yyyy HH:mm:ss.fff",
        "M/d/yyyy H:mm:ss.fff",
        "MM/dd/yyyy HH:mm:ss",
        "M/d/yyyy H:mm:ss"
    )

    $dateTime = [DateTime]::MinValue
    foreach ($format in $formats) {
        if ([DateTime]::TryParseExact($text, $format, $culture, $styles, [ref]$dateTime)) {
            return ([DateTimeOffset]$dateTime).ToUniversalTime()
        }
    }

    if ([DateTime]::TryParse($text, $culture, $styles, [ref]$dateTime)) {
        return ([DateTimeOffset]$dateTime).ToUniversalTime()
    }

    return $null
}

function Read-PhaseMarkerUtc([string]$Path) {
    $text = ""
    try {
        $text = (Get-Content -LiteralPath $Path -Raw).Trim()
    }
    catch {
        $text = ""
    }

    $timestamp = [DateTimeOffset]::MinValue
    if (-not [string]::IsNullOrWhiteSpace($text) -and
        [DateTimeOffset]::TryParse(
            $text,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$timestamp)) {
        return $timestamp.ToUniversalTime()
    }

    return [DateTimeOffset]::new((Get-Item -LiteralPath $Path).LastWriteTimeUtc)
}

function Get-CsvTimeColumn($Rows) {
    if ($Rows.Count -eq 0) {
        return $null
    }

    return $Rows[0].PSObject.Properties.Name | Select-Object -First 1
}

function Get-RowDouble($Row, [string]$ColumnName) {
    if ([string]::IsNullOrWhiteSpace($ColumnName)) {
        return $null
    }

    $property = $Row.PSObject.Properties[$ColumnName]
    if ($null -eq $property) {
        return $null
    }

    return Convert-ToDouble $property.Value
}

function Get-NearestTypeperfRowIndex($Rows, [string]$TimeColumn, [DateTimeOffset]$TargetUtc) {
    if ($Rows.Count -eq 0 -or [string]::IsNullOrWhiteSpace($TimeColumn)) {
        return $null
    }

    $bestIndex = $null
    $bestDeltaMs = [double]::MaxValue
    for ($i = 0; $i -lt $Rows.Count; $i++) {
        $sampleUtc = Convert-TypeperfTimestampToUtc $Rows[$i].$TimeColumn
        if ($null -eq $sampleUtc) {
            continue
        }

        $deltaMs = [Math]::Abs(($sampleUtc.UtcDateTime - $TargetUtc.UtcDateTime).TotalMilliseconds)
        if ($deltaMs -lt $bestDeltaMs) {
            $bestDeltaMs = $deltaMs
            $bestIndex = $i
        }
    }

    return $bestIndex
}

function New-PhaseSnapshots(
    [string]$PhaseDir,
    $Rows,
    [string]$TimeColumn,
    [string]$PrivateColumn,
    [string]$WorkingSetColumn,
    [string]$HandleColumn,
    [string]$ThreadColumn) {

    $snapshots = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $PhaseDir)) {
        return $snapshots.ToArray()
    }

    $phaseFiles = @(Get-ChildItem -LiteralPath $PhaseDir -Filter "*.marker" -ErrorAction SilentlyContinue |
        Sort-Object Name)
    if ($phaseFiles.Count -eq 0) {
        return $snapshots.ToArray()
    }

    $firstPrivate = $null
    $previousPrivate = $null
    $firstWorkingSet = $null
    $previousWorkingSet = $null
    foreach ($file in $phaseFiles) {
        $markerUtc = Read-PhaseMarkerUtc $file.FullName
        $sampleIndex = Get-NearestTypeperfRowIndex $Rows $TimeColumn $markerUtc
        $sampleUtc = $null
        $privateBytes = $null
        $workingSet = $null
        $handleCount = $null
        $threadCount = $null

        if ($null -ne $sampleIndex) {
            $row = $Rows[$sampleIndex]
            $sampleUtc = Convert-TypeperfTimestampToUtc $row.$TimeColumn
            $privateBytes = Get-RowDouble $row $PrivateColumn
            $workingSet = Get-RowDouble $row $WorkingSetColumn
            $handleCount = Get-RowDouble $row $HandleColumn
            $threadCount = Get-RowDouble $row $ThreadColumn
        }

        if ($null -eq $firstPrivate -and $null -ne $privateBytes) {
            $firstPrivate = $privateBytes
        }

        if ($null -eq $firstWorkingSet -and $null -ne $workingSet) {
            $firstWorkingSet = $workingSet
        }

        $privateDeltaFromPrevious = if ($null -ne $previousPrivate -and $null -ne $privateBytes) {
            $privateBytes - $previousPrivate
        }
        else {
            $null
        }

        $privateDeltaFromFirst = if ($null -ne $firstPrivate -and $null -ne $privateBytes) {
            $privateBytes - $firstPrivate
        }
        else {
            $null
        }

        $workingSetDeltaFromPrevious = if ($null -ne $previousWorkingSet -and $null -ne $workingSet) {
            $workingSet - $previousWorkingSet
        }
        else {
            $null
        }

        $workingSetDeltaFromFirst = if ($null -ne $firstWorkingSet -and $null -ne $workingSet) {
            $workingSet - $firstWorkingSet
        }
        else {
            $null
        }

        $snapshots.Add([pscustomobject]@{
            phase = $file.BaseName
            markerUtc = $markerUtc.ToString("O")
            sampleIndex = $sampleIndex
            sampleUtc = if ($null -ne $sampleUtc) { $sampleUtc.ToString("O") } else { $null }
            privateBytes = $privateBytes
            privateBytesDeltaFromPrevious = $privateDeltaFromPrevious
            privateBytesDeltaFromFirstPhase = $privateDeltaFromFirst
            workingSet = $workingSet
            workingSetDeltaFromPrevious = $workingSetDeltaFromPrevious
            workingSetDeltaFromFirstPhase = $workingSetDeltaFromFirst
            handleCount = $handleCount
            threadCount = $threadCount
        })

        if ($null -ne $privateBytes) {
            $previousPrivate = $privateBytes
        }

        if ($null -ne $workingSet) {
            $previousWorkingSet = $workingSet
        }
    }

    return $snapshots.ToArray()
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

function Write-LogTail([string]$Path, [int]$Count) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Write-Host "----- tail: $Path -----"
    Get-Content -LiteralPath $Path -Tail $Count | Write-Host
}

$OutputDir = Get-FullPath $OutputDir
$ToolDir = Join-Path $OutputDir ".tools"
New-Directory $OutputDir
New-Directory $ToolDir

$AppExePath = Get-FullPath $AppExePath
$TestProject = Get-FullPath $TestProject
if (-not (Test-Path -LiteralPath $AppExePath)) {
    Write-Host "::error title=PR Memory Gate::App executable not found at $AppExePath"
    throw "App executable not found: $AppExePath"
}

if (-not (Test-Path -LiteralPath $TestProject)) {
    Write-Host "::error title=PR Memory Gate::Test project not found at $TestProject"
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
$env:EASYDICT_UIA_MEMORY_AB_MODE = "B"

$typeperfCsv = Join-Path $OutputDir "typeperf.csv"
$counterJson = Join-Path $OutputDir "dotnet-counters.json"
$markerDir = Join-Path $OutputDir "markers"
$phaseDir = Join-Path $markerDir "phases"
$processIdMarker = Join-Path $markerDir "process-id.marker"
$closedMarker = Join-Path $markerDir "main-window-closed.marker"
$releaseMarker = Join-Path $markerDir "release.marker"
$postCloseIdleMarker = Join-Path $phaseDir "19-post-close-idle-complete.marker"
$baselineGcdump = Join-Path $OutputDir "baseline.gcdump"
$baselineHeapstat = Join-Path $OutputDir "baseline.heapstat.txt"
$finalGcdump = Join-Path $OutputDir "final.gcdump"
$finalHeapstat = Join-Path $OutputDir "final.heapstat.txt"
$phaseSnapshotsPath = Join-Path $OutputDir "phase-snapshots.json"
$testOut = Join-Path $OutputDir "dotnet-test.out.log"
$testErr = Join-Path $OutputDir "dotnet-test.err.log"
New-Directory $markerDir
New-Directory $phaseDir
Remove-Item -LiteralPath $processIdMarker, $closedMarker, $releaseMarker -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $phaseDir -Filter "*.marker" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
$env:EASYDICT_MEMORY_GATE_PROCESS_ID_PATH = $processIdMarker
$env:EASYDICT_MEMORY_GATE_CLOSED_MARKER_PATH = $closedMarker
$env:EASYDICT_MEMORY_GATE_RELEASE_MARKER_PATH = $releaseMarker
$env:EASYDICT_MEMORY_GATE_PHASE_DIR = $phaseDir

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
$null = $testProcess.Handle
try {
    $appProcess = $null
    if (Wait-File $processIdMarker 120 $testProcess) {
        $processIdText = (Get-Content -LiteralPath $processIdMarker -Raw).Trim()
        $markerProcessId = 0
        if ([int]::TryParse($processIdText, [ref]$markerProcessId)) {
            $appProcess = Get-Process -Id $markerProcessId -ErrorAction SilentlyContinue
            if ($null -ne $appProcess -and $AppExePath) {
                try {
                    if (-not [string]::Equals($appProcess.Path, $AppExePath, [StringComparison]::OrdinalIgnoreCase)) {
                        $appProcess = $null
                    }
                }
                catch {
                    $appProcess = $null
                }
            }
        }
    }

    if ($null -eq $appProcess) {
        $appProcess = Wait-TargetProcess "Easydict.WinUI" $AppExePath 120 $testProcess
    }
}
catch {
    Write-LogTail $testOut 120
    Write-LogTail $testErr 120
    throw
}
$processId = $appProcess.Id
$instance = Get-ProcessCounterInstance $processId
Write-Host "Monitoring process $processId ($instance)"

$typeperfCounters = @(
    "\Process($instance)\Private Bytes",
    "\Process($instance)\Handle Count",
    "\Process($instance)\Working Set",
    "\Process($instance)\Thread Count"
)
$typeperfJob = Start-TypeperfJob `
    -Counters $typeperfCounters `
    -CsvPath $typeperfCsv `
    -OutLogPath (Join-Path $OutputDir "typeperf.out.log") `
    -ErrLogPath (Join-Path $OutputDir "typeperf.err.log")

$counterArgs = "collect --process-id $processId --counters System.Runtime --format json --output `"$counterJson`""
$counterProcess = Start-Process -FilePath $dotnetCounters -ArgumentList $counterArgs -RedirectStandardOutput (Join-Path $OutputDir "dotnet-counters.out.log") -RedirectStandardError (Join-Path $OutputDir "dotnet-counters.err.log") -PassThru -WindowStyle Hidden

Start-Sleep -Seconds ([Math]::Max(0, $InitialIdleSeconds))
Invoke-Gcdump $dotnetGcdump $processId $baselineGcdump $baselineHeapstat (Join-Path $OutputDir "baseline.gcdump.log") | Out-Null

$closedObserved = Wait-File $closedMarker ([Math]::Max(30, $InitialIdleSeconds + $PostCloseIdleSeconds + 120)) $testProcess
if ($closedObserved) {
    try {
        $postCloseIdleObserved = Wait-File $postCloseIdleMarker ([Math]::Max(30, $PostCloseIdleSeconds + 30)) $testProcess
        if (-not $postCloseIdleObserved) {
            Write-Warning "Post-close idle marker was not observed before final gcdump collection."
        }

        # Keep final gcdump out of typeperf/dotnet-counters tail metrics.
        Stop-IfRunning $counterProcess
        $counterProcess = $null
        Stop-JobIfRunning $typeperfJob
        $typeperfJob = $null

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
$testProcess.Refresh()
$testExitCode = $testProcess.ExitCode

if (-not $closedObserved -and -not (Test-Path -LiteralPath $finalGcdump)) {
    Stop-IfRunning $counterProcess
    $counterProcess = $null
    Stop-JobIfRunning $typeperfJob
    $typeperfJob = $null

    Invoke-Gcdump $dotnetGcdump $processId $finalGcdump $finalHeapstat (Join-Path $OutputDir "final.gcdump.log") | Out-Null
}

Stop-IfRunning $counterProcess
Stop-JobIfRunning $typeperfJob

if ($testExitCode -ne 0) {
    # Surface the test runner output so the CI annotation is informative without
    # needing artifact download. The PS script ran the UIAutomation scenario in a
    # background process and captured stdout/stderr into $testOut/$testErr — print
    # both so the workflow annotation shows the real test failure.
    Write-Host "::group::dotnet test stdout ($testOut)"
    if (Test-Path -LiteralPath $testOut) {
        Get-Content -LiteralPath $testOut -Raw | Write-Host
    } else {
        Write-Host "(file missing)"
    }
    Write-Host "::endgroup::"
    Write-Host "::group::dotnet test stderr ($testErr)"
    if (Test-Path -LiteralPath $testErr) {
        Get-Content -LiteralPath $testErr -Raw | Write-Host
    } else {
        Write-Host "(file missing)"
    }
    Write-Host "::endgroup::"
    Write-Host "::error title=PR Memory Gate::Memory gate UIAutomation scenario failed with exit code $testExitCode"
    throw "Memory gate UIAutomation scenario failed with exit code $testExitCode. See '$testOut' and '$testErr'."
}

if (Test-Path -LiteralPath $testOut) {
    $testLog = Get-Content -LiteralPath $testOut -Raw
    if ($testLog -match "Fatal error\.|AccessViolationException") {
        Write-Host "::error title=PR Memory Gate::Fatal runtime error detected in app log"
        throw "Memory gate app process emitted a fatal runtime error. See '$testOut'."
    }
}

if (-not (Test-Path -LiteralPath $typeperfCsv)) {
    Write-Host "::error title=PR Memory Gate::typeperf csv missing at $typeperfCsv"
    throw "typeperf did not produce '$typeperfCsv'."
}

$rows = @(Import-Csv -LiteralPath $typeperfCsv)
$timeColumn = Get-CsvTimeColumn $rows
$privateColumn = Get-CsvColumn $rows "\Private Bytes"
$handleColumn = Get-CsvColumn $rows "\Handle Count"
$workingSetColumn = Get-CsvColumn $rows "\Working Set"
$threadColumn = Get-CsvColumn $rows "\Thread Count"
if (-not $privateColumn -or -not $handleColumn) {
    Write-Host "::error title=PR Memory Gate::typeperf csv missing required Private Bytes / Handle Count columns"
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
$postCloseStartIndex = $null
if (Test-Path -LiteralPath $closedMarker) {
    $postCloseStartIndex = Get-NearestTypeperfRowIndex $rows $timeColumn (Read-PhaseMarkerUtc $closedMarker)
}

if ($null -eq $postCloseStartIndex) {
    $postCloseStartIndex = [Math]::Max(0, $handles.Count - [Math]::Max(2, [Math]::Min(10, $PostCloseIdleSeconds)))
}

$postCloseHandleValues = Get-SeriesFromIndex $handles $postCloseStartIndex
$handleTailSlope = Get-TailSlope $postCloseHandleValues 10
$postCloseInitialHandles = Get-SampleAt $postCloseHandleValues 0
$postCloseHandleGrowth = if ($null -ne $postCloseInitialHandles -and $null -ne $finalHandles) {
    $finalHandles - $postCloseInitialHandles
}
else {
    $null
}
$baselineWorkingSet = Get-SampleAt $workingSet $baselineIndex
$finalWorkingSet = Get-TailAverage $workingSet 5

$gcHeap = Read-DotnetCounterValues $counterJson
$baselineGcHeap = Get-SampleAt $gcHeap $baselineIndex
$finalGcHeap = Get-TailAverage $gcHeap 5
$baselineManagedHeapBytes = Read-GcdumpHeapBytes $baselineHeapstat
$finalManagedHeapBytes = Read-GcdumpHeapBytes $finalHeapstat
$phaseSnapshots = New-PhaseSnapshots `
    -PhaseDir $phaseDir `
    -Rows $rows `
    -TimeColumn $timeColumn `
    -PrivateColumn $privateColumn `
    -WorkingSetColumn $workingSetColumn `
    -HandleColumn $handleColumn `
    -ThreadColumn $threadColumn
ConvertTo-Json -InputObject @($phaseSnapshots) -Depth 8 |
    Set-Content -LiteralPath $phaseSnapshotsPath -Encoding UTF8

$failures = New-Object System.Collections.Generic.List[string]
$privateRelativeLimit = $baselinePrivate * (1.0 + ($ThresholdPercent / 100.0))
$privateAbsoluteLimit = $baselinePrivate + ($PrivateBytesAbsoluteAllowanceMB * 1MB)
$privateLimit = [Math]::Max($privateRelativeLimit, $privateAbsoluteLimit)
if ($finalPrivate -gt $privateLimit) {
    $failures.Add(("Private Bytes exceeded threshold: baseline={0:N0}, final={1:N0}, limit={2:N0}" -f $baselinePrivate, $finalPrivate, $privateLimit))
}

if ($handleTailSlope -gt 0.25 -and $null -ne $postCloseHandleGrowth -and $postCloseHandleGrowth -gt $HandleCountPostCloseGrowthAllowance) {
    $failures.Add(("Handle Count is still growing after close: postCloseInitial={0:N1}, final={1:N1}, growth={2:N1}, allowance={3:N0}, tailSlope={4:N3} handles/sample" -f $postCloseInitialHandles, $finalHandles, $postCloseHandleGrowth, $HandleCountPostCloseGrowthAllowance, $handleTailSlope))
}

if ($null -ne $baselineManagedHeapBytes -and $null -ne $finalManagedHeapBytes) {
    $managedHeapRelativeLimit = $baselineManagedHeapBytes * (1.0 + ($ThresholdPercent / 100.0))
    $managedHeapAbsoluteLimit = $baselineManagedHeapBytes + ($ManagedHeapAbsoluteAllowanceMB * 1MB)
    $managedHeapLimit = [Math]::Max($managedHeapRelativeLimit, $managedHeapAbsoluteLimit)
    if ($finalManagedHeapBytes -gt $managedHeapLimit) {
        $failures.Add(("Managed heap bytes exceeded threshold after close: baseline={0:N0}, final={1:N0}, limit={2:N0}" -f $baselineManagedHeapBytes, $finalManagedHeapBytes, $managedHeapLimit))
    }
}
elseif ($null -ne $baselineGcHeap -and $null -ne $finalGcHeap -and $gcHeap.Count -gt 0) {
    $gcRelativeLimit = $baselineGcHeap * (1.0 + ($ThresholdPercent / 100.0))
    $gcAbsoluteLimit = $baselineGcHeap + $GcHeapAbsoluteAllowanceMB
    $gcLimit = [Math]::Max($gcRelativeLimit, $gcAbsoluteLimit)
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
    absoluteAllowancesMB = [pscustomobject]@{
        privateBytes = $PrivateBytesAbsoluteAllowanceMB
        managedHeap = $ManagedHeapAbsoluteAllowanceMB
        gcHeap = $GcHeapAbsoluteAllowanceMB
    }
    sampleCount = $privateBytes.Count
    baselineIndex = $baselineIndex
    privateBytes = [pscustomobject]@{
        baseline = $baselinePrivate
        finalTailAverage = $finalPrivate
        limit = $privateLimit
        relativeLimit = $privateRelativeLimit
        absoluteLimit = $privateAbsoluteLimit
    }
    handleCount = [pscustomobject]@{
        baseline = $baselineHandles
        finalTailAverage = $finalHandles
        tailSlope = $handleTailSlope
        postCloseStartIndex = $postCloseStartIndex
        postCloseSampleCount = $postCloseHandleValues.Count
        postCloseInitial = $postCloseInitialHandles
        postCloseGrowth = $postCloseHandleGrowth
        postCloseGrowthAllowance = $HandleCountPostCloseGrowthAllowance
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
    managedHeapBytes = [pscustomobject]@{
        baseline = $baselineManagedHeapBytes
        final = $finalManagedHeapBytes
        limit = $managedHeapLimit
    }
    phaseSnapshots = $phaseSnapshots
    artifacts = [pscustomobject]@{
        typeperfCsv = $typeperfCsv
        dotnetCountersJson = $counterJson
        baselineGcdump = $baselineGcdump
        baselineHeapstat = $baselineHeapstat
        finalGcdump = $finalGcdump
        finalHeapstat = $finalHeapstat
        phaseSnapshots = $phaseSnapshotsPath
    }
    failures = $failures.ToArray()
}

$summaryPath = Join-Path $OutputDir "summary.json"
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host "Memory gate summary written to $summaryPath"

if ($failures.Count -gt 0) {
    # Use the workflow-command form so each violation lands as a CI annotation
    # we can inspect from the check-runs annotations API without needing the
    # uploaded artifact. Each failure entry is a single line with the
    # baseline/final/limit numbers, which is exactly what we want surfaced.
    foreach ($failure in $failures) {
        Write-Host "::error title=PR Memory Gate threshold::$failure"
    }
    Write-Host "::error title=PR Memory Gate::Memory gate failed — $($failures.Count) threshold(s) exceeded"
    throw "Memory gate failed."
}

Write-Host "Memory gate passed."

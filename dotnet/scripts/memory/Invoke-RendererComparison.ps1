[CmdletBinding()]
param(
    [string]$AppExePath = "",
    [string]$TestProject = "",
    [string]$OutputDir = "",
    [ValidateRange(1, 12)]
    [int]$RunsPerBackend = 3,
    [ValidateRange(0, 300)]
    [int]$InitialIdleSeconds = 10,
    [ValidateRange(0, 300)]
    [int]$PostCloseIdleSeconds = 5,
    [ValidateRange(250, 10000)]
    [int]$GpuSampleIntervalMilliseconds = 1000,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateRange(1, 100)]
    [int]$CardCount = 1,
    [string]$ResultText = "Renderer comparison text wraps across the Minimal result card. 中文测量文本 keeps the Direct and XAML paths on the same deterministic result.",
    [switch]$SkipBuild,
    [switch]$SkipToolInstall,
    [ValidateRange(60, 300)]
    [int]$StreamingUpdateCount = 120,
    [ValidateRange(10, 1000)]
    [int]$StreamingUpdateIntervalMilliseconds = 50
)

$ErrorActionPreference = "Stop"

function New-Directory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Get-FullPath([string]$Path, [string]$BasePath) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Find-AppExecutable([string]$DotnetRoot, [string]$BuildConfiguration) {
    # This script builds x64 below, so keep discovery in the matching output subtree.
    # Searching bin\<configuration> can select an older AnyCPU executable instead.
    $outputRoot = Join-Path $DotnetRoot (Join-Path "src\Easydict.WinUI\bin\x64" $BuildConfiguration)
    $matches = @(Get-ChildItem -LiteralPath $outputRoot -Filter "Easydict.WinUI.exe" -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($matches.Count -eq 0) {
        throw "Could not find x64 Easydict.WinUI.exe below '$outputRoot'. Build the UI application or pass -AppExePath."
    }

    return $matches[0].FullName
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

    if ([double]::TryParse(
        $text,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::CurrentCulture,
        [ref]$number)) {
        return $number
    }

    return $null
}

function Get-Median([double[]]$Values) {
    if ($Values.Count -eq 0) {
        return $null
    }

    [double[]]$sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return $sorted[$middle]
    }

    return ($sorted[$middle - 1] + $sorted[$middle]) / 2.0
}

function Get-Distribution([object[]]$Candidates) {
    $values = New-Object System.Collections.Generic.List[double]
    foreach ($candidate in @($Candidates)) {
        $value = Convert-ToDouble $candidate
        if ($null -ne $value) {
            $values.Add($value)
        }
    }

    if ($values.Count -eq 0) {
        return $null
    }

    [double[]]$numbers = $values.ToArray()
    return [pscustomobject]@{
        samples = $numbers.Count
        median = Get-Median $numbers
        mean = ($numbers | Measure-Object -Average).Average
        minimum = ($numbers | Measure-Object -Minimum).Minimum
        maximum = ($numbers | Measure-Object -Maximum).Maximum
    }
}

function Get-NumericDelta([object]$EndValue, [object]$StartValue) {
    $endNumber = Convert-ToDouble $EndValue
    $startNumber = Convert-ToDouble $StartValue
    if ($null -eq $endNumber -or $null -eq $startNumber) {
        return $null
    }

    return $endNumber - $startNumber
}

function Read-StageSamples([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    $content = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($content)) {
        return @()
    }

    try {
        $samples = $content | ConvertFrom-Json
        return @($samples)
    }
    catch {
        Write-Warning "Could not parse Direct renderer stage telemetry '$Path': $($_.Exception.Message)"
        return @()
    }
}

function Get-StageDistribution($Runs, [string]$Backend, [string]$Stage, [string]$Property) {
    $values = New-Object System.Collections.Generic.List[double]
    foreach ($run in @($Runs | Where-Object { $_.backend -eq $Backend })) {
        foreach ($sample in @($run.stageSamples | Where-Object { $_.stage -eq $Stage })) {
            $value = Convert-ToDouble $sample.$Property
            if ($null -ne $value) {
                $values.Add($value)
            }
        }
    }

    if ($values.Count -eq 0) {
        return $null
    }

    return Get-Distribution $values.ToArray()
}

function Get-GpuCounterAvailability() {
    try {
        $set = Get-Counter -ListSet "GPU Process Memory" -ErrorAction Stop
        $required = @(
            "\GPU Process Memory(*)\Dedicated Usage",
            "\GPU Process Memory(*)\Shared Usage",
            "\GPU Process Memory(*)\Local Usage",
            "\GPU Process Memory(*)\Non Local Usage",
            "\GPU Process Memory(*)\Total Committed"
        )
        $available = @($set.Counter)
        $missing = @($required | Where-Object { $available -notcontains $_ })
        return [pscustomobject]@{
            available = $missing.Count -eq 0
            counters = $available
            missingCounters = $missing
            error = $null
        }
    }
    catch {
        return [pscustomobject]@{
            available = $false
            counters = @()
            missingCounters = @()
            error = $_.Exception.Message
        }
    }
}

function Get-EnvironmentMetadata($GpuCounterAvailability) {
    $adapters = @()
    try {
        $adapters = @(Get-CimInstance Win32_VideoController -ErrorAction Stop |
            Select-Object Name, AdapterRAM, DriverVersion, VideoProcessor)
    }
    catch {
        $adapters = @([pscustomobject]@{ error = $_.Exception.Message })
    }

    $operatingSystem = $null
    try {
        $operatingSystem = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop |
            Select-Object Caption, Version, BuildNumber
    }
    catch {
        $operatingSystem = [pscustomobject]@{ error = $_.Exception.Message }
    }

    return [pscustomobject]@{
        capturedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        machineName = $env:COMPUTERNAME
        operatingSystem = $operatingSystem
        adapters = $adapters
        gpuProcessMemory = $GpuCounterAvailability
        dotnetVersion = (& dotnet --version)
    }
}

function Write-RendererSettings([string]$SettingsPath, [bool]$DirectRenderer, [int]$EnabledCardCount) {
    $services = @("bing")
    for ($index = 1; $index -lt $EnabledCardCount; $index++) {
        $services += "benchmark-$index"
    }

    $settings = [ordered]@{
        UILanguage = "en-US"
        HasUserConfiguredServices = $true
        AppTheme = "Minimal"
        DirectRenderer = $DirectRenderer
        MainWindowEnabledServices = $services
        HideEmptyServiceResults = $false
    }
    $settings | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $SettingsPath -Encoding UTF8
}

function Wait-ForProcessIdMarker([string]$MarkerPath, $GuardProcess, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $MarkerPath) {
            $text = (Get-Content -LiteralPath $MarkerPath -Raw).Trim()
            $processId = 0
            if ([int]::TryParse($text, [ref]$processId)) {
                return $processId
            }
        }

        if ($GuardProcess.HasExited) {
            return $null
        }

        Start-Sleep -Milliseconds 200
    }

    return $null
}

function Stop-JobIfRunning($Job, [string]$LogPath) {
    if ($null -eq $Job) {
        return
    }

    try {
        if ($Job.State -eq "Running") {
            Stop-Job -Job $Job -ErrorAction SilentlyContinue
        }

        Receive-Job -Job $Job -ErrorAction SilentlyContinue | Out-File -LiteralPath $LogPath -Append -Encoding UTF8
    }
    finally {
        Remove-Job -Job $Job -Force -ErrorAction SilentlyContinue
    }
}

function Start-GpuProcessMemoryCapture(
    [int]$AppProcessId,
    [int]$DwmProcessId,
    [string]$CsvPath,
    [int]$SampleIntervalMilliseconds) {

    Remove-Item -LiteralPath $CsvPath -Force -ErrorAction SilentlyContinue
    return Start-Job -ScriptBlock {
        param(
            [int]$AppProcessId,
            [int]$DwmProcessId,
            [string]$CsvPath,
            [int]$SampleIntervalMilliseconds
        )

        $counterPaths = @(
            "\GPU Process Memory(*)\Dedicated Usage",
            "\GPU Process Memory(*)\Shared Usage",
            "\GPU Process Memory(*)\Local Usage",
            "\GPU Process Memory(*)\Non Local Usage",
            "\GPU Process Memory(*)\Total Committed"
        )

        function New-Totals() {
            return [ordered]@{
                dedicatedBytes = 0.0
                sharedBytes = 0.0
                localBytes = 0.0
                nonLocalBytes = 0.0
                totalCommittedBytes = 0.0
            }
        }

        function Get-MetricName([string]$Path) {
            if ($Path -match "\\dedicated usage$") { return "dedicatedBytes" }
            if ($Path -match "\\shared usage$") { return "sharedBytes" }
            if ($Path -match "\\local usage$") { return "localBytes" }
            if ($Path -match "\\non local usage$") { return "nonLocalBytes" }
            if ($Path -match "\\total committed$") { return "totalCommittedBytes" }
            return $null
        }

        $sampleClock = [System.Diagnostics.Stopwatch]::StartNew()
        $nextSampleAtMilliseconds = 0.0
        while ($true) {
            $appTotals = New-Totals
            $dwmTotals = New-Totals
            $appInstances = @{}
            $dwmInstances = @{}
            $status = "available"
            $sampleErrorText = $null

            try {
                $counter = Get-Counter -Counter $counterPaths -ErrorAction Stop
                foreach ($sample in $counter.CounterSamples) {
                    if ($sample.Status -ne 0) {
                        continue
                    }

                    $metric = Get-MetricName $sample.Path.ToLowerInvariant()
                    if ($null -eq $metric) {
                        continue
                    }

                    $value = [double]$sample.CookedValue
                    if ($sample.InstanceName -match ("^pid_" + $AppProcessId + "_")) {
                        $appTotals[$metric] += $value
                        $appInstances[$sample.InstanceName] = $true
                    }
                    elseif ($DwmProcessId -gt 0 -and $sample.InstanceName -match ("^pid_" + $DwmProcessId + "_")) {
                        $dwmTotals[$metric] += $value
                        $dwmInstances[$sample.InstanceName] = $true
                    }
                }
            }
            catch {
                $status = "sample-error"
                $sampleErrorText = $_.Exception.Message -replace "[\r\n]", " "
            }

            [pscustomobject][ordered]@{
                timestampUtc = [DateTimeOffset]::UtcNow.ToString("O")
                sampleStatus = $status
                sampleError = $sampleErrorText
                appProcessId = $AppProcessId
                appInstanceCount = $appInstances.Count
                appDedicatedBytes = $appTotals.dedicatedBytes
                appSharedBytes = $appTotals.sharedBytes
                appLocalBytes = $appTotals.localBytes
                appNonLocalBytes = $appTotals.nonLocalBytes
                appTotalCommittedBytes = $appTotals.totalCommittedBytes
                dwmProcessId = $DwmProcessId
                dwmInstanceCount = $dwmInstances.Count
                dwmDedicatedBytes = $dwmTotals.dedicatedBytes
                dwmSharedBytes = $dwmTotals.sharedBytes
                dwmLocalBytes = $dwmTotals.localBytes
                dwmNonLocalBytes = $dwmTotals.nonLocalBytes
                dwmTotalCommittedBytes = $dwmTotals.totalCommittedBytes
            } | Export-Csv -LiteralPath $CsvPath -NoTypeInformation -Append -Encoding UTF8

            $nextSampleAtMilliseconds += $SampleIntervalMilliseconds
            $remainingMilliseconds = [int][Math]::Ceiling(
                $nextSampleAtMilliseconds - $sampleClock.Elapsed.TotalMilliseconds)
            if ($remainingMilliseconds -gt 0) {
                Start-Sleep -Milliseconds $remainingMilliseconds
            }
            else {
                $nextSampleAtMilliseconds = $sampleClock.Elapsed.TotalMilliseconds
            }

        }
    } -ArgumentList $AppProcessId, $DwmProcessId, $CsvPath, $SampleIntervalMilliseconds
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

function Convert-GpuTimestampToUtc([object]$Value) {
    $timestamp = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse(
        ([string]$Value),
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal,
        [ref]$timestamp)) {
        return $timestamp.ToUniversalTime()
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
    foreach ($format in @(
        "MM/dd/yyyy HH:mm:ss.fff",
        "M/d/yyyy H:mm:ss.fff",
        "MM/dd/yyyy HH:mm:ss",
        "M/d/yyyy H:mm:ss")) {
        $dateTime = [DateTime]::MinValue
        if ([DateTime]::TryParseExact($text, $format, $culture, $styles, [ref]$dateTime)) {
            return ([DateTimeOffset]$dateTime).ToUniversalTime()
        }
    }

    return $null
}

function Get-CsvColumnName($Row, [string]$Suffix) {
    return @($Row.PSObject.Properties.Name |
        Where-Object { $_.EndsWith($Suffix, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1)[0]
}

function New-ProcessSnapshot($Rows, [DateTimeOffset]$TargetUtc, [string]$Phase) {
    if ($Rows.Count -eq 0) {
        return $null
    }

    $timeColumn = @($Rows[0].PSObject.Properties.Name)[0]
    $privateColumn = Get-CsvColumnName $Rows[0] "\Private Bytes"
    $workingSetColumn = Get-CsvColumnName $Rows[0] "\Working Set"
    $handleColumn = Get-CsvColumnName $Rows[0] "\Handle Count"
    $threadColumn = Get-CsvColumnName $Rows[0] "\Thread Count"
    if ([string]::IsNullOrWhiteSpace($timeColumn) -or [string]::IsNullOrWhiteSpace($privateColumn)) {
        return $null
    }

    $bestRow = $null
    $bestIndex = $null
    $bestUtc = $null
    $bestDelta = [double]::MaxValue
    for ($index = 0; $index -lt $Rows.Count; $index++) {
        $sampleUtc = Convert-TypeperfTimestampToUtc $Rows[$index].$timeColumn
        if ($null -eq $sampleUtc) {
            continue
        }

        $delta = [Math]::Abs(($sampleUtc.UtcDateTime - $TargetUtc.UtcDateTime).TotalMilliseconds)
        if ($delta -lt $bestDelta) {
            $bestRow = $Rows[$index]
            $bestIndex = $index
            $bestUtc = $sampleUtc
            $bestDelta = $delta
        }
    }

    if ($null -eq $bestRow) {
        return $null
    }

    return [pscustomobject]@{
        phase = $Phase
        markerUtc = $TargetUtc.ToString("O")
        sampleIndex = $bestIndex
        sampleUtc = $bestUtc.ToString("O")
        sampleDeltaMilliseconds = $bestDelta
        privateBytes = Convert-ToDouble $bestRow.$privateColumn
        workingSet = Convert-ToDouble $bestRow.$workingSetColumn
        handleCount = Convert-ToDouble $bestRow.$handleColumn
        threadCount = Convert-ToDouble $bestRow.$threadColumn
    }
}

function Get-NearestGpuSample($Rows, [DateTimeOffset]$TargetUtc) {
    $bestRow = $null
    $bestDelta = [double]::MaxValue
    foreach ($row in @($Rows)) {
        $timestamp = Convert-GpuTimestampToUtc $row.timestampUtc
        if ($null -eq $timestamp) {
            continue
        }

        $delta = [Math]::Abs(($timestamp.UtcDateTime - $TargetUtc.UtcDateTime).TotalMilliseconds)
        if ($delta -lt $bestDelta) {
            $bestDelta = $delta
            $bestRow = $row
        }
    }

    return $bestRow
}
function New-GpuSnapshot($Rows, [DateTimeOffset]$TargetUtc, [string]$Phase) {
    $row = Get-NearestGpuSample $Rows $TargetUtc
    if ($null -eq $row) {
        return $null
    }

    $sampleUtc = Convert-GpuTimestampToUtc $row.timestampUtc
    return [pscustomobject]@{
        phase = $Phase
        markerUtc = $TargetUtc.ToString("O")
        sampleUtc = $row.timestampUtc
        sampleDeltaMilliseconds = [Math]::Abs(
            ($sampleUtc.UtcDateTime - $TargetUtc.UtcDateTime).TotalMilliseconds)
        app = [pscustomobject]@{
            instanceCount = Convert-ToDouble $row.appInstanceCount
            dedicatedBytes = Convert-ToDouble $row.appDedicatedBytes
            sharedBytes = Convert-ToDouble $row.appSharedBytes
            localBytes = Convert-ToDouble $row.appLocalBytes
            nonLocalBytes = Convert-ToDouble $row.appNonLocalBytes
            totalCommittedBytes = Convert-ToDouble $row.appTotalCommittedBytes
        }
        # ponytail: DWM is shared system telemetry and must stay separate from per-app GPU values.
        dwm = [pscustomobject]@{
            processId = Convert-ToDouble $row.dwmProcessId
            instanceCount = Convert-ToDouble $row.dwmInstanceCount
            dedicatedBytes = Convert-ToDouble $row.dwmDedicatedBytes
            sharedBytes = Convert-ToDouble $row.dwmSharedBytes
            localBytes = Convert-ToDouble $row.dwmLocalBytes
            nonLocalBytes = Convert-ToDouble $row.dwmNonLocalBytes
            totalCommittedBytes = Convert-ToDouble $row.dwmTotalCommittedBytes
        }
    }
}

function New-GpuPhaseSnapshots([string]$PhaseDir, [string]$CsvPath) {
    if (-not (Test-Path -LiteralPath $PhaseDir) -or -not (Test-Path -LiteralPath $CsvPath)) {
        return @()
    }

    $rows = @(Import-Csv -LiteralPath $CsvPath | Where-Object { $_.sampleStatus -eq "available" })
    if ($rows.Count -eq 0) {
        return @()
    }

    $snapshots = New-Object System.Collections.Generic.List[object]
    foreach ($marker in @(Get-ChildItem -LiteralPath $PhaseDir -Filter "*.marker" -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $markerUtc = Read-PhaseMarkerUtc $marker.FullName
        $row = Get-NearestGpuSample $rows $markerUtc
        if ($null -eq $row) {
            continue
        }

        $snapshots.Add([pscustomobject]@{
            phase = $marker.BaseName
            markerUtc = $markerUtc.ToString("O")
            sampleUtc = $row.timestampUtc
            app = [pscustomobject]@{
                instanceCount = Convert-ToDouble $row.appInstanceCount
                dedicatedBytes = Convert-ToDouble $row.appDedicatedBytes
                sharedBytes = Convert-ToDouble $row.appSharedBytes
                localBytes = Convert-ToDouble $row.appLocalBytes
                nonLocalBytes = Convert-ToDouble $row.appNonLocalBytes
                totalCommittedBytes = Convert-ToDouble $row.appTotalCommittedBytes
            }
            # ponytail: DWM is a shared system compositor; report it separately and never sum it into app GPU memory.
            dwm = [pscustomobject]@{
                processId = Convert-ToDouble $row.dwmProcessId
                instanceCount = Convert-ToDouble $row.dwmInstanceCount
                dedicatedBytes = Convert-ToDouble $row.dwmDedicatedBytes
                sharedBytes = Convert-ToDouble $row.dwmSharedBytes
                localBytes = Convert-ToDouble $row.dwmLocalBytes
                nonLocalBytes = Convert-ToDouble $row.dwmNonLocalBytes
                totalCommittedBytes = Convert-ToDouble $row.dwmTotalCommittedBytes
            }
        })
    }

    return $snapshots.ToArray()
}

function Get-PhaseSnapshot($Snapshots, [string]$Phase) {
    return @($Snapshots | Where-Object { $_.phase -eq $Phase } | Select-Object -First 1)[0]
}

function Quote-ProcessArgument([string]$Value) {
    if ($Value.Length -eq 0) {
        return '""'
    }

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Start-MemoryGateProcess([string]$GateScript, [string[]]$GateArguments, [string]$OutLogPath, [string]$ErrLogPath) {
    $allArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $GateScript) + $GateArguments
    $commandLine = ($allArguments | ForEach-Object { Quote-ProcessArgument ([string]$_) }) -join " "
    $process = Start-Process -FilePath "powershell.exe" -ArgumentList $commandLine -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $OutLogPath -RedirectStandardError $ErrLogPath
    $null = $process.Handle
    return $process
}

function Set-ScopedProcessEnvironment([hashtable]$Values) {
    $previous = @{}
    foreach ($name in $Values.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
        [Environment]::SetEnvironmentVariable($name, [string]$Values[$name], "Process")
    }

    return $previous
}

function Restore-ScopedProcessEnvironment([hashtable]$Previous) {
    foreach ($name in $Previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $Previous[$name], "Process")
    }
}

function Get-RunMetricSummary($Runs, [string]$Backend) {
    $backendRuns = @($Runs | Where-Object { $_.backend -eq $Backend })
    return [pscustomobject]@{
        runs = $backendRuns.Count
        privateBytesAtResult = Get-Distribution @($backendRuns | ForEach-Object { $_.memoryAtResult.privateBytes })
        workingSetAtResult = Get-Distribution @($backendRuns | ForEach-Object { $_.memoryAtResult.workingSet })
        appGpuDedicatedAtResult = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtResult.app.dedicatedBytes })
        appGpuSharedAtResult = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtResult.app.sharedBytes })
        appGpuLocalAtResult = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtResult.app.localBytes })
        appGpuNonLocalAtResult = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtResult.app.nonLocalBytes })
        appGpuTotalCommittedAtResult = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtResult.app.totalCommittedBytes })
        privateBytesAtStreamingCompleted = Get-Distribution @($backendRuns | ForEach-Object { $_.memoryAtStreamingCompleted.privateBytes })
        workingSetAtStreamingCompleted = Get-Distribution @($backendRuns | ForEach-Object { $_.memoryAtStreamingCompleted.workingSet })
        streamingPrivateBytesDelta = Get-Distribution @($backendRuns | ForEach-Object { $_.streamingPrivateBytesDelta })
        appGpuDedicatedAtStreamingCompleted = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtStreamingCompleted.app.dedicatedBytes })
        appGpuSharedAtStreamingCompleted = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtStreamingCompleted.app.sharedBytes })
        appGpuLocalAtStreamingCompleted = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtStreamingCompleted.app.localBytes })
        appGpuNonLocalAtStreamingCompleted = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtStreamingCompleted.app.nonLocalBytes })
        appGpuTotalCommittedAtStreamingCompleted = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtStreamingCompleted.app.totalCommittedBytes })
        streamingGpuTotalCommittedDelta = Get-Distribution @($backendRuns | ForEach-Object { $_.streamingGpuTotalCommittedDelta })
        dwmGpuDedicatedAtResult = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtResult.dwm.dedicatedBytes })
        dwmGpuSharedAtResult = Get-Distribution @($backendRuns | ForEach-Object { $_.gpuAtResult.dwm.sharedBytes })
        firstResultRenderLatencyMilliseconds = Get-Distribution @(
            $backendRuns | ForEach-Object {
                if ($null -ne $_.rendererBenchmark -and
                    $_.rendererBenchmark.firstResult.status -eq "available") {
                    $_.rendererBenchmark.firstResult.renderLatencyMilliseconds
                }
            })
        streamingCpuPercent = Get-Distribution @(
            $backendRuns | ForEach-Object {
                if ($null -ne $_.rendererBenchmark -and
                    $_.rendererBenchmark.streaming.status -eq "available") {
                    $_.rendererBenchmark.streaming.cpuPercent.median
                }
            })
        streamingWindowDurationMilliseconds = Get-Distribution @(
            $backendRuns | ForEach-Object {
                if ($null -ne $_.rendererBenchmark -and
                    $_.rendererBenchmark.streaming.status -eq "available") {
                    $_.rendererBenchmark.streaming.durationMilliseconds
                }
            })
        streamingCpuSamples = Get-Distribution @(
            $backendRuns | ForEach-Object {
                if ($null -ne $_.rendererBenchmark -and
                    $_.rendererBenchmark.streaming.status -eq "available") {
                    $_.rendererBenchmark.streaming.cpuPercent.samples
                }
            })
        stageLayoutMilliseconds = Get-StageDistribution $Runs $Backend "layout" "elapsedMilliseconds"
        stageDisplayListMilliseconds = Get-StageDistribution $Runs $Backend "display-list" "elapsedMilliseconds"
        stageDrawMilliseconds = Get-StageDistribution $Runs $Backend "draw" "elapsedMilliseconds"
        stageLayoutAllocatedBytes = Get-StageDistribution $Runs $Backend "layout" "allocatedBytes"
        stageDisplayListAllocatedBytes = Get-StageDistribution $Runs $Backend "display-list" "allocatedBytes"
        stageDrawAllocatedBytes = Get-StageDistribution $Runs $Backend "draw" "allocatedBytes"
    }
}

function Invoke-RendererRun(
    [int]$Sequence,
    [int]$Iteration,
    [string]$Backend,
    [string]$ComparisonOutputDir,
    [string]$GateScript,
    [string]$ResolvedAppExePath,
    [string]$ResolvedTestProject,
    [string]$BuildConfiguration,
    [int]$EnabledCardCount,
    [string]$DeterministicResultText,
    [int]$InitialIdle,
    [int]$PostCloseIdle,
    [int]$GpuInterval,
    [int]$StreamingUpdates,
    [int]$StreamingUpdateInterval,
    [bool]$GpuCountersAvailable,
    [bool]$SkipToolInstallForRun) {

    $runDirectory = Join-Path $ComparisonOutputDir ("{0:D2}-{1:D2}-{2}" -f $Sequence, $Iteration, $Backend.ToLowerInvariant())
    $settingsDirectory = Join-Path $runDirectory "settings"
    New-Directory $runDirectory
    New-Directory $settingsDirectory
    $settingsPath = Join-Path $settingsDirectory "settings.json"
    $directRenderer = $Backend -eq "Direct"
    Write-RendererSettings $settingsPath $directRenderer $EnabledCardCount

    $gateOutput = Join-Path $runDirectory "memory-gate"
    $gateOutLog = Join-Path $runDirectory "comparison-driver.out.log"
    $gateErrLog = Join-Path $runDirectory "comparison-driver.err.log"
    $gpuCsv = Join-Path $runDirectory "gpu-process-memory.csv"
    $gpuJobLog = Join-Path $runDirectory "gpu-process-memory.job.log"
    $gpuPhaseSnapshotsPath = Join-Path $runDirectory "gpu-phase-snapshots.json"
    $runMetadataPath = Join-Path $runDirectory "run-metadata.json"
    $processIdMarker = Join-Path $gateOutput "markers\process-id.marker"
    $phaseDirectory = Join-Path $gateOutput "markers\phases"
    $stageSamplesPath = Join-Path $runDirectory "renderer-stage-samples.json"

    $runMetadata = [ordered]@{
        schemaVersion = 4
        sequence = $Sequence
        iteration = $Iteration
        backend = $Backend
        capturedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        settingsPath = $settingsPath
        settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        appExePath = $ResolvedAppExePath
        testProject = $ResolvedTestProject
        configuration = $BuildConfiguration
        initialIdleSeconds = $InitialIdle
        postCloseIdleSeconds = $PostCloseIdle
        gpuSampleIntervalMilliseconds = $GpuInterval
        gpuProcessMemoryAvailable = $GpuCountersAvailable
        rendererBenchmark = [pscustomobject]@{
            enabled = $true
            streamingUpdateCount = $StreamingUpdates
            streamingUpdateIntervalMilliseconds = $StreamingUpdateInterval
            stageSamplesPath = $stageSamplesPath
        }
        syntheticResult = $true
        appProcessId = $null
        dwmProcessId = $null
        gpuSampleStatus = "not-started"
    }
    $runMetadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $runMetadataPath -Encoding UTF8

    # ponytail: this wrapper invokes the gate with relaxed limits; it retains its UI scenario and raw artifacts
    # without changing the PR gate's threshold semantics.
    $gateArguments = @(
        "-AppExePath", $ResolvedAppExePath,
        "-TestProject", $ResolvedTestProject,
        "-Configuration", $BuildConfiguration,
        "-OutputDir", $gateOutput,
        "-InitialIdleSeconds", $InitialIdle,
        "-PostCloseIdleSeconds", $PostCloseIdle,
        "-ThresholdPercent", "1000",
        "-PrivateBytesAbsoluteAllowanceMB", "10000",
        "-ManagedHeapAbsoluteAllowanceMB", "10000",
        "-GcHeapAbsoluteAllowanceMB", "10000",
        "-HandleCountPostCloseGrowthAllowance", "10000",
        "-SkipBuild",
        "-RunRealTranslation",
        "-MeasureRendererPerformance",
        "-RendererBenchmarkStreamingUpdateCount", $StreamingUpdates,
        "-RendererBenchmarkStreamingUpdateIntervalMilliseconds", $StreamingUpdateInterval
    )
    if ($SkipToolInstallForRun) {
        $gateArguments += "-SkipToolInstall"
    }

    $environment = @{
        "EASYDICT_SETTINGS_DIR" = $settingsDirectory
        "EASYDICT_UIA_DIRECT_RESULT_TEXT" = $DeterministicResultText
        "EASYDICT_MEMORY_GATE_SKIP_MODE_TRANSITIONS" = "1"
        "EASYDICT_RENDERER_BENCHMARK_STAGE_PATH" = $stageSamplesPath
    }
    $previousEnvironment = Set-ScopedProcessEnvironment $environment
    $gateProcess = $null
    $gpuJob = $null
    $appProcessId = $null
    $dwmProcessId = 0
    try {
        $gateProcess = Start-MemoryGateProcess $GateScript $gateArguments $gateOutLog $gateErrLog
    }
    finally {
        Restore-ScopedProcessEnvironment $previousEnvironment
    }

    try {
        $appProcessId = Wait-ForProcessIdMarker $processIdMarker $gateProcess 120
        if ($null -eq $appProcessId) {
            throw "The memory-gate app process marker was not observed for $Backend run $Sequence. See '$gateOutLog' and '$gateErrLog'."
        }

        $runMetadata.appProcessId = $appProcessId
        $dwm = @(Get-Process -Name "dwm" -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($dwm.Count -gt 0) {
            $dwmProcessId = $dwm[0].Id
        }
        $runMetadata.dwmProcessId = $dwmProcessId

        if ($GpuCountersAvailable) {
            $gpuJob = Start-GpuProcessMemoryCapture $appProcessId $dwmProcessId $gpuCsv $GpuInterval
            $runMetadata.gpuSampleStatus = "collecting"
        }
        else {
            $runMetadata.gpuSampleStatus = "unavailable"
        }

        $gateProcess.WaitForExit()
        $gateProcess.Refresh()
    }
    finally {
        Stop-JobIfRunning $gpuJob $gpuJobLog
    }

    $gateExitCode = $gateProcess.ExitCode
    if ($null -eq $gateExitCode) {
        throw "Memory-gate process did not expose an exit code for $Backend run $Sequence. See '$gateOutLog' and '$gateErrLog'."
    }
    if ($gateExitCode -ne 0) {
        throw "Memory-gate scenario failed for $Backend run $Sequence with exit code $gateExitCode. See '$gateOutLog' and '$gateErrLog'."
    }

    $memorySummaryPath = Join-Path $gateOutput "summary.json"
    if (-not (Test-Path -LiteralPath $memorySummaryPath)) {
        throw "Memory-gate scenario completed without '$memorySummaryPath'."
    }

    $memorySummary = Get-Content -LiteralPath $memorySummaryPath -Raw | ConvertFrom-Json
    $gpuPhaseSnapshots = New-GpuPhaseSnapshots $phaseDirectory $gpuCsv
    $gpuPhaseSnapshots | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $gpuPhaseSnapshotsPath -Encoding UTF8
    $gpuRows = if (Test-Path -LiteralPath $gpuCsv) {
        @(Import-Csv -LiteralPath $gpuCsv | Where-Object { $_.sampleStatus -eq "available" })
    }
    else {
        @()
    }

    $typeperfRows = @(
        Import-Csv -LiteralPath $memorySummary.artifacts.typeperfCsv)
    $firstResultRenderedUtc = Convert-GpuTimestampToUtc `
        $memorySummary.rendererBenchmark.firstResult.renderedUtc
    $streamingCompletedUtc = Convert-GpuTimestampToUtc `
        $memorySummary.rendererBenchmark.streaming.completedUtc
    if ($null -eq $firstResultRenderedUtc -or $null -eq $streamingCompletedUtc) {
        throw "Renderer benchmark completed without valid result/stream timestamps."
    }

    $memoryAtResult = New-ProcessSnapshot `
        $typeperfRows $firstResultRenderedUtc "renderer-first-result-rendered"
    $memoryAtStreamingCompleted = New-ProcessSnapshot `
        $typeperfRows $streamingCompletedUtc "renderer-streaming-completed"
    $gpuAtResult = New-GpuSnapshot `
        $gpuRows $firstResultRenderedUtc "renderer-first-result-rendered"
    $gpuAtStreamingCompleted = New-GpuSnapshot `
        $gpuRows $streamingCompletedUtc "renderer-streaming-completed"
    $streamingPrivateBytesDelta = Get-NumericDelta `
        $memoryAtStreamingCompleted.privateBytes `
        $memoryAtResult.privateBytes
    $streamingGpuTotalCommittedDelta = Get-NumericDelta `
        $gpuAtStreamingCompleted.app.totalCommittedBytes `
        $gpuAtResult.app.totalCommittedBytes

    $runMetadata.gpuSampleStatus = if ($gpuRows.Count -gt 0) { "available" } elseif ($GpuCountersAvailable) { "no-valid-samples" } else { "unavailable" }
    $runMetadata.gpuSampleCount = $gpuRows.Count
    $runMetadata.rendererBenchmark |
        Add-Member -NotePropertyName result -NotePropertyValue $memorySummary.rendererBenchmark -Force
    $stageSamples = Read-StageSamples $stageSamplesPath
    $runMetadata.rendererBenchmark |
        Add-Member -NotePropertyName stageSamples -NotePropertyValue $stageSamples -Force
    $runMetadata | Add-Member -NotePropertyName memoryAtResult -NotePropertyValue $memoryAtResult -Force
    $runMetadata | Add-Member -NotePropertyName memoryAtStreamingCompleted -NotePropertyValue $memoryAtStreamingCompleted -Force
    $runMetadata | Add-Member -NotePropertyName gpuAtResult -NotePropertyValue $gpuAtResult -Force
    $runMetadata | Add-Member -NotePropertyName gpuAtStreamingCompleted -NotePropertyValue $gpuAtStreamingCompleted -Force
    $runMetadata | Add-Member -NotePropertyName streamingPrivateBytesDelta -NotePropertyValue $streamingPrivateBytesDelta -Force
    $runMetadata | Add-Member -NotePropertyName streamingGpuTotalCommittedDelta -NotePropertyValue $streamingGpuTotalCommittedDelta -Force
    $runMetadata.completedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    $runMetadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $runMetadataPath -Encoding UTF8

    return [pscustomobject]@{
        sequence = $Sequence
        iteration = $Iteration
        backend = $Backend
        outputDirectory = $runDirectory
        settingsPath = $settingsPath
        memorySummaryPath = $memorySummaryPath
        stageSamplesPath = $stageSamplesPath
        stageSamples = $stageSamples
        gpuCsvPath = if (Test-Path -LiteralPath $gpuCsv) { $gpuCsv } else { $null }
        gpuPhaseSnapshotsPath = $gpuPhaseSnapshotsPath
        runMetadataPath = $runMetadataPath
        gpuSampleStatus = $runMetadata.gpuSampleStatus
        memoryAtResult = $memoryAtResult
        memoryAtStreamingCompleted = $memoryAtStreamingCompleted
        gpuAtResult = $gpuAtResult
        gpuAtStreamingCompleted = $gpuAtStreamingCompleted
        streamingPrivateBytesDelta = $streamingPrivateBytesDelta
        streamingGpuTotalCommittedDelta = $streamingGpuTotalCommittedDelta
        rendererBenchmark = $memorySummary.rendererBenchmark
    }
}

$dotnetRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$repoRoot = Split-Path $dotnetRoot -Parent
$gateScript = Join-Path $PSScriptRoot "Invoke-PrMemoryGate.ps1"
if (-not (Test-Path -LiteralPath $gateScript)) {
    throw "Missing reusable memory-gate scenario at '$gateScript'."
}

if ([string]::IsNullOrWhiteSpace($TestProject)) {
    $TestProject = Join-Path $dotnetRoot "tests\Easydict.UIAutomation.Tests\Easydict.UIAutomation.Tests.csproj"
}
else {
    $TestProject = Get-FullPath $TestProject (Get-Location)
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot (Join-Path "artifacts\renderer-comparison" (Get-Date -Format "yyyyMMdd-HHmmss"))
}
else {
    $OutputDir = Get-FullPath $OutputDir (Get-Location)
}
New-Directory $OutputDir

if (-not (Test-Path -LiteralPath $TestProject)) {
    throw "UIAutomation test project not found at '$TestProject'."
}

if (-not $SkipBuild) {
    $appProject = Join-Path $dotnetRoot "src\Easydict.WinUI\Easydict.WinUI.csproj"
    & dotnet build $appProject -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "WinUI application build failed."
    }

    & dotnet build $TestProject -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "UIAutomation test build failed."
    }
}

if ([string]::IsNullOrWhiteSpace($AppExePath)) {
    $AppExePath = Find-AppExecutable $dotnetRoot $Configuration
}
else {
    $AppExePath = Get-FullPath $AppExePath (Get-Location)
}
if (-not (Test-Path -LiteralPath $AppExePath)) {
    throw "App executable not found at '$AppExePath'."
}

$gpuCounterAvailability = Get-GpuCounterAvailability
$environmentPath = Join-Path $OutputDir "environment.json"
Get-EnvironmentMetadata $gpuCounterAvailability | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $environmentPath -Encoding UTF8
if (-not $gpuCounterAvailability.available) {
    Write-Warning "GPU Process Memory counters are unavailable; preserving Private Bytes comparison only. $($gpuCounterAvailability.error)"
}

$runs = New-Object System.Collections.Generic.List[object]
$sequence = 0
for ($iteration = 1; $iteration -le $RunsPerBackend; $iteration++) {
    # Reverse each pair's first backend to balance cold-start order without mixing both backends in one app process.
    $pair = if (($iteration % 2) -eq 1) { @("Direct", "Xaml") } else { @("Xaml", "Direct") }
    foreach ($backend in $pair) {
        $sequence++
        $run = Invoke-RendererRun `
            -Sequence $sequence `
            -Iteration $iteration `
            -Backend $backend `
            -ComparisonOutputDir $OutputDir `
            -GateScript $gateScript `
            -ResolvedAppExePath $AppExePath `
            -ResolvedTestProject $TestProject `
            -BuildConfiguration $Configuration `
            -EnabledCardCount $CardCount `
            -DeterministicResultText $ResultText `
            -InitialIdle $InitialIdleSeconds `
            -PostCloseIdle $PostCloseIdleSeconds `
            -GpuInterval $GpuSampleIntervalMilliseconds `
            -StreamingUpdates $StreamingUpdateCount `
            -StreamingUpdateInterval $StreamingUpdateIntervalMilliseconds `
            -GpuCountersAvailable $gpuCounterAvailability.available `
            -SkipToolInstallForRun $SkipToolInstall
        $runs.Add($run)
    }
}

$comparison = [pscustomobject]@{
    schemaVersion = 4
    capturedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    configuration = $Configuration
    appExePath = $AppExePath
    testProject = $TestProject
    runsPerBackend = $RunsPerBackend
    cardCount = $CardCount
    initialIdleSeconds = $InitialIdleSeconds
    postCloseIdleSeconds = $PostCloseIdleSeconds
    gpuSampleIntervalMilliseconds = $GpuSampleIntervalMilliseconds
    rendererBenchmark = [pscustomobject]@{
        firstResult = [pscustomobject]@{
            directCompletion = "The first target card's Win2D draw returned."
            xamlCompletion = "The next XAML CompositionTarget.Rendering callback ran."
            measurement = "Elapsed time from the deterministic result mutation immediately before UI refresh to its backend completion callback."
        }
        streaming = [pscustomobject]@{
            updateCount = $StreamingUpdateCount
            updateIntervalMilliseconds = $StreamingUpdateIntervalMilliseconds
            processCpuSampling = "Raw typeperf Process percent processor time, one-second cadence, not normalized to logical-core count, restricted to samples bounded by controlled-stream start and completion markers."
        }
        stageTelemetry = [pscustomobject]@{
            pathPerRun = "renderer-stage-samples.json"
            stages = @("layout", "display-list", "draw")
            measurements = "Opt-in Direct renderer elapsed milliseconds and thread-local allocated bytes; empty when the app did not run the DEBUG probe."
        }
    }
    syntheticResult = $true
    environmentPath = $environmentPath
    gpuProcessMemory = [pscustomobject]@{
        available = $gpuCounterAvailability.available
        counters = $gpuCounterAvailability.counters
        error = $gpuCounterAvailability.error
        aggregation = "Each sample sums every GPU Process Memory adapter/LUID instance whose name begins with the app or DWM PID."
        limitation = "DWM is reported separately and must not be added to app counters as a per-app total."
    }
    # ponytail: raw per-run CSV remains the source of truth; medians summarize but do not hide variance.
    runs = $runs.ToArray()
    summary = [pscustomobject]@{
        direct = Get-RunMetricSummary $runs.ToArray() "Direct"
        xaml = Get-RunMetricSummary $runs.ToArray() "Xaml"
    }
    limitations = @(
        "Private Bytes measures app-process private committed memory, not CPU cost or total graphics memory.",
        "GPU Process Memory is unavailable on some RDP, software-adapter, and restricted environments; unavailable sampling does not fail a run.",
        "DWM is a shared system compositor. Its separately observed counters are contextual telemetry, not attributable per-app GPU totals.",
        "The deterministic result hook is a DEBUG-only UIAutomation path and avoids live translation services.",
        "First-result telemetry measures each backend's first renderer callback, not a Windows compositor-present timestamp.",
        "Streaming CPU is app-process percent processor time at one-second typeperf cadence; it excludes system-wide CPU and may not capture sub-second scheduler variation.",
        "Process and GPU result/stream values are nearest samples to the renderer callback timestamps, not exact callback-time readings."
    )
}

$summaryPath = Join-Path $OutputDir "comparison-summary.json"
$comparison | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host "Renderer comparison summary written to $summaryPath"
Write-Host "Direct app GPU Shared Usage median at result: $($comparison.summary.direct.appGpuSharedAtResult.median)"
Write-Host "Direct first-result render median: $($comparison.summary.direct.firstResultRenderLatencyMilliseconds.median)ms"
Write-Host "XAML first-result render median: $($comparison.summary.xaml.firstResultRenderLatencyMilliseconds.median)ms"
Write-Host "Direct streaming CPU median: $($comparison.summary.direct.streamingCpuPercent.median)%"
Write-Host "XAML streaming CPU median: $($comparison.summary.xaml.streamingCpuPercent.median)%"
Write-Host "XAML app GPU Shared Usage median at result: $($comparison.summary.xaml.appGpuSharedAtResult.median)"

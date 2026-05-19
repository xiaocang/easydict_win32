param(
    [string]$AppExePath = "dotnet/publish/x64/Easydict.WinUI.exe",
    [string]$OutputDir = "artifacts/memory-gate/nightly",
    [int]$DurationSeconds = 300,
    [string]$DiagnosticsToolVersion = "8.*",
    [string]$ScenarioCommand,
    [int]$ProcDumpCommitThresholdMb = 900,
    [string]$ProcDumpPath = "procdump.exe",
    [string]$VmMapPath = "vmmap.exe",
    [switch]$SkipToolInstall,
    [switch]$EnableWprReferenceSet,
    [switch]$EnableWprHeapVirtualAlloc,
    [switch]$EnableProcDump,
    [switch]$EnableVmMap
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

function Wait-TargetProcess([string]$ProcessName, [string]$ExpectedPath, [int]$TimeoutSeconds) {
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

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for process '$ProcessName'."
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

function Stop-WprIfStarted([bool]$Started, [string]$OutputPath) {
    if (-not $Started) {
        return
    }

    try {
        & wpr -stop $OutputPath
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "wpr -stop failed for '$OutputPath'."
        }
    }
    catch {
        Write-Warning "wpr -stop failed: $($_.Exception.Message)"
    }
}

$OutputDir = Get-FullPath $OutputDir
$ToolDir = Join-Path $OutputDir ".tools"
New-Directory $OutputDir
New-Directory $ToolDir

$AppExePath = Get-FullPath $AppExePath
if (-not (Test-Path -LiteralPath $AppExePath)) {
    throw "App executable not found: $AppExePath"
}

$dotnetCounters = Join-Path $ToolDir "dotnet-counters.exe"
$dotnetTrace = Join-Path $ToolDir "dotnet-trace.exe"
Install-DotnetTool "dotnet-counters" $dotnetCounters $ToolDir
Install-DotnetTool "dotnet-trace" $dotnetTrace $ToolDir

$env:EASYDICT_DEBUG_DISABLE_MOUSE_SELECTION_TRANSLATE = "1"

Write-Host "Launching Easydict for nightly memory profile..."
$appProcess = Start-Process -FilePath $AppExePath -PassThru -WindowStyle Hidden
$targetProcess = Wait-TargetProcess "Easydict.WinUI" $AppExePath 120
$processId = $targetProcess.Id
$instance = Get-ProcessCounterInstance $processId
Write-Host "Profiling process $processId ($instance)"

$typeperfCsv = Join-Path $OutputDir "typeperf.csv"
$counterJson = Join-Path $OutputDir "dotnet-counters.json"
$tracePath = Join-Path $OutputDir "gc-verbose.nettrace"
$refSetPath = Join-Path $OutputDir "wpr-reference-set.etl"
$heapPath = Join-Path $OutputDir "wpr-heap-virtualalloc.etl"
$vmmapPath = Join-Path $OutputDir "vmmap.txt"

$typeperfCounters = @(
    "\Process($instance)\Private Bytes",
    "\Process($instance)\Working Set - Private",
    "\Process($instance)\Handle Count",
    "\Process($instance)\Thread Count"
)
$quotedCounters = $typeperfCounters | ForEach-Object { '"' + $_ + '"' }
$typeperfArgs = ($quotedCounters + @("-si", "1", "-f", "CSV", "-o", '"' + $typeperfCsv + '"')) -join " "
$typeperfProcess = Start-Process -FilePath "typeperf.exe" -ArgumentList $typeperfArgs -RedirectStandardOutput (Join-Path $OutputDir "typeperf.out.log") -RedirectStandardError (Join-Path $OutputDir "typeperf.err.log") -PassThru -WindowStyle Hidden

$counterArgs = "collect --process-id $processId --counters System.Runtime --format json --output `"$counterJson`""
$counterProcess = Start-Process -FilePath $dotnetCounters -ArgumentList $counterArgs -RedirectStandardOutput (Join-Path $OutputDir "dotnet-counters.out.log") -RedirectStandardError (Join-Path $OutputDir "dotnet-counters.err.log") -PassThru -WindowStyle Hidden

$traceDuration = [TimeSpan]::FromSeconds($DurationSeconds).ToString("c")
$traceArgs = "collect -p $processId --profile gc-verbose --duration $traceDuration --output `"$tracePath`""
$traceProcess = Start-Process -FilePath $dotnetTrace -ArgumentList $traceArgs -RedirectStandardOutput (Join-Path $OutputDir "dotnet-trace.out.log") -RedirectStandardError (Join-Path $OutputDir "dotnet-trace.err.log") -PassThru -WindowStyle Hidden

$wprReferenceStarted = $false
$wprHeapStarted = $false
if ($EnableWprReferenceSet -and (Get-Command wpr.exe -ErrorAction SilentlyContinue)) {
    try {
        & wpr -start referenceset -filemode
        $wprReferenceStarted = $LASTEXITCODE -eq 0
    }
    catch {
        Write-Warning "WPR reference set start failed: $($_.Exception.Message)"
    }
}

if ($EnableWprHeapVirtualAlloc -and (Get-Command wpr.exe -ErrorAction SilentlyContinue)) {
    try {
        & wpr -start heap -filemode
        $wprHeapStarted = $LASTEXITCODE -eq 0
    }
    catch {
        Write-Warning "WPR heap start failed: $($_.Exception.Message)"
    }
}

$procDumpProcess = $null
if ($EnableProcDump -and (Get-Command $ProcDumpPath -ErrorAction SilentlyContinue)) {
    $dumpDir = Join-Path $OutputDir "procdump"
    New-Directory $dumpDir
    $procDumpArgs = "-accepteula -ma -m $ProcDumpCommitThresholdMb -n 1 $processId `"$dumpDir`""
    $procDumpProcess = Start-Process -FilePath $ProcDumpPath -ArgumentList $procDumpArgs -RedirectStandardOutput (Join-Path $OutputDir "procdump.out.log") -RedirectStandardError (Join-Path $OutputDir "procdump.err.log") -PassThru -WindowStyle Hidden
}

if (-not [string]::IsNullOrWhiteSpace($ScenarioCommand)) {
    Write-Host "Running scenario command: $ScenarioCommand"
    $scenarioProcess = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -Command $ScenarioCommand" -Wait -PassThru -WindowStyle Hidden
    if ($scenarioProcess.ExitCode -ne 0) {
        Write-Warning "Scenario command exited with code $($scenarioProcess.ExitCode)."
    }
}
else {
    Start-Sleep -Seconds $DurationSeconds
}

Stop-WprIfStarted $wprReferenceStarted $refSetPath
Stop-WprIfStarted $wprHeapStarted $heapPath

if ($EnableVmMap -and (Get-Command $VmMapPath -ErrorAction SilentlyContinue)) {
    try {
        & $VmMapPath -accepteula -p $processId $vmmapPath
    }
    catch {
        Write-Warning "VMMap capture failed: $($_.Exception.Message)"
    }
}

Stop-IfRunning $procDumpProcess
Stop-IfRunning $counterProcess
Stop-IfRunning $typeperfProcess
$traceProcess.WaitForExit([Math]::Max(30000, ($DurationSeconds + 30) * 1000)) | Out-Null
Stop-IfRunning $traceProcess

try {
    if (-not $targetProcess.HasExited) {
        Stop-Process -Id $targetProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
catch {
    Write-Warning "Failed to stop app process: $($_.Exception.Message)"
}

$summary = [pscustomobject]@{
    scenario = "nightly-memory-profile"
    processId = $processId
    durationSeconds = $DurationSeconds
    artifacts = [pscustomobject]@{
        typeperfCsv = $typeperfCsv
        dotnetCountersJson = $counterJson
        dotnetTrace = $tracePath
        wprReferenceSet = if ($wprReferenceStarted) { $refSetPath } else { $null }
        wprHeapVirtualAlloc = if ($wprHeapStarted) { $heapPath } else { $null }
        vmmap = if (Test-Path -LiteralPath $vmmapPath) { $vmmapPath } else { $null }
    }
}

$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDir "summary.json") -Encoding UTF8
Write-Host "Nightly memory profile artifacts written to $OutputDir"

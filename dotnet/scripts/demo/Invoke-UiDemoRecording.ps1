# Records the real WinUI app while a UI automation scenario drives it, then turns the capture into a
# small GIF. Local and CI share this one script; see README.md in this folder.
#
# Handshake with the scenario (the recording must not miss, or race, the operations):
#   1. this script creates a handshake directory and passes it as $env:EASYDICT_DEMO_HANDSHAKE_DIR;
#   2. the scenario writes `ready.json` (pid, hwnd, exePath, windowTitle) once the app and the target
#      page are ready, then waits;
#   3. this script validates that pid/hwnd belong to *this* run, starts the capture, and writes
#      `continue`;
#   4. only then does the scenario perform the recorded operations.
# Without that handshake a title match could capture a window that was already on the desktop, and
# the operations could finish before ffmpeg even started.
param(
    [Parameter(Mandatory = $true)][string]$ScenarioName,
    # Either a `dotnet test --filter` expression (normal case) or, for local harnessing, the path of a
    # script that performs the scenario itself.
    [string]$TestFilter,
    [string]$ScenarioCommand,
    [string]$TestProject = 'dotnet/tests/Easydict.UIAutomation.Tests/Easydict.UIAutomation.Tests.csproj',
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$AppExePath,
    [string]$OutputDir = 'artifacts/ui-demos',

    # `desktop` captures the whole screen, which is what includes separate dialogs; the blank-desktop
    # rule plus the pid/hwnd handshake is what keeps it honest. `title=<text>` captures one window.
    [string]$CaptureTarget = 'desktop',
    [int]$WindowWaitSeconds = 90,
    [int]$CaptureStartDelayMs = 500,

    [int]$MaxSeconds = 60,
    [int]$CaptureFps = 12,
    [int]$GifFps = 20,
    [double]$Scale = 0.75,
    [double]$Speed = 2.0,
    [string]$FFmpegPath = 'ffmpeg',
    [switch]$Build,

    [switch]$Publish,
    [string]$Repository,
    [int]$PullRequestNumber,
    [string]$SourceSha,
    [string]$ArtifactBranch = 'scratch/pr-demos',
    [switch]$KeepVideo,
    # Leaves ffmpeg out entirely; used to exercise the handshake without occupying the desktop.
    [switch]$SkipCapture,
    # Advanced: overrides the capture input (e.g. `testsrc=size=320x180:rate=10`) so the frame gate can be
    # exercised without recording a real desktop.
    [string]$CaptureSource,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) { Write-Host "[ui-demo] $Message" }

$ffmpeg = (Get-Command $FFmpegPath -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw "ffmpeg not found ('$FFmpegPath'). Install it or pass -FFmpegPath." }

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }

$videoPath = Join-Path $OutputDir "$ScenarioName.mp4"
$gifPath = Join-Path $OutputDir "$ScenarioName.gif"
$metaPath = Join-Path $OutputDir "$ScenarioName.meta.json"
$ffmpegLog = Join-Path $OutputDir "$ScenarioName.ffmpeg.log"
$testLog = Join-Path $OutputDir "$ScenarioName.test.log"

# Owns the HWND -> PID check; a matching title alone proves nothing.
Add-Type -Namespace UiDemo -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint processId);
'@ -ErrorAction SilentlyContinue

$handshakeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("easydict-demo-" + [guid]::NewGuid().ToString('N'))
if (-not $DryRun) { New-Item -ItemType Directory -Path $handshakeDir -Force | Out-Null }
$readyPath = Join-Path $handshakeDir 'ready.json'
$continuePath = Join-Path $handshakeDir 'continue'

# --- 1. Start the scenario ---------------------------------------------------------------------
if ($ScenarioCommand) {
    $testArgs = @('-NoProfile', '-File', [System.IO.Path]::GetFullPath($ScenarioCommand))
    if ($TestFilter) { $testArgs += @('-TestFilter', $TestFilter) }
    $testArgs += @('-HandshakeDir', $handshakeDir)
    $testRunner = 'pwsh'
}
else {
    if ([string]::IsNullOrWhiteSpace($TestFilter)) { throw '-TestFilter is required unless -ScenarioCommand is used.' }
    $testArgs = @('test', $TestProject, '-c', $Configuration, ('-p:Platform=' + $Platform), '--filter', $TestFilter)
    if (-not $Build) { $testArgs += '--no-build' }
    $testArgs += @('--logger', 'console;verbosity=minimal')
    $testRunner = 'dotnet'
}

$env:EASYDICT_DEMO_HANDSHAKE_DIR = $handshakeDir
if ($AppExePath) { $env:EASYDICT_EXE_PATH = [System.IO.Path]::GetFullPath($AppExePath) }
Write-Step "$testRunner $($testArgs -join ' ')"
Write-Step "handshake dir: $handshakeDir"

$testProcess = $null
$stdoutTask = $null
$stderrTask = $null
if (-not $DryRun) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $testRunner
    foreach ($argument in $testArgs) { $startInfo.ArgumentList.Add($argument) }
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $testProcess = [System.Diagnostics.Process]::Start($startInfo)
    # ReadToEndAsync avoids the background-thread scriptblock callback (no runspace, unsafe shared
    # buffers) and cannot deadlock on a full pipe.
    $stdoutTask = $testProcess.StandardOutput.ReadToEndAsync()
    $stderrTask = $testProcess.StandardError.ReadToEndAsync()
}

# --- 2. Wait for the scenario's ready signal ---------------------------------------------------
$ready = $null
$deadline = (Get-Date).AddSeconds($WindowWaitSeconds)
while (-not $DryRun -and (Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $readyPath) {
        try { $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json } catch { $ready = $null }
        if ($ready) { break }
    }
    if ($testProcess -and $testProcess.HasExited) { break }
    Start-Sleep -Milliseconds 300
}

if ($DryRun) {
    $ready = [pscustomobject]@{ pid = 0; hwnd = 0; exePath = 'dry-run'; windowTitle = 'dry-run' }
    Write-Step 'dry run: skipping the scenario and the handshake wait'
}
if (-not $ready) {
    if ($testProcess -and -not $testProcess.HasExited) { try { $testProcess.Kill($true) } catch { } }
    throw "The scenario did not report readiness within ${WindowWaitSeconds}s (expected $readyPath). Log: $testLog"
}

# The window must belong to the process the scenario just started.
$appPid = [int]$ready.pid
$appHwnd = [int64]$ready.hwnd
if (-not $DryRun) {
    $appProcess = Get-Process -Id $appPid -ErrorAction SilentlyContinue
    if (-not $appProcess) { throw "ready.json points at pid $appPid, which is not running." }
    if ($appHwnd -ne 0) {
        $ownerPid = [uint32]0
        [void][UiDemo.Native]::GetWindowThreadProcessId([System.IntPtr]$appHwnd, [ref]$ownerPid)
        if ($ownerPid -ne $appPid) {
            throw "ready.json hwnd $appHwnd belongs to pid $ownerPid, not to the reported pid $appPid."
        }
    }
    if ($ready.exePath -and $AppExePath) {
        $reported = ([string]$ready.exePath) -replace '/', '\'
        $expected = ([System.IO.Path]::GetFullPath($AppExePath)) -replace '/', '\'
        if (-not [string]::Equals($reported, $expected, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "ready.json reports exe '$reported' but this run launched '$expected'."
        }
    }
}
Write-Step "scenario ready: pid $appPid, hwnd $appHwnd, title '$($ready.windowTitle)'"

# --- 3. Start the capture, then release the scenario ------------------------------------------
$grabInput = if ($CaptureTarget -like 'title=*') { $CaptureTarget } else { 'desktop' }
$capture = $null
if (-not $DryRun -and -not $SkipCapture) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $ffmpeg
    if ($CaptureSource) {
        # Harness path: any lavfi source, so the progress gating can be exercised without the desktop.
        foreach ($argument in @('-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', $CaptureSource,
                '-t', "$MaxSeconds", '-pix_fmt', 'yuv420p', '-progress', 'pipe:2', '-nostats', $videoPath)) {
            $startInfo.ArgumentList.Add($argument)
        }
    }
    else {
        # `-progress pipe:2` turns stderr into machine-readable key=value lines, which is what lets the
        # handshake wait for a real frame instead of sleeping for a fixed time.
        foreach ($argument in @(
                '-hide_banner', '-loglevel', 'error', '-y',
                '-f', 'gdigrab', '-framerate', "$CaptureFps",
                '-i', $grabInput,
                '-t', "$MaxSeconds",
                '-pix_fmt', 'yuv420p',
                '-progress', 'pipe:2', '-nostats',
                $videoPath)) {
            $startInfo.ArgumentList.Add($argument)
        }
    }
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $capture = [System.Diagnostics.Process]::Start($startInfo)

    # Release the scenario only once FFmpeg reports a real frame; a fixed sleep proves nothing about
    # whether anything is being captured, and an early exit must fail instead of recording an empty file.
    $gateDeadline = (Get-Date).AddSeconds([Math]::Max($CaptureStartDelayMs, 15000) / 1000)
    $sawFrame = $false
    $gateLog = New-Object System.Text.StringBuilder
    while (-not $sawFrame -and (Get-Date) -lt $gateDeadline) {
        if ($capture.HasExited) { break }
        $lineTask = $capture.StandardError.ReadLineAsync()
        if (-not $lineTask.Wait(1000)) { continue }
        $line = $lineTask.Result
        if ($null -eq $line) { break }
        [void]$gateLog.AppendLine($line)
        if ($line -match '^frame=(\d+)' -and [int]$Matches[1] -ge 1) { $sawFrame = $true }
        elseif ($line -match '^out_time_(us|ms)=(\d+)' -and [long]$Matches[2] -gt 0) { $sawFrame = $true }
    }

    if (-not $sawFrame) {
        try { $capture.Kill($true) } catch { }
        throw "FFmpeg reported no frame before the gate expired; refusing to release the scenario. Output: $($gateLog.ToString())"
    }
    Write-Step 'capture reported its first frame; releasing the scenario'
    $captureErrTask = $capture.StandardError.ReadToEndAsync()
}

# Only now may the scenario perform the operations the demo is meant to show.
if (-not $DryRun) {
    [System.IO.File]::WriteAllText($continuePath, (Get-Date).ToUniversalTime().ToString('o'), (New-Object System.Text.UTF8Encoding($false)))
    Write-Step 'capture running; scenario released'
}

# --- 4. Wait for the scenario to finish, then stop the capture --------------------------------
$testExit = 0
$stdout = ''
$stderr = ''
try {
    if ($testProcess) {
        if (-not $testProcess.WaitForExit([Math]::Max($MaxSeconds, 60) * 1000)) {
            $testExit = 1
            Write-Step 'scenario did not finish in time'
            try { $testProcess.Kill($true) } catch { }
        }
        else {
            $testExit = $testProcess.ExitCode
        }
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
    }
}
finally {
    if ($capture -and -not $capture.HasExited) {
        try { $capture.StandardInput.WriteLine('q'); $capture.StandardInput.Flush() } catch { }
        if (-not $capture.WaitForExit(20000)) { try { $capture.Kill($true) } catch { } ; $capture.WaitForExit(5000) }
    }
    if ($stdout -or $stderr) {
        [System.IO.File]::WriteAllText($testLog, ($stdout + "`n" + $stderr), (New-Object System.Text.UTF8Encoding($false)))
    }
}

if ($capture) {
    $captureError = if ($captureErrTask) { $captureErrTask.Result } else { '' }
    [System.IO.File]::WriteAllText($ffmpegLog, $captureError, (New-Object System.Text.UTF8Encoding($false)))
    if ($capture.ExitCode -ne 0) { throw "ffmpeg exited with code $($capture.ExitCode). See $ffmpegLog." }
}

if ($testExit -ne 0) { Write-Step "scenario exited with code $testExit; the recording is still produced." }

if (-not $DryRun -and -not $SkipCapture) {
    if (-not (Test-Path -LiteralPath $videoPath)) { throw "No capture was produced at $videoPath." }
    $videoSize = (Get-Item -LiteralPath $videoPath).Length
    Write-Step "captured $([Math]::Round($videoSize / 1MB, 2)) MB"
    if ($videoSize -lt 4096) { throw "Capture looks empty ($videoSize bytes); check $ffmpegLog." }
}

# --- 5. Encode the GIF and write provenance --------------------------------------------------
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$videoFilter = 'setpts=' + (1.0 / $Speed).ToString($culture) + '*PTS' +
    ',scale=iw*' + $Scale.ToString($culture) + ':ih*' + $Scale.ToString($culture) + ':flags=lanczos' +
    ',fps=' + $GifFps + ',split[a][b];[a]palettegen[p];[b][p]paletteuse'

Write-Step "$ffmpeg -i $videoPath -vf $videoFilter $gifPath"
if (-not $DryRun -and -not $SkipCapture) {
    & $ffmpeg -hide_banner -loglevel warning -y -i $videoPath -vf $videoFilter $gifPath
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg GIF encode exited with code $LASTEXITCODE." }

    $gifSize = (Get-Item -LiteralPath $gifPath).Length
    Write-Step "gif: $gifPath ($([Math]::Round($gifSize / 1KB)) KB)"
    if ($gifSize -gt 10MB) { Write-Warning 'GIF is larger than 10 MB; crop or lower -GifFps before publishing.' }
    if (-not $KeepVideo) { Remove-Item -LiteralPath $videoPath -Force -ErrorAction SilentlyContinue }

    $meta = [ordered]@{
        scenario = $ScenarioName
        sourceSha = $SourceSha
        capturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        appPid = $appPid
        appHwnd = $appHwnd
        exePath = [string]$ready.exePath
        windowTitle = [string]$ready.windowTitle
        captureTarget = $grabInput
        testExitCode = $testExit
        testFilter = $TestFilter
    }
    [System.IO.File]::WriteAllText($metaPath, ($meta | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
}

# --- 6. Publish --------------------------------------------------------------------------------
if ($Publish) {
    if ([string]::IsNullOrWhiteSpace($Repository) -or [string]::IsNullOrWhiteSpace($SourceSha) -or $PullRequestNumber -le 0) {
        throw '-Repository, -PullRequestNumber and -SourceSha are required with -Publish.'
    }
    $publishScript = Join-Path $PSScriptRoot 'Publish-UiDemoToPullRequest.ps1'
    & $publishScript -GifPath $gifPath -MetaPath $metaPath -ScenarioName $ScenarioName -Repository $Repository `
        -PullRequestNumber $PullRequestNumber -SourceSha $SourceSha -ArtifactBranch $ArtifactBranch `
        -TestExitCode $testExit -DryRun:$DryRun
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the demo failed.' }
}

if ($testExit -ne 0) { throw "Scenario '$ScenarioName' failed; the recording was still produced." }
Write-Step 'done'

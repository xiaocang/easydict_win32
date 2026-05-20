param(
    [string]$SourceDir = "artifacts/memory-gate/nightly",
    [string]$BranchName = "scratch/memory-nightly",
    [string]$OutputRoot = "memory-nightly",
    [string]$RunId = $env:GITHUB_RUN_ID,
    [string]$RunAttempt = $env:GITHUB_RUN_ATTEMPT,
    [string]$SourceSha = $env:GITHUB_SHA,
    [string]$SourceRef = $env:GITHUB_REF_NAME,
    [int]$RetentionDays = 60,
    [int]$RetentionRuns = 60
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

function Remove-DirectoryInsideRoot([string]$Root, [string]$Path) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $pathFull = [System.IO.Path]::GetFullPath($Path)

    if (-not $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove '$pathFull' because it is outside '$rootFull'."
    }

    if (Test-Path -LiteralPath $pathFull) {
        Remove-Item -LiteralPath $pathFull -Recurse -Force
    }
}

function Invoke-Git([string]$WorkingDirectory, [string[]]$Arguments) {
    & git -C $WorkingDirectory @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Copy-ComparableArtifacts([string]$Source, [string]$Destination) {
    $patterns = @(
        "summary.json",
        "typeperf.csv",
        "dotnet-counters.json",
        "*.heapstat.txt",
        "*.log",
        "vmmap.txt"
    )

    $copied = New-Object System.Collections.Generic.List[object]
    foreach ($pattern in $patterns) {
        $matches = @(Get-ChildItem -LiteralPath $Source -Filter $pattern -File -ErrorAction SilentlyContinue)
        foreach ($match in $matches) {
            $targetPath = Join-Path $Destination $match.Name
            Copy-Item -LiteralPath $match.FullName -Destination $targetPath -Force
            $copied.Add([pscustomobject]@{
                path = $match.Name
                bytes = $match.Length
            })
        }
    }

    return @($copied)
}

function Get-HeavyArtifacts([string]$Source) {
    $heavyExtensions = @(".nettrace", ".etl", ".dmp", ".gcdump", ".zip")
    $artifacts = New-Object System.Collections.Generic.List[object]
    $files = @(Get-ChildItem -LiteralPath $Source -Recurse -File -ErrorAction SilentlyContinue)
    foreach ($file in $files) {
        if ($heavyExtensions -contains $file.Extension.ToLowerInvariant()) {
            $relativePath = [System.IO.Path]::GetRelativePath($Source, $file.FullName)
            $artifacts.Add([pscustomobject]@{
                path = $relativePath
                bytes = $file.Length
            })
        }
    }

    return @($artifacts)
}

function Get-RunCapturedAtUtc([System.IO.DirectoryInfo]$RunDirectory) {
    $manifestPath = Join-Path $RunDirectory.FullName "manifest.json"
    if (Test-Path -LiteralPath $manifestPath) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            if ($null -ne $manifest -and -not [string]::IsNullOrWhiteSpace($manifest.capturedAtUtc)) {
                return [DateTime]::Parse(
                    [string]$manifest.capturedAtUtc,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
            }
        }
        catch {
            Write-Warning "Could not parse capturedAtUtc from '$manifestPath': $($_.Exception.Message)"
        }
    }

    if ($RunDirectory.Name -match "^(?<timestamp>\d{8}T\d{6}Z)") {
        return [DateTime]::ParseExact(
            $Matches["timestamp"],
            "yyyyMMdd'T'HHmmss'Z'",
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
    }

    return $null
}

$SourceDir = Get-FullPath $SourceDir
if (-not (Test-Path -LiteralPath $SourceDir)) {
    Write-Warning "Memory profile source directory does not exist: $SourceDir"
    return
}

$repoRoot = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "Could not resolve git repository root."
}

$tempRoot = if ($env:RUNNER_TEMP) { Get-FullPath $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$scratchWorktree = Join-Path $tempRoot "easydict-memory-scratch-branch"

if (Test-Path -LiteralPath $scratchWorktree) {
    & git -C $repoRoot worktree remove --force $scratchWorktree 2>$null | Out-Null
    if (Test-Path -LiteralPath $scratchWorktree) {
        Remove-DirectoryInsideRoot $tempRoot $scratchWorktree
    }
}

$refSpec = "+refs/heads/${BranchName}:refs/remotes/origin/${BranchName}"
& git -C $repoRoot fetch origin $refSpec --depth=1 2>$null | Out-Null
$branchExists = $LASTEXITCODE -eq 0

if ($branchExists) {
    Invoke-Git $repoRoot @("worktree", "add", "-B", $BranchName, $scratchWorktree, "origin/$BranchName")
}
else {
    Invoke-Git $repoRoot @("worktree", "add", "--detach", $scratchWorktree, "HEAD")
    Invoke-Git $scratchWorktree @("checkout", "--orphan", $BranchName)
    Invoke-Git $scratchWorktree @("rm", "-rf", ".")
}

Invoke-Git $scratchWorktree @("config", "user.name", "github-actions[bot]")
Invoke-Git $scratchWorktree @("config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com")

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$shortSha = if (-not [string]::IsNullOrWhiteSpace($SourceSha) -and $SourceSha.Length -ge 7) {
    $SourceSha.Substring(0, 7)
}
else {
    $SourceSha
}

$runKeyParts = New-Object System.Collections.Generic.List[string]
$runKeyParts.Add($timestamp)
if (-not [string]::IsNullOrWhiteSpace($shortSha)) {
    $runKeyParts.Add($shortSha)
}
if (-not [string]::IsNullOrWhiteSpace($RunId)) {
    $runKeyParts.Add("run$RunId")
}
if (-not [string]::IsNullOrWhiteSpace($RunAttempt)) {
    $runKeyParts.Add("attempt$RunAttempt")
}

$runKey = $runKeyParts -join "-"
$branchRoot = Join-Path $scratchWorktree $OutputRoot
$runsRoot = Join-Path $branchRoot "runs"
$runDir = Join-Path $runsRoot $runKey
$latestDir = Join-Path $branchRoot "latest"
New-Directory $runDir

$copiedArtifacts = Copy-ComparableArtifacts $SourceDir $runDir
$skippedHeavyArtifacts = Get-HeavyArtifacts $SourceDir

$manifest = [ordered]@{
    schemaVersion = 1
    branchName = $BranchName
    outputRoot = $OutputRoot
    runKey = $runKey
    runId = $RunId
    runAttempt = $RunAttempt
    sourceSha = $SourceSha
    sourceRef = $SourceRef
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    retentionDays = $RetentionDays
    retentionRuns = $RetentionRuns
    copiedArtifacts = @($copiedArtifacts)
    skippedHeavyArtifacts = @($skippedHeavyArtifacts)
}
$manifestPath = Join-Path $runDir "manifest.json"
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Remove-DirectoryInsideRoot $scratchWorktree $latestDir
New-Directory $latestDir
Get-ChildItem -LiteralPath $runDir -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $latestDir -Recurse -Force
}

if (Test-Path -LiteralPath $runsRoot) {
    if ($RetentionDays -gt 0) {
        $cutoffUtc = (Get-Date).ToUniversalTime().AddDays(-$RetentionDays)
        foreach ($oldRun in @(Get-ChildItem -LiteralPath $runsRoot -Directory)) {
            if ([string]::Equals($oldRun.Name, $runKey, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $capturedAtUtc = Get-RunCapturedAtUtc $oldRun
            if ($null -ne $capturedAtUtc -and $capturedAtUtc -lt $cutoffUtc) {
                Write-Host "Removing memory profile run '$($oldRun.Name)' because it is older than $RetentionDays days."
                Remove-DirectoryInsideRoot $scratchWorktree $oldRun.FullName
            }
        }
    }

    $runDirs = @(Get-ChildItem -LiteralPath $runsRoot -Directory | Sort-Object Name -Descending)
    if ($RetentionRuns -gt 0) {
        foreach ($oldRun in @($runDirs | Select-Object -Skip $RetentionRuns)) {
            Remove-DirectoryInsideRoot $scratchWorktree $oldRun.FullName
        }
    }
}

$indexRuns = New-Object System.Collections.Generic.List[object]
foreach ($dir in @(Get-ChildItem -LiteralPath $runsRoot -Directory | Sort-Object Name -Descending)) {
    $runManifestPath = Join-Path $dir.FullName "manifest.json"
    if (Test-Path -LiteralPath $runManifestPath) {
        $indexRuns.Add((Get-Content -LiteralPath $runManifestPath -Raw | ConvertFrom-Json))
    }
}

$index = [ordered]@{
    schemaVersion = 1
    outputRoot = $OutputRoot
    latestRunKey = $runKey
    updatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    retentionDays = $RetentionDays
    retentionRuns = $RetentionRuns
    runs = @($indexRuns)
}
$index | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $branchRoot "index.json") -Encoding UTF8

Invoke-Git $scratchWorktree @("add", "-A", $OutputRoot)
$status = (& git -C $scratchWorktree status --short) -join "`n"
if ([string]::IsNullOrWhiteSpace($status)) {
    Write-Host "No memory profile changes to publish."
    return
}

Invoke-Git $scratchWorktree @("commit", "-m", "Publish nightly memory results $runKey")
Invoke-Git $scratchWorktree @("push", "origin", "HEAD:refs/heads/$BranchName")
Write-Host "Published comparable memory profile artifacts to branch '$BranchName' under '$OutputRoot'."

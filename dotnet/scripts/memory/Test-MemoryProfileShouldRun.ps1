param(
    [string]$BranchName = "scratch/memory-nightly",
    [string]$OutputRoot = "memory-nightly",
    [string]$SourceSha = $env:GITHUB_SHA
)

$ErrorActionPreference = "Stop"

function Write-GithubOutput([string]$Name, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        return
    }

    "$Name=$Value" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

$shouldRun = $true
$reason = "no previous memory profile result was found"
$lastSourceSha = ""

if ([string]::IsNullOrWhiteSpace($SourceSha)) {
    $reason = "current source sha is unavailable"
}
else {
    $repoRoot = (& git rev-parse --show-toplevel).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
        throw "Could not resolve git repository root."
    }

    $refSpec = "+refs/heads/${BranchName}:refs/remotes/origin/${BranchName}"
    & git -C $repoRoot fetch origin $refSpec --depth=1 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $indexRef = "origin/${BranchName}:$OutputRoot/index.json"
        $indexJson = (& git -C $repoRoot show $indexRef 2>$null) -join "`n"
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($indexJson)) {
            $index = $indexJson | ConvertFrom-Json
            $latestRun = @($index.runs) | Where-Object { $_.runKey -eq $index.latestRunKey } | Select-Object -First 1
            if ($null -eq $latestRun) {
                $latestRun = @($index.runs) | Select-Object -First 1
            }

            if ($null -ne $latestRun -and -not [string]::IsNullOrWhiteSpace($latestRun.sourceSha)) {
                $lastSourceSha = [string]$latestRun.sourceSha
                if ([string]::Equals($lastSourceSha, $SourceSha, [StringComparison]::OrdinalIgnoreCase)) {
                    $shouldRun = $false
                    $reason = "current source sha already has nightly memory results"
                }
                else {
                    $reason = "current source sha differs from latest profiled sha"
                }
            }
            else {
                $reason = "scratch branch index does not contain a source sha"
            }
        }
        else {
            $reason = "scratch branch does not contain $OutputRoot/index.json"
        }
    }
    else {
        # A failed fetch has three very different meanings and only one of them is the normal
        # first run, so ask the remote instead of assuming. The full ref name keeps the lookup
        # exact - no prefix or glob matching.
        $branchRef = "refs/heads/$BranchName"
        $remoteRefs = (& git -C $repoRoot ls-remote --heads origin $branchRef 2>$null) -join "`n"
        $lsRemoteExitCode = $LASTEXITCODE

        if ($lsRemoteExitCode -ne 0) {
            throw "Could not reach origin to resolve $branchRef (git ls-remote exited $lsRemoteExitCode)."
        }

        if (-not [string]::IsNullOrWhiteSpace($remoteRefs)) {
            throw "$branchRef exists on origin but could not be fetched; refusing to treat that as a first run."
        }

        $reason = "scratch branch does not exist yet"
    }
}

$shouldRunValue = $shouldRun.ToString().ToLowerInvariant()
Write-GithubOutput "should_run" $shouldRunValue
Write-GithubOutput "reason" $reason
Write-GithubOutput "last_source_sha" $lastSourceSha

Write-Host "should_run=$shouldRunValue"
Write-Host "reason=$reason"
if (-not [string]::IsNullOrWhiteSpace($lastSourceSha)) {
    Write-Host "last_source_sha=$lastSourceSha"
}

# GitHub Actions runs a `shell: pwsh` step through a wrapper that appends
# `if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }`, so the status of
# the last native command decides the step result. The last native command here is the fetch
# above, which legitimately fails while the scratch branch does not exist yet - that is the
# normal first run. That turned a successful "should run" decision into a failed step and
# skipped every profiling step behind it. The decision is complete at this point, so end the
# script with an explicit success code; genuine failures still throw before reaching it.
exit 0

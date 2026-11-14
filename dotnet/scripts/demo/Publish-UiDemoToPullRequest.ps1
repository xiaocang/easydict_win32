# Publishes a recorded GIF to a pull request so it plays inline, and records the demo in the PR body
# on first publication.
#
# An Actions artifact link downloads a file, so the GIF is pushed to a data branch and referenced
# through its raw.githubusercontent URL. Nothing is committed to master.
#
# Safety rules:
# - A failed `git fetch` is NOT evidence that the branch is missing; the ref is checked explicitly
#   with `ls-remote` so a network/auth failure aborts instead of creating a fresh orphan branch.
# - The worktree lives in a unique temp path and is added without `-B`, so shared refs and the three
#   working copies are untouched.
# - The run refuses to publish when the PR head no longer matches the recorded SHA (stale run).
# - Only comments/bodies written by our own bot are updated; anything else is left alone.
param(
    [Parameter(Mandatory = $true)][string]$GifPath,
    # Provenance written by the recorder. Only this metadata influences the comment, never anything
    # else produced by the pull request's code.
    [string]$MetaPath,
    [Parameter(Mandatory = $true)][string]$ScenarioName,
    [Parameter(Mandatory = $true)][string]$Repository,          # owner/name
    [Parameter(Mandatory = $true)][int]$PullRequestNumber,
    [Parameter(Mandatory = $true)][string]$SourceSha,
    [string]$ArtifactBranch = 'scratch/pr-demos',
    [string]$CommitMessage,
    [int]$TestExitCode = 0,
    [string]$ScenarioDescription,
    [string]$BotLogin = 'github-actions[bot]',
    # The first publication also writes a fixed region into the PR body; later runs only update the
    # comment, so the body keeps the first demo instead of churning.
    # Dry-run only: simulates the pull request's current body so the aggregation rules can be tested.
    [string]$DryRunPrBody,
    # Comment-first is the default (the pull request comment carries the GIF). The body region is only
    # maintained when this switch is passed, so routine CI runs never rewrite the description.
    [switch]$UpdatePullRequestBody,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) { Write-Host "[ui-demo:publish] $Message" }

function Invoke-Git([string]$WorkingDirectory, [string[]]$Arguments) {
    Write-Step "git $($Arguments -join ' ')"
    if ($DryRun) { return }
    & git -C $WorkingDirectory @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path -LiteralPath $GifPath)) { throw "GIF not found: $GifPath" }

# Treat everything from the recording as untrusted data: the scenario name becomes a path segment and
# the metadata must agree with the SHA this run claims to have recorded.
if ($ScenarioName -notmatch '^[a-z0-9][a-z0-9-]{0,63}$') { throw "Scenario name '$ScenarioName' is not a safe path segment." }
if ([System.IO.Path]::GetExtension($GifPath) -ne '.gif') { throw "Expected a .gif, got '$GifPath'." }
if (-not $DryRun) {
    $gifBytes = (Get-Item -LiteralPath $GifPath).Length
    if ($gifBytes -le 0 -or $gifBytes -gt 20MB) { throw "GIF size $gifBytes is outside the accepted range." }
}
if ($SourceSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'SourceSha must be a full 40-character commit SHA.' }
if ([string]::IsNullOrWhiteSpace($MetaPath)) { throw 'MetaPath is required to verify recording provenance.' }
if (-not (Test-Path -LiteralPath $MetaPath)) { throw "Metadata not found: $MetaPath" }
$meta = Get-Content -LiteralPath $MetaPath -Raw | ConvertFrom-Json
if ($meta.scenario -ne $ScenarioName) { throw "Metadata scenario '$($meta.scenario)' does not match '$ScenarioName'." }
if ([string]::IsNullOrWhiteSpace([string]$meta.sourceSha) -or $meta.sourceSha -ne $SourceSha) {
    throw "Metadata SHA '$($meta.sourceSha)' does not match '$SourceSha'."
}

$shortSha = if ($SourceSha.Length -ge 7) { $SourceSha.Substring(0, 7) } else { $SourceSha }
$relativePath = "pr-$PullRequestNumber/$SourceSha/$ScenarioName.gif"

# --- 0. Refuse to publish for a stale run -----------------------------------------------------
if (-not $DryRun) {
    Write-Step "checking pull request #$PullRequestNumber head"
    $prHead = & gh pr view $PullRequestNumber --repo $Repository --json headRefOid --jq '.headRefOid' 2>$null
    $prExit = $LASTEXITCODE
    if ($prExit -ne 0) { throw "Could not read pull request #$PullRequestNumber (exit $prExit)." }
    $prHead = ([string]$prHead).Trim()
    if ($prHead -ne $SourceSha) {
        throw "Refusing to publish: PR head is $prHead but this run recorded $SourceSha. A newer commit exists; let the new run publish."
    }
}
else {
    Write-Step 'dry run: skipping the pull request head check'
}

$repoRootOutput = & git rev-parse --show-toplevel 2>$null
$gitExit = $LASTEXITCODE
$repoRoot = @($repoRootOutput) | Select-Object -First 1
if ($gitExit -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) { throw 'Run this inside the repository.' }
$repoRoot = ([string]$repoRoot).Trim()

# --- 1. Is the data branch really missing, or did the network fail? ---------------------------
$lsRemoteOutput = & git -C $repoRoot ls-remote --heads origin "refs/heads/$ArtifactBranch" 2>$null
$lsRemoteExit = $LASTEXITCODE
if ($lsRemoteExit -ne 0) {
    throw "Could not reach origin to resolve refs/heads/$ArtifactBranch (git ls-remote exited $lsRemoteExit)."
}
$branchExists = -not [string]::IsNullOrWhiteSpace((@($lsRemoteOutput) -join ''))
Write-Step "data branch $ArtifactBranch exists: $branchExists"

# --- 2. Stage the GIF in a unique temp worktree -----------------------------------------------
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$worktree = Join-Path $tempRoot ("easydict-pr-demos-" + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $worktree) { Remove-Item -LiteralPath $worktree -Recurse -Force }

if ($branchExists) {
    Invoke-Git $repoRoot @('fetch', 'origin', "+refs/heads/${ArtifactBranch}:refs/remotes/origin/${ArtifactBranch}", '--depth=1')
    Invoke-Git $repoRoot @('worktree', 'add', '--detach', $worktree, "origin/$ArtifactBranch")
    # Keep HEAD detached: only the explicit push updates the remote data branch.
}
else {
    Invoke-Git $repoRoot @('worktree', 'add', '--detach', $worktree, 'HEAD')
    Invoke-Git $worktree @('checkout', '--orphan', $ArtifactBranch)
    Invoke-Git $worktree @('rm', '-rf', '.')
}

Invoke-Git $worktree @('config', 'user.name', $BotLogin)
Invoke-Git $worktree @('config', 'user.email', '41898282+github-actions[bot]@users.noreply.github.com')

$targetDir = Join-Path (Join-Path $worktree "pr-$PullRequestNumber") $SourceSha
if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
if (-not $DryRun) { Copy-Item -LiteralPath $GifPath -Destination (Join-Path $worktree $relativePath) -Force }

$message = if ($CommitMessage) { $CommitMessage } else { "Add UI demo '$ScenarioName' for PR #$PullRequestNumber at $shortSha" }
Invoke-Git $worktree @('add', '-A')

if (-not $DryRun) {
    & git -C $worktree diff --cached --quiet
    $diffExit = $LASTEXITCODE
    if ($diffExit -eq 0) { Write-Step 'nothing to publish (identical GIF already present)' }
    else {
        Invoke-Git $worktree @('commit', '-m', $message)
        Invoke-Git $worktree @('push', 'origin', "HEAD:refs/heads/$ArtifactBranch")
    }
    & git -C $repoRoot worktree remove --force $worktree 2>$null | Out-Null
}

# --- 3. Comment and (first time) PR body ------------------------------------------------------
$rawUrl = "https://raw.githubusercontent.com/$Repository/$ArtifactBranch/$relativePath"
$marker = "<!-- ui-demo:$ScenarioName -->"
$statusLine = if ($TestExitCode -eq 0) { 'scenario passed' } else { "scenario **failed** (exit $TestExitCode)" }
$description = if ($ScenarioDescription) { $ScenarioDescription } else { $ScenarioName }

$commentBody = @"
$marker
### UI demo — $description

![$ScenarioName]($rawUrl)

- scenario: ``$ScenarioName``
- build: ``$shortSha`` ($statusLine)
- file: ``$relativePath``
"@

if ($DryRun) {
    Write-Step 'dry run: skipping the comment write (the body rules are still evaluated below)'
    Write-Output $commentBody
}
else {
    # Only touch comments our own bot wrote, and only the one carrying this scenario's marker.
    # Fetch as JSON and filter here: quoting a jq expression through PowerShell is fragile.
    $commentsRaw = & gh api "repos/$Repository/issues/$PullRequestNumber/comments" --paginate 2>$null
    $commentsExit = $LASTEXITCODE
    if ($commentsExit -ne 0) { throw "Could not list PR comments for #$PullRequestNumber." }
    $existingId = (@($commentsRaw) -join "`n" | ConvertFrom-Json) |
        Where-Object { $_.body -like "*$marker*" -and $_.user.login -eq $BotLogin } |
        Select-Object -First 1 -ExpandProperty id

    $bodyPath = Join-Path $tempRoot "ui-demo-comment-$ScenarioName.md"
    [System.IO.File]::WriteAllText($bodyPath, $commentBody, (New-Object System.Text.UTF8Encoding($false)))

    if ($existingId) {
        Write-Step "updating comment $existingId"
        & gh api -X PATCH "repos/$Repository/issues/comments/$existingId" -F "body=@$bodyPath"
    }
    else {
        Write-Step 'creating the demo comment'
        & gh api -X POST "repos/$Repository/issues/$PullRequestNumber/comments" -F "body=@$bodyPath"
    }
    if ($LASTEXITCODE -ne 0) { throw 'Posting the demo comment failed.' }
}

# Comment-only runs must not read or edit the PR body.
if (-not $UpdatePullRequestBody) {
    Write-Step "published: $rawUrl (PR body untouched)"
    return
}

# --- 4. Pull request body: aggregate every scenario of the first delivery ----------------------
# The body carries one block per scenario, so the first delivery of a PR shows all of its demos in
# one place. A scenario that is already represented is left alone: later commits only update the
# comment, which keeps the body stable instead of churning on every push.
$bodyMarker = '<!-- ui-demo-body -->'
$bodyEnd = '<!-- /ui-demo-body -->'
$blockMarker = "<!-- ui-demo:$ScenarioName -->"
$blockEnd = "<!-- /ui-demo:$ScenarioName -->"
$block = @"
$blockMarker
**$description**

![$ScenarioName]($rawUrl)

<sub>scenario ``$ScenarioName`` · build ``$shortSha`` · $statusLine</sub>
$blockEnd
"@

if ($DryRun) {
    $prBody = ([string]$DryRunPrBody).TrimEnd()
}
else {
    $prBodyRaw = & gh pr view $PullRequestNumber --repo $Repository --json body --jq '.body'
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the pull request body.' }
    $prBody = ([string]$prBodyRaw).TrimEnd()
}

if (-not $UpdatePullRequestBody) {
    Write-Step "comment published; PR body untouched (pass -UpdatePullRequestBody to maintain the body region)"
}
elseif ($prBody -notmatch [regex]::Escape($bodyMarker)) {
    Write-Step 'adding the demo region to the PR body'
    $prBody = $prBody + "`n`n" + $bodyMarker + "`n" + $block + "`n" + $bodyEnd
}
elseif ($prBody -notmatch [regex]::Escape($blockMarker)) {
    Write-Step "adding scenario '$ScenarioName' to the existing demo region"
    $prBody = $prBody.Replace($bodyEnd, ($block + "`n" + $bodyEnd))
}
else {
    Write-Step 'PR body already lists this scenario; leaving it unchanged (updates go to the comment)'
    $prBody = $null
}

if ($null -ne $prBody) {
    if ($DryRun) {
        Write-Step 'dry run: resulting PR body'
        Write-Output $prBody
    }
    else {
        $prBodyPath = Join-Path $tempRoot "ui-demo-pr-body-$PullRequestNumber.md"
        [System.IO.File]::WriteAllText($prBodyPath, $prBody, (New-Object System.Text.UTF8Encoding($false)))
        & gh pr edit $PullRequestNumber --repo $Repository --body-file $prBodyPath
        if ($LASTEXITCODE -ne 0) { throw 'Updating the pull request body failed.' }
    }
}
Write-Step "published: $rawUrl"

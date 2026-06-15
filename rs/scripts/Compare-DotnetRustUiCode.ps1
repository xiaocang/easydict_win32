param(
    [string]$RepoRoot,
    [string]$OutputDir,
    [int]$Top = 80,
    [switch]$FailOnDrift
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
} else {
    $RepoRoot = Resolve-Path $RepoRoot
}

$cargoArgs = @(
    "run",
    "--manifest-path", (Join-Path $RepoRoot "rs\Cargo.toml"),
    "-p", "easydict_ui_parity_analyzer",
    "--bin", "easydict_ui_code_parity",
    "--",
    "--repo-root", $RepoRoot,
    "--top", $Top
)

if (-not [string]::IsNullOrWhiteSpace($OutputDir)) {
    $cargoArgs += @("--output-dir", $OutputDir)
}

if ($FailOnDrift) {
    $cargoArgs += "--fail-on-drift"
}

& cargo @cargoArgs
exit $LASTEXITCODE

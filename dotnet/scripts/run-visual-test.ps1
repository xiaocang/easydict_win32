# run-visual-test.ps1 — runs PdfTranslationVisualTest with env vars from ref/.env
#
# Usage:
#   pwsh -File dotnet\scripts\run-visual-test.ps1
#   pwsh -File dotnet\scripts\run-visual-test.ps1 -ClearCache   # delete stale translation cache first
#
# ref/.env format (KEY=VALUE lines; # comments ignored):
#   EASYDICT_TEST_PDF=C:\Users\johnn\Documents\work\easydict_win32\ref\1706.03762v7.pdf
#   DEEPSEEK_API_KEY=sk-xxxxxxxxxxxx

param(
    [switch]$ClearCache
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# Load ref/.env if it exists
$envFile = Join-Path $repoRoot "ref\.env"
if (Test-Path $envFile) {
    Write-Host "Loading env vars from $envFile"
    Get-Content $envFile | Where-Object { $_ -match '^\s*[^#]\S+=\S' } | ForEach-Object {
        $parts = $_ -split '=', 2
        $value = $parts[1].Trim().Trim('"').Trim("'")
        [System.Environment]::SetEnvironmentVariable($parts[0].Trim(), $value)
    }
}

# Validate required vars
if (-not $env:EASYDICT_TEST_PDF) { throw "EASYDICT_TEST_PDF is not set (add to ref/.env or process env)" }
if (-not $env:DEEPSEEK_API_KEY)  { throw "DEEPSEEK_API_KEY is not set (add to ref/.env or process env)"  }

Write-Host "Test PDF : $env:EASYDICT_TEST_PDF"
Write-Host "API key  : $($env:DEEPSEEK_API_KEY.Substring(0, [Math]::Min(8, $env:DEEPSEEK_API_KEY.Length)))..."

if ($ClearCache) {
    $cachePath = Join-Path $env:LOCALAPPDATA "Easydict\translation_cache.db"
    if (Test-Path $cachePath) {
        Write-Host "Clearing stale translation cache: $cachePath"
        Remove-Item $cachePath -Force
    }
}

$testProject = Join-Path $repoRoot "dotnet\tests\Easydict.WinUI.Tests"
dotnet test $testProject `
    --filter "FullyQualifiedName~PdfTranslationVisualTest" `
    -v normal

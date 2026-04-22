param(
    [string]$WorkspaceRoot = $(Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"

$requiredSubmodules = @(
    @{
        Path = "lib/PdfPig"
        Probe = "lib/PdfPig/src/UglyToad.PdfPig/UglyToad.PdfPig.csproj"
    },
    @{
        Path = "dotnet/lib/MDict.CSharp"
        Probe = "dotnet/lib/MDict.CSharp/MDict.Csharp/MDict.Csharp.csproj"
    }
)

function Test-SubmoduleReady {
    param(
        [hashtable]$Submodule
    )

    $probePath = Join-Path $WorkspaceRoot $Submodule.Probe
    return Test-Path -LiteralPath $probePath
}

$missingSubmodules = @($requiredSubmodules | Where-Object { -not (Test-SubmoduleReady $_) })

if ($missingSubmodules.Count -eq 0) {
    Write-Host "[submodules] Build dependencies are already initialized."
    exit 0
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "git was not found in PATH. Install git or initialize the required submodules manually."
}

$gitModulesPath = Join-Path $WorkspaceRoot ".gitmodules"
if (-not (Test-Path -LiteralPath $gitModulesPath)) {
    throw "This workspace does not contain .gitmodules, so the missing build dependencies cannot be restored automatically."
}

$gitDirPath = Join-Path $WorkspaceRoot ".git"
if (-not (Test-Path -LiteralPath $gitDirPath)) {
    throw "This workspace is missing the .git directory metadata. Re-clone the repository with git so submodules can be initialized."
}

$submodulePaths = @($missingSubmodules | ForEach-Object { $_.Path })
Write-Host ("[submodules] Initializing required build submodules: " + ($submodulePaths -join ", "))

$syncArgs = @("-C", $WorkspaceRoot, "submodule", "sync", "--") + $submodulePaths
& git @syncArgs
if ($LASTEXITCODE -ne 0) {
    throw "git submodule sync failed for: $($submodulePaths -join ', ')"
}

$updateArgs = @("-C", $WorkspaceRoot, "submodule", "update", "--init", "--recursive", "--") + $submodulePaths
& git @updateArgs
if ($LASTEXITCODE -ne 0) {
    throw "git submodule update failed for: $($submodulePaths -join ', ')"
}

$missingAfterUpdate = @($requiredSubmodules | Where-Object { -not (Test-SubmoduleReady $_) })
if ($missingAfterUpdate.Count -gt 0) {
    $missingProbePaths = $missingAfterUpdate | ForEach-Object { $_.Probe }
    throw "Required build submodules are still missing after initialization: $($missingProbePaths -join ', ')"
}

Write-Host "[submodules] Build dependencies are ready."

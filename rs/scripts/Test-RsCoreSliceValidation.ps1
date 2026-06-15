#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Fast self-tests for Invoke-RsCoreSliceValidation.ps1 close-out tooling.

.DESCRIPTION
  These tests intentionally use dry-run/recommendation/list modes only. They
  validate the wrapper's profile wiring without running cargo or taking the
  parallel UI isolation mutex, so the tests can be part of the tooling profile.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Resolve-Path -LiteralPath (Join-Path $scriptDir "..\..")
$wrapper = Join-Path $scriptDir "Invoke-RsCoreSliceValidation.ps1"
$powerShellExe = (Get-Process -Id $PID).Path

function Invoke-ValidationWrapper {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Push-Location $repoRoot
    try {
        $global:LASTEXITCODE = 0
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = & $powerShellExe -NoProfile -ExecutionPolicy Bypass -File $wrapper @Arguments 2>&1 | Out-String
            $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        [pscustomobject]@{
            ExitCode = $exitCode
            Output = $output
            Command = "$wrapper $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-ExitCode {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Result,

        [Parameter(Mandatory = $true)]
        [int]$Expected
    )

    if ($Result.ExitCode -ne $Expected) {
        throw "Expected exit code $Expected for '$($Result.Command)', got $($Result.ExitCode). Output:`n$($Result.Output)"
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Needle,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    if ($Text.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "$Context should contain '$Needle'. Output:`n$Text"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Needle,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    if ($Text.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "$Context should not contain '$Needle'. Output:`n$Text"
    }
}

function Invoke-TestCase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Body
    )

    Write-Host "Testing: $Name"
    & $Body
}

Invoke-TestCase "profile list includes tooling lane" {
    $result = Invoke-ValidationWrapper -Arguments @("-ListProfiles")
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "core-validation-tooling" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "validation wrapper self-tests" -Context $result.Command
}

Invoke-TestCase "tooling changes recommend tooling lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\scripts\Invoke-RsCoreSliceValidation.ps1"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "core-validation-tooling" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile core-validation-tooling" -Context $result.Command
}

Invoke-TestCase "input action changes recommend input lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\clipboard.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "input-actions" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile input-actions" -Context $result.Command
}

Invoke-TestCase "tts changes recommend tts lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "lib\easydict-windows-tts\src\lib.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "tts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile tts" -Context $result.Command
}

Invoke-TestCase "traditional HTTP changes recommend provider lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\traditional_http.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "traditional-http" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile traditional-http" -Context $result.Command
}

Invoke-TestCase "native bridge changes recommend native bridge lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\native_bridge.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "native-bridge" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile native-bridge" -Context $result.Command
}

Invoke-TestCase "protocol facade changes recommend protocol lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\protocol_core.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "protocol-facade" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile protocol-facade" -Context $result.Command
}

Invoke-TestCase "settings storage changes recommend settings credential lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\settings_storage.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "settings-credentials" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile settings-credentials" -Context $result.Command
}

Invoke-TestCase "mouse selection reducer changes recommend mouse selection lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\mouse_selection.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "mouse-selection" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile mouse-selection" -Context $result.Command
}

Invoke-TestCase "TATR ONNX changes recommend longdoc layout lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\table_structure_onnx.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "longdoc-layout" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile longdoc-layout" -Context $result.Command
}

Invoke-TestCase "generated credential lock drift remains profile-exempt" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "lib\easydict-windows-credentials\Cargo.lock"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "No non-parallel core paths were found." -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "No validation profile matched." -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "settings-credentials" -Context $result.Command
}

Invoke-TestCase "docs-only changes remain profile-exempt" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "experience.md,migration-list.md,refactor-progress.md"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "No non-parallel core paths were found." -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "No validation profile matched." -Context $result.Command
}

Invoke-TestCase "profile dry-run avoids isolation and cargo execution" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "windows-ai-prepare",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Dry run; validation step(s) that would run:" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "WindowsAI lib prepare contract" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "run recommended profiles dry-run selects top lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RunRecommendedProfiles",
        "-ChangedPath",
        "lib\easydict-windows-ai\src\lib.rs",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Selected recommended validation profile(s): windows-ai-prepare" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "windows-ai-prepare / WindowsAI lib prepare contract" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "run recommended profiles fails clearly when no lane matches" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RunRecommendedProfiles",
        "-ChangedPath",
        "experience.md",
        "-DryRun"
    )
    if ($result.ExitCode -eq 0) {
        throw "Expected no-profile dry-run to fail. Output:`n$($result.Output)"
    }
    Assert-Contains -Text $result.Output -Needle "No validation profile matched" -Context $result.Command
}

Write-Host "Rs core slice validation tooling tests passed."

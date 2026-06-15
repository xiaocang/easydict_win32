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
    Assert-Contains -Text $result.Output -Needle "mdx-native" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "openai-compatible" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "windows-ai-native" -Context $result.Command
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

Invoke-TestCase "desktop shell changes recommend desktop settings lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "lib\easydict-windows-shell\src\lib.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "desktop-settings" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile desktop-settings" -Context $result.Command
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

Invoke-TestCase "file dialog changes recommend file dialog lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "lib\easydict-windows-dialogs\src\lib.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "file-dialog" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile file-dialog" -Context $result.Command
}

Invoke-TestCase "text selection changes recommend selected-text lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\text_selection.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "text-selection" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile text-selection" -Context $result.Command
}

Invoke-TestCase "screen capture changes recommend OCR diagnostics lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "lib\easydict-windows-screen-capture\src\lib.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "ocr-diagnostics" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile ocr-diagnostics" -Context $result.Command
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

Invoke-TestCase "custom streaming changes recommend custom streaming lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\custom_streaming.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "custom-streaming" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile custom-streaming" -Context $result.Command
}

Invoke-TestCase "Built-in AI registration changes recommend registration lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\openai_compatible.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "builtin-ai-registration" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile builtin-ai-registration" -Context $result.Command
}

Invoke-TestCase "OpenAI-compatible changes recommend OpenAI lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\openai_compatible.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "openai-compatible" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile openai-compatible" -Context $result.Command
}

Invoke-TestCase "Foundry Local changes recommend Foundry lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "lib\easydict-foundry-local\src\lib.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "foundry-local" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile foundry-local" -Context $result.Command
}

Invoke-TestCase "OpenVINO download changes recommend OpenVINO lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\openvino_download.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "openvino-download" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile openvino-download" -Context $result.Command
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

Invoke-TestCase "named-event IPC changes recommend native bridge lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "lib\easydict-windows-ipc\src\lib.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "native-bridge" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile native-bridge" -Context $result.Command
}

Invoke-TestCase "browser registrar changes recommend browser support lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\browser_registrar.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "browser-support" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile browser-support" -Context $result.Command
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

Invoke-TestCase "Vision layout changes recommend longdoc layout lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\vision_layout.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "longdoc-layout" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile longdoc-layout" -Context $result.Command
}

Invoke-TestCase "DocLayout YOLO changes recommend longdoc layout lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\doc_layout_yolo.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "longdoc-layout" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile longdoc-layout" -Context $result.Command
}

Invoke-TestCase "native PDF export changes recommend longdoc export lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\pdf_native_export.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "longdoc-export" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile longdoc-export" -Context $result.Command
}

Invoke-TestCase "PDF source extraction changes recommend longdoc export lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\pdf_source_extraction.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "longdoc-export" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile longdoc-export" -Context $result.Command
}

Invoke-TestCase "content preservation changes recommend longdoc formula lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\content_preservation.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "longdoc-formula" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile longdoc-formula" -Context $result.Command
}

Invoke-TestCase "text layout changes recommend longdoc formula lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\text_layout.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "longdoc-formula" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile longdoc-formula" -Context $result.Command
}

Invoke-TestCase "MDX native lookup changes recommend MDX native lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\mdx_native.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "mdx-native" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile mdx-native" -Context $result.Command
}

Invoke-TestCase "local dictionary index changes recommend suggestion lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\local_dictionary_index.rs"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "local-dictionary-suggestions" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile local-dictionary-suggestions" -Context $result.Command
}

Invoke-TestCase "rs portable release workflow changes recommend release lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        ".github\workflows\release-publish.yml"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "rs-portable-release" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile rs-portable-release" -Context $result.Command
}

Invoke-TestCase "Package-Portable shim changes recommend rs portable release lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\scripts\Package-Portable.ps1"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "rs-portable-release" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "-Profile rs-portable-release" -Context $result.Command
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

Invoke-TestCase "generated dialog lock drift remains profile-exempt" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "lib\easydict-windows-dialogs\Cargo.lock"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "No non-parallel core paths were found." -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "No validation profile matched." -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "file-dialog" -Context $result.Command
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

Invoke-TestCase "desktop settings profile dry-run includes shell and registry boundary coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "desktop-settings",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Windows shell helper contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "desktop integration registry contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "desktop shell route ownership" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "default bundled helper process boundary" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "default shell URL boundary" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "default app shell task boundary" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "default desktop registry boundary scan" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "settings credentials profile dry-run includes storage, migration, and no-runtime coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "settings-credentials",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Windows credential wrapper contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "credential protection contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "settings storage contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "settings migration contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "settings save app diagnostics" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "settings path no retained runtime markers" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "file dialog profile dry-run includes helper and route coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "file-dialog",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Windows native dialog helper contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "app file dialog facade contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "app file dialog route ownership" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "LongDoc browse dialog routing" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "LongDoc browse dialog diagnostics" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "OCR diagnostics profile dry-run includes screen capture helper coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "ocr-diagnostics",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Windows screen capture helper contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "HTTP backend parse diagnostics" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "app screen capture facade contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "window snapshot diagnostics" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "native capture helper task surface" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "native bridge profile dry-run includes IPC and app receiver coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "native-bridge",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Windows IPC named-event helper contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "native bridge frame parser and binary contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "app named-event receiver ownership" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "protocol facade profile dry-run includes default and retained feature gates" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "protocol-facade",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "default protocol facade DTO contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "retained worker protocol feature contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "crate-root retained protocol exports stay feature-gated" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "default manifests do not enable retained protocol workers" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "input actions profile dry-run includes helper coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "input-actions",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Windows text-selection clipboard/insertion helper contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "clipboard facade and monitor contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "text insertion facade contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "silent OCR clipboard task surface" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "TTS profile dry-run includes helper, app, and legacy boundary coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "tts",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Windows SAPI TTS helper contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "app TTS facade contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "quick translate speak actions" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "auto-play translation speech routing" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "legacy PowerShell TTS features stay disabled" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "text selection profile dry-run includes helper and capture coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "text-selection",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "format text-selection slice" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Windows text-selection selected-text helper contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "backend diagnostic preservation" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "selected-text capture task" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "mouse selection capture result mapping" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "custom streaming profile dry-run includes app and CLI live-stream coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "custom-streaming",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "format custom streaming slice" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "app custom streaming contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "CLI Doubao local SSE contract" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "CLI Gemini local SSE contract" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "traditional HTTP profile dry-run includes native provider and CLI coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "traditional-http",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "format traditional HTTP slice" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "traditional HTTP planner/parser/preflight contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Quick Translate traditional HTTP providers" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Quick Translate Bing two-phase provider" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "CLI traditional providers avoid worker/CompatHost wording" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "Built-in AI registration profile dry-run includes app and registration coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "builtin-ai-registration",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "format Built-in AI registration slice" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "app Built-in AI registration state/lifecycle" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "OpenAI-compatible Built-in AI registration contract" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "OpenAI-compatible profile dry-run includes route coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "openai-compatible",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "OpenAI-compatible planner and executor contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Quick Translate OpenAI-compatible routes" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "CLI OpenAI translate/grammar/batch contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "CLI DeepSeek native contract" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "Foundry Local profile dry-run includes route and no-worker coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "foundry-local",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "format Foundry Local slice" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "app Foundry Local prepare state/lifecycle" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Quick Translate Auto Foundry route diagnostics" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Quick Translate packaged Auto LocalAI stale app-dir boundary" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "CLI Auto Foundry route diagnostics" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "LongDoc Auto Foundry route diagnostics" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "OpenAI-compatible Foundry Local prepare contract" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "OpenVINO download profile dry-run includes diagnostics and asset contracts" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "openvino-download",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "format OpenVINO download slice" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "OpenVINO download contracts and diagnostics" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "browser support profile dry-run includes registrar and extension coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "browser-support",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "browser registrar behavior contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "browser registrar binary contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "browser extension default release contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "browser extension package scanning contracts" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "MDX native profile dry-run includes lookup and resource coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "mdx-native",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "rs-mdict default contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "app native MDX/MDD lookup contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "quick translate MDX service contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "settings MDD companion discovery contracts" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "local dictionary suggestions profile dry-run includes index coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "local-dictionary-suggestions",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "LexIndex LXDX contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "LexIndex CLI contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "persistent local dictionary index contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Quick Translate local dictionary suggestion contracts" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "LongDoc layout profile dry-run includes DocLayout, Vision, and TATR coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "longdoc-layout",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "format LongDoc layout slice" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "layout model download contract" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "DocLayout-YOLO preprocessing contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "DocLayout-YOLO ONNX helper contract" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "vision layout request/parser/executor contract" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "TATR table structure contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "TATR ONNX helper contract" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "explicit VisionLLM config errors" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "vision backend page diagnostics" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "explicit TATR setup diagnostics" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "LongDoc export profile dry-run includes export and PDF coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "longdoc-export",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "format LongDoc export slice" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "LongDoc text and markdown export composers" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "PDF content-stream patch contract" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "native PDF export contract" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "PDF export block overlay metadata" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "PDF source extraction export metadata" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "LongDoc formula profile dry-run includes preservation and layout coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "longdoc-formula",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "format LongDoc formula/layout slice" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "text layout wrapping and fit contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "font metrics contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "document layout geometry contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "LaTeX render-text simplifier" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "formula protection contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "content preservation service contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "formula-aware text reconstruction contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "character paragraph evidence contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "PDF formula adapter contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "native LongDoc formula integration" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "WindowsAI native profile dry-run includes route coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "windows-ai-native",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "WindowsAI lib native contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Quick Translate native WindowsAI client routes" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "CLI native WindowsAI route contracts" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "LongDoc native WindowsAI routes" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "rust-only boundary profile dry-run includes default no-runtime coverage" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "rust-only-boundary",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "runtime policy defaults stay rust-only" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "default app source has no retained runtime entries" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "default app process spawn allowlist stays narrow" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "default CLI translate stays native" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "CLI LocalAI no-worker boundary" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "LongDoc CLI stale payload boundaries" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "LongDoc current app-dir ignores hybrid env" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "rs portable release profile dry-run includes default packaging gates" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "rs-portable-release",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "release defaults to rs portable" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "default packager surface is rust-only" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "zip validation excludes retained runtime" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "run recommended profiles dry-run selects MDX native lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RunRecommendedProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\mdx_native.rs",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Selected recommended validation profile(s): mdx-native" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "mdx-native / app native MDX/MDD lookup contracts" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "run recommended profiles dry-run selects OpenAI lane" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RunRecommendedProfiles",
        "-ChangedPath",
        "rs\crates\easydict_app\src\openai_compatible.rs",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Selected recommended validation profile(s): openai-compatible" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "openai-compatible / OpenAI-compatible planner and executor contracts" -Context $result.Command
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
    Assert-Contains -Text $result.Output -Needle "Selected recommended validation profile(s): windows-ai-native" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "windows-ai-native / WindowsAI lib native contracts" -Context $result.Command
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

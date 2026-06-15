#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Fast self-tests for Invoke-RsCoreSliceValidation.ps1 close-out tooling.

.DESCRIPTION
  These tests import the validation wrapper's definition section and exercise
  the same profile/recommendation functions in-process. A small black-box
  smoke set still invokes the wrapper to verify argument validation and dry-run
  CLI plumbing without paying that process-startup cost for every lane.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Resolve-Path -LiteralPath (Join-Path $scriptDir "..\..")
$wrapper = Join-Path $scriptDir "Invoke-RsCoreSliceValidation.ps1"
$powerShellExe = (Get-Process -Id $PID).Path
$wrapperText = Get-Content -LiteralPath $wrapper -Raw

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

function Assert-OccurrenceCount {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Needle,

        [Parameter(Mandatory = $true)]
        [int]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    $count = 0
    $offset = 0
    while ($offset -lt $Text.Length) {
        $index = $Text.IndexOf($Needle, $offset, [System.StringComparison]::OrdinalIgnoreCase)
        if ($index -lt 0) {
            break
        }

        $count += 1
        $offset = $index + $Needle.Length
    }

    if ($count -ne $Expected) {
        throw "$Context should contain '$Needle' $Expected time(s), got $count. Output:`n$Text"
    }
}

function Assert-SameStringSet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Expected,

        [Parameter(Mandatory = $true)]
        [string[]]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    $missing = @($Expected | Where-Object { $Actual -notcontains $_ })
    $extra = @($Actual | Where-Object { $Expected -notcontains $_ })
    if ($missing.Count -ne 0 -or $extra.Count -ne 0) {
        throw "$Context set mismatch. Missing: $($missing -join ', '). Extra: $($extra -join ', ')."
    }
}

function Assert-SameStringSequence {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Expected,

        [Parameter(Mandatory = $true)]
        [string[]]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    if ($Expected.Count -ne $Actual.Count) {
        throw "$Context length mismatch. Expected: $($Expected -join ', '). Actual: $($Actual -join ', ')."
    }

    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($Expected[$index] -ne $Actual[$index]) {
            throw "$Context mismatch at index $index. Expected: $($Expected -join ', '). Actual: $($Actual -join ', ')."
        }
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

function Get-ValidationWrapperDefinitionText {
    $startMarker = '$ErrorActionPreference = "Stop"'
    $endMarker = '$profileKeys = @(Expand-ProfileList $Profile)'
    $start = $wrapperText.IndexOf($startMarker, [System.StringComparison]::Ordinal)
    if ($start -lt 0) {
        throw "Could not find wrapper definition start marker '$startMarker'."
    }

    $end = $wrapperText.IndexOf($endMarker, $start, [System.StringComparison]::Ordinal)
    if ($end -lt 0) {
        throw "Could not find wrapper definition end marker '$endMarker'."
    }

    @"
`$PSCommandPath = '$($wrapper -replace "'", "''")'
$($wrapperText.Substring($start, $end - $start))
"@
}

function Get-TestRecommendation {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ChangedPath,

        [string]$DiffText
    )

    $recommendationPaths = @(Expand-PathList $ChangedPath | ForEach-Object { Normalize-RepoRelativePath $_ })
    if ([string]::IsNullOrWhiteSpace($DiffText)) {
        $DiffText = $recommendationPaths -join "`n"
    }
    Get-ProfileRecommendations -Paths $recommendationPaths -DiffText $DiffText
}

function Get-RecommendationProfileNames {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ChangedPath,

        [string]$DiffText
    )

    @((Get-TestRecommendation -ChangedPath $ChangedPath -DiffText $DiffText).Results | ForEach-Object { $_.Profile })
}

function Assert-RecommendationIncludes {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ChangedPath,

        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedProfiles,

        [string]$DiffText,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    $actual = @(Get-RecommendationProfileNames -ChangedPath $ChangedPath -DiffText $DiffText)
    foreach ($profile in $ExpectedProfiles) {
        if ($actual -notcontains $profile) {
            throw "$Context should recommend '$profile'. Actual: $($actual -join ', ')."
        }
    }
}

function Assert-RecommendationExact {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ChangedPath,

        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedProfiles,

        [string]$DiffText,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    $actual = @(Get-RecommendationProfileNames -ChangedPath $ChangedPath -DiffText $DiffText)
    Assert-SameStringSet -Expected $ExpectedProfiles -Actual $actual -Context $Context
}

function Get-ValidationStepsForProfiles {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Profiles
    )

    $profileKeys = @(Expand-ProfileList $Profiles)
    foreach ($profileKey in $profileKeys) {
        if (-not $validationProfiles.Contains($profileKey)) {
            throw "Unknown validation profile '$profileKey'."
        }
    }

    $validationSteps = @()
    if ($profileKeys.Count -eq 1) {
        $validationSteps = @($validationProfiles[$profileKeys[0]].Steps)
    }
    else {
        foreach ($profileKey in $profileKeys) {
            foreach ($step in @($validationProfiles[$profileKey].Steps)) {
                $validationSteps += (New-ValidationStep "$profileKey / $($step.Name)" $step.Command)
            }
        }
    }

    @(Select-UniqueValidationSteps -Steps $validationSteps)
}

function ConvertTo-DryRunText {
    param(
        [pscustomobject[]]$Steps,

        [string[]]$TrailingWhitespacePaths,

        [string]$GstepCommitMessage
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("Dry run; validation step(s) that would run:")
    foreach ($step in @($Steps)) {
        $lines.Add("  - $($step.Name): $($step.Command -join ' ')")
    }

    if ($null -ne $TrailingWhitespacePaths) {
        if ($TrailingWhitespacePaths.Count -eq 0) {
            $lines.Add("  - trailing whitespace check: no changed text files would be scanned")
        }
        else {
            $lines.Add("  - trailing whitespace check: rg -n ""[ \t]+$"" -- $($TrailingWhitespacePaths -join ' ')")
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($GstepCommitMessage)) {
        $lines.Add("  - gstep checkpoint after successful validation: $(Format-GstepCommitCommandForDisplay -Message $GstepCommitMessage)")
    }

    $lines -join "`n"
}

function Get-ProfileDryRunText {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Profiles
    )

    ConvertTo-DryRunText -Steps (Get-ValidationStepsForProfiles -Profiles $Profiles)
}

function Get-RecommendedDryRunText {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ChangedPath,

        [switch]$AllRecommendedProfiles,

        [int]$MaxRecommendedProfiles = 0
    )

    $recommendation = Get-TestRecommendation -ChangedPath $ChangedPath
    $selectedProfiles = @(Get-SelectedRecommendationResults `
            -Recommendation $recommendation `
            -AllRecommendedProfiles:$AllRecommendedProfiles `
            -MaxRecommendedProfiles $MaxRecommendedProfiles)
    $profileNames = @($selectedProfiles | ForEach-Object { $_.Profile })
    $steps = @()
    foreach ($profileName in $profileNames) {
        foreach ($step in @($validationProfiles[$profileName].Steps)) {
            $steps += (New-ValidationStep "$profileName / $($step.Name)" $step.Command)
        }
    }

    "Selected recommended validation profile(s): $($profileNames -join ', ')`n" +
    (ConvertTo-DryRunText -Steps (Select-UniqueValidationSteps -Steps $steps))
}

. ([scriptblock]::Create((Get-ValidationWrapperDefinitionText)))

Invoke-TestCase "profile list includes tooling lane" {
    foreach ($profileName in @("core-validation-tooling", "mdx-native", "openai-compatible", "translation-cache", "windows-ai-native")) {
        if (-not $validationProfiles.Contains($profileName)) {
            throw "Expected validation profile '$profileName' to exist."
        }
    }
    Assert-Contains `
        -Text $validationProfiles["core-validation-tooling"].Steps[0].Name `
        -Needle "validation wrapper self-tests" `
        -Context "core-validation-tooling profile"
}

Invoke-TestCase "profile definitions, recommendations, and dry-run wiring stay aligned" {
    Assert-SameStringSet `
        -Expected @($validationProfiles.Keys) `
        -Actual @($profileRecommendations.Keys) `
        -Context "Validation profile and recommendation rule"

    $dryRun = Get-ProfileDryRunText -Profiles @($validationProfiles.Keys)
    Assert-Contains -Text $dryRun -Needle "core-validation-tooling / validation wrapper self-tests" -Context "all-profile dry-run"
    Assert-Contains -Text $dryRun -Needle "rust-only-boundary / default app process spawn allowlist stays narrow" -Context "all-profile dry-run"
    Assert-NotContains -Text $dryRun -Needle "Waiting for core validation isolation lock." -Context "all-profile dry-run"
}

Invoke-TestCase "cleanup restore path retries transient file-copy locks" {
    Assert-Contains -Text $wrapperText -Needle "function Copy-ItemWithRetry" -Context $wrapper
    Assert-Contains -Text $wrapperText -Needle 'Retrying $OperationName after transient file copy failure' -Context $wrapper
    Assert-Contains -Text $wrapperText -Needle "Failed to restore" -Context $wrapper
    Assert-Contains -Text $wrapperText -Needle "cleanupErrors" -Context $wrapper
}

Invoke-TestCase "tooling changes only recommend tooling lane" {
    Assert-RecommendationExact `
        -ChangedPath @("rs\scripts\Invoke-RsCoreSliceValidation.ps1") `
        -ExpectedProfiles @("core-validation-tooling") `
        -Context "tooling path recommendation"
}

$recommendationCases = @(
    @{ Name = "desktop shell changes recommend desktop settings lane"; Path = "lib\easydict-windows-shell\src\lib.rs"; Profiles = @("desktop-settings") },
    @{ Name = "input action changes recommend input lane"; Path = "rs\crates\easydict_app\src\clipboard.rs"; Profiles = @("input-actions") },
    @{ Name = "shared text-selection helper changes recommend all dependent input lanes"; Path = "lib\easydict-windows-text-selection\src\lib.rs"; Profiles = @("input-actions", "mouse-selection", "text-selection") },
    @{ Name = "tts changes recommend tts lane"; Path = "lib\easydict-windows-tts\src\lib.rs"; Profiles = @("tts") },
    @{ Name = "file dialog changes recommend file dialog lane"; Path = "lib\easydict-windows-dialogs\src\lib.rs"; Profiles = @("file-dialog") },
    @{ Name = "text selection changes recommend selected-text lane"; Path = "rs\crates\easydict_app\src\text_selection.rs"; Profiles = @("text-selection") },
    @{ Name = "screen capture changes recommend OCR diagnostics lane"; Path = "lib\easydict-windows-screen-capture\src\lib.rs"; Profiles = @("ocr-diagnostics") },
    @{ Name = "Windows OCR helper changes recommend OCR diagnostics lane"; Path = "rs\crates\easydict_windows_ocr\src\lib.rs"; Profiles = @("ocr-diagnostics") },
    @{ Name = "traditional HTTP changes recommend provider lane"; Path = "rs\crates\easydict_app\src\traditional_http.rs"; Profiles = @("traditional-http") },
    @{ Name = "translation cache changes recommend cache lane"; Path = "rs\crates\easydict_app\src\translation_cache.rs"; Profiles = @("translation-cache") },
    @{ Name = "custom streaming changes recommend custom streaming lane"; Path = "rs\crates\easydict_app\src\custom_streaming.rs"; Profiles = @("custom-streaming") },
    @{ Name = "Built-in AI registration changes recommend registration lane"; Path = "rs\crates\easydict_app\src\openai_compatible.rs"; Profiles = @("builtin-ai-registration") },
    @{ Name = "OpenAI-compatible changes recommend OpenAI lane"; Path = "rs\crates\easydict_app\src\openai_compatible.rs"; Profiles = @("openai-compatible") },
    @{ Name = "Foundry Local changes recommend Foundry lane"; Path = "lib\easydict-foundry-local\src\lib.rs"; Profiles = @("foundry-local") },
    @{ Name = "OpenVINO download changes recommend OpenVINO lane"; Path = "rs\crates\easydict_app\src\openvino_download.rs"; Profiles = @("openvino-download") },
    @{ Name = "native bridge changes recommend native bridge lane"; Path = "rs\crates\easydict_app\src\native_bridge.rs"; Profiles = @("native-bridge") },
    @{ Name = "named-event IPC changes recommend native bridge lane"; Path = "lib\easydict-windows-ipc\src\lib.rs"; Profiles = @("native-bridge") },
    @{ Name = "browser registrar changes recommend browser support lane"; Path = "rs\crates\easydict_app\src\browser_registrar.rs"; Profiles = @("browser-support") },
    @{ Name = "protocol facade changes recommend protocol lane"; Path = "rs\crates\easydict_app\src\protocol_core.rs"; Profiles = @("protocol-facade") },
    @{ Name = "settings storage changes recommend settings credential lane"; Path = "rs\crates\easydict_app\src\settings_storage.rs"; Profiles = @("settings-credentials") },
    @{ Name = "mouse selection reducer changes recommend mouse selection lane"; Path = "rs\crates\easydict_app\src\mouse_selection.rs"; Profiles = @("mouse-selection") },
    @{ Name = "TATR ONNX changes recommend longdoc layout lane"; Path = "rs\crates\easydict_app\src\table_structure_onnx.rs"; Profiles = @("longdoc-layout") },
    @{ Name = "Vision layout changes recommend longdoc layout lane"; Path = "rs\crates\easydict_app\src\vision_layout.rs"; Profiles = @("longdoc-layout") },
    @{ Name = "DocLayout YOLO changes recommend longdoc layout lane"; Path = "rs\crates\easydict_app\src\doc_layout_yolo.rs"; Profiles = @("longdoc-layout") },
    @{ Name = "native PDF export changes recommend longdoc export lane"; Path = "rs\crates\easydict_app\src\pdf_native_export.rs"; Profiles = @("longdoc-export") },
    @{ Name = "PDF source extraction changes recommend longdoc export lane"; Path = "rs\crates\easydict_app\src\pdf_source_extraction.rs"; Profiles = @("longdoc-export") },
    @{ Name = "content preservation changes recommend longdoc formula lane"; Path = "rs\crates\easydict_app\src\content_preservation.rs"; Profiles = @("longdoc-formula") },
    @{ Name = "text layout changes recommend longdoc formula lane"; Path = "rs\crates\easydict_app\src\text_layout.rs"; Profiles = @("longdoc-formula") },
    @{ Name = "MDX native lookup changes recommend MDX native lane"; Path = "rs\crates\easydict_app\src\mdx_native.rs"; Profiles = @("mdx-native") },
    @{ Name = "local dictionary index changes recommend suggestion lane"; Path = "rs\crates\easydict_app\src\local_dictionary_index.rs"; Profiles = @("local-dictionary-suggestions") },
    @{ Name = "rs portable release workflow changes recommend release lane"; Path = ".github\workflows\release-publish.yml"; Profiles = @("rs-portable-release") },
    @{ Name = "Package-Portable shim changes recommend rs portable release lane"; Path = "rs\scripts\Package-Portable.ps1"; Profiles = @("rs-portable-release") }
)

foreach ($case in $recommendationCases) {
    $currentCase = $case
    Invoke-TestCase $currentCase.Name {
        Assert-RecommendationIncludes `
            -ChangedPath @($currentCase.Path) `
            -ExpectedProfiles @($currentCase.Profiles) `
            -Context $currentCase.Name
    }
}

Invoke-TestCase "LongDoc cache quality-report diffs default to export close-out lane" {
    $diffText = @'
+    let mut cache_warnings = Vec::new();
+    let _cache = LongDocumentTranslationCache::open(cache_path);
+    push_native_long_document_cache_warning(&mut cache_warnings, "Long document translation cache could not be opened".to_string());
+    let quality_report = native_long_document_quality_report_json(&checkpoint, metrics.as_ref(), &cache_warnings);
+    write_native_result_json_sidecar(result_json_path.as_deref(), &result);
+    assert!(sidecar_quality_report.contains("qualityReport"));
'@
    $recommendation = Get-TestRecommendation `
        -ChangedPath @("rs\crates\easydict_app\src\long_document.rs") `
        -DiffText $diffText
    $selected = @(Get-SelectedRecommendationResults -Recommendation $recommendation | ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("longdoc-export") `
        -Actual $selected `
        -Context "LongDoc cache quality-report default recommendation"
}

Invoke-TestCase "translation cache clear diffs default to cache close-out lane" {
    $diffText = @'
+        let persistent_translation_cache_clear_error = (message == Message::ClearTranslationCache)
+            .then(|| self.clear_persistent_translation_cache().err())
+            .flatten();
+            self.state.settings.translation_cache_status = format!("Clear failed: {error}");
+pub fn clear_persistent_translation_cache_for_settings(settings: &protocol::SettingsSnapshot) -> Result<(), PersistentTranslationCacheError> {
+    let mut cache = LongDocumentTranslationCache::open(long_document_translation_cache_path(settings.cache_dir_str()))?;
+    cache.clear()
+}
'@
    $recommendation = Get-TestRecommendation `
        -ChangedPath @("rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs") `
        -DiffText $diffText
    $selected = @(Get-SelectedRecommendationResults -Recommendation $recommendation | ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("translation-cache") `
        -Actual $selected `
        -Context "translation cache clear default recommendation"
}

Invoke-TestCase "OCR capture background diffs default to OCR diagnostics lane" {
    $diffText = @'
+pub fn capture_screen_background_result() -> Result<CaptureBackground, String> {
+    crate::screen_capture_native::capture_screen_region_result(
+        easydict_windows_screen_capture::ScreenCaptureRequest::virtual_desktop(),
+    )
+}
+const CAPTURE_BACKGROUND_ERROR_PREFIX: &str = "Screen capture background failed: ";
+state.last_ocr_error = Some(format!("{CAPTURE_BACKGROUND_ERROR_PREFIX}{error}"));
'@
    $recommendation = Get-TestRecommendation `
        -ChangedPath @("rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\src\lib.rs") `
        -DiffText $diffText
    $selected = @(Get-SelectedRecommendationResults -Recommendation $recommendation | ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("ocr-diagnostics") `
        -Actual $selected `
        -Context "OCR capture background default recommendation"
}

Invoke-TestCase "shared text-selection recommendation output preserves combined close-out commands" {
    $recommendation = Get-TestRecommendation -ChangedPath @("lib\easydict-windows-text-selection\src\lib.rs")
    $actual = @($recommendation.Results | ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection", "text-selection") `
        -Actual $actual `
        -Context "shared text-selection recommendation order"

    $output = & {
        Show-ProfileRecommendations `
            -Recommendation $recommendation `
            -ChangedPath @("lib\easydict-windows-text-selection\src\lib.rs")
    } 6>&1 | Out-String -Width 4096
    $profileCsv = (@($recommendation.Results | ForEach-Object { $_.Profile }) -join ",")
    if ($profileCsv -ne "input-actions,mouse-selection,text-selection") {
        throw "Unexpected shared text-selection profile CSV: $profileCsv"
    }
    $selectorArgs = Get-RecommendationSelectorArguments -ChangedPath @("lib\easydict-windows-text-selection\src\lib.rs")
    if ($selectorArgs -ne " -ChangedPath lib/easydict-windows-text-selection/src/lib.rs") {
        throw "Unexpected shared text-selection selector args: $selectorArgs"
    }
    Assert-Contains -Text $output -Needle "Combined close-out command for listed profile(s):" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Default recommended close-out:" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -CheckTrailingWhitespace" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Default recommended close-out dry-run:" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -CheckTrailingWhitespace -DryRun" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Run all recommendations with trailing whitespace close-out:" -Context "shared text-selection recommendation output"
}

Invoke-TestCase "recommendation report keeps selector, selected profiles, commands, and steps aligned" {
    $changedPath = @("lib\easydict-windows-text-selection\src\lib.rs")
    $recommendation = Get-TestRecommendation -ChangedPath $changedPath
    $report = New-RecommendationReport -Recommendation $recommendation -ChangedPath $changedPath

    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection", "text-selection") `
        -Actual @($report.DefaultSelectedProfiles) `
        -Context "recommendation report default selected profiles"
    Assert-SameStringSequence `
        -Expected @("lib/easydict-windows-text-selection/src/lib.rs") `
        -Actual @($report.Selector.ChangedPath) `
        -Context "recommendation report changed paths"
    Assert-Contains `
        -Text $report.Commands.DefaultRecommendedCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -CheckTrailingWhitespace" `
        -Context "recommendation report default close-out command"
    Assert-Contains `
        -Text $report.Commands.AllRecommendedCloseOut `
        -Needle "-AllRecommendedProfiles -CheckTrailingWhitespace" `
        -Context "recommendation report all-recommended close-out command"

    $inputActions = @($report.Results | Where-Object { $_.Profile -eq "input-actions" })[0]
    Assert-Contains `
        -Text (@($inputActions.Steps | ForEach-Object { $_.Name }) -join "`n") `
        -Needle "Windows text-selection clipboard/insertion helper contracts" `
        -Context "recommendation report profile steps"
}

Invoke-TestCase "generated dependency lock drift remains profile-exempt" {
    foreach ($path in @(
            "lib\easydict-windows-credentials\Cargo.lock",
            "lib\easydict-windows-dialogs\Cargo.lock"
        )) {
        $recommendation = Get-TestRecommendation -ChangedPath @($path)
        if ($recommendation.CorePaths.Count -ne 0 -or $recommendation.Results.Count -ne 0) {
            throw "$path should be profile-exempt. Core paths: $($recommendation.CorePaths -join ', '). Results: $(@($recommendation.Results | ForEach-Object { $_.Profile }) -join ', ')."
        }
    }
}

Invoke-TestCase "docs-only changes remain profile-exempt" {
    $recommendation = Get-TestRecommendation -ChangedPath @("migration-list.md", "refactor-progress.md", "experience.md")
    if ($recommendation.CorePaths.Count -ne 0 -or $recommendation.Results.Count -ne 0) {
        throw "Docs-only changes should be profile-exempt. Core paths: $($recommendation.CorePaths -join ', '). Results: $(@($recommendation.Results | ForEach-Object { $_.Profile }) -join ', ')."
    }
}

Invoke-TestCase "run recommended profiles selects every tied top lane by default" {
    $selected = @(Get-SelectedRecommendationResults -Recommendation (Get-TestRecommendation -ChangedPath @("lib\easydict-windows-text-selection\src\lib.rs")) |
        ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection", "text-selection") `
        -Actual $selected `
        -Context "tied recommendation default selection"
}

Invoke-TestCase "run recommended profiles supports all recommendations and explicit caps" {
    $all = Get-RecommendedDryRunText `
        -ChangedPath @("lib\easydict-windows-text-selection\src\lib.rs") `
        -AllRecommendedProfiles
    Assert-Contains -Text $all -Needle "Selected recommended validation profile(s): input-actions, mouse-selection, text-selection" -Context "all recommended dry-run"
    Assert-Contains -Text $all -Needle "mouse-selection / mouse-selection reducer and producer contracts" -Context "all recommended dry-run"

    $capped = @(Get-SelectedRecommendationResults `
            -Recommendation (Get-TestRecommendation -ChangedPath @("lib\easydict-windows-text-selection\src\lib.rs")) `
            -MaxRecommendedProfiles 2 |
        ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection") `
        -Actual $capped `
        -Context "capped recommendation selection"
}

Invoke-TestCase "shared app fallback paths recommend rust-only boundary when no specific lane matches" {
    $recommendation = Get-TestRecommendation -ChangedPath @("rs\crates\easydict_app\src\state.rs")
    Assert-RecommendationIncludes `
        -ChangedPath @("rs\crates\easydict_app\src\state.rs") `
        -ExpectedProfiles @("rust-only-boundary") `
        -Context "state.rs fallback recommendation"
    if (@($recommendation.Results | Where-Object { $_.Profile -eq "rust-only-boundary" -and $_.FallbackPathMatches.Count -gt 0 }).Count -eq 0) {
        throw "state.rs should reach rust-only-boundary through fallback path matching."
    }
}

Invoke-TestCase "profile dry-run avoids isolation and keeps duplicate commands collapsed" {
    $dryRun = Get-ProfileDryRunText -Profiles @("input-actions", "text-selection")
    Assert-Contains -Text $dryRun -Needle "input-actions / clipboard facade and monitor contracts" -Context "multi-profile dry-run"
    Assert-Contains -Text $dryRun -Needle "input-actions / quick translate text insertion actions" -Context "multi-profile dry-run"
    Assert-Contains -Text $dryRun -Needle "text-selection / backend diagnostic preservation" -Context "multi-profile dry-run"
    Assert-Contains -Text $dryRun -Needle "text-selection / selected-text capture task" -Context "multi-profile dry-run"
    Assert-OccurrenceCount `
        -Text $dryRun `
        -Needle "cargo test --manifest-path lib\easydict-windows-text-selection\Cargo.toml" `
        -Expected 1 `
        -Context "multi-profile dry-run"
    Assert-NotContains -Text $dryRun -Needle "Waiting for core validation isolation lock." -Context "multi-profile dry-run"
}

$profileDryRunCases = @(
    @{
        Name = "desktop settings profile dry-run includes shell and registry boundary coverage"
        Profiles = @("desktop-settings")
        Needles = @("Windows shell helper contracts", "desktop integration registry contracts", "desktop shell route ownership", "default bundled helper process boundary", "default shell URL boundary", "default app shell task boundary", "default desktop registry boundary scan")
    },
    @{
        Name = "settings credentials profile dry-run includes storage, migration, and no-runtime coverage"
        Profiles = @("settings-credentials")
        Needles = @("Windows credential wrapper contracts", "credential protection contracts", "settings storage contracts", "settings migration contracts", "settings save app diagnostics", "settings path no retained runtime markers")
    },
    @{
        Name = "TTS profile dry-run includes helper, app, and legacy boundary coverage"
        Profiles = @("tts")
        Needles = @("Windows SAPI TTS helper contracts", "app TTS facade contracts", "quick translate speak actions", "auto-play translation speech routing", "legacy PowerShell TTS features stay disabled")
    },
    @{
        Name = "text selection profile dry-run includes helper and capture coverage"
        Profiles = @("text-selection")
        Needles = @("format text-selection slice", "Windows text-selection selected-text helper contracts", "backend diagnostic preservation", "selected-text capture task", "mouse selection capture result mapping")
    },
    @{
        Name = "custom streaming profile dry-run includes app and CLI live-stream coverage"
        Profiles = @("custom-streaming")
        Needles = @("format custom streaming slice", "app custom streaming contracts", "CLI Doubao local SSE contract", "CLI Gemini local SSE contract")
    },
    @{
        Name = "traditional HTTP profile dry-run includes native provider and CLI coverage"
        Profiles = @("traditional-http")
        Needles = @("format traditional HTTP slice", "traditional HTTP planner/parser/preflight contracts", "Quick Translate traditional HTTP providers", "Quick Translate Bing two-phase provider", "CLI traditional providers avoid worker/CompatHost wording")
    },
    @{
        Name = "translation cache profile dry-run includes memory, persistent, clear, and LongDoc coverage"
        Profiles = @("translation-cache")
        Needles = @("format translation cache slice", "translation cache core contracts", "Quick Translate cache clear/status contracts", "LongDoc persistent cache contracts", "LongDoc formula cache hash contract")
    },
    @{
        Name = "Built-in AI registration profile dry-run includes app and registration coverage"
        Profiles = @("builtin-ai-registration")
        Needles = @("format Built-in AI registration slice", "app Built-in AI registration state/lifecycle", "OpenAI-compatible Built-in AI registration contract")
    },
    @{
        Name = "OpenAI-compatible profile dry-run includes route coverage"
        Profiles = @("openai-compatible")
        Needles = @("OpenAI-compatible planner and executor contracts", "Quick Translate OpenAI-compatible routes", "CLI OpenAI translate/grammar/batch contracts", "CLI DeepSeek native contract")
    },
    @{
        Name = "Foundry Local profile dry-run includes route and no-worker coverage"
        Profiles = @("foundry-local")
        Needles = @("format Foundry Local slice", "app Foundry Local prepare state/lifecycle", "Quick Translate Auto Foundry route diagnostics", "Quick Translate packaged Auto LocalAI stale app-dir boundary", "CLI Auto Foundry route diagnostics", "LongDoc Auto Foundry route diagnostics", "OpenAI-compatible Foundry Local prepare contract")
    },
    @{
        Name = "OpenVINO download profile dry-run includes diagnostics and asset contracts"
        Profiles = @("openvino-download")
        Needles = @("format OpenVINO download slice", "OpenVINO download contracts and diagnostics")
    },
    @{
        Name = "browser support profile dry-run includes registrar and extension coverage"
        Profiles = @("browser-support")
        Needles = @("browser registrar behavior contracts", "browser registrar binary contracts", "browser extension default release contracts", "browser extension package scanning contracts")
    },
    @{
        Name = "OCR diagnostics profile dry-run includes capture background coverage"
        Profiles = @("ocr-diagnostics")
        Needles = @("format OCR diagnostics slice", "Windows screen capture helper contracts", "Windows native OCR helper contracts", "HTTP backend parse diagnostics", "app screen capture facade contracts", "app OCR capture diagnostics", "capture background diagnostics", "window snapshot diagnostics", "snapshot startup contract", "native capture helper task surface")
    },
    @{
        Name = "MDX native profile dry-run includes lookup and resource coverage"
        Profiles = @("mdx-native")
        Needles = @("rs-mdict default contracts", "optional Collins real-corpus MDX/MDD contracts", "app native MDX/MDD lookup contracts", "quick translate MDX service contracts", "settings MDD companion discovery contracts")
    },
    @{
        Name = "local dictionary suggestions profile dry-run includes index coverage"
        Profiles = @("local-dictionary-suggestions")
        Needles = @("LexIndex LXDX contracts", "LexIndex CLI contracts", "persistent local dictionary index contracts", "Quick Translate local dictionary suggestion contracts")
    },
    @{
        Name = "LongDoc layout profile dry-run includes DocLayout, Vision, and TATR coverage"
        Profiles = @("longdoc-layout")
        Needles = @("format LongDoc layout slice", "layout model download contract", "DocLayout-YOLO preprocessing contracts", "DocLayout-YOLO ONNX helper contract", "vision layout request/parser/executor contract", "TATR table structure contracts", "TATR ONNX helper contract", "explicit VisionLLM config errors", "vision backend page diagnostics", "explicit TATR setup diagnostics")
    },
    @{
        Name = "LongDoc export profile dry-run includes export and PDF coverage"
        Profiles = @("longdoc-export")
        Needles = @("format LongDoc export slice", "LongDoc text and markdown export composers", "PDF content-stream patch contract", "native PDF export contract", "PDF export block overlay metadata", "PDF source extraction export metadata")
    },
    @{
        Name = "LongDoc formula profile dry-run includes preservation and layout coverage"
        Profiles = @("longdoc-formula")
        Needles = @("format LongDoc formula/layout slice", "text layout wrapping and fit contracts", "font metrics contracts", "document layout geometry contracts", "LaTeX render-text simplifier", "formula protection contracts", "content preservation service contracts", "formula-aware text reconstruction contracts", "character paragraph evidence contracts", "PDF formula adapter contracts", "native LongDoc formula integration")
    },
    @{
        Name = "WindowsAI native profile dry-run includes route coverage"
        Profiles = @("windows-ai-native")
        Needles = @("WindowsAI lib native contracts", "Quick Translate native WindowsAI client routes", "CLI native WindowsAI route contracts", "LongDoc native WindowsAI routes")
    },
    @{
        Name = "rust-only boundary profile dry-run includes default no-runtime coverage"
        Profiles = @("rust-only-boundary")
        Needles = @("format default runtime boundary slice", "runtime policy defaults stay rust-only", "default app source has no retained runtime entries", "default app process spawn allowlist stays narrow", "default CLI translate stays native", "CLI LocalAI no-worker boundary", "LongDoc CLI stale payload boundaries", "LongDoc current app-dir ignores hybrid env")
    },
    @{
        Name = "rs portable release profile dry-run includes default packaging gates"
        Profiles = @("rs-portable-release")
        Needles = @("format rs portable release slice", "release defaults to rs portable", "rs portable release workflow and release-asset contracts", "zip validation excludes retained runtime for CLI entrypoint", "zip validation excludes retained runtime for GUI entrypoint")
    }
)

foreach ($case in $profileDryRunCases) {
    $currentCase = $case
    Invoke-TestCase $currentCase.Name {
        $dryRun = Get-ProfileDryRunText -Profiles @($currentCase.Profiles)
        foreach ($needle in @($currentCase.Needles)) {
            Assert-Contains -Text $dryRun -Needle $needle -Context $currentCase.Name
        }
        Assert-NotContains -Text $dryRun -Needle "Waiting for core validation isolation lock." -Context $currentCase.Name
    }
}

Invoke-TestCase "run recommended profiles dry-run selects MDX native lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\mdx_native.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): mdx-native" -Context "mdx run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "mdx-native / app native MDX/MDD lookup contracts" -Context "mdx run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects OpenAI lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\openai_compatible.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): openai-compatible" -Context "OpenAI run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "openai-compatible / OpenAI-compatible planner and executor contracts" -Context "OpenAI run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects top lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("lib\easydict-windows-ai\src\lib.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): windows-ai-native" -Context "WindowsAI run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "windows-ai-native / WindowsAI lib native contracts" -Context "WindowsAI run-recommended dry-run"
}

Invoke-TestCase "recommended trailing whitespace dry-run scans docs alongside code paths" {
    $paths = @(Get-TrailingWhitespaceCheckPaths -ChangedPath @(
            "rs\scripts\Invoke-RsCoreSliceValidation.ps1",
            "migration-list.md",
            "refactor-progress.md",
            "experience.md"
        ) -DiffFrom "gstep:@" -DiffTo "worktree")
    $dryRun = ConvertTo-DryRunText `
        -Steps (Get-ValidationStepsForProfiles -Profiles @("core-validation-tooling")) `
        -TrailingWhitespacePaths $paths
    Assert-Contains -Text $dryRun -Needle "trailing whitespace check: rg -n" -Context "trailing whitespace dry-run"
    Assert-Contains -Text $dryRun -Needle "migration-list.md" -Context "trailing whitespace dry-run"
    Assert-Contains -Text $dryRun -Needle "refactor-progress.md" -Context "trailing whitespace dry-run"
    Assert-Contains -Text $dryRun -Needle "experience.md" -Context "trailing whitespace dry-run"
}

Invoke-TestCase "standalone trailing whitespace dry-run accepts explicit changed paths" {
    $paths = @(Get-TrailingWhitespaceCheckPaths -ChangedPath @("experience.md") -DiffFrom "gstep:@" -DiffTo "worktree")
    $dryRun = ConvertTo-DryRunText -Steps @() -TrailingWhitespacePaths $paths
    Assert-Contains -Text $dryRun -Needle "trailing whitespace check: rg -n" -Context "standalone trailing whitespace dry-run"
    Assert-Contains -Text $dryRun -Needle "experience.md" -Context "standalone trailing whitespace dry-run"
    Assert-NotContains -Text $dryRun -Needle "No validation profile matched" -Context "standalone trailing whitespace dry-run"
}

Invoke-TestCase "dry-run can preview one-pass validation and gstep checkpoint" {
    $dryRun = ConvertTo-DryRunText `
        -Steps (Get-ValidationStepsForProfiles -Profiles @("core-validation-tooling")) `
        -TrailingWhitespacePaths @("rs/scripts/Invoke-RsCoreSliceValidation.ps1", "rs/scripts/Test-RsCoreSliceValidation.ps1") `
        -GstepCommitMessage "Checkpoint validation tooling"
    Assert-Contains -Text $dryRun -Needle "validation wrapper self-tests" -Context "checkpoint dry-run"
    Assert-Contains -Text $dryRun -Needle "trailing whitespace check: rg -n" -Context "checkpoint dry-run"
    Assert-Contains -Text $dryRun -Needle 'gstep checkpoint after successful validation: gstep commit -m "Checkpoint validation tooling"' -Context "checkpoint dry-run"
}

Invoke-TestCase "black-box profile dry-run avoids isolation and cargo execution" {
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

Invoke-TestCase "black-box checkpoint dry-run avoids isolation and previews checkpoint" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-Profile",
        "core-validation-tooling",
        "-CheckTrailingWhitespace",
        "-DryRun",
        "-GstepCommitMessage",
        "Checkpoint validation tooling"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle 'gstep checkpoint after successful validation: gstep commit -m "Checkpoint validation tooling"' -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "trailing whitespace check: rg -n" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "black-box recommendation json is parseable and preserves close-out commands" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "lib\easydict-windows-text-selection\src\lib.rs",
        "-Json"
    )
    Assert-ExitCode -Result $result -Expected 0
    $report = $result.Output | ConvertFrom-Json
    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection", "text-selection") `
        -Actual @($report.DefaultSelectedProfiles) `
        -Context $result.Command
    Assert-Contains `
        -Text $report.Commands.DefaultRecommendedCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -CheckTrailingWhitespace" `
        -Context $result.Command
    Assert-Contains `
        -Text (@($report.Results[0].Steps | ForEach-Object { $_.Name }) -join "`n") `
        -Needle "format input action slice" `
        -Context $result.Command
}

Invoke-TestCase "all recommended profiles flag requires run-recommended mode" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-AllRecommendedProfiles",
        "-DryRun"
    )
    if ($result.ExitCode -eq 0) {
        throw "Expected -AllRecommendedProfiles without -RunRecommendedProfiles to fail. Output:`n$($result.Output)"
    }
    Assert-Contains -Text $result.Output -Needle "-AllRecommendedProfiles is only valid with -RunRecommendedProfiles" -Context $result.Command
}

Invoke-TestCase "checkpoint message keeps isolation enabled" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-NoParallelUiIsolation",
        "-Profile",
        "core-validation-tooling",
        "-DryRun",
        "-GstepCommitMessage",
        "Unsafe checkpoint"
    )
    if ($result.ExitCode -eq 0) {
        throw "Expected checkpoint with -NoParallelUiIsolation to fail. Output:`n$($result.Output)"
    }
    Assert-Contains -Text $result.Output -Needle "-GstepCommitMessage cannot be combined with -NoParallelUiIsolation" -Context $result.Command
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

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

    Format-ValidationDryRunText `
        -Steps $Steps `
        -CheckTrailingWhitespace:($PSBoundParameters.ContainsKey("TrailingWhitespacePaths")) `
        -TrailingWhitespacePaths $TrailingWhitespacePaths `
        -GstepCommitMessage $GstepCommitMessage
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
    $recommendedPlan = New-RecommendedValidationPlan `
            -Recommendation $recommendation `
            -AllRecommendedProfiles:$AllRecommendedProfiles `
            -MaxRecommendedProfiles $MaxRecommendedProfiles
    $profileNames = @($recommendedPlan.SelectedResults | ForEach-Object { $_.Profile })

    "Selected recommended validation profile(s): $($profileNames -join ', ')`n" +
    (ConvertTo-DryRunText -Steps $recommendedPlan.Steps)
}

. ([scriptblock]::Create((Get-ValidationWrapperDefinitionText)))

Invoke-TestCase "profile list includes tooling lane" {
    foreach ($profileName in @("core-validation-tooling", "app-core-catalog", "app-preview-window", "asset-downloads", "cli-translate", "longdoc-cli", "longdoc-script", "mdx-native", "openai-compatible", "retained-worker-ipc", "pdf-to-images", "pdf-overlay", "store-listings", "encrypt-secret", "msix-validate", "icon-generator", "runtime-guards", "windows-registry", "nllb-native", "rust-helper-build", "dotnet-runtime-extract", "msix-runtime-profile", "startup-activation", "translation-cache", "windows-ai-native")) {
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

Invoke-TestCase "checkpoint re-isolates late parallel UI drift before scope guard" {
    Assert-Contains -Text $wrapperText -Needle "late parallel UI/parity or generated file(s) before checkpoint" -Context $wrapper
    Assert-Contains -Text $wrapperText -Needle "gstep-at-checkpoint" -Context $wrapper
    Assert-Contains -Text $wrapperText -Needle '$alreadyIsolated = $normalizedAlreadyIsolatedFiles -contains $normalizedRelativePath' -Context $wrapper
    Assert-Contains -Text $wrapperText -Needle "if (-not `$alreadyIsolated)" -Context $wrapper
    Assert-Contains -Text $wrapperText -Needle "backup late `$relativePath" -Context $wrapper
    Assert-Contains -Text $wrapperText -Needle "re-isolate late `$relativePath" -Context $wrapper
    Assert-NotContains -Text $wrapperText -Needle '$normalizedAlreadyIsolatedFiles -notcontains $normalizedPath' -Context $wrapper

    $lateIndex = $wrapperText.IndexOf("late parallel UI/parity or generated file(s) before checkpoint", [System.StringComparison]::Ordinal)
    $guardIndex = $wrapperText.LastIndexOf('$unexpectedPaths = @(Get-UnexpectedCheckpointPaths', [System.StringComparison]::Ordinal)
    $commitIndex = $wrapperText.IndexOf('Running post-validation checkpoint:', [System.StringComparison]::Ordinal)
    if ($lateIndex -lt 0 -or $guardIndex -lt 0 -or $commitIndex -lt 0 -or $lateIndex -gt $guardIndex -or $guardIndex -gt $commitIndex) {
        throw "Late parallel UI isolation must run before the checkpoint scope guard and before gstep commit."
    }
}

Invoke-TestCase "tooling changes only recommend tooling lane" {
    Assert-RecommendationExact `
        -ChangedPath @("rs\scripts\Invoke-RsCoreSliceValidation.ps1") `
        -ExpectedProfiles @("core-validation-tooling") `
        -Context "tooling path recommendation"
}

Invoke-TestCase "planned changed paths still recommend profiles with an empty diff" {
    $filteredDiff = Get-RecommendationDiffText `
        -DiffText "" `
        -AllowedPaths @("rs\crates\easydict_icon_generator\src\lib.rs")
    if ($filteredDiff -ne "") {
        throw "Empty clean-tree recommendation diff should remain empty."
    }

    $recommendation = Get-ProfileRecommendations `
        -Paths @("rs\crates\easydict_icon_generator\src\lib.rs") `
        -DiffText $filteredDiff
    Assert-SameStringSequence `
        -Expected @("icon-generator") `
        -Actual @($recommendation.Results | ForEach-Object { $_.Profile }) `
        -Context "empty diff planned path recommendation"
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
    @{ Name = "shared resource download changes recommend asset download lane"; Path = "rs\crates\easydict_app\src\resource_download.rs"; Profiles = @("asset-downloads") },
    @{ Name = "CJK font download changes recommend asset download lane"; Path = "rs\crates\easydict_app\src\font_download.rs"; Profiles = @("asset-downloads") },
    @{ Name = "native bridge changes recommend native bridge lane"; Path = "rs\crates\easydict_app\src\native_bridge.rs"; Profiles = @("native-bridge") },
    @{ Name = "named-event IPC changes recommend native bridge lane"; Path = "lib\easydict-windows-ipc\src\lib.rs"; Profiles = @("native-bridge") },
    @{ Name = "browser registrar changes recommend browser support lane"; Path = "rs\crates\easydict_app\src\browser_registrar.rs"; Profiles = @("browser-support") },
    @{ Name = "browser extension package shim changes recommend browser support lane"; Path = "browser-extension\scripts\Package-Extension.ps1"; Profiles = @("browser-support") },
    @{ Name = "protocol facade changes recommend protocol lane"; Path = "rs\crates\easydict_app\src\protocol_core.rs"; Profiles = @("protocol-facade") },
    @{ Name = "retained worker client changes recommend retained IPC lane"; Path = "rs\crates\easydict_app\src\compat_client.rs"; Profiles = @("retained-worker-ipc") },
    @{ Name = "retained worker IPC mock changes recommend retained IPC lane"; Path = "rs\crates\easydict_app\src\bin\easydict_ipc_mock.rs"; Profiles = @("retained-worker-ipc") },
    @{ Name = "startup activation changes recommend activation lane"; Path = "rs\crates\easydict_app\src\activation.rs"; Profiles = @("startup-activation") },
    @{ Name = "settings storage changes recommend settings credential lane"; Path = "rs\crates\easydict_app\src\settings_storage.rs"; Profiles = @("settings-credentials") },
    @{ Name = "settings runtime status changes recommend runtime-status lane"; Path = "rs\crates\easydict_app\src\settings_status.rs"; Profiles = @("settings-runtime-status") },
    @{ Name = "app data changes recommend core catalog lane"; Path = "rs\crates\easydict_app\src\app_data.rs"; Profiles = @("app-core-catalog") },
    @{ Name = "translation service catalog changes recommend core catalog lane"; Path = "rs\crates\easydict_app\src\translation_services.rs"; Profiles = @("app-core-catalog") },
    @{ Name = "app preview binary changes recommend preview/window lane"; Path = "rs\crates\easydict_app\src\main.rs"; Profiles = @("app-preview-window") },
    @{ Name = "window options changes recommend preview/window lane"; Path = "rs\crates\easydict_app\src\window_options.rs"; Profiles = @("app-preview-window") },
    @{ Name = "preview iced binary changes recommend preview/window lane"; Path = "rs\crates\easydict_preview_iced\src\main.rs"; Profiles = @("app-preview-window") },
    @{ Name = "CLI parser changes recommend CLI translate lane"; Path = "rs\crates\easydict_app\src\cli_translate.rs"; Profiles = @("cli-translate") },
    @{ Name = "CLI binary entrypoint changes recommend CLI translate lane"; Path = "rs\crates\easydict_app\src\bin\easydict_cli.rs"; Profiles = @("cli-translate") },
    @{ Name = "LongDoc CLI parser changes recommend LongDoc CLI lane"; Path = "rs\crates\easydict_app\src\long_document_cli.rs"; Profiles = @("longdoc-cli") },
    @{ Name = "LongDoc CLI binary entrypoint changes recommend LongDoc CLI lane"; Path = "rs\crates\easydict_app\src\bin\easydict_long_doc.rs"; Profiles = @("longdoc-cli") },
    @{ Name = "LongDoc root script changes recommend LongDoc script lane"; Path = "scripts\translate-long-doc.ps1"; Profiles = @("longdoc-script") },
    @{ Name = "mouse selection reducer changes recommend mouse selection lane"; Path = "rs\crates\easydict_app\src\mouse_selection.rs"; Profiles = @("mouse-selection") },
    @{ Name = "TATR ONNX changes recommend longdoc layout lane"; Path = "rs\crates\easydict_app\src\table_structure_onnx.rs"; Profiles = @("longdoc-layout") },
    @{ Name = "Vision layout changes recommend longdoc layout lane"; Path = "rs\crates\easydict_app\src\vision_layout.rs"; Profiles = @("longdoc-layout") },
    @{ Name = "DocLayout YOLO changes recommend longdoc layout lane"; Path = "rs\crates\easydict_app\src\doc_layout_yolo.rs"; Profiles = @("longdoc-layout") },
    @{ Name = "native PDF export changes recommend longdoc export lane"; Path = "rs\crates\easydict_app\src\pdf_native_export.rs"; Profiles = @("longdoc-export") },
    @{ Name = "PDF source extraction changes recommend longdoc export lane"; Path = "rs\crates\easydict_app\src\pdf_source_extraction.rs"; Profiles = @("longdoc-export") },
    @{ Name = "content preservation changes recommend longdoc formula lane"; Path = "rs\crates\easydict_app\src\content_preservation.rs"; Profiles = @("longdoc-formula") },
    @{ Name = "text layout changes recommend longdoc formula lane"; Path = "rs\crates\easydict_app\src\text_layout.rs"; Profiles = @("longdoc-formula") },
    @{ Name = "MDX native lookup changes recommend MDX native lane"; Path = "rs\crates\easydict_app\src\mdx_native.rs"; Profiles = @("mdx-native") },
    @{ Name = "MDX real-corpus validation script changes recommend MDX native lane"; Path = "rs\scripts\Invoke-MdxRealCorpusValidation.ps1"; Profiles = @("mdx-native") },
    @{ Name = "local dictionary index changes recommend suggestion lane"; Path = "rs\crates\easydict_app\src\local_dictionary_index.rs"; Profiles = @("local-dictionary-suggestions") },
    @{ Name = "PDF render helper changes recommend PDF-to-images lane"; Path = "lib\easydict-pdf-render\src\lib.rs"; Profiles = @("pdf-to-images") },
    @{ Name = "PDF-to-images CLI changes recommend PDF-to-images lane"; Path = "rs\crates\easydict_pdf_to_images\src\main.rs"; Profiles = @("pdf-to-images") },
    @{ Name = "PDF-to-images shim changes recommend PDF-to-images lane"; Path = "dotnet\scripts\pdf-to-images.ps1"; Profiles = @("pdf-to-images") },
    @{ Name = "PDF overlay helper changes recommend PDF overlay lane"; Path = "lib\easydict-pdf-overlay\src\lib.rs"; Profiles = @("pdf-overlay") },
    @{ Name = "Store listing Rust tool changes recommend store-listings lane"; Path = "rs\crates\easydict_store_listings\src\lib.rs"; Profiles = @("store-listings") },
    @{ Name = "Store listing metadata changes recommend store-listings lane"; Path = ".winstore\listings\en-us.yaml"; Profiles = @("store-listings") },
    @{ Name = "Store listing shim changes recommend store-listings lane"; Path = ".winstore\scripts\Sync-StoreListings.ps1"; Profiles = @("store-listings") },
    @{ Name = "Store listing workflow changes recommend store-listings lane"; Path = ".github\workflows\store-listings.yml"; Profiles = @("store-listings") },
    @{ Name = "Encrypt secret Rust tool changes recommend encrypt-secret lane"; Path = "rs\crates\easydict_encrypt_secret\src\lib.rs"; Profiles = @("encrypt-secret") },
    @{ Name = "MSIX validator Rust tool changes recommend msix-validate lane"; Path = "rs\crates\easydict_msix_validate\src\lib.rs"; Profiles = @("msix-validate") },
    @{ Name = "Icon generator Rust tool changes recommend icon-generator lane"; Path = "rs\crates\easydict_icon_generator\src\lib.rs"; Profiles = @("icon-generator") },
    @{ Name = "runtime guard changes recommend runtime-guards lane"; Path = "lib\easydict-runtime-guards\src\lib.rs"; Profiles = @("runtime-guards") },
    @{ Name = "Windows registry helper changes recommend windows-registry lane"; Path = "lib\easydict-windows-registry\src\lib.rs"; Profiles = @("windows-registry") },
    @{ Name = "NLLB helper changes recommend NLLB native lane"; Path = "lib\easydict-nllb\src\lib.rs"; Profiles = @("nllb-native") },
    @{ Name = "Windows asset shim changes recommend icon-generator lane"; Path = "dotnet\scripts\generate-windows-assets.ps1"; Profiles = @("icon-generator") },
    @{ Name = "macOS icon refresh shim changes recommend icon-generator lane"; Path = "dotnet\scripts\generate-assets-from-macos-icon.ps1"; Profiles = @("icon-generator") },
    @{ Name = "service icon shim changes recommend icon-generator lane"; Path = "dotnet\scripts\convert-service-icons.ps1"; Profiles = @("icon-generator") },
    @{ Name = "Rust helper build shim changes recommend helper build lane"; Path = "dotnet\scripts\Build-RustHelpers.ps1"; Profiles = @("rust-helper-build") },
    @{ Name = ".NET runtime extraction shim changes recommend runtime extract lane"; Path = "dotnet\scripts\Extract-DotnetRuntime.ps1"; Profiles = @("dotnet-runtime-extract") },
    @{ Name = "MSIX sign-and-install shim changes recommend MSIX runtime profile lane"; Path = "dotnet\scripts\sign-and-install.ps1"; Profiles = @("msix-runtime-profile") },
    @{ Name = "QDC deploy shim changes recommend MSIX runtime profile lane"; Path = "dotnet\scripts\qdc\Deploy-ToQdc.ps1"; Profiles = @("msix-runtime-profile") },
    @{ Name = "QDC install shim changes recommend MSIX runtime profile lane"; Path = "dotnet\scripts\qdc\Install-OnQdc.ps1"; Profiles = @("msix-runtime-profile") },
    @{ Name = "UI automation MSIX workflow changes recommend MSIX runtime profile lane"; Path = ".github\workflows\ui-automation.yml"; Profiles = @("msix-runtime-profile") },
    @{ Name = "MSIX runtime profile contract changes recommend MSIX runtime profile lane"; Path = "rs\crates\easydict_packager\tests\msix_runtime_profile_contract_behavior.rs"; Profiles = @("msix-runtime-profile") },
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

Invoke-TestCase "Makefile encrypt-secret diffs default to encrypt-secret lane" {
    $diffText = @'
+# Usage: make encrypt-secret SECRET=your-secret
+encrypt-secret:
+ifndef SECRET
+	$(error SECRET is required. Usage: make encrypt-secret SECRET=your-secret)
+endif
+	@cargo run --manifest-path ../rs/Cargo.toml -p easydict_encrypt_secret -- "$(SECRET)"
'@
    $recommendation = Get-TestRecommendation `
        -ChangedPath @("dotnet\Makefile") `
        -DiffText $diffText
    $selected = @(Get-SelectedRecommendationResults -Recommendation $recommendation | ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("encrypt-secret") `
        -Actual $selected `
        -Context "Makefile encrypt-secret default recommendation"
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

Invoke-TestCase "settings runtime status reducer diffs default to runtime-status lane" {
    $recommendation = Get-TestRecommendation `
        -ChangedPath @("rs\crates\easydict_app\src\state.rs") `
        -DiffText "SettingsRuntimeStatusLoaded settings_runtime_status open_vino_status foundry_local_status windows_ai_status"
    $selected = @(Get-SelectedRecommendationResults -Recommendation $recommendation | ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("settings-runtime-status") `
        -Actual $selected `
        -Context "Settings runtime-status default recommendation"
}

Invoke-TestCase "Foundry CLI entrypoint diffs append Foundry lane without suppressing CLI close-out" {
    $recommendation = Get-TestRecommendation `
        -ChangedPath @("rs\crates\easydict_app\src\bin\easydict_cli.rs") `
        -DiffText "Foundry Local foundry_local auto_foundry_local_native_probe_request"
    $selected = @(Get-SelectedRecommendationResults -Recommendation $recommendation | ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("cli-translate", "foundry-local") `
        -Actual $selected `
        -Context "Foundry CLI entrypoint default recommendation"
}

Invoke-TestCase "changed-path diff text narrows broad CLI behavior recommendations" {
    $recommendation = Get-TestRecommendation `
        -ChangedPath @("rs\crates\easydict_app\tests\cli_translate_behavior.rs") `
        -DiffText @"
diff --git a/rs/crates/easydict_app/tests/cli_translate_behavior.rs b/rs/crates/easydict_app/tests/cli_translate_behavior.rs
+fn custom_streaming_stream_command_writes_doubao_chunks_before_sse_response_completes() {}
+fn custom_streaming_stream_command_writes_gemini_chunks_before_sse_response_completes() {}
+let _ = "Gemini Doubao response.output_text.delta streamGenerateContent";
"@
    $selected = @(Get-SelectedRecommendationResults -Recommendation $recommendation | ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("custom-streaming") `
        -Actual $selected `
        -Context "custom streaming CLI behavior selected profiles"
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
    Assert-Contains -Text $output -Needle "Default selected profile(s) for -RunRecommendedProfiles:" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "input-actions, mouse-selection, text-selection" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Default selected unique validation step count:" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Default selected validation step(s):" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "input-actions / Windows text-selection clipboard/insertion helper contracts" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Default fast close-out:" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Invoke-RsCoreSliceValidation.ps1 -CloseOut -ChangedPath lib/easydict-windows-text-selection/src/lib.rs" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Default fast close-out dry-run:" -Context "shared text-selection recommendation output"
    Assert-Contains -Text $output -Needle "Invoke-RsCoreSliceValidation.ps1 -CloseOut -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -DryRun" -Context "shared text-selection recommendation output"
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
    $defaultPlan = New-RecommendedValidationPlan -Recommendation $recommendation

    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection", "text-selection") `
        -Actual @($report.DefaultSelectedProfiles) `
        -Context "recommendation report default selected profiles"
    if ($report.DefaultSelectedStepCount -ne @($defaultPlan.Steps).Count) {
        throw "recommendation report should expose the default selected unique step count. Expected $(@($defaultPlan.Steps).Count), got $($report.DefaultSelectedStepCount)."
    }
    Assert-Contains `
        -Text (@($report.DefaultSelectedSteps | ForEach-Object { $_.Name }) -join "`n") `
        -Needle "input-actions / Windows text-selection clipboard/insertion helper contracts" `
        -Context "recommendation report default selected steps"
    Assert-SameStringSequence `
        -Expected @("lib/easydict-windows-text-selection/src/lib.rs") `
        -Actual @($report.Selector.ChangedPath) `
        -Context "recommendation report changed paths"
    Assert-Contains `
        -Text $report.Commands.DefaultRecommendedCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -CheckTrailingWhitespace" `
        -Context "recommendation report default close-out command"
    Assert-Contains `
        -Text $report.Commands.DefaultFastCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -CloseOut -ChangedPath lib/easydict-windows-text-selection/src/lib.rs" `
        -Context "recommendation report default fast close-out command"
    Assert-Contains `
        -Text $report.Commands.AllRecommendedCloseOut `
        -Needle "-CloseOut -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -AllRecommendedProfiles" `
        -Context "recommendation report all-recommended close-out command"

    $inputActions = @($report.Results | Where-Object { $_.Profile -eq "input-actions" })[0]
    Assert-Contains `
        -Text (@($inputActions.Steps | ForEach-Object { $_.Name }) -join "`n") `
        -Needle "Windows text-selection clipboard/insertion helper contracts" `
        -Context "recommendation report profile steps"
}

Invoke-TestCase "recommendation JSON keeps default selected fields as stable arrays" {
    $singleResult = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "rs\scripts\Invoke-RsCoreSliceValidation.ps1",
        "-Json"
    )
    Assert-ExitCode -Result $singleResult -Expected 0
    $singleReport = $singleResult.Output | ConvertFrom-Json
    if ($singleReport.DefaultSelectedProfiles -isnot [array]) {
        throw "Single-profile recommendation JSON should keep DefaultSelectedProfiles as an array. Output:`n$($singleResult.Output)"
    }
    if ($singleReport.DefaultSelectedSteps -isnot [array]) {
        throw "Single-profile recommendation JSON should keep DefaultSelectedSteps as an array. Output:`n$($singleResult.Output)"
    }
    Assert-SameStringSequence `
        -Expected @("core-validation-tooling") `
        -Actual @($singleReport.DefaultSelectedProfiles) `
        -Context $singleResult.Command
    Assert-Contains `
        -Text (@($singleReport.DefaultSelectedSteps | ForEach-Object { $_.Name }) -join "`n") `
        -Needle "core-validation-tooling / validation wrapper self-tests" `
        -Context $singleResult.Command

    $emptyResult = Invoke-ValidationWrapper -Arguments @(
        "-RecommendProfiles",
        "-ChangedPath",
        "experience.md",
        "-Json"
    )
    Assert-ExitCode -Result $emptyResult -Expected 0
    $emptyReport = $emptyResult.Output | ConvertFrom-Json
    if ($emptyReport.DefaultSelectedProfiles -isnot [array]) {
        throw "Empty recommendation JSON should keep DefaultSelectedProfiles as an array. Output:`n$($emptyResult.Output)"
    }
    if ($emptyReport.DefaultSelectedSteps -isnot [array]) {
        throw "Empty recommendation JSON should keep DefaultSelectedSteps as an array. Output:`n$($emptyResult.Output)"
    }
    if (@($emptyReport.DefaultSelectedProfiles).Count -ne 0 -or @($emptyReport.DefaultSelectedSteps).Count -ne 0) {
        throw "Empty recommendation JSON should keep default selected arrays empty. Output:`n$($emptyResult.Output)"
    }
}

Invoke-TestCase "dry-run JSON report keeps exact selected execution plan aligned" {
    $changedPath = @("lib\easydict-windows-text-selection\src\lib.rs")
    $recommendation = Get-TestRecommendation -ChangedPath $changedPath
    $plan = New-RecommendedValidationPlan -Recommendation $recommendation
    $report = New-ValidationDryRunReport `
        -Mode "run-recommended" `
        -SelectedProfiles @($plan.SelectedResults | ForEach-Object { $_.Profile }) `
        -Steps $plan.Steps `
        -CheckTrailingWhitespace $true `
        -TrailingWhitespacePaths @("lib/easydict-windows-text-selection/src/lib.rs", "experience.md") `
        -GstepCommitMessage "Checkpoint validation plan" `
        -Recommendation $recommendation `
        -ChangedPath $changedPath

    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection", "text-selection") `
        -Actual @($report.SelectedProfiles) `
        -Context "dry-run JSON selected profiles"
    if ($report.StepCount -ne @($plan.Steps).Count) {
        throw "dry-run JSON should expose the exact selected unique step count. Expected $(@($plan.Steps).Count), got $($report.StepCount)."
    }
    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection", "text-selection") `
        -Actual @($report.ProfileStepCoverage | ForEach-Object { $_.Profile }) `
        -Context "dry-run JSON profile step coverage"
    foreach ($profileCoverage in @($report.ProfileStepCoverage)) {
        $expectedStepCount = @($validationProfiles[$profileCoverage.Profile].Steps).Count
        if ($profileCoverage.StepCount -ne $expectedStepCount) {
            throw "dry-run JSON profile coverage for '$($profileCoverage.Profile)' should expose $expectedStepCount raw step(s), got $($profileCoverage.StepCount)."
        }
    }
    Assert-Contains `
        -Text (@($report.Steps | ForEach-Object { $_.Name }) -join "`n") `
        -Needle "input-actions / Windows text-selection clipboard/insertion helper contracts" `
        -Context "dry-run JSON selected steps"
    Assert-SameStringSequence `
        -Expected @("lib/easydict-windows-text-selection/src/lib.rs", "experience.md") `
        -Actual @($report.TrailingWhitespacePaths) `
        -Context "dry-run JSON trailing whitespace paths"
    if (-not $report.CheckTrailingWhitespace) {
        throw "dry-run JSON should record that trailing whitespace checking is enabled."
    }
    Assert-Contains `
        -Text $report.GstepCheckpoint.Display `
        -Needle 'gstep commit -m "Checkpoint validation plan"' `
        -Context "dry-run JSON checkpoint preview"
    Assert-Contains `
        -Text $report.Commands.CurrentCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -CheckTrailingWhitespace -GstepCommitMessage 'Checkpoint validation plan'" `
        -Context "dry-run JSON current close-out command"
    Assert-Contains `
        -Text $report.Commands.CurrentDryRunJson `
        -Needle "-DryRun -Json" `
        -Context "dry-run JSON current dry-run command"
    Assert-Contains `
        -Text $report.Commands.SelectedProfileCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -Profile input-actions,mouse-selection,text-selection -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -CheckTrailingWhitespace -GstepCommitMessage 'Checkpoint validation plan'" `
        -Context "dry-run JSON selected profile close-out command"
    Assert-Contains `
        -Text $report.Commands.SelectedProfileDryRunJson `
        -Needle "-DryRun -Json" `
        -Context "dry-run JSON selected profile dry-run command"
    Assert-SameStringSequence `
        -Expected @("lib/easydict-windows-text-selection/src/lib.rs") `
        -Actual @($report.Selector.ChangedPath) `
        -Context "dry-run JSON selector"
    Assert-Contains `
        -Text (@($report.Recommendation.Results | ForEach-Object { $_.Profile }) -join "`n") `
        -Needle "text-selection" `
        -Context "dry-run JSON recommendation summary"
}

Invoke-TestCase "dry-run JSON commands preserve all-recommended and capped recommender flags" {
    $changedPath = @("lib\easydict-windows-text-selection\src\lib.rs")
    $recommendation = Get-TestRecommendation -ChangedPath $changedPath
    $allPlan = New-RecommendedValidationPlan -Recommendation $recommendation -AllRecommendedProfiles
    $allReport = New-ValidationDryRunReport `
        -Mode "run-recommended" `
        -SelectedProfiles @($allPlan.SelectedResults | ForEach-Object { $_.Profile }) `
        -Steps $allPlan.Steps `
        -Recommendation $recommendation `
        -ChangedPath $changedPath `
        -AllRecommendedProfiles $true
    Assert-Contains `
        -Text $allReport.Commands.CurrentCloseOut `
        -Needle "-RunRecommendedProfiles -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -AllRecommendedProfiles" `
        -Context "all-recommended dry-run JSON command"

    $cappedPlan = New-RecommendedValidationPlan -Recommendation $recommendation -MaxRecommendedProfiles 2
    $cappedReport = New-ValidationDryRunReport `
        -Mode "run-recommended" `
        -SelectedProfiles @($cappedPlan.SelectedResults | ForEach-Object { $_.Profile }) `
        -Steps $cappedPlan.Steps `
        -Recommendation $recommendation `
        -ChangedPath $changedPath `
        -MaxRecommendedProfiles 2
    Assert-Contains `
        -Text $cappedReport.Commands.CurrentCloseOut `
        -Needle "-RunRecommendedProfiles -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -MaxRecommendedProfiles 2" `
        -Context "capped dry-run JSON command"
}

Invoke-TestCase "close-out dry-run JSON uses shortcut while preserving selected profile equivalent" {
    $changedPath = @("lib\easydict-windows-text-selection\src\lib.rs")
    $recommendation = Get-TestRecommendation -ChangedPath $changedPath
    $plan = New-RecommendedValidationPlan -Recommendation $recommendation
    $report = New-ValidationDryRunReport `
        -Mode "close-out" `
        -SelectedProfiles @($plan.SelectedResults | ForEach-Object { $_.Profile }) `
        -Steps $plan.Steps `
        -CheckTrailingWhitespace $true `
        -TrailingWhitespacePaths @("lib/easydict-windows-text-selection/src/lib.rs") `
        -GstepCommitMessage "Checkpoint validation plan" `
        -Recommendation $recommendation `
        -ChangedPath $changedPath

    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection", "text-selection") `
        -Actual @($report.SelectedProfiles) `
        -Context "close-out JSON selected profiles"
    Assert-Contains `
        -Text $report.Commands.CurrentCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -CloseOut -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -GstepCommitMessage 'Checkpoint validation plan'" `
        -Context "close-out JSON current command"
    Assert-NotContains `
        -Text $report.Commands.CurrentCloseOut `
        -Needle "-CheckTrailingWhitespace" `
        -Context "close-out JSON current command"
    Assert-Contains `
        -Text $report.Commands.SelectedProfileCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -Profile input-actions,mouse-selection,text-selection -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -CheckTrailingWhitespace -GstepCommitMessage 'Checkpoint validation plan'" `
        -Context "close-out JSON selected profile equivalent"
    Assert-Contains `
        -Text $report.Commands.CurrentDryRunJson `
        -Needle "-CloseOut -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -GstepCommitMessage 'Checkpoint validation plan' -DryRun -Json" `
        -Context "close-out JSON dry-run replay command"
}

Invoke-TestCase "dry-run JSON commands preserve explicit diff selectors with changed paths" {
    $changedPath = @("rs\crates\easydict_app\tests\cli_translate_behavior.rs")
    $recommendation = Get-TestRecommendation `
        -ChangedPath $changedPath `
        -DiffText "custom_streaming Gemini Doubao response.output_text.delta streamGenerateContent"
    $plan = New-RecommendedValidationPlan -Recommendation $recommendation
    $report = New-ValidationDryRunReport `
        -Mode "run-recommended" `
        -SelectedProfiles @($plan.SelectedResults | ForEach-Object { $_.Profile }) `
        -Steps $plan.Steps `
        -CheckTrailingWhitespace $true `
        -TrailingWhitespacePaths @("rs/crates/easydict_app/tests/cli_translate_behavior.rs") `
        -Recommendation $recommendation `
        -ChangedPath $changedPath `
        -DiffFrom "gstep:step-339" `
        -DiffTo "gstep:step-340"

    Assert-Contains `
        -Text $report.Commands.CurrentCloseOut `
        -Needle "-RunRecommendedProfiles -ChangedPath rs/crates/easydict_app/tests/cli_translate_behavior.rs -DiffFrom gstep:step-339 -DiffTo gstep:step-340 -CheckTrailingWhitespace" `
        -Context "current close-out selector command"
    Assert-Contains `
        -Text $report.Commands.SelectedProfileCloseOut `
        -Needle "-Profile custom-streaming -ChangedPath rs/crates/easydict_app/tests/cli_translate_behavior.rs -DiffFrom gstep:step-339 -DiffTo gstep:step-340 -CheckTrailingWhitespace" `
        -Context "selected profile selector command"
    Assert-Contains `
        -Text $report.Commands.CurrentDryRunJson `
        -Needle "-DiffFrom gstep:step-339 -DiffTo gstep:step-340 -CheckTrailingWhitespace -DryRun -Json" `
        -Context "current dry-run selector command"
}

Invoke-TestCase "generated dependency lock drift remains profile-exempt" {
    foreach ($path in @(
            "lib\easydict-windows-credentials\Cargo.lock",
            "lib\easydict-windows-dialogs\Cargo.lock",
            "lib\easydict-pdf-overlay\Cargo.lock",
            "lib\easydict-windows-registry\Cargo.lock"
        )) {
        $recommendation = Get-TestRecommendation -ChangedPath @($path)
        if ($recommendation.CorePaths.Count -ne 0 -or $recommendation.Results.Count -ne 0) {
            throw "$path should be profile-exempt. Core paths: $($recommendation.CorePaths -join ', '). Results: $(@($recommendation.Results | ForEach-Object { $_.Profile }) -join ', ')."
        }
    }
}

Invoke-TestCase "generated dependency lock drift is cleaned before checkpoint scope guard" {
    $cleanupIndex = $wrapperText.IndexOf(
        'Remove-GeneratedCargoLockDrift -Paths $generatedCargoLockFilesAbsentBeforeRun',
        [System.StringComparison]::Ordinal)
    $scopeIndex = $wrapperText.IndexOf(
        '$checkpointDirtyPaths = @(Get-GstepDirtyPaths -From "gstep:@" -To "worktree")',
        [System.StringComparison]::Ordinal)
    if ($cleanupIndex -lt 0 -or $scopeIndex -lt 0 -or $cleanupIndex -gt $scopeIndex) {
        throw "Generated dependency lock drift must be removed before checkpoint scope guard runs."
    }
}

Invoke-TestCase "parallel UI and platform files remain profile-exempt" {
    foreach ($path in @(
            "lib\winfluent-rs\crates\win_fluent\src\platform.rs",
            "lib\winfluent-rs\crates\win_fluent_platform_win\src\lib.rs"
        )) {
        $recommendation = Get-TestRecommendation -ChangedPath @($path)
        if ($recommendation.CorePaths.Count -ne 0 -or $recommendation.Results.Count -ne 0) {
            throw "$path should be profile-exempt as parallel UI/platform work. Core paths: $($recommendation.CorePaths -join ', '). Results: $(@($recommendation.Results | ForEach-Object { $_.Profile }) -join ', ')."
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

Invoke-TestCase "run recommended profiles keeps tooling lane with higher-scoring feature lane" {
    $recommendation = Get-TestRecommendation `
        -ChangedPath @(
            "rs\crates\easydict_app\src\translation_cache.rs",
            "rs\scripts\Invoke-RsCoreSliceValidation.ps1"
        ) `
        -DiffText "ClearTranslationCache LongDocumentTranslationCache translation_cache_status"
    $selected = @(Get-SelectedRecommendationResults -Recommendation $recommendation |
        ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("translation-cache", "core-validation-tooling") `
        -Actual $selected `
        -Context "feature plus tooling default recommendation"

    $capped = @(Get-SelectedRecommendationResults `
            -Recommendation $recommendation `
            -MaxRecommendedProfiles 1 |
        ForEach-Object { $_.Profile })
    Assert-SameStringSequence `
        -Expected @("translation-cache") `
        -Actual $capped `
        -Context "explicit recommendation cap"
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

Invoke-TestCase "LongDoc script parser validation command runs without quoting drift" {
    $parserStep = @($validationProfiles["longdoc-script"].Steps | Where-Object { $_.Name -eq "PowerShell parse LongDoc script shim" })[0]
    if ($null -eq $parserStep) {
        throw "Expected LongDoc script parser step to exist."
    }

    $output = & $parserStep.Command[0] @($parserStep.Command | Select-Object -Skip 1) 2>&1 | Out-String -Width 4096
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "LongDoc script parser command failed with exit code $exitCode.`n$output"
    }
}

Invoke-TestCase "browser extension script parser validation command runs without quoting drift" {
    $parserStep = @($validationProfiles["browser-support"].Steps | Where-Object { $_.Name -eq "PowerShell parse browser extension package shim" })[0]
    if ($null -eq $parserStep) {
        throw "Expected browser extension script parser step to exist."
    }

    $output = & $parserStep.Command[0] @($parserStep.Command | Select-Object -Skip 1) 2>&1 | Out-String -Width 4096
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Browser extension script parser command failed with exit code $exitCode.`n$output"
    }
}

Invoke-TestCase "Rust helper build script parser validation command runs without quoting drift" {
    $parserStep = @($validationProfiles["rust-helper-build"].Steps | Where-Object { $_.Name -eq "PowerShell parse Rust helper build shim" })[0]
    if ($null -eq $parserStep) {
        throw "Expected Rust helper build script parser step to exist."
    }

    $output = & $parserStep.Command[0] @($parserStep.Command | Select-Object -Skip 1) 2>&1 | Out-String -Width 4096
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Rust helper build script parser command failed with exit code $exitCode.`n$output"
    }
}

Invoke-TestCase ".NET runtime extraction script parser validation command runs without quoting drift" {
    $parserStep = @($validationProfiles["dotnet-runtime-extract"].Steps | Where-Object { $_.Name -eq "PowerShell parse .NET runtime extraction shim" })[0]
    if ($null -eq $parserStep) {
        throw "Expected .NET runtime extraction script parser step to exist."
    }

    $output = & $parserStep.Command[0] @($parserStep.Command | Select-Object -Skip 1) 2>&1 | Out-String -Width 4096
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw ".NET runtime extraction script parser command failed with exit code $exitCode.`n$output"
    }
}

Invoke-TestCase "MSIX and QDC install script parser validation command runs without quoting drift" {
    $parserStep = @($validationProfiles["msix-runtime-profile"].Steps | Where-Object { $_.Name -eq "PowerShell parse MSIX/QDC install shims" })[0]
    if ($null -eq $parserStep) {
        throw "Expected MSIX/QDC script parser step to exist."
    }

    $output = & $parserStep.Command[0] @($parserStep.Command | Select-Object -Skip 1) 2>&1 | Out-String -Width 4096
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "MSIX/QDC script parser command failed with exit code $exitCode.`n$output"
    }
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
        Name = "settings runtime-status profile dry-run includes filesystem/provider status and app writeback"
        Profiles = @("settings-runtime-status")
        Needles = @("format settings runtime-status slice", "settings runtime-status filesystem/provider contracts", "settings runtime-status app writeback contracts")
    },
    @{
        Name = "app core catalog profile dry-run includes app-data, service catalog, and no-runtime coverage"
        Profiles = @("app-core-catalog")
        Needles = @("format app data and service catalog slice", "app data root contracts", "translation service catalog contracts", "default CLI translate catalog boundary", "default process spawn no-runtime boundary")
    },
    @{
        Name = "app preview/window profile dry-run includes preview binary, window contracts, and no-runtime coverage"
        Profiles = @("app-preview-window")
        Needles = @("format app preview/window slice", "app preview binary builds", "preview iced portable GUI contracts", "preview iced portable GUI builds", "main window preview scenarios render", "window option and window-specific contracts", "default process spawn no-runtime boundary")
    },
    @{
        Name = "CLI translate profile dry-run includes parser, native default, legacy flags, and no-worker coverage"
        Profiles = @("cli-translate")
        Needles = @("format CLI translate slice", "CLI parser contracts", "default CLI rejects legacy retained-worker flags", "default CLI native Google smoke", "CLI LocalAI default no-worker boundary", "CLI legacy flags stay feature-gated", "default process spawn no-runtime boundary")
    },
    @{
        Name = "LongDoc CLI profile dry-run includes parser, stale payload, native preflight, and no-worker coverage"
        Profiles = @("longdoc-cli")
        Needles = @("format LongDoc CLI slice", "LongDoc CLI help omits legacy app-dir", "LongDoc CLI service list stays native", "LongDoc CLI stale payload boundaries", "LongDoc CLI target-auto no-worker boundary", "LongDoc CLI LocalAI native preflight boundary", "default process spawn no-runtime boundary")
    },
    @{
        Name = "LongDoc script profile dry-run includes parser and shim release-contract coverage"
        Profiles = @("longdoc-script")
        Needles = @("PowerShell parse LongDoc script shim", "LongDoc script Rust-only and helper forwarding contracts")
    },
    @{
        Name = "asset downloads profile dry-run includes shared resource, font, layout, and OpenVINO asset coverage"
        Profiles = @("asset-downloads")
        Needles = @("format shared asset download slice", "shared resource download policy contracts", "CJK font download contracts", "LongDoc layout model asset contracts", "OpenVINO asset download contracts")
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
        Needles = @("format custom streaming slice", "app custom streaming contracts", "CLI Doubao local SSE contract", "CLI Gemini local SSE contract", "CLI custom streaming latency contracts")
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
        Needles = @("PowerShell parse browser extension package shim", "browser registrar behavior contracts", "browser registrar binary contracts", "browser extension default release contracts", "browser extension PowerShell shim forwarding contract", "browser extension package scanning contracts")
    },
    @{
        Name = "retained worker IPC profile dry-run includes Rust mock, compat client, and default guard coverage"
        Profiles = @("retained-worker-ipc")
        Needles = @("format retained worker IPC slice", "retained worker IPC Rust mock binary builds", "retained worker IPC compatibility contracts", "retained IPC mock helper stays feature gated", "retained IPC tests avoid shell mock runtime")
    },
    @{
        Name = "startup activation profile dry-run includes parser, routing, shell/protocol, and boundary coverage"
        Profiles = @("startup-activation")
        Needles = @("format startup activation slice", "startup activation parser contracts", "startup activation app routing", "shell/protocol OCR activation contract", "startup activation stays app-core only")
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
        Name = "PDF-to-images profile dry-run includes render helper, shim, and CLI coverage"
        Profiles = @("pdf-to-images")
        Needles = @("format PDF-to-images slice", "PowerShell parse PDF-to-images shim", "Rust PDF render helper contracts", "PDF-to-images CLI contracts")
    },
    @{
        Name = "Store listings profile dry-run includes Rust tool, shim, and metadata coverage"
        Profiles = @("store-listings")
        Needles = @("format Store listing tool slice", "PowerShell parse Store listing shim", "Store listing Rust contracts", "Store listing metadata validates")
    },
    @{
        Name = "Encrypt secret profile dry-run includes Rust helper and retired .NET boundary coverage"
        Profiles = @("encrypt-secret")
        Needles = @("format encrypt-secret slice", "Rust secret encryption contracts")
    },
    @{
        Name = "MSIX validator profile dry-run includes validator Rust coverage"
        Profiles = @("msix-validate")
        Needles = @("format MSIX validator slice", "MSIX validator Rust contracts")
    },
    @{
        Name = "Icon generator profile dry-run includes Rust tool and shim coverage"
        Profiles = @("icon-generator")
        Needles = @("format icon generator slice", "PowerShell parse icon generator shims", "icon generator Rust contracts")
    },
    @{
        Name = "runtime guards profile dry-run includes default and retained-feature coverage"
        Profiles = @("runtime-guards")
        Needles = @("format runtime guards crate", "runtime guards default contracts", "runtime guards retained-feature contracts")
    },
    @{
        Name = "Windows registry profile dry-run includes helper contracts"
        Profiles = @("windows-registry")
        Needles = @("format Windows registry helper crate", "Windows registry helper contracts")
    },
    @{
        Name = "NLLB native profile dry-run includes default and ORT/OpenVINO coverage"
        Profiles = @("nllb-native")
        Needles = @("format NLLB/OpenVINO helper crate", "NLLB default contracts", "NLLB ORT/OpenVINO feature contracts")
    },
    @{
        Name = "PDF overlay profile dry-run includes helper contracts"
        Profiles = @("pdf-overlay")
        Needles = @("format PDF overlay helper crate", "PDF overlay helper contracts")
    },
    @{
        Name = "Rust helper build profile dry-run includes shim, child env, and legacy alias coverage"
        Profiles = @("rust-helper-build")
        Needles = @("format Rust helper build release-contract slice", "PowerShell parse Rust helper build shim", "release orchestration uses Rust helpers", "Rust helper build child env and shim contracts")
    },
    @{
        Name = ".NET runtime extraction profile dry-run includes hybrid-only shim and Rust extractor coverage"
        Profiles = @("dotnet-runtime-extract")
        Needles = @("format .NET runtime extraction release-contract slice", "PowerShell parse .NET runtime extraction shim", ".NET runtime extraction stays hybrid-gated", ".NET runtime extraction PowerShell shim contract", "Rust .NET runtime extractor contracts")
    },
    @{
        Name = "MSIX runtime-profile profile dry-run includes QDC/UI automation and validator ordering coverage"
        Profiles = @("msix-runtime-profile")
        Needles = @("format MSIX runtime-profile contracts", "PowerShell parse MSIX/QDC install shims", "MSIX runtime-profile static contracts", "sign-and-install validator ordering contracts", "QDC machine install validator ordering contract")
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

Invoke-TestCase "run recommended profiles dry-run selects MDX real-corpus helper lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\scripts\Invoke-MdxRealCorpusValidation.ps1")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): mdx-native" -Context "mdx real-corpus helper run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "mdx-native / optional Collins real-corpus MDX/MDD contracts" -Context "mdx real-corpus helper run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "mdx-native / app native MDX/MDD lookup contracts" -Context "mdx real-corpus helper run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects OpenAI lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\openai_compatible.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): openai-compatible" -Context "OpenAI run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "openai-compatible / OpenAI-compatible planner and executor contracts" -Context "OpenAI run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects asset download lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\resource_download.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): asset-downloads" -Context "asset download run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "asset-downloads / shared resource download policy contracts" -Context "asset download run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "asset-downloads / CJK font download contracts" -Context "asset download run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects app core catalog lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\translation_services.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): app-core-catalog" -Context "app core catalog run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "app-core-catalog / app data root contracts" -Context "app core catalog run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "app-core-catalog / translation service catalog contracts" -Context "app core catalog run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects app preview/window lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\main.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): app-preview-window" -Context "app preview run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "app-preview-window / app preview binary builds" -Context "app preview run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "app-preview-window / preview iced portable GUI contracts" -Context "app preview run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "app-preview-window / main window preview scenarios render" -Context "app preview run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects window options lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\window_options.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): app-preview-window" -Context "window options run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "app-preview-window / window option and window-specific contracts" -Context "window options run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects preview iced binary lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_preview_iced\src\main.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): app-preview-window" -Context "preview iced run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "app-preview-window / preview iced portable GUI contracts" -Context "preview iced run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "app-preview-window / preview iced portable GUI builds" -Context "preview iced run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): rust-only-boundary" -Context "preview iced run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects CLI translate lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\cli_translate.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): cli-translate" -Context "CLI translate run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "cli-translate / CLI parser contracts" -Context "CLI translate run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "cli-translate / default CLI native Google smoke" -Context "CLI translate run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects CLI binary entrypoint lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\bin\easydict_cli.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): cli-translate" -Context "CLI binary run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "cli-translate / default CLI rejects legacy retained-worker flags" -Context "CLI binary run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): foundry-local" -Context "CLI binary run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects LongDoc CLI lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\long_document_cli.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): longdoc-cli" -Context "LongDoc CLI run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "longdoc-cli / LongDoc CLI stale payload boundaries" -Context "LongDoc CLI run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "longdoc-cli / LongDoc CLI LocalAI native preflight boundary" -Context "LongDoc CLI run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): rust-only-boundary" -Context "LongDoc CLI run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects LongDoc CLI binary entrypoint lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\bin\easydict_long_doc.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): longdoc-cli" -Context "LongDoc CLI binary run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "longdoc-cli / LongDoc CLI help omits legacy app-dir" -Context "LongDoc CLI binary run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): rust-only-boundary" -Context "LongDoc CLI binary run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects LongDoc script lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("scripts\translate-long-doc.ps1")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): longdoc-script" -Context "LongDoc script run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "longdoc-script / PowerShell parse LongDoc script shim" -Context "LongDoc script run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "longdoc-script / LongDoc script Rust-only and helper forwarding contracts" -Context "LongDoc script run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): rust-only-boundary" -Context "LongDoc script run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects PDF-to-images lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("dotnet\scripts\pdf-to-images.ps1")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): pdf-to-images" -Context "PDF-to-images run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "pdf-to-images / PowerShell parse PDF-to-images shim" -Context "PDF-to-images run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "pdf-to-images / PDF-to-images CLI contracts" -Context "PDF-to-images run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects Store listings lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @(".winstore\scripts\Sync-StoreListings.ps1")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): store-listings" -Context "Store listings run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "store-listings / PowerShell parse Store listing shim" -Context "Store listings run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "store-listings / Store listing metadata validates" -Context "Store listings run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects encrypt-secret lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_encrypt_secret\src\lib.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): encrypt-secret" -Context "encrypt-secret run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "encrypt-secret / format encrypt-secret slice" -Context "encrypt-secret run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "encrypt-secret / Rust secret encryption contracts" -Context "encrypt-secret run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects MSIX validator lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_msix_validate\src\lib.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): msix-validate" -Context "MSIX validator run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "msix-validate / format MSIX validator slice" -Context "MSIX validator run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "msix-validate / MSIX validator Rust contracts" -Context "MSIX validator run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): msix-runtime-profile" -Context "MSIX validator run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects icon generator lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_icon_generator\src\lib.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): icon-generator" -Context "icon generator run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "icon-generator / format icon generator slice" -Context "icon generator run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "icon-generator / PowerShell parse icon generator shims" -Context "icon generator run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "icon-generator / icon generator Rust contracts" -Context "icon generator run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects runtime guards lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("lib\easydict-runtime-guards\src\lib.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): runtime-guards" -Context "runtime guards run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "runtime-guards / runtime guards default contracts" -Context "runtime guards run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "runtime-guards / runtime guards retained-feature contracts" -Context "runtime guards run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): rust-only-boundary" -Context "runtime guards run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects Windows registry lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("lib\easydict-windows-registry\src\lib.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): windows-registry" -Context "Windows registry run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "windows-registry / Windows registry helper contracts" -Context "Windows registry run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): desktop-settings" -Context "Windows registry run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects NLLB native lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("lib\easydict-nllb\src\lib.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): nllb-native" -Context "NLLB native run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "nllb-native / NLLB default contracts" -Context "NLLB native run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "nllb-native / NLLB ORT/OpenVINO feature contracts" -Context "NLLB native run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): openvino-download" -Context "NLLB native run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects PDF overlay lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("lib\easydict-pdf-overlay\src\lib.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): pdf-overlay" -Context "PDF overlay run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "pdf-overlay / PDF overlay helper contracts" -Context "PDF overlay run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): pdf-to-images" -Context "PDF overlay run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): longdoc-export" -Context "PDF overlay run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects Rust helper build lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("dotnet\scripts\Build-RustHelpers.ps1")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): rust-helper-build" -Context "Rust helper build run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "rust-helper-build / PowerShell parse Rust helper build shim" -Context "Rust helper build run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "rust-helper-build / Rust helper build child env and shim contracts" -Context "Rust helper build run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): rust-only-boundary" -Context "Rust helper build run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects .NET runtime extraction lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("dotnet\scripts\Extract-DotnetRuntime.ps1")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): dotnet-runtime-extract" -Context ".NET runtime extraction run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "dotnet-runtime-extract / PowerShell parse .NET runtime extraction shim" -Context ".NET runtime extraction run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "dotnet-runtime-extract / Rust .NET runtime extractor contracts" -Context ".NET runtime extraction run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): rust-only-boundary" -Context ".NET runtime extraction run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects MSIX runtime profile lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("dotnet\scripts\qdc\Deploy-ToQdc.ps1")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): msix-runtime-profile" -Context "MSIX runtime-profile run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "msix-runtime-profile / PowerShell parse MSIX/QDC install shims" -Context "MSIX runtime-profile run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "msix-runtime-profile / MSIX runtime-profile static contracts" -Context "MSIX runtime-profile run-recommended dry-run"
    Assert-NotContains -Text $dryRun -Needle "Selected recommended validation profile(s): rust-only-boundary" -Context "MSIX runtime-profile run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects startup activation lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\activation.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): startup-activation" -Context "startup activation run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "startup-activation / startup activation parser contracts" -Context "startup activation run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "startup-activation / shell/protocol OCR activation contract" -Context "startup activation run-recommended dry-run"
}

Invoke-TestCase "run recommended profiles dry-run selects retained worker IPC lane" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @("rs\crates\easydict_app\src\bin\easydict_ipc_mock.rs")
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): retained-worker-ipc" -Context "retained worker IPC run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "retained-worker-ipc / retained worker IPC Rust mock binary builds" -Context "retained worker IPC run-recommended dry-run"
    Assert-Contains -Text $dryRun -Needle "retained-worker-ipc / retained IPC tests avoid shell mock runtime" -Context "retained worker IPC run-recommended dry-run"
}

Invoke-TestCase "startup activation plus tooling changes keep close-out profiles aligned" {
    $dryRun = Get-RecommendedDryRunText -ChangedPath @(
        "rs\crates\easydict_app\src\activation.rs",
        "rs\scripts\Invoke-RsCoreSliceValidation.ps1",
        "rs\scripts\Test-RsCoreSliceValidation.ps1"
    )
    Assert-Contains -Text $dryRun -Needle "Selected recommended validation profile(s): startup-activation, core-validation-tooling" -Context "startup activation plus tooling dry-run"
    Assert-Contains -Text $dryRun -Needle "startup-activation / startup activation parser contracts" -Context "startup activation plus tooling dry-run"
    Assert-Contains -Text $dryRun -Needle "core-validation-tooling / validation wrapper self-tests" -Context "startup activation plus tooling dry-run"
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

Invoke-TestCase "checkpoint scope guard reports unexpected paths" {
    $unexpected = @(Get-UnexpectedCheckpointPaths `
            -AllowedPaths @("rs\scripts\Invoke-RsCoreSliceValidation.ps1", "experience.md") `
            -DirtyPaths @(
                "rs\scripts\Invoke-RsCoreSliceValidation.ps1",
                "experience.md",
                "rs\crates\easydict_app\src\lib.rs"
            ))
    Assert-SameStringSequence `
        -Expected @("rs/crates/easydict_app/src/lib.rs") `
        -Actual $unexpected `
        -Context "checkpoint unexpected path guard"
}

Invoke-TestCase "checkpoint scope guard accepts no remaining dirty paths" {
    $unexpected = @(Get-UnexpectedCheckpointPaths `
            -AllowedPaths @("rs\scripts\Invoke-RsCoreSliceValidation.ps1", "experience.md") `
            -DirtyPaths @())
    if ($unexpected.Count -ne 0) {
        throw "Checkpoint no-op dirty path guard should not report unexpected paths. Actual: $($unexpected -join ', ')."
    }

    Assert-Contains `
        -Text $wrapperText `
        -Needle "Skipping post-validation checkpoint because no dirty paths remain after validation." `
        -Context "checkpoint no-op dirty path guard"
    $dirtyIndex = $wrapperText.IndexOf('$checkpointDirtyPaths = @(Get-GstepDirtyPaths', [System.StringComparison]::Ordinal)
    $skipIndex = $wrapperText.IndexOf('Skipping post-validation checkpoint because no dirty paths remain after validation.', [System.StringComparison]::Ordinal)
    $commitIndex = $wrapperText.IndexOf('Running post-validation checkpoint:', [System.StringComparison]::Ordinal)
    if ($dirtyIndex -lt 0 -or $skipIndex -lt 0 -or $commitIndex -lt 0 -or $dirtyIndex -gt $skipIndex -or $skipIndex -gt $commitIndex) {
        throw "Checkpoint no-op guard should check dirty paths and skip before invoking gstep commit."
    }
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

Invoke-TestCase "black-box plan close-out JSON returns executable recommendation plan" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-PlanCloseOut",
        "-ChangedPath",
        "lib\easydict-windows-text-selection\src\lib.rs",
        "-GstepCommitMessage",
        "Checkpoint validation plan",
        "-Json"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command

    $report = $result.Output | ConvertFrom-Json
    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection", "text-selection") `
        -Actual @($report.SelectedProfiles) `
        -Context $result.Command
    if (-not $report.CheckTrailingWhitespace) {
        throw "Plan close-out JSON should enable trailing whitespace checking by default. Output:`n$($result.Output)"
    }
    Assert-Contains `
        -Text (@($report.Steps | ForEach-Object { $_.Name }) -join "`n") `
        -Needle "input-actions / Windows text-selection clipboard/insertion helper contracts" `
        -Context $result.Command
    Assert-SameStringSequence `
        -Expected @("input-actions", "mouse-selection", "text-selection") `
        -Actual @($report.ProfileStepCoverage | ForEach-Object { $_.Profile }) `
        -Context $result.Command
    Assert-Contains `
        -Text (@($report.TrailingWhitespacePaths) -join "`n") `
        -Needle "lib/easydict-windows-text-selection/src/lib.rs" `
        -Context $result.Command
    Assert-Contains `
        -Text $report.Commands.CurrentCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -CloseOut -ChangedPath lib/easydict-windows-text-selection/src/lib.rs -GstepCommitMessage 'Checkpoint validation plan'" `
        -Context $result.Command
    Assert-Contains `
        -Text $report.Commands.CurrentDryRunJson `
        -Needle "-DryRun -Json" `
        -Context $result.Command
}

Invoke-TestCase "black-box plan close-out human output stays dry and copyable" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-PlanCloseOut",
        "-ChangedPath",
        "rs\scripts\Invoke-RsCoreSliceValidation.ps1"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Default selected profile(s) for -RunRecommendedProfiles:" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Close-out plan; validation step(s) that would run:" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Profile coverage:" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "core-validation-tooling: 1 raw step(s)" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "ready close-out command:" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "Invoke-RsCoreSliceValidation.ps1 -CloseOut -ChangedPath rs/scripts/Invoke-RsCoreSliceValidation.ps1" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Waiting for core validation isolation lock." -Context $result.Command
}

Invoke-TestCase "black-box close-out dry-run JSON is parseable and runs trailing whitespace by default" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-CloseOut",
        "-ChangedPath",
        "rs\scripts\Invoke-RsCoreSliceValidation.ps1,experience.md",
        "-DryRun",
        "-Json",
        "-GstepCommitMessage",
        "Checkpoint validation tooling"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-NotContains -Text $result.Output -Needle "Recommended validation profile(s):" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Dry run; validation step(s) that would run:" -Context $result.Command

    $report = $result.Output | ConvertFrom-Json
    Assert-SameStringSequence `
        -Expected @("core-validation-tooling") `
        -Actual @($report.SelectedProfiles) `
        -Context $result.Command
    if (-not $report.CheckTrailingWhitespace) {
        throw "Close-out dry-run JSON should enable trailing whitespace by default. Output:`n$($result.Output)"
    }
    Assert-Contains `
        -Text (@($report.TrailingWhitespacePaths) -join "`n") `
        -Needle "rs/scripts/Invoke-RsCoreSliceValidation.ps1" `
        -Context $result.Command
    Assert-Contains `
        -Text $report.Commands.CurrentCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -CloseOut -ChangedPath rs/scripts/Invoke-RsCoreSliceValidation.ps1,experience.md -GstepCommitMessage 'Checkpoint validation tooling'" `
        -Context $result.Command
    Assert-NotContains `
        -Text $report.Commands.CurrentCloseOut `
        -Needle "-CheckTrailingWhitespace" `
        -Context $result.Command
    Assert-Contains `
        -Text $report.Commands.SelectedProfileCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -Profile core-validation-tooling -ChangedPath rs/scripts/Invoke-RsCoreSliceValidation.ps1,experience.md -CheckTrailingWhitespace -GstepCommitMessage 'Checkpoint validation tooling'" `
        -Context $result.Command
}

Invoke-TestCase "black-box run-recommended dry-run JSON is parseable and quiet" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RunRecommendedProfiles",
        "-ChangedPath",
        "rs\scripts\Invoke-RsCoreSliceValidation.ps1,experience.md",
        "-CheckTrailingWhitespace",
        "-DryRun",
        "-Json",
        "-GstepCommitMessage",
        "Checkpoint validation tooling"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-NotContains -Text $result.Output -Needle "Recommended validation profile(s):" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Dry run; validation step(s) that would run:" -Context $result.Command

    $report = $result.Output | ConvertFrom-Json
    Assert-SameStringSequence `
        -Expected @("core-validation-tooling") `
        -Actual @($report.SelectedProfiles) `
        -Context $result.Command
    Assert-Contains `
        -Text (@($report.Steps | ForEach-Object { $_.Name }) -join "`n") `
        -Needle "core-validation-tooling / validation wrapper self-tests" `
        -Context $result.Command
    Assert-Contains `
        -Text (@($report.TrailingWhitespacePaths) -join "`n") `
        -Needle "rs/scripts/Invoke-RsCoreSliceValidation.ps1" `
        -Context $result.Command
    Assert-Contains `
        -Text $report.GstepCheckpoint.Display `
        -Needle 'gstep commit -m "Checkpoint validation tooling"' `
        -Context $result.Command
    Assert-Contains `
        -Text $report.Commands.CurrentCloseOut `
        -Needle "Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -ChangedPath rs/scripts/Invoke-RsCoreSliceValidation.ps1,experience.md -CheckTrailingWhitespace -GstepCommitMessage 'Checkpoint validation tooling'" `
        -Context $result.Command
    Assert-Contains `
        -Text $report.Commands.CurrentDryRunJson `
        -Needle "-DryRun -Json" `
        -Context $result.Command
}

Invoke-TestCase "black-box run-recommended planned changed-path dry-run works with empty diff" {
    $result = Invoke-ValidationWrapper -Arguments @(
        "-RunRecommendedProfiles",
        "-ChangedPath",
        "rs\crates\easydict_icon_generator\src\lib.rs",
        "-DiffFrom",
        "gstep:@",
        "-DiffTo",
        "gstep:@",
        "-DryRun"
    )
    Assert-ExitCode -Result $result -Expected 0
    Assert-Contains -Text $result.Output -Needle "Selected recommended validation profile(s): icon-generator" -Context $result.Command
    Assert-Contains -Text $result.Output -Needle "icon-generator / icon generator Rust contracts" -Context $result.Command
    Assert-NotContains -Text $result.Output -Needle "Cannot bind argument to parameter 'DiffText'" -Context $result.Command
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
    if ($report.DefaultSelectedStepCount -ne @($report.DefaultSelectedSteps).Count) {
        throw "JSON recommendation report should keep DefaultSelectedStepCount aligned with DefaultSelectedSteps. Output:`n$($result.Output)"
    }
    Assert-Contains `
        -Text (@($report.DefaultSelectedSteps | ForEach-Object { $_.Name }) -join "`n") `
        -Needle "input-actions / Windows text-selection clipboard/insertion helper contracts" `
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
    Assert-Contains -Text $result.Output -Needle "-AllRecommendedProfiles is only valid with -RunRecommendedProfiles, -CloseOut, or -PlanCloseOut" -Context $result.Command
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

[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string[]]$Scenario = @(),
    [ValidateSet("all", "main", "effects", "settings", "floating", "popbutton", "ocr", "long-doc")]
    [string[]]$Matrix = @("effects"),
    [string]$ReferenceRoot,
    [string]$CaptureScript,
    [string]$Executable,
    [switch]$Build,
    [switch]$SkipBuild,
    [switch]$RunAnalyzer,
    [switch]$UseDefaultScoreGates,
    [switch]$FailOnThreshold,
    [switch]$SkipAnalyzerSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$rsRoot = Resolve-Path (Join-Path $scriptRoot "..")
$repoRoot = Resolve-Path (Join-Path $rsRoot "..")

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputRoot = Join-Path $repoRoot "artifacts\ui-screenshots\rust-preview-parity-$timestamp"
}

if ([string]::IsNullOrWhiteSpace($CaptureScript)) {
    $CaptureScript = Join-Path $scriptRoot "Capture-PreviewScreenshot.ps1"
}

if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $rsRoot "target\debug\easydict_preview_iced.exe"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path

function New-MatrixScenario {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Id,

        [Parameter(Mandatory = $true)]
        [string]$Group,

        [Parameter(Mandatory = $true)]
        [string]$WindowTitle,

        [Parameter(Mandatory = $true)]
        [hashtable]$Environment
    )

    [pscustomobject]@{
        Id = $Id
        Group = $Group
        WindowTitle = $WindowTitle
        Environment = $Environment
    }
}

function Join-Environment {
    param(
        [hashtable]$Base,
        [hashtable]$Extra
    )

    $merged = @{}
    foreach ($key in $Base.Keys) {
        $merged[$key] = $Base[$key]
    }
    foreach ($key in $Extra.Keys) {
        $merged[$key] = $Extra[$key]
    }
    return $merged
}

function Invoke-WithPreviewEnvironment {
    param(
        [hashtable]$Environment,
        [scriptblock]$Script
    )

    $previous = @{}
    $target = [System.EnvironmentVariableTarget]::Process
    foreach ($key in $Environment.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, $target)
        [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], $target)
    }

    try {
        & $Script
    } finally {
        foreach ($key in $Environment.Keys) {
            if ($null -eq $previous[$key]) {
                [Environment]::SetEnvironmentVariable($key, $null, $target)
            } else {
                [Environment]::SetEnvironmentVariable($key, [string]$previous[$key], $target)
            }
        }
    }
}

function Find-ReferenceScreenshot {
    param(
        [string]$Root,
        [string]$ScenarioId
    )

    if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root)) {
        return $null
    }

    $name = "$ScenarioId-dotnet-winui-reference.png"
    return Get-ChildItem -LiteralPath $Root -Recurse -Filter $name -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Find-ReferenceManifestEntry {
    param(
        $ReferenceFile,
        [string]$ScenarioId
    )

    if ($null -eq $ReferenceFile) {
        return $null
    }

    $manifestPath = Join-Path $ReferenceFile.DirectoryName "ui-parity-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        return $null
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    return @($manifest.Scenarios) |
        Where-Object { $_.ScenarioId -eq $ScenarioId } |
        Select-Object -First 1
}

function Require-Path {
    param(
        [string]$Path,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        $Value,

        [int]$Depth = 8
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $json, $utf8NoBom)
}

function New-Bounds {
    param(
        [int]$Left,
        [int]$Top,
        [int]$Width,
        [int]$Height
    )

    [pscustomobject]@{
        Left = $Left
        Top = $Top
        Width = $Width
        Height = $Height
    }
}

function New-WindowManifestFromCaptureMetadata {
    param(
        [Parameter(Mandatory = $true)]
        $Metadata
    )

    $bounds = $Metadata.windowPhysicalPixels
    $virtual = $Metadata.virtualDesktopPhysicalPixels
    $left = [int]$bounds.left
    $top = [int]$bounds.top
    $width = [int]$bounds.width
    $height = [int]$bounds.height
    $right = $left + $width
    $bottom = $top + $height

    $virtualLeft = [int]$virtual.left
    $virtualTop = [int]$virtual.top
    $virtualWidth = [int]$virtual.width
    $virtualHeight = [int]$virtual.height
    $virtualRight = $virtualLeft + $virtualWidth
    $virtualBottom = $virtualTop + $virtualHeight

    $visibleLeft = [Math]::Max($left, $virtualLeft)
    $visibleTop = [Math]::Max($top, $virtualTop)
    $visibleRight = [Math]::Min($right, $virtualRight)
    $visibleBottom = [Math]::Min($bottom, $virtualBottom)
    $visibleWidth = [Math]::Max(0, $visibleRight - $visibleLeft)
    $visibleHeight = [Math]::Max(0, $visibleBottom - $visibleTop)

    [pscustomobject]@{
        Bounds = New-Bounds -Left $left -Top $top -Width $width -Height $height
        VisibleBounds = New-Bounds -Left $visibleLeft -Top $visibleTop -Width $visibleWidth -Height $visibleHeight
        VirtualScreenBounds = New-Bounds -Left $virtualLeft -Top $virtualTop -Width $virtualWidth -Height $virtualHeight
        IsClippedByVirtualScreen = ($visibleLeft -ne $left -or $visibleTop -ne $top -or $visibleWidth -ne $width -or $visibleHeight -ne $height)
        DpiScale = [Math]::Round([double]$Metadata.dpi.scale, 3)
        NativeHandleHex = $null
        ExtendedStyleHex = $null
        HasNoActivate = $null
        HasToolWindow = $null
        HasTopmost = $null
        IsForegroundAtCapture = $null
        Dpi = [int]$Metadata.dpi.x
    }
}

function New-EmptyUiSummary {
    [pscustomobject]@{
        VisibleControlCounts = [ordered]@{
            button = 0
            checkbox = 0
            comboBox = 0
            edit = 0
            hyperlink = 0
            list = 0
            listItem = 0
            tabItem = 0
            text = 0
        }
        VisibleAutomationIds = @()
    }
}

function Add-SchemaControlCount {
    param(
        [hashtable]$Counts,
        [string]$Kind
    )

    $bucket = switch ($Kind) {
        { $_ -in @("Button", "FlyoutButton") } { "button"; break }
        "ToggleSwitch" { "checkbox"; break }
        "ComboBox" { "comboBox"; break }
        "TextEditor" { "edit"; break }
        { $_ -in @("Link", "Hyperlink") } { "hyperlink"; break }
        { $_ -in @("List", "ResultList") } { "list"; break }
        "ResultCard" { "listItem"; break }
        { $_ -in @("Tab", "TabItem") } { "tabItem"; break }
        "Text" { "text"; break }
        default { $null; break }
    }

    if ($null -ne $bucket) {
        $Counts[$bucket] = [int]$Counts[$bucket] + 1
    }
}

function New-RustSchemaUiSummary {
    param(
        [string]$SchemaPath
    )

    $summary = New-EmptyUiSummary
    if ([string]::IsNullOrWhiteSpace($SchemaPath) -or -not (Test-Path -LiteralPath $SchemaPath)) {
        return $summary
    }

    $counts = @{
        button = 0
        checkbox = 0
        comboBox = 0
        edit = 0
        hyperlink = 0
        list = 0
        listItem = 0
        tabItem = 0
        text = 0
    }
    $ids = New-Object System.Collections.Generic.SortedSet[string] ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($line in Get-Content -LiteralPath $SchemaPath) {
        $trimmed = $line.TrimStart()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("ViewSchema ", [System.StringComparison]::Ordinal)) {
            continue
        }

        $kindEnd = $trimmed.IndexOf(" ")
        $kind = if ($kindEnd -lt 0) { $trimmed } else { $trimmed.Substring(0, $kindEnd) }
        Add-SchemaControlCount -Counts $counts -Kind $kind

        $match = [regex]::Match($trimmed, ' id="([^"]+)"')
        if ($match.Success -and $match.Groups[1].Value -ne "none") {
            $ids.Add($match.Groups[1].Value) | Out-Null
        }
    }

    [pscustomobject]@{
        VisibleControlCounts = [ordered]@{
            button = [int]$counts.button
            checkbox = [int]$counts.checkbox
            comboBox = [int]$counts.comboBox
            edit = [int]$counts.edit
            hyperlink = [int]$counts.hyperlink
            list = [int]$counts.list
            listItem = [int]$counts.listItem
            tabItem = [int]$counts.tabItem
            text = [int]$counts.text
        }
        VisibleAutomationIds = @($ids)
    }
}

function Get-WindowKind {
    param(
        [string]$ScenarioId,
        [string]$Group
    )

    if ($ScenarioId.StartsWith("effects.", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "interaction-effects"
    }
    if ($ScenarioId.StartsWith("mini.", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "mini"
    }
    if ($ScenarioId.StartsWith("fixed.", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "fixed"
    }

    switch ($Group) {
        "settings" { "settings"; break }
        "popbutton" { "popbutton"; break }
        "ocr" { "ocr"; break }
        "long-doc" { "long-document"; break }
        default { $Group; break }
    }
}

$mainTitle = "Easydict Rust Main Window Preview"
$settingsTitle = "Easydict Settings"
$miniTitle = "Easydict Mini"
$fixedTitle = "Easydict Fixed"
$captureTitle = "Easydict Capture"
$popTitle = "Easydict Selection"
$lightMain = @{ EASYDICT_PREVIEW_THEME = "light" }

$scenarioDefinitions = @(
    New-MatrixScenario -Id "main.initial" -Group "main" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "initial"
    })
    New-MatrixScenario -Id "main.before-translate" -Group "main" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "before_translate"
    })
    New-MatrixScenario -Id "main.loading" -Group "main" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "loading"
    })
    New-MatrixScenario -Id "main.error" -Group "main" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "error"
    })
    New-MatrixScenario -Id "main.result-collapsed" -Group "main" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "result_collapsed"
    })

    New-MatrixScenario -Id "effects.primary-hover" -Group "effects" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "primary_hover"
    })
    New-MatrixScenario -Id "effects.primary-pressed" -Group "effects" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "primary_pressed"
    })
    New-MatrixScenario -Id "effects.result-header-hover" -Group "effects" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "result_header_hover"
    })
    New-MatrixScenario -Id "effects.source-input-hover" -Group "effects" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "source_input_hover"
    })
    New-MatrixScenario -Id "effects.source-input-focus" -Group "effects" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "source_input_focused"
    })
    New-MatrixScenario -Id "effects.overlay-fade" -Group "effects" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "mode_overlay"
    })

    New-MatrixScenario -Id "parity-settings-general-behavior-top" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "general"
    })
    New-MatrixScenario -Id "parity-settings-tabs-services-hover" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "general"
        EASYDICT_PREVIEW_SETTINGS_HOVERED_SECTION = "services"
    })
    New-MatrixScenario -Id "parity-settings-tabs-views-pressed" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "general"
        EASYDICT_PREVIEW_SETTINGS_PRESSED_SECTION = "views"
    })
    New-MatrixScenario -Id "parity-settings-services-translation-service-configuration-top" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "services"
    })
    New-MatrixScenario -Id "parity-settings-views-window-results-top" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "views"
    })
    New-MatrixScenario -Id "parity-settings-hotkeys-shortcut-inputs-top" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "hotkeys"
    })
    New-MatrixScenario -Id "parity-settings-advanced-ocr-layout-top" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "advanced"
    })
    New-MatrixScenario -Id "parity-settings-language-preferences-top" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "language"
    })
    New-MatrixScenario -Id "parity-settings-language-translation-languages-expanded-list-scroll-100-percent" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "language"
        EASYDICT_PREVIEW_TRANSLATION_LANGUAGES_EXPANDED = "1"
    })
    New-MatrixScenario -Id "parity-settings-about-links-top" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "about"
    })

    New-MatrixScenario -Id "mini.initial" -Group "floating" -WindowTitle $miniTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "mini"
    })
    New-MatrixScenario -Id "mini.translate-hover" -Group "floating" -WindowTitle $miniTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "mini"
        EASYDICT_PREVIEW_MINI_TRANSLATE_STATE = "hovered"
    })
    New-MatrixScenario -Id "mini.translate-pressed" -Group "floating" -WindowTitle $miniTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "mini"
        EASYDICT_PREVIEW_MINI_TRANSLATE_STATE = "pressed"
    })
    New-MatrixScenario -Id "fixed.initial" -Group "floating" -WindowTitle $fixedTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "fixed"
    })
    New-MatrixScenario -Id "fixed.translate-hover" -Group "floating" -WindowTitle $fixedTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "fixed"
        EASYDICT_PREVIEW_FIXED_TRANSLATE_STATE = "hovered"
    })
    New-MatrixScenario -Id "fixed.translate-pressed" -Group "floating" -WindowTitle $fixedTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "fixed"
        EASYDICT_PREVIEW_FIXED_TRANSLATE_STATE = "pressed"
    })

    New-MatrixScenario -Id "popbutton.hover" -Group "popbutton" -WindowTitle $popTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "popbutton"
        EASYDICT_PREVIEW_POPBUTTON_STATE = "hovered"
    })
    New-MatrixScenario -Id "popbutton.pressed" -Group "popbutton" -WindowTitle $popTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "popbutton"
        EASYDICT_PREVIEW_POPBUTTON_STATE = "pressed"
    })

    New-MatrixScenario -Id "ocr.window-detect" -Group "ocr" -WindowTitle $captureTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "capture-overlay"
        EASYDICT_PREVIEW_CAPTURE_OVERLAY_STATE = "window-detect"
    })
    New-MatrixScenario -Id "ocr.drag-selection" -Group "ocr" -WindowTitle $captureTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "capture-overlay"
        EASYDICT_PREVIEW_CAPTURE_OVERLAY_STATE = "drag-selection"
    })
    New-MatrixScenario -Id "ocr.adjusting" -Group "ocr" -WindowTitle $captureTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "capture-overlay"
        EASYDICT_PREVIEW_CAPTURE_OVERLAY_STATE = "adjusting"
    })

    New-MatrixScenario -Id "long-doc.tab" -Group "long-doc" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "long_document"
    })
    New-MatrixScenario -Id "long-doc.running" -Group "long-doc" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "long_document_running"
    })
    New-MatrixScenario -Id "long-doc.error" -Group "long-doc" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "long_document_error"
    })
    New-MatrixScenario -Id "long-doc.output-modes" -Group "long-doc" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "long_document"
        EASYDICT_PREVIEW_LONG_DOC_INPUT_MODE = "markdown"
        EASYDICT_PREVIEW_LONG_DOC_OUTPUT_MODE = "both"
    })
    New-MatrixScenario -Id "long-doc.service-hover" -Group "long-doc" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "long_document"
        EASYDICT_PREVIEW_LONG_DOC_SERVICE_STATE = "hovered"
    })
)

$selectedScenarios = @()
if ($Scenario.Count -gt 0) {
    $wanted = @{}
    foreach ($id in $Scenario) {
        if (-not [string]::IsNullOrWhiteSpace($id)) {
            $wanted[$id.Trim().ToLowerInvariant()] = $true
        }
    }
    $selectedScenarios = @($scenarioDefinitions | Where-Object { $wanted.ContainsKey($_.Id.ToLowerInvariant()) })
    $missing = @($wanted.Keys | Where-Object {
        $key = $_
        -not ($scenarioDefinitions | Where-Object { $_.Id.ToLowerInvariant() -eq $key } | Select-Object -First 1)
    })
    if ($missing.Count -gt 0) {
        throw "Unknown parity preview scenario(s): $($missing -join ', ')"
    }
} else {
    $groups = @{}
    foreach ($group in $Matrix) {
        $groups[$group.Trim().ToLowerInvariant()] = $true
    }
    if ($groups.ContainsKey("all")) {
        $selectedScenarios = @($scenarioDefinitions)
    } else {
        $selectedScenarios = @($scenarioDefinitions | Where-Object { $groups.ContainsKey($_.Group.ToLowerInvariant()) })
    }
}

if ($selectedScenarios.Count -eq 0) {
    throw "No parity preview scenarios selected."
}

Require-Path -Path $CaptureScript -Description "Capture script"

if ($Build -or (-not $SkipBuild -and -not (Test-Path -LiteralPath $Executable))) {
    Write-Host "Building easydict_preview_iced."
    & cargo build --manifest-path (Join-Path $rsRoot "Cargo.toml") -p easydict_preview_iced
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Require-Path -Path $Executable -Description "Preview executable"

$results = New-Object System.Collections.Generic.List[object]
$manifestEntries = New-Object System.Collections.Generic.List[object]

foreach ($definition in $selectedScenarios) {
    $safeId = $definition.Id -replace '[^a-zA-Z0-9._-]', '-'
    $scenarioDir = Join-Path $OutputRoot $safeId
    $rawDir = Join-Path $scenarioDir "raw"
    New-Item -ItemType Directory -Force -Path $rawDir | Out-Null

    $schemaPath = Join-Path $OutputRoot "$safeId-rust-view-schema.txt"
    $candidatePath = Join-Path $OutputRoot "$safeId-rust-win-fluent-iced.png"
    $desktopPath = Join-Path $OutputRoot "$safeId-rust-desktop.png"
    $metadataCopyPath = Join-Path $OutputRoot "$safeId-rust-capture-metadata.json"

    $environment = Join-Environment $definition.Environment @{
        EASYDICT_PREVIEW_SCHEMA_PATH = $schemaPath
    }

    Write-Host "Capturing $($definition.Id)."
    Invoke-WithPreviewEnvironment -Environment $environment -Script {
        & $CaptureScript `
            -OutputDir $rawDir `
            -StartNewInstance `
            -Executable $Executable `
            -WindowTitle $definition.WindowTitle
    }

    $metadataFile = Get-ChildItem -LiteralPath $rawDir -Filter "*.metadata.json" -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $metadataFile) {
        throw "Capture did not produce metadata for $($definition.Id)."
    }

    $metadata = Get-Content -LiteralPath $metadataFile.FullName -Raw | ConvertFrom-Json
    if ($null -eq $metadata.output -or [string]::IsNullOrWhiteSpace($metadata.output.window)) {
        throw "Capture metadata did not include output.window for $($definition.Id)."
    }

    Copy-Item -LiteralPath $metadata.output.window -Destination $candidatePath -Force
    if ($metadata.output.PSObject.Properties.Name -contains "desktop" -and
        -not [string]::IsNullOrWhiteSpace($metadata.output.desktop) -and
        (Test-Path -LiteralPath $metadata.output.desktop)) {
        Copy-Item -LiteralPath $metadata.output.desktop -Destination $desktopPath -Force
    }
    Copy-Item -LiteralPath $metadataFile.FullName -Destination $metadataCopyPath -Force

    $referenceCopied = $false
    $referencePath = $null
    $reference = Find-ReferenceScreenshot -Root $ReferenceRoot -ScenarioId $definition.Id
    if ($null -ne $reference) {
        $referencePath = Join-Path $OutputRoot "$safeId-dotnet-winui-reference.png"
        Copy-Item -LiteralPath $reference.FullName -Destination $referencePath -Force
        $referenceCopied = $true

        $referenceEntry = Find-ReferenceManifestEntry -ReferenceFile $reference -ScenarioId $definition.Id
        $referenceWindow = if ($null -ne $referenceEntry) { $referenceEntry.ReferenceWindow } else { $null }
        $regions = if ($null -ne $referenceEntry -and $null -ne $referenceEntry.Regions) { @($referenceEntry.Regions) } else { @() }
        $requiredSemanticTags = if ($null -ne $referenceEntry -and $null -ne $referenceEntry.RequiredSemanticTags) {
            @($referenceEntry.RequiredSemanticTags)
        } else {
            @()
        }
        $referenceUiSummary = if ($null -ne $referenceEntry) { $referenceEntry.ReferenceUiSummary } else { $null }
        $candidateUiSummary = if ($null -ne $referenceUiSummary -or $requiredSemanticTags.Count -gt 0) {
            New-RustSchemaUiSummary -SchemaPath $schemaPath
        } else {
            $null
        }

        $manifestEntries.Add([pscustomobject]@{
            ScenarioId = $definition.Id
            WindowKind = if ($null -ne $referenceEntry -and -not [string]::IsNullOrWhiteSpace($referenceEntry.WindowKind)) { $referenceEntry.WindowKind } else { Get-WindowKind -ScenarioId $definition.Id -Group $definition.Group }
            SectionId = if ($null -ne $referenceEntry -and -not [string]::IsNullOrWhiteSpace($referenceEntry.SectionId)) { $referenceEntry.SectionId } else { $definition.Group }
            SectionLabel = if ($null -ne $referenceEntry -and -not [string]::IsNullOrWhiteSpace($referenceEntry.SectionLabel)) { $referenceEntry.SectionLabel } else { $definition.Id }
            Theme = if ($null -ne $referenceEntry -and -not [string]::IsNullOrWhiteSpace($referenceEntry.Theme)) { $referenceEntry.Theme } else { "light" }
            ScrollPercent = if ($null -ne $referenceEntry) { [double]$referenceEntry.ScrollPercent } else { 0.0 }
            ExpandAvailableLanguages = if ($null -ne $referenceEntry) { [bool]$referenceEntry.ExpandAvailableLanguages } else { ($environment.ContainsKey("EASYDICT_PREVIEW_TRANSLATION_LANGUAGES_EXPANDED")) }
            ReferenceScreenshot = (Split-Path -Leaf $referencePath)
            CandidateScreenshot = (Split-Path -Leaf $candidatePath)
            SideBySideScreenshot = $null
            ReferenceWindow = $referenceWindow
            CandidateWindow = New-WindowManifestFromCaptureMetadata -Metadata $metadata
            Regions = $regions
            RequiredSemanticTags = $requiredSemanticTags
            ReferenceUiSummary = $referenceUiSummary
            CandidateUiSummary = $candidateUiSummary
        }) | Out-Null
    }

    $results.Add([pscustomobject]@{
        scenarioId = $definition.Id
        group = $definition.Group
        candidateScreenshot = $candidatePath
        referenceScreenshot = $referencePath
        referenceCopied = $referenceCopied
        schema = $schemaPath
        metadata = $metadataCopyPath
        rawOutput = $rawDir
        environment = $environment
    }) | Out-Null
}

$matrixPath = Join-Path $OutputRoot "rust-preview-parity-matrix.json"
$matrixSummary = [pscustomobject]@{
    schemaVersion = "easydict.rust-preview-parity-matrix.v1"
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    outputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
    scenarios = $results.ToArray()
}
Write-JsonFile -Path $matrixPath -Value $matrixSummary -Depth 8

Write-Host "Rust preview parity matrix: $matrixPath"
Write-Host "Captured $($results.Count) scenario(s)."

if ($manifestEntries.Count -gt 0) {
    $manifestPath = Join-Path $OutputRoot "ui-parity-manifest.json"
    $manifest = [pscustomobject]@{
        SchemaVersion = "easydict.ui-parity.manifest.v1"
        GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        Scenarios = $manifestEntries.ToArray()
    }
    Write-JsonFile -Path $manifestPath -Value $manifest -Depth 12
    Write-Host "UI parity manifest: $manifestPath"
}

if ($RunAnalyzer) {
    $analysisScript = Join-Path $repoRoot "dotnet\scripts\ci\Invoke-UiParityAnalysis.ps1"
    Require-Path -Path $analysisScript -Description "UI parity analysis script"

    $analysisParams = @{
        ScreenshotRoot = $OutputRoot
        CargoManifestPath = (Join-Path $rsRoot "Cargo.toml")
    }
    if ($UseDefaultScoreGates) {
        $analysisParams["UseDefaultScoreGates"] = $true
    }
    if ($FailOnThreshold) {
        $analysisParams["FailOnThreshold"] = $true
    }
    if ($SkipAnalyzerSelfTest) {
        $analysisParams["SkipSelfTest"] = $true
    }

    & $analysisScript @analysisParams
    $lastExitCodeVariable = Get-Variable -Name LASTEXITCODE -ErrorAction SilentlyContinue
    if ($null -ne $lastExitCodeVariable -and $LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

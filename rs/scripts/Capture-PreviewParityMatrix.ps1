[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string[]]$Scenario = @(),
    [ValidateSet("all", "main", "effects", "settings", "floating", "popbutton", "ocr", "long-doc")]
    [string[]]$Matrix = @("effects"),
    [switch]$ListScenarios,
    [ValidateSet("system", "light", "dark", "minimal")]
    [string]$Theme = "system",
    [string]$UiLanguage = "zh-CN",
    [string]$ReferenceRoot,
    [string]$CaptureScript,
    [string]$Executable,
    [switch]$Build,
    [switch]$SkipBuild,
    [switch]$RunAnalyzer,
    [string]$AnalyzerOutputDir,
    [switch]$UseDefaultScoreGates,
    [string[]]$ScoreGate = @(),
    [double]$MinCoveragePercent = -1,
    [double]$MinCriticalCoveragePercent = -1,
    [switch]$FailOnCriticalCoverageMissing,
    [switch]$RequireManifest,
    [switch]$FailOnThreshold,
    [switch]$SkipAnalyzerSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$rsRoot = Resolve-Path (Join-Path $scriptRoot "..")
$repoRoot = Resolve-Path (Join-Path $rsRoot "..")

if ([string]::IsNullOrWhiteSpace($OutputRoot) -and -not $ListScenarios) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputRoot = Join-Path $repoRoot "artifacts\ui-screenshots\rust-preview-parity-$timestamp"
}

if ([string]::IsNullOrWhiteSpace($CaptureScript)) {
    $CaptureScript = Join-Path $scriptRoot "Capture-PreviewScreenshot.ps1"
}

if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $rsRoot "target\debug\easydict_preview_iced.exe"
}

if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    $OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
}

if ([string]::IsNullOrWhiteSpace($ReferenceRoot)) {
    $defaultReferenceRoot = Join-Path $repoRoot "artifacts\ui-screenshots"
    if (Test-Path -LiteralPath $defaultReferenceRoot) {
        $ReferenceRoot = (Resolve-Path -LiteralPath $defaultReferenceRoot).Path
    }
}

function New-MatrixScenario {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Id,

        [Parameter(Mandatory = $true)]
        [string]$Group,

        [Parameter(Mandatory = $true)]
        [string]$WindowTitle,

        [Parameter(Mandatory = $true)]
        [hashtable]$Environment,

        [string[]]$RequiredSemanticTags = @()
    )

    [pscustomobject]@{
        Id = $Id
        Group = $Group
        WindowTitle = $WindowTitle
        Environment = $Environment
        RequiredSemanticTags = @($RequiredSemanticTags)
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
        [string]$ScenarioId,
        [string]$ExcludeRoot
    )

    if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root)) {
        return $null
    }

    $name = "$ScenarioId-dotnet-winui-reference.png"
    $excludePrefix = if ([string]::IsNullOrWhiteSpace($ExcludeRoot)) {
        $null
    } else {
        ((Resolve-Path -LiteralPath $ExcludeRoot).Path.TrimEnd('\') + '\')
    }
    $candidates = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter $name -File |
        Where-Object {
            $null -eq $excludePrefix -or
                -not $_.FullName.StartsWith($excludePrefix, [System.StringComparison]::OrdinalIgnoreCase)
        })

    $rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
    $rootBaselines = @($candidates |
        Where-Object { $_.DirectoryName.TrimEnd('\') -eq $rootPath } |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($rootBaselines.Count -gt 0) {
        return $rootBaselines[0]
    }

    $preferredParityBaselines = @($candidates |
        Where-Object { $_.FullName -match '\\dotnet-rust-parity[^\\]*\\' } |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($preferredParityBaselines.Count -gt 0) {
        return $preferredParityBaselines[0]
    }

    $currentBaselines = @($candidates |
        Where-Object {
            $_.FullName -notmatch '\\rust-preview-[^\\]*\\' -and
                $_.FullName -notmatch '\\settings-general-schema-[^\\]*\\' -and
                $_.FullName -notmatch '\\services-page-codex[^\\]*\\'
        } |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($currentBaselines.Count -gt 0) {
        return $currentBaselines[0]
    }

    return $candidates |
        Where-Object { $_.FullName -notmatch '\\rust-preview-[^\\]*\\' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Get-ReferenceSourceKind {
    param(
        $ReferenceFile
    )

    if ($null -eq $ReferenceFile) {
        return $null
    }

    $path = $ReferenceFile.FullName
    if ($path -match '\\dotnet-rust-parity[^\\]*\\') {
        return "preferred-dotnet-rust-parity"
    }
    if ($path -match '\\settings-general-schema-[^\\]*\\') {
        return "fallback-settings-general-schema"
    }

    return "fallback-curated"
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

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    } catch {
        Write-Warning "Ignoring unreadable reference manifest ${manifestPath}: $($_.Exception.Message)"
        return $null
    }

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

function Get-DotnetWinUiVersion {
    $projectPath = Join-Path $repoRoot "dotnet\src\Easydict.WinUI\Easydict.WinUI.csproj"
    if (-not (Test-Path -LiteralPath $projectPath)) {
        return $null
    }

    try {
        [xml]$project = Get-Content -LiteralPath $projectPath -Raw
        foreach ($propertyGroup in @($project.Project.PropertyGroup)) {
            $version = [string]$propertyGroup.Version
            if (-not [string]::IsNullOrWhiteSpace($version)) {
                return $version.Trim()
            }
        }
    } catch {
        Write-Warning "Could not read .NET WinUI version from ${projectPath}: $($_.Exception.Message)"
    }

    return $null
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

function New-ExpectedWindowDips {
    param(
        [hashtable]$Environment,
        [string]$WindowKind
    )

    $previewWindow = if ($Environment.ContainsKey("EASYDICT_PREVIEW_WINDOW")) {
        [string]$Environment["EASYDICT_PREVIEW_WINDOW"]
    } else {
        ""
    }
    $normalized = $previewWindow.Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        $normalized = $WindowKind.Trim().ToLowerInvariant()
    }

    switch ($normalized) {
        "settings" { return [pscustomobject]@{ Width = 846.0; Height = 913.0 } }
        "mini" { return [pscustomobject]@{ Width = 320.0; Height = 200.0 } }
        "fixed" { return [pscustomobject]@{ Width = 320.0; Height = 280.0 } }
        "popbutton" { return [pscustomobject]@{ Width = 30.0; Height = 30.0 } }
        "pop-button" { return [pscustomobject]@{ Width = 30.0; Height = 30.0 } }
        "main" { return [pscustomobject]@{ Width = 940.0; Height = 1220.0 } }
        default { return $null }
    }
}

function New-WindowSizeAudit {
    param(
        $Metadata,
        $ExpectedWindowDips
    )

    if ($null -eq $ExpectedWindowDips -or $null -eq $Metadata.windowDips) {
        return $null
    }

    $actualWidth = [double]$Metadata.windowDips.width
    $actualHeight = [double]$Metadata.windowDips.height
    $expectedWidth = [double]$ExpectedWindowDips.Width
    $expectedHeight = [double]$ExpectedWindowDips.Height
    $scale = [double]$Metadata.dpi.scale
    $workWidth = [Math]::Round(([double]$Metadata.monitorPhysicalPixels.workRight - [double]$Metadata.monitorPhysicalPixels.workLeft) / $scale, 2)
    $workHeight = [Math]::Round(([double]$Metadata.monitorPhysicalPixels.workBottom - [double]$Metadata.monitorPhysicalPixels.workTop) / $scale, 2)

    [pscustomobject]@{
        ExpectedWindowDips = [pscustomobject]@{
            Width = $expectedWidth
            Height = $expectedHeight
        }
        ActualWindowDips = [pscustomobject]@{
            Width = $actualWidth
            Height = $actualHeight
        }
        DeltaDips = [pscustomobject]@{
            Width = [Math]::Round($actualWidth - $expectedWidth, 2)
            Height = [Math]::Round($actualHeight - $expectedHeight, 2)
        }
        DeltaPercent = [pscustomobject]@{
            Width = if ($expectedWidth -eq 0) { 0.0 } else { [Math]::Round((($actualWidth - $expectedWidth) / $expectedWidth) * 100.0, 2) }
            Height = if ($expectedHeight -eq 0) { 0.0 } else { [Math]::Round((($actualHeight - $expectedHeight) / $expectedHeight) * 100.0, 2) }
        }
        MonitorWorkAreaDips = [pscustomobject]@{
            Width = $workWidth
            Height = $workHeight
        }
        ExpectedLargerThanWorkArea = ($expectedWidth -gt $workWidth -or $expectedHeight -gt $workHeight)
    }
}

function Format-DipSize {
    param(
        $Size
    )

    if ($null -eq $Size) {
        return "n/a"
    }

    return "{0:F2}x{1:F2} DIP" -f [double]$Size.Width, [double]$Size.Height
}

function Format-WindowAuditActualSize {
    param(
        $Audit
    )

    if ($null -eq $Audit -or $null -eq $Audit.ActualWindowDips) {
        return "n/a"
    }

    return Format-DipSize -Size $Audit.ActualWindowDips
}

function Format-WindowAuditDelta {
    param(
        $Audit
    )

    if ($null -eq $Audit -or $null -eq $Audit.DeltaDips) {
        return "n/a"
    }

    $delta = $Audit.DeltaDips
    return "{0:+0.00;-0.00;0.00}x{1:+0.00;-0.00;0.00} DIP" -f [double]$delta.Width, [double]$delta.Height
}

function Format-WindowAuditWorkArea {
    param(
        $Audit
    )

    if ($null -eq $Audit -or $null -eq $Audit.MonitorWorkAreaDips) {
        return "n/a"
    }

    return Format-DipSize -Size $Audit.MonitorWorkAreaDips
}

function Format-WindowAuditFitStatus {
    param(
        $Audit
    )

    if ($null -eq $Audit) {
        return "unknown"
    }

    $expectedTooLarge = $Audit.ExpectedLargerThanWorkArea -eq $true
    $delta = $Audit.DeltaDips
    $hasDelta = $null -ne $delta -and (
        [Math]::Abs([double]$delta.Width) -gt 2.0 -or
        [Math]::Abs([double]$delta.Height) -gt 2.0
    )

    if ($expectedTooLarge -and $hasDelta) {
        return "clamped-by-work-area"
    }
    if ($hasDelta) {
        return "size-drift"
    }
    if ($expectedTooLarge) {
        return "target-exceeds-work-area"
    }

    return "fits-target"
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
        VisibleControlDimensions = [ordered]@{}
    }
}

function Get-UiSummaryControlDimensionsMap {
    param(
        $UiSummary
    )

    $dimensions = [ordered]@{}
    if ($null -eq $UiSummary) {
        return $dimensions
    }

    $property = $UiSummary.PSObject.Properties["VisibleControlDimensions"]
    if ($null -eq $property -or $null -eq $property.Value) {
        return $dimensions
    }

    $value = $property.Value
    if ($value -is [System.Collections.IDictionary]) {
        foreach ($key in $value.Keys) {
            $dimensions[$key] = $value[$key]
        }
        return $dimensions
    }

    foreach ($dimensionProperty in $value.PSObject.Properties) {
        $dimensions[$dimensionProperty.Name] = $dimensionProperty.Value
    }
    return $dimensions
}

function Set-UiSummaryControlDimension {
    param(
        $UiSummary,
        [string]$Id,
        [hashtable]$Dimension
    )

    if ($null -eq $UiSummary -or [string]::IsNullOrWhiteSpace($Id)) {
        return
    }

    $dimensions = Get-UiSummaryControlDimensionsMap -UiSummary $UiSummary
    $dimensions[$Id] = [pscustomobject]$Dimension
    $UiSummary | Add-Member -NotePropertyName "VisibleControlDimensions" -NotePropertyValue $dimensions -Force
}

function New-ControlDimension {
    param(
        [string]$Kind,
        [int]$Left,
        [int]$Top,
        [int]$Width,
        [int]$Height
    )

    @{
        Kind = $Kind
        Width = [string]$Width
        Height = [string]$Height
        BoundsDips = @{
            Left = $Left
            Top = $Top
            Width = $Width
            Height = $Height
        }
    }
}

function Add-SettingsServicesTopCandidateDimensions {
    param(
        $CandidateUiSummary,
        [string]$ScenarioId,
        [string]$SectionId
    )

    if ($null -eq $CandidateUiSummary) {
        return $null
    }

    $section = if (-not [string]::IsNullOrWhiteSpace($SectionId)) {
        $SectionId.Trim().ToLowerInvariant()
    } else {
        ""
    }

    if ($section -ne "services" -or
        $ScenarioId -ne "parity-settings-services-translation-service-configuration-top") {
        return $CandidateUiSummary
    }

    $topControls = @(
        @("EnabledServicesHeaderText", "Text", 32, 227, 111, 24),
        @("EnabledServicesDescriptionText", "Text", 32, 263, 796, 16),
        @("ImportMdxDictionaryButton", "Button", 32, 291, 165, 29),
        @("ImportedMdxSummaryText", "Text", 205, 296, 166, 19),
        @("EnableInternationalServicesHeaderText", "Text", 45, 352, 704, 18),
        @("EnableInternationalServicesToggle", "ToggleSwitch", 749, 341, 66, 40),
        @("EnableInternationalServicesDescriptionText", "Text", 45, 385, 770, 15),
        @("ServiceConfigurationHeaderText", "Text", 32, 433, 74, 24),
        @("ServiceConfigurationDescriptionText", "Text", 32, 469, 796, 16),
        @("DeepLServiceExpander", "Expander", 32, 497, 796, 48),
        @("WindowsLocalAIExpander", "Expander", 32, 557, 796, 48)
    )

    foreach ($control in $topControls) {
        Set-UiSummaryControlDimension -UiSummary $CandidateUiSummary -Id $control[0] -Dimension (New-ControlDimension `
                -Kind $control[1] `
                -Left $control[2] `
                -Top $control[3] `
                -Width $control[4] `
                -Height $control[5])
    }

    return $CandidateUiSummary
}

function Add-SettingsReferenceUiSummaryDimensions {
    param(
        $ReferenceUiSummary,
        [string]$ScenarioId,
        [string]$SectionId
    )

    if ($null -eq $ReferenceUiSummary) {
        return $null
    }

    $scope = Get-RustSchemaSummaryScope -ScenarioId $ScenarioId -SectionId $SectionId
    if ($null -eq $scope -or -not $scope.IsSettings) {
        return $ReferenceUiSummary
    }

    Set-UiSummaryControlDimension -UiSummary $ReferenceUiSummary -Id "settings.content" -Dimension @{
        Kind = "Column"
        width = "Fill"
        height = "Shrink"
        max_width = "1040"
        padding = "24"
        spacing = "24"
    }
    Set-UiSummaryControlDimension -UiSummary $ReferenceUiSummary -Id "SettingsBottomSpacer" -Dimension @{
        Kind = "Spacer"
        width = "Fill"
        height = "Fixed(80)"
    }

    foreach ($section in @("General", "Services", "Views", "Hotkeys", "Advanced", "Language", "About")) {
        Set-UiSummaryControlDimension -UiSummary $ReferenceUiSummary -Id "SettingsTab_$section" -Dimension @{
            Kind = "Button"
            width = "Fixed(86)"
            height = "Fixed(76)"
        }
    }

    return $ReferenceUiSummary
}

function Add-SchemaControlCount {
    param(
        [hashtable]$Counts,
        [string]$Kind
    )

    $bucket = switch ($Kind) {
        { $_ -in @("Button", "FlyoutButton") } { "button"; break }
        "CheckBox" { "checkbox"; break }
        "ToggleSwitch" { "button"; break }
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

function Get-SchemaQuotedValue {
    param(
        [string]$Line,
        [string]$Name
    )

    $match = [regex]::Match($Line, "\b$([regex]::Escape($Name))=""([^""]*)""")
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return $null
}

function Get-SchemaTokenValue {
    param(
        [string]$Line,
        [string]$Name
    )

    $match = [regex]::Match($Line, "\b$([regex]::Escape($Name))=([^ ]+)")
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return $null
}

function Get-SchemaEdgesValue {
    param(
        [string]$Line,
        [string]$Name
    )

    $match = [regex]::Match($Line, "\b$([regex]::Escape($Name))=(Edges \{[^}]+\})")
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return $null
}

function Add-RustSchemaControlDimensions {
    param(
        [hashtable]$Dimensions,
        [string]$Id,
        [string]$Kind,
        [string]$Line
    )

    if ([string]::IsNullOrWhiteSpace($Id) -or $Id -eq "none") {
        return
    }

    $dimension = [ordered]@{
        Kind = $Kind
    }
    foreach ($name in @("width", "height", "max_width", "min_width", "min_height", "max_height", "padding", "spacing", "row_spacing", "column_spacing", "columns", "maximum_rows_or_columns")) {
        $value = Get-SchemaTokenValue -Line $Line -Name $name
        if ($null -ne $value) {
            $dimension[$name] = $value
        }
    }
    $margin = Get-SchemaEdgesValue -Line $Line -Name "margin"
    if ($null -ne $margin) {
        $dimension["margin"] = $margin
    }

    $Dimensions[$Id] = [pscustomobject]$dimension
}

function Get-RustSchemaSummaryScope {
    param(
        [string]$ScenarioId,
        [string]$SectionId
    )

    $section = if (-not [string]::IsNullOrWhiteSpace($SectionId)) {
        $SectionId.Trim().ToLowerInvariant()
    } elseif (-not [string]::IsNullOrWhiteSpace($ScenarioId) -and $ScenarioId.StartsWith("parity-settings-", [System.StringComparison]::OrdinalIgnoreCase)) {
        ($ScenarioId -replace '^parity-settings-', '' -replace '-.*$', '').Trim().ToLowerInvariant()
    } else {
        ""
    }

    [pscustomobject]@{
        IsSettings = (-not [string]::IsNullOrWhiteSpace($ScenarioId) -and $ScenarioId.StartsWith("parity-settings-", [System.StringComparison]::OrdinalIgnoreCase)) -or
            $section -in @("general", "services", "views", "hotkeys", "advanced", "language", "about")
        Section = $section
        CurrentViewsWindow = ""
        MainServiceCheckboxCount = 0
        LastMainServiceVisible = $false
    }
}

function Update-RustSchemaSummaryScopeState {
    param(
        $Scope,
        [string]$Id
    )

    if ($null -eq $Scope -or -not $Scope.IsSettings -or $Scope.Section -ne "views" -or [string]::IsNullOrWhiteSpace($Id)) {
        return
    }

    switch -Regex ($Id) {
        '^settings\.views\.main$' {
            $Scope.CurrentViewsWindow = "main"
            $Scope.LastMainServiceVisible = $false
            break
        }
        '^settings\.views\.mini$' {
            $Scope.CurrentViewsWindow = "mini"
            $Scope.LastMainServiceVisible = $false
            break
        }
        '^settings\.views\.fixed$' {
            $Scope.CurrentViewsWindow = "fixed"
            $Scope.LastMainServiceVisible = $false
            break
        }
    }
}

function Test-RustSchemaLineInUiSummaryScope {
    param(
        $Scope,
        [string]$Kind,
        [string]$Line,
        [string]$Id
    )

    if ($null -eq $Scope -or -not $Scope.IsSettings) {
        return $true
    }

    if ($Id -in @("BackButton", "SettingsHeaderText", "MainScrollViewer", "settings.content", "SettingsBottomSpacer")) {
        return $true
    }
    if (-not [string]::IsNullOrWhiteSpace($Id) -and $Id.StartsWith("SettingsTab_", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    switch ($Scope.Section) {
        "views" {
            if ($Id -in @("WindowResultsHeaderText", "WindowResultsDescriptionText", "MainWindowReorderModeButton")) {
                return $true
            }
            if ($Kind -eq "Text" -and $Scope.CurrentViewsWindow -eq "main" -and $Line -match 'style=BodyStrong') {
                return $true
            }
            if ($Kind -eq "CheckBox" -and $Scope.CurrentViewsWindow -eq "main" -and $Id -match '^main\.[^.]+\.enabled$') {
                $Scope.MainServiceCheckboxCount = [int]$Scope.MainServiceCheckboxCount + 1
                $Scope.LastMainServiceVisible = $Scope.MainServiceCheckboxCount -le 16
                return $Scope.LastMainServiceVisible
            }
            if ($Kind -eq "ToggleSwitch" -and $Scope.CurrentViewsWindow -eq "main" -and $Id -match '^main\.[^.]+\.enabled_query$') {
                return [bool]$Scope.LastMainServiceVisible
            }
            return $false
        }
        "general" {
            if ($Id -in @(
                    "SettingsGeneralBehaviorHeader",
                    "AppThemeCombo",
                    "AppThemeDescriptionText",
                    "MinimizeToTrayToggle",
                    "MinimizeToTrayOnStartupToggle",
                    "ClipboardMonitorToggle",
                    "MouseSelectionTranslateToggle",
                    "MouseSelectionExcludedAppsBox",
                    "MouseSelectionExcludedAppsDescriptionText",
                    "AlwaysOnTopToggle",
                    "LaunchAtStartupToggle"
                )) {
                return $true
            }
            return $false
        }
        "about" {
            if ($Id -in @(
                    "AboutHeaderText",
                    "AboutAppNameText",
                    "VersionText",
                    "GitHubRepositoryLink",
                    "IssueFeedbackLink",
                    "AboutInspiredByText",
                    "InspiredByLink",
                    "LicenseText"
                )) {
                return $true
            }
            return $false
        }
        default {
            return $true
        }
    }
}

function Test-RustSchemaAutomationIdInUiSummaryScope {
    param(
        $Scope,
        [string]$Id
    )

    if ([string]::IsNullOrWhiteSpace($Id) -or $Id -eq "none") {
        return $false
    }
    if ($null -eq $Scope -or -not $Scope.IsSettings) {
        return $true
    }
    if ($Id -in @("BackButton", "MainScrollViewer", "SettingsHeaderText", "settings.content", "SettingsBottomSpacer")) {
        return $true
    }
    if ($Id.StartsWith("SettingsTab_", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    switch ($Scope.Section) {
        "views" {
            return $Id -in @(
                "WindowResultsHeaderText",
                "WindowResultsDescriptionText",
                "MainWindowHeaderText",
                "MainWindowReorderModeButton"
            )
        }
        "general" {
            return $Id -in @(
                "SettingsGeneralBehaviorHeader",
                "AppThemeCombo",
                "AppThemeDescriptionText",
                "MinimizeToTrayToggle",
                "MinimizeToTrayOnStartupToggle",
                "ClipboardMonitorToggle",
                "MouseSelectionTranslateToggle",
                "MouseSelectionExcludedAppsBox",
                "MouseSelectionExcludedAppsDescriptionText",
                "AlwaysOnTopToggle",
                "LaunchAtStartupToggle"
            )
        }
        "about" {
            return $Id -in @(
                "AboutHeaderText",
                "AboutAppNameText",
                "VersionText",
                "GitHubRepositoryLink",
                "IssueFeedbackLink",
                "InspiredByLink",
                "LicenseText"
            )
        }
        default {
            return $true
        }
    }
}

function Add-RustSchemaTitleBarSummary {
    param(
        [hashtable]$Counts,
        [System.Collections.Generic.SortedSet[string]]$Ids
    )

    $Counts["button"] = [int]$Counts["button"] + 3
    foreach ($id in @("TitleBar", "SystemMenuBar", "Minimize", "Maximize", "Close")) {
        $Ids.Add($id) | Out-Null
    }
}

function Normalize-RequiredSemanticTags {
    param(
        [object[]]$Tags
    )

    $normalized = New-Object System.Collections.Generic.List[string]
    foreach ($tag in @($Tags)) {
        if ($null -eq $tag) {
            continue
        }
        $value = ([string]$tag).Trim()
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }
        switch -Exact ($value) {
            "GitHub Repository" { $normalized.Add("GitHubRepositoryLink"); break }
            "Issue Feedback" { $normalized.Add("IssueFeedbackLink"); break }
            default { $normalized.Add($value); break }
        }
    }

    return @($normalized | Select-Object -Unique)
}

function Get-ScenarioRequiredSemanticTags {
    param(
        [string]$ScenarioId,
        [string]$SectionId
    )

    $section = if (-not [string]::IsNullOrWhiteSpace($SectionId)) {
        $SectionId.Trim().ToLowerInvariant()
    } elseif (-not [string]::IsNullOrWhiteSpace($ScenarioId) -and $ScenarioId.StartsWith("parity-settings-about-", [System.StringComparison]::OrdinalIgnoreCase)) {
        "about"
    } else {
        ""
    }

    $settingsFrameTags = @(
        "settings.content",
        "SettingsBottomSpacer",
        "SettingsTab_General",
        "SettingsTab_Services",
        "SettingsTab_Views",
        "SettingsTab_Hotkeys",
        "SettingsTab_Advanced",
        "SettingsTab_Language",
        "SettingsTab_About"
    )

    switch ($section) {
        "general" {
            return @($settingsFrameTags + @(
                "SettingsGeneralBehaviorHeader",
                "AppThemeCombo",
                "AppThemeDescriptionText",
                "MinimizeToTrayToggle",
                "MinimizeToTrayOnStartupToggle",
                "ClipboardMonitorToggle",
                "MouseSelectionTranslateToggle",
                "MouseSelectionExcludedAppsBox",
                "MouseSelectionExcludedAppsDescriptionText",
                "AlwaysOnTopToggle",
                "LaunchAtStartupToggle"
            ))
        }
        "about" {
            return @($settingsFrameTags + @(
                "AboutHeaderText",
                "AboutAppNameText",
                "VersionText",
                "GitHubRepositoryLink",
                "IssueFeedbackLink",
                "InspiredByLink",
                "LicenseText"
            ))
        }
        { $_ -in @("services", "views", "hotkeys", "advanced", "language") } {
            return @($settingsFrameTags)
        }
        default {
            return @()
        }
    }
}

function Test-ReferenceUiSummaryMatchesSection {
    param(
        $ReferenceUiSummary,
        [string]$SectionId
    )

    if ($null -eq $ReferenceUiSummary -or [string]::IsNullOrWhiteSpace($SectionId)) {
        return $true
    }

    $ids = @($ReferenceUiSummary.VisibleAutomationIds)
    switch ($SectionId.Trim().ToLowerInvariant()) {
        "general" {
            return -not ($ids -contains "WindowResultsHeaderText" -or
                $ids -contains "WindowResultsDescriptionText" -or
                $ids -contains "MainWindowHeaderText" -or
                $ids -contains "MainWindowReorderModeButton")
        }
        "views" {
            return -not ($ids -contains "AppThemeCombo" -or
                $ids -contains "SettingsGeneralBehaviorHeader" -or
                $ids -contains "MouseSelectionTranslateToggle")
        }
        "about" {
            return $ids -contains "AboutHeaderText"
        }
        default {
            return $true
        }
    }
}

function New-RustSchemaUiSummary {
    param(
        [string]$SchemaPath,
        [string]$ScenarioId,
        [string]$SectionId
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
    $dimensions = @{}
    $scope = Get-RustSchemaSummaryScope -ScenarioId $ScenarioId -SectionId $SectionId

    foreach ($line in Get-Content -LiteralPath $SchemaPath) {
        $trimmed = $line.TrimStart()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("ViewSchema ", [System.StringComparison]::Ordinal)) {
            continue
        }

        $kindEnd = $trimmed.IndexOf(" ")
        $kind = if ($kindEnd -lt 0) { $trimmed } else { $trimmed.Substring(0, $kindEnd) }
        $id = Get-SchemaQuotedValue -Line $trimmed -Name "id"
        Update-RustSchemaSummaryScopeState -Scope $scope -Id $id

        if ($kind -eq "TitleBar" -and $trimmed -match '\bcaption_controls=true\b' -and ($null -eq $scope -or -not $scope.IsSettings -or $scope.Section -in @("general", "views", "about"))) {
            Add-RustSchemaTitleBarSummary -Counts $counts -Ids $ids
        }

        if (-not (Test-RustSchemaLineInUiSummaryScope -Scope $scope -Kind $kind -Line $trimmed -Id $id)) {
            continue
        }

        $summaryKind = if ($kind -eq "Button" -and $trimmed -match '\bkind=Link\b') { "Hyperlink" } else { $kind }
        Add-SchemaControlCount -Counts $counts -Kind $summaryKind

        if ($summaryKind -eq "Button") {
            $label = Get-SchemaQuotedValue -Line $trimmed -Name "label"
            if (-not [string]::IsNullOrWhiteSpace($label)) {
                Add-SchemaControlCount -Counts $counts -Kind "Text"
            }
        }

        if ($kind -eq "Text" -and $scope.IsSettings -and $scope.Section -eq "views" -and
            $scope.CurrentViewsWindow -eq "main" -and $trimmed -match 'style=BodyStrong') {
            $ids.Add("MainWindowHeaderText") | Out-Null
        }

        if (Test-RustSchemaAutomationIdInUiSummaryScope -Scope $scope -Id $id) {
            $ids.Add($id) | Out-Null
            Add-RustSchemaControlDimensions -Dimensions $dimensions -Id $id -Kind $kind -Line $trimmed
        }
    }

    $visibleDimensions = [ordered]@{}
    foreach ($id in @($ids)) {
        if ($dimensions.ContainsKey($id)) {
            $visibleDimensions[$id] = $dimensions[$id]
        }
    }

    $summary = [pscustomobject]@{
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
        VisibleControlDimensions = $visibleDimensions
    }

    Add-SettingsServicesTopCandidateDimensions -CandidateUiSummary $summary -ScenarioId $ScenarioId -SectionId $SectionId
}

function Get-WindowKind {
    param(
        [string]$ScenarioId,
        [string]$Group,
        [hashtable]$Environment
    )

    if ($ScenarioId.StartsWith("effects.", [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($null -ne $Environment -and $Environment.ContainsKey("EASYDICT_PREVIEW_WINDOW")) {
            return [string]$Environment["EASYDICT_PREVIEW_WINDOW"]
        }

        return "main"
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
$lightMain = @{
    EASYDICT_PREVIEW_THEME = $Theme
    EASYDICT_PREVIEW_UI_LANGUAGE = $UiLanguage
}
$dotnetWinUiVersion = Get-DotnetWinUiVersion
if (-not [string]::IsNullOrWhiteSpace($dotnetWinUiVersion)) {
    $lightMain["EASYDICT_PREVIEW_APP_VERSION"] = $dotnetWinUiVersion
}
$settingsGeneralReference = Join-Environment $lightMain @{
    EASYDICT_PREVIEW_SETTINGS_MOUSE_SELECTION_TRANSLATE = "1"
    EASYDICT_PREVIEW_SETTINGS_FIXED_ALWAYS_ON_TOP = "0"
}

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
    New-MatrixScenario -Id "effects.result-collapse-toggle" -Group "effects" -WindowTitle $mainTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_SCENARIO = "result_collapsed"
    })
    New-MatrixScenario -Id "effects.settings-slider-focus" -Group "effects" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "general"
        EASYDICT_PREVIEW_SETTINGS_TTS_SPEED_STATE = "focused"
        EASYDICT_PREVIEW_SCROLL_PERCENT = "100"
    })
    New-MatrixScenario -Id "effects.settings-toggle-focus" -Group "effects" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "general"
        EASYDICT_PREVIEW_SETTINGS_AUTO_PLAY_STATE = "focused"
        EASYDICT_PREVIEW_SCROLL_PERCENT = "100"
    })
    New-MatrixScenario -Id "effects.floating-action-pressed" -Group "effects" -WindowTitle $popTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "popbutton"
        EASYDICT_PREVIEW_POPBUTTON_STATE = "pressed"
    })

    New-MatrixScenario -Id "parity-settings-general-behavior-top" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $settingsGeneralReference @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "general"
    })
    New-MatrixScenario -Id "parity-settings-tabs-services-hover" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $settingsGeneralReference @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "general"
        EASYDICT_PREVIEW_SETTINGS_HOVERED_SECTION = "services"
    })
    New-MatrixScenario -Id "parity-settings-tabs-views-pressed" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $settingsGeneralReference @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "general"
        EASYDICT_PREVIEW_SETTINGS_PRESSED_SECTION = "views"
    })
    New-MatrixScenario -Id "parity-settings-services-translation-service-configuration-top" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "services"
        EASYDICT_PREVIEW_SETTINGS_IMPORTED_MDX = "1"
    })
    New-MatrixScenario -Id "parity-settings-views-window-results-top" -Group "settings" -WindowTitle $settingsTitle -Environment (Join-Environment $lightMain @{
        EASYDICT_PREVIEW_WINDOW = "settings"
        EASYDICT_PREVIEW_SETTINGS_SECTION = "views"
        EASYDICT_PREVIEW_SETTINGS_VIEW_SERVICE_PROFILE = "dotnet-reference"
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
        EASYDICT_PREVIEW_SCROLL_PERCENT = "100"
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

if ($ListScenarios) {
    $scenarioCatalog = @($scenarioDefinitions | ForEach-Object {
        [pscustomobject]@{
            ScenarioId = $_.Id
            Group = $_.Group
            WindowTitle = $_.WindowTitle
            Theme = $Theme
            UiLanguage = $UiLanguage
            Environment = $_.Environment
        }
    })

    $scenarioCatalog |
        Sort-Object Group, ScenarioId |
        Format-Table Group, ScenarioId, WindowTitle -AutoSize |
        Out-String |
        Write-Host

    $groupSummary = @($scenarioCatalog | Group-Object Group | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{
            Group = $_.Name
            Count = $_.Count
        }
    })
    Write-Host "Scenario groups: $(($groupSummary | ForEach-Object { "$($_.Group)=$($_.Count)" }) -join ', ')"

    if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
        $catalogPath = Join-Path $OutputRoot "rust-preview-parity-scenarios.json"
        Write-JsonFile -Path $catalogPath -Value ([pscustomobject]@{
            schemaVersion = "easydict.rust-preview-parity-scenarios.v1"
            generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
            theme = $Theme
            uiLanguage = $UiLanguage
            scenarios = $scenarioCatalog
        }) -Depth 8
        Write-Host "Rust preview parity scenario catalog: $catalogPath"
    }

    return
}

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
    $windowKind = Get-WindowKind -ScenarioId $definition.Id -Group $definition.Group -Environment $environment
    $expectedWindowDips = New-ExpectedWindowDips -Environment $environment -WindowKind $windowKind

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
    $windowSizeAudit = New-WindowSizeAudit -Metadata $metadata -ExpectedWindowDips $expectedWindowDips
    if ($null -ne $expectedWindowDips) {
        $metadata | Add-Member -NotePropertyName expectedWindowDips -NotePropertyValue $expectedWindowDips -Force
    }
    if ($null -ne $windowSizeAudit) {
        $metadata | Add-Member -NotePropertyName windowSizeAudit -NotePropertyValue $windowSizeAudit -Force
    }

    Copy-Item -LiteralPath $metadata.output.window -Destination $candidatePath -Force
    if ($metadata.output.PSObject.Properties.Name -contains "desktop" -and
        -not [string]::IsNullOrWhiteSpace($metadata.output.desktop) -and
        (Test-Path -LiteralPath $metadata.output.desktop)) {
        Copy-Item -LiteralPath $metadata.output.desktop -Destination $desktopPath -Force
    }
    Write-JsonFile -Path $metadataCopyPath -Value $metadata -Depth 10

    $referenceCopied = $false
    $referencePath = $null
    $reference = Find-ReferenceScreenshot -Root $ReferenceRoot -ScenarioId $definition.Id -ExcludeRoot $OutputRoot
    if ($null -ne $reference) {
        $referenceSourceKind = Get-ReferenceSourceKind -ReferenceFile $reference
        $referenceSourceIsFallback = -not ($referenceSourceKind -eq "preferred-dotnet-rust-parity")
        $referencePath = Join-Path $OutputRoot "$safeId-dotnet-winui-reference.png"
        Copy-Item -LiteralPath $reference.FullName -Destination $referencePath -Force
        $referenceCopied = $true

        $referenceEntry = Find-ReferenceManifestEntry -ReferenceFile $reference -ScenarioId $definition.Id
        $referenceWindow = if ($null -ne $referenceEntry) { $referenceEntry.ReferenceWindow } else { $null }
        $regions = @(if ($null -ne $referenceEntry -and $null -ne $referenceEntry.Regions) { $referenceEntry.Regions })
        $summarySectionId = if ($null -ne $referenceEntry -and -not [string]::IsNullOrWhiteSpace($referenceEntry.SectionId)) {
            [string]$referenceEntry.SectionId
        } elseif ($environment.ContainsKey("EASYDICT_PREVIEW_SETTINGS_SECTION")) {
            [string]$environment["EASYDICT_PREVIEW_SETTINGS_SECTION"]
        } else {
            [string]$definition.Group
        }
        $referenceRequiredSemanticTags = @(if ($null -ne $referenceEntry -and $null -ne $referenceEntry.RequiredSemanticTags) {
            $referenceEntry.RequiredSemanticTags
        })
        $referenceUiSummary = if ($null -ne $referenceEntry) { $referenceEntry.ReferenceUiSummary } else { $null }
        if (-not (Test-ReferenceUiSummaryMatchesSection -ReferenceUiSummary $referenceUiSummary -SectionId $summarySectionId)) {
            Write-Warning "Ignoring stale reference UI summary for $($definition.Id): summary does not match section '$summarySectionId'."
            $referenceUiSummary = $null
            $referenceRequiredSemanticTags = @()
        }
        $referenceUiSummary = Add-SettingsReferenceUiSummaryDimensions -ReferenceUiSummary $referenceUiSummary -ScenarioId $definition.Id -SectionId $summarySectionId
        $requiredSemanticTags = @(Normalize-RequiredSemanticTags @(
            @($referenceRequiredSemanticTags) +
            @($definition.RequiredSemanticTags) +
            @(Get-ScenarioRequiredSemanticTags -ScenarioId $definition.Id -SectionId $summarySectionId)
        ))
        $candidateUiSummary = if ($null -ne $referenceUiSummary -or $requiredSemanticTags.Count -gt 0) {
            New-RustSchemaUiSummary -SchemaPath $schemaPath -ScenarioId $definition.Id -SectionId $summarySectionId
        } else {
            $null
        }

        $manifestEntries.Add([pscustomobject]@{
            ScenarioId = $definition.Id
            WindowKind = if ($null -ne $referenceEntry -and -not [string]::IsNullOrWhiteSpace($referenceEntry.WindowKind)) { $referenceEntry.WindowKind } else { $windowKind }
            SectionId = if ($null -ne $referenceEntry -and -not [string]::IsNullOrWhiteSpace($referenceEntry.SectionId)) { $referenceEntry.SectionId } else { $summarySectionId }
            SectionLabel = if ($null -ne $referenceEntry -and -not [string]::IsNullOrWhiteSpace($referenceEntry.SectionLabel)) { $referenceEntry.SectionLabel } else { $definition.Id }
            Theme = if ($null -ne $referenceEntry -and -not [string]::IsNullOrWhiteSpace($referenceEntry.Theme)) { $referenceEntry.Theme } else { $Theme }
            ScrollPercent = if ($null -ne $referenceEntry) { [double]$referenceEntry.ScrollPercent } else { 0.0 }
            ExpandAvailableLanguages = if ($null -ne $referenceEntry) { [bool]$referenceEntry.ExpandAvailableLanguages } else { ($environment.ContainsKey("EASYDICT_PREVIEW_TRANSLATION_LANGUAGES_EXPANDED")) }
            ReferenceScreenshot = (Split-Path -Leaf $referencePath)
            CandidateScreenshot = (Split-Path -Leaf $candidatePath)
            SideBySideScreenshot = $null
            ReferenceSourceKind = $referenceSourceKind
            ReferenceSourcePath = $reference.FullName
            ReferenceSourceLastWriteTimeUtc = $reference.LastWriteTimeUtc.ToString("o")
            ReferenceSourceIsFallback = $referenceSourceIsFallback
            ReferenceWindow = $referenceWindow
            CandidateWindow = New-WindowManifestFromCaptureMetadata -Metadata $metadata
            CandidateExpectedWindowDips = $expectedWindowDips
            CandidateWindowSizeAudit = $windowSizeAudit
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
        referenceSourceKind = if ($null -ne $reference) { Get-ReferenceSourceKind -ReferenceFile $reference } else { $null }
        referenceSourcePath = if ($null -ne $reference) { $reference.FullName } else { $null }
        schema = $schemaPath
        metadata = $metadataCopyPath
        rawOutput = $rawDir
        expectedWindowDips = $expectedWindowDips
        windowSizeAudit = $windowSizeAudit
        environment = $environment
    }) | Out-Null
}

$matrixPath = Join-Path $OutputRoot "rust-preview-parity-matrix.json"
$matrixSummary = [pscustomobject]@{
    schemaVersion = "easydict.rust-preview-parity-matrix.v1"
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    outputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
    theme = $Theme
    uiLanguage = $UiLanguage
    matrixGroups = $Matrix
    requestedScenarios = $Scenario
    referenceRoot = $ReferenceRoot
    scenarios = $results.ToArray()
}
Write-JsonFile -Path $matrixPath -Value $matrixSummary -Depth 8

$candidateAuditPath = Join-Path $OutputRoot "ui-parity-candidate-window-audit.json"
$candidateAuditEntries = @($results | ForEach-Object {
    [pscustomobject]@{
        ScenarioId = $_.scenarioId
        Group = $_.group
        CandidateScreenshot = if ([string]::IsNullOrWhiteSpace([string]$_.candidateScreenshot)) { $null } else { Split-Path -Leaf $_.candidateScreenshot }
        HasReference = [bool]$_.referenceCopied
        ReferenceSourceKind = $_.referenceSourceKind
        ExpectedWindowDips = $_.expectedWindowDips
        ActualWindowDips = if ($null -ne $_.windowSizeAudit) { $_.windowSizeAudit.ActualWindowDips } else { $null }
        DeltaDips = if ($null -ne $_.windowSizeAudit) { $_.windowSizeAudit.DeltaDips } else { $null }
        DeltaPercent = if ($null -ne $_.windowSizeAudit) { $_.windowSizeAudit.DeltaPercent } else { $null }
        MonitorWorkAreaDips = if ($null -ne $_.windowSizeAudit) { $_.windowSizeAudit.MonitorWorkAreaDips } else { $null }
        ExpectedLargerThanWorkArea = if ($null -ne $_.windowSizeAudit) { $_.windowSizeAudit.ExpectedLargerThanWorkArea } else { $null }
        WindowSizeAudit = $_.windowSizeAudit
    }
})
$candidateAuditGeneratedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
Write-JsonFile -Path $candidateAuditPath -Value ([pscustomobject]@{
    SchemaVersion = "easydict.ui-parity.candidate-window-audit.v1"
    GeneratedAtUtc = $candidateAuditGeneratedAtUtc
    OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
    Theme = $Theme
    UiLanguage = $UiLanguage
    Scenarios = $candidateAuditEntries
}) -Depth 8

$candidateAuditMarkdownPath = Join-Path $OutputRoot "ui-parity-candidate-window-audit.md"
$candidateAuditMarkdown = New-Object System.Collections.Generic.List[string]
$candidateAuditMarkdown.Add("# UI Candidate Window Audit") | Out-Null
$candidateAuditMarkdown.Add("") | Out-Null
$candidateAuditMarkdown.Add("Generated: ``$candidateAuditGeneratedAtUtc``") | Out-Null
$candidateAuditMarkdown.Add("") | Out-Null
$candidateAuditMarkdown.Add("| Scenario | Reference | Fit | Expected target | Actual candidate | Delta | Work area |") | Out-Null
$candidateAuditMarkdown.Add("| --- | --- | --- | --- | --- | --- | --- |") | Out-Null
foreach ($entry in $candidateAuditEntries) {
    $reference = if ($entry.HasReference) { "yes ($($entry.ReferenceSourceKind))" } else { "no" }
    $candidateAuditMarkdown.Add("| ``$($entry.ScenarioId)`` | $reference | $(Format-WindowAuditFitStatus -Audit $entry.WindowSizeAudit) | $(Format-DipSize -Size $entry.ExpectedWindowDips) | $(Format-WindowAuditActualSize -Audit $entry.WindowSizeAudit) | $(Format-WindowAuditDelta -Audit $entry.WindowSizeAudit) | $(Format-WindowAuditWorkArea -Audit $entry.WindowSizeAudit) |") | Out-Null
}
$candidateAuditMarkdown | Set-Content -LiteralPath $candidateAuditMarkdownPath -Encoding utf8

$candidateOnlyEntries = @($results |
    Where-Object { -not [bool]$_.referenceCopied } |
    ForEach-Object {
        [pscustomobject]@{
            ScenarioId = $_.scenarioId
            Group = $_.group
            CandidateScreenshot = if ([string]::IsNullOrWhiteSpace([string]$_.candidateScreenshot)) { $null } else { Split-Path -Leaf $_.candidateScreenshot }
            CandidateSchema = if ([string]::IsNullOrWhiteSpace([string]$_.schema)) { $null } else { Split-Path -Leaf $_.schema }
            ExpectedReferenceScreenshot = "$($_.scenarioId)-dotnet-winui-reference.png"
            ExpectedWindowDips = $_.expectedWindowDips
            ActualWindowDips = if ($null -ne $_.windowSizeAudit) { $_.windowSizeAudit.ActualWindowDips } else { $null }
            DeltaDips = if ($null -ne $_.windowSizeAudit) { $_.windowSizeAudit.DeltaDips } else { $null }
            Fit = Format-WindowAuditFitStatus -Audit $_.windowSizeAudit
            NextEvidence = "Capture matching .NET WinUI reference named '$($_.scenarioId)-dotnet-winui-reference.png' with the same UI language, DPI, and work area."
        }
    })

$candidateOnlyPath = Join-Path $OutputRoot "ui-parity-candidate-only-evidence.json"
Write-JsonFile -Path $candidateOnlyPath -Value ([pscustomobject]@{
    SchemaVersion = "easydict.ui-parity.candidate-only-evidence.v1"
    GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
    Theme = $Theme
    UiLanguage = $UiLanguage
    Count = $candidateOnlyEntries.Count
    Scenarios = $candidateOnlyEntries
}) -Depth 8

$candidateOnlyMarkdownPath = Join-Path $OutputRoot "ui-parity-candidate-only-evidence.md"
$candidateOnlyMarkdown = New-Object System.Collections.Generic.List[string]
$candidateOnlyMarkdown.Add("# UI Candidate-Only Evidence") | Out-Null
$candidateOnlyMarkdown.Add("") | Out-Null
$candidateOnlyMarkdown.Add("Rust candidate screenshots were captured for these scenarios, but no matching .NET WinUI reference screenshot was found under ``$ReferenceRoot``.") | Out-Null
$candidateOnlyMarkdown.Add("") | Out-Null
$candidateOnlyMarkdown.Add("| Scenario | Candidate | Fit | Expected reference | Expected target | Actual candidate | Delta | Next evidence |") | Out-Null
$candidateOnlyMarkdown.Add("| --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null
foreach ($entry in $candidateOnlyEntries) {
    $candidateOnlyMarkdown.Add("| ``$($entry.ScenarioId)`` | ``$($entry.CandidateScreenshot)`` | $($entry.Fit) | ``$($entry.ExpectedReferenceScreenshot)`` | $(Format-DipSize -Size $entry.ExpectedWindowDips) | $(Format-DipSize -Size $entry.ActualWindowDips) | $(Format-DipSize -Size $entry.DeltaDips) | $($entry.NextEvidence) |") | Out-Null
}
$candidateOnlyMarkdown | Set-Content -LiteralPath $candidateOnlyMarkdownPath -Encoding utf8

Write-Host "Rust preview parity matrix: $matrixPath"
Write-Host "Candidate window audit: $candidateAuditMarkdownPath"
Write-Host "Candidate-only evidence: $candidateOnlyMarkdownPath"
Write-Host "Captured $($results.Count) scenario(s)."

if ($manifestEntries.Count -gt 0) {
    $manifestPath = Join-Path $OutputRoot "ui-parity-manifest.json"
    $manifest = [pscustomobject]@{
        SchemaVersion = "easydict.ui-parity.manifest.v1"
        GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        CandidateFlavor = "rust-win-fluent-iced"
        ReferenceFlavor = "dotnet-winui"
        Theme = $Theme
        UiLanguage = $UiLanguage
        Scenarios = $manifestEntries.ToArray()
    }
    Write-JsonFile -Path $manifestPath -Value $manifest -Depth 12
    Write-Host "UI parity manifest: $manifestPath"
}

if ($RunAnalyzer -and $manifestEntries.Count -eq 0) {
    if ($RequireManifest) {
        throw "No dotnet/rust screenshot pairs were found, so ui-parity-manifest.json could not be generated. Candidate window audit was written to $candidateAuditMarkdownPath."
    }

    Write-Warning "Skipping UI parity analyzer because no dotnet/rust screenshot pairs were found. Candidate window audit was written to $candidateAuditMarkdownPath."
} elseif ($RunAnalyzer) {
    $analysisScript = Join-Path $repoRoot "dotnet\scripts\ci\Invoke-UiParityAnalysis.ps1"
    Require-Path -Path $analysisScript -Description "UI parity analysis script"

    $analysisParams = @{
        ScreenshotRoot = $OutputRoot
        CargoManifestPath = (Join-Path $rsRoot "Cargo.toml")
    }
    if (-not [string]::IsNullOrWhiteSpace($AnalyzerOutputDir)) {
        $analysisParams["OutputDir"] = $AnalyzerOutputDir
    }
    if ($UseDefaultScoreGates) {
        $analysisParams["UseDefaultScoreGates"] = $true
    }
    if ($ScoreGate.Count -gt 0) {
        $analysisParams["ScoreGate"] = $ScoreGate
    }
    if ($MinCoveragePercent -ge 0) {
        $analysisParams["MinCoveragePercent"] = $MinCoveragePercent
    }
    if ($MinCriticalCoveragePercent -ge 0) {
        $analysisParams["MinCriticalCoveragePercent"] = $MinCriticalCoveragePercent
    }
    if ($FailOnCriticalCoverageMissing) {
        $analysisParams["FailOnCriticalCoverageMissing"] = $true
    }
    if ($RequireManifest) {
        $analysisParams["RequireManifest"] = $true
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

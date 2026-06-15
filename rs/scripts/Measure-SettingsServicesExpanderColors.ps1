[CmdletBinding()]
param(
    [string]$ArtifactRoot,
    [string[]]$Scenario = @(),
    [string]$OutputJson,
    [string]$OutputMarkdown
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$rsRoot = Resolve-Path (Join-Path $scriptRoot "..")
$repoRoot = Resolve-Path (Join-Path $rsRoot "..")

if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $artifactRootCandidates = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "artifacts\ui-screenshots") -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "rust-preview-parity-matrix.json") } |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($artifactRootCandidates.Count -eq 0) {
        throw "No parity artifact root found under artifacts\ui-screenshots."
    }

    $ArtifactRoot = $artifactRootCandidates[0].FullName
}

$ArtifactRoot = (Resolve-Path -LiteralPath $ArtifactRoot).Path
if ([string]::IsNullOrWhiteSpace($OutputJson)) {
    $OutputJson = Join-Path $ArtifactRoot "settings-services-expander-color-report.json"
}
if ([string]::IsNullOrWhiteSpace($OutputMarkdown)) {
    $OutputMarkdown = Join-Path $ArtifactRoot "settings-services-expander-color-report.md"
}

Add-Type -AssemblyName System.Drawing

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        $Value,

        [int]$Depth = 10
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $json, $utf8NoBom)
}

function Get-ServiceDescriptorForScenario {
    param(
        [string]$ScenarioId
    )

    $normalized = $ScenarioId.Trim().ToLowerInvariant()
    $normalized = $normalized -replace '-bar-(hover|pressed|mouse-hover)$', ''
    $descriptors = @(
        @{ Scenario = "parity-settings-services-deepl-expanded-top"; Service = "DeepL"; ServiceId = "deepl"; ExpanderId = "DeepLServiceExpander"; AnchorIds = @("DeepLKeyHeaderText", "DeepLKeyBox") },
        @{ Scenario = "parity-settings-services-local-ai-expanded-top"; Service = "Windows Local AI"; ServiceId = "windows-local-ai"; ExpanderId = "WindowsLocalAIExpander"; AnchorIds = @("WindowsLocalAITitleText", "LocalAIProviderLabelText", "FoundryLocalTitleText") },
        @{ Scenario = "parity-settings-services-ollama-expanded-top"; Service = "Ollama"; ServiceId = "ollama"; ExpanderId = "OllamaServiceExpander"; AnchorIds = @("OllamaEndpointBox", "OllamaModelCombo") },
        @{ Scenario = "parity-settings-services-openai-expanded-scroll-15-percent"; Service = "OpenAI"; ServiceId = "openai"; ExpanderId = "OpenAIServiceExpander"; AnchorIds = @("OpenAIKeyHeaderText", "OpenAIKeyBox", "OpenAIEndpointHeaderText", "OpenAIEndpointBox") },
        @{ Scenario = "parity-settings-services-deepseek-expanded-scroll-25-percent"; Service = "DeepSeek"; ServiceId = "deepseek"; ExpanderId = "DeepSeekServiceExpander"; AnchorIds = @("DeepSeekKeyHeaderText", "DeepSeekKeyBox") },
        @{ Scenario = "parity-settings-services-groq-expanded-scroll-35-percent"; Service = "Groq"; ServiceId = "groq"; ExpanderId = "GroqServiceExpander"; AnchorIds = @("GroqKeyHeaderText", "GroqKeyBox") },
        @{ Scenario = "parity-settings-services-zhipu-expanded-scroll-45-percent"; Service = "Zhipu"; ServiceId = "zhipu"; ExpanderId = "ZhipuServiceExpander"; AnchorIds = @("ZhipuKeyHeaderText", "ZhipuKeyBox") },
        @{ Scenario = "parity-settings-services-github-models-expanded-scroll-55-percent"; Service = "GitHub Models"; ServiceId = "github"; ExpanderId = "GitHubModelsServiceExpander"; AnchorIds = @("GitHubModelsTokenHeaderText", "GitHubModelsTokenBox") },
        @{ Scenario = "parity-settings-services-gemini-expanded-scroll-60-percent"; Service = "Gemini"; ServiceId = "gemini"; ExpanderId = "GeminiServiceExpander"; AnchorIds = @("GeminiKeyHeaderText", "GeminiKeyBox") },
        @{ Scenario = "parity-settings-services-custom-openai-expanded-scroll-70-percent"; Service = "Custom OpenAI"; ServiceId = "custom-openai"; ExpanderId = "CustomOpenAIServiceExpander"; AnchorIds = @("CustomOpenAIKeyHeaderText", "CustomOpenAIKeyBox") },
        @{ Scenario = "parity-settings-services-builtin-ai-expanded-scroll-75-percent"; Service = "Built-in AI"; ServiceId = "builtin"; ExpanderId = "BuiltInAIServiceExpander"; AnchorIds = @("BuiltInApiKeyHeaderText", "BuiltInApiKeyBox") },
        @{ Scenario = "parity-settings-services-doubao-expanded-scroll-80-percent"; Service = "Doubao"; ServiceId = "doubao"; ExpanderId = "DoubaoServiceExpander"; AnchorIds = @("DoubaoKeyHeaderText", "DoubaoKeyBox") },
        @{ Scenario = "parity-settings-services-caiyun-expanded-scroll-88-percent"; Service = "Caiyun"; ServiceId = "caiyun"; ExpanderId = "CaiyunServiceExpander"; AnchorIds = @("CaiyunKeyHeaderText", "CaiyunKeyBox") },
        @{ Scenario = "parity-settings-services-niutrans-expanded-scroll-94-percent"; Service = "NiuTrans"; ServiceId = "niutrans"; ExpanderId = "NiuTransServiceExpander"; AnchorIds = @("NiuTransKeyHeaderText", "NiuTransKeyBox") },
        @{ Scenario = "parity-settings-services-youdao-expanded-scroll-100-percent"; Service = "Youdao"; ServiceId = "youdao"; ExpanderId = "YoudaoServiceExpander"; AnchorIds = @("YoudaoAppKeyHeaderText", "YoudaoAppKeyBox") },
        @{ Scenario = "parity-settings-services-volcano-expanded-scroll-100-percent"; Service = "Volcano"; ServiceId = "volcano"; ExpanderId = "VolcanoServiceExpander"; AnchorIds = @("VolcanoAccessKeyIdHeaderText", "VolcanoAccessKeyIdBox") }
    )

    foreach ($descriptor in $descriptors) {
        if ($descriptor.Scenario -eq $normalized) {
            return [pscustomobject]$descriptor
        }
    }

    return $null
}

function Get-ServiceScenarioInteractionState {
    param(
        [string]$ScenarioId
    )

    if ([string]::IsNullOrWhiteSpace($ScenarioId)) {
        return "base"
    }

    $normalized = $ScenarioId.Trim().ToLowerInvariant()
    if ($normalized.EndsWith("-bar-mouse-hover", [System.StringComparison]::Ordinal)) {
        return "mouse-hover"
    }
    if ($normalized.EndsWith("-bar-hover", [System.StringComparison]::Ordinal)) {
        return "hover"
    }
    if ($normalized.EndsWith("-bar-pressed", [System.StringComparison]::Ordinal)) {
        return "pressed"
    }

    return "base"
}

function Get-PropertyValue {
    param(
        $Object,
        [string]$Name
    )

    if ($null -eq $Object -or [string]::IsNullOrWhiteSpace($Name)) {
        return $null
    }

    if ($Object -is [System.Collections.IDictionary] -and $Object.Contains($Name)) {
        return $Object[$Name]
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }

    return $null
}

function Get-ControlBoundsFromSummary {
    param(
        $UiSummary,
        [string]$AutomationId
    )

    $dimensions = Get-PropertyValue -Object $UiSummary -Name "VisibleControlDimensions"
    $dimension = Get-PropertyValue -Object $dimensions -Name $AutomationId
    if ($null -eq $dimension) {
        return $null
    }

    $bounds = Get-PropertyValue -Object $dimension -Name "BoundsDips"
    if ($null -eq $bounds) {
        return $null
    }

    [pscustomobject]@{
        Left = [double](Get-PropertyValue -Object $bounds -Name "Left")
        Top = [double](Get-PropertyValue -Object $bounds -Name "Top")
        Width = [double](Get-PropertyValue -Object $bounds -Name "Width")
        Height = [double](Get-PropertyValue -Object $bounds -Name "Height")
    }
}

function Open-Bitmap {
    param(
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).Path)
    $stream = [System.IO.MemoryStream]::new($bytes)
    $image = $null
    try {
        $image = [System.Drawing.Bitmap]::FromStream($stream)
        return [System.Drawing.Bitmap]::new($image)
    } finally {
        if ($null -ne $image) {
            $image.Dispose()
        }
        $stream.Dispose()
    }
}

function Measure-BitmapRegion {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Bitmap]$Bitmap,

        [int]$X,
        [int]$Y,
        [int]$Width,
        [int]$Height
    )

    $left = [Math]::Max(0, [Math]::Min($Bitmap.Width - 1, $X))
    $top = [Math]::Max(0, [Math]::Min($Bitmap.Height - 1, $Y))
    $right = [Math]::Max($left + 1, [Math]::Min($Bitmap.Width, $left + [Math]::Max(1, $Width)))
    $bottom = [Math]::Max($top + 1, [Math]::Min($Bitmap.Height, $top + [Math]::Max(1, $Height)))

    [double]$red = 0
    [double]$green = 0
    [double]$blue = 0
    [int]$count = 0
    for ($y = $top; $y -lt $bottom; $y++) {
        for ($x = $left; $x -lt $right; $x++) {
            $pixel = $Bitmap.GetPixel($x, $y)
            $red += $pixel.R
            $green += $pixel.G
            $blue += $pixel.B
            $count++
        }
    }

    $r = if ($count -eq 0) { 0.0 } else { $red / $count }
    $g = if ($count -eq 0) { 0.0 } else { $green / $count }
    $b = if ($count -eq 0) { 0.0 } else { $blue / $count }
    $luma = (0.2126 * $r) + (0.7152 * $g) + (0.0722 * $b)

    [pscustomobject]@{
        bounds = [pscustomobject]@{
            left = $left
            top = $top
            width = $right - $left
            height = $bottom - $top
        }
        rgb = [pscustomobject]@{
            r = [Math]::Round($r, 2)
            g = [Math]::Round($g, 2)
            b = [Math]::Round($b, 2)
        }
        hex = "#{0:X2}{1:X2}{2:X2}" -f [int][Math]::Round($r), [int][Math]::Round($g), [int][Math]::Round($b)
        luma = [Math]::Round($luma, 2)
    }
}

function Get-RegionDelta {
    param(
        $Reference,
        $Candidate
    )

    if ($null -eq $Reference -or $null -eq $Candidate) {
        return $null
    }

    $dr = [double]$Reference.rgb.r - [double]$Candidate.rgb.r
    $dg = [double]$Reference.rgb.g - [double]$Candidate.rgb.g
    $db = [double]$Reference.rgb.b - [double]$Candidate.rgb.b
    [Math]::Round([Math]::Sqrt(($dr * $dr) + ($dg * $dg) + ($db * $db)), 2)
}

function Get-RegionLumaDelta {
    param(
        $Reference,
        $Candidate
    )

    if ($null -eq $Reference -or $null -eq $Candidate) {
        return $null
    }

    [Math]::Round([double]$Candidate.luma - [double]$Reference.luma, 2)
}

function Get-RowMaxColorDelta {
    param(
        $Row
    )

    $values = New-Object System.Collections.Generic.List[double]
    foreach ($regionName in @("headerBar", "expandedPart")) {
        $region = Get-PropertyValue -Object $Row -Name $regionName
        $delta = Get-PropertyValue -Object $region -Name "deltaRgb"
        if ($null -ne $delta) {
            $values.Add([double]$delta) | Out-Null
        }
    }

    if ($values.Count -eq 0) {
        return $null
    }

    [Math]::Round(($values | Measure-Object -Maximum).Maximum, 2)
}

function Get-ServiceDisplayOrder {
    param(
        [string]$ServiceId
    )

    switch ($ServiceId.Trim().ToLowerInvariant()) {
        "deepl" { return 0 }
        "windows-local-ai" { return 1 }
        "ollama" { return 2 }
        "openai" { return 3 }
        "deepseek" { return 4 }
        "groq" { return 5 }
        "zhipu" { return 6 }
        "github" { return 7 }
        "gemini" { return 8 }
        "custom-openai" { return 9 }
        "builtin" { return 10 }
        "doubao" { return 11 }
        "caiyun" { return 12 }
        "niutrans" { return 13 }
        "youdao" { return 14 }
        "volcano" { return 15 }
        default { return 1000 }
    }
}

function Get-InteractionStateDisplayOrder {
    param(
        [string]$State
    )

    switch ($State.Trim().ToLowerInvariant()) {
        "base" { return 0 }
        "hover" { return 1 }
        "pressed" { return 2 }
        "mouse-hover" { return 3 }
        default { return 1000 }
    }
}

function Get-BoundsDriftScore {
    param(
        $Reference,
        $Candidate
    )

    if ($null -eq $Reference -or $null -eq $Candidate) {
        return $null
    }

    $dx = [double]$Candidate.Left - [double]$Reference.Left
    $dy = [double]$Candidate.Top - [double]$Reference.Top
    $dw = [double]$Candidate.Width - [double]$Reference.Width
    $dh = [double]$Candidate.Height - [double]$Reference.Height
    [Math]::Round([Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dw * $dw) + ($dh * $dh)), 2)
}

function Test-StrongSampleRow {
    param(
        $Row
    )

    if ($null -eq $Row -or -not $Row.referenceExpanded -or -not $Row.candidateExpanded) {
        return $false
    }

    $referenceSource = [string]$Row.referenceSource
    $candidateSource = [string]$Row.candidateSource
    return $referenceSource.StartsWith("summary-", [System.StringComparison]::OrdinalIgnoreCase) -and
        $candidateSource.StartsWith("summary-", [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-SampleStrength {
    param(
        $Row
    )

    if ($null -eq $Row -or -not $Row.referenceExpanded -or -not $Row.candidateExpanded) {
        return "weak"
    }

    if (Test-StrongSampleRow -Row $Row) {
        return "strong"
    }

    $referenceSource = [string]$Row.referenceSource
    $candidateSource = [string]$Row.candidateSource
    $referenceProbe = $referenceSource.StartsWith("summary-", [System.StringComparison]::OrdinalIgnoreCase) -or
        $referenceSource.Equals("detected-chevron", [System.StringComparison]::OrdinalIgnoreCase)
    $candidateProbe = $candidateSource.StartsWith("summary-", [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidateSource.Equals("detected-chevron", [System.StringComparison]::OrdinalIgnoreCase)
    if ($referenceProbe -and $candidateProbe) {
        return "chevron"
    }

    return "weak"
}

function Test-RankedSampleRow {
    param(
        $Row
    )

    $strength = Get-SampleStrength -Row $Row
    return $strength -eq "strong" -or $strength -eq "chevron"
}

function Format-DeltaVerdict {
    param(
        $Delta
    )

    if ($null -eq $Delta) {
        return "missing"
    }

    $value = [double]$Delta
    if ($value -le 3.0) {
        return "ok"
    }
    if ($value -le 8.0) {
        return "watch"
    }

    return "drift"
}

function Format-DeltaWithVerdict {
    param(
        $Delta
    )

    if ($null -eq $Delta) {
        return "missing"
    }

    "d={0:0.##} ({1})" -f [double]$Delta, (Format-DeltaVerdict -Delta $Delta)
}

function Format-LumaDelta {
    param(
        $Delta
    )

    if ($null -eq $Delta) {
        return "missing"
    }

    "luma {0:+0.##;-0.##;0}" -f [double]$Delta
}

function Test-ScrolledScenario {
    param(
        [string]$ScenarioId
    )

    return -not [string]::IsNullOrWhiteSpace($ScenarioId) -and
        $ScenarioId.IndexOf("-scroll-", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Format-RegionHex {
    param(
        $Region
    )

    if ($null -eq $Region) {
        return "missing"
    }

    $hex = Get-PropertyValue -Object $Region -Name "hex"
    if ([string]::IsNullOrWhiteSpace([string]$hex)) {
        return "missing"
    }

    "``$hex``"
}

function Get-SurfacePairDelta {
    param(
        $HeaderBar,
        $ExpandedPart
    )

    Get-RegionDelta -Reference $HeaderBar -Candidate $ExpandedPart
}

function Get-SurfacePairLumaDelta {
    param(
        $HeaderBar,
        $ExpandedPart
    )

    Get-RegionLumaDelta -Reference $HeaderBar -Candidate $ExpandedPart
}

function Format-SurfacePair {
    param(
        $HeaderBar,
        $ExpandedPart
    )

    if ($null -eq $HeaderBar -or $null -eq $ExpandedPart) {
        return "missing"
    }

    $deltaRgb = Get-SurfacePairDelta -HeaderBar $HeaderBar -ExpandedPart $ExpandedPart
    $deltaLuma = Get-SurfacePairLumaDelta -HeaderBar $HeaderBar -ExpandedPart $ExpandedPart
    "$(Format-RegionHex $HeaderBar) -> $(Format-RegionHex $ExpandedPart) / $(Format-DeltaWithVerdict $deltaRgb) / $(Format-LumaDelta $deltaLuma)"
}

function Get-ScaleForEntry {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        $Window
    )

    $dpiScale = Get-PropertyValue -Object $Window -Name "DpiScale"
    if ($null -ne $dpiScale -and [double]$dpiScale -gt 0) {
        return [double]$dpiScale
    }

    $bounds = Get-PropertyValue -Object $Window -Name "Bounds"
    $windowWidth = Get-PropertyValue -Object $bounds -Name "Width"
    if ($null -ne $windowWidth -and [double]$windowWidth -gt 0) {
        return [double]$Bitmap.Width / [double]$windowWidth
    }

    return 1.0
}

function Convert-BoundsToPixels {
    param(
        $BoundsDips,
        [double]$Scale
    )

    if ($null -eq $BoundsDips) {
        return $null
    }

    [pscustomobject]@{
        Left = [int][Math]::Round([double]$BoundsDips.Left * $Scale)
        Top = [int][Math]::Round([double]$BoundsDips.Top * $Scale)
        Width = [int][Math]::Round([double]$BoundsDips.Width * $Scale)
        Height = [int][Math]::Round([double]$BoundsDips.Height * $Scale)
    }
}

function Convert-ExpanderPixelsToDips {
    param(
        $PixelBounds,
        [double]$Scale
    )

    if ($null -eq $PixelBounds) {
        return $null
    }
    if ($Scale -le 0) {
        $Scale = 1.0
    }

    [pscustomobject]@{
        Left = [Math]::Round([double]$PixelBounds.left / $Scale, 2)
        Top = [Math]::Round([double]$PixelBounds.top / $Scale, 2)
        Width = [Math]::Round([double]$PixelBounds.width / $Scale, 2)
        Height = [Math]::Round([double]$PixelBounds.headerHeight / $Scale, 2)
    }
}

function Get-FirstControlBoundsFromSummary {
    param(
        $UiSummary,
        [string[]]$AutomationIds
    )

    foreach ($automationId in @($AutomationIds)) {
        $bounds = Get-ControlBoundsFromSummary -UiSummary $UiSummary -AutomationId $automationId
        if ($null -ne $bounds) {
            return [pscustomobject]@{
                AutomationId = $automationId
                BoundsDips = $bounds
            }
        }
    }

    return $null
}

function Get-ExpanderHeaderProbeFromSummary {
    param(
        $UiSummary,
        $Descriptor
    )

    $expanderBounds = Get-ControlBoundsFromSummary -UiSummary $UiSummary -AutomationId $Descriptor.ExpanderId
    if ($null -ne $expanderBounds) {
        return [pscustomobject]@{
            Source = "summary-expander"
            DerivedFrom = $Descriptor.ExpanderId
            BoundsDips = $expanderBounds
        }
    }

    $anchor = Get-FirstControlBoundsFromSummary -UiSummary $UiSummary -AutomationIds ([string[]]$Descriptor.AnchorIds)
    if ($null -eq $anchor) {
        return $null
    }

    $anchorBounds = $anchor.BoundsDips
    [pscustomobject]@{
        Source = "summary-anchor"
        DerivedFrom = $anchor.AutomationId
        BoundsDips = [pscustomobject]@{
            Left = [Math]::Max(0.0, [double]$anchorBounds.Left - 16.0)
            Top = [Math]::Max(0.0, [double]$anchorBounds.Top - 65.0)
            Width = 796.0
            Height = 48.0
        }
    }
}

function Format-BoundsDips {
    param(
        $Bounds
    )

    if ($null -eq $Bounds) {
        return "missing"
    }

    "{0:0.#},{1:0.#} {2:0.#}x{3:0.#}" -f `
        [double]$Bounds.Left, `
        [double]$Bounds.Top, `
        [double]$Bounds.Width, `
        [double]$Bounds.Height
}

function Format-BoundsDeltaDips {
    param(
        $Reference,
        $Candidate
    )

    if ($null -eq $Reference -or $null -eq $Candidate) {
        return "missing"
    }

    "d={0:0.#},{1:0.#} {2:0.#}x{3:0.#}" -f `
        ([double]$Candidate.Left - [double]$Reference.Left), `
        ([double]$Candidate.Top - [double]$Reference.Top), `
        ([double]$Candidate.Width - [double]$Reference.Width), `
        ([double]$Candidate.Height - [double]$Reference.Height)
}

function Get-WindowSizeDips {
    param(
        $Window
    )

    $bounds = Get-PropertyValue -Object $Window -Name "Bounds"
    if ($null -eq $bounds) {
        return $null
    }

    $scale = Get-PropertyValue -Object $Window -Name "DpiScale"
    if ($null -eq $scale -or [double]$scale -le 0) {
        $scale = 1.0
    }

    [pscustomobject]@{
        Width = [Math]::Round(([double](Get-PropertyValue -Object $bounds -Name "Width") / [double]$scale), 2)
        Height = [Math]::Round(([double](Get-PropertyValue -Object $bounds -Name "Height") / [double]$scale), 2)
    }
}

function Format-SizeDips {
    param(
        $Size
    )

    if ($null -eq $Size) {
        return "missing"
    }

    "{0:0.##}x{1:0.##}" -f [double]$Size.Width, [double]$Size.Height
}

function Find-ExpandedBodyTop {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [double]$StartPercent = 0.18,
        [double]$Scale = 1.0
    )

    if ($Scale -le 0) {
        $Scale = 1.0
    }
    $sampleX = [Math]::Min([Math]::Max([int][Math]::Round(610.0 * $Scale), [int]($Bitmap.Width * 0.70)), [Math]::Max(0, $Bitmap.Width - [int][Math]::Round(180.0 * $Scale)))
    $sampleWidth = [Math]::Min([int][Math]::Round(180.0 * $Scale), $Bitmap.Width - $sampleX)
    $startY = [Math]::Max([int][Math]::Round(120.0 * $Scale), [int]($Bitmap.Height * $StartPercent))
    $sampleHeight = [int][Math]::Round(16.0 * $Scale)
    for ($y = $startY; $y -lt ($Bitmap.Height - [int][Math]::Round(160.0 * $Scale)); $y += [Math]::Max(1, [int][Math]::Round(2.0 * $Scale))) {
        $body = Measure-BitmapRegion -Bitmap $Bitmap -X $sampleX -Y $y -Width $sampleWidth -Height $sampleHeight
        $bodyMid = Measure-BitmapRegion -Bitmap $Bitmap -X $sampleX -Y ($y + [int][Math]::Round(48.0 * $Scale)) -Width $sampleWidth -Height $sampleHeight
        $bodyDeep = Measure-BitmapRegion -Bitmap $Bitmap -X $sampleX -Y ($y + [int][Math]::Round(96.0 * $Scale)) -Width $sampleWidth -Height $sampleHeight
        $header = Measure-BitmapRegion -Bitmap $Bitmap -X $sampleX -Y ($y - [int][Math]::Round(36.0 * $Scale)) -Width $sampleWidth -Height $sampleHeight
        $bodyLumas = @([double]$body.luma, [double]$bodyMid.luma, [double]$bodyDeep.luma)
        $bodyMin = ($bodyLumas | Measure-Object -Minimum).Minimum
        $bodyMax = ($bodyLumas | Measure-Object -Maximum).Maximum
        $bodyAverage = ($bodyLumas | Measure-Object -Average).Average
        if ($bodyMin -ge 238.0 -and
            $bodyMax -le 251.2 -and
            ($bodyMax - $bodyMin) -le 3.0 -and
            [double]$header.luma -ge 250.5 -and
            ([double]$header.luma - $bodyAverage) -ge 2.0) {
            return $y
        }
    }

    return $null
}

function Find-ExpandedHeaderBoundsByChevron {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [double]$Scale = 1.0
    )

    if ($Scale -le 0) {
        $Scale = 1.0
    }

    $scanLeft = [int][Math]::Round($Bitmap.Width * 0.88)
    $scanRight = [int][Math]::Round($Bitmap.Width * 0.97)
    $scanTop = [int][Math]::Round(70.0 * $Scale)
    $scanBottom = [Math]::Max($scanTop + 1, $Bitmap.Height - [int][Math]::Round(48.0 * $Scale))
    $darkLumaThreshold = 150.0
    $rows = New-Object System.Collections.Generic.List[object]

    for ($y = $scanTop; $y -lt $scanBottom; $y++) {
        [int]$count = 0
        [double]$sumX = 0.0
        [int]$minX = [int]::MaxValue
        [int]$maxX = [int]::MinValue
        for ($x = $scanLeft; $x -lt $scanRight; $x++) {
            $pixel = $Bitmap.GetPixel($x, $y)
            $luma = (0.2126 * $pixel.R) + (0.7152 * $pixel.G) + (0.0722 * $pixel.B)
            if ($luma -lt $darkLumaThreshold) {
                $count++
                $sumX += $x
                $minX = [Math]::Min($minX, $x)
                $maxX = [Math]::Max($maxX, $x)
            }
        }

        if ($count -gt 0) {
            $rows.Add([pscustomobject]@{
                y = $y
                count = $count
                sumX = $sumX
                minX = $minX
                maxX = $maxX
            }) | Out-Null
        }
    }

    $clusters = New-Object System.Collections.Generic.List[object]
    $currentRows = New-Object System.Collections.Generic.List[object]
    $previousY = $null
    foreach ($row in $rows) {
        if ($null -ne $previousY -and ([int]$row.y - [int]$previousY) -gt 1) {
            if ($currentRows.Count -gt 0) {
                $clusters.Add(@($currentRows.ToArray())) | Out-Null
            }
            $currentRows = New-Object System.Collections.Generic.List[object]
        }

        $currentRows.Add($row) | Out-Null
        $previousY = [int]$row.y
    }
    if ($currentRows.Count -gt 0) {
        $clusters.Add(@($currentRows.ToArray())) | Out-Null
    }

    $upChevrons = New-Object System.Collections.Generic.List[object]
    foreach ($cluster in $clusters) {
        $clusterRows = @($cluster)
        if ($clusterRows.Count -eq 0) {
            continue
        }

        $yStart = [int]($clusterRows | Select-Object -First 1).y
        $yEnd = [int]($clusterRows | Select-Object -Last 1).y
        $height = $yEnd - $yStart + 1
        $totalDark = [int](($clusterRows | Measure-Object -Property count -Sum).Sum)
        $minX = [int](($clusterRows | Measure-Object -Property minX -Minimum).Minimum)
        $maxX = [int](($clusterRows | Measure-Object -Property maxX -Maximum).Maximum)
        $width = $maxX - $minX + 1
        if ($height -lt 6 -or $height -gt 40 -or $width -lt 8 -or $width -gt 70 -or $totalDark -lt 18 -or $totalDark -gt 220) {
            continue
        }

        $topLimit = $yStart + [Math]::Max(1, [int][Math]::Floor($height / 3.0))
        $bottomLimit = $yEnd - [Math]::Max(1, [int][Math]::Floor($height / 3.0))
        $topRows = @($clusterRows | Where-Object { [int]$_.y -le $topLimit })
        $bottomRows = @($clusterRows | Where-Object { [int]$_.y -ge $bottomLimit })
        $topCount = [int](($topRows | Measure-Object -Property count -Sum).Sum)
        $bottomCount = [int](($bottomRows | Measure-Object -Property count -Sum).Sum)
        if ($topCount -eq 0 -or $bottomCount -eq 0) {
            continue
        }

        $topAverageX = [double](($topRows | Measure-Object -Property sumX -Sum).Sum) / [double]$topCount
        $bottomAverageX = [double](($bottomRows | Measure-Object -Property sumX -Sum).Sum) / [double]$bottomCount
        $topAverageWidth = [double](@($topRows | ForEach-Object { [int]$_.maxX - [int]$_.minX + 1 }) | Measure-Object -Average).Average
        $bottomAverageWidth = [double](@($bottomRows | ForEach-Object { [int]$_.maxX - [int]$_.minX + 1 }) | Measure-Object -Average).Average
        $looksLikeUpChevron = (($bottomAverageX - $topAverageX) -ge 1.0) -or
            (($bottomAverageWidth - $topAverageWidth) -ge 1.0)
        if (-not $looksLikeUpChevron) {
            continue
        }

        $upChevrons.Add([pscustomobject]@{
            centerY = ($yStart + $yEnd) / 2.0
            yStart = $yStart
            yEnd = $yEnd
            minX = $minX
            maxX = $maxX
            totalDark = $totalDark
        }) | Out-Null
    }

    if ($upChevrons.Count -eq 0) {
        foreach ($cluster in $clusters) {
            $clusterRows = @($cluster)
            if ($clusterRows.Count -eq 0) {
                continue
            }

            $yStart = [int]($clusterRows | Select-Object -First 1).y
            $yEnd = [int]($clusterRows | Select-Object -Last 1).y
            $height = $yEnd - $yStart + 1
            $totalDark = [int](($clusterRows | Measure-Object -Property count -Sum).Sum)
            $minX = [int](($clusterRows | Measure-Object -Property minX -Minimum).Minimum)
            $maxX = [int](($clusterRows | Measure-Object -Property maxX -Maximum).Maximum)
            $width = $maxX - $minX + 1
            if ($height -lt 6 -or $height -gt 14 -or $width -lt 8 -or $width -gt 32 -or $totalDark -lt 18 -or $totalDark -gt 140) {
                continue
            }

            $topLimit = $yStart + [Math]::Max(1, [int][Math]::Floor($height / 3.0))
            $bottomLimit = $yEnd - [Math]::Max(1, [int][Math]::Floor($height / 3.0))
            $topRows = @($clusterRows | Where-Object { [int]$_.y -le $topLimit })
            $bottomRows = @($clusterRows | Where-Object { [int]$_.y -ge $bottomLimit })
            if ($topRows.Count -eq 0 -or $bottomRows.Count -eq 0) {
                continue
            }

            $topAverageWidth = [double](@($topRows | ForEach-Object { [int]$_.maxX - [int]$_.minX + 1 }) | Measure-Object -Average).Average
            $bottomAverageWidth = [double](@($bottomRows | ForEach-Object { [int]$_.maxX - [int]$_.minX + 1 }) | Measure-Object -Average).Average
            if (($bottomAverageWidth - $topAverageWidth) -lt 1.0) {
                continue
            }

            $upChevrons.Add([pscustomobject]@{
                centerY = ($yStart + $yEnd) / 2.0
                yStart = $yStart
                yEnd = $yEnd
                minX = $minX
                maxX = $maxX
                totalDark = $totalDark
            }) | Out-Null
        }
    }

    if ($upChevrons.Count -eq 0) {
        return $null
    }

    $chevron = @($upChevrons.ToArray() | Sort-Object centerY | Select-Object -First 1)[0]
    $left = [int][Math]::Round(24.0 * $Scale)
    $width = [Math]::Min($Bitmap.Width - ($left * 2), [int][Math]::Round(796.0 * $Scale))
    if ($width -le 0) {
        return $null
    }

    [pscustomobject]@{
        Left = $left
        Top = [Math]::Max(0, [int][Math]::Round([double]$chevron.centerY - (24.0 * $Scale)))
        Width = $width
        Height = [int][Math]::Round(48.0 * $Scale)
    }
}

function Find-ExpandedHeaderBoundsByCompactChevron {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [double]$Scale = 1.0
    )

    if ($Scale -le 0) {
        $Scale = 1.0
    }

    $scanLeft = [int][Math]::Round($Bitmap.Width * 0.88)
    $scanRight = [int][Math]::Round($Bitmap.Width * 0.97)
    $scanTop = [int][Math]::Round(70.0 * $Scale)
    $scanBottom = [Math]::Max($scanTop + 1, $Bitmap.Height - [int][Math]::Round(48.0 * $Scale))
    $darkLumaThreshold = 190.0
    $rows = New-Object System.Collections.Generic.List[object]

    for ($y = $scanTop; $y -lt $scanBottom; $y++) {
        [int]$count = 0
        [double]$sumX = 0.0
        [int]$minX = [int]::MaxValue
        [int]$maxX = [int]::MinValue
        for ($x = $scanLeft; $x -lt $scanRight; $x++) {
            $pixel = $Bitmap.GetPixel($x, $y)
            $luma = (0.2126 * $pixel.R) + (0.7152 * $pixel.G) + (0.0722 * $pixel.B)
            if ($luma -lt $darkLumaThreshold) {
                $count++
                $sumX += $x
                $minX = [Math]::Min($minX, $x)
                $maxX = [Math]::Max($maxX, $x)
            }
        }

        if ($count -gt 0) {
            $rows.Add([pscustomobject]@{
                y = $y
                count = $count
                sumX = $sumX
                minX = $minX
                maxX = $maxX
            }) | Out-Null
        }
    }

    $clusters = New-Object System.Collections.Generic.List[object]
    $current = New-Object System.Collections.Generic.List[object]
    $previousY = $null
    foreach ($row in $rows) {
        if ($null -ne $previousY -and ([int]$row.y - [int]$previousY) -gt 1) {
            if ($current.Count -gt 0) {
                $clusters.Add([object[]]$current.ToArray()) | Out-Null
            }
            $current = New-Object System.Collections.Generic.List[object]
        }

        $current.Add($row) | Out-Null
        $previousY = [int]$row.y
    }
    if ($current.Count -gt 0) {
        $clusters.Add([object[]]$current.ToArray()) | Out-Null
    }

    foreach ($cluster in @($clusters.ToArray() | Sort-Object { [int]($_ | Select-Object -First 1).y })) {
        $clusterRows = @($cluster)
        if ($clusterRows.Count -eq 0) {
            continue
        }

        $yStart = [int]($clusterRows | Select-Object -First 1).y
        $yEnd = [int]($clusterRows | Select-Object -Last 1).y
        $height = $yEnd - $yStart + 1
        $totalDark = [int](($clusterRows | Measure-Object -Property count -Sum).Sum)
        $minX = [int](($clusterRows | Measure-Object -Property minX -Minimum).Minimum)
        $maxX = [int](($clusterRows | Measure-Object -Property maxX -Maximum).Maximum)
        $width = $maxX - $minX + 1
        if ($height -lt 6 -or $height -gt 14 -or $width -lt 8 -or $width -gt 32 -or $totalDark -lt 18 -or $totalDark -gt 140) {
            continue
        }

        $topLimit = $yStart + [Math]::Max(1, [int][Math]::Floor($height / 3.0))
        $bottomLimit = $yEnd - [Math]::Max(1, [int][Math]::Floor($height / 3.0))
        $topRows = @($clusterRows | Where-Object { [int]$_.y -le $topLimit })
        $bottomRows = @($clusterRows | Where-Object { [int]$_.y -ge $bottomLimit })
        if ($topRows.Count -eq 0 -or $bottomRows.Count -eq 0) {
            continue
        }

        $topAverageWidth = [double](@($topRows | ForEach-Object { [int]$_.maxX - [int]$_.minX + 1 }) | Measure-Object -Average).Average
        $bottomAverageWidth = [double](@($bottomRows | ForEach-Object { [int]$_.maxX - [int]$_.minX + 1 }) | Measure-Object -Average).Average
        if (($bottomAverageWidth - $topAverageWidth) -lt 1.0) {
            continue
        }

        $left = [int][Math]::Round(24.0 * $Scale)
        $availableWidth = $Bitmap.Width - ($left * 2)
        if ($availableWidth -le 0) {
            return $null
        }

        return [pscustomobject]@{
            Left = $left
            Top = [Math]::Max(0, [int][Math]::Round((($yStart + $yEnd) / 2.0) - (24.0 * $Scale)))
            Width = [Math]::Min($availableWidth, [int][Math]::Round(796.0 * $Scale))
            Height = [int][Math]::Round(48.0 * $Scale)
        }
    }

    return $null
}

function Measure-ExpanderRegions {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        $BoundsPixels,
        [double]$DetectionStartPercent = 0.18,
        [string]$SourceName = "bounds",
        [double]$Scale = 1.0
    )

    if ($Scale -le 0) {
        $Scale = 1.0
    }
    $headerInsetY = [int][Math]::Round(10.0 * $Scale)
    $bodyTopOffset = [int][Math]::Round(49.0 * $Scale)
    $headerHeight = [int][Math]::Round(48.0 * $Scale)
    $headerSampleHeight = [int][Math]::Round(26.0 * $Scale)
    $headerMaxWidth = [int][Math]::Round(220.0 * $Scale)
    $headerMinWidth = [int][Math]::Round(80.0 * $Scale)
    $headerRightInset = [int][Math]::Round(96.0 * $Scale)
    $headerReservedWidth = [int][Math]::Round(160.0 * $Scale)
    $bodyPreferredX = [int][Math]::Round(560.0 * $Scale)
    $bodyRightReserve = [int][Math]::Round(190.0 * $Scale)
    $bodyTrailingPadding = [int][Math]::Round(24.0 * $Scale)
    $bodyMaxWidth = [int][Math]::Round(170.0 * $Scale)
    $bodyMinWidth = [int][Math]::Round(64.0 * $Scale)
    $bodyInsetY = [int][Math]::Round(96.0 * $Scale)
    $bodySampleHeight = [int][Math]::Round(64.0 * $Scale)

    $source = $SourceName
    if ($null -ne $BoundsPixels) {
        $x = [int]$BoundsPixels.Left
        $y = [int]$BoundsPixels.Top
        $width = [int]$BoundsPixels.Width
        $headerY = $y + $headerInsetY
        $bodyTop = $y + $bodyTopOffset
    } else {
        $chevronBounds = Find-ExpandedHeaderBoundsByCompactChevron -Bitmap $Bitmap -Scale $Scale
        if ($null -eq $chevronBounds) {
            $chevronBounds = Find-ExpandedHeaderBoundsByChevron -Bitmap $Bitmap -Scale $Scale
        }
        if ($null -ne $chevronBounds) {
            $source = "detected-chevron"
            $x = [int]$chevronBounds.Left
            $y = [int]$chevronBounds.Top
            $width = [int]$chevronBounds.Width
            $headerY = $y + $headerInsetY
            $bodyTop = $y + $bodyTopOffset
        } else {
            $source = "detected"
            $bodyTop = Find-ExpandedBodyTop -Bitmap $Bitmap -StartPercent $DetectionStartPercent -Scale $Scale
            if ($null -eq $bodyTop) {
                return $null
            }
            $x = [Math]::Max([int][Math]::Round(24.0 * $Scale), [int]($Bitmap.Width * 0.03))
            $width = [Math]::Min($Bitmap.Width - ($x * 2), [int][Math]::Round(796.0 * $Scale))
            $y = [int]$bodyTop - $bodyTopOffset
            $headerY = $y + $headerInsetY
        }
    }

    $headerWidth = [Math]::Min($headerMaxWidth, [Math]::Max($headerMinWidth, $width - $headerReservedWidth))
    $headerX = $x + [Math]::Max($headerRightInset, $width - $headerWidth - $headerRightInset)
    $bodyX = $x + [Math]::Min([Math]::Max($bodyPreferredX, [int]($width * 0.70)), [Math]::Max(0, $width - $bodyRightReserve))
    $bodyWidth = [Math]::Min($bodyMaxWidth, [Math]::Max($bodyMinWidth, $width - ($bodyX - $x) - $bodyTrailingPadding))

    [pscustomobject]@{
        source = $source
        expanderBounds = [pscustomobject]@{
            left = $x
            top = $y
            width = $width
            headerHeight = $headerHeight
        }
        headerBar = Measure-BitmapRegion -Bitmap $Bitmap -X $headerX -Y $headerY -Width $headerWidth -Height $headerSampleHeight
        divider = Measure-BitmapRegion -Bitmap $Bitmap -X ($x + [int][Math]::Round(8.0 * $Scale)) -Y ([int]$bodyTop - 1) -Width ([Math]::Max(1, $width - [int][Math]::Round(16.0 * $Scale))) -Height 1
        expandedPart = Measure-BitmapRegion -Bitmap $Bitmap -X $bodyX -Y ([int]$bodyTop + $bodyInsetY) -Width $bodyWidth -Height $bodySampleHeight
    }
}

function New-ScenarioRecordFromManifest {
    param(
        $Entry
    )

    [pscustomobject]@{
        ScenarioId = [string]$Entry.ScenarioId
        ReferenceScreenshot = Join-Path $ArtifactRoot ([string]$Entry.ReferenceScreenshot)
        CandidateScreenshot = Join-Path $ArtifactRoot ([string]$Entry.CandidateScreenshot)
        ReferenceUiSummary = $Entry.ReferenceUiSummary
        CandidateUiSummary = $Entry.CandidateUiSummary
        ReferenceWindow = $Entry.ReferenceWindow
        CandidateWindow = $Entry.CandidateWindow
    }
}

function New-ScenarioRecordFromMatrix {
    param(
        $Entry
    )

    [pscustomobject]@{
        ScenarioId = [string]$Entry.scenarioId
        ReferenceScreenshot = [string]$Entry.referenceScreenshot
        CandidateScreenshot = [string]$Entry.candidateScreenshot
        ReferenceUiSummary = $null
        CandidateUiSummary = $null
        ReferenceWindow = $null
        CandidateWindow = $null
    }
}

$scenarioFilter = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($value in @($Scenario)) {
    if (-not [string]::IsNullOrWhiteSpace($value)) {
        $scenarioFilter.Add($value.Trim()) | Out-Null
    }
}

$recordsByScenario = [ordered]@{}
$manifestPath = Join-Path $ArtifactRoot "ui-parity-manifest.json"
if (Test-Path -LiteralPath $manifestPath) {
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($entry in @($manifest.Scenarios)) {
            $record = New-ScenarioRecordFromManifest -Entry $entry
            $recordsByScenario[$record.ScenarioId] = $record
        }
    } catch {
        $message = [string]$_.Exception.Message
        if ($message.Length -gt 240) {
            $message = $message.Substring(0, 240) + "..."
        }
        Write-Warning "Ignoring unreadable parity manifest ${manifestPath}: $message"
    }
}

$matrixPath = Join-Path $ArtifactRoot "rust-preview-parity-matrix.json"
if (Test-Path -LiteralPath $matrixPath) {
    $matrix = Get-Content -LiteralPath $matrixPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in @($matrix.scenarios)) {
        $scenarioId = [string]$entry.scenarioId
        if (-not $recordsByScenario.Contains($scenarioId)) {
            $recordsByScenario[$scenarioId] = New-ScenarioRecordFromMatrix -Entry $entry
        }
    }
}

$rows = New-Object System.Collections.Generic.List[object]
foreach ($record in $recordsByScenario.Values) {
    if ($scenarioFilter.Count -gt 0 -and -not $scenarioFilter.Contains($record.ScenarioId)) {
        continue
    }

    $descriptor = Get-ServiceDescriptorForScenario -ScenarioId $record.ScenarioId
    if ($null -eq $descriptor) {
        continue
    }

    $candidateBitmap = Open-Bitmap -Path $record.CandidateScreenshot
    if ($null -eq $candidateBitmap) {
        continue
    }

    $referenceBitmap = Open-Bitmap -Path $record.ReferenceScreenshot
    try {
        $candidateScale = Get-ScaleForEntry -Bitmap $candidateBitmap -Window $record.CandidateWindow
        $candidateProbe = Get-ExpanderHeaderProbeFromSummary -UiSummary $record.CandidateUiSummary -Descriptor $descriptor
        $candidateBoundsDips = if ($null -ne $candidateProbe) { $candidateProbe.BoundsDips } else { $null }
        $candidateBounds = Convert-BoundsToPixels -BoundsDips $candidateBoundsDips -Scale $candidateScale
        $candidateSource = if ($null -ne $candidateProbe) {
            [string]$candidateProbe.Source
        } else {
            "detected"
        }
        $detectionStartPercent = if (Test-ScrolledScenario -ScenarioId $record.ScenarioId) { 0.18 } else { 0.50 }
        $candidateRegions = Measure-ExpanderRegions `
            -Bitmap $candidateBitmap `
            -BoundsPixels $candidateBounds `
            -DetectionStartPercent $detectionStartPercent `
            -SourceName $candidateSource `
            -Scale $candidateScale
        if ($null -eq $candidateBoundsDips -and $null -ne $candidateRegions) {
            $candidateBoundsDips = Convert-ExpanderPixelsToDips -PixelBounds $candidateRegions.expanderBounds -Scale $candidateScale
        }

        $referenceRegions = $null
        $referenceBoundsDips = $null
        $referenceSource = "missing"
        $referenceDerivedFrom = $null
        if ($null -ne $referenceBitmap) {
            $referenceScale = Get-ScaleForEntry -Bitmap $referenceBitmap -Window $record.ReferenceWindow
            $referenceProbe = Get-ExpanderHeaderProbeFromSummary -UiSummary $record.ReferenceUiSummary -Descriptor $descriptor
            if ($null -ne $referenceProbe) {
                $referenceBoundsDips = $referenceProbe.BoundsDips
                $referenceSource = [string]$referenceProbe.Source
                $referenceDerivedFrom = [string]$referenceProbe.DerivedFrom
                $referenceBounds = if ($referenceSource -eq "summary-expander") {
                    Convert-BoundsToPixels -BoundsDips $referenceBoundsDips -Scale $referenceScale
                } else {
                    $null
                }
                $referenceRegions = Measure-ExpanderRegions `
                    -Bitmap $referenceBitmap `
                    -BoundsPixels $referenceBounds `
                    -DetectionStartPercent $detectionStartPercent `
                    -SourceName $referenceSource `
                    -Scale $referenceScale
                if ($referenceSource -ne "summary-expander" -and $null -ne $referenceRegions) {
                    $referenceSource = [string]$referenceRegions.source
                    $referenceDerivedFrom = $null
                    $referenceBoundsDips = Convert-ExpanderPixelsToDips -PixelBounds $referenceRegions.expanderBounds -Scale $referenceScale
                }
            } elseif ($null -ne $record.ReferenceUiSummary) {
                $referenceSource = "not-expanded"
            } else {
                $referenceSource = "detected"
                $referenceRegions = Measure-ExpanderRegions `
                    -Bitmap $referenceBitmap `
                    -BoundsPixels $null `
                    -DetectionStartPercent $detectionStartPercent `
                    -SourceName $referenceSource `
                    -Scale $referenceScale
                if ($null -ne $referenceRegions) {
                    $referenceBoundsDips = Convert-ExpanderPixelsToDips -PixelBounds $referenceRegions.expanderBounds -Scale $referenceScale
                }
            }
        }

        $rows.Add([pscustomobject]@{
            scenarioId = $record.ScenarioId
            service = $descriptor.Service
            serviceId = $descriptor.ServiceId
            expanderId = $descriptor.ExpanderId
            interactionState = Get-ServiceScenarioInteractionState -ScenarioId $record.ScenarioId
            hasReference = $null -ne $referenceBitmap
            referenceExpanded = $null -ne $referenceRegions
            candidateExpanded = $null -ne $candidateRegions
            candidateSource = if ($null -ne $candidateRegions) { $candidateRegions.source } else { "missing" }
            referenceSource = $referenceSource
            candidateDerivedFrom = if ($null -ne $candidateProbe) { [string]$candidateProbe.DerivedFrom } else { $null }
            referenceDerivedFrom = $referenceDerivedFrom
            candidateExpanderBoundsDips = $candidateBoundsDips
            referenceExpanderBoundsDips = $referenceBoundsDips
            candidateWindowDips = Get-WindowSizeDips -Window $record.CandidateWindow
            referenceWindowDips = Get-WindowSizeDips -Window $record.ReferenceWindow
            headerBar = [pscustomobject]@{
                reference = if ($null -ne $referenceRegions) { $referenceRegions.headerBar } else { $null }
                candidate = if ($null -ne $candidateRegions) { $candidateRegions.headerBar } else { $null }
                deltaRgb = Get-RegionDelta -Reference $(if ($null -ne $referenceRegions) { $referenceRegions.headerBar } else { $null }) -Candidate $(if ($null -ne $candidateRegions) { $candidateRegions.headerBar } else { $null })
                deltaLuma = Get-RegionLumaDelta -Reference $(if ($null -ne $referenceRegions) { $referenceRegions.headerBar } else { $null }) -Candidate $(if ($null -ne $candidateRegions) { $candidateRegions.headerBar } else { $null })
            }
            divider = [pscustomobject]@{
                reference = if ($null -ne $referenceRegions) { $referenceRegions.divider } else { $null }
                candidate = if ($null -ne $candidateRegions) { $candidateRegions.divider } else { $null }
                deltaRgb = Get-RegionDelta -Reference $(if ($null -ne $referenceRegions) { $referenceRegions.divider } else { $null }) -Candidate $(if ($null -ne $candidateRegions) { $candidateRegions.divider } else { $null })
                deltaLuma = Get-RegionLumaDelta -Reference $(if ($null -ne $referenceRegions) { $referenceRegions.divider } else { $null }) -Candidate $(if ($null -ne $candidateRegions) { $candidateRegions.divider } else { $null })
            }
            expandedPart = [pscustomobject]@{
                reference = if ($null -ne $referenceRegions) { $referenceRegions.expandedPart } else { $null }
                candidate = if ($null -ne $candidateRegions) { $candidateRegions.expandedPart } else { $null }
                deltaRgb = Get-RegionDelta -Reference $(if ($null -ne $referenceRegions) { $referenceRegions.expandedPart } else { $null }) -Candidate $(if ($null -ne $candidateRegions) { $candidateRegions.expandedPart } else { $null })
                deltaLuma = Get-RegionLumaDelta -Reference $(if ($null -ne $referenceRegions) { $referenceRegions.expandedPart } else { $null }) -Candidate $(if ($null -ne $candidateRegions) { $candidateRegions.expandedPart } else { $null })
            }
            surfacePair = [pscustomobject]@{
                referenceDeltaRgb = Get-SurfacePairDelta -HeaderBar $(if ($null -ne $referenceRegions) { $referenceRegions.headerBar } else { $null }) -ExpandedPart $(if ($null -ne $referenceRegions) { $referenceRegions.expandedPart } else { $null })
                referenceDeltaLuma = Get-SurfacePairLumaDelta -HeaderBar $(if ($null -ne $referenceRegions) { $referenceRegions.headerBar } else { $null }) -ExpandedPart $(if ($null -ne $referenceRegions) { $referenceRegions.expandedPart } else { $null })
                candidateDeltaRgb = Get-SurfacePairDelta -HeaderBar $(if ($null -ne $candidateRegions) { $candidateRegions.headerBar } else { $null }) -ExpandedPart $(if ($null -ne $candidateRegions) { $candidateRegions.expandedPart } else { $null })
                candidateDeltaLuma = Get-SurfacePairLumaDelta -HeaderBar $(if ($null -ne $candidateRegions) { $candidateRegions.headerBar } else { $null }) -ExpandedPart $(if ($null -ne $candidateRegions) { $candidateRegions.expandedPart } else { $null })
            }
        }) | Out-Null
    } finally {
        $candidateBitmap.Dispose()
        if ($null -ne $referenceBitmap) {
            $referenceBitmap.Dispose()
        }
    }
}

$scenarioRows = @($rows.ToArray())
$orderedScenarioRows = @(
    $scenarioRows |
        Sort-Object `
            @{ Expression = { Get-ServiceDisplayOrder -ServiceId $_.serviceId }; Ascending = $true },
            @{ Expression = { Get-InteractionStateDisplayOrder -State $_.interactionState }; Ascending = $true },
            scenarioId
)
$baseExpandedRows = @($orderedScenarioRows | Where-Object { $_.interactionState -eq "base" })
$referenceGapRows = @($scenarioRows | Where-Object { (-not $_.hasReference) -or (-not $_.referenceExpanded) })
$strongSampleRows = @($scenarioRows | Where-Object { Test-StrongSampleRow -Row $_ })
$chevronSampleRows = @($scenarioRows | Where-Object { (Get-SampleStrength -Row $_) -eq "chevron" })
$rankedSampleRows = @($scenarioRows | Where-Object { Test-RankedSampleRow -Row $_ })
$weakSampleRows = @($scenarioRows | Where-Object { -not (Test-RankedSampleRow -Row $_) })
$largestColorDeltas = @(
    $rankedSampleRows |
        Where-Object { $null -ne (Get-RowMaxColorDelta -Row $_) } |
        Sort-Object @{ Expression = { Get-RowMaxColorDelta -Row $_ }; Descending = $true } |
        Select-Object -First 12 |
        ForEach-Object {
            [pscustomobject]@{
                scenarioId = $_.scenarioId
                service = $_.service
                interactionState = $_.interactionState
                sampleStrength = Get-SampleStrength -Row $_
                maxDeltaRgb = Get-RowMaxColorDelta -Row $_
                headerBarDeltaRgb = $_.headerBar.deltaRgb
                dividerDeltaRgb = $_.divider.deltaRgb
                expandedPartDeltaRgb = $_.expandedPart.deltaRgb
            }
        }
)
$largestHeaderBoundsDrifts = @(
    $rankedSampleRows |
        Where-Object {
            $null -ne (Get-BoundsDriftScore -Reference $_.referenceExpanderBoundsDips -Candidate $_.candidateExpanderBoundsDips)
        } |
        Sort-Object @{ Expression = { Get-BoundsDriftScore -Reference $_.referenceExpanderBoundsDips -Candidate $_.candidateExpanderBoundsDips }; Descending = $true } |
        Select-Object -First 12 |
        ForEach-Object {
            [pscustomobject]@{
                scenarioId = $_.scenarioId
                service = $_.service
                interactionState = $_.interactionState
                driftScoreDips = Get-BoundsDriftScore -Reference $_.referenceExpanderBoundsDips -Candidate $_.candidateExpanderBoundsDips
                referenceBoundsDips = $_.referenceExpanderBoundsDips
                candidateBoundsDips = $_.candidateExpanderBoundsDips
            }
        }
)
$serviceStateCoverage = @(
    $baseExpandedRows |
        ForEach-Object {
            $serviceId = [string]$_.serviceId
            $serviceRows = @($orderedScenarioRows | Where-Object { $_.serviceId -eq $serviceId })
            $capturedStates = @($serviceRows | ForEach-Object { [string]$_.interactionState } | Sort-Object -Unique)
            $expectedStates = @("base", "hover", "pressed")
            if ($serviceId -eq "ollama") {
                $expectedStates += "mouse-hover"
            }
            $missingStates = @($expectedStates | Where-Object { $_ -notin $capturedStates })
            $referenceGapStates = @($serviceRows | Where-Object { (-not $_.hasReference) -or (-not $_.referenceExpanded) } | ForEach-Object { [string]$_.interactionState })
            [pscustomobject]@{
                service = $_.service
                serviceId = $serviceId
                expanderId = $_.expanderId
                capturedStates = @($capturedStates)
                missingStates = @($missingStates)
                referenceGapStates = @($referenceGapStates)
            }
        }
)

$summary = [pscustomobject]@{
    scenarioCount = $scenarioRows.Count
    baseExpandedScenarioCount = $baseExpandedRows.Count
    strongSampleCount = $strongSampleRows.Count
    chevronSampleCount = $chevronSampleRows.Count
    rankedSampleCount = $rankedSampleRows.Count
    weakSampleCount = $weakSampleRows.Count
    referenceExpandedCount = @($scenarioRows | Where-Object { $_.referenceExpanded }).Count
    referenceGapCount = $referenceGapRows.Count
    colorDeltaThresholds = [pscustomobject]@{
        okMaxRgb = 3.0
        watchMaxRgb = 8.0
    }
    largestColorDeltas = $largestColorDeltas
    largestHeaderBoundsDrifts = $largestHeaderBoundsDrifts
    serviceStateCoverage = $serviceStateCoverage
}

$report = [pscustomobject]@{
    schemaVersion = "easydict.settings-services-expander-colors.v6"
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    artifactRoot = $ArtifactRoot
    summary = $summary
    scenarios = $orderedScenarioRows
}
Write-JsonFile -Path $OutputJson -Value $report -Depth 12

$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add("# Settings Services Expander Color Report") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Artifact root: ``$ArtifactRoot``") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Summary: $($summary.scenarioCount) measured scenarios, $($summary.baseExpandedScenarioCount) base expanded service items, $($summary.referenceExpandedCount) expanded references, $($summary.referenceGapCount) reference gaps, $($summary.strongSampleCount) strong summary samples, $($summary.chevronSampleCount) chevron-probe samples, $($summary.weakSampleCount) weak/missing samples. Color verdict thresholds: ok <= 3 RGB, watch <= 8 RGB, drift > 8 RGB.") | Out-Null
$markdown.Add("") | Out-Null

$markdown.Add("## Service State Coverage") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Each service should normally have an expanded base capture plus header bar hover and pressed captures. Ollama also has an optional real cursor hover probe.") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("| Service | Expander | Captured states | Missing states | Reference gaps |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- |") | Out-Null
foreach ($coverage in $serviceStateCoverage) {
    $captured = if ($coverage.capturedStates.Count -eq 0) { "none" } else { $coverage.capturedStates -join ", " }
    $missing = if ($coverage.missingStates.Count -eq 0) { "none" } else { $coverage.missingStates -join ", " }
    $gaps = if ($coverage.referenceGapStates.Count -eq 0) { "none" } else { $coverage.referenceGapStates -join ", " }
    $markdown.Add("| $($coverage.service) | ``$($coverage.expanderId)`` | $captured | $missing | $gaps |") | Out-Null
}
$markdown.Add("") | Out-Null

$markdown.Add("## Reference Gaps") | Out-Null
$markdown.Add("") | Out-Null
if ($referenceGapRows.Count -eq 0) {
    $markdown.Add("No missing or collapsed .NET references were detected.") | Out-Null
} else {
    $markdown.Add("| Scenario | Service | State | Reference | Source |") | Out-Null
    $markdown.Add("| --- | --- | --- | --- | --- |") | Out-Null
    foreach ($row in $referenceGapRows) {
        $reference = if ($row.hasReference) { "not-expanded" } else { "missing" }
        $source = "ref=$($row.referenceSource), rust=$($row.candidateSource)"
        $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $($row.interactionState) | $reference | $source |") | Out-Null
    }
}
$markdown.Add("") | Out-Null

$markdown.Add("## Weak Sample Rows") | Out-Null
$markdown.Add("") | Out-Null
if ($weakSampleRows.Count -eq 0) {
    $markdown.Add("No weak samples were detected; all ranked color deltas use summary bounds or matched chevron probes.") | Out-Null
} else {
    $markdown.Add("Rows here are useful visual evidence, but their color deltas are not ranked because at least one side used generic detected or missing bounds instead of summary bounds or a chevron probe.") | Out-Null
    $markdown.Add("") | Out-Null
    $markdown.Add("| Scenario | Service | State | Reference | Source | Next action |") | Out-Null
    $markdown.Add("| --- | --- | --- | --- | --- | --- |") | Out-Null
    foreach ($row in $weakSampleRows) {
        $reference = if ($row.hasReference) {
            if ($row.referenceExpanded) { "expanded" } else { "not-expanded" }
        } else {
            "missing"
        }
        $source = "ref=$($row.referenceSource), rust=$($row.candidateSource)"
        $nextAction = if (-not $row.hasReference) {
            "capture .NET reference"
        } elseif (-not $row.referenceExpanded) {
            "refresh expanded .NET reference"
        } elseif ($row.candidateSource -eq "detected") {
            "add candidate bounds"
        } else {
            "inspect bounds"
        }
        $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $($row.interactionState) | $reference | $source | $nextAction |") | Out-Null
    }
}
$markdown.Add("") | Out-Null

$markdown.Add("## Per-Service Expanded Comparison") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Rows are ordered by the .NET Services page order. Header bar and expanded part are the decisive color surfaces; divider is reported separately later because 1px anti-aliasing makes it noisier.") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("| Service | State | Scenario | Reference | Sample | Window DIP | Header bounds DIP | Header bar | Expanded part | Rust scheme |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null
foreach ($row in $orderedScenarioRows) {
    $reference = if ($row.hasReference) {
        if ($row.referenceExpanded) {
            "expanded"
        } else {
            "not-expanded"
        }
    } else {
        "missing"
    }
    $sample = Get-SampleStrength -Row $row
    $window = "ref $(Format-SizeDips $row.referenceWindowDips) / rust $(Format-SizeDips $row.candidateWindowDips)"
    $bounds = "ref $(Format-BoundsDips $row.referenceExpanderBoundsDips) / rust $(Format-BoundsDips $row.candidateExpanderBoundsDips) / $(Format-BoundsDeltaDips -Reference $row.referenceExpanderBoundsDips -Candidate $row.candidateExpanderBoundsDips)"
    $bar = if (-not $row.hasReference) {
        "rust $(Format-RegionHex $row.headerBar.candidate)"
    } elseif (-not $row.referenceExpanded) {
        "ref missing / rust $(Format-RegionHex $row.headerBar.candidate)"
    } else {
        "ref $(Format-RegionHex $row.headerBar.reference) / rust $(Format-RegionHex $row.headerBar.candidate) / $(Format-DeltaWithVerdict $row.headerBar.deltaRgb) / $(Format-LumaDelta $row.headerBar.deltaLuma)"
    }
    $expanded = if (-not $row.hasReference) {
        "rust $(Format-RegionHex $row.expandedPart.candidate)"
    } elseif (-not $row.referenceExpanded) {
        "ref missing / rust $(Format-RegionHex $row.expandedPart.candidate)"
    } else {
        "ref $(Format-RegionHex $row.expandedPart.reference) / rust $(Format-RegionHex $row.expandedPart.candidate) / $(Format-DeltaWithVerdict $row.expandedPart.deltaRgb) / $(Format-LumaDelta $row.expandedPart.deltaLuma)"
    }
    $rustScheme = if ($row.candidateExpanded) {
        Format-SurfacePair -HeaderBar $row.headerBar.candidate -ExpandedPart $row.expandedPart.candidate
    } else {
        "missing"
    }
    $markdown.Add("| $($row.service) | $($row.interactionState) | ``$($row.scenarioId)`` | $reference | $sample | $window | $bounds | $bar | $expanded | $rustScheme |") | Out-Null
}
$markdown.Add("") | Out-Null

$markdown.Add("## Largest Color Deltas") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Rows ranked here use explicit summary bounds or matched chevron probes on both .NET reference and Rust candidate. The Sample column keeps the evidence source visible.") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("| Scenario | Service | State | Sample | Max surface RGB | Header bar | Divider | Expanded part |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null
foreach ($row in $largestColorDeltas) {
    $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $($row.interactionState) | $($row.sampleStrength) | $(Format-DeltaWithVerdict $row.maxDeltaRgb) | $(Format-DeltaWithVerdict $row.headerBarDeltaRgb) | $(Format-DeltaWithVerdict $row.dividerDeltaRgb) | $(Format-DeltaWithVerdict $row.expandedPartDeltaRgb) |") | Out-Null
}
$markdown.Add("") | Out-Null

$markdown.Add("## Base Expanded Service Items") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("| Scenario | Service | Reference | Sample | Window DIP | Header bounds DIP | Bar color | Expanded part color |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null
foreach ($row in $baseExpandedRows) {
    $reference = if ($row.hasReference) {
        if ($row.referenceExpanded) {
            "expanded"
        } else {
            "not-expanded"
        }
    } else {
        "missing"
    }
    $sample = Get-SampleStrength -Row $row
    $window = "ref $(Format-SizeDips $row.referenceWindowDips) / rust $(Format-SizeDips $row.candidateWindowDips)"
    $bounds = "ref $(Format-BoundsDips $row.referenceExpanderBoundsDips) / rust $(Format-BoundsDips $row.candidateExpanderBoundsDips) / $(Format-BoundsDeltaDips -Reference $row.referenceExpanderBoundsDips -Candidate $row.candidateExpanderBoundsDips)"
    $bar = "ref $(Format-RegionHex $row.headerBar.reference) / rust $(Format-RegionHex $row.headerBar.candidate) / $(Format-DeltaWithVerdict $row.headerBar.deltaRgb) / $(Format-LumaDelta $row.headerBar.deltaLuma)"
    $expanded = "ref $(Format-RegionHex $row.expandedPart.reference) / rust $(Format-RegionHex $row.expandedPart.candidate) / $(Format-DeltaWithVerdict $row.expandedPart.deltaRgb) / $(Format-LumaDelta $row.expandedPart.deltaLuma)"
    if (-not $row.hasReference) {
        $bar = "rust $(Format-RegionHex $row.headerBar.candidate)"
        $expanded = "rust $(Format-RegionHex $row.expandedPart.candidate)"
    } elseif (-not $row.referenceExpanded) {
        $bar = "ref missing / rust $(Format-RegionHex $row.headerBar.candidate)"
        $expanded = "ref missing / rust $(Format-RegionHex $row.expandedPart.candidate)"
    }
    $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $reference | $sample | $window | $bounds | $bar | $expanded |") | Out-Null
}
$markdown.Add("") | Out-Null

$markdown.Add("## Bar / Expanded Surface Pairing") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Each pair is sampled as header bar -> expanded part so the expander color scheme is visible per service. The pair delta describes internal surface separation; the Cross-surface delta column is the .NET vs Rust mismatch signal.") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("| Scenario | Service | Reference scheme | Rust scheme | Cross-surface delta |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- |") | Out-Null
foreach ($row in $baseExpandedRows) {
    $referenceScheme = if ($row.referenceExpanded) {
        Format-SurfacePair -HeaderBar $row.headerBar.reference -ExpandedPart $row.expandedPart.reference
    } else {
        "missing"
    }
    $candidateScheme = if ($row.candidateExpanded) {
        Format-SurfacePair -HeaderBar $row.headerBar.candidate -ExpandedPart $row.expandedPart.candidate
    } else {
        "missing"
    }
    $crossDelta = if ($row.referenceExpanded -and $row.candidateExpanded) {
        "bar $(Format-DeltaWithVerdict $row.headerBar.deltaRgb), expanded $(Format-DeltaWithVerdict $row.expandedPart.deltaRgb)"
    } elseif ($row.candidateExpanded) {
        "rust only"
    } else {
        "missing"
    }
    $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $referenceScheme | $candidateScheme | $crossDelta |") | Out-Null
}
$markdown.Add("") | Out-Null

$markdown.Add("## Ranked Header Bounds Drift") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Only strong summary samples or matched chevron probes are ranked here. Weak detected rows still appear in All Measurements, but their position deltas should be treated as evidence-quality gaps before UI bugs.") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("| Scenario | Service | State | Drift score DIP | Header bounds DIP |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- |") | Out-Null
foreach ($row in $largestHeaderBoundsDrifts) {
    $bounds = "ref $(Format-BoundsDips $row.referenceBoundsDips) / rust $(Format-BoundsDips $row.candidateBoundsDips) / $(Format-BoundsDeltaDips -Reference $row.referenceBoundsDips -Candidate $row.candidateBoundsDips)"
    $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $($row.interactionState) | $($row.driftScoreDips) | $bounds |") | Out-Null
}
$markdown.Add("") | Out-Null

$markdown.Add("## All Measurements") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("| Scenario | Service | State | Reference | Window DIP | Header bounds DIP | Source | Header bar | Divider | Expanded part |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null
foreach ($row in $orderedScenarioRows) {
    $reference = if ($row.hasReference) {
        if ($row.referenceExpanded) {
            "expanded"
        } else {
            "not-expanded"
        }
    } else {
        "missing"
    }
    $window = "ref $(Format-SizeDips $row.referenceWindowDips) / rust $(Format-SizeDips $row.candidateWindowDips)"
    $bounds = "ref $(Format-BoundsDips $row.referenceExpanderBoundsDips) / rust $(Format-BoundsDips $row.candidateExpanderBoundsDips) / $(Format-BoundsDeltaDips -Reference $row.referenceExpanderBoundsDips -Candidate $row.candidateExpanderBoundsDips)"
    $source = "ref=$($row.referenceSource)"
    if (-not [string]::IsNullOrWhiteSpace([string]$row.referenceDerivedFrom)) {
        $source += "($($row.referenceDerivedFrom))"
    }
    $source += ", rust=$($row.candidateSource)"
    if (-not [string]::IsNullOrWhiteSpace([string]$row.candidateDerivedFrom)) {
        $source += "($($row.candidateDerivedFrom))"
    }
    $header = if ($null -ne $row.headerBar.candidate) {
        "ref $(Format-RegionHex $row.headerBar.reference) / rust $(Format-RegionHex $row.headerBar.candidate) / $(Format-DeltaWithVerdict $row.headerBar.deltaRgb) / $(Format-LumaDelta $row.headerBar.deltaLuma)"
    } else {
        "n/a"
    }
    $divider = if ($null -ne $row.divider.candidate) {
        "ref $(Format-RegionHex $row.divider.reference) / rust $(Format-RegionHex $row.divider.candidate) / $(Format-DeltaWithVerdict $row.divider.deltaRgb) / $(Format-LumaDelta $row.divider.deltaLuma)"
    } else {
        "n/a"
    }
    $expanded = if ($null -ne $row.expandedPart.candidate) {
        "ref $(Format-RegionHex $row.expandedPart.reference) / rust $(Format-RegionHex $row.expandedPart.candidate) / $(Format-DeltaWithVerdict $row.expandedPart.deltaRgb) / $(Format-LumaDelta $row.expandedPart.deltaLuma)"
    } else {
        "n/a"
    }
    if (-not $row.hasReference) {
        $header = if ($null -ne $row.headerBar.candidate) { "rust $(Format-RegionHex $row.headerBar.candidate)" } else { "n/a" }
        $divider = if ($null -ne $row.divider.candidate) { "rust $(Format-RegionHex $row.divider.candidate)" } else { "n/a" }
        $expanded = if ($null -ne $row.expandedPart.candidate) { "rust $(Format-RegionHex $row.expandedPart.candidate)" } else { "n/a" }
    } elseif (-not $row.referenceExpanded) {
        $header = if ($null -ne $row.headerBar.candidate) { "ref missing / rust $(Format-RegionHex $row.headerBar.candidate)" } else { "n/a" }
        $divider = if ($null -ne $row.divider.candidate) { "ref missing / rust $(Format-RegionHex $row.divider.candidate)" } else { "n/a" }
        $expanded = if ($null -ne $row.expandedPart.candidate) { "ref missing / rust $(Format-RegionHex $row.expandedPart.candidate)" } else { "n/a" }
    }
    $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $($row.interactionState) | $reference | $window | $bounds | $source | $header | $divider | $expanded |") | Out-Null
}
$markdown | Set-Content -LiteralPath $OutputMarkdown -Encoding utf8

Write-Host "Color report JSON: $OutputJson"
Write-Host "Color report Markdown: $OutputMarkdown"

[CmdletBinding()]
param(
    [string]$ArtifactRoot,
    [string[]]$Scenario = @(),
    [string]$OutputJson,
    [string]$OutputMarkdown,
    [double]$MaxSurfaceDeltaRgb = 3.0,
    [double]$MaxBoundsDriftDips = 0.5,
    [switch]$UseSummaryBounds,
    [switch]$ValidateVisibleExpanderBounds,
    [switch]$InferImageExpandedBodyBounds,
    [switch]$FailOnSurfaceDrift
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
        @{ Scenario = "parity-settings-services-volcano-expanded-scroll-100-percent"; Service = "Volcano"; ServiceId = "volcano"; ExpanderId = "VolcanoServiceExpander"; AnchorIds = @("VolcanoAccessKeyIdHeaderText", "VolcanoAccessKeyIdBox"); DotnetReferenceExpected = $false }
    )

    foreach ($descriptor in $descriptors) {
        if ($descriptor.Scenario -eq $normalized) {
            return [pscustomobject]$descriptor
        }
    }

    return $null
}

function Test-DotnetReferenceExpected {
    param(
        $DescriptorOrRow
    )

    if ($null -eq $DescriptorOrRow) {
        return $true
    }

    $property = $DescriptorOrRow.PSObject.Properties["DotnetReferenceExpected"]
    if ($null -eq $property) {
        return $true
    }

    return [bool]$property.Value
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
    $colorCounts = @{}
    for ($y = $top; $y -lt $bottom; $y++) {
        for ($x = $left; $x -lt $right; $x++) {
            $pixel = $Bitmap.GetPixel($x, $y)
            $red += $pixel.R
            $green += $pixel.G
            $blue += $pixel.B
            $key = ([int]$pixel.R -shl 16) -bor ([int]$pixel.G -shl 8) -bor [int]$pixel.B
            if ($colorCounts.ContainsKey($key)) {
                $colorCounts[$key] = [int]$colorCounts[$key] + 1
            } else {
                $colorCounts[$key] = 1
            }
            $count++
        }
    }

    $r = if ($count -eq 0) { 0.0 } else { $red / $count }
    $g = if ($count -eq 0) { 0.0 } else { $green / $count }
    $b = if ($count -eq 0) { 0.0 } else { $blue / $count }
    $luma = (0.2126 * $r) + (0.7152 * $g) + (0.0722 * $b)
    [int]$surfaceKey = 0
    [int]$surfaceCount = 0
    foreach ($entry in $colorCounts.GetEnumerator()) {
        if ([int]$entry.Value -gt $surfaceCount) {
            $surfaceKey = [int]$entry.Key
            $surfaceCount = [int]$entry.Value
        }
    }
    $surfaceR = ($surfaceKey -shr 16) -band 0xFF
    $surfaceG = ($surfaceKey -shr 8) -band 0xFF
    $surfaceB = $surfaceKey -band 0xFF
    $surfaceLuma = (0.2126 * $surfaceR) + (0.7152 * $surfaceG) + (0.0722 * $surfaceB)
    $surfaceRatio = if ($count -eq 0) { 0.0 } else { [double]$surfaceCount / [double]$count }

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
        surfaceRgb = [pscustomobject]@{
            r = [Math]::Round($surfaceR, 2)
            g = [Math]::Round($surfaceG, 2)
            b = [Math]::Round($surfaceB, 2)
        }
        surfaceHex = "#{0:X2}{1:X2}{2:X2}" -f $surfaceR, $surfaceG, $surfaceB
        surfaceLuma = [Math]::Round($surfaceLuma, 2)
        surfaceSampleCount = $surfaceCount
        surfaceSampleRatio = [Math]::Round($surfaceRatio, 4)
    }
}

function Measure-ExpandedPartSurface {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Bitmap]$Bitmap,

        [int]$ExpanderX,
        [int]$ExpanderWidth,
        [int]$BodyTop,
        [double]$Scale = 1.0
    )

    if ($Scale -le 0) {
        $Scale = 1.0
    }

    $sampleWidth = [int][Math]::Round(64.0 * $Scale)
    $sampleHeight = [int][Math]::Round(14.0 * $Scale)
    $sampleWidth = [Math]::Max(16, $sampleWidth)
    $sampleHeight = [Math]::Max(8, $sampleHeight)
    $candidateXOffsets = @(
        [int][Math]::Round(32.0 * $Scale),
        [int][Math]::Round([double]$ExpanderWidth * 0.30),
        [int][Math]::Round([double]$ExpanderWidth * 0.54),
        [int][Math]::Round([double]$ExpanderWidth * 0.72)
    )
    $candidateYOffsets = @(24, 64, 104, 160, 220, 280)
    $candidates = New-Object System.Collections.Generic.List[object]

    foreach ($offsetDip in $candidateYOffsets) {
        $y = [int]$BodyTop + [int][Math]::Round([double]$offsetDip * $Scale)
        if ($y -ge ($Bitmap.Height - $sampleHeight)) {
            continue
        }

        foreach ($xOffset in $candidateXOffsets) {
            $maxX = [Math]::Max(0, $Bitmap.Width - $sampleWidth - 1)
            $x = [Math]::Min($maxX, [Math]::Max(0, [int]$ExpanderX + [int]$xOffset))
            $region = Measure-BitmapRegion -Bitmap $Bitmap -X $x -Y $y -Width $sampleWidth -Height $sampleHeight
            $surfaceLuma = [double](Get-RegionSurfaceLuma -Region $region)
            $surfaceRatio = [double](Get-PropertyValue -Object $region -Name "surfaceSampleRatio")

            # The light WinUI expanded surface is visibly below the header luma
            # but above text/border/input chrome. Prefer stable blank surface
            # samples over large regions dominated by child controls.
            if ($surfaceLuma -lt 238.0 -or $surfaceLuma -gt 251.2 -or $surfaceRatio -lt 0.45) {
                continue
            }

            $targetLuma = 246.93
            $score = [Math]::Abs($surfaceLuma - $targetLuma) + ((1.0 - $surfaceRatio) * 3.0)
            $candidates.Add([pscustomobject]@{
                score = $score
                luma = $surfaceLuma
                ratio = $surfaceRatio
                region = $region
            }) | Out-Null
        }
    }

    if ($candidates.Count -gt 0) {
        return @($candidates.ToArray() | Sort-Object score, luma | Select-Object -First 1)[0].region
    }

    return $null
}

function Measure-HeaderBarSurface {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Bitmap]$Bitmap,

        [int]$ExpanderX,
        [int]$ExpanderWidth,
        [int]$HeaderTop,
        [double]$Scale = 1.0
    )

    if ($Scale -le 0) {
        $Scale = 1.0
    }

    $sampleWidth = [int][Math]::Round(84.0 * $Scale)
    $sampleHeight = [int][Math]::Round(8.0 * $Scale)
    $sampleWidth = [Math]::Max(24, $sampleWidth)
    $sampleHeight = [Math]::Max(6, $sampleHeight)
    $candidateXOffsets = @(
        [int][Math]::Round([double]$ExpanderWidth * 0.48),
        [int][Math]::Round([double]$ExpanderWidth * 0.62),
        [int][Math]::Round([double]$ExpanderWidth * 0.76)
    )
    $candidateYOffsets = @(6, 11, 32, 37)
    $candidates = New-Object System.Collections.Generic.List[object]

    foreach ($offsetDip in $candidateYOffsets) {
        $y = [int]$HeaderTop + [int][Math]::Round([double]$offsetDip * $Scale)
        if ($y -ge ($Bitmap.Height - $sampleHeight)) {
            continue
        }

        foreach ($xOffset in $candidateXOffsets) {
            $maxX = [Math]::Max(0, $Bitmap.Width - $sampleWidth - 1)
            $x = [Math]::Min($maxX, [Math]::Max(0, [int]$ExpanderX + [int]$xOffset))
            $region = Measure-BitmapRegion -Bitmap $Bitmap -X $x -Y $y -Width $sampleWidth -Height $sampleHeight
            $surfaceLuma = [double](Get-RegionSurfaceLuma -Region $region)
            $surfaceRatio = [double](Get-PropertyValue -Object $region -Name "surfaceSampleRatio")

            # WinUI hover/pressed can draw a narrow feedback band through the
            # header. The bar color comparison should sample the stable header
            # surface, not that transient interaction stripe.
            if ($surfaceLuma -lt 250.5 -or $surfaceLuma -gt 255.0 -or $surfaceRatio -lt 0.65) {
                continue
            }

            $targetLuma = 253.07
            $score = [Math]::Abs($surfaceLuma - $targetLuma) + ((1.0 - $surfaceRatio) * 3.0)
            $candidates.Add([pscustomobject]@{
                score = $score
                luma = $surfaceLuma
                ratio = $surfaceRatio
                region = $region
            }) | Out-Null
        }
    }

    if ($candidates.Count -gt 0) {
        return @($candidates.ToArray() | Sort-Object score, luma | Select-Object -First 1)[0].region
    }

    return $null
}

function Get-RegionSurfaceRgb {
    param(
        $Region
    )

    if ($null -eq $Region) {
        return $null
    }

    $surfaceRgb = Get-PropertyValue -Object $Region -Name "surfaceRgb"
    if ($null -ne $surfaceRgb) {
        return $surfaceRgb
    }

    return Get-PropertyValue -Object $Region -Name "rgb"
}

function Get-RegionSurfaceLuma {
    param(
        $Region
    )

    if ($null -eq $Region) {
        return $null
    }

    $surfaceLuma = Get-PropertyValue -Object $Region -Name "surfaceLuma"
    if ($null -ne $surfaceLuma) {
        return $surfaceLuma
    }

    return Get-PropertyValue -Object $Region -Name "luma"
}

function Get-RegionDelta {
    param(
        $Reference,
        $Candidate
    )

    if ($null -eq $Reference -or $null -eq $Candidate) {
        return $null
    }

    $referenceRgb = Get-RegionSurfaceRgb -Region $Reference
    $candidateRgb = Get-RegionSurfaceRgb -Region $Candidate
    if ($null -eq $referenceRgb -or $null -eq $candidateRgb) {
        return $null
    }

    $dr = [double]$referenceRgb.r - [double]$candidateRgb.r
    $dg = [double]$referenceRgb.g - [double]$candidateRgb.g
    $db = [double]$referenceRgb.b - [double]$candidateRgb.b
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

    $referenceLuma = Get-RegionSurfaceLuma -Region $Reference
    $candidateLuma = Get-RegionSurfaceLuma -Region $Candidate
    if ($null -eq $referenceLuma -or $null -eq $candidateLuma) {
        return $null
    }

    [Math]::Round([double]$candidateLuma - [double]$referenceLuma, 2)
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

function Get-SizeDriftScore {
    param(
        $Reference,
        $Candidate
    )

    if ($null -eq $Reference -or $null -eq $Candidate) {
        return $null
    }

    $dw = [double]$Candidate.Width - [double]$Reference.Width
    $dh = [double]$Candidate.Height - [double]$Reference.Height
    [Math]::Round([Math]::Sqrt(($dw * $dw) + ($dh * $dh)), 2)
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

function Test-ImageSampleSource {
    param(
        [string]$Source
    )

    if ([string]::IsNullOrWhiteSpace($Source)) {
        return $false
    }

    return $Source.StartsWith("summary-", [System.StringComparison]::OrdinalIgnoreCase) -or
        $Source.Equals("detected", [System.StringComparison]::OrdinalIgnoreCase) -or
        $Source.Equals("detected-chevron", [System.StringComparison]::OrdinalIgnoreCase)
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

    if ((Test-ImageSampleSource -Source $referenceSource) -and
        (Test-ImageSampleSource -Source $candidateSource)) {
        return "image"
    }

    return "weak"
}

function Test-RankedSampleRow {
    param(
        $Row
    )

    $strength = Get-SampleStrength -Row $Row
    return $strength -eq "strong" -or $strength -eq "chevron" -or $strength -eq "image"
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

function Format-PlainDeltaWithVerdict {
    param(
        $Delta
    )

    if ($null -eq $Delta) {
        return "missing"
    }

    "{0:0.##} ({1})" -f [double]$Delta, (Format-DeltaVerdict -Delta $Delta)
}

function Test-ScrolledScenario {
    param(
        [string]$ScenarioId
    )

    return -not [string]::IsNullOrWhiteSpace($ScenarioId) -and
        $ScenarioId.IndexOf("-scroll-", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-ExpandedBodyGeometryEvidence {
    param(
        $Row
    )

    if ($null -eq $Row) {
        return [pscustomobject]@{
            Kind = "diagnostic"
            Reason = "missing row"
        }
    }

    if ([bool]$Row.referenceExpandedBodyBoundsInferred -or
        [bool]$Row.candidateExpandedBodyBoundsInferred) {
        $reason = "image-inferred visible body bounds"
        if (Test-ScrolledScenario -ScenarioId ([string]$Row.scenarioId)) {
            $reason = "$reason; scrolled viewport may clip the full body"
        }
        return [pscustomobject]@{
            Kind = "diagnostic"
            Reason = $reason
        }
    }

    if ($null -eq $Row.referenceExpandedBodyBoundsDips -or
        $null -eq $Row.candidateExpandedBodyBoundsDips) {
        return [pscustomobject]@{
            Kind = "diagnostic"
            Reason = "missing expanded body bounds"
        }
    }

    $sampleStrength = Get-SampleStrength -Row $Row
    if ($sampleStrength -eq "strong" -or $sampleStrength -eq "chevron") {
        return [pscustomobject]@{
            Kind = "verified"
            Reason = "non-inferred summary/full expander bounds"
        }
    }

    [pscustomobject]@{
        Kind = "diagnostic"
        Reason = "image-detected bounds sample"
    }
}

function Format-RegionHex {
    param(
        $Region
    )

    if ($null -eq $Region) {
        return "missing"
    }

    $hex = Get-PropertyValue -Object $Region -Name "surfaceHex"
    if ([string]::IsNullOrWhiteSpace([string]$hex)) {
        $hex = Get-PropertyValue -Object $Region -Name "hex"
    }
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
    if ($null -eq $deltaRgb) {
        return "$(Format-RegionHex $HeaderBar) -> $(Format-RegionHex $ExpandedPart) / separation missing / $(Format-LumaDelta $deltaLuma)"
    }

    "$(Format-RegionHex $HeaderBar) -> $(Format-RegionHex $ExpandedPart) / separation={0:0.##} RGB / $(Format-LumaDelta $deltaLuma)" -f [double]$deltaRgb
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

function Convert-FullExpanderPixelsToDips {
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

    $fullHeight = Get-PropertyValue -Object $PixelBounds -Name "fullHeight"
    if ($null -eq $fullHeight) {
        return $null
    }

    [pscustomobject]@{
        Left = [Math]::Round([double]$PixelBounds.left / $Scale, 2)
        Top = [Math]::Round([double]$PixelBounds.top / $Scale, 2)
        Width = [Math]::Round([double]$PixelBounds.width / $Scale, 2)
        Height = [Math]::Round([double]$fullHeight / $Scale, 2)
    }
}

function New-ExpandedBodyBoundsDips {
    param(
        $FullExpanderBoundsDips
    )

    if ($null -eq $FullExpanderBoundsDips) {
        return $null
    }

    $fullHeight = [double]$FullExpanderBoundsDips.Height
    $bodyTopOffset = 49.0
    $bodyHeight = $fullHeight - $bodyTopOffset
    if ($bodyHeight -le 0.0) {
        return $null
    }

    [pscustomobject]@{
        Left = [double]$FullExpanderBoundsDips.Left
        Top = [Math]::Round([double]$FullExpanderBoundsDips.Top + $bodyTopOffset, 2)
        Width = [double]$FullExpanderBoundsDips.Width
        Height = [Math]::Round($bodyHeight, 2)
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
        $fullBoundsDips = [pscustomobject]@{
            Left = [double]$expanderBounds.Left
            Top = [double]$expanderBounds.Top
            Width = [double]$expanderBounds.Width
            Height = [double]$expanderBounds.Height
        }
        return [pscustomobject]@{
            Source = "summary-expander"
            DerivedFrom = $Descriptor.ExpanderId
            BoundsDips = [pscustomobject]@{
                Left = [double]$expanderBounds.Left
                Top = [double]$expanderBounds.Top
                Width = [double]$expanderBounds.Width
                Height = [Math]::Min(48.0, [double]$expanderBounds.Height)
            }
            FullBoundsDips = $fullBoundsDips
            ExpandedBodyBoundsDips = New-ExpandedBodyBoundsDips -FullExpanderBoundsDips $fullBoundsDips
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
        FullBoundsDips = $null
        ExpandedBodyBoundsDips = $null
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

function Test-ExpanderHeaderChevronAtBounds {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Bitmap]$Bitmap,

        $BoundsPixels,

        [double]$Scale = 1.0
    )

    if ($null -eq $BoundsPixels) {
        return $false
    }
    if ($Scale -le 0) {
        $Scale = 1.0
    }

    $left = [int]$BoundsPixels.Left
    $top = [int]$BoundsPixels.Top
    $width = [int]$BoundsPixels.Width
    $height = [int]$BoundsPixels.Height
    if ($width -le 0 -or $height -le 0) {
        return $false
    }

    $scanLeft = [Math]::Max(0, $left + $width - [int][Math]::Round(88.0 * $Scale))
    $scanRight = [Math]::Min($Bitmap.Width, $left + $width - [int][Math]::Round(8.0 * $Scale))
    $scanTop = [Math]::Max(0, $top + [int][Math]::Round(8.0 * $Scale))
    $scanBottom = [Math]::Min($Bitmap.Height, $top + [int][Math]::Round(46.0 * $Scale))
    if ($scanRight -le $scanLeft -or $scanBottom -le $scanTop) {
        return $false
    }

    [int]$darkCount = 0
    [int]$minX = [int]::MaxValue
    [int]$maxX = [int]::MinValue
    [int]$minY = [int]::MaxValue
    [int]$maxY = [int]::MinValue
    for ($y = $scanTop; $y -lt $scanBottom; $y++) {
        for ($x = $scanLeft; $x -lt $scanRight; $x++) {
            $pixel = $Bitmap.GetPixel($x, $y)
            $luma = (0.2126 * $pixel.R) + (0.7152 * $pixel.G) + (0.0722 * $pixel.B)
            if ($luma -lt 170.0) {
                $darkCount++
                $minX = [Math]::Min($minX, $x)
                $maxX = [Math]::Max($maxX, $x)
                $minY = [Math]::Min($minY, $y)
                $maxY = [Math]::Max($maxY, $y)
            }
        }
    }

    return $darkCount -ge [Math]::Max(8, [int][Math]::Round(8.0 * $Scale))
}

function Find-ExpandedBodyBottom {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Bitmap]$Bitmap,

        [int]$ExpanderX,
        [int]$ExpanderWidth,
        [int]$BodyTop,
        [double]$Scale = 1.0
    )

    if ($Scale -le 0) {
        $Scale = 1.0
    }

    $sampleWidth = [int][Math]::Round(56.0 * $Scale)
    $sampleWidth = [Math]::Max(18, $sampleWidth)
    $sampleHeight = [int][Math]::Round(6.0 * $Scale)
    $sampleHeight = [Math]::Max(3, $sampleHeight)
    $sampleX = [Math]::Min(
        [Math]::Max(0, $Bitmap.Width - $sampleWidth - 1),
        [Math]::Max(0, $ExpanderX + $ExpanderWidth - [int][Math]::Round(180.0 * $Scale))
    )
    $startY = $BodyTop + [int][Math]::Round(12.0 * $Scale)
    $minBodyHeight = [int][Math]::Round(64.0 * $Scale)
    $lastBodyY = $null
    $misses = 0

    for ($y = $startY; $y -lt ($Bitmap.Height - $sampleHeight); $y += [Math]::Max(1, [int][Math]::Round(3.0 * $Scale))) {
        $region = Measure-BitmapRegion -Bitmap $Bitmap -X $sampleX -Y $y -Width $sampleWidth -Height $sampleHeight
        $surfaceLuma = [double](Get-RegionSurfaceLuma -Region $region)
        $surfaceRatio = [double](Get-PropertyValue -Object $region -Name "surfaceSampleRatio")
        $isBodySurface = $surfaceLuma -ge 238.0 -and $surfaceLuma -le 251.2 -and $surfaceRatio -ge 0.35

        if ($isBodySurface) {
            $lastBodyY = $y
            $misses = 0
            continue
        }

        if ($null -ne $lastBodyY -and ($y - $BodyTop) -ge $minBodyHeight) {
            $misses++
            if ($misses -ge 3) {
                return [Math]::Min($Bitmap.Height, [int]$lastBodyY + $sampleHeight)
            }
        }
    }

    if ($null -eq $lastBodyY) {
        return $null
    }

    return [Math]::Min($Bitmap.Height, [int]$lastBodyY + $sampleHeight)
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
        if ($height -lt 4 -or $height -gt 14 -or $width -lt 4 -or $width -gt 32 -or $totalDark -lt 10 -or $totalDark -gt 140) {
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
        [double]$Scale = 1.0,
        [switch]$ValidateBoundsVisual,
        [switch]$InferBodyBounds
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
        if ($ValidateBoundsVisual -and -not (Test-ExpanderHeaderChevronAtBounds -Bitmap $Bitmap -BoundsPixels $BoundsPixels -Scale $Scale)) {
            return $null
        }
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
    $expandedPart = Measure-ExpandedPartSurface `
        -Bitmap $Bitmap `
        -ExpanderX $x `
        -ExpanderWidth $width `
        -BodyTop ([int]$bodyTop) `
        -Scale $Scale
    if ($null -eq $expandedPart) {
        $expandedPart = Measure-BitmapRegion -Bitmap $Bitmap -X $bodyX -Y ([int]$bodyTop + $bodyInsetY) -Width $bodyWidth -Height $bodySampleHeight
    }

    $headerBar = Measure-HeaderBarSurface `
        -Bitmap $Bitmap `
        -ExpanderX $x `
        -ExpanderWidth $width `
        -HeaderTop $y `
        -Scale $Scale
    if ($null -eq $headerBar) {
        $headerBar = Measure-BitmapRegion -Bitmap $Bitmap -X $headerX -Y $headerY -Width $headerWidth -Height $headerSampleHeight
    }

    $bodyBottom = if ($InferBodyBounds) {
        Find-ExpandedBodyBottom `
            -Bitmap $Bitmap `
            -ExpanderX $x `
            -ExpanderWidth $width `
            -BodyTop ([int]$bodyTop) `
            -Scale $Scale
    } else {
        $null
    }
    $fullHeight = if ($null -ne $bodyBottom -and [int]$bodyBottom -gt $y) {
        [int]$bodyBottom - $y
    } else {
        $null
    }

    [pscustomobject]@{
        source = $source
        expanderBounds = [pscustomobject]@{
            left = $x
            top = $y
            width = $width
            headerHeight = $headerHeight
            bodyTop = $bodyTop
            fullHeight = $fullHeight
        }
        headerBar = $headerBar
        divider = Measure-BitmapRegion -Bitmap $Bitmap -X ($x + [int][Math]::Round(8.0 * $Scale)) -Y ([int]$bodyTop - 1) -Width ([Math]::Max(1, $width - [int][Math]::Round(16.0 * $Scale))) -Height 1
        expandedPart = $expandedPart
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
        $candidateProbe = if ($UseSummaryBounds) {
            Get-ExpanderHeaderProbeFromSummary -UiSummary $record.CandidateUiSummary -Descriptor $descriptor
        } else {
            $null
        }
        $candidateBoundsDips = if ($null -ne $candidateProbe) { $candidateProbe.BoundsDips } else { $null }
        $candidateFullBoundsDips = if ($null -ne $candidateProbe) { $candidateProbe.FullBoundsDips } else { $null }
        $candidateBodyBoundsDips = if ($null -ne $candidateProbe) { $candidateProbe.ExpandedBodyBoundsDips } else { $null }
        $candidateBodyBoundsInferred = $false
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
            -Scale $candidateScale `
            -ValidateBoundsVisual:$ValidateVisibleExpanderBounds `
            -InferBodyBounds:$InferImageExpandedBodyBounds
        if ($null -eq $candidateBoundsDips -and $null -ne $candidateRegions) {
            $candidateBoundsDips = Convert-ExpanderPixelsToDips -PixelBounds $candidateRegions.expanderBounds -Scale $candidateScale
        }
        if ($InferImageExpandedBodyBounds -and
            $null -ne $candidateRegions -and
            ($null -eq $candidateBodyBoundsDips -or
                ($null -ne $candidateFullBoundsDips -and [double]$candidateFullBoundsDips.Height -le 49.0))) {
            $imageFullBoundsDips = Convert-FullExpanderPixelsToDips -PixelBounds $candidateRegions.expanderBounds -Scale $candidateScale
            if ($null -ne $imageFullBoundsDips) {
                $candidateFullBoundsDips = $imageFullBoundsDips
                $candidateBodyBoundsDips = New-ExpandedBodyBoundsDips -FullExpanderBoundsDips $candidateFullBoundsDips
                $candidateBodyBoundsInferred = $true
            }
        }

        $referenceRegions = $null
        $referenceBoundsDips = $null
        $referenceFullBoundsDips = $null
        $referenceBodyBoundsDips = $null
        $referenceBodyBoundsInferred = $false
        $referenceSource = "missing"
        $referenceDerivedFrom = $null
        if ($null -ne $referenceBitmap) {
            $referenceScale = Get-ScaleForEntry -Bitmap $referenceBitmap -Window $record.ReferenceWindow
            $referenceProbe = if ($UseSummaryBounds) {
                Get-ExpanderHeaderProbeFromSummary -UiSummary $record.ReferenceUiSummary -Descriptor $descriptor
            } else {
                $null
            }
            if ($null -ne $referenceProbe) {
                $referenceBoundsDips = $referenceProbe.BoundsDips
                $referenceFullBoundsDips = $referenceProbe.FullBoundsDips
                $referenceBodyBoundsDips = $referenceProbe.ExpandedBodyBoundsDips
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
                    -Scale $referenceScale `
                    -ValidateBoundsVisual:$ValidateVisibleExpanderBounds `
                    -InferBodyBounds:$InferImageExpandedBodyBounds
                if ($referenceSource -ne "summary-expander" -and $null -ne $referenceRegions) {
                    $referenceSource = [string]$referenceRegions.source
                    $referenceDerivedFrom = $null
                    $referenceBoundsDips = Convert-ExpanderPixelsToDips -PixelBounds $referenceRegions.expanderBounds -Scale $referenceScale
                }
                if ($InferImageExpandedBodyBounds -and
                    $null -ne $referenceRegions -and
                    ($null -eq $referenceBodyBoundsDips -or
                        ($null -ne $referenceFullBoundsDips -and [double]$referenceFullBoundsDips.Height -le 49.0))) {
                    $imageFullBoundsDips = Convert-FullExpanderPixelsToDips -PixelBounds $referenceRegions.expanderBounds -Scale $referenceScale
                    if ($null -ne $imageFullBoundsDips) {
                        $referenceFullBoundsDips = $imageFullBoundsDips
                        $referenceBodyBoundsDips = New-ExpandedBodyBoundsDips -FullExpanderBoundsDips $referenceFullBoundsDips
                        $referenceBodyBoundsInferred = $true
                    }
                }
            } elseif ($UseSummaryBounds -and $null -ne $record.ReferenceUiSummary) {
                $referenceSource = "not-expanded"
            } else {
                $referenceSource = "detected"
                $referenceRegions = Measure-ExpanderRegions `
                    -Bitmap $referenceBitmap `
                    -BoundsPixels $null `
                    -DetectionStartPercent $detectionStartPercent `
                    -SourceName $referenceSource `
                    -Scale $referenceScale `
                    -ValidateBoundsVisual:$ValidateVisibleExpanderBounds `
                    -InferBodyBounds:$InferImageExpandedBodyBounds
                if ($null -ne $referenceRegions) {
                    $referenceBoundsDips = Convert-ExpanderPixelsToDips -PixelBounds $referenceRegions.expanderBounds -Scale $referenceScale
                    if ($InferImageExpandedBodyBounds) {
                        $referenceFullBoundsDips = Convert-FullExpanderPixelsToDips -PixelBounds $referenceRegions.expanderBounds -Scale $referenceScale
                        $referenceBodyBoundsDips = New-ExpandedBodyBoundsDips -FullExpanderBoundsDips $referenceFullBoundsDips
                        $referenceBodyBoundsInferred = $true
                    }
                }
            }
        }

        $rows.Add([pscustomobject]@{
            scenarioId = $record.ScenarioId
            service = $descriptor.Service
            serviceId = $descriptor.ServiceId
            expanderId = $descriptor.ExpanderId
            dotnetReferenceExpected = Test-DotnetReferenceExpected -DescriptorOrRow $descriptor
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
            candidateFullExpanderBoundsDips = $candidateFullBoundsDips
            referenceFullExpanderBoundsDips = $referenceFullBoundsDips
            candidateExpandedBodyBoundsDips = $candidateBodyBoundsDips
            referenceExpandedBodyBoundsDips = $referenceBodyBoundsDips
            candidateExpandedBodyBoundsInferred = $candidateBodyBoundsInferred
            referenceExpandedBodyBoundsInferred = $referenceBodyBoundsInferred
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
$imageSampleRows = @($scenarioRows | Where-Object { (Get-SampleStrength -Row $_) -eq "image" })
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
    $orderedScenarioRows |
        Group-Object serviceId |
        Sort-Object @{ Expression = { Get-ServiceDisplayOrder -ServiceId ([string]$_.Name) }; Ascending = $true } |
        ForEach-Object {
            $serviceRows = @($_.Group)
            $firstRow = @($serviceRows | Select-Object -First 1)[0]
            $serviceId = [string]$firstRow.serviceId
            $capturedStates = @(
                $serviceRows |
                    Sort-Object @{ Expression = { Get-InteractionStateDisplayOrder -State ([string]$_.interactionState) }; Ascending = $true } |
                    ForEach-Object { [string]$_.interactionState } |
                    Select-Object -Unique
            )
            $expectedStates = @("base", "hover", "pressed")
            if ($serviceId -eq "ollama") {
                $expectedStates += "mouse-hover"
            }
            $missingStates = @($expectedStates | Where-Object { $_ -notin $capturedStates })
            $referenceGapStates = @(
                $serviceRows |
                    Where-Object { (-not $_.hasReference) -or (-not $_.referenceExpanded) } |
                    Sort-Object @{ Expression = { Get-InteractionStateDisplayOrder -State ([string]$_.interactionState) }; Ascending = $true } |
                    ForEach-Object { [string]$_.interactionState } |
                    Select-Object -Unique
            )
            [pscustomobject]@{
                service = $firstRow.service
                serviceId = $serviceId
                expanderId = $firstRow.expanderId
                capturedStates = @($capturedStates)
                missingStates = @($missingStates)
                referenceGapStates = @($referenceGapStates)
            }
        }
)
$surfaceSchemeRows = @(
    $baseExpandedRows |
        ForEach-Object {
            $sampleStrength = Get-SampleStrength -Row $_
            $boundsDrift = Get-BoundsDriftScore -Reference $_.referenceExpanderBoundsDips -Candidate $_.candidateExpanderBoundsDips
            $boundsSizeDrift = Get-SizeDriftScore -Reference $_.referenceExpanderBoundsDips -Candidate $_.candidateExpanderBoundsDips
            $bodyBoundsDrift = Get-BoundsDriftScore -Reference $_.referenceExpandedBodyBoundsDips -Candidate $_.candidateExpandedBodyBoundsDips
            $bodySizeDrift = Get-SizeDriftScore -Reference $_.referenceExpandedBodyBoundsDips -Candidate $_.candidateExpandedBodyBoundsDips
            $windowDrift = Get-SizeDriftScore -Reference $_.referenceWindowDips -Candidate $_.candidateWindowDips
            $headerDelta = $_.headerBar.deltaRgb
            $expandedDelta = $_.expandedPart.deltaRgb
            $maxSurfaceDelta = Get-RowMaxColorDelta -Row $_
            $issues = New-Object System.Collections.Generic.List[string]
            $diagnostics = New-Object System.Collections.Generic.List[string]
            $dotnetReferenceExpected = Test-DotnetReferenceExpected -DescriptorOrRow $_

            if (-not $dotnetReferenceExpected -and -not $_.hasReference) {
                $issues.Add(".NET WinUI has no matching service expander") | Out-Null
            } elseif (-not $_.hasReference) {
                $issues.Add("missing .NET reference") | Out-Null
            } elseif (-not $_.referenceExpanded) {
                $issues.Add("reference is not expanded") | Out-Null
            }
            if (-not $_.candidateExpanded) {
                $issues.Add("missing Rust candidate") | Out-Null
            }
            if ($sampleStrength -eq "weak") {
                $issues.Add("weak bounds sample") | Out-Null
            }
            if ($null -ne $maxSurfaceDelta -and [double]$maxSurfaceDelta -gt [double]$MaxSurfaceDeltaRgb) {
                $issues.Add("surface delta > $MaxSurfaceDeltaRgb RGB") | Out-Null
            }
            if ($null -ne $boundsSizeDrift -and [double]$boundsSizeDrift -gt [double]$MaxBoundsDriftDips) {
                $issues.Add("header size drift > $MaxBoundsDriftDips DIP") | Out-Null
            }
            $bodySizeDriftIsDiagnostic = [bool]$_.referenceExpandedBodyBoundsInferred -or
                [bool]$_.candidateExpandedBodyBoundsInferred
            if ($null -ne $bodySizeDrift -and [double]$bodySizeDrift -gt [double]$MaxBoundsDriftDips) {
                if ($bodySizeDriftIsDiagnostic) {
                    $diagnostics.Add("expanded body size drift > $MaxBoundsDriftDips DIP (diagnostic: inferred bounds)") | Out-Null
                } else {
                    $issues.Add("expanded body size drift > $MaxBoundsDriftDips DIP") | Out-Null
                }
            }
            if ($null -ne $windowDrift -and [double]$windowDrift -gt [double]$MaxBoundsDriftDips) {
                $issues.Add("window size drift > $MaxBoundsDriftDips DIP") | Out-Null
            }

            $verdict = if (-not $dotnetReferenceExpected -and -not $_.hasReference) {
                "rust-only"
            } elseif ($issues.Count -eq 0) {
                "ok"
            } elseif (-not $_.hasReference -or -not $_.referenceExpanded -or -not $_.candidateExpanded) {
                "gap"
            } elseif ($sampleStrength -eq "weak") {
                "weak"
            } elseif ($null -ne $maxSurfaceDelta -and [double]$maxSurfaceDelta -le 8.0) {
                "watch"
            } else {
                "drift"
            }

            [pscustomobject]@{
                scenarioId = $_.scenarioId
                service = $_.service
                serviceId = $_.serviceId
                expanderId = $_.expanderId
                dotnetReferenceExpected = $dotnetReferenceExpected
                sampleStrength = $sampleStrength
                hasReference = $_.hasReference
                referenceExpanded = $_.referenceExpanded
                candidateExpanded = $_.candidateExpanded
                referenceScheme = if ($_.referenceExpanded) { Format-SurfacePair -HeaderBar $_.headerBar.reference -ExpandedPart $_.expandedPart.reference } else { "missing" }
                candidateScheme = if ($_.candidateExpanded) { Format-SurfacePair -HeaderBar $_.headerBar.candidate -ExpandedPart $_.expandedPart.candidate } else { "missing" }
                headerBarDeltaRgb = $headerDelta
                expandedPartDeltaRgb = $expandedDelta
                maxSurfaceDeltaRgb = $maxSurfaceDelta
                headerBoundsDriftDips = $boundsDrift
                headerSizeDriftDips = $boundsSizeDrift
                expandedBodyBoundsDriftDips = $bodyBoundsDrift
                expandedBodySizeDriftDips = $bodySizeDrift
                windowDriftDips = $windowDrift
                referenceWindowDips = $_.referenceWindowDips
                candidateWindowDips = $_.candidateWindowDips
                referenceExpanderBoundsDips = $_.referenceExpanderBoundsDips
                candidateExpanderBoundsDips = $_.candidateExpanderBoundsDips
                referenceFullExpanderBoundsDips = $_.referenceFullExpanderBoundsDips
                candidateFullExpanderBoundsDips = $_.candidateFullExpanderBoundsDips
                referenceExpandedBodyBoundsDips = $_.referenceExpandedBodyBoundsDips
                candidateExpandedBodyBoundsDips = $_.candidateExpandedBodyBoundsDips
                referenceExpandedBodyBoundsInferred = $_.referenceExpandedBodyBoundsInferred
                candidateExpandedBodyBoundsInferred = $_.candidateExpandedBodyBoundsInferred
                verdict = $verdict
                issues = @($issues.ToArray())
                diagnostics = @($diagnostics.ToArray())
            }
        }
)
$surfaceSchemeIssues = @($surfaceSchemeRows | Where-Object { $_.verdict -ne "ok" -and $_.verdict -ne "rust-only" })
$surfaceSchemeRustOnly = @($surfaceSchemeRows | Where-Object { $_.verdict -eq "rust-only" })
$surfaceGeometryDiagnostics = @($surfaceSchemeRows | Where-Object { $null -ne $_.diagnostics -and $_.diagnostics.Count -gt 0 })
$expandedBodyGeometrySourceRows = if ($baseExpandedRows.Count -gt 0) { $baseExpandedRows } else { $orderedScenarioRows }
$expandedBodyGeometryRows = @(
    $expandedBodyGeometrySourceRows |
        Where-Object {
            $bodySizeDrift = Get-SizeDriftScore -Reference $_.referenceExpandedBodyBoundsDips -Candidate $_.candidateExpandedBodyBoundsDips
            $null -ne $bodySizeDrift -and [double]$bodySizeDrift -gt [double]$MaxBoundsDriftDips
        } |
        Sort-Object `
            @{ Expression = { Get-ServiceDisplayOrder -ServiceId $_.serviceId }; Ascending = $true },
            @{ Expression = { Get-InteractionStateDisplayOrder -State $_.interactionState }; Ascending = $true },
            scenarioId |
        ForEach-Object {
            $bodyBoundsDrift = Get-BoundsDriftScore -Reference $_.referenceExpandedBodyBoundsDips -Candidate $_.candidateExpandedBodyBoundsDips
            $bodySizeDrift = Get-SizeDriftScore -Reference $_.referenceExpandedBodyBoundsDips -Candidate $_.candidateExpandedBodyBoundsDips
            $inferred = [bool]$_.referenceExpandedBodyBoundsInferred -or [bool]$_.candidateExpandedBodyBoundsInferred
            $evidence = Get-ExpandedBodyGeometryEvidence -Row $_
            [pscustomobject]@{
                scenarioId = $_.scenarioId
                service = $_.service
                serviceId = $_.serviceId
                expanderId = $_.expanderId
                interactionState = $_.interactionState
                sampleStrength = Get-SampleStrength -Row $_
                sizeDriftDips = $bodySizeDrift
                boundsDriftDips = $bodyBoundsDrift
                referenceBoundsDips = $_.referenceExpandedBodyBoundsDips
                candidateBoundsDips = $_.candidateExpandedBodyBoundsDips
                referenceInferred = [bool]$_.referenceExpandedBodyBoundsInferred
                candidateInferred = [bool]$_.candidateExpandedBodyBoundsInferred
                diagnostic = $inferred
                evidence = [string]$evidence.Kind
                evidenceReason = [string]$evidence.Reason
            }
        }
)
$expandedBodyGeometryVerifiedRows = @($expandedBodyGeometryRows | Where-Object { $_.evidence -eq "verified" })
$expandedBodyGeometryDiagnosticRows = @($expandedBodyGeometryRows | Where-Object { $_.evidence -ne "verified" })

$summary = [pscustomobject]@{
    scenarioCount = $scenarioRows.Count
    baseExpandedScenarioCount = $baseExpandedRows.Count
    strongSampleCount = $strongSampleRows.Count
    chevronSampleCount = $chevronSampleRows.Count
    imageSampleCount = $imageSampleRows.Count
    rankedSampleCount = $rankedSampleRows.Count
    weakSampleCount = $weakSampleRows.Count
    referenceExpandedCount = @($scenarioRows | Where-Object { $_.referenceExpanded }).Count
    referenceGapCount = $referenceGapRows.Count
    surfaceSchemeComparedCount = @($surfaceSchemeRows | Where-Object { $_.verdict -eq "ok" }).Count
    surfaceSchemeIssueCount = $surfaceSchemeIssues.Count
    surfaceSchemeRustOnlyCount = $surfaceSchemeRustOnly.Count
    surfaceGeometryDiagnosticCount = $surfaceGeometryDiagnostics.Count
    expandedBodyGeometryDriftCount = $expandedBodyGeometryRows.Count
    expandedBodyGeometryVerifiedCount = $expandedBodyGeometryVerifiedRows.Count
    expandedBodyGeometryDiagnosticCount = $expandedBodyGeometryDiagnosticRows.Count
    colorDeltaThresholds = [pscustomobject]@{
        okMaxRgb = 3.0
        watchMaxRgb = 8.0
        failMaxSurfaceDeltaRgb = $MaxSurfaceDeltaRgb
        failMaxBoundsDriftDips = $MaxBoundsDriftDips
    }
    surfaceSchemeIssues = $surfaceSchemeIssues
    surfaceGeometryDiagnostics = $surfaceGeometryDiagnostics
    expandedBodyGeometryRows = $expandedBodyGeometryRows
    expandedBodyGeometryVerifiedRows = $expandedBodyGeometryVerifiedRows
    expandedBodyGeometryDiagnosticRows = $expandedBodyGeometryDiagnosticRows
    largestColorDeltas = $largestColorDeltas
    largestHeaderBoundsDrifts = $largestHeaderBoundsDrifts
    serviceStateCoverage = $serviceStateCoverage
}

$report = [pscustomobject]@{
    schemaVersion = "easydict.settings-services-expander-colors.v13"
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    artifactRoot = $ArtifactRoot
    boundsMode = if ($UseSummaryBounds) { "summary" } else { "image-detected" }
    visibleExpanderBoundsValidation = [bool]$ValidateVisibleExpanderBounds
    imageExpandedBodyBoundsInference = [bool]$InferImageExpandedBodyBounds
    colorMode = "dominant-surface"
    summary = $summary
    surfaceSchemeRows = $surfaceSchemeRows
    expandedBodyGeometryRows = $expandedBodyGeometryRows
    expandedBodyGeometryVerifiedRows = $expandedBodyGeometryVerifiedRows
    expandedBodyGeometryDiagnosticRows = $expandedBodyGeometryDiagnosticRows
    scenarios = $orderedScenarioRows
}
Write-JsonFile -Path $OutputJson -Value $report -Depth 12

$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add("# Settings Services Expander Color Report") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Artifact root: ``$ArtifactRoot``") | Out-Null
$markdown.Add("") | Out-Null
$boundsModeText = if ($UseSummaryBounds) { "summary/UIA bounds" } else { "image-detected expanded chevron bounds" }
$optionalBoundsText = @()
if ($ValidateVisibleExpanderBounds) {
    $optionalBoundsText += "summary bounds require a matching visible expander chevron"
}
if ($InferImageExpandedBodyBounds) {
    $optionalBoundsText += "missing expanded body bounds may be inferred from image surfaces and shown as diagnostics"
}
$optionalBoundsSuffix = if ($optionalBoundsText.Count -gt 0) { " Optional diagnostics: $($optionalBoundsText -join '; ')." } else { "" }
$markdown.Add("Sampling: bounds use $boundsModeText; color deltas use each sampled region's dominant surface color, while JSON also preserves average RGB/luma for diagnostics.$optionalBoundsSuffix") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Summary: $($summary.scenarioCount) measured scenarios, $($summary.baseExpandedScenarioCount) base expanded service items, $($summary.referenceExpandedCount) expanded references, $($summary.referenceGapCount) reference gaps, $($summary.strongSampleCount) strong summary samples, $($summary.chevronSampleCount) chevron-probe samples, $($summary.imageSampleCount) image-detected samples, $($summary.weakSampleCount) weak/missing samples, $($summary.surfaceSchemeComparedCount) base service surface schemes ok, $($summary.surfaceSchemeRustOnlyCount) rust-only service surface schemes, $($summary.surfaceSchemeIssueCount) base service surface scheme issues, $($summary.surfaceGeometryDiagnosticCount) base expanded-body geometry diagnostics, $($summary.expandedBodyGeometryVerifiedCount) verified expanded-body geometry drifts, $($summary.expandedBodyGeometryDiagnosticCount) diagnostic expanded-body geometry drifts. Color verdict thresholds: ok <= 3 RGB, watch <= 8 RGB, drift > 8 RGB. Optional gate: max surface delta <= $MaxSurfaceDeltaRgb RGB and absolute window/header size drift <= $MaxBoundsDriftDips DIP; exact expanded-body size drift is gated when both sides come from non-inferred bounds, while image-inferred body bounds remain visible as diagnostics. Viewport x/y drift remains visible in the bounds columns but is not treated as size drift.") | Out-Null
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
        $reference = if (-not (Test-DotnetReferenceExpected -DescriptorOrRow $row) -and -not $row.hasReference) {
            "rust-only"
        } elseif ($row.hasReference) {
            "not-expanded"
        } else {
            "missing"
        }
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
    $markdown.Add("Rows here are useful visual evidence, but their color deltas are not ranked because at least one side was missing or could not be tied to summary/image-detected bounds.") | Out-Null
    $markdown.Add("") | Out-Null
    $markdown.Add("| Scenario | Service | State | Reference | Source | Next action |") | Out-Null
    $markdown.Add("| --- | --- | --- | --- | --- | --- |") | Out-Null
    foreach ($row in $weakSampleRows) {
        $reference = if (-not (Test-DotnetReferenceExpected -DescriptorOrRow $row) -and -not $row.hasReference) {
            "rust-only"
        } elseif ($row.hasReference) {
            if ($row.referenceExpanded) { "expanded" } else { "not-expanded" }
        } else {
            "missing"
        }
        $source = "ref=$($row.referenceSource), rust=$($row.candidateSource)"
        $nextAction = if (-not (Test-DotnetReferenceExpected -DescriptorOrRow $row) -and -not $row.hasReference) {
            "Rust-only; decide parity scope"
        } elseif (-not $row.hasReference) {
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

$markdown.Add("## Surface Scheme Verdict") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("This is the focused base-state checklist for expanding each Settings > Services item. It treats the header bar and expanded part as the decisive color pair, while also keeping absolute window size and expander bounds in DIP visible.") | Out-Null
$markdown.Add("") | Out-Null
if ($surfaceSchemeRows.Count -eq 0) {
    $markdown.Add("No base expanded captures are present in this artifact. Use the per-service and bar/expanded pairing tables below for the measured interaction states.") | Out-Null
} else {
    $markdown.Add("| Service | Verdict | Sample | Window DIP | Header bounds DIP | Expanded body bounds DIP | Header bar delta | Expanded part delta | Reference scheme | Rust scheme | Issues / diagnostics |") | Out-Null
    $markdown.Add("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null
    foreach ($row in $surfaceSchemeRows) {
        $window = "ref $(Format-SizeDips $row.referenceWindowDips) / rust $(Format-SizeDips $row.candidateWindowDips) / drift $(Format-PlainDeltaWithVerdict $row.windowDriftDips)"
        $bounds = "ref $(Format-BoundsDips $row.referenceExpanderBoundsDips) / rust $(Format-BoundsDips $row.candidateExpanderBoundsDips) / drift $(Format-PlainDeltaWithVerdict $row.headerBoundsDriftDips)"
        $bodyBounds = "ref $(Format-BoundsDips $row.referenceExpandedBodyBoundsDips) / rust $(Format-BoundsDips $row.candidateExpandedBodyBoundsDips) / drift $(Format-PlainDeltaWithVerdict $row.expandedBodyBoundsDriftDips)"
        $rowNotes = New-Object System.Collections.Generic.List[string]
        foreach ($issue in @($row.issues)) {
            $rowNotes.Add([string]$issue) | Out-Null
        }
        foreach ($diagnostic in @($row.diagnostics)) {
            $rowNotes.Add([string]$diagnostic) | Out-Null
        }
        $issues = if ($rowNotes.Count -eq 0) { "none" } else { $rowNotes -join "; " }
        $markdown.Add("| $($row.service) | $($row.verdict) | $($row.sampleStrength) | $window | $bounds | $bodyBounds | $(Format-DeltaWithVerdict $row.headerBarDeltaRgb) | $(Format-DeltaWithVerdict $row.expandedPartDeltaRgb) | $($row.referenceScheme) | $($row.candidateScheme) | $issues |") | Out-Null
    }
}
$markdown.Add("") | Out-Null

$markdown.Add("## Expanded Body Geometry Evidence") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Rows here call out absolute expanded-body size differences. Verified rows use non-inferred full expander bounds and can drive layout fixes; diagnostic rows usually come from inferred visible-body bounds in scrolled captures, so they should guide visual inspection without being mixed into the bar/body color gate.") | Out-Null
$markdown.Add("") | Out-Null
if ($expandedBodyGeometryRows.Count -eq 0) {
    $markdown.Add("No expanded body size drift above $MaxBoundsDriftDips DIP was detected.") | Out-Null
} else {
    $markdown.Add("| Service | State | Scenario | Evidence | Sample | Size drift DIP | Bounds drift DIP | Expanded body bounds DIP | Inferred | Reason |") | Out-Null
    $markdown.Add("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null
    foreach ($row in $expandedBodyGeometryRows) {
        $bodyBounds = "ref $(Format-BoundsDips $row.referenceBoundsDips) / rust $(Format-BoundsDips $row.candidateBoundsDips)"
        $inferred = if ($row.referenceInferred -or $row.candidateInferred) {
            "ref=$($row.referenceInferred), rust=$($row.candidateInferred)"
        } else {
            "no"
        }
        $markdown.Add("| $($row.service) | $($row.interactionState) | ``$($row.scenarioId)`` | $($row.evidence) | $($row.sampleStrength) | $(Format-PlainDeltaWithVerdict $row.sizeDriftDips) | $(Format-PlainDeltaWithVerdict $row.boundsDriftDips) | $bodyBounds | $inferred | $($row.evidenceReason) |") | Out-Null
    }
}
$markdown.Add("") | Out-Null

$markdown.Add("## Per-Service Expanded Comparison") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Rows are ordered by the .NET Services page order. Header bar and expanded part are the decisive color surfaces; divider is reported separately later because 1px anti-aliasing makes it noisier.") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("| Service | State | Scenario | Reference | Sample | Window DIP | Header bounds DIP | Expanded body bounds DIP | Header bar | Expanded part | Rust scheme |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null
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
    $bodyBounds = "ref $(Format-BoundsDips $row.referenceExpandedBodyBoundsDips) / rust $(Format-BoundsDips $row.candidateExpandedBodyBoundsDips) / $(Format-BoundsDeltaDips -Reference $row.referenceExpandedBodyBoundsDips -Candidate $row.candidateExpandedBodyBoundsDips)"
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
    $markdown.Add("| $($row.service) | $($row.interactionState) | ``$($row.scenarioId)`` | $reference | $sample | $window | $bounds | $bodyBounds | $bar | $expanded | $rustScheme |") | Out-Null
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
$markdown.Add("| Scenario | Service | Reference | Sample | Window DIP | Header bounds DIP | Expanded body bounds DIP | Bar color | Expanded part color |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null
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
    $bodyBounds = "ref $(Format-BoundsDips $row.referenceExpandedBodyBoundsDips) / rust $(Format-BoundsDips $row.candidateExpandedBodyBoundsDips) / $(Format-BoundsDeltaDips -Reference $row.referenceExpandedBodyBoundsDips -Candidate $row.candidateExpandedBodyBoundsDips)"
    $bar = "ref $(Format-RegionHex $row.headerBar.reference) / rust $(Format-RegionHex $row.headerBar.candidate) / $(Format-DeltaWithVerdict $row.headerBar.deltaRgb) / $(Format-LumaDelta $row.headerBar.deltaLuma)"
    $expanded = "ref $(Format-RegionHex $row.expandedPart.reference) / rust $(Format-RegionHex $row.expandedPart.candidate) / $(Format-DeltaWithVerdict $row.expandedPart.deltaRgb) / $(Format-LumaDelta $row.expandedPart.deltaLuma)"
    if (-not $row.hasReference) {
        $bar = "rust $(Format-RegionHex $row.headerBar.candidate)"
        $expanded = "rust $(Format-RegionHex $row.expandedPart.candidate)"
    } elseif (-not $row.referenceExpanded) {
        $bar = "ref missing / rust $(Format-RegionHex $row.headerBar.candidate)"
        $expanded = "ref missing / rust $(Format-RegionHex $row.expandedPart.candidate)"
    }
    $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $reference | $sample | $window | $bounds | $bodyBounds | $bar | $expanded |") | Out-Null
}
$markdown.Add("") | Out-Null

$markdown.Add("## Bar / Expanded Surface Pairing") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Each measured state is sampled as header bar -> expanded part so the expander color scheme is visible per service. The pair separation is the internal visual step from bar to body; the Cross-surface delta column is the .NET vs Rust mismatch signal.") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("| Scenario | Service | State | Reference scheme | Rust scheme | Cross-surface delta |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- | --- |") | Out-Null
foreach ($row in $orderedScenarioRows) {
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
    $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $($row.interactionState) | $referenceScheme | $candidateScheme | $crossDelta |") | Out-Null
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
$markdown.Add("| Scenario | Service | State | Reference | Window DIP | Header bounds DIP | Expanded body bounds DIP | Source | Header bar | Divider | Expanded part |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null
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
    $bodyBounds = "ref $(Format-BoundsDips $row.referenceExpandedBodyBoundsDips) / rust $(Format-BoundsDips $row.candidateExpandedBodyBoundsDips) / $(Format-BoundsDeltaDips -Reference $row.referenceExpandedBodyBoundsDips -Candidate $row.candidateExpandedBodyBoundsDips)"
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
    $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $($row.interactionState) | $reference | $window | $bounds | $bodyBounds | $source | $header | $divider | $expanded |") | Out-Null
}
$markdown | Set-Content -LiteralPath $OutputMarkdown -Encoding utf8

Write-Host "Color report JSON: $OutputJson"
Write-Host "Color report Markdown: $OutputMarkdown"

if ($FailOnSurfaceDrift -and $surfaceSchemeIssues.Count -gt 0) {
    $issueSummary = @(
        $surfaceSchemeIssues |
            ForEach-Object {
                $issueText = if ($_.issues.Count -eq 0) { $_.verdict } else { $_.issues -join "; " }
                "$($_.service): $issueText"
            }
    ) -join " | "
    throw "Settings Services surface scheme parity gate failed: $issueSummary"
}

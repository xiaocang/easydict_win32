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
    $descriptors = @(
        @{ Scenario = "parity-settings-services-deepl-expanded-top"; Service = "DeepL"; ExpanderId = "DeepLServiceExpander" },
        @{ Scenario = "parity-settings-services-local-ai-expanded-top"; Service = "Windows Local AI"; ExpanderId = "WindowsLocalAIExpander" },
        @{ Scenario = "parity-settings-services-ollama-expanded-top"; Service = "Ollama"; ExpanderId = "OllamaServiceExpander" },
        @{ Scenario = "parity-settings-services-openai-expanded-scroll-15-percent"; Service = "OpenAI"; ExpanderId = "OpenAIServiceExpander" },
        @{ Scenario = "parity-settings-services-deepseek-expanded-scroll-25-percent"; Service = "DeepSeek"; ExpanderId = "DeepSeekServiceExpander" },
        @{ Scenario = "parity-settings-services-groq-expanded-scroll-35-percent"; Service = "Groq"; ExpanderId = "GroqServiceExpander" },
        @{ Scenario = "parity-settings-services-zhipu-expanded-scroll-45-percent"; Service = "Zhipu"; ExpanderId = "ZhipuServiceExpander" },
        @{ Scenario = "parity-settings-services-github-models-expanded-scroll-55-percent"; Service = "GitHub Models"; ExpanderId = "GitHubModelsServiceExpander" },
        @{ Scenario = "parity-settings-services-gemini-expanded-scroll-60-percent"; Service = "Gemini"; ExpanderId = "GeminiServiceExpander" },
        @{ Scenario = "parity-settings-services-custom-openai-expanded-scroll-70-percent"; Service = "Custom OpenAI"; ExpanderId = "CustomOpenAIServiceExpander" },
        @{ Scenario = "parity-settings-services-builtin-ai-expanded-scroll-75-percent"; Service = "Built-in AI"; ExpanderId = "BuiltInAIServiceExpander" },
        @{ Scenario = "parity-settings-services-doubao-expanded-scroll-80-percent"; Service = "Doubao"; ExpanderId = "DoubaoServiceExpander" },
        @{ Scenario = "parity-settings-services-caiyun-expanded-scroll-88-percent"; Service = "Caiyun"; ExpanderId = "CaiyunServiceExpander" },
        @{ Scenario = "parity-settings-services-niutrans-expanded-scroll-94-percent"; Service = "NiuTrans"; ExpanderId = "NiuTransServiceExpander" },
        @{ Scenario = "parity-settings-services-youdao-expanded-scroll-100-percent"; Service = "Youdao"; ExpanderId = "YoudaoServiceExpander" },
        @{ Scenario = "parity-settings-services-volcano-expanded-scroll-100-percent"; Service = "Volcano"; ExpanderId = "VolcanoServiceExpander" }
    )

    foreach ($descriptor in $descriptors) {
        if ($descriptor.Scenario -eq $normalized) {
            return [pscustomobject]$descriptor
        }
    }

    return $null
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

    [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $Path).Path)
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

function Get-ScaleForEntry {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        $Window
    )

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

function Find-ExpandedBodyTop {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [double]$StartPercent = 0.18
    )

    $sampleX = [Math]::Min([Math]::Max(610, [int]($Bitmap.Width * 0.70)), [Math]::Max(0, $Bitmap.Width - 180))
    $sampleWidth = [Math]::Min(180, $Bitmap.Width - $sampleX)
    $startY = [Math]::Max(120, [int]($Bitmap.Height * $StartPercent))
    for ($y = $startY; $y -lt ($Bitmap.Height - 160); $y += 2) {
        $body = Measure-BitmapRegion -Bitmap $Bitmap -X $sampleX -Y $y -Width $sampleWidth -Height 16
        $bodyMid = Measure-BitmapRegion -Bitmap $Bitmap -X $sampleX -Y ($y + 48) -Width $sampleWidth -Height 16
        $bodyDeep = Measure-BitmapRegion -Bitmap $Bitmap -X $sampleX -Y ($y + 96) -Width $sampleWidth -Height 16
        $header = Measure-BitmapRegion -Bitmap $Bitmap -X $sampleX -Y ($y - 36) -Width $sampleWidth -Height 16
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

function Measure-ExpanderRegions {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        $BoundsPixels,
        [double]$DetectionStartPercent = 0.18
    )

    $source = "bounds"
    if ($null -ne $BoundsPixels) {
        $x = [int]$BoundsPixels.Left
        $y = [int]$BoundsPixels.Top
        $width = [int]$BoundsPixels.Width
        $headerY = $y + 10
        $bodyTop = $y + 49
    } else {
        $source = "detected"
        $bodyTop = Find-ExpandedBodyTop -Bitmap $Bitmap -StartPercent $DetectionStartPercent
        if ($null -eq $bodyTop) {
            return $null
        }
        $x = [Math]::Max(24, [int]($Bitmap.Width * 0.03))
        $width = [Math]::Min($Bitmap.Width - ($x * 2), 796)
        $y = [int]$bodyTop - 49
        $headerY = $y + 10
    }

    $headerWidth = [Math]::Min(220, [Math]::Max(80, $width - 160))
    $headerX = $x + [Math]::Max(96, $width - $headerWidth - 96)
    $bodyX = $x + [Math]::Min([Math]::Max(560, [int]($width * 0.70)), [Math]::Max(0, $width - 190))
    $bodyWidth = [Math]::Min(170, [Math]::Max(64, $width - ($bodyX - $x) - 24))

    [pscustomobject]@{
        source = $source
        headerBar = Measure-BitmapRegion -Bitmap $Bitmap -X $headerX -Y $headerY -Width $headerWidth -Height 26
        divider = Measure-BitmapRegion -Bitmap $Bitmap -X ($x + 8) -Y ([int]$bodyTop - 1) -Width ([Math]::Max(1, $width - 16)) -Height 1
        expandedPart = Measure-BitmapRegion -Bitmap $Bitmap -X $bodyX -Y ([int]$bodyTop + 16) -Width $bodyWidth -Height 64
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
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($entry in @($manifest.Scenarios)) {
        $record = New-ScenarioRecordFromManifest -Entry $entry
        $recordsByScenario[$record.ScenarioId] = $record
    }
}

$matrixPath = Join-Path $ArtifactRoot "rust-preview-parity-matrix.json"
if (Test-Path -LiteralPath $matrixPath) {
    $matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
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
        # Expanded service screenshots need image-detected regions because schema bounds
        # describe the expander header before runtime layout has inserted the body.
        $useDetectedBounds = $true
        $candidateScale = Get-ScaleForEntry -Bitmap $candidateBitmap -Window $record.CandidateWindow
        $candidateBounds = if ($useDetectedBounds) {
            $null
        } else {
            Convert-BoundsToPixels `
                -BoundsDips (Get-ControlBoundsFromSummary -UiSummary $record.CandidateUiSummary -AutomationId $descriptor.ExpanderId) `
                -Scale $candidateScale
        }
        $detectionStartPercent = if (Test-ScrolledScenario -ScenarioId $record.ScenarioId) { 0.18 } else { 0.50 }
        $candidateRegions = Measure-ExpanderRegions `
            -Bitmap $candidateBitmap `
            -BoundsPixels $candidateBounds `
            -DetectionStartPercent $detectionStartPercent

        $referenceRegions = $null
        if ($null -ne $referenceBitmap) {
            $referenceScale = Get-ScaleForEntry -Bitmap $referenceBitmap -Window $record.ReferenceWindow
            $referenceBounds = if ($useDetectedBounds) {
                $null
            } else {
                Convert-BoundsToPixels `
                    -BoundsDips (Get-ControlBoundsFromSummary -UiSummary $record.ReferenceUiSummary -AutomationId $descriptor.ExpanderId) `
                    -Scale $referenceScale
            }
            $referenceRegions = Measure-ExpanderRegions `
                -Bitmap $referenceBitmap `
                -BoundsPixels $referenceBounds `
                -DetectionStartPercent $detectionStartPercent
        }

        $rows.Add([pscustomobject]@{
            scenarioId = $record.ScenarioId
            service = $descriptor.Service
            expanderId = $descriptor.ExpanderId
            hasReference = $null -ne $referenceBitmap
            candidateSource = if ($null -ne $candidateRegions) { $candidateRegions.source } else { "missing" }
            referenceSource = if ($null -ne $referenceRegions) { $referenceRegions.source } else { "missing" }
            headerBar = [pscustomobject]@{
                reference = if ($null -ne $referenceRegions) { $referenceRegions.headerBar } else { $null }
                candidate = if ($null -ne $candidateRegions) { $candidateRegions.headerBar } else { $null }
                deltaRgb = Get-RegionDelta -Reference $(if ($null -ne $referenceRegions) { $referenceRegions.headerBar } else { $null }) -Candidate $(if ($null -ne $candidateRegions) { $candidateRegions.headerBar } else { $null })
            }
            divider = [pscustomobject]@{
                reference = if ($null -ne $referenceRegions) { $referenceRegions.divider } else { $null }
                candidate = if ($null -ne $candidateRegions) { $candidateRegions.divider } else { $null }
                deltaRgb = Get-RegionDelta -Reference $(if ($null -ne $referenceRegions) { $referenceRegions.divider } else { $null }) -Candidate $(if ($null -ne $candidateRegions) { $candidateRegions.divider } else { $null })
            }
            expandedPart = [pscustomobject]@{
                reference = if ($null -ne $referenceRegions) { $referenceRegions.expandedPart } else { $null }
                candidate = if ($null -ne $candidateRegions) { $candidateRegions.expandedPart } else { $null }
                deltaRgb = Get-RegionDelta -Reference $(if ($null -ne $referenceRegions) { $referenceRegions.expandedPart } else { $null }) -Candidate $(if ($null -ne $candidateRegions) { $candidateRegions.expandedPart } else { $null })
            }
        }) | Out-Null
    } finally {
        $candidateBitmap.Dispose()
        if ($null -ne $referenceBitmap) {
            $referenceBitmap.Dispose()
        }
    }
}

$report = [pscustomobject]@{
    schemaVersion = "easydict.settings-services-expander-colors.v1"
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    artifactRoot = $ArtifactRoot
    scenarios = $rows.ToArray()
}
Write-JsonFile -Path $OutputJson -Value $report -Depth 12

$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add("# Settings Services Expander Color Report") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("Artifact root: ``$ArtifactRoot``") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("| Scenario | Service | Reference | Source | Header bar | Divider | Expanded part |") | Out-Null
$markdown.Add("| --- | --- | --- | --- | --- | --- | --- |") | Out-Null
foreach ($row in $rows) {
    $reference = if ($row.hasReference) { "yes" } else { "missing" }
    $source = "ref=$($row.referenceSource), cand=$($row.candidateSource)"
    $header = if ($null -ne $row.headerBar.candidate) {
        "ref $(Format-RegionHex $row.headerBar.reference) / rust $(Format-RegionHex $row.headerBar.candidate) / d=$($row.headerBar.deltaRgb)"
    } else {
        "n/a"
    }
    $divider = if ($null -ne $row.divider.candidate) {
        "ref $(Format-RegionHex $row.divider.reference) / rust $(Format-RegionHex $row.divider.candidate) / d=$($row.divider.deltaRgb)"
    } else {
        "n/a"
    }
    $expanded = if ($null -ne $row.expandedPart.candidate) {
        "ref $(Format-RegionHex $row.expandedPart.reference) / rust $(Format-RegionHex $row.expandedPart.candidate) / d=$($row.expandedPart.deltaRgb)"
    } else {
        "n/a"
    }
    if (-not $row.hasReference) {
        $header = if ($null -ne $row.headerBar.candidate) { "rust $(Format-RegionHex $row.headerBar.candidate)" } else { "n/a" }
        $divider = if ($null -ne $row.divider.candidate) { "rust $(Format-RegionHex $row.divider.candidate)" } else { "n/a" }
        $expanded = if ($null -ne $row.expandedPart.candidate) { "rust $(Format-RegionHex $row.expandedPart.candidate)" } else { "n/a" }
    }
    $markdown.Add("| ``$($row.scenarioId)`` | $($row.service) | $reference | $source | $header | $divider | $expanded |") | Out-Null
}
$markdown | Set-Content -LiteralPath $OutputMarkdown -Encoding utf8

Write-Host "Color report JSON: $OutputJson"
Write-Host "Color report Markdown: $OutputMarkdown"

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot,

    [ValidateSet("main", "floating", "settings", "effects", "popbutton", "ocr")]
    [string]$Scope = "main",

    [ValidateSet("light", "dark", "system")]
    [string]$Theme = "light",

    [ValidateSet("zh-CN", "en-US")]
    [string]$UiLanguage = "zh-CN",

    [ValidateSet("initial", "buttons", "dropdown-open", "dropdown-open-only", "dropdowns", "dropdown-options", "all")]
    [string]$MainOperationsScope = "dropdown-open-only",

    [ValidateSet("source", "target", "all")]
    [string]$MainDropdown = "target",

    [ValidateSet("initial", "buttons", "dropdown-open", "dropdown-open-only", "dropdowns", "dropdown-options", "all")]
    [string]$FloatingScope = "dropdown-open-only",

    [ValidateSet("mini", "fixed", "all")]
    [string]$FloatingWindow = "mini",

    [ValidateSet("source", "target", "all")]
    [string]$FloatingDropdown = "target",

    [string]$SettingsSection = "parity-settings-general-behavior-top",

    [string]$DropdownOptionIndexes,

    [switch]$SkipBuild,

    [switch]$SkipAnalyzerSelfTest
)

$ErrorActionPreference = "Stop"

function Resolve-RepositoryRoot {
    $ciRoot = Resolve-Path -LiteralPath $PSScriptRoot
    return (Resolve-Path -LiteralPath (Join-Path $ciRoot "..\..\..")).Path
}

function Initialize-RunDirectories {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    foreach ($generatedPath in @(
            (Join-Path $Root "captures"),
            (Join-Path $Root "analysis"),
            (Join-Path $Root "preflight.json"),
            (Join-Path $Root "run-summary.md"))) {
        if (Test-Path -LiteralPath $generatedPath) {
            Remove-Item -LiteralPath $generatedPath -Recurse -Force
        }
    }

    New-Item -ItemType Directory -Force -Path (Join-Path $Root "captures") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Root "analysis") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Root "logs") | Out-Null
}

function Format-CommandLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $parts = @($FilePath) + $Arguments
    return ($parts | ForEach-Object {
            if ($_ -match '[\s"]') {
                '"' + ($_ -replace '"', '\"') + '"'
            } else {
                $_
            }
        }) -join " "
}

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$LogRoot
    )

    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '-'
    $index = ($script:Commands.Count + 1).ToString("00", [System.Globalization.CultureInfo]::InvariantCulture)
    $stdoutPath = Join-Path $LogRoot "$index-$safeName.stdout.log"
    $stderrPath = Join-Path $LogRoot "$index-$safeName.stderr.log"
    $commandLine = Format-CommandLine -FilePath $FilePath -Arguments $Arguments
    $startedAtUtc = [DateTimeOffset]::UtcNow
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $exitCode = $null

    try {
        $process = Start-Process `
            -FilePath $FilePath `
            -ArgumentList $Arguments `
            -NoNewWindow `
            -Wait `
            -PassThru `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath
        $exitCode = [int]$process.ExitCode
    } finally {
        $stopwatch.Stop()
        $command = [ordered]@{
            Name = $Name
            CommandLine = $commandLine
            StartedAtUtc = $startedAtUtc.ToString("O", [System.Globalization.CultureInfo]::InvariantCulture)
            DurationMs = [int64]$stopwatch.Elapsed.TotalMilliseconds
            ExitCode = $exitCode
            StdoutPath = $stdoutPath
            StderrPath = $stderrPath
        }
        $script:Commands.Add([pscustomobject]$command) | Out-Null
    }

    return [pscustomobject]$command
}

function Get-RunMetrics {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        return $null
    }
}

function Get-PropertyValue {
    param(
        [object]$Object,
        [string[]]$Names
    )

    if ($null -eq $Object) {
        return $null
    }

    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property) {
            return $property.Value
        }
    }

    return $null
}

function Format-Bounds {
    param([object]$Bounds)

    if ($null -eq $Bounds) {
        return $null
    }

    return "$($Bounds.Left),$($Bounds.Top) $($Bounds.Width)x$($Bounds.Height)"
}

function Get-WindowSummary {
    param([object]$Window)

    if ($null -eq $Window) {
        return [pscustomobject]@{ Dpi = $null; DpiScale = $null; Bounds = $null }
    }

    return [pscustomobject]@{
        Dpi = $Window.Dpi
        DpiScale = $Window.DpiScale
        Bounds = Format-Bounds -Bounds $Window.Bounds
    }
}

function Get-ManifestSummary {
    param([Parameter(Mandatory = $true)][string]$Path)

    $emptyWindow = [pscustomobject]@{ Dpi = $null; DpiScale = $null; Bounds = $null }
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{
            ScenarioCount = 0
            FirstScenarioId = $null
            ManifestPath = $Path
            ReferenceWindow = $emptyWindow
            CandidateWindow = $emptyWindow
        }
    }

    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $scenarios = @($manifest.Scenarios)
    if ($scenarios.Count -eq 1 -and $null -eq $scenarios[0]) {
        $scenarios = @()
    }

    $first = if ($scenarios.Count -gt 0) { $scenarios[0] } else { $null }
    $firstId = if ($null -ne $first) { $first.ScenarioId } else { $null }
    $referenceWindow = if ($null -ne $first) { $first.ReferenceWindow } else { $null }
    $candidateWindow = if ($null -ne $first) { $first.CandidateWindow } else { $null }

    return [pscustomobject]@{
        ScenarioCount = $scenarios.Count
        FirstScenarioId = $firstId
        ManifestPath = $Path
        ReferenceWindow = Get-WindowSummary -Window $referenceWindow
        CandidateWindow = Get-WindowSummary -Window $candidateWindow
    }
}

function Get-ScreenshotPairSummary {
    param([Parameter(Mandatory = $true)][string]$CapturesDir)

    $referenceSuffix = "-dotnet-winui-reference.png"
    $candidateSuffix = "-rust-win-fluent-iced.png"
    $sideBySideSuffix = "-dotnet-vs-rust-side-by-side.png"

    $referenceStems = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $candidateStems = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $sideBySideStems = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    if (Test-Path -LiteralPath $CapturesDir) {
        foreach ($file in Get-ChildItem -LiteralPath $CapturesDir -Recurse -File -Filter "*.png") {
            if ($file.Name.EndsWith($referenceSuffix, [StringComparison]::OrdinalIgnoreCase)) {
                [void]$referenceStems.Add($file.Name.Substring(0, $file.Name.Length - $referenceSuffix.Length))
            } elseif ($file.Name.EndsWith($candidateSuffix, [StringComparison]::OrdinalIgnoreCase)) {
                [void]$candidateStems.Add($file.Name.Substring(0, $file.Name.Length - $candidateSuffix.Length))
            } elseif ($file.Name.EndsWith($sideBySideSuffix, [StringComparison]::OrdinalIgnoreCase)) {
                [void]$sideBySideStems.Add($file.Name.Substring(0, $file.Name.Length - $sideBySideSuffix.Length))
            }
        }
    }

    $commonCount = 0
    foreach ($stem in $referenceStems) {
        if ($candidateStems.Contains($stem) -and $sideBySideStems.Contains($stem)) {
            $commonCount++
        }
    }

    return [pscustomobject]@{
        ReferenceScreenshotCount = $referenceStems.Count
        CandidateScreenshotCount = $candidateStems.Count
        SideBySideScreenshotCount = $sideBySideStems.Count
        CommonScenarioCount = $commonCount
    }
}

function Get-ReportSummary {
    param([string]$ReportJsonPath)

    if (-not (Test-Path -LiteralPath $ReportJsonPath)) {
        return [pscustomobject]@{ Analyzed = 0; LowScore = 0 }
    }

    try {
        $content = Get-Content -LiteralPath $ReportJsonPath -Raw
        $totalMatch = [regex]::Match($content, '"TotalScenarios"\s*:\s*(?<value>\d+)')
        $warnMatch = [regex]::Match($content, '"WarnCount"\s*:\s*(?<value>\d+)')
        $failMatch = [regex]::Match($content, '"FailCount"\s*:\s*(?<value>\d+)')

        $analyzed = if ($totalMatch.Success) { [int]$totalMatch.Groups["value"].Value } else { 0 }
        $warnCount = if ($warnMatch.Success) { [int]$warnMatch.Groups["value"].Value } else { 0 }
        $failCount = if ($failMatch.Success) { [int]$failMatch.Groups["value"].Value } else { 0 }

        return [pscustomobject]@{ Analyzed = $analyzed; LowScore = ($warnCount + $failCount) }
    } catch {
        return [pscustomobject]@{ Analyzed = 0; LowScore = 0 }
    }
}

function Get-ShortGitHead {
    param([string]$RepositoryRoot)

    try {
        $result = & git -C $RepositoryRoot rev-parse --short HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($result)) {
            return ($result | Select-Object -First 1).Trim()
        }
    } catch {
    }

    return "unavailable"
}

function Get-GstepSelector {
    try {
        & gstep status *> $null
        if ($LASTEXITCODE -eq 0) {
            return "gstep:@"
        }
    } catch {
    }

    return "unavailable"
}

function Set-OptionalProcessEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq "all") {
        [Environment]::SetEnvironmentVariable($Name, $null, "Process")
        return
    }

    [Environment]::SetEnvironmentVariable($Name, $Value, "Process")
}

function Write-PreflightArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Status,

        [string]$FailureCategory,

        [string]$FailureMessage
    )

    $manifestSummary = Get-ManifestSummary -Path $script:ManifestPath
    $screenshotSummary = Get-ScreenshotPairSummary -CapturesDir $script:Captures
    $reportSummary = Get-ReportSummary -ReportJsonPath (Join-Path $script:Analysis "ui-parity-report.json")
    $reportPath = Join-Path $script:Analysis "ui-parity-report.md"
    $coveragePath = Join-Path $script:Analysis "ui-parity-coverage.md"
    $runMetricsPath = Join-Path $script:Captures "ui-parity-run-metrics.json"
    $runMetrics = Get-RunMetrics -Path $runMetricsPath

    $preflight = [ordered]@{
        SchemaVersion = "easydict.ui-parity.preflight.v1"
        GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString("O", [System.Globalization.CultureInfo]::InvariantCulture)
        Status = $Status
        FailureCategory = if ([string]::IsNullOrWhiteSpace($FailureCategory)) { $null } else { $FailureCategory }
        FailureMessage = if ([string]::IsNullOrWhiteSpace($FailureMessage)) { $null } else { $FailureMessage }
        RunRoot = $script:RunRoot
        CapturesDir = $script:Captures
        AnalysisDir = $script:Analysis
        Scope = $Scope
        Theme = $Theme
        UiLanguage = $UiLanguage
        MainOperationsScope = $MainOperationsScope
        MainDropdown = $MainDropdown
        FloatingScope = $FloatingScope
        FloatingWindow = $FloatingWindow
        FloatingDropdown = $FloatingDropdown
        SettingsSection = $SettingsSection
        DropdownOptionIndexes = if ([string]::IsNullOrWhiteSpace($DropdownOptionIndexes)) { $null } else { $DropdownOptionIndexes }
        SkipBuild = [bool]$SkipBuild
        SkipAnalyzerSelfTest = [bool]$SkipAnalyzerSelfTest
        RustPreviewExePath = if ([string]::IsNullOrWhiteSpace($script:RustPreviewExePath)) { $null } else { $script:RustPreviewExePath }
        ManifestPath = $script:ManifestPath
        AnalyzerReportPath = $reportPath
        CoverageReportPath = $coveragePath
        RunMetricsPath = $runMetricsPath
        RunMetrics = $runMetrics
        ReferenceScreenshotCount = $screenshotSummary.ReferenceScreenshotCount
        CandidateScreenshotCount = $screenshotSummary.CandidateScreenshotCount
        SideBySideScreenshotCount = $screenshotSummary.SideBySideScreenshotCount
        ScenarioCount = $manifestSummary.ScenarioCount
        FirstScenarioId = $manifestSummary.FirstScenarioId
        ReferenceWindow = $manifestSummary.ReferenceWindow
        CandidateWindow = $manifestSummary.CandidateWindow
        Commands = @($script:Commands)
    }

    $preflight | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $script:PreflightPath -Encoding UTF8

    $preflightLine = if ($Status -eq "pass") { "pass" } else { "fail ($FailureCategory)" }
    $missingCount = if ($Status -eq "pass") {
        0
    } elseif ($FailureCategory -in @("preflight-no-manifest", "preflight-no-screenshot", "preflight-no-window")) {
        1
    } else {
        0
    }
    $harnessInvalid = if ($Status -eq "pass") { 0 } else { 1 }
    $evidence = if ($Status -eq "pass") {
        "preflight.json; $reportPath; $coveragePath"
    } else {
        $failedCommand = @($script:Commands | Where-Object { $_.ExitCode -ne 0 } | Select-Object -Last 1)
        if ($failedCommand.Count -gt 0) {
            "preflight.json; $($failedCommand[0].StdoutPath); $($failedCommand[0].StderrPath)"
        } else {
            "preflight.json"
        }
    }
    $nextDo = if ($Status -eq "pass") {
        "Use this run root as evidence for the next coverage/root-cause pass."
    } else {
        "Fix $FailureCategory before UI token/layout changes."
    }
    $nextDoNot = if ($Status -eq "pass") {
        "Do not reuse default artifacts/ui-screenshots as the run root."
    } else {
        "Do not tune UI tokens from this run when preflight failed."
    }

    $summary = [System.Collections.Generic.List[string]]::new()
    $summary.Add("# UI parity run summary")
    $summary.Add("")
    $summary.Add("- Run root: $script:RunRoot")
    $summary.Add("- gstep selector: $(Get-GstepSelector)")
    $summary.Add("- Git HEAD: $(Get-ShortGitHead -RepositoryRoot $script:RepoRoot)")
    $summary.Add("- Scope: $Scope")
    $summary.Add("- Theme: $Theme")
    $summary.Add("- UI language: $UiLanguage")
    $summary.Add("- Main operations scope: $MainOperationsScope")
    $summary.Add("- Main dropdown: $MainDropdown")
    $summary.Add("- Floating scope: $FloatingScope")
    $summary.Add("- Floating window: $FloatingWindow")
    $summary.Add("- Floating dropdown: $FloatingDropdown")
    $summary.Add("- Settings section: $SettingsSection")
    $summary.Add("- Dropdown option indexes: $(if ([string]::IsNullOrWhiteSpace($DropdownOptionIndexes)) { 'all' } else { $DropdownOptionIndexes })")
    $summary.Add("- Skip build: $([bool]$SkipBuild)")
    $summary.Add("- Skip analyzer self-test: $([bool]$SkipAnalyzerSelfTest)")
    $summary.Add("- Preflight: $preflightLine")
    $summary.Add("- Manifest: $script:ManifestPath")
    $summary.Add("- Analyzer report: $reportPath")
    $summary.Add("- Coverage report: $coveragePath")
    $summary.Add("- Run metrics: $runMetricsPath")
    $summary.Add("")
    $summary.Add("## Commands")
    foreach ($command in $script:Commands) {
        $duration = if ($null -eq $command.DurationMs) { "n/a" } else { "$($command.DurationMs) ms" }
        $summary.Add("- $($command.Name): exit $($command.ExitCode), started $($command.StartedAtUtc), duration $duration")
    }
    $summary.Add("")
    $summary.Add("## Scenario counts")
    $summary.Add("- captured: $($screenshotSummary.CommonScenarioCount)")
    $summary.Add("- analyzed: $($reportSummary.Analyzed)")
    $summary.Add("- accepted: 0")
    $summary.Add("- low-score: $($reportSummary.LowScore)")
    $summary.Add("- harness-invalid: $harnessInvalid")
    $metricsHarnessInvalid = if ($null -ne $runMetrics) { $runMetrics.harnessInvalid } else { "n/a" }
    $metricsTimeouts = if ($null -ne $runMetrics) { $runMetrics.rustTimeouts } else { "n/a" }
    $summary.Add("- Rust render requests: $(if ($null -ne $runMetrics) { $runMetrics.rustRenderRequests } else { 'n/a' })")
    $summary.Add("- Rust render durations (ms): $(if ($null -ne $runMetrics) { ($runMetrics.rustRenderDurationsMs -join ', ') } else { 'n/a' })")
    $summary.Add("- Rust timeouts: $metricsTimeouts")
    $summary.Add("- Metrics harness-invalid: $metricsHarnessInvalid")
    $summary.Add("- missing: $missingCount")
    $summary.Add("")
    $summary.Add("## One root cause this run")
    $summary.Add("- Bucket: harness")
    $summary.Add("- Evidence: $evidence")
    $summary.Add("- Changed files:")
    $summary.Add("- Verification: Invoke-UiParityPreflight.ps1 -RunRoot $script:RunRoot -Scope $Scope")
    $summary.Add("")
    $summary.Add("## Next run")
    $summary.Add("- Do: $nextDo")
    $summary.Add("- Do not: $nextDoNot")
    $summary | Set-Content -LiteralPath $script:RunSummaryPath -Encoding UTF8

    Write-Host "Run root: $script:RunRoot"
    Write-Host "Captures: $script:Captures"
    Write-Host "Manifest: $script:ManifestPath"
    Write-Host "Analysis: $script:Analysis"
    $previewLine = if ([string]::IsNullOrWhiteSpace($script:RustPreviewExePath) -or -not (Test-Path -LiteralPath $script:RustPreviewExePath)) { "missing" } else { $script:RustPreviewExePath }
    Write-Host "Run metrics: $runMetricsPath"
    Write-Host "Rust preview exe: $previewLine"
    Write-Host "Reference screenshots: $($screenshotSummary.ReferenceScreenshotCount)"
    Write-Host "Candidate screenshots: $($screenshotSummary.CandidateScreenshotCount)"
    Write-Host "Side-by-side screenshots: $($screenshotSummary.SideBySideScreenshotCount)"
    Write-Host "First scenario: $(if ($manifestSummary.FirstScenarioId) { $manifestSummary.FirstScenarioId } else { 'n/a' })"
    $referenceDpi = if ($null -ne $manifestSummary.ReferenceWindow.Dpi) { $manifestSummary.ReferenceWindow.Dpi } else { "n/a" }
    $referenceBounds = if ($manifestSummary.ReferenceWindow.Bounds) { $manifestSummary.ReferenceWindow.Bounds } else { "n/a" }
    $candidateDpi = if ($null -ne $manifestSummary.CandidateWindow.Dpi) { $manifestSummary.CandidateWindow.Dpi } else { "n/a" }
    $candidateBounds = if ($manifestSummary.CandidateWindow.Bounds) { $manifestSummary.CandidateWindow.Bounds } else { "n/a" }
    Write-Host "Reference DPI/client rect: $referenceDpi / $referenceBounds"
    Write-Host "Candidate DPI/client rect: $candidateDpi / $candidateBounds"
}

function Complete-Preflight {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Status,

        [string]$FailureCategory,

        [string]$FailureMessage
    )

    Write-PreflightArtifacts -Status $Status -FailureCategory $FailureCategory -FailureMessage $FailureMessage
    if ($Status -eq "pass") {
        exit 0
    }

    exit 1
}

$script:RepoRoot = Resolve-RepositoryRoot
$script:RunRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RunRoot)
Initialize-RunDirectories -Root $script:RunRoot
$script:Captures = Join-Path $script:RunRoot "captures"
$script:Analysis = Join-Path $script:RunRoot "analysis"
$script:Logs = Join-Path $script:RunRoot "logs"
$script:ManifestPath = Join-Path $script:Captures "ui-parity-manifest.json"
$script:PreflightPath = Join-Path $script:RunRoot "preflight.json"
$script:RunSummaryPath = Join-Path $script:RunRoot "run-summary.md"
$script:Commands = [System.Collections.Generic.List[object]]::new()
$script:RustPreviewExePath = Join-Path $script:RepoRoot "rs\target\debug\easydict_preview_iced.exe"

$rustManifestPath = Join-Path $script:RepoRoot "rs\Cargo.toml"
$uiaTestProject = Join-Path $script:RepoRoot "dotnet\tests\Easydict.UIAutomation.Tests\Easydict.UIAutomation.Tests.csproj"
$analyzerWrapper = Join-Path $script:RepoRoot "dotnet\scripts\ci\Invoke-UiParityAnalysis.ps1"

$env:SCREENSHOT_OUTPUT_DIR = $script:Captures
$env:EASYDICT_UIA_DOTNET_RUST_PARITY = "1"
$env:EASYDICT_UIA_PARITY_UI_LANGUAGE = $UiLanguage
$env:EASYDICT_UIA_PARITY_THEME = $Theme
$selectedDropdownOptionIndexes = @(
    $DropdownOptionIndexes -split ',' |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
$script:RustPreviewSessionGate = (
    $Scope -eq "main" -and
    $MainOperationsScope -in @("dropdown-open", "dropdown-open-only", "dropdowns", "dropdown-options") -and
    $MainDropdown -eq "target"
)
$expectedDropdownOptionCount = if ($selectedDropdownOptionIndexes.Count -gt 0) {
    $selectedDropdownOptionIndexes.Count
} else {
    9
}
$script:ExpectedRustRenderCount = if ($MainOperationsScope -in @("dropdowns", "dropdown-options")) {
    1 + $expectedDropdownOptionCount
} else {
    1
}

try {
    if (-not $SkipBuild) {
        $cargoBuild = Invoke-LoggedCommand `
            -Name "cargo-build-rust-preview" `
            -FilePath "cargo" `
            -Arguments @("build", "--manifest-path", $rustManifestPath, "-p", "easydict_preview_iced", "--features", "parity-diagnostics") `
            -LogRoot $script:Logs
        if ($cargoBuild.ExitCode -ne 0) {
            Complete-Preflight -Status "fail" -FailureCategory "preflight-build-failed" -FailureMessage "Rust preview build failed."
        }
    }

    if (-not (Test-Path -LiteralPath $script:RustPreviewExePath)) {
        Complete-Preflight -Status "fail" -FailureCategory "preflight-build-failed" -FailureMessage "Rust preview exe was not found at $script:RustPreviewExePath."
    }

    $script:RustPreviewExePath = (Resolve-Path -LiteralPath $script:RustPreviewExePath).Path
    $env:EASYDICT_RUST_PREVIEW_EXE_PATH = $script:RustPreviewExePath

    if (-not $SkipAnalyzerSelfTest) {
        $analyzerSelfTest = Invoke-LoggedCommand `
            -Name "ui-parity-analyzer-self-test" `
            -FilePath "powershell.exe" `
            -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $analyzerWrapper, "-ScreenshotRoot", $script:Captures, "-OutputDir", $script:Analysis) `
            -LogRoot $script:Logs
        if ($analyzerSelfTest.ExitCode -ne 0) {
            Complete-Preflight -Status "fail" -FailureCategory "preflight-analyzer-failed" -FailureMessage "UI parity analyzer self-test failed."
        }
    }

    if (-not $SkipBuild) {
        $dotnetBuild = Invoke-LoggedCommand `
            -Name "dotnet-build-uia-tests" `
            -FilePath "dotnet" `
            -Arguments @("build", $uiaTestProject, "--configuration", "Debug", "-p:Platform=x64", "-p:BuildWorkerOutputs=false", "-p:EnableInProcLongDocFallback=false", "-p:RuntimeProfile=rust-only") `
            -LogRoot $script:Logs
        if ($dotnetBuild.ExitCode -ne 0) {
            Complete-Preflight -Status "fail" -FailureCategory "preflight-build-failed" -FailureMessage "UI automation test project build failed."
        }
    }

    $scopeFilters = @{
        main = "FullyQualifiedName~Easydict.UIAutomation.Tests.Tests.DotnetRustParityTests.MainWindowOperations_ShouldRenderDotnetAndRustPreviewSideBySide"
        floating = "FullyQualifiedName~Easydict.UIAutomation.Tests.Tests.DotnetRustParityTests.FloatingWindows_ShouldRenderDotnetAndRustPreviewSideBySide"
        settings = "FullyQualifiedName~Easydict.UIAutomation.Tests.Tests.DotnetRustParityTests.Settings_ShouldRenderDotnetAndRustPreviewSideBySide"
        effects = "FullyQualifiedName~Easydict.UIAutomation.Tests.Tests.DotnetRustParityTests.MainWindowEffects_ShouldRenderDotnetAndRustPreviewSideBySide"
        popbutton = "FullyQualifiedName~Easydict.UIAutomation.Tests.Tests.DotnetRustParityTests.PopButton_ShouldRenderDotnetAndRustPreviewSideBySide"
        ocr = "FullyQualifiedName~Easydict.UIAutomation.Tests.Tests.DotnetRustParityTests.OcrOverlay_ShouldRenderDotnetAndRustPreviewSideBySide"
    }
    foreach ($filterVariable in @(
            "EASYDICT_UIA_PARITY_MAIN_OPERATIONS_SCOPE",
            "EASYDICT_UIA_PARITY_MAIN_DROPDOWN",
            "EASYDICT_UIA_PARITY_FLOATING_SCOPE",
            "EASYDICT_UIA_PARITY_FLOATING_WINDOW",
            "EASYDICT_UIA_PARITY_FLOATING_DROPDOWN",
            "EASYDICT_UIA_PARITY_SETTINGS_SECTION",
            "EASYDICT_UIA_PARITY_MAIN_INITIAL_ONLY",
            "EASYDICT_UIA_PARITY_DROPDOWN_OPTION_INDEXES")) {
        Set-OptionalProcessEnvironment -Name $filterVariable -Value "all"
    }
    Set-OptionalProcessEnvironment -Name "EASYDICT_UIA_PARITY_DROPDOWN_OPTION_INDEXES" -Value $DropdownOptionIndexes
    switch ($Scope) {
        "main" {
            Set-OptionalProcessEnvironment -Name "EASYDICT_UIA_PARITY_MAIN_OPERATIONS_SCOPE" -Value $MainOperationsScope
            Set-OptionalProcessEnvironment -Name "EASYDICT_UIA_PARITY_MAIN_DROPDOWN" -Value $MainDropdown
        }
        "floating" {
            Set-OptionalProcessEnvironment -Name "EASYDICT_UIA_PARITY_FLOATING_SCOPE" -Value $FloatingScope
            Set-OptionalProcessEnvironment -Name "EASYDICT_UIA_PARITY_FLOATING_WINDOW" -Value $FloatingWindow
            Set-OptionalProcessEnvironment -Name "EASYDICT_UIA_PARITY_FLOATING_DROPDOWN" -Value $FloatingDropdown
        }
        "settings" {
            Set-OptionalProcessEnvironment -Name "EASYDICT_UIA_PARITY_SETTINGS_SECTION" -Value $SettingsSection
        }
        "effects" {
            $env:EASYDICT_UIA_PARITY_MAIN_INITIAL_ONLY = "1"
        }
    }

    $dotnetTest = Invoke-LoggedCommand `
        -Name "dotnet-test-uia-$Scope" `
        -FilePath "dotnet" `
        -Arguments @(
            "test",
            $uiaTestProject,
            "--configuration",
            "Debug",
            "--no-build",
            "--verbosity",
            "normal",
            "--logger",
            "console;verbosity=detailed",
            "-p:Platform=x64",
            "-p:BuildWorkerOutputs=false",
            "-p:EnableInProcLongDocFallback=false",
            "-p:RuntimeProfile=rust-only",
            "--filter",
            $scopeFilters[$Scope]) `
        -LogRoot $script:Logs

    if ($script:RustPreviewSessionGate) {
        $sessionMetricsPath = Join-Path $script:Captures "ui-parity-run-metrics.json"
        $sessionMetrics = Get-RunMetrics -Path $sessionMetricsPath
        if ($null -eq $sessionMetrics) {
            Complete-Preflight -Status "fail" -FailureCategory "preflight-rust-preview-session-gate-failed" -FailureMessage "Rust preview session did not write run metrics."
        }

        $sessionDurations = @($sessionMetrics.rustRenderDurationsMs)
        $sessionFailed = (
            [int]$sessionMetrics.rustProcessStarts -ne 1 -or
            [int]$sessionMetrics.rustRenderRequests -ne $script:ExpectedRustRenderCount -or
            [int]$sessionMetrics.rustTimeouts -ne 0 -or
            [int]$sessionMetrics.harnessInvalid -ne 0 -or
            $sessionDurations.Count -ne $script:ExpectedRustRenderCount
        )
        if (-not $sessionFailed -and $sessionDurations.Count -gt 0) {
            $sessionFailed = (($sessionDurations | Measure-Object -Maximum).Maximum -gt 5000)
        }
        if ($sessionFailed) {
            $metricsSummary = "processes=$($sessionMetrics.rustProcessStarts), renders=$($sessionMetrics.rustRenderRequests)/$($script:ExpectedRustRenderCount), timeouts=$($sessionMetrics.rustTimeouts), harnessInvalid=$($sessionMetrics.harnessInvalid), durationsMs=$($sessionDurations -join ',')"
            Complete-Preflight -Status "fail" -FailureCategory "preflight-rust-preview-session-gate-failed" -FailureMessage "Rust preview session gate failed: $metricsSummary."
        }
    }

    $screenshots = Get-ScreenshotPairSummary -CapturesDir $script:Captures
    $manifest = Get-ManifestSummary -Path $script:ManifestPath
    if (-not (Test-Path -LiteralPath $script:ManifestPath)) {
        if ($screenshots.ReferenceScreenshotCount -eq 0 -and $screenshots.CandidateScreenshotCount -eq 0) {
            Complete-Preflight -Status "fail" -FailureCategory "preflight-no-window" -FailureMessage "UIA smoke produced no manifest and no reference/candidate screenshots."
        }

        Complete-Preflight -Status "fail" -FailureCategory "preflight-no-manifest" -FailureMessage "UIA smoke produced screenshots but no ui-parity-manifest.json."
    }

    if ($manifest.ScenarioCount -lt 1) {
        Complete-Preflight -Status "fail" -FailureCategory "preflight-no-manifest" -FailureMessage "ui-parity-manifest.json contains no scenarios."
    }

    if ($screenshots.CommonScenarioCount -lt 1) {
        Complete-Preflight -Status "fail" -FailureCategory "preflight-no-screenshot" -FailureMessage "No common scenario stem had reference, candidate, and side-by-side screenshots."
    }

    $finalAnalysis = Invoke-LoggedCommand `
        -Name "ui-parity-analyzer-manifest" `
        -FilePath "powershell.exe" `
        -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $analyzerWrapper, "-ScreenshotRoot", $script:Captures, "-OutputDir", $script:Analysis, "-RequireManifest", "-ManifestOnly", "-SkipSelfTest") `
        -LogRoot $script:Logs
    $reportPath = Join-Path $script:Analysis "ui-parity-report.md"
    $coveragePath = Join-Path $script:Analysis "ui-parity-coverage.md"
    if ($finalAnalysis.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath) -or -not (Test-Path -LiteralPath $coveragePath)) {
        Complete-Preflight -Status "fail" -FailureCategory "preflight-analyzer-failed" -FailureMessage "Manifest-required UI parity analysis failed or did not write expected reports."
    }

    if ($dotnetTest.ExitCode -ne 0) {
        Complete-Preflight -Status "fail" -FailureCategory "preflight-uia-failed" -FailureMessage "UIA smoke failed after writing usable preflight evidence."
    }

    Complete-Preflight -Status "pass"
} catch {
    Complete-Preflight -Status "fail" -FailureCategory "preflight-analyzer-failed" -FailureMessage $_.Exception.Message
}

param(
    [string]$InputFile,

    [string]$TargetLanguage,

    [string]$EnvFile,
    [string]$SourceLanguage = "auto",
    [string]$OutputFile,
    [string]$ServiceId,
    [string]$OutputMode,
    [string]$Layout,
    [string]$PdfExportMode,
    [string]$PageRange,
    [int]$MaxConcurrency,
    [string]$VisionEndpoint,
    [string]$VisionApiKey,
    [string]$VisionModel,
    [switch]$ListServices
)

$projectPath = Join-Path $PSScriptRoot "..\dotnet\src\Easydict.WinUI\Easydict.WinUI.csproj"
$arguments = @(
    "run",
    "--project", $projectPath,
    "-p:WindowsPackageType=None",
    "-p:EnableLocalDebugLongDocCli=true",
    "--",
    "--translate-long-doc"
)

if ($ListServices) {
    $arguments += "--list-services"
}
else {
    if (-not $InputFile) { throw "InputFile is required unless -ListServices is used." }
    if (-not $TargetLanguage) { throw "TargetLanguage is required unless -ListServices is used." }

    $arguments += @("--input", $InputFile, "--target-language", $TargetLanguage)

    if ($SourceLanguage) { $arguments += @("--from", $SourceLanguage) }
    if ($EnvFile) { $arguments += @("--env-file", $EnvFile) }
    if ($OutputFile) { $arguments += @("--output", $OutputFile) }
    if ($ServiceId) { $arguments += @("--service", $ServiceId) }
    if ($OutputMode) { $arguments += @("--output-mode", $OutputMode) }
    if ($Layout) { $arguments += @("--layout", $Layout) }
    if ($PdfExportMode) { $arguments += @("--pdf-export-mode", $PdfExportMode) }
    if ($PageRange) { $arguments += @("--page-range", $PageRange) }
    if ($PSBoundParameters.ContainsKey("MaxConcurrency")) { $arguments += @("--max-concurrency", $MaxConcurrency) }
    if ($VisionEndpoint) { $arguments += @("--vision-endpoint", $VisionEndpoint) }
    if ($VisionApiKey) { $arguments += @("--vision-api-key", $VisionApiKey) }
    if ($VisionModel) { $arguments += @("--vision-model", $VisionModel) }
}

& dotnet @arguments
exit $LASTEXITCODE

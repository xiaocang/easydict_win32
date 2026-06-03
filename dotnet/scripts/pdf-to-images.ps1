param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$InputPdf,

    [Parameter(Position = 1)]
    [string]$OutputDir,

    [double]$Dpi = 144,

    [ValidateSet("png", "jpg", "jpeg")]
    [string]$Format = "png",

    [double]$Scale
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $repoRoot
$rustManifest = Join-Path $workspaceRoot "rs\Cargo.toml"
$resolvedFormat = if ($Format -eq "jpeg") { "jpg" } else { $Format }

if (-not (Test-Path $InputPdf)) {
    throw "Input PDF not found: $InputPdf"
}

function Get-DefaultOutputDir {
    param([string]$PdfPath)

    $sourceDir = Split-Path -Parent $PdfPath
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($PdfPath)
    return Join-Path $sourceDir ($baseName + "_pages")
}

$resolvedInput = (Resolve-Path $InputPdf).Path
$resolvedOutputDir = if ($OutputDir) { $OutputDir } else { Get-DefaultOutputDir -PdfPath $resolvedInput }
$resolvedOutputDir = [System.IO.Path]::GetFullPath($resolvedOutputDir)
New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

Write-Host "Converting PDF pages to images..."
Write-Host "Tool       : easydict_pdf_to_images"
Write-Host "Input PDF   : $resolvedInput"
Write-Host "Output dir  : $resolvedOutputDir"

$arguments = @(
    "run",
    "--manifest-path", $rustManifest,
    "-p", "easydict_pdf_to_images",
    "--",
    "--input", $resolvedInput,
    "--output-dir", $resolvedOutputDir,
    "--format", $resolvedFormat
)

if ($PSBoundParameters.ContainsKey("Scale")) {
    $arguments += @("--scale", $Scale.ToString([System.Globalization.CultureInfo]::InvariantCulture))
}
else {
    $arguments += @("--dpi", $Dpi.ToString([System.Globalization.CultureInfo]::InvariantCulture))
}

& cargo @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

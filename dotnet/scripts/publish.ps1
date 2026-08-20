# Easydict WinUI Publish Script
# Creates a self-contained deployment that includes .NET runtime + workers.

param(
    [ValidateSet("x64", "x86", "arm64")]
    [string]$Platform = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$CreateZip
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionDir = Split-Path -Parent $ScriptDir
$ProjectPath = Join-Path $SolutionDir "src\Easydict.WinUI\Easydict.WinUI.csproj"
$PublishDir = Join-Path $SolutionDir "src\Easydict.WinUI\bin\publish\win-$Platform"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Easydict WinUI Publisher" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Platform:      $Platform"
Write-Host "Configuration: $Configuration"
Write-Host "Output:        $PublishDir"
Write-Host ""

if (Test-Path $PublishDir) {
    Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
    Remove-Item -Path $PublishDir -Recurse -Force
}

Write-Host "Publishing self-contained app (workers auto-published by PublishWorkerOutputs)..." -ForegroundColor Green
dotnet publish $ProjectPath `
    -c $Configuration `
    -p:Platform=$Platform `
    --self-contained true `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed!" -ForegroundColor Red
    exit 1
}

$ocrExe = Join-Path $PublishDir "workers\ocr\Easydict.Workers.Ocr.exe"
$longDocExe = Join-Path $PublishDir "workers\longdoc\Easydict.Workers.LongDoc.exe"
$localAiExe = Join-Path $PublishDir "workers\localai\Easydict.Workers.LocalAi.exe"
if (-not (Test-Path $ocrExe) -and $Platform -ne "x86") {
    Write-Host "ERROR: OCR worker not found at $ocrExe - PublishWorkerOutputs did not run." -ForegroundColor Red
    Write-Host "Hint: ensure -p:PublishWorkerOutputs is not set to false and dotnet SDK is available in PATH." -ForegroundColor Yellow
    exit 1
}
if (Test-Path $ocrExe) { Write-Host "Worker OK: workers/ocr/Easydict.Workers.Ocr.exe" -ForegroundColor Green }
if (Test-Path $longDocExe) { Write-Host "Worker OK: workers/longdoc/Easydict.Workers.LongDoc.exe" -ForegroundColor Green }
if (Test-Path $localAiExe) { Write-Host "Worker OK: workers/localai/Easydict.Workers.LocalAi.exe" -ForegroundColor Green }

$BridgeProject = Join-Path $SolutionDir "src\Easydict.NativeBridge\Easydict.NativeBridge.csproj"
$BridgePublishDir = Join-Path $SolutionDir "src\Easydict.NativeBridge\bin\publish\win-$Platform"

Write-Host "Publishing NativeBridge..." -ForegroundColor Green
dotnet publish $BridgeProject `
    -c $Configuration `
    -r "win-$Platform" `
    --self-contained true `
    -o $BridgePublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "NativeBridge publish failed!" -ForegroundColor Red
    exit 1
}

$BridgeExe = Join-Path $BridgePublishDir "easydict-native-bridge.exe"
if (Test-Path $BridgeExe) {
    Copy-Item $BridgeExe -Destination $PublishDir
    Write-Host "Copied easydict-native-bridge.exe to publish output" -ForegroundColor Green
} else {
    Write-Host "WARNING: easydict-native-bridge.exe not found at $BridgeExe" -ForegroundColor Yellow
}

$RegistrarProject = Join-Path $SolutionDir "src\Easydict.BrowserRegistrar\Easydict.BrowserRegistrar.csproj"
$RegistrarPublishDir = Join-Path $SolutionDir "src\Easydict.BrowserRegistrar\bin\publish\win-$Platform"

Write-Host "Publishing BrowserRegistrar..." -ForegroundColor Green
dotnet publish $RegistrarProject `
    -c $Configuration `
    -r "win-$Platform" `
    --self-contained true `
    -o $RegistrarPublishDir `
    -p:PublishTrimmed=true

if ($LASTEXITCODE -ne 0) {
    Write-Host "BrowserRegistrar publish failed!" -ForegroundColor Red
    exit 1
}

$RegistrarExe = Join-Path $RegistrarPublishDir "BrowserHostRegistrar.exe"
if (Test-Path $RegistrarExe) {
    Copy-Item $RegistrarExe -Destination $PublishDir
    Write-Host "Copied BrowserHostRegistrar.exe to publish output" -ForegroundColor Green
} else {
    Write-Host "WARNING: BrowserHostRegistrar.exe not found at $RegistrarExe" -ForegroundColor Yellow
}

$files = Get-ChildItem -Path $PublishDir -Recurse -File
$totalSize = ($files | Measure-Object -Property Length -Sum).Sum
$sizeMB = [math]::Round($totalSize / 1MB, 2)

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Publish Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "Files:  $($files.Count)"
Write-Host "Size:   $sizeMB MB"
Write-Host "Output: $PublishDir"

if ($CreateZip) {
    $zipPath = Join-Path $SolutionDir "Easydict-win-$Platform-$Configuration.zip"
    Write-Host ""
    Write-Host "Creating ZIP archive..." -ForegroundColor Yellow

    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Compress-Archive -Path "$PublishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

    $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "ZIP created: $zipPath ($zipSize MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "To run the app:" -ForegroundColor Cyan
Write-Host "  $PublishDir\Easydict.WinUI.exe"

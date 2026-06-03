# Easydict WinUI Publish Script
# Creates a self-contained deployment that includes .NET runtime

param(
    [ValidateSet("x64", "x86", "arm64")]
    [string]$Platform = "x64",
    
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("Hybrid", "RustOnly")]
    [string]$RuntimeProfile = "Hybrid",
    
    [switch]$CreateZip
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionDir = Split-Path -Parent $ScriptDir
$RepoRoot = Split-Path -Parent $SolutionDir
$CargoManifest = Join-Path $RepoRoot "rs\Cargo.toml"
$ProjectPath = Join-Path $SolutionDir "src\Easydict.WinUI\Easydict.WinUI.csproj"
$PublishDir = Join-Path $SolutionDir "src\Easydict.WinUI\bin\publish\win-$Platform"
$IsRustOnlyRuntime = $RuntimeProfile -eq "RustOnly"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Easydict WinUI Publisher" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Platform:      $Platform"
Write-Host "Configuration: $Configuration"
Write-Host "Runtime:       $RuntimeProfile"
Write-Host "Output:        $PublishDir"
Write-Host ""

# Clean previous publish
if (Test-Path $PublishDir) {
    Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
    Remove-Item -Path $PublishDir -Recurse -Force
}

# Publish
Write-Host "Publishing self-contained app..." -ForegroundColor Green
dotnet publish $ProjectPath `
    -c $Configuration `
    -p:Platform=$Platform `
    --self-contained true `
    -o $PublishDir `
    -p:BuildWorkerOutputs=false `
    -p:EnableInProcLongDocFallback=false `
    -p:RuntimeProfile=$RuntimeProfile

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed!" -ForegroundColor Red
    exit 1
}

if ($IsRustOnlyRuntime) {
    Write-Host "Skipping retained .NET workers for RustOnly runtime profile." -ForegroundColor Yellow
} elseif ($Platform -ne "x86") {
    Write-Host "Publishing remaining .NET workers..." -ForegroundColor Green
    dotnet publish (Join-Path $SolutionDir "src\Easydict.Workers.LongDoc\Easydict.Workers.LongDoc.csproj") `
        -c $Configuration `
        -r "win-$Platform" `
        --self-contained true `
        -o (Join-Path $PublishDir "workers\longdoc") `
        -p:Platform=$Platform `
        -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) {
        Write-Host "LongDoc worker publish failed!" -ForegroundColor Red
        exit 1
    }

    dotnet publish (Join-Path $SolutionDir "src\Easydict.Workers.LocalAi\Easydict.Workers.LocalAi.csproj") `
        -c $Configuration `
        -r "win-$Platform" `
        --self-contained true `
        -o (Join-Path $PublishDir "workers\localai") `
        -p:Platform=$Platform `
        -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) {
        Write-Host "LocalAI worker publish failed!" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "Skipping .NET workers for x86; worker projects do not support win-x86." -ForegroundColor Yellow
}

# Build Rust-owned helper executables and copy them beside the app.
# These names are consumed by the Rust desktop/browser support runtime.
Write-Host "Publishing Rust helper executables..." -ForegroundColor Green
& (Join-Path $ScriptDir "Build-RustHelpers.ps1") `
    -Platform $Platform `
    -Configuration $Configuration `
    -OutputDir $PublishDir

# Calculate size
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

# Create ZIP if requested
if ($CreateZip) {
    $zipPath = Join-Path $SolutionDir "Easydict-win-$Platform-$Configuration.zip"
    Write-Host ""
    Write-Host "Creating ZIP archive..." -ForegroundColor Yellow
    
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    cargo run --manifest-path $CargoManifest -p easydict_packager -- `
        zip-directory `
        --source $PublishDir `
        --destination $zipPath
    if ($LASTEXITCODE -ne 0) {
        throw "Rust ZIP creation failed"
    }
    
    $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "ZIP created: $zipPath ($zipSize MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "To run the app:" -ForegroundColor Cyan
Write-Host "  $PublishDir\Easydict.WinUI.exe"


[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][int]$ExpectedOsBuild,
    [string]$ResultsDirectory = "artifacts/portable-settings-smoke"
)

$ErrorActionPreference = 'Stop'
$original = @{}
$variables = 'EASYDICT_EXE_PATH','EASYDICT_EXPECTED_OS_BUILD','EASYDICT_SETTINGS_DIR','SCREENSHOT_OUTPUT_DIR'
foreach ($name in $variables) { $original[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
$tempRoot = $null
$launchedRoot = $null
try {
    $currentBuild = [int](Get-ItemPropertyValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' CurrentBuild)
    if ($currentBuild -ne $ExpectedOsBuild -or $currentBuild -ge 22000) {
        throw "Portable smoke requires Windows build $ExpectedOsBuild below 22000; current build is $currentBuild."
    }
    if (Get-Process -Name Easydict.WinUI -ErrorAction SilentlyContinue) {
        throw 'Easydict.WinUI is already running. Close the existing app before running portable smoke.'
    }
    if (-not (Test-Path -LiteralPath $ZipPath)) { throw "ZIP not found: $ZipPath" }
    $results = [IO.Path]::GetFullPath($ResultsDirectory)
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('easydict-portable-smoke-' + [guid]::NewGuid())
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $tempRoot
    $exe = @(Get-ChildItem -LiteralPath $tempRoot -Filter 'Easydict.WinUI.exe' -File -Recurse)
    if ($exe.Count -ne 1 -or $exe[0].DirectoryName -ne $tempRoot) { throw 'ZIP must contain exactly one root Easydict.WinUI.exe.' }
    $launchedRoot = $tempRoot
    $settings = Join-Path $tempRoot 'settings-isolated'
    New-Item -ItemType Directory -Force -Path $settings | Out-Null
    $env:EASYDICT_EXE_PATH = $exe[0].FullName
    $env:EASYDICT_EXPECTED_OS_BUILD = [string]$ExpectedOsBuild
    $env:EASYDICT_SETTINGS_DIR = $settings
    $env:SCREENSHOT_OUTPUT_DIR = $results
    $trx = 'portable-settings-smoke.trx'
    dotnet test (Join-Path $PSScriptRoot '..\..\tests\Easydict.UIAutomation.Tests\Easydict.UIAutomation.Tests.csproj') --filter 'FullyQualifiedName=Easydict.UIAutomation.Tests.Tests.PortableCompatibilityTests.PortableBuild_SettingsPageOpensAndDegradesGracefullyOnDownlevelWindows' --logger "trx;LogFileName=$trx" --results-directory $results
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process') }
    if ($launchedRoot) {
        $rootPrefix = $launchedRoot.TrimEnd('\') + '\'
        Get-Process -Name Easydict.WinUI -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
        } | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    if ($tempRoot -and (Test-Path $tempRoot)) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

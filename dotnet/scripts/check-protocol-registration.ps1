param(
    [string]$Protocol = "easydict"
)

$ErrorActionPreference = "Stop"

$basePath = "HKCU:\Software\Classes\$Protocol"
$commandPath = "$basePath\shell\open\command"

Write-Host "Checking protocol registration for '$Protocol'..." -ForegroundColor Cyan
Write-Host "Base key:    $basePath"
Write-Host "Command key: $commandPath"
Write-Host ""

if (-not (Test-Path $basePath)) {
    Write-Error "Missing registry key: $basePath"
    exit 1
}

if (-not (Test-Path $commandPath)) {
    Write-Error "Missing registry key: $commandPath"
    exit 1
}

$baseKey = Get-Item -Path $basePath
$commandKey = Get-Item -Path $commandPath

$description = $baseKey.GetValue("")
$urlProtocol = $baseKey.GetValue("URL Protocol", $null)
$command = $commandKey.GetValue("")

Write-Host "Description : $description"
Write-Host "URL Protocol: $(if ($null -eq $urlProtocol) { '<missing>' } else { '<present>' })"
Write-Host "Command     : $command"

if ([string]::IsNullOrWhiteSpace($command)) {
    Write-Error "Protocol command is empty."
    exit 1
}

$exePath = $null
if ($command -match '^\s*"([^"]+)"') {
    $exePath = $Matches[1]
}
elseif ($command -match '^\s*([^\s]+)') {
    $exePath = $Matches[1]
}

if ([string]::IsNullOrWhiteSpace($exePath)) {
    Write-Error "Unable to parse executable path from command."
    exit 1
}

$exeExists = Test-Path -Path $exePath -PathType Leaf
Write-Host "Executable  : $exePath"
Write-Host "Exe Exists  : $exeExists"

if (-not $exeExists) {
    Write-Error "Protocol command points to a missing executable."
    exit 1
}

if ($null -eq $urlProtocol) {
    Write-Error "'URL Protocol' value is missing."
    exit 1
}

Write-Host ""
Write-Host "Protocol registration looks valid." -ForegroundColor Green

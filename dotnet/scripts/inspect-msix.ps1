$msixPath = ".\msix\Easydict-v0.2.0-x64.msix"
$zipPath = Join-Path $env:TEMP "msix-inspect.zip"
$extractDir = Join-Path $env:TEMP "msix-inspect"

Copy-Item $msixPath $zipPath -Force
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $extractDir

Write-Host "=== Top-level files ==="
Get-ChildItem $extractDir | Format-Table Name, Length -AutoSize

Write-Host "=== resources.pri exists? ==="
$priFile = Join-Path $extractDir "resources.pri"
if (Test-Path $priFile) {
    Write-Host "YES - Size: $((Get-Item $priFile).Length) bytes"
} else {
    Write-Host "NO - resources.pri is MISSING from the MSIX!"
}

Write-Host "=== Strings directory? ==="
$stringsDir = Join-Path $extractDir "Strings"
if (Test-Path $stringsDir) {
    Write-Host "YES"
    Get-ChildItem $stringsDir -Recurse | Format-Table FullName -AutoSize
} else {
    Write-Host "NO Strings directory"
}

# Cleanup
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue

# Retired local release entrypoint.
#
# The first Rust release is portable-only by default. Release creation must flow
# through .github/workflows/release-publish.yml so the rs portable payload,
# checksum, and no-.NET-runtime release contracts run before assets are uploaded.

param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$FromTag,

    [switch]$PreRelease
)

$ErrorActionPreference = "Stop"

$script:unused = $FromTag, $PreRelease

Write-Host "dotnet/scripts/release.ps1 is retired." -ForegroundColor Yellow
Write-Host ""
Write-Host "Create the tag and let the Release and Publish workflow build the first rs portable package:" -ForegroundColor Cyan
Write-Host "  git tag $Tag"
Write-Host "  git push origin $Tag"
Write-Host ""
Write-Host "The workflow default release_flavor is rs-portable and runs easydict_packager pack-rs-portable, validate-rs-portable, checksum verification, and retained .NET payload guards." -ForegroundColor Cyan
Write-Host "Use workflow_dispatch release_flavor=hybrid only for the explicit legacy/coexistence artifact set." -ForegroundColor Cyan

exit 1

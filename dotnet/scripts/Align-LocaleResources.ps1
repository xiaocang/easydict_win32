#requires -Version 7
<#
.SYNOPSIS
Aligns per-locale .resw files with en-US/Resources.resw so every locale exposes
the same set of resource keys.

When a key is present in en-US but missing in a locale, this script appends the
key to the locale file using the en-US value as fallback. Existing translations
are left untouched. The translation team can replace the English fallback values
later without re-running this script.
#>

[CmdletBinding()]
param(
    [string] $StringsRoot = (Join-Path $PSScriptRoot '..\src\Easydict.WinUI\Strings')
)

$ErrorActionPreference = 'Stop'

$enUsPath = Join-Path $StringsRoot 'en-US\Resources.resw'
if (-not (Test-Path $enUsPath)) {
    throw "en-US source not found: $enUsPath"
}

# Capture each <data name="K" ...>...</data> block (single- or multi-line).
$dataRegex = [regex] '(?s)<data\s+name="(?<name>[^"]+)"[^>]*>.*?</data>'

function Read-DataBlocks {
    param([string] $Path)
    $text = Get-Content -Raw -LiteralPath $Path
    $matches = $dataRegex.Matches($text)
    $blocks = [ordered]@{}
    foreach ($m in $matches) {
        $name = $m.Groups['name'].Value
        if (-not $blocks.Contains($name)) {
            $blocks[$name] = $m.Value
        }
    }
    return @{ Text = $text; Blocks = $blocks }
}

$enUs = Read-DataBlocks -Path $enUsPath
Write-Host "en-US has $($enUs.Blocks.Count) keys"

$locales = Get-ChildItem -Directory -Path $StringsRoot |
    Where-Object { $_.Name -ne 'en-US' } |
    Select-Object -ExpandProperty Name

foreach ($locale in $locales) {
    $path = Join-Path $StringsRoot "$locale\Resources.resw"
    if (-not (Test-Path $path)) {
        Write-Warning "Skipping missing file: $path"
        continue
    }

    $loc = Read-DataBlocks -Path $path
    $missing = @($enUs.Blocks.Keys | Where-Object { -not $loc.Blocks.Contains($_) })

    if ($missing.Count -eq 0) {
        Write-Host ("  {0}: already aligned" -f $locale)
        continue
    }

    $insertions = foreach ($key in $missing) { $enUs.Blocks[$key] }
    $payload = ([string]::Join("`r`n  ", $insertions))

    # Append just before </root>, mirroring the indentation of the existing block list.
    $closingTag = '</root>'
    if (-not $loc.Text.Contains($closingTag)) {
        Write-Warning "Skipping $locale — no </root> tag found"
        continue
    }

    $insertion = "  $payload`r`n$closingTag"
    $newText = $loc.Text.Replace($closingTag, $insertion)
    [System.IO.File]::WriteAllText($path, $newText, [System.Text.UTF8Encoding]::new($false))
    Write-Host ("  {0}: appended {1} missing keys" -f $locale, $missing.Count)
}

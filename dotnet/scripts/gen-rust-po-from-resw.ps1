#!/usr/bin/env pwsh
# Generates Rust .po locale files from the .NET .resw resources.
#
# Strategy (gap-fill, all languages):
#   1. The en-US.po is the source of truth for WHICH keys the Rust app needs
#      (its dotted keys) and their en-US values.
#   2. Map each Rust dotted key -> .NET resource name by matching the en-US value
#      (the Rust en-US strings were originally copied from .NET).
#   3. For every other language, emit a .po with the same key set, pulling each
#      translation from that language's .resw via the mapping. Missing matches
#      fall back to the en-US value so the key set stays complete and live.
#
# en-US.po and zh-CN.po are hand-authored and preserved; this script only
# (re)generates the additional languages.

param(
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
)

$ErrorActionPreference = 'Stop'

$localesDir = Join-Path $RepoRoot 'rs\crates\easydict_app\locales'
$reswDir    = Join-Path $RepoRoot 'dotnet\src\Easydict.WinUI\Strings'
$enPoPath   = Join-Path $localesDir 'en-US.po'

# Languages to generate (all .NET languages except the two hand-authored ones).
$targetLangs = @(
    'ar-SA','da-DK','de-DE','fr-FR','hi-IN','id-ID','it-IT','ja-JP',
    'ko-KR','ms-MY','th-TH','vi-VN','zh-TW'
)

function Read-Po([string]$path) {
    $entries = [System.Collections.Generic.List[object]]::new()
    $id = $null; $val = $null; $field = ''
    foreach ($line in Get-Content -LiteralPath $path -Encoding UTF8) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        if ($t.StartsWith('msgid ')) {
            if ($null -ne $id -and $id -ne '') { $entries.Add([pscustomobject]@{ Key = $id; Value = $val }) }
            $id = Unquote-Po $t.Substring(6); $val = ''; $field = 'id'
        } elseif ($t.StartsWith('msgstr ')) {
            $val = Unquote-Po $t.Substring(7); $field = 'str'
        } elseif ($t.StartsWith('"')) {
            $piece = Unquote-Po $t
            if ($field -eq 'id') { $id += $piece } elseif ($field -eq 'str') { $val += $piece }
        }
    }
    if ($null -ne $id -and $id -ne '') { $entries.Add([pscustomobject]@{ Key = $id; Value = $val }) }
    return $entries
}

function Unquote-Po([string]$token) {
    $token = $token.Trim()
    if ($token.StartsWith('"') -and $token.EndsWith('"') -and $token.Length -ge 2) {
        $token = $token.Substring(1, $token.Length - 2)
    }
    $sb = [System.Text.StringBuilder]::new()
    for ($i = 0; $i -lt $token.Length; $i++) {
        $c = $token[$i]
        if ($c -eq '\' -and $i + 1 -lt $token.Length) {
            $n = $token[$i + 1]; $i++
            switch ($n) {
                'n' { [void]$sb.Append("`n") }
                't' { [void]$sb.Append("`t") }
                'r' { [void]$sb.Append("`r") }
                '"' { [void]$sb.Append('"') }
                '\' { [void]$sb.Append('\') }
                default { [void]$sb.Append($n) }
            }
        } else { [void]$sb.Append($c) }
    }
    return $sb.ToString()
}

function Escape-Po([string]$s) {
    if ($null -eq $s) { return '' }
    $s = $s -replace '\\', '\\'
    $s = $s -replace '"', '\"'
    $s = $s -replace "`r`n", '\n'
    $s = $s -replace "`n", '\n'
    $s = $s -replace "`r", '\n'
    $s = $s -replace "`t", '\t'
    return $s
}

function Read-Resw([string]$path) {
    [xml]$doc = Get-Content -LiteralPath $path -Encoding UTF8 -Raw
    $map = @{}
    foreach ($d in $doc.root.data) {
        if ($null -ne $d.name) { $map[$d.name] = [string]$d.value }
    }
    return $map
}

# 1. Read en-US.po (key order + en values) and en-US.resw (name -> value).
$enPo = Read-Po $enPoPath
$enResw = Read-Resw (Join-Path $reswDir 'en-US\Resources.resw')

# value -> .NET name (first wins on duplicate values)
$valueToName = @{}
foreach ($name in $enResw.Keys) {
    $v = $enResw[$name]
    if (-not $valueToName.ContainsKey($v)) { $valueToName[$v] = $name }
}

# Supplemental mapping for keys whose Rust en-US wording was customized and so
# does not match a .NET value verbatim, but which clearly correspond to a .NET
# resource (verified by meaning). These take precedence over value matching.
$supplementalMap = @{
    'settings.unsaved.title'                    = 'UnsavedChangesTitle'
    'settings.unsaved.message'                  = 'UnsavedChangesMessage'
    'settings.services.international.description' = 'EnableInternationalServicesDescription'
    'settings.general.mouse_selection'          = 'MouseSelectionTranslate'
    'settings.general.auto_play_translation'    = 'AutoPlayTranslation'
    'main.completed'                            = 'ServiceResultsComplete'
}

# 2. Build key -> .NET name mapping by en-US value match (+ supplemental).
$keyToName = @{}
$unmatched = [System.Collections.Generic.List[string]]::new()
foreach ($e in $enPo) {
    if ($supplementalMap.ContainsKey($e.Key)) {
        $keyToName[$e.Key] = $supplementalMap[$e.Key]
    } elseif ($valueToName.ContainsKey($e.Value)) {
        $keyToName[$e.Key] = $valueToName[$e.Value]
    } else {
        $unmatched.Add($e.Key)
    }
}

Write-Host "Mapped $($keyToName.Count)/$($enPo.Count) keys to .NET names."
Write-Host "Unmatched (will fall back to en-US value): $($unmatched.Count)"
$unmatched | ForEach-Object { Write-Host "  - $_" }

# Placeholder fix: .NET uses {0}; Rust uses named {count}/{version}.
function Fix-Placeholders([string]$value, [string]$enValue) {
    if ($enValue -match '\{count\}') { return ($value -replace '\{0(:[^}]*)?\}', '{count}') }
    if ($enValue -match '\{version\}') { return ($value -replace '\{0(:[^}]*)?\}', '{version}') }
    return $value
}

# 3. Generate a .po per target language.
foreach ($lang in $targetLangs) {
    $reswPath = Join-Path $reswDir "$lang\Resources.resw"
    if (-not (Test-Path $reswPath)) { Write-Warning "missing resw: $reswPath"; continue }
    $resw = Read-Resw $reswPath

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('msgid ""')
    [void]$sb.AppendLine('msgstr ""')
    [void]$sb.AppendLine('"Project-Id-Version: easydict_app\n"')
    [void]$sb.AppendLine("`"Language: $lang\n`"")
    [void]$sb.AppendLine('"MIME-Version: 1.0\n"')
    [void]$sb.AppendLine('"Content-Type: text/plain; charset=UTF-8\n"')
    [void]$sb.AppendLine('"Content-Transfer-Encoding: 8bit\n"')

    $matched = 0
    foreach ($e in $enPo) {
        $value = $e.Value  # default: en-US fallback
        if ($keyToName.ContainsKey($e.Key)) {
            $name = $keyToName[$e.Key]
            if ($resw.ContainsKey($name) -and $resw[$name] -ne '') {
                $value = Fix-Placeholders $resw[$name] $e.Value
                $matched++
            }
        }
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine("msgid `"$($e.Key)`"")
        [void]$sb.AppendLine("msgstr `"$(Escape-Po $value)`"")
    }

    $outPath = Join-Path $localesDir "$lang.po"
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($outPath, $sb.ToString(), $utf8NoBom)
    Write-Host "Wrote $lang.po ($matched/$($enPo.Count) localized from resw)"
}

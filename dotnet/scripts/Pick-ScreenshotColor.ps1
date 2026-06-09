#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Sample pixel colors from a screenshot (e.g. a .NET reference) to drive Rust
    theme alignment.

.DESCRIPTION
    Two modes:
      * Interactive (default): opens the image in a window. Move the mouse to read
        the pixel color live; click to record a sample (also copied to clipboard).
        Recorded samples print to the console on exit.
      * Scriptable: pass -Points "x,y;x,y;..." (optionally -Window N for an NxN
        average) or -Region "x,y,w,h" to print colors without any UI.

.EXAMPLE
    pwsh -File Pick-ScreenshotColor.ps1 -Image ref.png
    pwsh -File Pick-ScreenshotColor.ps1 -Image ref.png -Points "300,765;430,670" -Window 5
    pwsh -File Pick-ScreenshotColor.ps1 -Image ref.png -Region "120,740,600,60"
#>
param(
    [Parameter(Mandatory)][string]$Image,
    [string]$Points,
    [int]$Window = 1,
    [string]$Region
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$path = (Resolve-Path -LiteralPath $Image).Path
$bmp = [System.Drawing.Bitmap]::FromFile($path)
Write-Host "Image: $path ($($bmp.Width)x$($bmp.Height))"

function Format-Color([int]$r, [int]$g, [int]$b) {
    "#{0:X2}{1:X2}{2:X2}  rgb({3},{4},{5})" -f $r, $g, $b, $r, $g, $b
}

function Get-AverageColor([System.Drawing.Bitmap]$b, [int]$x0, [int]$y0, [int]$w, [int]$h) {
    $rs = 0.0; $gs = 0.0; $bs = 0.0; $n = 0
    for ($y = $y0; $y -lt $y0 + $h; $y++) {
        for ($x = $x0; $x -lt $x0 + $w; $x++) {
            if ($x -ge 0 -and $y -ge 0 -and $x -lt $b.Width -and $y -lt $b.Height) {
                $p = $b.GetPixel($x, $y)
                $rs += $p.R; $gs += $p.G; $bs += $p.B; $n++
            }
        }
    }
    if ($n -eq 0) { return $null }
    [pscustomobject]@{ R = [int][Math]::Round($rs / $n); G = [int][Math]::Round($gs / $n); B = [int][Math]::Round($bs / $n) }
}

# --- Scriptable: region average ---
if ($Region) {
    $p = $Region -split ','
    $c = Get-AverageColor $bmp ([int]$p[0]) ([int]$p[1]) ([int]$p[2]) ([int]$p[3])
    Write-Host ("region {0} avg = {1}" -f $Region, (Format-Color $c.R $c.G $c.B))
    $bmp.Dispose(); return
}

# --- Scriptable: discrete points (NxN average centered on each) ---
if ($Points) {
    $half = [Math]::Floor($Window / 2)
    foreach ($pt in ($Points -split ';' | Where-Object { $_ -match '\S' })) {
        $xy = $pt -split ','
        $x = [int]$xy[0]; $y = [int]$xy[1]
        $c = Get-AverageColor $bmp ($x - $half) ($y - $half) $Window $Window
        Write-Host ("({0,4},{1,4})  {2}" -f $x, $y, (Format-Color $c.R $c.G $c.B))
    }
    $bmp.Dispose(); return
}

# --- Interactive picker ---
Add-Type -AssemblyName System.Windows.Forms
$samples = New-Object System.Collections.Generic.List[string]

$form = New-Object System.Windows.Forms.Form
$form.Text = "Color Picker - $([System.IO.Path]::GetFileName($path))"
$form.Width = [Math]::Min(1400, $bmp.Width + 120)
$form.Height = [Math]::Min(950, $bmp.Height + 160)

$panel = New-Object System.Windows.Forms.Panel
$panel.Dock = 'Fill'; $panel.AutoScroll = $true

$pic = New-Object System.Windows.Forms.PictureBox
$pic.Image = $bmp
$pic.SizeMode = 'AutoSize'   # 1:1 pixels -> click coords map directly to image
$panel.Controls.Add($pic)

$status = New-Object System.Windows.Forms.Label
$status.Dock = 'Bottom'; $status.Height = 52
$status.Font = New-Object System.Drawing.Font('Consolas', 13)
$status.TextAlign = 'MiddleLeft'
$status.Text = "  Move to read, click to record (copied to clipboard)."

$swatch = New-Object System.Windows.Forms.Panel
$swatch.Dock = 'Right'; $swatch.Width = 90
$swatch.BackColor = [System.Drawing.Color]::White

$form.Controls.Add($panel)
$form.Controls.Add($swatch)
$form.Controls.Add($status)

$pic.Add_MouseMove({
    param($s, $e)
    if ($e.X -ge 0 -and $e.Y -ge 0 -and $e.X -lt $bmp.Width -and $e.Y -lt $bmp.Height) {
        $p = $bmp.GetPixel($e.X, $e.Y)
        $swatch.BackColor = $p
        $status.Text = "  ({0},{1})  #{2:X2}{3:X2}{4:X2}  rgb({5},{6},{7})" -f $e.X, $e.Y, $p.R, $p.G, $p.B, $p.R, $p.G, $p.B
    }
}.GetNewClosure())

$pic.Add_MouseClick({
    param($s, $e)
    if ($e.X -ge 0 -and $e.Y -ge 0 -and $e.X -lt $bmp.Width -and $e.Y -lt $bmp.Height) {
        $p = $bmp.GetPixel($e.X, $e.Y)
        $hex = "#{0:X2}{1:X2}{2:X2}" -f $p.R, $p.G, $p.B
        $line = "({0},{1}) {2} rgb({3},{4},{5})" -f $e.X, $e.Y, $hex, $p.R, $p.G, $p.B
        $samples.Add($line)
        try { [System.Windows.Forms.Clipboard]::SetText($hex) } catch {}
        $form.Text = "Picked $($samples.Count): $hex"
    }
}.GetNewClosure())

[void]$form.ShowDialog()
$bmp.Dispose()

if ($samples.Count -gt 0) {
    Write-Host "`nRecorded samples:"
    $samples | ForEach-Object { Write-Host "  $_" }
}

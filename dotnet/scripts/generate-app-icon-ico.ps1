param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePng,

    [Parameter(Mandatory = $true)]
    [string]$OutputIco,

    [Parameter(Mandatory = $false)]
    [string]$OutputTrayPng,

    [Parameter(Mandatory = $false)]
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256),
    [switch]$DarkMode,
    [switch]$SmoothDarkOutline,
    [string]$OutputPngDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -Path "$PSScriptRoot/IconRasterizer.cs" -ReferencedAssemblies System.Drawing

function New-PngBytes {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Image]$Source,

        [Parameter(Mandatory = $true)]
        [int]$Size
    )

    $bitmap = [Easydict.IconTools.IconRasterizer]::Render($Source, $Size, $DarkMode.IsPresent, $SmoothDarkOutline.IsPresent)
    $ms = New-Object System.IO.MemoryStream
    try {
        $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$ms.ToArray()
    }
    finally { $ms.Dispose(); $bitmap.Dispose() }
}
function Write-Ico {
    param(
        [Parameter(Mandatory = $true)]
        [byte[][]]$PngImages,

        [Parameter(Mandatory = $true)]
        [int[]]$ImageSizes,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ($PngImages.Count -ne $ImageSizes.Count) {
        throw "PngImages and ImageSizes must have the same length."
    }

    $count = $PngImages.Count
    $headerSize = 6
    $dirEntrySize = 16
    $dirSize = $count * $dirEntrySize
    $dataOffset = $headerSize + $dirSize

    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        # ICONDIR
        $bw.Write([UInt16]0)      # reserved
        $bw.Write([UInt16]1)      # type: icon
        $bw.Write([UInt16]$count) # image count

        # Precompute offsets
        $offsets = New-Object System.Collections.Generic.List[int]
        $running = $dataOffset
        for ($i = 0; $i -lt $count; $i++) {
            $offsets.Add($running)
            $running += $PngImages[$i].Length
        }

        # ICONDIRENTRY (one per image)
        for ($i = 0; $i -lt $count; $i++) {
            $size = $ImageSizes[$i]
            $w = if ($size -ge 256) { 0 } else { $size }
            $h = if ($size -ge 256) { 0 } else { $size }

            $bw.Write([byte]$w)           # width (0 == 256)
            $bw.Write([byte]$h)           # height (0 == 256)
            $bw.Write([byte]0)            # color count
            $bw.Write([byte]0)            # reserved
            $bw.Write([UInt16]1)          # planes
            $bw.Write([UInt16]32)         # bit count
            $bw.Write([UInt32]$PngImages[$i].Length) # bytes in resource
            $bw.Write([UInt32]$offsets[$i])          # image offset
        }

        # Image data blocks
        for ($i = 0; $i -lt $count; $i++) {
            $bw.Write($PngImages[$i])
        }
    }
    finally {
        $bw.Dispose()
        $fs.Dispose()
    }
}

$sourceFull = (Resolve-Path -LiteralPath $SourcePng).Path
$outputFull = $OutputIco

Write-Host "Source PNG : $sourceFull"
Write-Host "Output ICO : $outputFull"
Write-Host "Sizes      : $($Sizes -join ', ')"

$sourceImage = [System.Drawing.Image]::FromFile($sourceFull)
try {
    $pngImages = @()
    foreach ($s in $Sizes) {
        $bytes = New-PngBytes -Source $sourceImage -Size $s
        $pngImages += ,$bytes
        if ($OutputPngDirectory) {
            New-Item -ItemType Directory -Force -Path $OutputPngDirectory | Out-Null
            [System.IO.File]::WriteAllBytes((Join-Path $OutputPngDirectory "Icon-$s.png"), $bytes)
        }
    }

    $outDir = Split-Path -Parent $outputFull
    if (-not [string]::IsNullOrWhiteSpace($outDir)) {
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    }

    Write-Ico -PngImages $pngImages -ImageSizes $Sizes -Path $outputFull

    # Generate TrayIcon.png if requested
    if ($PSBoundParameters.ContainsKey('OutputTrayPng') -and -not [string]::IsNullOrWhiteSpace($OutputTrayPng)) {
        Write-Host "Generating TrayIcon.png..."

        $trayDir = Split-Path -Parent $OutputTrayPng
        if ($trayDir) { New-Item -ItemType Directory -Force -Path $trayDir | Out-Null }
        [System.IO.File]::WriteAllBytes($OutputTrayPng, (New-PngBytes -Source $sourceImage -Size 32))
    }
}
finally {
    $sourceImage.Dispose()
}


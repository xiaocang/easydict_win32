param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePng,

    [Parameter(Mandatory = $true)]
    [string]$OutputIco,

    [Parameter(Mandatory = $false)]
    [string]$OutputTrayPng,

    [Parameter(Mandatory = $false)]
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-PngBytes {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Image]$Source,

        [Parameter(Mandatory = $true)]
        [int]$Size
    )

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $graphics.DrawImage($Source, 0, 0, $Size, $Size)

        $ms = New-Object System.IO.MemoryStream
        $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$ms.ToArray()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
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
        $pngImages += ,(New-PngBytes -Source $sourceImage -Size $s)
    }

    $outDir = Split-Path -Parent $outputFull
    if (-not [string]::IsNullOrWhiteSpace($outDir)) {
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    }

    Write-Ico -PngImages $pngImages -ImageSizes $Sizes -Path $outputFull

    # Generate TrayIcon.png if requested
    if ($PSBoundParameters.ContainsKey('OutputTrayPng') -and -not [string]::IsNullOrWhiteSpace($OutputTrayPng)) {
        Write-Host "Generating TrayIcon.png..."

        $traySize = 32
        $trayBitmap = New-Object System.Drawing.Bitmap $traySize, $traySize, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $trayGraphics = [System.Drawing.Graphics]::FromImage($trayBitmap)
        try {
            $trayGraphics.Clear([System.Drawing.Color]::Transparent)
            $trayGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $trayGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $trayGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $trayGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

            $trayGraphics.DrawImage($sourceImage, 0, 0, $traySize, $traySize)

            $trayDir = Split-Path -Parent $OutputTrayPng
            if (-not [string]::IsNullOrWhiteSpace($trayDir)) {
                New-Item -ItemType Directory -Force -Path $trayDir | Out-Null
            }

            $trayBitmap.Save($OutputTrayPng, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host "TrayIcon.png saved to: $OutputTrayPng"
        }
        finally {
            $trayGraphics.Dispose()
            $trayBitmap.Dispose()
        }
    }
}
finally {
    $sourceImage.Dispose()
}


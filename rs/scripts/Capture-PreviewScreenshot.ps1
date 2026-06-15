param(
    [string]$WindowTitle = "Easydict Rust Main Window Preview",
    [string]$ProcessName = "easydict_preview_iced",
    [string]$OutputDir,
    [switch]$StartIfMissing,
    [switch]$StartNewInstance,
    [string]$Executable,
    [int]$SettlingMilliseconds = 900,
    [int]$ContentCheckRetries = 8,
    [int]$ContentCheckDelayMilliseconds = 350,
    [double]$CursorDipX = -1,
    [double]$CursorDipY = -1,
    [switch]$AllowLikelyBlankCapture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$rsRoot = Resolve-Path (Join-Path $scriptRoot "..")
$repoRoot = Resolve-Path (Join-Path $rsRoot "..")

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\winfluent-preview"
}

if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $rsRoot "target\debug\easydict_preview_iced.exe"
}

$settingsPreviewOpen = $false
if (-not [string]::IsNullOrWhiteSpace($env:EASYDICT_PREVIEW_SETTINGS_OPEN)) {
    $settingsPreviewOpen = @("1", "true", "yes", "on") -contains $env:EASYDICT_PREVIEW_SETTINGS_OPEN.Trim().ToLowerInvariant()
}

$previewWindow = if ([string]::IsNullOrWhiteSpace($env:EASYDICT_PREVIEW_WINDOW)) {
    if ($settingsPreviewOpen) { "settings" } else { "main" }
} else {
    $env:EASYDICT_PREVIEW_WINDOW.Trim().ToLowerInvariant()
}

$defaultWindowTitle = "Easydict Rust Main Window Preview"
if ([string]::IsNullOrWhiteSpace($WindowTitle) -or $WindowTitle -eq $defaultWindowTitle) {
    $WindowTitle = switch ($previewWindow) {
        { $_ -in @("settings") } { "Easydict Settings"; break }
        { $_ -in @("mini") } { "Easydict Mini"; break }
        { $_ -in @("fixed") } { "Easydict Fixed"; break }
        { $_ -in @("capture", "capture-overlay", "ocr", "ocr-overlay") } { "Easydict Capture"; break }
        { $_ -in @("popbutton", "pop-button") } { "Easydict Selection"; break }
        default { $defaultWindowTitle; break }
    }
}

$script:previewMinWidth = 200
$script:previewMinHeight = 200
switch ($previewWindow) {
    { $_ -in @("popbutton", "pop-button") } {
        $script:previewMinWidth = 30
        $script:previewMinHeight = 30
        break
    }
    { $_ -in @("capture", "capture-overlay", "ocr", "ocr-overlay") } {
        $script:previewMinWidth = 120
        $script:previewMinHeight = 80
        break
    }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$script:StartedPreviewProcess = $null
$script:CapturedPreviewWindow = $null

trap {
    if ($null -ne $script:StartedPreviewProcess -and -not $script:StartedPreviewProcess.HasExited) {
        Stop-Process -Id $script:StartedPreviewProcess.Id -ErrorAction SilentlyContinue
    }
    throw $_
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Win32DpiCapture
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        out RECT rect,
        int attributeSize);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);
}
"@

function Enable-PerMonitorDpiAwareness {
    try {
        $null = [Win32DpiCapture]::SetProcessDpiAwarenessContext([IntPtr](-4))
    } catch {
        $null = [Win32DpiCapture]::SetProcessDPIAware()
    }
}

function Get-TopLevelWindowsForProcess($processIds) {
    $idSet = @{}
    foreach ($processId in $processIds) {
        $idSet[[uint32]$processId] = $true
    }

    $windows = New-Object System.Collections.Generic.List[object]
    $callback = [Win32DpiCapture+EnumWindowsProc]{
        param([IntPtr]$hwnd, [IntPtr]$lParam)

        [uint32]$ownerProcessId = 0
        $null = [Win32DpiCapture]::GetWindowThreadProcessId($hwnd, [ref]$ownerProcessId)
        if (-not $idSet.ContainsKey($ownerProcessId)) {
            return $true
        }

        $title = New-Object System.Text.StringBuilder 512
        $null = [Win32DpiCapture]::GetWindowText($hwnd, $title, $title.Capacity)
        $rect = Get-WindowRectPhysical $hwnd
        $width = $rect.Right - $rect.Left
        $height = $rect.Bottom - $rect.Top

        $windows.Add([pscustomobject]@{
            Handle = $hwnd
            ProcessId = $ownerProcessId
            Visible = [Win32DpiCapture]::IsWindowVisible($hwnd)
            Title = $title.ToString()
            Width = $width
            Height = $height
            Area = $width * $height
        }) | Out-Null

        return $true
    }

    $null = [Win32DpiCapture]::EnumWindows($callback, [IntPtr]::Zero)
    return @($windows.ToArray())
}

function Get-PreviewWindowHandle {
    if ($StartNewInstance) {
        $processes = @()
    } else {
        $processes = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    }

    if ($processes.Count -gt 0) {
        $window = Get-TopLevelWindowsForProcess ($processes | ForEach-Object { $_.Id }) |
            Where-Object { $_.Visible -and $_.Width -ge $script:previewMinWidth -and $_.Height -ge $script:previewMinHeight -and $_.Title -like "*$WindowTitle*" } |
            Sort-Object Area -Descending |
            Select-Object -First 1

        if ($null -ne $window) {
            $script:CapturedPreviewWindow = $window
            return $window.Handle
        }

        $window = Get-TopLevelWindowsForProcess ($processes | ForEach-Object { $_.Id }) |
            Where-Object { $_.Visible -and $_.Width -ge $script:previewMinWidth -and $_.Height -ge $script:previewMinHeight } |
            Sort-Object Area -Descending |
            Select-Object -First 1

        if ($null -ne $window) {
            $script:CapturedPreviewWindow = $window
            return $window.Handle
        }
    }

    if ($StartIfMissing -or $StartNewInstance) {
        if (-not (Test-Path -LiteralPath $Executable)) {
            throw "Preview executable not found: $Executable"
        }

        $started = Start-Process -FilePath $Executable -WorkingDirectory $rsRoot -PassThru
        $script:StartedPreviewProcess = $started
        for ($i = 0; $i -lt 80; $i++) {
            Start-Sleep -Milliseconds 100
            $started.Refresh()
            $window = Get-TopLevelWindowsForProcess @($started.Id) |
                Where-Object { $_.Visible -and $_.Width -ge $script:previewMinWidth -and $_.Height -ge $script:previewMinHeight } |
                Sort-Object @{ Expression = { $_.Title -like "*$WindowTitle*" }; Descending = $true }, Area -Descending |
                Select-Object -First 1

            if ($null -ne $window) {
                $script:CapturedPreviewWindow = $window
                return $window.Handle
            }
        }
    }

    throw "Could not find a visible preview window for process '$ProcessName'."
}

function Get-WindowRectPhysical($hwnd) {
    $getWindowRect = New-Object Win32DpiCapture+RECT
    if (-not [Win32DpiCapture]::GetWindowRect($hwnd, [ref]$getWindowRect)) {
        throw "GetWindowRect failed."
    }

    $rect = New-Object Win32DpiCapture+RECT
    $dwmResult = [Win32DpiCapture]::DwmGetWindowAttribute(
        $hwnd,
        9,
        [ref]$rect,
        [Runtime.InteropServices.Marshal]::SizeOf([type][Win32DpiCapture+RECT]))

    $dwmWidth = $rect.Right - $rect.Left
    $dwmHeight = $rect.Bottom - $rect.Top
    $getWidth = $getWindowRect.Right - $getWindowRect.Left
    $getHeight = $getWindowRect.Bottom - $getWindowRect.Top

    if ($dwmResult -ge 0 -and $dwmWidth -gt 0 -and $dwmHeight -gt 0 -and ($dwmWidth * $dwmHeight) -ge ($getWidth * $getHeight)) {
        return $rect
    }

    return $getWindowRect
}

function Get-WindowDpiScale($hwnd) {
    $monitor = [Win32DpiCapture]::MonitorFromWindow($hwnd, 2)
    $dpiX = [uint32]96
    $dpiY = [uint32]96
    $dpiResult = [Win32DpiCapture]::GetDpiForMonitor($monitor, 0, [ref]$dpiX, [ref]$dpiY)
    if ($dpiResult -lt 0 -or $dpiX -eq 0) {
        $dpiX = [Win32DpiCapture]::GetDpiForWindow($hwnd)
    }
    if ($dpiX -eq 0) {
        return 1.0
    }

    return [double]$dpiX / 96.0
}

function Format-HwndHex([IntPtr]$hwnd) {
    if ($hwnd -eq [IntPtr]::Zero) {
        return "0x0"
    }

    "0x{0:X}" -f $hwnd.ToInt64()
}

function Measure-WindowOcclusion($hwnd, $rect) {
    $gaRoot = 2
    $foreground = [Win32DpiCapture]::GetForegroundWindow()
    [uint32]$targetProcessId = 0
    $null = [Win32DpiCapture]::GetWindowThreadProcessId($hwnd, [ref]$targetProcessId)

    $width = [Math]::Max(1, $rect.Right - $rect.Left)
    $height = [Math]::Max(1, $rect.Bottom - $rect.Top)
    $probePoints = @(
        @{ Name = "title-left"; X = $rect.Left + [Math]::Min([Math]::Max(24, [int]($width * 0.08)), $width - 24); Y = $rect.Top + [Math]::Min(36, [Math]::Max(12, [int]($height * 0.08))) },
        @{ Name = "title-right"; X = $rect.Right - [Math]::Min([Math]::Max(80, [int]($width * 0.08)), $width - 24); Y = $rect.Top + [Math]::Min(36, [Math]::Max(12, [int]($height * 0.08))) },
        @{ Name = "center"; X = $rect.Left + [int]($width / 2); Y = $rect.Top + [int]($height / 2) },
        @{ Name = "lower-center"; X = $rect.Left + [int]($width / 2); Y = $rect.Top + [int]($height * 0.82) }
    )

    $probes = New-Object System.Collections.Generic.List[object]
    $matchingProbeCount = 0
    foreach ($probe in $probePoints) {
        $point = New-Object Win32DpiCapture+POINT
        $point.X = [int]$probe["X"]
        $point.Y = [int]$probe["Y"]
        $topWindow = [Win32DpiCapture]::WindowFromPoint($point)
        $rootWindow = if ($topWindow -ne [IntPtr]::Zero) {
            [Win32DpiCapture]::GetAncestor($topWindow, $gaRoot)
        } else {
            [IntPtr]::Zero
        }
        [uint32]$rootProcessId = 0
        if ($rootWindow -ne [IntPtr]::Zero) {
            $null = [Win32DpiCapture]::GetWindowThreadProcessId($rootWindow, [ref]$rootProcessId)
        }
        $matchesTarget = $rootWindow -eq $hwnd -or ($rootProcessId -ne 0 -and $rootProcessId -eq $targetProcessId)
        if ($matchesTarget) {
            $matchingProbeCount++
        }

        $probes.Add([ordered]@{
            name = [string]$probe["Name"]
            x = [int]$probe["X"]
            y = [int]$probe["Y"]
            topWindow = Format-HwndHex $topWindow
            rootWindow = Format-HwndHex $rootWindow
            rootProcessId = if ($rootProcessId -ne 0) { [int]$rootProcessId } else { $null }
            matchesTarget = [bool]$matchesTarget
        }) | Out-Null
    }

    [ordered]@{
        targetWindow = Format-HwndHex $hwnd
        targetProcessId = if ($targetProcessId -ne 0) { [int]$targetProcessId } else { $null }
        foregroundWindow = Format-HwndHex $foreground
        isForeground = [bool]($foreground -eq $hwnd)
        matchingProbeCount = [int]$matchingProbeCount
        probeCount = [int]$probePoints.Count
        isLikelyOccluded = [bool]($matchingProbeCount -lt $probePoints.Count)
        probes = $probes.ToArray()
    }
}

function Save-BitmapPngWithRetry($bitmap, [string]$path) {
    $directory = Split-Path -Parent $path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $attempts = 4
    $lastError = $null
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        $tempPath = if ([string]::IsNullOrWhiteSpace($directory)) {
            "_c$PID$attempt.png"
        } else {
            Join-Path $directory "_c$PID$attempt.png"
        }
        try {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
            $bitmap.Save($tempPath, [System.Drawing.Imaging.ImageFormat]::Png)
            Move-Item -LiteralPath $tempPath -Destination $path -Force
            return
        } catch {
            $lastError = $_
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
            if ($attempt -lt $attempts) {
                Start-Sleep -Milliseconds (120 * $attempt)
            }
        }
    }

    $message = if ($null -ne $lastError) { [string]$lastError.Exception.Message } else { "unknown error" }
    throw "Failed to save PNG '$path' after $attempts attempt(s): $message"
}

function Save-ScreenRegionPng($path, [int]$x, [int]$y, [int]$width, [int]$height) {
    if ($width -le 0 -or $height -le 0) {
        throw "Invalid capture size ${width}x${height}."
    }

    $bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $width, $height), [System.Drawing.CopyPixelOperation]::SourceCopy)
        Save-BitmapPngWithRetry -bitmap $bitmap -path $path
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Open-BitmapWithRetry([string]$path) {
    $attempts = 8
    $lastError = $null
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        $stream = $null
        $image = $null
        try {
            $bytes = [System.IO.File]::ReadAllBytes($path)
            $stream = [System.IO.MemoryStream]::new($bytes)
            $image = [System.Drawing.Bitmap]::FromStream($stream)
            return [System.Drawing.Bitmap]::new($image)
        } catch {
            $lastError = $_
            if ($attempt -lt $attempts) {
                Start-Sleep -Milliseconds (160 * $attempt)
            }
        } finally {
            if ($null -ne $image) {
                $image.Dispose()
            }
            if ($null -ne $stream) {
                $stream.Dispose()
            }
        }
    }

    $message = if ($null -ne $lastError) { [string]$lastError.Exception.Message } else { "unknown error" }
    throw "Failed to open PNG '$path' after $attempts attempt(s): $message"
}

function Measure-ImageContent($path) {
    $bitmap = Open-BitmapWithRetry $path
    try {
        $sampleLeft = if ($bitmap.Width -gt 160) { 12 } else { 0 }
        $sampleTop = if ($bitmap.Height -gt 220) { 72 } else { 0 }
        $sampleRight = if ($bitmap.Width -gt 160) { $bitmap.Width - 12 } else { $bitmap.Width }
        $sampleBottom = if ($bitmap.Height -gt 220) { $bitmap.Height - 12 } else { $bitmap.Height }
        $sampleWidth = [Math]::Max(1, $sampleRight - $sampleLeft)
        $sampleHeight = [Math]::Max(1, $sampleBottom - $sampleTop)
        $sampleColumns = [Math]::Min(96, [Math]::Max(1, $sampleWidth))
        $sampleRows = [Math]::Min(72, [Math]::Max(1, $sampleHeight))
        $stepX = [Math]::Max(1, [int][Math]::Floor($sampleWidth / $sampleColumns))
        $stepY = [Math]::Max(1, [int][Math]::Floor($sampleHeight / $sampleRows))

        [long]$count = 0
        [double]$lumaSum = 0
        [double]$lumaSquaredSum = 0
        [int]$nearWhite = 0
        [int]$nearBlack = 0
        [int]$minR = 255
        [int]$minG = 255
        [int]$minB = 255
        [int]$maxR = 0
        [int]$maxG = 0
        [int]$maxB = 0

        for ($y = $sampleTop; $y -lt $sampleBottom; $y += $stepY) {
            for ($x = $sampleLeft; $x -lt $sampleRight; $x += $stepX) {
                $pixel = $bitmap.GetPixel($x, $y)
                $r = [int]$pixel.R
                $g = [int]$pixel.G
                $b = [int]$pixel.B
                $luma = (0.2126 * $r) + (0.7152 * $g) + (0.0722 * $b)

                $count++
                $lumaSum += $luma
                $lumaSquaredSum += ($luma * $luma)
                if ($r -ge 245 -and $g -ge 245 -and $b -ge 245) {
                    $nearWhite++
                }
                if ($r -le 10 -and $g -le 10 -and $b -le 10) {
                    $nearBlack++
                }
                $minR = [Math]::Min($minR, $r)
                $minG = [Math]::Min($minG, $g)
                $minB = [Math]::Min($minB, $b)
                $maxR = [Math]::Max($maxR, $r)
                $maxG = [Math]::Max($maxG, $g)
                $maxB = [Math]::Max($maxB, $b)
            }
        }

        $mean = if ($count -eq 0) { 0.0 } else { $lumaSum / $count }
        $variance = if ($count -eq 0) {
            0.0
        } else {
            [Math]::Max(0.0, ($lumaSquaredSum / $count) - ($mean * $mean))
        }
        $stddev = [Math]::Sqrt($variance)
        $colorSpread = [Math]::Max(
            [Math]::Max($maxR - $minR, $maxG - $minG),
            $maxB - $minB)
        $nearWhiteRatio = if ($count -eq 0) { 0.0 } else { [double]$nearWhite / [double]$count }
        $nearBlackRatio = if ($count -eq 0) { 0.0 } else { [double]$nearBlack / [double]$count }
        $likelyBlank = (
            ($nearWhiteRatio -ge 0.985 -and $stddev -le 4.0 -and $colorSpread -le 12) -or
            ($nearBlackRatio -ge 0.985 -and $stddev -le 4.0 -and $colorSpread -le 12)
        )

        [pscustomobject]@{
            isLikelyBlank = [bool]$likelyBlank
            sampleBounds = [pscustomobject]@{
                left = $sampleLeft
                top = $sampleTop
                width = $sampleWidth
                height = $sampleHeight
            }
            sampleCount = [int]$count
            nearWhiteRatio = [Math]::Round($nearWhiteRatio, 5)
            nearBlackRatio = [Math]::Round($nearBlackRatio, 5)
            lumaMean = [Math]::Round($mean, 3)
            lumaStdDev = [Math]::Round($stddev, 3)
            colorSpread = [int]$colorSpread
            minRgb = @($minR, $minG, $minB)
            maxRgb = @($maxR, $maxG, $maxB)
        }
    } finally {
        $bitmap.Dispose()
    }
}

Enable-PerMonitorDpiAwareness

$hwnd = Get-PreviewWindowHandle

$swRestore = 9
$swpNoMove = 0x0002
$swpNoSize = 0x0001
$swpShowWindow = 0x0040
$hwndTopMost = [IntPtr](-1)
$hwndNotTopMost = [IntPtr](-2)
$restorePreviewZOrder = $false

$null = [Win32DpiCapture]::ShowWindow($hwnd, $swRestore)
$null = [Win32DpiCapture]::SetWindowPos($hwnd, $hwndTopMost, 0, 0, 0, 0, $swpNoMove -bor $swpNoSize -bor $swpShowWindow)
$restorePreviewZOrder = $true
$null = [Win32DpiCapture]::SetForegroundWindow($hwnd)
$cursorPhysical = $null
if ($CursorDipX -ge 0 -and $CursorDipY -ge 0) {
    $cursorRect = Get-WindowRectPhysical $hwnd
    $cursorScale = Get-WindowDpiScale $hwnd
    $cursorPhysical = [ordered]@{
        x = [int][Math]::Round($cursorRect.Left + ($CursorDipX * $cursorScale))
        y = [int][Math]::Round($cursorRect.Top + ($CursorDipY * $cursorScale))
        source = "requested"
    }
    $null = [Win32DpiCapture]::SetCursorPos($cursorPhysical["x"], $cursorPhysical["y"])
} else {
    $cursorRect = Get-WindowRectPhysical $hwnd
    $cursorScale = Get-WindowDpiScale $hwnd
    $cursorPhysical = [ordered]@{
        x = [int][Math]::Round($cursorRect.Left + (18.0 * $cursorScale))
        y = [int][Math]::Round($cursorRect.Top + (18.0 * $cursorScale))
        source = "default-titlebar-safe-point"
    }
    $null = [Win32DpiCapture]::SetCursorPos($cursorPhysical["x"], $cursorPhysical["y"])
}
Start-Sleep -Milliseconds ([Math]::Max(0, $SettlingMilliseconds))

$captureAttempt = 0
$contentCheck = $null
$occlusionCheck = $null
$windowPath = $null
$desktopPath = $null
$metadataPath = $null

try {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $base = "main-window-preview-dpi-$timestamp"
    $windowPath = Join-Path $OutputDir "$base.window.png"
    $desktopPath = Join-Path $OutputDir "$base.desktop.png"
    $metadataPath = Join-Path $OutputDir "$base.metadata.json"

    $maxAttempts = [Math]::Max(1, $ContentCheckRetries + 1)
    for ($captureAttempt = 1; $captureAttempt -le $maxAttempts; $captureAttempt++) {
        $rect = Get-WindowRectPhysical $hwnd
        $windowWidth = $rect.Right - $rect.Left
        $windowHeight = $rect.Bottom - $rect.Top

        $monitor = [Win32DpiCapture]::MonitorFromWindow($hwnd, 2)
        $monitorInfo = New-Object Win32DpiCapture+MONITORINFO
        $monitorInfo.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][Win32DpiCapture+MONITORINFO])
        $null = [Win32DpiCapture]::GetMonitorInfo($monitor, [ref]$monitorInfo)

        $dpiX = [uint32]96
        $dpiY = [uint32]96
        $dpiResult = [Win32DpiCapture]::GetDpiForMonitor($monitor, 0, [ref]$dpiX, [ref]$dpiY)
        if ($dpiResult -lt 0 -or $dpiX -eq 0) {
            $dpiX = [Win32DpiCapture]::GetDpiForWindow($hwnd)
            $dpiY = $dpiX
        }

        $scale = [double]$dpiX / 96.0
        $virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen

        $null = [Win32DpiCapture]::ShowWindow($hwnd, $swRestore)
        $null = [Win32DpiCapture]::SetWindowPos($hwnd, $hwndTopMost, 0, 0, 0, 0, $swpNoMove -bor $swpNoSize -bor $swpShowWindow)
        $null = [Win32DpiCapture]::SetForegroundWindow($hwnd)
        Start-Sleep -Milliseconds 120
        $occlusionCheck = Measure-WindowOcclusion -hwnd $hwnd -rect $rect
        if ([bool]$occlusionCheck.isLikelyOccluded -and $captureAttempt -lt $maxAttempts) {
            Start-Sleep -Milliseconds ([Math]::Max(0, $ContentCheckDelayMilliseconds))
            continue
        }

        Save-ScreenRegionPng $windowPath $rect.Left $rect.Top $windowWidth $windowHeight
        $contentCheck = Measure-ImageContent $windowPath
        if ((-not [bool]$occlusionCheck.isLikelyOccluded) -and (-not $contentCheck.isLikelyBlank -or $AllowLikelyBlankCapture)) {
            break
        }
        if ($captureAttempt -lt $maxAttempts) {
            Start-Sleep -Milliseconds ([Math]::Max(0, $ContentCheckDelayMilliseconds))
        }
    }

    if ($null -ne $occlusionCheck -and [bool]$occlusionCheck.isLikelyOccluded) {
        throw "Captured preview window appears occluded after $captureAttempt attempt(s): $windowPath"
    }

    if ($null -ne $contentCheck -and $contentCheck.isLikelyBlank -and -not $AllowLikelyBlankCapture) {
        throw "Captured preview window is likely blank after $captureAttempt attempt(s): $windowPath"
    }

    Save-ScreenRegionPng $desktopPath $virtual.Left $virtual.Top $virtual.Width $virtual.Height
} finally {
    if ($restorePreviewZOrder) {
        $null = [Win32DpiCapture]::SetWindowPos($hwnd, $hwndNotTopMost, 0, 0, 0, 0, $swpNoMove -bor $swpNoSize -bor $swpShowWindow)
    }
}

$metadata = [ordered]@{
    capturedAt = (Get-Date).ToUniversalTime().ToString("o")
    dpi = [ordered]@{
        x = [int]$dpiX
        y = [int]$dpiY
        scale = [Math]::Round($scale, 4)
    }
    windowPhysicalPixels = [ordered]@{
        left = $rect.Left
        top = $rect.Top
        right = $rect.Right
        bottom = $rect.Bottom
        width = $windowWidth
        height = $windowHeight
    }
    windowDips = [ordered]@{
        left = [Math]::Round($rect.Left / $scale, 2)
        top = [Math]::Round($rect.Top / $scale, 2)
        right = [Math]::Round($rect.Right / $scale, 2)
        bottom = [Math]::Round($rect.Bottom / $scale, 2)
        width = [Math]::Round($windowWidth / $scale, 2)
        height = [Math]::Round($windowHeight / $scale, 2)
    }
    monitorPhysicalPixels = [ordered]@{
        workLeft = $monitorInfo.rcWork.Left
        workTop = $monitorInfo.rcWork.Top
        workRight = $monitorInfo.rcWork.Right
        workBottom = $monitorInfo.rcWork.Bottom
        monitorLeft = $monitorInfo.rcMonitor.Left
        monitorTop = $monitorInfo.rcMonitor.Top
        monitorRight = $monitorInfo.rcMonitor.Right
        monitorBottom = $monitorInfo.rcMonitor.Bottom
    }
    virtualDesktopPhysicalPixels = [ordered]@{
        left = $virtual.Left
        top = $virtual.Top
        width = $virtual.Width
        height = $virtual.Height
    }
    previewProcess = [ordered]@{
        processId = if ($null -ne $script:CapturedPreviewWindow) { [int]$script:CapturedPreviewWindow.ProcessId } else { $null }
        title = if ($null -ne $script:CapturedPreviewWindow) { $script:CapturedPreviewWindow.Title } else { $null }
        startedNewInstance = [bool]$StartNewInstance
    }
    contentCheck = [ordered]@{
        enabled = -not [bool]$AllowLikelyBlankCapture
        attempts = [int]$captureAttempt
        retryLimit = [int]$ContentCheckRetries
        delayMilliseconds = [int]$ContentCheckDelayMilliseconds
        result = $contentCheck
    }
    occlusionCheck = $occlusionCheck
    cursor = [ordered]@{
        requestedDipX = if ($CursorDipX -ge 0) { [double]$CursorDipX } else { $null }
        requestedDipY = if ($CursorDipY -ge 0) { [double]$CursorDipY } else { $null }
        physical = $cursorPhysical
    }
    output = [ordered]@{
        window = $windowPath
        desktop = $desktopPath
        metadata = $metadataPath
    }
}

$metadata | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metadataPath -Encoding UTF8

Write-Host "Window screenshot: $windowPath"
Write-Host "Desktop screenshot: $desktopPath"
Write-Host "Metadata: $metadataPath"
Write-Host ("DPI: {0} ({1:P0}), window: {2}x{3} physical, {4}x{5} DIP" -f $dpiX, $scale, $windowWidth, $windowHeight, [Math]::Round($windowWidth / $scale, 2), [Math]::Round($windowHeight / $scale, 2))

if ($null -ne $script:StartedPreviewProcess -and -not $script:StartedPreviewProcess.HasExited) {
    Stop-Process -Id $script:StartedPreviewProcess.Id -ErrorAction SilentlyContinue
}

param(
    [string]$WindowTitle = "Easydict Rust Main Window Preview",
    [string]$ProcessName = "easydict_preview_iced",
    [string]$OutputDir,
    [switch]$StartIfMissing,
    [switch]$StartNewInstance,
    [string]$Executable
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

$previewWindow = if ([string]::IsNullOrWhiteSpace($env:EASYDICT_PREVIEW_WINDOW)) {
    "main"
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
    throw
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

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

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

function Save-ScreenRegionPng($path, [int]$x, [int]$y, [int]$width, [int]$height) {
    if ($width -le 0 -or $height -le 0) {
        throw "Invalid capture size ${width}x${height}."
    }

    $bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $width, $height), [System.Drawing.CopyPixelOperation]::SourceCopy)
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
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
Start-Sleep -Milliseconds 350

try {
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
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $base = "main-window-preview-dpi-$timestamp"
    $windowPath = Join-Path $OutputDir "$base.window.png"
    $desktopPath = Join-Path $OutputDir "$base.desktop.png"
    $metadataPath = Join-Path $OutputDir "$base.metadata.json"

    Save-ScreenRegionPng $windowPath $rect.Left $rect.Top $windowWidth $windowHeight
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

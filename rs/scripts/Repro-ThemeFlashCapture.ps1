# Repro-ThemeFlashCapture.ps1
#
# Continuously samples the real compositor surface while driving the settings
# navigation route, live theme selector, or main-window mode selector.
# Navigation clicks settings -> Services -> Back -> settings -> General.
# ThemeSwitch selects Light -> Dark -> System. ModeSwitch opens the inline
# selector and switches Translate -> Long Document -> Translate.
# Boot frames keep the first 10 visible samples; transition frames cover
# 800ms after every live theme or mode selection.
#
# Usage (from rs/):
#   powershell -ExecutionPolicy Bypass -File scripts\Repro-ThemeFlashCapture.ps1 `
#     -Executable target\debug\easydict_preview_iced.exe `
#     -OutputDir $env:TEMP\easydict-flash-frames `
#     -SettingsDir <dir containing settings.json with "AppTheme": "Dark">
#
param(
    [string]$Executable,
    [string]$OutputDir,
    [string]$SettingsDir,
    [int]$DurationMs = 9000,
    [ValidateSet("Navigation", "ThemeSwitch", "ModeSwitch")]
    [string]$Scenario = "Navigation"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FlashProbe {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, uint size);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    public static void Wheel(int delta) {
        mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
    }
    public static bool IsCompositorVisible(IntPtr hwnd) {
        if (!IsWindowVisible(hwnd)) { return false; }
        int cloaked;
        int result = DwmGetWindowAttribute(hwnd, 14, out cloaked, sizeof(int));
        return result != 0 || cloaked == 0;
    }
    public static IntPtr FindMainWindow(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hwnd, IntPtr lparam) {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != pid || !IsWindowVisible(hwnd)) { return true; }
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) { return true; }
            if (rect.Right - rect.Left >= 300 && rect.Bottom - rect.Top >= 300) { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

[FlashProbe]::SetProcessDPIAware() | Out-Null

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Executable
$psi.UseShellExecute = $false
$psi.EnvironmentVariables["EASYDICT_SETTINGS_DIR"] = $SettingsDir
$process = [System.Diagnostics.Process]::Start($psi)

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$rows = New-Object System.Collections.Generic.List[string]
$rows.Add("frame,ms,visible,x,y,w,h,meanR,meanG,meanB,saved")
$frame = 0
$topmostApplied = $false
$visibleSince = -1L
# Window-relative action plans (coordinates measured on the 419x495 layout).
if ($Scenario -eq "ThemeSwitch") {
    $actionPlan = @(
        @{ At = 1500; Kind = "click"; X = 388; Y = 58 },
        @{ At = 2500; Kind = "wheel"; Delta = -120 },
        @{ At = 3200; Kind = "click"; X = 164; Y = 438 },
        @{ At = 3800; Kind = "click"; X = 65; Y = 404; Transition = "dark-to-light" },
        @{ At = 5000; Kind = "click"; X = 164; Y = 438 },
        @{ At = 5600; Kind = "click"; X = 65; Y = 438; Transition = "light-to-dark" },
        @{ At = 6800; Kind = "click"; X = 164; Y = 438 },
        @{ At = 7400; Kind = "click"; X = 65; Y = 370; Transition = "dark-to-system" }
    )
    $shotPlan = @(1000, 2200, 3400, 4200, 5200, 6000, 7000, 7800, 8600)
} elseif ($Scenario -eq "ModeSwitch") {
    $actionPlan = @(
        @{ At = 1500; Kind = "click"; X = 110; Y = 58 },
        @{ At = 2300; Kind = "click"; X = 110; Y = 107; Transition = "translate-to-long-document" },
        @{ At = 4500; Kind = "click"; X = 110; Y = 58 },
        @{ At = 5300; Kind = "click"; X = 110; Y = 85; Transition = "long-document-to-translate" }
    )
    $shotPlan = @(1000, 1900, 3000, 4900, 6000, 7000)
} else {
    $actionPlan = @(
        @{ At = 1500; Kind = "click"; X = 388; Y = 58 },
        @{ At = 3000; Kind = "click"; X = 163; Y = 155 },
        @{ At = 4500; Kind = "click"; X = 41; Y = 74 },
        @{ At = 6000; Kind = "click"; X = 388; Y = 58 },
        @{ At = 7500; Kind = "click"; X = 67; Y = 155 }
    )
    $shotPlan = @(1000, 2200, 3700, 5200, 6700, 8200)
}
$actionIndex = 0
$captureTransitionUntil = -1L
$transitionIndex = 0
$shotIndex = 0
$bootShots = 0
$bitmap = $null
$graphics = $null
$bitmapW = 0
$bitmapH = 0

while ($clock.ElapsedMilliseconds -lt $DurationMs) {
    $handle = [FlashProbe]::FindMainWindow([uint32]$process.Id)

    $visible = $false
    $rect = New-Object FlashProbe+RECT
    if ($handle -ne [IntPtr]::Zero) {
        $visible = [FlashProbe]::IsCompositorVisible($handle)
        if ($visible) {
            [FlashProbe]::GetWindowRect($handle, [ref]$rect) | Out-Null
            if (-not $topmostApplied) {
                # NOSIZE|NOMOVE|NOACTIVATE only: never force-show the window;
                # topmost is applied only after the app made itself visible.
                [FlashProbe]::SetWindowPos($handle, [IntPtr](-1), 0, 0, 0, 0, 0x13) | Out-Null
                $topmostApplied = $true
                $visibleSince = $clock.ElapsedMilliseconds
            }
            if ($visibleSince -ge 0 -and $actionIndex -lt $actionPlan.Count) {
                $step = $actionPlan[$actionIndex]
                if (($clock.ElapsedMilliseconds - $visibleSince) -ge $step.At) {
                    $winW = $rect.Right - $rect.Left
                    $winH = $rect.Bottom - $rect.Top
                    if ($step.Kind -eq "wheel") {
                        $wheelX = $rect.Left + [int](210 * $winW / 419.0)
                        $wheelY = $rect.Top + [int](300 * $winH / 495.0)
                        [FlashProbe]::SetCursorPos($wheelX, $wheelY) | Out-Null
                        [FlashProbe]::Wheel([int]$step.Delta)
                    } else {
                        $clickX = $rect.Left + [int]($step.X * $winW / 419.0)
                        $clickY = $rect.Top + [int]($step.Y * $winH / 495.0)
                        [FlashProbe]::Click($clickX, $clickY)
                    }
                    if ($step.ContainsKey("Transition")) {
                        $transitionIndex++
                        $captureTransitionUntil = $clock.ElapsedMilliseconds + 800
                    }
                    $actionIndex++
                }
            }
        }
    }

    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($visible -and $w -gt 50 -and $h -gt 50) {
        if ($null -eq $bitmap -or $bitmapW -ne $w -or $bitmapH -ne $h) {
            if ($null -ne $graphics) { $graphics.Dispose() }
            if ($null -ne $bitmap) { $bitmap.Dispose() }
            $bitmap = New-Object System.Drawing.Bitmap($w, $h)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $bitmapW = $w; $bitmapH = $h
        }
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) | Out-Null

        $sumR = 0L; $sumG = 0L; $sumB = 0L; $count = 0L
        $data = $bitmap.LockBits((New-Object System.Drawing.Rectangle(0, 0, $w, $h)), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $stride = $data.Stride
            $buffer = New-Object byte[] ($stride)
            for ($y = 4; $y -lt $h; $y += 12) {
                [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + [int64]$y * $stride), $buffer, 0, $stride)
                for ($x = 8; $x -lt ($w - 8); $x += 16) {
                    $offset = $x * 4
                    $sumB += $buffer[$offset]; $sumG += $buffer[$offset + 1]; $sumR += $buffer[$offset + 2]; $count++
                }
            }
        } finally {
            $bitmap.UnlockBits($data)
        }

        $meanR = [Math]::Round($sumR / [double]$count, 1)
        $meanG = [Math]::Round($sumG / [double]$count, 1)
        $meanB = [Math]::Round($sumB / [double]$count, 1)
        $luma = 0.299 * $meanR + 0.587 * $meanG + 0.114 * $meanB

        $saved = ""
        if ($bootShots -lt 10) {
            $saved = "boot-{0}.png" -f $bootShots
            $bitmap.Save((Join-Path $OutputDir $saved), [System.Drawing.Imaging.ImageFormat]::Png)
            $bootShots++
        }
        if ($clock.ElapsedMilliseconds -le $captureTransitionUntil) {
            $saved = "transition-{0}-{1:D5}.png" -f $transitionIndex, $frame
            $bitmap.Save((Join-Path $OutputDir $saved), [System.Drawing.Imaging.ImageFormat]::Png)
        } elseif ($Scenario -ne "ThemeSwitch" -and $luma -gt 128) {
            # Under dark navigation/mode probes, any light frame is a flash candidate.
            $saved = "flash-{0:D5}.png" -f $frame
            $bitmap.Save((Join-Path $OutputDir $saved), [System.Drawing.Imaging.ImageFormat]::Png)
        } elseif ($visibleSince -ge 0 -and $shotIndex -lt $shotPlan.Count -and ($clock.ElapsedMilliseconds - $visibleSince) -ge $shotPlan[$shotIndex]) {
            $saved = "stage-{0}.png" -f $shotIndex
            $bitmap.Save((Join-Path $OutputDir $saved), [System.Drawing.Imaging.ImageFormat]::Png)
            $shotIndex++
        }
        $rows.Add("$frame,$($clock.ElapsedMilliseconds),$visible,$($rect.Left),$($rect.Top),$w,$h,$meanR,$meanG,$meanB,$saved")
    } else {
        $rows.Add("$frame,$($clock.ElapsedMilliseconds),$visible,$($rect.Left),$($rect.Top),$w,$h,,,,")
    }
    $frame++
}

if ($null -ne $graphics) { $graphics.Dispose() }
if ($null -ne $bitmap) { $bitmap.Dispose() }
$rows | Set-Content (Join-Path $OutputDir "frames.csv")
if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
[pscustomobject]@{ Frames = $frame; Csv = (Join-Path $OutputDir "frames.csv") } | ConvertTo-Json -Compress

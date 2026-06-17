[CmdletBinding()]
param(
    [int]$ClickX = 0,
    [int]$ClickY = 0,
    [int]$ScrollClicks = 0,
    [string]$OutDir = "C:\Users\johnn\Documents\work\easydict_win32.refactor\artifacts\ui-screenshots\expand-repro"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Nx {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool at);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  delegate bool EnumProc(IntPtr h, IntPtr p);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr LargestVisible(uint target) {
    IntPtr best = IntPtr.Zero; long ba = 0;
    EnumWindows((h, p) => { uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != target || !IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r); long a = (long)(r.Right-r.Left)*(r.Bottom-r.Top);
      if (a > ba) { ba = a; best = h; } return true; }, IntPtr.Zero);
    return best;
  }
  public static void ForceForeground(IntPtr h) {
    ShowWindow(h, 9);
    SetWindowPos(h, new IntPtr(-1), 60, 40, 0, 0, 0x0001 | 0x0040);
    uint ft; GetWindowThreadProcessId(GetForegroundWindow(), out ft);
    uint cur = GetCurrentThreadId();
    AttachThreadInput(cur, ft, true); BringWindowToTop(h); SetForegroundWindow(h); AttachThreadInput(cur, ft, false);
    SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0040);
  }
}
"@

[void][Nx]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-Process easydict_preview_iced -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Get-ChildItem Env: | Where-Object { $_.Name -like 'EASYDICT_PREVIEW_*' } | ForEach-Object { Remove-Item ("Env:" + $_.Name) -ErrorAction SilentlyContinue }

$exe = "C:\Users\johnn\Documents\work\easydict_win32.refactor\rs\target\debug\easydict_preview_iced.exe"
$stderr = Join-Path $OutDir "stderr.log"
$env:RUST_BACKTRACE = "full"
$env:EASYDICT_PREVIEW_WINDOW = "settings"
$env:EASYDICT_PREVIEW_SETTINGS_SECTION = "services"
$env:EASYDICT_PREVIEW_SETTINGS_LOCAL_AI_PROVIDER = "WindowsAI"
if ($env:REPRO_START_EXPANDED -eq "1") { $env:EASYDICT_PREVIEW_SETTINGS_EXPANDED_SERVICE_CONFIGURATIONS = "windows-local-ai" }
$proc = Start-Process -FilePath $exe -PassThru -RedirectStandardError $stderr
Write-Host "pid=$($proc.Id)"

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
    if ($proc.HasExited) { Write-Host "ERROR exited early code=$($proc.ExitCode)"; break }
    $h = [Nx]::LargestVisible([uint32]$proc.Id)
    if ($h -ne [IntPtr]::Zero) { $r0 = New-Object Nx+RECT; [void][Nx]::GetWindowRect($h, [ref]$r0); if (($r0.Right - $r0.Left) -ge 300) { $hwnd = $h; break } }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "no window"; if (Test-Path $stderr) { Get-Content $stderr -Tail 30 }; exit 1 }

[Nx]::ForceForeground($hwnd)
Start-Sleep -Milliseconds 2500
$r = New-Object Nx+RECT
[void][Nx]::GetWindowRect($hwnd, [ref]$r)
$w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
Write-Host "rect: $($r.Left),$($r.Top) ${w}x${hh}"

Add-Type -AssemblyName System.Drawing
function Shot($name) {
    $rr = New-Object Nx+RECT; [void][Nx]::GetWindowRect($hwnd, [ref]$rr)
    $ww = $rr.Right - $rr.Left; $hhh = $rr.Bottom - $rr.Top
    if ($ww -le 0 -or $hhh -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap $ww, $hhh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rr.Left, $rr.Top, 0, 0, (New-Object System.Drawing.Size($ww, $hhh)))
    $p = Join-Path $OutDir $name; $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot: $p"
}
Shot "before.png"

if ($ScrollClicks -ne 0) {
    [void][Nx]::SetCursorPos($r.Left + [int]($w * 0.5), $r.Top + [int]($hh * 0.5))
    Start-Sleep -Milliseconds 150
    [Nx]::mouse_event(0x0800, 0, 0, [uint32]([int]($ScrollClicks * 120)), [IntPtr]::Zero)  # WHEEL
    Start-Sleep -Milliseconds 500
    Shot "after-scroll.png"
}

if ($ClickX -ne 0 -or $ClickY -ne 0) {
    $cx = $r.Left + $ClickX; $cy = $r.Top + $ClickY
    Write-Host "clicking at window-rel ($ClickX,$ClickY) screen ($cx,$cy)"
    [void][Nx]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 200
    [Nx]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    [Nx]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 2500
    $proc.Refresh()
    if ($proc.HasExited) {
        Write-Host ("AFTER CLICK: EXITED code=" + $proc.ExitCode + $(if ($proc.ExitCode -eq -1073741571) { " [STACK OVERFLOW REPRODUCED]" } else { "" }))
    } else { Write-Host "AFTER CLICK: still running"; Shot "after-click.png" }
}

Write-Host "--- stderr ---"
if (Test-Path $stderr) { Get-Content $stderr -Tail 40 }
Get-Process easydict_preview_iced -ErrorAction SilentlyContinue | Stop-Process -Force

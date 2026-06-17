[CmdletBinding()]
param(
    [int]$ScrollClicks = -6,
    [string]$Provider = "Auto",
    [string]$Status = "",
    [string]$OutDir = "C:\Users\johnn\Documents\work\easydict_win32.refactor\artifacts\ui-screenshots\localai-infobars"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Nx2 {
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
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
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

[void][Nx2]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-Process easydict_preview_iced -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Get-ChildItem Env: | Where-Object { $_.Name -like 'EASYDICT_PREVIEW_*' } | ForEach-Object { Remove-Item ("Env:" + $_.Name) -ErrorAction SilentlyContinue }

$exe = "C:\Users\johnn\Documents\work\easydict_win32.refactor\rs\target\debug\easydict_preview_iced.exe"
$stderr = Join-Path $OutDir "stderr.log"
$env:RUST_BACKTRACE = "full"
$env:EASYDICT_PREVIEW_WINDOW = "settings"
$env:EASYDICT_PREVIEW_SETTINGS_SECTION = "services"
$env:EASYDICT_PREVIEW_SETTINGS_LOCAL_AI_PROVIDER = $Provider
if ($Status -ne "") { $env:EASYDICT_PREVIEW_SETTINGS_LOCAL_AI_STATUS = $Status }
$env:EASYDICT_PREVIEW_SETTINGS_EXPANDED_SERVICE_CONFIGURATIONS = "windows-local-ai"
$proc = Start-Process -FilePath $exe -PassThru -RedirectStandardError $stderr
Write-Host "pid=$($proc.Id)"

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
    if ($proc.HasExited) { Write-Host "ERROR exited early code=$($proc.ExitCode)"; break }
    $h = [Nx2]::LargestVisible([uint32]$proc.Id)
    if ($h -ne [IntPtr]::Zero) { $r0 = New-Object Nx2+RECT; [void][Nx2]::GetWindowRect($h, [ref]$r0); if (($r0.Right - $r0.Left) -ge 300) { $hwnd = $h; break } }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "no window"; if (Test-Path $stderr) { Get-Content $stderr -Tail 30 }; exit 1 }

[Nx2]::ForceForeground($hwnd)
Start-Sleep -Milliseconds 2500
$r = New-Object Nx2+RECT
[void][Nx2]::GetWindowRect($hwnd, [ref]$r)
$w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
Write-Host "rect: $($r.Left),$($r.Top) ${w}x${hh}"

Add-Type -AssemblyName System.Drawing
function Shot($name) {
    $rr = New-Object Nx2+RECT; [void][Nx2]::GetWindowRect($hwnd, [ref]$rr)
    $ww = $rr.Right - $rr.Left; $hhh = $rr.Bottom - $rr.Top
    if ($ww -le 0 -or $hhh -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap $ww, $hhh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    # PW_RENDERFULLCONTENT (2): capture the window's own pixels even if occluded.
    [void][Nx2]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $p = Join-Path $OutDir $name; $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot: $p"
}
Shot "top.png"

if ($ScrollClicks -ne 0) {
    [void][Nx2]::SetCursorPos($r.Left + [int]($w * 0.5), $r.Top + [int]($hh * 0.5))
    Start-Sleep -Milliseconds 150
    $delta = [uint32]([int64]([int]($ScrollClicks * 120)) -band 0xFFFFFFFFL)
    [Nx2]::mouse_event(0x0800, 0, 0, $delta, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 600
    Shot "scrolled.png"
}

Write-Host "--- stderr ---"
if (Test-Path $stderr) { Get-Content $stderr -Tail 20 }
Get-Process easydict_preview_iced -ErrorAction SilentlyContinue | Stop-Process -Force

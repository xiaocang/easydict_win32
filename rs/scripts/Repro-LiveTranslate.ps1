[CmdletBinding()]
param(
    [string]$Text = "Hello world",
    [int]$WaitMs = 8000,
    [string]$OutDir = "C:\Users\johnn\Documents\work\easydict_win32.refactor\artifacts\ui-screenshots\live-repro"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Native {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  delegate bool EnumProc(IntPtr h, IntPtr p);
  public struct RECT { public int Left, Top, Right, Bottom; }
  // Largest visible top-level window of the given process (the main window).
  public static IntPtr LargestVisible(uint target) {
    IntPtr best = IntPtr.Zero; long bestArea = 0;
    EnumWindows((h, p) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != target || !IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r);
      long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
      if (area > bestArea) { bestArea = area; best = h; }
      return true;
    }, IntPtr.Zero);
    return best;
  }
  // Move the window to a clear spot and force it to the foreground (AttachThreadInput
  // works around the Win32 foreground lock that blocks background processes).
  public static void ForceForeground(IntPtr h) {
    ShowWindow(h, 9); // SW_RESTORE
    SetWindowPos(h, new IntPtr(-1), 80, 80, 0, 0, 0x0001 | 0x0040); // HWND_TOPMOST, NOSIZE|SHOWWINDOW
    uint fgThread; GetWindowThreadProcessId(GetForegroundWindow(), out fgThread);
    uint cur = GetCurrentThreadId();
    AttachThreadInput(cur, fgThread, true);
    BringWindowToTop(h);
    SetForegroundWindow(h);
    AttachThreadInput(cur, fgThread, false);
    SetWindowPos(h, new IntPtr(-2), 80, 80, 0, 0, 0x0001 | 0x0040); // HWND_NOTOPMOST (keep on top of others but not always)
  }
}
"@

[void][Native]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-Process easydict_preview_iced -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# The production GUI is easydict_preview_iced run WITHOUT any EASYDICT_PREVIEW_* env
# (those env vars switch it into preview mode). Clear them so we get the real app.
Get-ChildItem Env: | Where-Object { $_.Name -like 'EASYDICT_PREVIEW_*' } | ForEach-Object { Remove-Item ("Env:" + $_.Name) -ErrorAction SilentlyContinue }
$exe = "C:\Users\johnn\Documents\work\easydict_win32.refactor\rs\target\debug\easydict_preview_iced.exe"
$stderr = Join-Path $OutDir "stderr.log"
$stdout = Join-Path $OutDir "stdout.log"
$env:RUST_BACKTRACE = "1"
$proc = Start-Process -FilePath $exe -PassThru -RedirectStandardError $stderr -RedirectStandardOutput $stdout
Write-Host "Launched easydict_app pid=$($proc.Id)"

# Find the main window = largest visible top-level window of the process.
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
    if ($proc.HasExited) { Write-Host "ERROR: app exited early"; break }
    $h = [Native]::LargestVisible([uint32]$proc.Id)
    if ($h -ne [IntPtr]::Zero) {
        $r0 = New-Object Native+RECT
        [void][Native]::GetWindowRect($h, [ref]$r0)
        if (($r0.Right - $r0.Left) -ge 200) { $hwnd = $h; break }
    }
}

if ($hwnd -eq [IntPtr]::Zero) {
    Write-Host "ERROR: main window not found"
    if (Test-Path $stderr) { Write-Host "--- stderr ---"; Get-Content $stderr -Tail 40 }
    exit 1
}

[Native]::ForceForeground($hwnd)
Start-Sleep -Milliseconds 800
$r = New-Object Native+RECT
[void][Native]::GetWindowRect($hwnd, [ref]$r)
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
Write-Host "Window rect: $($r.Left),$($r.Top) ${w}x${h}"

# Click the source input (upper-center region of the main window).
$cx = $r.Left + [int]($w * 0.5)
$cy = $r.Top + [int]($h * 0.32)
[void][Native]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 150
[Native]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)  # LEFTDOWN
[Native]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)  # LEFTUP
Start-Sleep -Milliseconds 400

Add-Type -AssemblyName System.Windows.Forms
# Paste via clipboard to bypass any active IME (SendKeys gets converted by a CN IME).
Set-Clipboard -Value $Text
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait("^v")
Start-Sleep -Milliseconds 600
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Write-Host "Typed text + Enter; waiting ${WaitMs}ms for translation..."
Start-Sleep -Milliseconds $WaitMs

# Capture the window region.
Add-Type -AssemblyName System.Drawing
[void][Native]::GetWindowRect($hwnd, [ref]$r)
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$shot = Join-Path $OutDir "live-main.png"
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "Screenshot: $shot"

Write-Host "--- stderr tail ---"
if (Test-Path $stderr) { Get-Content $stderr -Tail 60 } else { Write-Host "(no stderr)" }
Write-Host "--- stdout tail ---"
if (Test-Path $stdout) { Get-Content $stdout -Tail 30 } else { Write-Host "(no stdout)" }

Get-Process easydict_preview_iced -ErrorAction SilentlyContinue | Stop-Process -Force

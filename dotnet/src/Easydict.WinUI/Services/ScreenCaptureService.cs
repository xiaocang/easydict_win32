using System.Diagnostics;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.ScreenCapture;

namespace Easydict.WinUI.Services;

/// <summary>
/// Orchestrates the screen capture flow: creates a Snipaste-style overlay window,
/// waits for user selection, and returns the captured region.
/// The capture runs on a dedicated STA thread so the main UI thread is never blocked.
/// Only one capture can run at a time — concurrent calls return null immediately.
/// </summary>
public sealed class ScreenCaptureService
{
    private int _isCapturing;

    /// <summary>
    /// Starts the screenshot capture flow. Returns the captured region,
    /// or null if the user cancels (Esc / right-click) or if another capture is already in progress.
    /// This method is safe to call from the UI thread — the capture overlay
    /// runs on a separate STA thread.
    /// </summary>
    public async Task<ScreenCaptureResult?> CaptureRegionAsync()
    {
        // Prevent concurrent captures — the window class name is a singleton and
        // two overlays would fight for foreground focus.
        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
        {
            Debug.WriteLine("[ScreenCapture] Capture already in progress, ignoring");
            return null;
        }

        try
        {
            Debug.WriteLine("[ScreenCapture] Starting capture...");

            using var captureWindow = new ScreenCaptureWindow();
            var result = await captureWindow.CaptureAsync();

            if (result is not null)
            {
                Debug.WriteLine($"[ScreenCapture] Region captured: {result.PixelWidth}×{result.PixelHeight}");
            }
            else
            {
                Debug.WriteLine("[ScreenCapture] Capture cancelled by user");
            }

            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _isCapturing, 0);
        }
    }
}

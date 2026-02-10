using System.Diagnostics;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.ScreenCapture;

namespace Easydict.WinUI.Services;

/// <summary>
/// Orchestrates the screen capture flow: creates a Snipaste-style overlay window,
/// waits for user selection, and returns the captured region.
/// The capture runs on a dedicated STA thread so the main UI thread is never blocked.
/// </summary>
public sealed class ScreenCaptureService
{
    /// <summary>
    /// Starts the screenshot capture flow. Returns the captured region,
    /// or null if the user cancels (Esc / right-click).
    /// This method is safe to call from the UI thread — the capture overlay
    /// runs on a separate STA thread.
    /// </summary>
    public async Task<ScreenCaptureResult?> CaptureRegionAsync()
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
}

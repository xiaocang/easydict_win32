using System.Diagnostics;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.ScreenCapture;

namespace Easydict.WinUI.Services;

/// <summary>
/// Orchestrates the screen capture flow: creates a Snipaste-style overlay window,
/// waits for user selection, and returns the captured region.
/// The capture runs on a dedicated STA thread so the main UI thread is never blocked.
/// Serializes captures via a semaphore: only one overlay can be open at a time;
/// waiting callers block until the foreground capture finishes or is cancelled.
/// </summary>
public sealed class ScreenCaptureService
{
    private readonly SemaphoreSlim _captureSemaphore = new(1, 1);
    private ScreenCaptureWindow? _currentCaptureWindow;

    /// <summary>
    /// Starts the screenshot capture flow. Returns the captured region,
    /// or null if the user cancels (Esc / right-click).
    /// When <paramref name="cancellationToken"/> fires, the overlay tears down
    /// and this method returns null. This method is safe to call from the UI
    /// thread — the capture overlay runs on a separate STA thread.
    /// </summary>
    public async Task<ScreenCaptureResult?> CaptureRegionAsync(CancellationToken cancellationToken = default)
    {
        await _captureSemaphore.WaitAsync(cancellationToken);
        try
        {
            Debug.WriteLine("[ScreenCapture] Starting capture...");

            using var captureWindow = new ScreenCaptureWindow();
            _currentCaptureWindow = captureWindow;
            var result = await captureWindow.CaptureAsync(cancellationToken);

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
            _currentCaptureWindow = null;
            _captureSemaphore.Release();
        }
    }

    /// <summary>
    /// Cancels the currently showing capture overlay, if any.
    /// Safe to call from any thread; does nothing when no capture is in progress.
    /// </summary>
    public void CancelCurrentCapture()
    {
        _currentCaptureWindow?.Cancel();
    }
}

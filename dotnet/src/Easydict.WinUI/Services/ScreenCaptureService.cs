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
    private static int _activeCaptureCount;

    private readonly SemaphoreSlim _captureSemaphore = new(1, 1);
    private readonly Func<ScreenCaptureWindow>? _captureWindowFactory;
    private ScreenCaptureWindow? _currentCaptureWindow;

    /// <summary>
    /// Gets whether an acquired screen capture is still active, including overlay teardown.
    /// OCR recognition after the overlay closes is not included.
    /// </summary>
    internal static bool IsCaptureInProgress => Volatile.Read(ref _activeCaptureCount) > 0;

    public ScreenCaptureService()
    {
    }

    internal ScreenCaptureService(Func<ScreenCaptureWindow> captureWindowFactory)
    {
        _captureWindowFactory = captureWindowFactory
            ?? throw new ArgumentNullException(nameof(captureWindowFactory));
    }

    /// <summary>
    /// Starts the screenshot capture flow. Returns the captured region,
    /// or null if the user cancels (Esc / right-click).
    /// When <paramref name="cancellationToken"/> fires, this method returns null
    /// only after the overlay and its GDI resources have been torn down. This method
    /// is safe to call from the UI thread — the capture overlay runs on a separate STA thread.
    /// </summary>
    public async Task<ScreenCaptureResult?> CaptureRegionAsync(CancellationToken cancellationToken = default)
    {
        await _captureSemaphore.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _activeCaptureCount);
        try
        {
            Debug.WriteLine("[ScreenCapture] Starting capture...");

            using var captureWindow = _captureWindowFactory?.Invoke() ?? new ScreenCaptureWindow();
            Volatile.Write(ref _currentCaptureWindow, captureWindow);
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
            Volatile.Write(ref _currentCaptureWindow, null);
            Interlocked.Decrement(ref _activeCaptureCount);
            _captureSemaphore.Release();
        }
    }

    /// <summary>
    /// Cancels the currently showing capture overlay, if any.
    /// Safe to call from any thread; does nothing when no capture is in progress.
    /// </summary>
    public void CancelCurrentCapture()
    {
        var captureWindow = Volatile.Read(ref _currentCaptureWindow);
        captureWindow?.Cancel();
    }
}

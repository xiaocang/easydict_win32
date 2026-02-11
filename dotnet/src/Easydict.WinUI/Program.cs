using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI;

/// <summary>
/// Custom entry point that intercepts --ocr-translate before starting the WinUI app.
/// When launched from the Windows Shell context menu, the app signals the running
/// instance via a named event and exits immediately (no second window).
/// </summary>
public static class Program
{
    /// <summary>
    /// Named event used to signal the running instance to start OCR capture.
    /// AutoReset: once signaled, the event resets automatically after the waiter wakes.
    /// </summary>
    internal const string OcrTranslateEventName = @"Local\Easydict-OcrTranslate";

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--ocr-translate"))
        {
            // Signal the running instance and exit without creating a window.
            try
            {
                using var evt = EventWaitHandle.OpenExisting(OcrTranslateEventName);
                evt.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // App is not running — nothing to signal.
            }
            return;
        }

        // Normal WinUI 3 startup (replicates the auto-generated Main).
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}

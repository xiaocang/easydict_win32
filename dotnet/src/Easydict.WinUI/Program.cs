using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Easydict.WinUI.Services;

namespace Easydict.WinUI;

/// <summary>
/// Custom entry point that handles:
///   1. --ocr-translate CLI arg (from Shell context menu) → signal running instance via named event
///   2. easydict://ocr-translate protocol activation (from browser extension) → same IPC signal
///   3. Normal WinUI 3 startup
///
/// When the app is already running, the second process signals it and exits immediately.
/// When the app is NOT running, it starts normally and queues OCR after initialization.
/// </summary>
public static class Program
{
    /// <summary>
    /// Named event used to signal the running instance to start OCR capture.
    /// AutoReset: once signaled, the event resets automatically after the waiter wakes.
    /// </summary>
    internal const string OcrTranslateEventName = @"Local\Easydict-OcrTranslate";

    /// <summary>
    /// Set to true when the app is cold-launched via protocol activation for OCR.
    /// Checked by App.xaml.cs after InitializeServices() to trigger OCR on first launch.
    /// </summary>
    internal static bool PendingOcrTranslate { get; private set; }

    /// <summary>
    /// Set to a path like "local-api" when launched via easydict://settings/&lt;path&gt;.
    /// Consumed by App.xaml.cs to deep-link into a specific Settings tab.
    /// </summary>
    internal static string? PendingSettingsPath { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
#if LOCAL_DEBUG_LONGDOC_CLI
        if (LongDocumentCliCommand.IsCommand(args))
        {
            Environment.ExitCode = LongDocumentCliCommand.RunAsync(args).GetAwaiter().GetResult();
            return;
        }
#endif

        // Unpackaged (Inno/portable) installs rely on HKCU protocol registration.
        // Repair registration early so future easydict:// launches are reliable.
        if (!EasydictConditions.IsPackaged)
        {
            ProtocolRegistrationService.EnsureRegistered();
        }

        // Determine if this launch should trigger OCR
        var shouldTriggerOcr = args.Contains("--ocr-translate")
            || IsOcrProtocolActivation()
            || args.Any(a => a.StartsWith("easydict://ocr-translate", StringComparison.OrdinalIgnoreCase));

        if (shouldTriggerOcr)
        {
            // Try to signal the running instance
            try
            {
                using var evt = EventWaitHandle.OpenExisting(OcrTranslateEventName);
                evt.Set();
                return; // Running instance signaled — exit
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // App is not running — fall through to start normally.
                // Mark pending so App.xaml.cs triggers OCR after initialization.
                PendingOcrTranslate = true;
            }
        }

        // Check for easydict://settings/<path> deep link. Resolves both from packaged
        // protocol activation and from argv (unpackaged / second-instance launch).
        var settingsPath = ParseSettingsPath(args);
        if (settingsPath != null)
        {
            PendingSettingsPath = settingsPath;
            // Best-effort: nudge an already-running instance to show the main window
            // (the running app's protocol-activation handler will navigate to the tab).
            // We still need to start (single-instance redirector handles the rest).
        }

        // Replicates the auto-generated Main suppressed by DISABLE_XAML_GENERATED_MAIN.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    /// <summary>
    /// Checks whether this process was launched via easydict://ocr-translate protocol activation.
    /// Uses the Windows App SDK AppLifecycle API.
    /// </summary>
    private static bool IsOcrProtocolActivation()
    {
        // AppInstance.GetActivatedEventArgs throws when there is no activation context
        // (plain command-line launch or unpackaged). Unpackaged builds receive
        // easydict:// via argv, which Main handles directly.
        if (!EasydictConditions.IsPackaged)
            return false;

        try
        {
            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs.Kind != ExtendedActivationKind.Protocol)
                return false;

            if (activatedArgs.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
            {
                // easydict://ocr-translate → Host = "ocr-translate"
                return string.Equals(protocolArgs.Uri.Host, "ocr-translate",
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // WinRT activation infrastructure not available.
        }
        return false;
    }

    /// <summary>
    /// Try to resolve a Settings deep-link from either argv (unpackaged) or the packaged
    /// protocol activation context. Returns e.g. "local-api" for easydict://settings/local-api.
    /// </summary>
    private static string? ParseSettingsPath(string[] args)
    {
        foreach (var a in args)
        {
            if (TryExtractSettingsPath(a, out var path))
                return path;
        }

        if (!EasydictConditions.IsPackaged) return null;
        try
        {
            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs.Kind != ExtendedActivationKind.Protocol)
                return null;
            if (activatedArgs.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
            {
                if (TryExtractSettingsPath(protocolArgs.Uri.ToString(), out var path))
                    return path;
            }
        }
        catch (System.Runtime.InteropServices.COMException) { }
        return null;
    }

    private static bool TryExtractSettingsPath(string raw, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(raw)) return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "easydict", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "settings", StringComparison.OrdinalIgnoreCase)) return false;
        path = uri.AbsolutePath.Trim('/');
        return path.Length > 0;
    }
}

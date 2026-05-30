using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Easydict.WinUI.Services;
#if !MICROSOFT_WINDOWSAPPSDK_SELFCONTAINED
using Microsoft.Windows.ApplicationModel.DynamicDependency;
#endif

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

            // WinAppSDK has two mutually exclusive unpackaged deployment models:
            // self-contained portable builds load the local runtime DLLs through
            // the SDK's UndockedRegFreeWinRT module initializer, while
            // framework-dependent builds must bootstrap the installed runtime
            // package before any Microsoft.UI.Xaml type is touched. Calling
            // Bootstrap.TryInitialize in a self-contained build mixes those models
            // and can crash on launch when both local DLLs and a framework package
            // are present.
            //
            // For framework-dependent unpackaged builds, this explicit call is the
            // only Bootstrap surface. The csproj disables the SDK bootstrap
            // auto-initializer by default, and the hand-rolled Main suppresses the
            // SDK-generated entry point via DISABLE_XAML_GENERATED_MAIN.
#if !MICROSOFT_WINDOWSAPPSDK_SELFCONTAINED
            //
            // Major.Minor = 2.0 encoded in the high/low 16-bit halves.
            const uint windowsAppSdkVersion = 0x00020000;
            if (!Bootstrap.TryInitialize(windowsAppSdkVersion, out var bootstrapHresult))
            {
                Console.Error.WriteLine(
                    $"[Easydict] WinAppSDK Bootstrap.TryInitialize failed: 0x{bootstrapHresult:X8}. " +
                    "Install the Windows App SDK 2.0 runtime (Microsoft.WindowsAppRuntime.2).");
                Environment.ExitCode = bootstrapHresult != 0 ? bootstrapHresult : 1;
                return;
            }
#endif
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
}

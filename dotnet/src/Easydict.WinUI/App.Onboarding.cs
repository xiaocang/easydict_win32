using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Views;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI;

public partial class App
{
    internal static void OpenQuickStart(QuickStartEntryPoint entryPoint)
    {
        var app = Instance;
        app._window?.DispatcherQueue.TryEnqueue(() =>
        {
            app.ShowAndActivateWindow();
            var frame = app.EnsureRootFrame();
            if (frame is null || frame.Content is QuickStartPage) return;
            frame.Navigate(typeof(QuickStartPage), entryPoint);
        });
    }

    private void TryShowFirstLaunchQuickStart(string? activationArguments)
    {
        // A command/hotkey/startup action must keep doing the action the user requested.
        if (Program.PendingOcrTranslate || !string.IsNullOrWhiteSpace(activationArguments)) return;
#if WINUI_TEST
        // Existing UI suites exercise normal app startup. The onboarding suite explicitly opts into the real
        // fresh-install route instead of having every unrelated test unexpectedly land on the tutorial.
        if (Environment.GetEnvironmentVariable("EASYDICT_TEST_ONBOARDING_AUTO") != "1") return;
#endif
        if (OnboardingStateService.Instance.ShouldAutoShow)
            OpenQuickStart(QuickStartEntryPoint.Automatic);
    }
}

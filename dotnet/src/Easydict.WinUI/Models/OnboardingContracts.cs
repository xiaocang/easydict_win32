namespace Easydict.WinUI.Models;

/// <summary>Offline demonstrations available in the shared quick-start experience.</summary>
public enum QuickStartDemoKind
{
    SelectionTranslation,
    ScreenshotTranslation
}

/// <summary>The surface that opened quick start; replay does not reset installation progress.</summary>
public enum QuickStartEntryPoint
{
    Automatic,
    Help,
    About,
    Tray
}

/// <summary>A result from this practice request, never from the offline demonstration.</summary>
public sealed record OnboardingPracticeResult(bool Succeeded, string? TranslatedText = null, string? ErrorMessage = null)
{
    public bool HasTranslation => Succeeded && !string.IsNullOrWhiteSpace(TranslatedText);
}

/// <summary>Runs selected practice text through the existing translation pipeline.</summary>
public interface IOnboardingPracticeRunner
{
    Task<OnboardingPracticeResult> TranslateSelectionAsync(string text, CancellationToken cancellationToken);
}

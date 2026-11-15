using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>Bridges the practice control's selection to the existing Mini translation pipeline.</summary>
internal sealed class MiniOnboardingPracticeRunner : IOnboardingPracticeRunner
{
    public Task<OnboardingPracticeResult> TranslateSelectionAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new OnboardingPracticeResult(false, ErrorMessage:
                LocalizationService.Instance.GetStringOrDefault("OnboardingPracticeSelectText", "Select some text to translate first.")));
        }
        return MiniWindowService.Instance.TranslatePracticeSelectionAsync(text, cancellationToken);
    }
}

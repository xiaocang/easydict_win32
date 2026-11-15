namespace Easydict.WinUI.Services;

/// <summary>Persists first-run, practice, and support invitation progress.</summary>
public sealed class OnboardingStateService
{
    private static readonly Lazy<OnboardingStateService> LazyInstance = new(() => new OnboardingStateService());
    public static OnboardingStateService Instance => LazyInstance.Value;

    private readonly SettingsService _settings;
    private readonly bool _settingsFileExistedAtStartup;

    public OnboardingStateService(SettingsService? settings = null)
    {
        _settings = settings ?? SettingsService.Instance;
        _settingsFileExistedAtStartup = _settings.SettingsFileExistedAtStartup;
    }

    /// <summary>True once for a fresh installation; never automatically for an existing install.</summary>
    public bool ShouldAutoShow
    {
        get
        {
            if (_settings.QuickStartAutoShowPending.HasValue)
            {
                return _settings.QuickStartAutoShowPending.Value
                    && !_settings.QuickStartHasBeenShown
                    && !_settings.QuickStartWasSkipped;
            }

            // A settings file from before this feature is an existing installation.
            // Only a truly new install may auto-show when the pending key is absent.
            return !_settingsFileExistedAtStartup
                && !_settings.HasPersistedSetting(nameof(SettingsService.QuickStartAutoShowPending))
                && !_settings.QuickStartHasBeenShown
                && !_settings.QuickStartWasSkipped;
        }
    }

    public void MarkShown()
    {
        _settings.QuickStartAutoShowPending = false;
        _settings.QuickStartHasBeenShown = true;
        _settings.Save();
    }

    public void MarkSkipped()
    {
        _settings.QuickStartAutoShowPending = false;
        _settings.QuickStartWasSkipped = true;
        _settings.Save();
    }

    public void MarkPracticeSucceeded()
    {
        _settings.QuickStartAutoShowPending = false;
        _settings.QuickStartHasBeenShown = true;
        _settings.QuickStartPracticeSucceeded = true;
        _settings.Save();
    }

    public bool MarkInvitationShownIfEligible()
    {
        if (!SupportInvitationEligible)
        {
            return false;
        }

        MarkInvitationShown();
        return true;
    }

    public bool SupportInvitationEligible =>
        _settings.QuickStartPracticeSucceeded
        && !_settings.SupportInvitationWasShown
        && !_settings.SupportInvitationWasDismissed;

    public void MarkInvitationShown()
    {
        _settings.SupportInvitationWasShown = true;
        _settings.Save();
    }

    public void DismissInvitation()
    {
        _settings.SupportInvitationWasDismissed = true;
        _settings.Save();
    }

    public void MarkGitHubOpened()
    {
        _settings.GitHubSupportWasOpened = true;
        _settings.Save();
    }
}

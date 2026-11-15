using Windows.System;

namespace Easydict.WinUI.Services;

/// <summary>Opens Easydict's GitHub page without claiming that the user starred it.</summary>
public static class GitHubSupportService
{
    public const string RepositoryUrl = "https://github.com/xiaocang/easydict_win32";

    public static async Task<bool> OpenAsync()
    {
        try
        {
            var opened = await Launcher.LaunchUriAsync(new Uri(RepositoryUrl));
            if (opened)
            {
                OnboardingStateService.Instance.MarkGitHubOpened();
            }

            return opened;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GitHubSupport] Could not open GitHub: {ex.Message}");
            return false;
        }
    }
}

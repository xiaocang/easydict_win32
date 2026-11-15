using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
[Collection("SettingsService")]
public sealed class OnboardingStateServiceTests
{
    [Fact]
    public void NewInstall_AutoShowsOnce_AndShownStateSurvivesRestart()
    {
        using var directory = new TemporaryDirectory();
        var settings = new SettingsService(directory.Path);
        var state = new OnboardingStateService(settings);

        state.ShouldAutoShow.Should().BeTrue();
        state.MarkShown();
        state.ShouldAutoShow.Should().BeFalse();

        var restarted = new OnboardingStateService(new SettingsService(directory.Path));
        restarted.ShouldAutoShow.Should().BeFalse();
    }

    [Fact]
    public void ExistingInstallWithoutOnboardingKeys_DoesNotAutoShow()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{}");

        var state = new OnboardingStateService(new SettingsService(directory.Path));

        state.ShouldAutoShow.Should().BeFalse();
    }

    [Fact]
    public void ExistingInstallWithCorruptSettings_DoesNotAutoShow()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "not-json");

        var state = new OnboardingStateService(new SettingsService(directory.Path));

        state.ShouldAutoShow.Should().BeFalse();
    }

    [Fact]
    public void SkippingAutoShow_PersistsAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var state = new OnboardingStateService(new SettingsService(directory.Path));
        state.ShouldAutoShow.Should().BeTrue();

        state.MarkSkipped();

        var restarted = new OnboardingStateService(new SettingsService(directory.Path));
        restarted.ShouldAutoShow.Should().BeFalse();
    }

    [Fact]
    public void PracticeSuccessIsPersistedSeparatelyFromShowingTheDemo()
    {
        using var directory = new TemporaryDirectory();
        var state = new OnboardingStateService(new SettingsService(directory.Path));

        state.MarkShown();
        var afterDemo = new OnboardingStateService(new SettingsService(directory.Path));
        afterDemo.SupportInvitationEligible.Should().BeFalse();

        afterDemo.MarkPracticeSucceeded();
        var afterRestart = new OnboardingStateService(new SettingsService(directory.Path));
        afterRestart.SupportInvitationEligible.Should().BeTrue();
    }

    [Fact]
    public void SupportInvitation_IsShownAtMostOnce_AndDismissalPersists()
    {
        using var directory = new TemporaryDirectory();
        var state = new OnboardingStateService(new SettingsService(directory.Path));
        state.MarkPracticeSucceeded();

        state.MarkInvitationShownIfEligible().Should().BeTrue();
        state.MarkInvitationShownIfEligible().Should().BeFalse();

        var restarted = new OnboardingStateService(new SettingsService(directory.Path));
        restarted.SupportInvitationEligible.Should().BeFalse();
    }

    [Fact]
    public void DismissedSupportInvitation_DoesNotReturnAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var state = new OnboardingStateService(new SettingsService(directory.Path));
        state.MarkPracticeSucceeded();
        state.DismissInvitation();

        new OnboardingStateService(new SettingsService(directory.Path))
            .SupportInvitationEligible.Should().BeFalse();
    }

    [Fact]
    public void SaveFailureCanBeRetriedAndDoesNotReportSuccessEarly()
    {
        using var directory = new TemporaryDirectory();
        var settings = new SettingsService(directory.Path);
        var state = new OnboardingStateService(settings);
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, "locked-content");

        using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Action save = state.MarkSkipped;
            save.Should().Throw<IOException>();
        }

        state.MarkSkipped();
        new OnboardingStateService(new SettingsService(directory.Path))
            .ShouldAutoShow.Should().BeFalse();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Easydict.WinUI.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

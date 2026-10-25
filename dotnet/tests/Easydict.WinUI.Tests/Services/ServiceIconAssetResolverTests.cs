using Easydict.WinUI.Services;
using Easydict.WinUI.Models;
using Easydict.WinUI.Views;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class ServiceIconAssetResolverTests
{
    [Theory]
    [InlineData("magpie", "Magpie")]
    [InlineData("custom-openai", "CustomOpenAI")]
    [InlineData("codex", "codex")]
    [InlineData("claude-code", "claude-code")]
    public void ProviderServiceIdsResolveToShippedIconNames(string serviceId, string expected)
    {
        ServiceIconAssetResolver.ResolveProviderIconName(serviceId).Should().Be(expected);
    }

    [Fact]
    public void GitHubIconUsesLightThemeAssetAlias()
    {
        ServiceIconAssetResolver.GetIconName("github", Microsoft.UI.Xaml.ElementTheme.Light)
            .Should().Be(ServiceIconAssetResolver.GitHubOnLightIconName);
    }

    [Fact]
    public void SavedMagpieProfileServiceIdResolvesToMagpieIcon()
    {
        var savedProfile = new AiServiceProfile
        {
            ProfileId = "profile-1",
            ServiceId = "ai-profile:profile-1",
            ProviderType = "magpie"
        };

        ServiceIconAssetResolver.ResolveProviderIconName(savedProfile.ServiceId, [savedProfile])
            .Should().Be("Magpie");
    }

    [Fact]
    public void DeletedProfileServiceIdDoesNotResolveAsSavedProvider()
    {
        ServiceIconAssetResolver.ResolveProviderIconName("ai-profile:deleted", Array.Empty<AiServiceProfile>())
            .Should().Be("ai-profile:deleted");
    }

    [Fact]
    public void ViewsOnlyIncludeSavedAiProfileServiceIds()
    {
        var savedProfile = new AiServiceProfile
        {
            ProfileId = "profile-1",
            ServiceId = "ai-profile:profile-1",
            ProviderType = "magpie"
        };
        var savedLegacyProfile = new AiServiceProfile
        {
            ProfileId = "migrated-1",
            ServiceId = "openai",
            ProviderType = "openai"
        };

        SettingsPage.ShouldIncludeServiceInViews("openai", Array.Empty<AiServiceProfile>()).Should().BeFalse();
        SettingsPage.ShouldIncludeServiceInViews("ai-profile:profile-1", [savedProfile]).Should().BeTrue();
        SettingsPage.ShouldIncludeServiceInViews("ai-profile:deleted", [savedProfile]).Should().BeFalse();
        SettingsPage.ShouldIncludeServiceInViews("openai", [savedLegacyProfile]).Should().BeTrue();
    }
}

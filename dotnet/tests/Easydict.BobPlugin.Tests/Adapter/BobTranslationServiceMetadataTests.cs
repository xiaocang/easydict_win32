using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Adapter;

/// <summary>
/// The metadata the host reads before ever running a plugin: identity, origin, configuration
/// state, execution policy and cache separation.
/// </summary>
public class BobTranslationServiceMetadataTests
{
    [Fact]
    public void TheServiceIdIsNamespacedAndTheNameIsThePluginsOwn()
    {
        using var fixture = PluginFixture.Create("dict");
        var service = fixture.Service();

        service.ServiceId.Should().Be("bob:test.dict:test");
        service.DisplayName.Should().Be("Dictionary", "the plugin marking is carried by Origin, not the name");
        service.IsStreaming.Should().BeTrue();
    }

    [Fact]
    public void TheOriginMarksItAsAThirdPartyPlugin()
    {
        using var fixture = PluginFixture.Create("dict");

        var origin = fixture.Service().Origin;

        origin.Kind.Should().Be(ServiceOriginKind.Plugin);
        origin.IsNative.Should().BeFalse();
        origin.Label.Should().Be("Bob");
        origin.Detail.Should().Be("test.dict v2.1.0");
    }

    [Fact]
    public void APluginWithoutSecureOptionsNeedsNoConfiguration()
    {
        using var fixture = PluginFixture.Create("dict");
        var service = fixture.Service();

        service.RequiresApiKey.Should().BeFalse();
        service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void APluginIsUnconfiguredUntilItsSecureOptionsHaveValues()
    {
        using var missing = PluginFixture.Create("options", secureOptionIds: ["apiKey"]);
        using var blank = PluginFixture.Create(
            "options",
            options: new Dictionary<string, string> { ["apiKey"] = "   " },
            secureOptionIds: ["apiKey"]);
        using var filled = PluginFixture.Create(
            "options",
            options: new Dictionary<string, string> { ["apiKey"] = "sk-test" },
            secureOptionIds: ["apiKey"]);

        missing.Service().RequiresApiKey.Should().BeTrue();
        missing.Service().IsConfigured.Should().BeFalse();
        blank.Service().IsConfigured.Should().BeFalse();
        filled.Service().IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void PluginsAreConservativeByDefault()
    {
        using var fixture = PluginFixture.Create("dict");

        var policy = fixture.Service().ExecutionPolicy;

        policy.AllowHostRetry.Should().BeFalse();
        policy.AllowResultCache.Should().BeFalse();
        policy.AllowPhoneticEnrichment.Should().BeFalse();
    }

    [Fact]
    public void ThePolicyIsWhateverTheUserOptedInTo()
    {
        using var fixture = PluginFixture.Create("dict", policy: ServiceExecutionPolicy.Default);

        fixture.Service().ExecutionPolicy.Should().Be(ServiceExecutionPolicy.Default);
    }

    [Fact]
    public void TheCacheDiscriminatorSeparatesOptionSets()
    {
        using var first = PluginFixture.Create(
            "options",
            options: new Dictionary<string, string> { ["model"] = "fast" });
        using var second = PluginFixture.Create(
            "options",
            options: new Dictionary<string, string> { ["model"] = "accurate" });

        var a = first.Service().CacheKeyDiscriminator;
        var b = second.Service().CacheKeyDiscriminator;

        a.Should().NotBeNullOrEmpty();
        a.Should().StartWith("3.4.5|", "the plugin version has to separate cache entries too");
        b.Should().NotBe(a);
    }

    [Fact]
    public void TheCacheDiscriminatorIsStableAndOrderIndependent()
    {
        using var first = PluginFixture.Create(
            "options",
            options: new Dictionary<string, string> { ["apiKey"] = "k", ["model"] = "fast" });
        using var second = PluginFixture.Create(
            "options",
            options: new Dictionary<string, string> { ["model"] = "fast", ["apiKey"] = "k" });

        first.Service().CacheKeyDiscriminator.Should().Be(second.Service().CacheKeyDiscriminator);
    }

    [Fact]
    public void WithoutADeclaredLanguageListEveryPairIsAccepted()
    {
        using var fixture = PluginFixture.Create("dict");
        var service = fixture.Service();

        service.SupportedLanguages.Should().BeEmpty();
        service.SupportsLanguagePair(Language.Auto, Language.Korean).Should().BeTrue();
        service.SupportsLanguagePair(Language.Arabic, Language.Thai).Should().BeTrue();
    }

    [Fact]
    public void ADeclaredLanguageListIsResolvedAndEnforced()
    {
        using var fixture = PluginFixture.Create(
            "languages",
            supportedLanguageCodes: ["auto", "en", "zh-Hans", "not-a-language"]);
        var service = fixture.Service();

        service.SupportedLanguages.Should().Equal(Language.English, Language.SimplifiedChinese);
        service.SupportsLanguagePair(Language.Auto, Language.English).Should().BeTrue();
        service.SupportsLanguagePair(Language.English, Language.SimplifiedChinese).Should().BeTrue();
        service.SupportsLanguagePair(Language.English, Language.Korean).Should().BeFalse();
    }

    [Fact]
    public async Task LanguageDetectionIsLeftToTheHost()
    {
        using var fixture = PluginFixture.Create("dict");

        (await fixture.Service().DetectLanguageAsync("hello")).Should().Be(Language.Auto);
    }
}

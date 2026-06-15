using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

public class DeepLServiceTests
{
    private readonly HttpClient _httpClient;
    private readonly DeepLService _service;

    public DeepLServiceTests()
    {
        _httpClient = new HttpClient();
        _service = new DeepLService(_httpClient);
    }

    [Fact]
    public void SupportedLanguages_ContainsTraditionalChinese()
    {
        _service.SupportedLanguages.Should().Contain(Language.TraditionalChinese);
    }

    [Fact]
    public void SupportsLanguagePair_TraditionalChineseToEnglish_ReturnsTrue()
    {
        _service.SupportsLanguagePair(Language.TraditionalChinese, Language.English)
            .Should().BeTrue();
    }

    [Fact]
    public void SupportsLanguagePair_EnglishToTraditionalChinese_ReturnsTrue()
    {
        _service.SupportsLanguagePair(Language.English, Language.TraditionalChinese)
            .Should().BeTrue();
    }

    [Theory]
    // Regression set for #174 (initially missing) ...
    [InlineData(Language.Vietnamese)]
    [InlineData(Language.Arabic)]
    [InlineData(Language.Thai)]
    [InlineData(Language.Hebrew)]
    [InlineData(Language.Tamil)]
    [InlineData(Language.Telugu)]
    // ... plus the rest of DeepL's current (100+ language, next-gen model) support present in the enum.
    [InlineData(Language.Hindi)]
    [InlineData(Language.Bengali)]
    [InlineData(Language.Urdu)]
    [InlineData(Language.Malay)]
    [InlineData(Language.Filipino)]
    [InlineData(Language.Persian)]
    [InlineData(Language.Estonian)]
    [InlineData(Language.Latvian)]
    [InlineData(Language.Lithuanian)]
    [InlineData(Language.Slovak)]
    [InlineData(Language.Slovenian)]
    public void SupportedLanguages_ContainsDeepLSupportedLanguages(Language language)
    {
        // DeepL supports these and they exist in the app's Language enum, so local validation
        // (the only consumer of SupportedLanguages) must not reject them.
        _service.SupportedLanguages.Should().Contain(language);
    }

    [Fact]
    public void SupportedLanguages_ExcludesClassicalChinese()
    {
        // DeepL has no Classical/Literary Chinese target; it is the one enum language DeepL omits.
        _service.SupportedLanguages.Should().NotContain(Language.ClassicalChinese);
    }

    [Fact]
    public void SupportsLanguagePair_JapaneseToVietnamese_ReturnsTrue()
    {
        // Exact repro from #174.
        _service.SupportsLanguagePair(Language.Japanese, Language.Vietnamese)
            .Should().BeTrue();
    }

    [Fact]
    public void SupportsLanguagePair_EnglishToVietnamese_ReturnsTrue()
    {
        _service.SupportsLanguagePair(Language.English, Language.Vietnamese)
            .Should().BeTrue();
    }

    [Fact]
    public void ServiceId_IsDeepL()
    {
        _service.ServiceId.Should().Be("deepl");
    }

    [Fact]
    public void DisplayName_IsDeepL()
    {
        _service.DisplayName.Should().Be("DeepL");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        // Web mode doesn't require API key
        _service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_IsTrue()
    {
        // Web mode is always available
        _service.IsConfigured.Should().BeTrue();
    }
}

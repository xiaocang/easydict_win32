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
    public void SupportedLanguages_ApiMode_ContainsDeepLSupportedLanguages(Language language)
    {
        // The official API supports the full next-gen set; an API key selects API mode.
        _service.Configure("test-key:fx", useWebFirst: true);
        _service.SupportedLanguages.Should().Contain(language);
    }

    [Fact]
    public void SupportedLanguages_ExcludesClassicalChinese()
    {
        // DeepL has no Classical/Literary Chinese target; excluded in both web and API mode.
        _service.SupportedLanguages.Should().NotContain(Language.ClassicalChinese);
        _service.Configure("test-key:fx", useWebFirst: true);
        _service.SupportedLanguages.Should().NotContain(Language.ClassicalChinese);
    }

    [Fact]
    public void SupportsLanguagePair_ApiMode_JapaneseToVietnamese_ReturnsTrue()
    {
        // Exact repro from #174 — works once an API key is configured (Vietnamese is API-only).
        _service.Configure("test-key:fx", useWebFirst: true);
        _service.SupportsLanguagePair(Language.Japanese, Language.Vietnamese)
            .Should().BeTrue();
    }

    [Fact]
    public void SupportsLanguagePair_ApiMode_EnglishToVietnamese_ReturnsTrue()
    {
        _service.Configure("test-key:fx", useWebFirst: true);
        _service.SupportsLanguagePair(Language.English, Language.Vietnamese)
            .Should().BeTrue();
    }

    [Theory]
    // Next-gen languages are API-only; the free web JSON-RPC endpoint rejects them (HTTP 400),
    // so keyless mode must not offer them (root cause of "DeepL web translation failed: BadRequest").
    [InlineData(Language.Vietnamese)]
    [InlineData(Language.Arabic)]
    [InlineData(Language.Thai)]
    [InlineData(Language.Hindi)]
    public void SupportedLanguages_KeylessWebMode_ExcludesApiOnlyLanguages(Language language)
    {
        _service.SupportedLanguages.Should().NotContain(language);
    }

    [Fact]
    public void SupportedLanguages_KeylessWebMode_ContainsClassicLanguages()
    {
        _service.SupportedLanguages.Should().Contain(Language.English);
        _service.SupportedLanguages.Should().Contain(Language.German);
        _service.SupportedLanguages.Should().Contain(Language.Japanese);
    }

    [Fact]
    public void SupportsLanguagePair_KeylessWebMode_EnglishToVietnamese_ReturnsFalse()
    {
        // No API key: Vietnamese is not translatable via the free web endpoint.
        _service.SupportsLanguagePair(Language.English, Language.Vietnamese)
            .Should().BeFalse();
    }

    [Fact]
    public async Task TranslateAsync_KeylessWebMode_VietnameseTarget_ThrowsHelpfulMessage()
    {
        // Validation fails before any network call, with guidance to add an API key.
        var request = new TranslationRequest
        {
            Text = "Hello",
            FromLanguage = Language.English,
            ToLanguage = Language.Vietnamese
        };

        var act = async () => await _service.TranslateAsync(request);

        (await act.Should().ThrowAsync<TranslationException>())
            .Which.Message.Should().Contain("API key");
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

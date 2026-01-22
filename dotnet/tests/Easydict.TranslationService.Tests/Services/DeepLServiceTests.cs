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

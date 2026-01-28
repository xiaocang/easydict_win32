using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Integration tests for GoogleTranslateService using real API calls.
/// No API key required.
/// </summary>
[Trait("Category", "Integration")]
public class GoogleTranslateServiceIntegrationTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly GoogleTranslateService _service;

    public GoogleTranslateServiceIntegrationTests()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _service = new GoogleTranslateService(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [Fact]
    public async Task TranslateAsync_EnglishToChinese_ReturnsChineseTranslation()
    {
        var request = new TranslationRequest
        {
            Text = "Hello, world!",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        result.TranslatedText.Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "translation should contain Chinese characters");
    }

    [Fact]
    public async Task TranslateAsync_ChineseToEnglish_ReturnsEnglishTranslation()
    {
        var request = new TranslationRequest
        {
            Text = "你好世界",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        var lowerText = result.TranslatedText.ToLower();
        (lowerText.Contains("hello") || lowerText.Contains("world")).Should().BeTrue(
            "translation of '你好世界' should contain 'hello' or 'world'");
    }
}

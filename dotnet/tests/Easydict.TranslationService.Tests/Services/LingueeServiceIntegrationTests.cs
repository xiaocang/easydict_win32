using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Integration tests for LingueeService using real API calls.
/// No API key required.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Service", "linguee")]
public class LingueeServiceIntegrationTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LingueeService _service;

    public LingueeServiceIntegrationTests()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _service = new LingueeService(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [Fact]
    public async Task TranslateAsync_EnglishToChinese_ReturnsTranslation()
    {
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TranslateAsync_ChineseToEnglish_ReturnsTranslation()
    {
        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
    }
}

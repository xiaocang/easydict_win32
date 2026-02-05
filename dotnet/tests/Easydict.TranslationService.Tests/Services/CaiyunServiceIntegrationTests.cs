using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Integration tests for CaiyunService using real API calls.
/// Requires CAIYUN_API_KEY environment variable to be set.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Service", "caiyun")]
public class CaiyunServiceIntegrationTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly CaiyunService _service;
    private readonly string? _apiKey;

    public CaiyunServiceIntegrationTests()
    {
        _apiKey = Environment.GetEnvironmentVariable("CAIYUN_API_KEY");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _service = new CaiyunService(_httpClient);

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _service.Configure(_apiKey);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [SkippableFact]
    public async Task TranslateAsync_EnglishToChinese_ReturnsChineseTranslation()
    {
        Skip.If(string.IsNullOrEmpty(_apiKey), "CAIYUN_API_KEY not set");

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

    [SkippableFact]
    public async Task TranslateAsync_ChineseToEnglish_ReturnsEnglishTranslation()
    {
        Skip.If(string.IsNullOrEmpty(_apiKey), "CAIYUN_API_KEY not set");

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

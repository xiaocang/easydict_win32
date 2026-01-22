using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Integration tests for DoubaoService using real API calls.
/// Requires DOUBAO_API_KEY environment variable to be set.
/// </summary>
[Trait("Category", "Integration")]
public class DoubaoServiceIntegrationTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly DoubaoService _service;
    private readonly string? _apiKey;

    public DoubaoServiceIntegrationTests()
    {
        _apiKey = Environment.GetEnvironmentVariable("DOUBAO_API_KEY");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _service = new DoubaoService(_httpClient);

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
        Skip.If(string.IsNullOrEmpty(_apiKey), "DOUBAO_API_KEY not set");

        var request = new TranslationRequest
        {
            Text = "Hello, world!",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        // Verify the result contains Chinese characters
        result.TranslatedText.Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "translation should contain Chinese characters");
    }

    [SkippableFact]
    public async Task TranslateAsync_ChineseToEnglish_ReturnsEnglishTranslation()
    {
        Skip.If(string.IsNullOrEmpty(_apiKey), "DOUBAO_API_KEY not set");

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

    [SkippableFact]
    public async Task TranslateStreamAsync_ReturnsStreamingChunks()
    {
        Skip.If(string.IsNullOrEmpty(_apiKey), "DOUBAO_API_KEY not set");

        var request = new TranslationRequest
        {
            Text = "The quick brown fox jumps over the lazy dog.",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var chunks = new List<string>();
        await foreach (var chunk in _service.TranslateStreamAsync(request))
        {
            chunks.Add(chunk);
        }

        chunks.Should().NotBeEmpty("streaming should return chunks");
        var fullText = string.Concat(chunks);
        fullText.Should().NotBeNullOrWhiteSpace();
        // Verify the result contains Chinese characters
        fullText.Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "streamed translation should contain Chinese characters");
    }

    [SkippableFact]
    public async Task TranslateAsync_EnglishToTraditionalChinese_ReturnsTraditionalChineseTranslation()
    {
        Skip.If(string.IsNullOrEmpty(_apiKey), "DOUBAO_API_KEY not set");

        var request = new TranslationRequest
        {
            Text = "Hello, world!",
            FromLanguage = Language.English,
            ToLanguage = Language.TraditionalChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        // Verify the result contains Chinese characters
        result.TranslatedText.Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "translation should contain Traditional Chinese characters");
    }

    [Fact]
    public void Configure_SetsApiKey()
    {
        var service = new DoubaoService(_httpClient);
        var testApiKey = "test-api-key";

        service.Configure(testApiKey);

        service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void ServiceProperties_HaveCorrectValues()
    {
        _service.ServiceId.Should().Be("doubao");
        _service.DisplayName.Should().Be("Doubao");
        _service.RequiresApiKey.Should().BeTrue();
        _service.IsStreaming.Should().BeTrue();
    }

    [Fact]
    public void SupportedLanguages_ContainsTraditionalChinese()
    {
        _service.SupportedLanguages.Should().Contain(Language.TraditionalChinese);
    }
}

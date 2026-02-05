using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Integration tests for GeminiService using real API calls.
/// Requires GEMINI_API_KEY environment variable to be set.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Service", "gemini")]
public class GeminiServiceIntegrationTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly GeminiService _service;
    private readonly string? _apiKey;

    public GeminiServiceIntegrationTests()
    {
        _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _service = new GeminiService(_httpClient);

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
        Skip.If(string.IsNullOrEmpty(_apiKey), "GEMINI_API_KEY not set");

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
        Skip.If(string.IsNullOrEmpty(_apiKey), "GEMINI_API_KEY not set");

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
        Skip.If(string.IsNullOrEmpty(_apiKey), "GEMINI_API_KEY not set");

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
        fullText.Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "streamed translation should contain Chinese characters");
    }
}

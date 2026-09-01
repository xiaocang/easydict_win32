using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Integration tests for OrcaRouterService using real API calls.
/// Requires ORCAROUTER_API_KEY environment variable to be set.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Service", "orcarouter")]
public class OrcaRouterServiceIntegrationTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OrcaRouterService _service;
    private readonly string? _apiKey;

    public OrcaRouterServiceIntegrationTests()
    {
        _apiKey = Environment.GetEnvironmentVariable("ORCAROUTER_API_KEY");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _service = new OrcaRouterService(_httpClient);

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
        Skip.If(string.IsNullOrEmpty(_apiKey), "ORCAROUTER_API_KEY not set");

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
    public async Task TranslateStreamAsync_EnglishToChinese_ReturnsChineseTranslation()
    {
        Skip.If(string.IsNullOrEmpty(_apiKey), "ORCAROUTER_API_KEY not set");

        var request = new TranslationRequest
        {
            Text = "Hello, world!",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var chunks = new List<string>();
        await foreach (var chunk in _service.TranslateStreamAsync(request))
        {
            chunks.Add(chunk);
        }

        chunks.Should().NotBeEmpty("streaming should return chunks");
        string.Concat(chunks).Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "streamed translation should contain Chinese characters");
    }

    [SkippableFact]
    public async Task FetchModelsAsync_ReturnsNonEmptyCatalogWithFreeModelsFirst()
    {
        Skip.If(string.IsNullOrEmpty(_apiKey), "ORCAROUTER_API_KEY not set");

        var models = await _service.FetchModelsAsync();

        models.Should().NotBeEmpty();
        if (models.Any(m => m.IsFree))
        {
            models[0].IsFree.Should().BeTrue();
        }
    }
}

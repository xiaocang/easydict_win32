using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Integration tests for VolcanoService using real API calls.
/// Requires VOLCANO_ACCESS_KEY_ID and VOLCANO_SECRET_ACCESS_KEY environment variables to be set.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Service", "volcano")]
public class VolcanoServiceIntegrationTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly VolcanoService _service;
    private readonly string? _accessKeyId;
    private readonly string? _secretAccessKey;
    private readonly bool _isConfigured;

    public VolcanoServiceIntegrationTests()
    {
        _accessKeyId = Environment.GetEnvironmentVariable("VOLCANO_ACCESS_KEY_ID");
        _secretAccessKey = Environment.GetEnvironmentVariable("VOLCANO_SECRET_ACCESS_KEY");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _service = new VolcanoService(_httpClient);

        _isConfigured = !string.IsNullOrEmpty(_accessKeyId) && !string.IsNullOrEmpty(_secretAccessKey);
        if (_isConfigured)
        {
            _service.Configure(_accessKeyId!, _secretAccessKey!);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [SkippableFact]
    public async Task TranslateAsync_EnglishToChinese_ReturnsChineseTranslation()
    {
        Skip.If(!_isConfigured, "VOLCANO_ACCESS_KEY_ID or VOLCANO_SECRET_ACCESS_KEY not set");

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
        result.ServiceName.Should().Be("Volcano");
    }

    [SkippableFact]
    public async Task TranslateAsync_ChineseToEnglish_ReturnsEnglishTranslation()
    {
        Skip.If(!_isConfigured, "VOLCANO_ACCESS_KEY_ID or VOLCANO_SECRET_ACCESS_KEY not set");

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
    public async Task TranslateAsync_AutoDetect_DetectsSourceLanguage()
    {
        Skip.If(!_isConfigured, "VOLCANO_ACCESS_KEY_ID or VOLCANO_SECRET_ACCESS_KEY not set");

        var request = new TranslationRequest
        {
            Text = "Bonjour le monde",
            FromLanguage = Language.Auto,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        result.DetectedLanguage.Should().Be(Language.French,
            "service should detect French as source language");
    }

    [SkippableFact]
    public async Task TranslateAsync_TraditionalChinese_ReturnsTraditionalChineseTranslation()
    {
        Skip.If(!_isConfigured, "VOLCANO_ACCESS_KEY_ID or VOLCANO_SECRET_ACCESS_KEY not set");

        var request = new TranslationRequest
        {
            Text = "Hello, world!",
            FromLanguage = Language.English,
            ToLanguage = Language.TraditionalChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        // Traditional Chinese characters should be present
        result.TranslatedText.Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "translation should contain Chinese characters");
    }

    [SkippableFact]
    public async Task TranslateAsync_ClassicalChinese_ReturnsClassicalChineseTranslation()
    {
        Skip.If(!_isConfigured, "VOLCANO_ACCESS_KEY_ID or VOLCANO_SECRET_ACCESS_KEY not set");

        var request = new TranslationRequest
        {
            Text = "The world is vast.",
            FromLanguage = Language.English,
            ToLanguage = Language.ClassicalChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        result.TranslatedText.Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "translation should contain Chinese characters");
    }

    [SkippableFact]
    public async Task TranslateAsync_JapaneseToEnglish_ReturnsEnglishTranslation()
    {
        Skip.If(!_isConfigured, "VOLCANO_ACCESS_KEY_ID or VOLCANO_SECRET_ACCESS_KEY not set");

        var request = new TranslationRequest
        {
            Text = "こんにちは",
            FromLanguage = Language.Japanese,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        result.TranslatedText.ToLower().Should().Contain("hello",
            "translation of 'こんにちは' should contain 'hello'");
    }

    [SkippableFact]
    public async Task TranslateAsync_KoreanToChinese_ReturnsChineseTranslation()
    {
        Skip.If(!_isConfigured, "VOLCANO_ACCESS_KEY_ID or VOLCANO_SECRET_ACCESS_KEY not set");

        var request = new TranslationRequest
        {
            Text = "안녕하세요",
            FromLanguage = Language.Korean,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        result.TranslatedText.Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "translation should contain Chinese characters");
    }

    [SkippableFact]
    public async Task TranslateAsync_LongText_SuccessfullyTranslates()
    {
        Skip.If(!_isConfigured, "VOLCANO_ACCESS_KEY_ID or VOLCANO_SECRET_ACCESS_KEY not set");

        var longText = string.Join(" ", Enumerable.Repeat("Hello world.", 50));

        var request = new TranslationRequest
        {
            Text = longText,
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        result.TranslatedText.Length.Should().BeGreaterThan(50,
            "long text should produce substantial translation");
    }
}

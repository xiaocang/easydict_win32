using System.Text.RegularExpressions;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Integration tests for DeepLService using real API calls.
/// Uses web translation by default; optionally uses official API if DEEPL_API_KEY is set.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Service", "deepl")]
public class DeepLServiceIntegrationTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly DeepLService _service;
    private readonly string? _apiKey;

    public DeepLServiceIntegrationTests()
    {
        _apiKey = Environment.GetEnvironmentVariable("DEEPL_API_KEY");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _service = new DeepLService(_httpClient);

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _service.Configure(_apiKey, useWebFirst: false);
        }
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

    [SkippableFact]
    public async Task TranslateAsync_ChineseToEnglish_ReturnsEnglishTranslation()
    {
        var request = new TranslationRequest
        {
            Text = "你好世界",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        TranslationResult result;
        try
        {
            result = await _service.TranslateAsync(request);
        }
        catch (TranslationException exception) when (
            string.IsNullOrEmpty(_apiKey) &&
            exception.ErrorCode == TranslationErrorCode.ServiceUnavailable &&
            exception.Message.Contains("client version is outdated", StringComparison.OrdinalIgnoreCase))
        {
            Skip.If(true, "DeepL's public web endpoint rejected its client version");
            throw;
        }

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        var acceptedTerms = new[] { "hello", "hi", "hey", "greetings", "world" };
        acceptedTerms.Any(term => Regex.IsMatch(
                result.TranslatedText,
                $@"\b{Regex.Escape(term)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Should().BeTrue(
                $"translation of '你好世界' should contain a greeting or 'world'; actual text was '{result.TranslatedText}'");
    }
}

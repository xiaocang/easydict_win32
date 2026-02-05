using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Integration tests for YoudaoService using real API calls.
/// Tests both Web Dictionary (for words with phonetics) and Web Translate (for sentences).
/// No API key required for web APIs.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Service", "youdao")]
public class YoudaoServiceIntegrationTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly YoudaoService _service;

    public YoudaoServiceIntegrationTests()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _service = new YoudaoService(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    #region Web Dictionary Tests (Single Words with Phonetics)

    [Fact]
    public async Task TranslateAsync_EnglishWord_ReturnsUSUKPhonetics()
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
        result.WordResult.Should().NotBeNull("single word should return WordResult");
        result.WordResult!.Phonetics.Should().NotBeNull();
        result.WordResult.Phonetics.Should().HaveCountGreaterOrEqualTo(1,
            "should have at least one phonetic");

        // Check for US or UK phonetic
        var hasUSorUK = result.WordResult.Phonetics!.Any(p => p.Accent == "US" || p.Accent == "UK");
        hasUSorUK.Should().BeTrue("should have US or UK phonetic for English word");

        // Verify phonetic text looks like IPA
        var phonetic = result.WordResult.Phonetics!.First();
        phonetic.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TranslateAsync_EnglishWord_ReturnsDefinitions()
    {
        var request = new TranslationRequest
        {
            Text = "apple",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Definitions.Should().NotBeNull();
        result.WordResult.Definitions.Should().HaveCountGreaterOrEqualTo(1);

        // Should have Chinese definition
        var firstDef = result.WordResult.Definitions!.First();
        firstDef.Meanings.Should().NotBeNullOrEmpty();
        firstDef.Meanings!.First().Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "definition should contain Chinese characters");
    }

    [Fact]
    public async Task TranslateAsync_EnglishWord_ReturnsAudioUrl()
    {
        var request = new TranslationRequest
        {
            Text = "test",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().NotBeNull();

        var phoneticWithAudio = result.WordResult.Phonetics!
            .FirstOrDefault(p => !string.IsNullOrEmpty(p.AudioUrl));
        phoneticWithAudio.Should().NotBeNull("at least one phonetic should have AudioUrl");
        phoneticWithAudio!.AudioUrl.Should().Contain("dict.youdao.com/dictvoice");
    }

    [Fact]
    public async Task TranslateAsync_EnglishPhrase_ReturnsResult()
    {
        var request = new TranslationRequest
        {
            Text = "good morning",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        result.TranslatedText.Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "translation should contain Chinese characters");
    }

    #endregion

    #region Web Translate Tests (Sentences)

    [Fact]
    public async Task TranslateAsync_EnglishSentence_ReturnsChineseTranslation()
    {
        var request = new TranslationRequest
        {
            Text = "This is a test sentence for translation.",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        result.TranslatedText.Should().MatchRegex(@"[\u4e00-\u9fff]+",
            "translation should contain Chinese characters");
        result.WordResult.Should().BeNull("sentences should not have WordResult");
    }

    [Fact]
    public async Task TranslateAsync_ChineseToEnglish_ReturnsEnglishTranslation()
    {
        var request = new TranslationRequest
        {
            Text = "今天天气很好。",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        result.TranslatedText.Should().MatchRegex(@"[a-zA-Z]+",
            "translation should contain English letters");
    }

    [Fact]
    public async Task TranslateAsync_ChineseSentenceWithoutPunctuation_ReturnsRelevantTranslation()
    {
        // This is the bug case: Chinese sentence without punctuation was incorrectly
        // treated as a word and sent to dictionary API, returning unrelated results
        var request = new TranslationRequest
        {
            Text = "今天天气怎么样",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
        result.TranslatedText.Should().MatchRegex(@"[a-zA-Z]+",
            "translation should contain English letters");

        // The translation should be about weather, not some random sentence
        // Accept common variations: weather, today, how
        result.TranslatedText.ToLowerInvariant().Should().ContainAny(
            "weather", "today", "how",
            "translation should be semantically related to the input about weather");
    }

    [Fact]
    public async Task TranslateAsync_MultilineSentence_ReturnsTranslation()
    {
        var request = new TranslationRequest
        {
            Text = "First line.\nSecond line.",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region IsWordQuery Tests

    [Theory]
    [InlineData("hello", true)]
    [InlineData("good morning", true)]
    [InlineData("test-driven", true)]
    [InlineData("don't", true)]
    [InlineData("Hello, world!", false)]
    [InlineData("This is a sentence.", false)]
    [InlineData("Line1\nLine2", false)]
    // Chinese sentences without punctuation should NOT be treated as words
    [InlineData("今天天气怎么样", false)]
    [InlineData("今天天气很好", false)]
    // Chinese sentences with punctuation
    [InlineData("今天天气很好。", false)]
    [InlineData("你好！", false)]
    [InlineData("你好吗？", false)]
    // Single Chinese words should be treated as words
    [InlineData("你好", true)]
    [InlineData("苹果", true)]
    public void IsWordQuery_ReturnsExpectedResult(string text, bool expected)
    {
        YoudaoService.IsWordQuery(text).Should().Be(expected);
    }

    #endregion

    #region PhoneticDisplayHelper Integration Tests

    [Fact]
    public async Task GetTargetPhonetics_YoudaoResult_ReturnsUSUKPhonetics()
    {
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        // Use PhoneticDisplayHelper to filter target phonetics
        var targetPhonetics = PhoneticDisplayHelper.GetTargetPhonetics(result);

        targetPhonetics.Should().NotBeEmpty("Youdao should return US/UK phonetics for English words");
        targetPhonetics.Should().OnlyContain(p => p.Accent == "US" || p.Accent == "UK" || p.Accent == "dest",
            "target phonetics should only include US, UK, or dest accents");
    }

    #endregion

    #region Service Properties Tests

    [Fact]
    public void ServiceId_IsYoudao()
    {
        _service.ServiceId.Should().Be("youdao");
    }

    [Fact]
    public void DisplayName_IsYoudao()
    {
        _service.DisplayName.Should().Be("Youdao");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        _service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_AlwaysTrue()
    {
        _service.IsConfigured.Should().BeTrue();
    }

    #endregion
}

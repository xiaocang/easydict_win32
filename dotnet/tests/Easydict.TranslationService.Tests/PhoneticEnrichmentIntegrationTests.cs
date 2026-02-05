using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests;

/// <summary>
/// Integration tests for phonetic enrichment from Youdao.
/// Tests the TranslationManager.EnrichPhoneticsIfMissingAsync method
/// which automatically fetches phonetics from Youdao when:
/// 1. Target language is English
/// 2. Translated text is a word/phrase (not a sentence)
/// 3. Result lacks target phonetics (US/UK)
/// </summary>
[Trait("Category", "Integration")]
public class PhoneticEnrichmentIntegrationTests : IDisposable
{
    private readonly TranslationManager _manager;

    public PhoneticEnrichmentIntegrationTests()
    {
        _manager = new TranslationManager();
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    #region EnrichPhoneticsIfMissingAsync Tests

    [Fact]
    public async Task EnrichPhoneticsIfMissingAsync_ChineseToEnglish_AddsYoudaoPhonetics()
    {
        // Arrange - Translate Chinese to English, then enrich with phonetics
        // The enrichment looks up the TRANSLATED English text in Youdao
        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var resultWithoutPhonetics = new TranslationResult
        {
            TranslatedText = "hello",
            OriginalText = "你好",
            ServiceName = "TestService",
            TargetLanguage = Language.English,
            DetectedLanguage = Language.SimplifiedChinese
        };

        // Act
        var enrichedResult = await _manager.EnrichPhoneticsIfMissingAsync(
            resultWithoutPhonetics, request);

        // Assert
        enrichedResult.Should().NotBeNull();
        enrichedResult.WordResult.Should().NotBeNull("phonetics should be added from Youdao");
        enrichedResult.WordResult!.Phonetics.Should().NotBeNull();
        enrichedResult.WordResult.Phonetics.Should().HaveCountGreaterOrEqualTo(1);

        // Should have US or UK phonetics from Youdao
        var hasTargetPhonetics = enrichedResult.WordResult.Phonetics!
            .Any(p => p.Accent == "US" || p.Accent == "UK");
        hasTargetPhonetics.Should().BeTrue("Youdao should provide US/UK phonetics for English words");
    }

    [Fact]
    public async Task EnrichPhoneticsIfMissingAsync_ResultAlreadyHasTargetPhonetics_ReturnsUnchanged()
    {
        // Arrange - Create a result that already has target phonetics
        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var existingPhonetic = new Phonetic { Text = "həˈloʊ", Accent = "US" };
        var resultWithPhonetics = new TranslationResult
        {
            TranslatedText = "hello",
            OriginalText = "你好",
            ServiceName = "TestService",
            TargetLanguage = Language.English,
            DetectedLanguage = Language.SimplifiedChinese,
            WordResult = new WordResult
            {
                Phonetics = [existingPhonetic]
            }
        };

        // Act
        var enrichedResult = await _manager.EnrichPhoneticsIfMissingAsync(
            resultWithPhonetics, request);

        // Assert - Should return the same result unchanged
        enrichedResult.WordResult.Should().NotBeNull();
        enrichedResult.WordResult!.Phonetics.Should().HaveCount(1);
        enrichedResult.WordResult.Phonetics![0].Text.Should().Be("həˈloʊ");
    }

    [Fact]
    public async Task EnrichPhoneticsIfMissingAsync_SentenceTranslation_ReturnsUnchanged()
    {
        // Arrange - Sentences should not trigger phonetic enrichment
        var request = new TranslationRequest
        {
            Text = "这是一个完整的句子。",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var resultWithoutPhonetics = new TranslationResult
        {
            TranslatedText = "This is a complete sentence.",
            OriginalText = "这是一个完整的句子。",
            ServiceName = "TestService",
            TargetLanguage = Language.English
        };

        // Act
        var enrichedResult = await _manager.EnrichPhoneticsIfMissingAsync(
            resultWithoutPhonetics, request);

        // Assert - Should not add phonetics for sentences
        enrichedResult.WordResult.Should().BeNull(
            "sentences should not trigger phonetic enrichment");
    }

    [Fact]
    public async Task EnrichPhoneticsIfMissingAsync_TargetNotEnglish_ReturnsUnchanged()
    {
        // Arrange - Enrichment only runs when target is English
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var resultWithoutPhonetics = new TranslationResult
        {
            TranslatedText = "你好",
            OriginalText = "hello",
            ServiceName = "TestService",
            TargetLanguage = Language.SimplifiedChinese,
            DetectedLanguage = Language.English
        };

        // Act
        var enrichedResult = await _manager.EnrichPhoneticsIfMissingAsync(
            resultWithoutPhonetics, request);

        // Assert - Should not add phonetics when target is not English
        enrichedResult.WordResult.Should().BeNull(
            "phonetic enrichment should be skipped when target language is not English");
    }

    [Fact]
    public async Task EnrichPhoneticsIfMissingAsync_OnlySrcPhonetics_AddsTargetPhonetics()
    {
        // Arrange - Create a result with only source phonetics
        // When target is English and only src phonetics exist, enrichment should add US/UK
        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var srcPhonetic = new Phonetic { Text = "nǐ hǎo", Accent = "src" };
        var resultWithOnlySrcPhonetics = new TranslationResult
        {
            TranslatedText = "hello",
            OriginalText = "你好",
            ServiceName = "TestService",
            TargetLanguage = Language.English,
            DetectedLanguage = Language.SimplifiedChinese,
            WordResult = new WordResult
            {
                Phonetics = [srcPhonetic]
            }
        };

        // Act
        var enrichedResult = await _manager.EnrichPhoneticsIfMissingAsync(
            resultWithOnlySrcPhonetics, request);

        // Assert - Should add target phonetics while preserving source phonetics
        enrichedResult.WordResult.Should().NotBeNull();
        enrichedResult.WordResult!.Phonetics.Should().NotBeNull();
        enrichedResult.WordResult.Phonetics!.Count.Should().BeGreaterThan(1,
            "should have both original src and new target phonetics");

        // Should have both src (original) and US/UK (from Youdao)
        enrichedResult.WordResult.Phonetics.Should().Contain(p => p.Accent == "src");
        var hasTargetPhonetics = enrichedResult.WordResult.Phonetics
            .Any(p => p.Accent == "US" || p.Accent == "UK");
        hasTargetPhonetics.Should().BeTrue("should add US/UK phonetics from Youdao");
    }

    #endregion

    #region End-to-End Translation with Phonetic Enrichment

    [Fact]
    public async Task TranslateAsync_ChineseToEnglish_EnrichesPhoneticsFromYoudao()
    {
        // This test verifies the full flow: Google returns translation without phonetics,
        // then TranslationManager enriches it with Youdao phonetics for the English result
        var request = new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        // Translate using Google
        var result = await _manager.TranslateAsync(request, default, "google");

        // Assert
        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();

        // After enrichment, should have US/UK phonetics for the English translation
        var targetPhonetics = PhoneticDisplayHelper.GetTargetPhonetics(result);
        targetPhonetics.Should().NotBeEmpty(
            "Chinese→English translation should be enriched with US/UK phonetics from Youdao");

        // Verify at least one US or UK phonetic
        targetPhonetics.Should().Contain(p => p.Accent == "US" || p.Accent == "UK",
            "Youdao should provide US/UK phonetics for the English translation");
    }

    [Fact]
    public async Task TranslateAsync_YoudaoEnglishToChinese_ReturnsPhonetics()
    {
        // Youdao English→Chinese should return phonetics directly (not via enrichment)
        // because the dict API returns phonetics for the English source word.
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        TranslationResult? result = null;
        try
        {
            result = await _manager.TranslateAsync(request, default, "youdao");
        }
        catch (TranslationException)
        {
            // Skip test if Youdao API is unavailable
            return;
        }

        // Assert
        result.Should().NotBeNull();
        result!.TranslatedText.Should().NotBeNullOrWhiteSpace();

        // Youdao English→Chinese should return phonetics for the English SOURCE word
        // These come directly from Youdao's dict response (not enrichment)
        var allPhonetics = result.WordResult?.Phonetics;
        allPhonetics.Should().NotBeNullOrEmpty(
            "Youdao English→Chinese should return phonetics for the English word");

        // Verify at least one US or UK phonetic
        allPhonetics.Should().Contain(p => p.Accent == "US" || p.Accent == "UK",
            "Youdao should provide US/UK phonetics for English words");
    }

    #endregion
}

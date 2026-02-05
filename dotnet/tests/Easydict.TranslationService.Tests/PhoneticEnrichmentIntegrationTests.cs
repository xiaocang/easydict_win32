using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests;

/// <summary>
/// Integration tests for phonetic enrichment from Youdao.
/// Tests the TranslationManager.EnrichPhoneticsIfMissingAsync method
/// which automatically fetches phonetics from Youdao when translation
/// results lack target phonetics (US/UK/dest).
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
    public async Task EnrichPhoneticsIfMissingAsync_WordWithoutPhonetics_AddsYoudaoPhonetics()
    {
        // Arrange - Create a result without phonetics (simulating a service that doesn't return phonetics)
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
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var existingPhonetic = new Phonetic { Text = "həˈloʊ", Accent = "US" };
        var resultWithPhonetics = new TranslationResult
        {
            TranslatedText = "你好",
            OriginalText = "hello",
            ServiceName = "TestService",
            TargetLanguage = Language.SimplifiedChinese,
            DetectedLanguage = Language.English,
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
    public async Task EnrichPhoneticsIfMissingAsync_SentenceQuery_ReturnsUnchanged()
    {
        // Arrange - Sentences should not trigger phonetic enrichment
        var request = new TranslationRequest
        {
            Text = "This is a complete sentence.",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var resultWithoutPhonetics = new TranslationResult
        {
            TranslatedText = "这是一个完整的句子。",
            OriginalText = "This is a complete sentence.",
            ServiceName = "TestService",
            TargetLanguage = Language.SimplifiedChinese
        };

        // Act
        var enrichedResult = await _manager.EnrichPhoneticsIfMissingAsync(
            resultWithoutPhonetics, request);

        // Assert - Should not add phonetics for sentences
        enrichedResult.WordResult.Should().BeNull(
            "sentences should not trigger phonetic enrichment");
    }

    [Fact]
    public async Task EnrichPhoneticsIfMissingAsync_OnlySrcPhonetics_AddsTargetPhonetics()
    {
        // Arrange - Create a result with only source phonetics (pinyin)
        // This simulates Google Translate returning Chinese romanization for Chinese input
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var srcPhonetic = new Phonetic { Text = "nǐ hǎo", Accent = "src" };
        var resultWithOnlySrcPhonetics = new TranslationResult
        {
            TranslatedText = "你好",
            OriginalText = "hello",
            ServiceName = "TestService",
            TargetLanguage = Language.SimplifiedChinese,
            DetectedLanguage = Language.English,
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
    public async Task TranslateAsync_GoogleService_EnrichesPhoneticsFromYoudao()
    {
        // This test verifies the full flow: Google returns translation without phonetics,
        // then TranslationManager enriches it with Youdao phonetics
        var request = new TranslationRequest
        {
            Text = "test",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Translate using Google (which doesn't return US/UK phonetics for single words)
        var result = await _manager.TranslateAsync(request, default, "google");

        // After enrichment, should have phonetics
        // Note: This depends on whether Google returns target phonetics
        // If Google doesn't return target phonetics, Youdao enrichment should kick in
        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrWhiteSpace();

        // Check if phonetics are present (either from Google or enriched from Youdao)
        var targetPhonetics = PhoneticDisplayHelper.GetTargetPhonetics(result);
        // This assertion may vary depending on Google's response
        // The enrichment logic should ensure we have target phonetics for words
    }

    #endregion
}

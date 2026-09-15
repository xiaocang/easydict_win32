using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Foundation;

/// <summary>
/// Covers which side of a translation the host looks up a pronunciation for.
///
/// Enrichment used to run only when the target language was English, so a service that
/// returns no phonetics of its own (an LLM, DeepL) never gained one in the far more common
/// en→zh direction. The English side is now resolved explicitly: the translation when
/// translating into English, the query when translating out of it.
/// </summary>
public class PhoneticEnrichmentDirectionTests : IDisposable
{
    private readonly TranslationManager _manager = new();
    private readonly List<string> _lookups = [];

    public PhoneticEnrichmentDirectionTests()
    {
        // Stand in for the real Youdao service and record which text was looked up.
        _manager.RegisterService(new PolicyTestService("youdao")
        {
            ResultFactory = r =>
            {
                _lookups.Add(r.Text);
                return new TranslationResult
                {
                    TranslatedText = r.Text,
                    OriginalText = r.Text,
                    ServiceName = "Youdao",
                    WordResult = new WordResult
                    {
                        Phonetics = new[]
                        {
                            new Phonetic { Text = "həˈloʊ", Accent = "US" },
                            new Phonetic { Text = "həˈləʊ", Accent = "UK" }
                        }
                    }
                };
            }
        });
    }

    private static TranslationResult Result(
        string original,
        string translated,
        Language target,
        Language detected = Language.Auto,
        params Phonetic[] phonetics) => new()
    {
        OriginalText = original,
        TranslatedText = translated,
        ServiceName = "source-service",
        TargetLanguage = target,
        DetectedLanguage = detected,
        WordResult = phonetics.Length > 0 ? new WordResult { Phonetics = phonetics } : null
    };

    [Fact]
    public async Task TranslateAsync_EnglishToChineseWord_AddsPronunciation()
    {
        // End-to-end through the manager: a service that returns no phonetics of its own
        // still gets a pronunciation attached for the word the user asked about.
        _manager.RegisterService(new PolicyTestService("gpt"));

        var result = await _manager.TranslateAsync(
            new TranslationRequest
            {
                Text = "hello",
                FromLanguage = Language.English,
                ToLanguage = Language.SimplifiedChinese
            },
            serviceId: "gpt");

        _lookups.Should().Equal("hello");
        result.WordResult!.Phonetics.Should().Contain(p => p.Accent == "US");
    }

    [Fact]
    public async Task EnrichPhonetics_AutoSourceWithDetectedEnglish_UsesQueryText()
    {
        // The UI usually resolves the language before querying, but not every service
        // echoes it back on the request; fall back to what the result detected.
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.Auto,
            ToLanguage = Language.SimplifiedChinese
        };

        var enriched = await _manager.EnrichPhoneticsIfMissingAsync(
            Result("hello", "你好", Language.SimplifiedChinese, Language.English), request);

        _lookups.Should().Equal("hello");
        enriched.WordResult!.Phonetics.Should().HaveCount(2);
    }

    [Fact]
    public async Task EnrichPhonetics_AutoSourceUndetectedEnglishWord_UsesQueryText()
    {
        // Several services hardcode DetectedLanguage to Auto. Falling back to the script of
        // the text keeps those services from silently losing their pronunciation.
        var request = new TranslationRequest
        {
            Text = "serendipity",
            FromLanguage = Language.Auto,
            ToLanguage = Language.SimplifiedChinese
        };

        var enriched = await _manager.EnrichPhoneticsIfMissingAsync(
            Result("serendipity", "机缘巧合", Language.SimplifiedChinese), request);

        _lookups.Should().Equal("serendipity");
        enriched.WordResult!.Phonetics.Should().HaveCount(2);
    }

    [Fact]
    public async Task EnrichPhonetics_AutoSourceUndetectedNonLatinWord_Skips()
    {
        var request = new TranslationRequest
        {
            Text = "Привет",
            FromLanguage = Language.Auto,
            ToLanguage = Language.SimplifiedChinese
        };

        var original = Result("Привет", "你好", Language.SimplifiedChinese);
        var enriched = await _manager.EnrichPhoneticsIfMissingAsync(original, request);

        _lookups.Should().BeEmpty("an English pronunciation lookup is pointless here");
        enriched.Should().BeSameAs(original);
    }

    [Fact]
    public async Task EnrichPhonetics_ResultAlreadyHasPronunciation_Skips()
    {
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var original = Result("hello", "你好", Language.SimplifiedChinese, Language.English,
            new Phonetic { Text = "həˈloʊ", Accent = "US" });

        var enriched = await _manager.EnrichPhoneticsIfMissingAsync(original, request);

        _lookups.Should().BeEmpty("the service already supplied a pronunciation");
        enriched.Should().BeSameAs(original);
    }

    [Fact]
    public async Task EnrichPhonetics_EnglishSentence_Skips()
    {
        var request = new TranslationRequest
        {
            Text = "The quick brown fox jumps over the lazy dog.",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var original = Result(request.Text, "敏捷的棕色狐狸跳过了懒狗。", Language.SimplifiedChinese, Language.English);
        var enriched = await _manager.EnrichPhoneticsIfMissingAsync(original, request);

        _lookups.Should().BeEmpty("phonetics are a dictionary affordance, not a sentence one");
        enriched.Should().BeSameAs(original);
    }

    public void Dispose() => _manager.Dispose();
}

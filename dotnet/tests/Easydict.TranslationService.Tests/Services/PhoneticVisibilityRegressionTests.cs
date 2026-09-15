using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Regression tests for https://github.com/xiaocang/easydict_win32/issues/218.
///
/// Phonetics were parsed correctly by the services but discarded before display: the UI
/// only rendered them when the target language was English, and only for US/UK accents.
/// These tests drive real service payloads all the way to
/// <see cref="PhoneticDisplayHelper.GetDisplayPhonetics"/>, which is what the result card
/// renders, so a regression in either the parsing or the selection half is caught here
/// rather than only in UI automation.
/// </summary>
public class PhoneticVisibilityRegressionTests
{
    private readonly MockHttpMessageHandler _mockHandler = new();
    private readonly HttpClient _httpClient;

    public PhoneticVisibilityRegressionTests()
    {
        _httpClient = new HttpClient(_mockHandler);
    }

    /// <summary>Youdao web-dictionary payload for an English word.</summary>
    private const string YoudaoHelloResponse = """
        {
            "simple": {
                "word": {
                    "usphone": "həˈloʊ",
                    "usspeech": "hello&type=1",
                    "ukphone": "həˈləʊ",
                    "ukspeech": "hello&type=2"
                }
            },
            "ec": {
                "word": {
                    "trs": [ { "pos": "int.", "tran": "喂；你好" } ]
                }
            }
        }
        """;

    [Fact]
    public async Task Youdao_EnglishToChinese_ShowsPronunciation()
    {
        // The bug: looking up an English word for its Chinese meaning returned US/UK
        // phonetics from Youdao, and the UI dropped every one of them because the
        // target language was not English.
        _mockHandler.EnqueueJsonResponse(YoudaoHelloResponse);
        var service = new YoudaoService(_httpClient);

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        var displayed = PhoneticDisplayHelper.GetDisplayPhonetics(result);

        displayed.Should().HaveCount(2, "Youdao supplies both a US and a UK pronunciation");
        displayed.Select(p => p.Accent).Should().Equal("US", "UK");
        displayed.Select(p => p.Text).Should().Equal("həˈloʊ", "həˈləʊ");
    }

    [Fact]
    public async Task Youdao_EnglishToChinese_SpeakerReadsTheEnglishWord()
    {
        // The badge's speaker button used to read TranslatedText aloud with an English
        // voice, which in this direction is the Chinese translation.
        _mockHandler.EnqueueJsonResponse(YoudaoHelloResponse);
        var service = new YoudaoService(_httpClient);

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        PhoneticDisplayHelper.GetEnglishPhoneticSubject(result).Should().Be("hello");
    }

    [Fact]
    public async Task Youdao_ChineseToEnglish_StillShowsPronunciation()
    {
        // The direction that already worked must keep working.
        _mockHandler.EnqueueJsonResponse(YoudaoHelloResponse);
        var service = new YoudaoService(_httpClient);

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        });

        PhoneticDisplayHelper.GetDisplayPhonetics(result)
            .Should().OnlyContain(p => p.Accent == "US" || p.Accent == "UK")
            .And.HaveCount(2);
    }

    [Fact]
    public async Task GoogleTranslate_SourceTransliteration_IsShown()
    {
        // Google supplies romanizations rather than US/UK pronunciations; the UI used to
        // filter for US/UK only, so a Google phonetic could never be displayed at all.
        _mockHandler.EnqueueJsonResponse("""
            {
                "sentences": [
                    {"trans": "Hello", "orig": "你好", "src_translit": "nǐ hǎo"}
                ],
                "src": "zh-CN"
            }
            """);
        var service = new GoogleTranslateService(_httpClient);

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "你好",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        });

        PhoneticDisplayHelper.GetDisplayPhonetics(result)
            .Should().ContainSingle(p => p.Accent == "src" && p.Text == "nǐ hǎo");
    }

    [Fact]
    public async Task GoogleTranslate_TargetTransliteration_IsShown()
    {
        _mockHandler.EnqueueJsonResponse("""
            {
                "sentences": [
                    {"trans": "你好", "orig": "hello"},
                    {"translit": "nǐ hǎo"}
                ],
                "src": "en"
            }
            """);
        var service = new GoogleTranslateService(_httpClient);

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        PhoneticDisplayHelper.GetDisplayPhonetics(result)
            .Should().ContainSingle(p => p.Accent == "dest" && p.Text == "nǐ hǎo");
    }

    [Fact]
    public async Task GoogleTranslate_Sentence_ShowsNothing()
    {
        // Romanizations come back for sentences too; phonetics stay a dictionary affordance.
        _mockHandler.EnqueueJsonResponse("""
            {
                "sentences": [
                    {
                        "trans": "敏捷的棕色狐狸跳过了懒狗。",
                        "orig": "The quick brown fox jumps over the lazy dog.",
                        "translit": "mǐn jié de zōng sè hú lí tiào guò le lǎn gǒu."
                    }
                ],
                "src": "en"
            }
            """);
        var service = new GoogleTranslateService(_httpClient);

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "The quick brown fox jumps over the lazy dog.",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        PhoneticDisplayHelper.GetDisplayPhonetics(result).Should().BeEmpty(
            "a sentence translation is not a dictionary lookup");
    }
}

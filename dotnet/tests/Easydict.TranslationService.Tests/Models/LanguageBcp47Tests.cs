using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Models;

/// <summary>
/// Tests for LanguageExtensions.ToBcp47() mapping used by TTS voice selection.
/// </summary>
public class LanguageBcp47Tests
{
    [Theory]
    [InlineData(Language.English, "en-US")]
    [InlineData(Language.SimplifiedChinese, "zh-CN")]
    [InlineData(Language.TraditionalChinese, "zh-TW")]
    [InlineData(Language.ClassicalChinese, "zh-CN")]
    [InlineData(Language.Japanese, "ja-JP")]
    [InlineData(Language.Korean, "ko-KR")]
    [InlineData(Language.French, "fr-FR")]
    [InlineData(Language.Spanish, "es-ES")]
    [InlineData(Language.Portuguese, "pt-BR")]
    [InlineData(Language.Italian, "it-IT")]
    [InlineData(Language.German, "de-DE")]
    [InlineData(Language.Russian, "ru-RU")]
    [InlineData(Language.Arabic, "ar-SA")]
    [InlineData(Language.Hebrew, "he-IL")]
    [InlineData(Language.Hindi, "hi-IN")]
    [InlineData(Language.Norwegian, "nb-NO")]
    [InlineData(Language.Filipino, "fil-PH")]
    public void ToBcp47_ReturnsExpectedTag(Language language, string expected)
    {
        language.ToBcp47().Should().Be(expected);
    }

    [Fact]
    public void ToBcp47_Auto_FallsBackToEnUS()
    {
        Language.Auto.ToBcp47().Should().Be("en-US");
    }

    [Fact]
    public void ToBcp47_AllLanguages_ReturnNonEmptyTag()
    {
        var allLanguages = Enum.GetValues<Language>();

        foreach (var lang in allLanguages)
        {
            lang.ToBcp47().Should().NotBeNullOrEmpty(
                because: $"{lang} should map to a valid BCP-47 tag");
        }
    }

    [Fact]
    public void ToBcp47_AllLanguages_ContainHyphen()
    {
        // All BCP-47 locale tags should have language-region format
        var allLanguages = Enum.GetValues<Language>();

        foreach (var lang in allLanguages)
        {
            lang.ToBcp47().Should().Contain("-",
                because: $"{lang} BCP-47 tag should be in language-region format");
        }
    }

    [Fact]
    public void ToBcp47_ChineseVariants_AreDifferent()
    {
        var simplified = Language.SimplifiedChinese.ToBcp47();
        var traditional = Language.TraditionalChinese.ToBcp47();

        simplified.Should().NotBe(traditional);
        simplified.Should().StartWith("zh-");
        traditional.Should().StartWith("zh-");
    }

    [Theory]
    [InlineData(Language.Swedish, "sv-SE")]
    [InlineData(Language.Romanian, "ro-RO")]
    [InlineData(Language.Thai, "th-TH")]
    [InlineData(Language.Dutch, "nl-NL")]
    [InlineData(Language.Hungarian, "hu-HU")]
    [InlineData(Language.Greek, "el-GR")]
    [InlineData(Language.Danish, "da-DK")]
    [InlineData(Language.Finnish, "fi-FI")]
    [InlineData(Language.Polish, "pl-PL")]
    [InlineData(Language.Czech, "cs-CZ")]
    [InlineData(Language.Turkish, "tr-TR")]
    [InlineData(Language.Ukrainian, "uk-UA")]
    [InlineData(Language.Bulgarian, "bg-BG")]
    [InlineData(Language.Indonesian, "id-ID")]
    [InlineData(Language.Malay, "ms-MY")]
    [InlineData(Language.Vietnamese, "vi-VN")]
    [InlineData(Language.Persian, "fa-IR")]
    [InlineData(Language.Telugu, "te-IN")]
    [InlineData(Language.Tamil, "ta-IN")]
    [InlineData(Language.Urdu, "ur-PK")]
    [InlineData(Language.Bengali, "bn-IN")]
    [InlineData(Language.Slovak, "sk-SK")]
    [InlineData(Language.Slovenian, "sl-SI")]
    [InlineData(Language.Estonian, "et-EE")]
    [InlineData(Language.Latvian, "lv-LV")]
    [InlineData(Language.Lithuanian, "lt-LT")]
    public void ToBcp47_RemainingLanguages_ReturnCorrectTag(Language language, string expected)
    {
        language.ToBcp47().Should().Be(expected);
    }
}

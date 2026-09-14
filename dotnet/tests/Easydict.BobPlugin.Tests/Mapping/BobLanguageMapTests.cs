using Easydict.BobPlugin.Mapping;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Mapping;

public class BobLanguageMapTests
{
    [Theory]
    [InlineData(Language.Auto, "auto")]
    [InlineData(Language.SimplifiedChinese, "zh-Hans")]
    [InlineData(Language.TraditionalChinese, "zh-Hant")]
    [InlineData(Language.English, "en")]
    [InlineData(Language.Japanese, "ja")]
    [InlineData(Language.Korean, "ko")]
    [InlineData(Language.German, "de")]
    public void ToBobCode_UsesBobSpellings(Language language, string expected)
        => BobLanguageMap.ToBobCode(language).Should().Be(expected);

    [Theory]
    [InlineData("zh-Hans", Language.SimplifiedChinese)]
    [InlineData("zh-hans", Language.SimplifiedChinese)]
    [InlineData("zh-CN", Language.SimplifiedChinese)]
    [InlineData("zh-TW", Language.TraditionalChinese)]
    [InlineData("yue", Language.TraditionalChinese)]
    [InlineData("nb", Language.Norwegian)]
    [InlineData("fil", Language.Filipino)]
    [InlineData("pt-BR", Language.Portuguese)]
    [InlineData("en", Language.English)]
    [InlineData("auto", Language.Auto)]
    public void FromBobCode_AcceptsTheSpellingsPluginsUse(string code, Language expected)
        => BobLanguageMap.FromBobCode(code).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-language")]
    public void FromBobCode_ReturnsNullForWhatItCannotPlace(string? code)
        => BobLanguageMap.FromBobCode(code).Should().BeNull();

    [Fact]
    public void EveryEmittedCodeParsesBack()
    {
        foreach (var language in Enum.GetValues<Language>())
        {
            var code = BobLanguageMap.ToBobCode(language);
            if (code is null)
            {
                continue;
            }

            BobLanguageMap.FromBobCode(code).Should().Be(language, $"'{code}' is what we emit for {language}");
        }
    }

    [Theory]
    [InlineData("hello world", "en")]
    [InlineData("", "en")]
    [InlineData("   ", "en")]
    [InlineData("你好世界", "zh-Hans")]
    [InlineData("こんにちは", "ja")]
    [InlineData("안녕하세요", "ko")]
    [InlineData("Здравствуйте", "ru")]
    [InlineData("مرحبا", "ar")]
    [InlineData("שלום", "he")]
    [InlineData("สวัสดี", "th")]
    [InlineData("नमस्ते", "hi")]
    [InlineData("Γειά σου", "el")]
    public void GuessByScript_PicksAConcreteCode(string text, string expected)
        => BobLanguageMap.GuessByScript(text).Should().Be(expected);

    [Theory]
    // Japanese routinely opens with kanji, so only looking at the first character reads as Chinese.
    [InlineData("日本語のテキスト", "ja")]
    [InlineData("東京", "zh-Hans")]
    [InlineData("漢字とかな", "ja")]
    [InlineData("한국어 漢字", "ko")]
    public void GuessByScript_LooksAtTheWholeString(string text, string expected)
        => BobLanguageMap.GuessByScript(text).Should().Be(expected);

    [Fact]
    public void GuessByScript_IsNotFlippedByAStrayForeignCharacter()
        => BobLanguageMap.GuessByScript("这是一段很长的中文文本 α").Should().Be("zh-Hans");
}

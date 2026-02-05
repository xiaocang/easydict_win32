using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Models;

/// <summary>
/// Tests for PhoneticDisplayHelper covering accent labels, text formatting, and phonetic extraction.
/// </summary>
public class PhoneticDisplayHelperTests
{
    #region GetAccentDisplayLabel Tests

    [Theory]
    [InlineData("US", "美")]
    [InlineData("UK", "英")]
    [InlineData("src", "原")]
    [InlineData("dest", "译")]
    public void GetAccentDisplayLabel_KnownAccents_ReturnsMappedLabel(string accent, string expected)
    {
        PhoneticDisplayHelper.GetAccentDisplayLabel(accent).Should().Be(expected);
    }

    [Fact]
    public void GetAccentDisplayLabel_NullAccent_ReturnsNull()
    {
        PhoneticDisplayHelper.GetAccentDisplayLabel(null).Should().BeNull();
    }

    [Fact]
    public void GetAccentDisplayLabel_EmptyAccent_ReturnsNull()
    {
        PhoneticDisplayHelper.GetAccentDisplayLabel("").Should().BeNull();
    }

    [Theory]
    [InlineData("AU")]
    [InlineData("custom")]
    [InlineData("pinyin")]
    public void GetAccentDisplayLabel_UnknownAccent_ReturnsAccentAsIs(string accent)
    {
        PhoneticDisplayHelper.GetAccentDisplayLabel(accent).Should().Be(accent);
    }

    #endregion

    #region FormatPhoneticText Tests

    [Fact]
    public void FormatPhoneticText_PlainText_WrapsInSlashes()
    {
        PhoneticDisplayHelper.FormatPhoneticText("həˈloʊ").Should().Be("/həˈloʊ/");
    }

    [Fact]
    public void FormatPhoneticText_AlreadyWrapped_ReturnsAsIs()
    {
        PhoneticDisplayHelper.FormatPhoneticText("/həˈloʊ/").Should().Be("/həˈloʊ/");
    }

    [Fact]
    public void FormatPhoneticText_Romanization_WrapsInSlashes()
    {
        PhoneticDisplayHelper.FormatPhoneticText("nǐ hǎo").Should().Be("/nǐ hǎo/");
    }

    [Fact]
    public void FormatPhoneticText_OnlyLeadingSlash_WrapsAgain()
    {
        // Only one slash at start, not a complete wrap
        PhoneticDisplayHelper.FormatPhoneticText("/hello").Should().Be("//hello/");
    }

    #endregion

    #region GetDisplayablePhonetics Tests

    [Fact]
    public void GetDisplayablePhonetics_NullResult_ReturnsEmpty()
    {
        PhoneticDisplayHelper.GetDisplayablePhonetics(null).Should().BeEmpty();
    }

    [Fact]
    public void GetDisplayablePhonetics_NoWordResult_ReturnsEmpty()
    {
        var result = new TranslationResult
        {
            TranslatedText = "Hello",
            OriginalText = "你好",
            ServiceName = "Google"
        };

        PhoneticDisplayHelper.GetDisplayablePhonetics(result).Should().BeEmpty();
    }

    [Fact]
    public void GetDisplayablePhonetics_EmptyPhonetics_ReturnsEmpty()
    {
        var result = new TranslationResult
        {
            TranslatedText = "Hello",
            OriginalText = "你好",
            ServiceName = "Google",
            WordResult = new WordResult { Phonetics = [] }
        };

        PhoneticDisplayHelper.GetDisplayablePhonetics(result).Should().BeEmpty();
    }

    [Fact]
    public void GetDisplayablePhonetics_WithPhonetics_ReturnsNonEmpty()
    {
        var result = new TranslationResult
        {
            TranslatedText = "Hello",
            OriginalText = "你好",
            ServiceName = "Google",
            WordResult = new WordResult
            {
                Phonetics =
                [
                    new Phonetic { Text = "nǐ hǎo", Accent = "src" }
                ]
            }
        };

        var phonetics = PhoneticDisplayHelper.GetDisplayablePhonetics(result);
        phonetics.Should().HaveCount(1);
        phonetics[0].Text.Should().Be("nǐ hǎo");
        phonetics[0].Accent.Should().Be("src");
    }

    [Fact]
    public void GetDisplayablePhonetics_FiltersNullTextPhonetics()
    {
        var result = new TranslationResult
        {
            TranslatedText = "Hello",
            OriginalText = "你好",
            ServiceName = "Google",
            WordResult = new WordResult
            {
                Phonetics =
                [
                    new Phonetic { Text = null, Accent = "src" },
                    new Phonetic { Text = "nǐ hǎo", Accent = "src" },
                    new Phonetic { Text = "", Accent = "dest" }
                ]
            }
        };

        var phonetics = PhoneticDisplayHelper.GetDisplayablePhonetics(result);
        phonetics.Should().HaveCount(1);
        phonetics[0].Text.Should().Be("nǐ hǎo");
    }

    [Fact]
    public void GetDisplayablePhonetics_MultiplePhoneticsPreserved()
    {
        var result = new TranslationResult
        {
            TranslatedText = "你好",
            OriginalText = "hello",
            ServiceName = "Google",
            WordResult = new WordResult
            {
                Phonetics =
                [
                    new Phonetic { Text = "hello", Accent = "src" },
                    new Phonetic { Text = "nǐ hǎo", Accent = "dest" }
                ]
            }
        };

        var phonetics = PhoneticDisplayHelper.GetDisplayablePhonetics(result);
        phonetics.Should().HaveCount(2);
        phonetics[0].Accent.Should().Be("src");
        phonetics[1].Accent.Should().Be("dest");
    }

    [Fact]
    public void GetDisplayablePhonetics_USUKAccents()
    {
        var result = new TranslationResult
        {
            TranslatedText = "你好",
            OriginalText = "hello",
            ServiceName = "Google Dict",
            WordResult = new WordResult
            {
                Phonetics =
                [
                    new Phonetic { Text = "heˈloʊ", Accent = "US" },
                    new Phonetic { Text = "heˈləʊ", Accent = "UK" }
                ]
            }
        };

        var phonetics = PhoneticDisplayHelper.GetDisplayablePhonetics(result);
        phonetics.Should().HaveCount(2);
        phonetics[0].Accent.Should().Be("US");
        phonetics[1].Accent.Should().Be("UK");
    }

    #endregion

    #region GetTargetPhonetics Tests

    [Fact]
    public void GetTargetPhonetics_NullResult_ReturnsEmpty()
    {
        PhoneticDisplayHelper.GetTargetPhonetics(null).Should().BeEmpty();
    }

    [Fact]
    public void GetTargetPhonetics_NoWordResult_ReturnsEmpty()
    {
        var result = new TranslationResult
        {
            TranslatedText = "Hello",
            OriginalText = "你好",
            ServiceName = "Google"
        };

        PhoneticDisplayHelper.GetTargetPhonetics(result).Should().BeEmpty();
    }

    [Fact]
    public void GetTargetPhonetics_OnlySourcePhonetic_ReturnsEmpty()
    {
        var result = new TranslationResult
        {
            TranslatedText = "Hello",
            OriginalText = "你好",
            ServiceName = "Google",
            WordResult = new WordResult
            {
                Phonetics =
                [
                    new Phonetic { Text = "nǐ hǎo", Accent = "src" }
                ]
            }
        };

        PhoneticDisplayHelper.GetTargetPhonetics(result).Should().BeEmpty();
    }

    [Fact]
    public void GetTargetPhonetics_DestPhonetic_ReturnsIt()
    {
        var result = new TranslationResult
        {
            TranslatedText = "你好",
            OriginalText = "hello",
            ServiceName = "Google",
            WordResult = new WordResult
            {
                Phonetics =
                [
                    new Phonetic { Text = "nǐ hǎo", Accent = "dest" }
                ]
            }
        };

        var phonetics = PhoneticDisplayHelper.GetTargetPhonetics(result);
        phonetics.Should().HaveCount(1);
        phonetics[0].Text.Should().Be("nǐ hǎo");
        phonetics[0].Accent.Should().Be("dest");
    }

    [Fact]
    public void GetTargetPhonetics_USUKAccents_ReturnsAll()
    {
        var result = new TranslationResult
        {
            TranslatedText = "你好",
            OriginalText = "hello",
            ServiceName = "Google Dict",
            WordResult = new WordResult
            {
                Phonetics =
                [
                    new Phonetic { Text = "heˈloʊ", Accent = "US" },
                    new Phonetic { Text = "heˈləʊ", Accent = "UK" }
                ]
            }
        };

        var phonetics = PhoneticDisplayHelper.GetTargetPhonetics(result);
        phonetics.Should().HaveCount(2);
        phonetics[0].Accent.Should().Be("US");
        phonetics[1].Accent.Should().Be("UK");
    }

    [Fact]
    public void GetTargetPhonetics_MixedSourceAndTarget_ReturnsOnlyTarget()
    {
        var result = new TranslationResult
        {
            TranslatedText = "Hello",
            OriginalText = "你好",
            ServiceName = "Google",
            WordResult = new WordResult
            {
                Phonetics =
                [
                    new Phonetic { Text = "nǐ hǎo", Accent = "src" },
                    new Phonetic { Text = "heˈloʊ", Accent = "US" },
                    new Phonetic { Text = "heˈləʊ", Accent = "UK" }
                ]
            }
        };

        var phonetics = PhoneticDisplayHelper.GetTargetPhonetics(result);
        phonetics.Should().HaveCount(2);
        phonetics[0].Accent.Should().Be("US");
        phonetics[1].Accent.Should().Be("UK");
    }

    [Fact]
    public void GetTargetPhonetics_FiltersEmptyAndNullText()
    {
        var result = new TranslationResult
        {
            TranslatedText = "Hello",
            OriginalText = "你好",
            ServiceName = "Google",
            WordResult = new WordResult
            {
                Phonetics =
                [
                    new Phonetic { Text = null, Accent = "dest" },
                    new Phonetic { Text = "", Accent = "US" },
                    new Phonetic { Text = "heˈloʊ", Accent = "US" }
                ]
            }
        };

        var phonetics = PhoneticDisplayHelper.GetTargetPhonetics(result);
        phonetics.Should().HaveCount(1);
        phonetics[0].Text.Should().Be("heˈloʊ");
    }

    #endregion
}

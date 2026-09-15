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

    #region GetDisplayPhonetics Tests

    private static TranslationResult Lookup(
        string original,
        string translated,
        Language target,
        Language detected,
        params Phonetic[] phonetics) => new()
    {
        OriginalText = original,
        TranslatedText = translated,
        ServiceName = "test",
        TargetLanguage = target,
        DetectedLanguage = detected,
        WordResult = phonetics.Length > 0 ? new WordResult { Phonetics = phonetics } : null
    };

    [Fact]
    public void GetDisplayPhonetics_EnglishToChinese_ShowsPronunciation()
    {
        // Looking an English word up for its Chinese meaning is the most common dictionary
        // use; the pronunciation belongs to the word the user typed.
        var result = Lookup("hello", "你好", Language.SimplifiedChinese, Language.English,
            new Phonetic { Text = "həˈloʊ", Accent = "US" },
            new Phonetic { Text = "həˈləʊ", Accent = "UK" });

        PhoneticDisplayHelper.GetDisplayPhonetics(result)
            .Should().HaveCount(2)
            .And.OnlyContain(p => p.Accent == "US" || p.Accent == "UK");
    }

    [Fact]
    public void GetDisplayPhonetics_ChineseToEnglish_ShowsPronunciation()
    {
        var result = Lookup("你好", "hello", Language.English, Language.SimplifiedChinese,
            new Phonetic { Text = "həˈloʊ", Accent = "US" });

        PhoneticDisplayHelper.GetDisplayPhonetics(result).Should().ContainSingle();
    }

    [Fact]
    public void GetDisplayPhonetics_NeitherSideEnglish_DropsPronunciation()
    {
        var result = Lookup("你好", "こんにちは", Language.Japanese, Language.SimplifiedChinese,
            new Phonetic { Text = "həˈloʊ", Accent = "US" });

        PhoneticDisplayHelper.GetDisplayPhonetics(result).Should().BeEmpty(
            "a US pronunciation is meaningless when no side of the result is English");
    }

    [Theory]
    [InlineData("コンピューター", "computer")]   // Japanese katakana, 7 characters
    [InlineData("안녕하세요", "hello")]           // Korean hangul, 5 characters
    [InlineData("一石二鸟", "kill two birds")]   // four-character Chinese idiom
    public void GetDisplayPhonetics_LongerCjkWord_ShowsPronunciation(string original, string translated)
    {
        // The CJK word heuristic only accepts 1-3 characters, so gating display on the query
        // alone would discard the pronunciation of a perfectly ordinary CJK dictionary word
        // whose English translation the host had already looked up.
        var result = Lookup(original, translated, Language.English, Language.Auto,
            new Phonetic { Text = "kəmˈpjuːtər", Accent = "US" });

        PhoneticDisplayHelper.GetDisplayPhonetics(result).Should().ContainSingle(
            p => p.Accent == "US",
            "the English side is a word even though the query is a longer CJK string");
    }

    [Fact]
    public void GetDisplayPhonetics_GlossTranslation_ShowsPronunciation()
    {
        // Youdao's dictionary translation is a gloss ("int. 喂；你好"), never a bare word, so
        // gating display on the translation alone would hide every Youdao pronunciation.
        var result = Lookup("hello", "int. 喂；你好", Language.SimplifiedChinese, Language.English,
            new Phonetic { Text = "həˈloʊ", Accent = "US" });

        PhoneticDisplayHelper.GetDisplayPhonetics(result).Should().ContainSingle(p => p.Accent == "US");
    }

    [Fact]
    public void GetDisplayPhonetics_Sentence_ReturnsEmpty()
    {
        var result = Lookup(
            "The quick brown fox jumps over the lazy dog.",
            "敏捷的棕色狐狸跳过了懒狗。",
            Language.SimplifiedChinese,
            Language.English,
            new Phonetic { Text = "həˈloʊ", Accent = "US" });

        PhoneticDisplayHelper.GetDisplayPhonetics(result).Should().BeEmpty(
            "phonetics are a dictionary affordance, not a sentence-translation one");
    }

    [Fact]
    public void GetDisplayPhonetics_IncludesRomanizations()
    {
        // Google supplies transliterations rather than US/UK pronunciations.
        var result = Lookup("你好", "hello", Language.English, Language.SimplifiedChinese,
            new Phonetic { Text = "nǐ hǎo", Accent = "src" });

        PhoneticDisplayHelper.GetDisplayPhonetics(result)
            .Should().ContainSingle(p => p.Accent == "src" && p.Text == "nǐ hǎo");
    }

    [Fact]
    public void GetDisplayPhonetics_OrdersPronunciationBeforeRomanization()
    {
        var result = Lookup("你好", "hello", Language.English, Language.SimplifiedChinese,
            new Phonetic { Text = "nǐ hǎo", Accent = "src" },
            new Phonetic { Text = "həˈləʊ", Accent = "UK" },
            new Phonetic { Text = "həˈloʊ", Accent = "US" });

        PhoneticDisplayHelper.GetDisplayPhonetics(result)
            .Select(p => p.Accent)
            .Should().Equal("US", "UK", "src");
    }

    [Fact]
    public void GetDisplayPhonetics_SkipsUnknownAccentsAndEmptyText()
    {
        var result = Lookup("hello", "你好", Language.SimplifiedChinese, Language.English,
            new Phonetic { Text = "", Accent = "US" },
            new Phonetic { Text = "whatever", Accent = "mystery" });

        PhoneticDisplayHelper.GetDisplayPhonetics(result).Should().BeEmpty();
    }

    #endregion

    #region GetEnglishPhoneticSubject Tests

    [Fact]
    public void GetEnglishPhoneticSubject_TargetEnglish_ReturnsTranslation()
    {
        var result = Lookup("你好", "hello", Language.English, Language.SimplifiedChinese);
        PhoneticDisplayHelper.GetEnglishPhoneticSubject(result).Should().Be("hello");
    }

    [Fact]
    public void GetEnglishPhoneticSubject_SourceEnglish_ReturnsOriginal()
    {
        var result = Lookup("hello", "你好", Language.SimplifiedChinese, Language.English);
        PhoneticDisplayHelper.GetEnglishPhoneticSubject(result).Should().Be("hello");
    }

    [Fact]
    public void GetEnglishPhoneticSubject_UndetectedLatinWord_ReturnsOriginal()
    {
        var result = Lookup("hello", "你好", Language.SimplifiedChinese, Language.Auto);
        PhoneticDisplayHelper.GetEnglishPhoneticSubject(result).Should().Be("hello");
    }

    [Fact]
    public void GetEnglishPhoneticSubject_NeitherSideEnglish_ReturnsNull()
    {
        var result = Lookup("你好", "こんにちは", Language.Japanese, Language.SimplifiedChinese);
        PhoneticDisplayHelper.GetEnglishPhoneticSubject(result).Should().BeNull();
    }

    #endregion

    #region GetEnglishPhonetics Tests

    [Fact]
    public void GetEnglishPhonetics_IgnoresRomanizations()
    {
        var result = Lookup("你好", "hello", Language.English, Language.SimplifiedChinese,
            new Phonetic { Text = "nǐ hǎo", Accent = "src" },
            new Phonetic { Text = "nǐ hǎo", Accent = "dest" });

        PhoneticDisplayHelper.GetEnglishPhonetics(result).Should().BeEmpty(
            "a romanization is not a pronunciation guide and must not suppress enrichment");
    }

    [Fact]
    public void GetEnglishPhonetics_ReturnsUsAndUk()
    {
        var result = Lookup("你好", "hello", Language.English, Language.SimplifiedChinese,
            new Phonetic { Text = "həˈloʊ", Accent = "US" },
            new Phonetic { Text = "nǐ hǎo", Accent = "src" });

        PhoneticDisplayHelper.GetEnglishPhonetics(result)
            .Should().ContainSingle(p => p.Accent == "US");
    }

    #endregion
}

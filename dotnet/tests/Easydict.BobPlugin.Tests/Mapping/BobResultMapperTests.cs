using Easydict.BobPlugin.Mapping;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Mapping;

public class BobResultMapperTests
{
    private static TranslationRequest NewRequest(string text = "word", string? originalText = null) => new()
    {
        Text = text,
        FromLanguage = Language.English,
        ToLanguage = Language.SimplifiedChinese,
        OriginalText = originalText
    };

    private static readonly TranslationRequest Request = NewRequest();

    [Fact]
    public void Map_JoinsParagraphsWithNewlines()
    {
        var result = BobResultMapper.Map(
            new BobResult { ToParagraphs = ["first", "second"] },
            Request,
            "Example");

        result.TranslatedText.Should().Be("first\nsecond");
        result.ResultKind.Should().Be(TranslationResultKind.Success);
        result.ServiceName.Should().Be("Example");
    }

    [Fact]
    public void Map_NeverProducesRawHtml()
    {
        var result = BobResultMapper.Map(
            new BobResult { ToParagraphs = ["<b>not markup</b><script>alert(1)</script>"] },
            Request,
            "Example");

        result.RawHtml.Should().BeNull("a plugin's plain string must not gain HTML rendering privileges");
        result.TranslatedText.Should().Contain("<script>");
    }

    [Fact]
    public void Map_UsesTheLanguagesThePluginReported()
    {
        var result = BobResultMapper.Map(
            new BobResult { From = "ja", To = "en", ToParagraphs = ["x"] },
            Request,
            "Example");

        result.DetectedLanguage.Should().Be(Language.Japanese);
        result.TargetLanguage.Should().Be(Language.English);
    }

    [Fact]
    public void Map_FallsBackToTheRequestForUnknownLanguageCodes()
    {
        var result = BobResultMapper.Map(
            new BobResult { From = "klingon", To = null, ToParagraphs = ["x"] },
            Request,
            "Example");

        result.DetectedLanguage.Should().Be(Language.English);
        result.TargetLanguage.Should().Be(Language.SimplifiedChinese);
    }

    [Fact]
    public void Map_KeepsOriginalTextWhenTheRequestCarriesOne()
    {
        var request = NewRequest("processed", originalText: "  original  ");

        var result = BobResultMapper.Map(new BobResult { ToParagraphs = ["x"] }, request, "Example");

        result.OriginalText.Should().Be("  original  ");
    }

    [Fact]
    public void Map_ReportsNoResultForAnEmptyPayload()
    {
        var result = BobResultMapper.Map(new BobResult(), Request, "Example");

        result.ResultKind.Should().Be(TranslationResultKind.NoResult);
        result.TranslatedText.Should().BeEmpty();
        result.WordResult.Should().BeNull();
    }

    [Fact]
    public void Map_TranslatesTheWholeDictionary()
    {
        var result = BobResultMapper.Map(FullDictionary(), Request, "Example");

        result.WordResult.Should().NotBeNull();
        var word = result.WordResult!;

        word.Phonetics.Should().HaveCount(2);
        word.Phonetics![0].Accent.Should().Be("US");
        word.Phonetics[0].Text.Should().Be("/wərd/");
        word.Phonetics[0].AudioUrl.Should().Be("https://example.invalid/us.mp3");
        word.Phonetics[1].Accent.Should().Be("UK");
        word.Phonetics[1].AudioUrl.Should().BeNull("only url-typed tts is usable directly");

        word.Definitions.Should().HaveCount(3, "two parts plus one addition");
        word.Definitions![0].PartOfSpeech.Should().Be("n.");
        word.Definitions[0].Meanings.Should().Equal("词", "单词");
        word.Definitions[2].PartOfSpeech.Should().Be("Note");
        word.Definitions[2].Meanings.Should().Equal("An addition.");

        word.WordForms.Should().HaveCount(1);
        word.WordForms![0].Name.Should().Be("复数");
        word.WordForms[0].Value.Should().Be("words, wordes");

        word.Synonyms.Should().HaveCount(1);
        word.Synonyms![0].PartOfSpeech.Should().Be("n.");
        word.Synonyms[0].Words.Should().Equal("term (术语)", "vocable");
    }

    [Fact]
    public void Map_UnknownPhoneticTypesBecomeTheSourceAccent()
    {
        var result = BobResultMapper.Map(
            new BobResult
            {
                ToParagraphs = ["x"],
                ToDict = new BobDict { Phonetics = [new BobPhonetic { Type = "romaji", Value = "kotoba" }] }
            },
            Request,
            "Example");

        result.WordResult!.Phonetics![0].Accent.Should().Be("src");
    }

    [Fact]
    public void Map_DescribesADictionaryWhenThereAreNoParagraphs()
    {
        var result = BobResultMapper.Map(
            new BobResult
            {
                ToDict = new BobDict { Parts = [new BobPart { Part = "n.", Means = ["词", "单词"] }] }
            },
            Request,
            "Example");

        result.ResultKind.Should().Be(TranslationResultKind.Success);
        result.TranslatedText.Should().Be("n. 词; 单词");
    }

    [Fact]
    public void Map_DropsEmptySections()
    {
        var result = BobResultMapper.Map(
            new BobResult
            {
                ToParagraphs = ["x"],
                ToDict = new BobDict
                {
                    Phonetics = [new BobPhonetic { Type = "us", Value = "  " }],
                    Parts = [new BobPart { Part = "n.", Means = [] }],
                    Exchanges = [new BobExchange { Name = "plural", Words = [] }],
                    RelatedWordParts = [new BobRelatedWordPart { Part = "n.", Words = [] }],
                    Additions = [new BobAddition { Name = "Note", Value = "  " }]
                }
            },
            Request,
            "Example");

        result.WordResult.Should().BeNull("a dictionary whose every section is empty adds nothing");
    }

    private static BobResult FullDictionary() => new()
    {
        From = "en",
        To = "zh-Hans",
        ToParagraphs = ["单词"],
        ToDict = new BobDict
        {
            Word = "word",
            Phonetics =
            [
                new BobPhonetic { Type = "us", Value = "/wərd/", Tts = new BobTts { Type = "url", Value = "https://example.invalid/us.mp3" } },
                new BobPhonetic { Type = "uk", Value = "/wɜːd/", Tts = new BobTts { Type = "base64", Value = "AAAA" } }
            ],
            Parts =
            [
                new BobPart { Part = "n.", Means = ["词", "单词"] },
                new BobPart { Part = "v.", Means = ["措辞"] }
            ],
            Exchanges = [new BobExchange { Name = "复数", Words = ["words", "wordes"] }],
            RelatedWordParts =
            [
                new BobRelatedWordPart
                {
                    Part = "n.",
                    Words =
                    [
                        new BobRelatedWord { Word = "term", Means = ["术语"] },
                        new BobRelatedWord { Word = "vocable" }
                    ]
                }
            ],
            Additions = [new BobAddition { Name = "Note", Value = "An addition." }]
        }
    };
}

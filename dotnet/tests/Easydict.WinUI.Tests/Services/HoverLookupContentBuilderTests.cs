using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for HoverLookupContentBuilder (pure formatting of a TranslationResult for the popup).
/// </summary>
public class HoverLookupContentBuilderTests
{
    private static TranslationResult Result(
        string translated,
        WordResult? wordResult = null,
        IReadOnlyList<string>? alternatives = null,
        TranslationResultKind kind = TranslationResultKind.Success,
        string? infoMessage = null) =>
        new()
        {
            TranslatedText = translated,
            OriginalText = "hello",
            ServiceName = "Test Service",
            WordResult = wordResult,
            Alternatives = alternatives,
            ResultKind = kind,
            InfoMessage = infoMessage,
        };

    [Fact]
    public void Build_PlainTranslation_UsesTranslatedTextAndServiceName()
    {
        var content = HoverLookupContentBuilder.Build("hello", Result("你好"));

        content.Word.Should().Be("hello");
        content.Phonetics.Should().BeNull();
        content.Body.Should().Be("你好");
        content.ServiceName.Should().Be("Test Service");
    }

    [Fact]
    public void Build_Definitions_AreFormattedOnePerPartOfSpeech()
    {
        var wordResult = new WordResult
        {
            Definitions =
            [
                new Definition { PartOfSpeech = "int.", Meanings = ["喂", "你好"] },
                new Definition { PartOfSpeech = "n.", Meanings = ["表示问候"] },
                new Definition { PartOfSpeech = "v.", Meanings = [] },          // skipped
                new Definition { PartOfSpeech = null, Meanings = ["打招呼"] },  // no POS prefix
            ]
        };

        var body = HoverLookupContentBuilder.FormatDefinitions(wordResult);

        body.Should().Be(string.Join(Environment.NewLine, "int. 喂; 你好", "n. 表示问候", "打招呼"));
    }

    [Fact]
    public void FormatDefinitions_CapsLinesAndMeanings()
    {
        var definitions = Enumerable.Range(0, HoverLookupContentBuilder.MaxDefinitionLines + 3)
            .Select(i => new Definition
            {
                PartOfSpeech = $"pos{i}",
                Meanings = Enumerable.Range(0, HoverLookupContentBuilder.MaxMeaningsPerDefinition + 2).Select(j => $"m{j}").ToList()
            })
            .ToList();

        var body = HoverLookupContentBuilder.FormatDefinitions(new WordResult { Definitions = definitions });
        var lines = body.Split(Environment.NewLine);

        lines.Should().HaveCount(HoverLookupContentBuilder.MaxDefinitionLines);
        lines[0].Split("; ").Should().HaveCount(HoverLookupContentBuilder.MaxMeaningsPerDefinition);
    }

    [Fact]
    public void FormatBody_RedundantTranslatedText_IsSuppressedInFavorOfDefinitions()
    {
        // Youdao style: TranslatedText is a flattened rendering of the definitions.
        var wordResult = new WordResult
        {
            Definitions =
            [
                new Definition { PartOfSpeech = "int.", Meanings = ["喂", "你好"] },
                new Definition { PartOfSpeech = "n.", Meanings = ["表示问候"] },
            ]
        };
        var result = Result("int. 喂；你好\nn. 表示问候", wordResult);

        var body = HoverLookupContentBuilder.FormatBody(result, null);

        body.Should().Be(string.Join(Environment.NewLine, "int. 喂; 你好", "n. 表示问候"));
    }

    [Fact]
    public void FormatBody_IndependentTranslatedText_IsShownBeforeDefinitions()
    {
        // GoogleWeb style: plain translation plus definitions.
        var wordResult = new WordResult
        {
            Definitions = [new Definition { PartOfSpeech = "noun", Meanings = ["greeting"] }]
        };
        var result = Result("你好", wordResult);

        var body = HoverLookupContentBuilder.FormatBody(result, null);

        body.Should().Be(string.Join(Environment.NewLine, "你好", "noun greeting"));
    }

    [Fact]
    public void FormatBody_Alternatives_AreAppendedDedupedAndCapped()
    {
        var result = Result("你好", alternatives: ["你好", "哈喽", " 喂 ", "哈喽", "嗨", "您好"]);

        var body = HoverLookupContentBuilder.FormatBody(result, null);

        body.Should().Be(string.Join(Environment.NewLine, "你好", "哈喽; 喂; 嗨"));
    }

    [Fact]
    public void FormatBody_NoResult_UsesInfoMessageThenFallback()
    {
        HoverLookupContentBuilder.FormatBody(Result("", kind: TranslationResultKind.NoResult, infoMessage: "Nothing found"), "No result")
            .Should().Be("Nothing found");
        HoverLookupContentBuilder.FormatBody(Result("", kind: TranslationResultKind.NoResult), "No result")
            .Should().Be("No result");
        HoverLookupContentBuilder.FormatBody(Result("   "), "No result")
            .Should().Be("No result");
        HoverLookupContentBuilder.FormatBody(Result("   "), null)
            .Should().BeEmpty();
    }

    [Fact]
    public void FormatPhonetics_PrefersUsUk_FormatsWithSlashes_AndCapsAtTwo()
    {
        var result = Result("你好", new WordResult
        {
            Phonetics =
            [
                new Phonetic { Text = "hɛˈləʊ", Accent = "UK" },
                new Phonetic { Text = "/həˈloʊ/", Accent = "US" },
                new Phonetic { Text = "xxx", Accent = "dest" },
                new Phonetic { Text = "", Accent = "US" },
            ]
        });

        HoverLookupContentBuilder.FormatPhonetics(result).Should().Be("美 /həˈloʊ/  英 /hɛˈləʊ/");
    }

    [Fact]
    public void FormatPhonetics_DedupesIdenticalText_AndHandlesUnknownAccent()
    {
        var result = Result("你好", new WordResult
        {
            Phonetics =
            [
                new Phonetic { Text = "həˈloʊ", Accent = "US" },
                new Phonetic { Text = "/həˈloʊ/", Accent = "UK" },
                new Phonetic { Text = "hello", Accent = null },
            ]
        });

        HoverLookupContentBuilder.FormatPhonetics(result).Should().Be("美 /həˈloʊ/  /hello/");
    }

    [Fact]
    public void FormatPhonetics_NoPhonetics_ReturnsNull()
    {
        HoverLookupContentBuilder.FormatPhonetics(Result("你好")).Should().BeNull();
        HoverLookupContentBuilder.FormatPhonetics(Result("你好", new WordResult { Phonetics = [] })).Should().BeNull();
    }
}

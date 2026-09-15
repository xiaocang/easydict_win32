using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Foundation;

/// <summary>
/// Regression test: host phonetic enrichment used to rebuild <see cref="WordResult"/> and silently drop
/// <see cref="WordResult.WordForms"/> and <see cref="WordResult.Synonyms"/>.
/// </summary>
public class PhoneticEnrichmentPreservesWordResultTests : IDisposable
{
    private readonly TranslationManager _manager = new();

    [Fact]
    public async Task EnrichPhoneticsIfMissingAsync_KeepsWordFormsAndSynonyms()
    {
        _manager.RegisterService(new PolicyTestService("youdao")
        {
            ResultFactory = r => new TranslationResult
            {
                TranslatedText = "跑",
                OriginalText = r.Text,
                ServiceName = "Youdao",
                WordResult = new WordResult
                {
                    Phonetics = new[] { new Phonetic { Text = "rʌn", Accent = "US" }, new Phonetic { Text = "rʌn", Accent = "UK" } }
                }
            }
        });

        var original = new TranslationResult
        {
            TranslatedText = "run",
            OriginalText = "跑",
            ServiceName = "Plugin",
            TargetLanguage = Language.English,
            WordResult = new WordResult
            {
                Phonetics = new[] { new Phonetic { Text = "pǎo", Accent = "src" } },
                Definitions = new[] { new Definition { PartOfSpeech = "v.", Meanings = new[] { "to move fast" } } },
                Examples = new[] { "I run every day." },
                WordForms = new[] { new WordForm { Name = "past", Value = "ran" }, new WordForm { Name = "pp", Value = "run" } },
                Synonyms = new[] { new Synonym { PartOfSpeech = "v.", Meaning = "move fast", Words = new[] { "sprint", "dash" } } }
            }
        };
        var request = new TranslationRequest { Text = "跑", FromLanguage = Language.SimplifiedChinese, ToLanguage = Language.English };

        var enriched = await _manager.EnrichPhoneticsIfMissingAsync(original, request);

        enriched.WordResult.Should().NotBeNull();
        enriched.WordResult!.Phonetics.Should().HaveCount(3, "existing src phonetic plus US and UK from Youdao");
        enriched.WordResult.Definitions.Should().BeEquivalentTo(original.WordResult!.Definitions);
        enriched.WordResult.Examples.Should().BeEquivalentTo(original.WordResult.Examples);
        enriched.WordResult.WordForms.Should().BeEquivalentTo(original.WordResult.WordForms);
        enriched.WordResult.Synonyms.Should().BeEquivalentTo(original.WordResult.Synonyms);
    }

    [Fact]
    public void WithPhonetics_CopiesEveryField()
    {
        var source = new WordResult
        {
            Definitions = new[] { new Definition { PartOfSpeech = "n." } },
            Examples = new[] { "ex" },
            WordForms = new[] { new WordForm { Name = "pl", Value = "cats" } },
            Synonyms = new[] { new Synonym { Words = new[] { "kitty" } } }
        };
        var phonetics = new[] { new Phonetic { Text = "kæt", Accent = "US" } };

        var copy = source.WithPhonetics(phonetics);

        copy.Phonetics.Should().BeSameAs(phonetics);
        copy.Definitions.Should().BeSameAs(source.Definitions);
        copy.Examples.Should().BeSameAs(source.Examples);
        copy.WordForms.Should().BeSameAs(source.WordForms);
        copy.Synonyms.Should().BeSameAs(source.Synonyms);
        ((WordResult?)null).WithPhonetics(phonetics).Phonetics.Should().BeSameAs(phonetics);
    }

    public void Dispose() => _manager.Dispose();
}

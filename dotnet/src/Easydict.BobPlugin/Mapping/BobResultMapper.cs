using Easydict.TranslationService.Models;

namespace Easydict.BobPlugin.Mapping;

/// <summary>
/// Converts a plugin result into Easydict's <see cref="TranslationResult"/>.
/// Plain plugin text is never promoted to <see cref="TranslationResult.RawHtml"/>: a plugin
/// string must not gain HTML rendering privileges it did not ask for.
/// </summary>
public static class BobResultMapper
{
    /// <summary>Map a plugin result for the given request.</summary>
    public static TranslationResult Map(BobResult result, TranslationRequest request, string serviceDisplayName)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);

        var wordResult = MapDictionary(result.ToDict);
        var text = JoinParagraphs(result.ToParagraphs) ?? DescribeDictionary(result.ToDict) ?? string.Empty;
        var hasContent = !string.IsNullOrWhiteSpace(text) || wordResult is not null;

        return new TranslationResult
        {
            TranslatedText = text,
            OriginalText = request.OriginalText ?? request.Text,
            DetectedLanguage = BobLanguageMap.FromBobCode(result.From) ?? request.FromLanguage,
            TargetLanguage = BobLanguageMap.FromBobCode(result.To) ?? request.ToLanguage,
            ServiceName = serviceDisplayName,
            ResultKind = hasContent ? TranslationResultKind.Success : TranslationResultKind.NoResult,
            WordResult = wordResult
        };
    }

    /// <summary>Text of a result, or <c>null</c> when the plugin returned no paragraphs.</summary>
    private static string? JoinParagraphs(List<string>? paragraphs)
    {
        if (paragraphs is null || paragraphs.Count == 0)
        {
            return null;
        }

        var joined = string.Join("\n", paragraphs.Where(p => p is not null));
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    /// <summary>
    /// Fallback plain text for dictionary-only results, so the row is never blank when a plugin
    /// returns definitions without paragraphs.
    /// </summary>
    private static string? DescribeDictionary(BobDict? dict)
    {
        if (dict?.Parts is null || dict.Parts.Count == 0)
        {
            return null;
        }

        var lines = dict.Parts
            .Where(p => p.Means is { Count: > 0 })
            .Select(p => string.IsNullOrWhiteSpace(p.Part)
                ? string.Join("; ", p.Means!)
                : $"{p.Part} {string.Join("; ", p.Means!)}")
            .ToList();

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static WordResult? MapDictionary(BobDict? dict)
    {
        if (dict is null)
        {
            return null;
        }

        var phonetics = MapPhonetics(dict.Phonetics);
        var definitions = MapDefinitions(dict.Parts, dict.Additions);
        var wordForms = MapWordForms(dict.Exchanges);
        var synonyms = MapSynonyms(dict.RelatedWordParts);

        if (phonetics is null && definitions is null && wordForms is null && synonyms is null)
        {
            return null;
        }

        return new WordResult
        {
            Phonetics = phonetics,
            Definitions = definitions,
            WordForms = wordForms,
            Synonyms = synonyms
        };
    }

    private static IReadOnlyList<Phonetic>? MapPhonetics(List<BobPhonetic>? phonetics)
    {
        if (phonetics is null || phonetics.Count == 0)
        {
            return null;
        }

        var mapped = phonetics
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => new Phonetic
            {
                Text = p.Value,
                Accent = MapAccent(p.Type),
                // Only http(s) audio is usable directly; base64 payloads are ignored for now.
                AudioUrl = string.Equals(p.Tts?.Type, "url", StringComparison.OrdinalIgnoreCase) ? p.Tts!.Value : null
            })
            .ToList();

        return mapped.Count == 0 ? null : mapped;
    }

    /// <summary>Bob's pronunciation types map onto the accents Easydict's UI knows.</summary>
    private static string MapAccent(string? type) => type?.ToLowerInvariant() switch
    {
        "us" => "US",
        "uk" => "UK",
        _ => "src"
    };

    private static IReadOnlyList<Definition>? MapDefinitions(List<BobPart>? parts, List<BobAddition>? additions)
    {
        var definitions = new List<Definition>();

        foreach (var part in parts ?? [])
        {
            if (part.Means is not { Count: > 0 })
            {
                continue;
            }

            definitions.Add(new Definition
            {
                PartOfSpeech = part.Part,
                Meanings = part.Means.Where(m => !string.IsNullOrWhiteSpace(m)).ToList()
            });
        }

        // Additions are free-form sections; showing them as definitions keeps the plugin's extra
        // content visible without inventing a new rendering path.
        foreach (var addition in additions ?? [])
        {
            if (string.IsNullOrWhiteSpace(addition.Value))
            {
                continue;
            }

            definitions.Add(new Definition
            {
                PartOfSpeech = addition.Name,
                Meanings = [addition.Value]
            });
        }

        return definitions.Count == 0 ? null : definitions;
    }

    private static IReadOnlyList<WordForm>? MapWordForms(List<BobExchange>? exchanges)
    {
        if (exchanges is null || exchanges.Count == 0)
        {
            return null;
        }

        var forms = exchanges
            .Where(e => e.Words is { Count: > 0 })
            .Select(e => new WordForm
            {
                Name = e.Name,
                Value = string.Join(", ", e.Words!.Where(w => !string.IsNullOrWhiteSpace(w)))
            })
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .ToList();

        return forms.Count == 0 ? null : forms;
    }

    private static IReadOnlyList<Synonym>? MapSynonyms(List<BobRelatedWordPart>? relatedWordParts)
    {
        if (relatedWordParts is null || relatedWordParts.Count == 0)
        {
            return null;
        }

        var synonyms = relatedWordParts
            .Where(p => p.Words is { Count: > 0 })
            .Select(p => new Synonym
            {
                PartOfSpeech = p.Part,
                Words = p.Words!
                    .Where(w => !string.IsNullOrWhiteSpace(w.Word))
                    .Select(w => w.Means is { Count: > 0 }
                        ? $"{w.Word} ({string.Join("; ", w.Means)})"
                        : w.Word!)
                    .ToList()
            })
            .Where(s => s.Words is { Count: > 0 })
            .ToList();

        return synonyms.Count == 0 ? null : synonyms;
    }
}

using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Models;

/// <summary>
/// What the hover lookup popup displays for one word.
/// </summary>
public sealed record HoverLookupContent(string Word, string? Phonetics, string Body, string ServiceName);

/// <summary>
/// Turns a <see cref="TranslationResult"/> into compact popup text. Pure logic, unit-testable.
/// </summary>
public static class HoverLookupContentBuilder
{
    public const int MaxDefinitionLines = 6;
    public const int MaxMeaningsPerDefinition = 4;
    public const int MaxAlternatives = 3;
    public const int MaxPhonetics = 2;

    /// <summary>
    /// Build the popup content for <paramref name="word"/> from a service result.
    /// </summary>
    /// <param name="word">The looked-up word (as shown in the header).</param>
    /// <param name="result">The service result.</param>
    /// <param name="noResultText">Localized fallback body when the result carries nothing to show.</param>
    public static HoverLookupContent Build(string word, TranslationResult result, string? noResultText = null)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(result);

        return new HoverLookupContent(
            word,
            FormatPhonetics(result),
            FormatBody(result, noResultText),
            result.ServiceName ?? string.Empty);
    }

    /// <summary>
    /// Body text: plain translation (unless it merely repeats the definitions, Youdao-style),
    /// then definitions as "pos meaning; meaning" lines, then up to a few alternatives.
    /// </summary>
    internal static string FormatBody(TranslationResult result, string? noResultText)
    {
        if (result.ResultKind == TranslationResultKind.NoResult)
        {
            return FirstNonEmpty(result.InfoMessage, noResultText);
        }

        var parts = new List<string>();
        var translated = result.TranslatedText?.Trim() ?? string.Empty;
        var definitions = FormatDefinitions(result.WordResult);
        var hasDefinitions = definitions.Length > 0;

        if (translated.Length > 0 &&
            (!hasDefinitions || !DictionaryDisplayHelper.IsTranslatedTextRedundantWithDefinitions(result)))
        {
            parts.Add(translated);
        }

        if (hasDefinitions)
        {
            parts.Add(definitions);
        }

        if (result.Alternatives is { Count: > 0 } alternatives)
        {
            var extra = alternatives
                .Where(alternative => !string.IsNullOrWhiteSpace(alternative))
                .Select(alternative => alternative.Trim())
                .Where(alternative => !string.Equals(alternative, translated, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxAlternatives)
                .ToList();
            if (extra.Count > 0)
            {
                parts.Add(string.Join("; ", extra));
            }
        }

        return parts.Count == 0
            ? noResultText?.Trim() ?? string.Empty
            : string.Join(Environment.NewLine, parts);
    }

    /// <summary>
    /// Definitions as one line per part of speech: "n. meaning; meaning".
    /// </summary>
    internal static string FormatDefinitions(WordResult? wordResult)
    {
        var definitions = wordResult?.Definitions;
        if (definitions is null || definitions.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var definition in definitions)
        {
            var meanings = definition.Meanings?
                .Where(meaning => !string.IsNullOrWhiteSpace(meaning))
                .Select(meaning => meaning.Trim())
                .Take(MaxMeaningsPerDefinition)
                .ToList();
            if (meanings is null || meanings.Count == 0)
            {
                continue;
            }

            var joined = string.Join("; ", meanings);
            var partOfSpeech = definition.PartOfSpeech?.Trim();
            lines.Add(string.IsNullOrEmpty(partOfSpeech) ? joined : $"{partOfSpeech} {joined}");

            if (lines.Count >= MaxDefinitionLines)
            {
                break;
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Up to two phonetics, US/UK first, e.g. "美 /həˈloʊ/  英 /hɛˈləʊ/". Null when none.
    /// </summary>
    internal static string? FormatPhonetics(TranslationResult result)
    {
        var phonetics = PhoneticDisplayHelper.GetDisplayablePhonetics(result);
        if (phonetics.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phonetic in phonetics.OrderBy(p => AccentRank(p.Accent)))
        {
            var text = PhoneticDisplayHelper.FormatPhoneticText(phonetic.Text!.Trim());
            if (!seen.Add(text))
            {
                continue;
            }

            var label = PhoneticDisplayHelper.GetAccentDisplayLabel(phonetic.Accent);
            parts.Add(string.IsNullOrEmpty(label) ? text : $"{label} {text}");
            if (parts.Count >= MaxPhonetics)
            {
                break;
            }
        }

        return parts.Count == 0 ? null : string.Join("  ", parts);
    }

    private static int AccentRank(string? accent) => accent switch
    {
        "US" => 0,
        "UK" => 1,
        "src" => 2,
        "dest" => 3,
        _ => 4,
    };

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}

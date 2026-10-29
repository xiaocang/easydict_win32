using System.Net;
using System.Text.RegularExpressions;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services.AnkiConnect;

/// <summary>Expand a note field template, escaping lookup text as Anki HTML.</summary>
public static class AnkiFieldTemplate
{
    public static string Expand(string template, TranslationResult result)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(result);
        var definitions = result.WordResult?.Definitions?.Select(d =>
            string.Join(" ", new[] { d.PartOfSpeech, string.Join("; ", d.Meanings ?? Array.Empty<string>()) }.Where(s => !string.IsNullOrWhiteSpace(s))));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{Word}"] = result.OriginalText,
            ["{Translation}"] = result.TranslatedText,
            ["{Phonetic}"] = string.Join(" ", result.WordResult?.Phonetics?.Select(p => p.Text).Where(s => !string.IsNullOrWhiteSpace(s)) ?? Array.Empty<string?>()),
            ["{Definition}"] = string.Join("\n", definitions ?? Array.Empty<string>())
        };
        return Regex.Replace(template, @"\{(?:Word|Phonetic|Definition|Translation)\}", match =>
            WebUtility.HtmlEncode(values[match.Value] ?? "").Replace("\r\n", "\n").Replace("\n", "<br>"),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Models;

/// <summary>
/// Pure decision rules for hover word lookup (service choice, source-language guess).
/// Kept free of WinUI/Win32 dependencies so they are unit-testable.
/// </summary>
public static class HoverLookupRules
{
    /// <summary>
    /// Services that return dictionary data (phonetics, definitions), tried first in Auto mode.
    /// </summary>
    public static readonly IReadOnlyList<string> PreferredDictionaryServiceIds = ["youdao", "google_web"];

    /// <summary>Prefix of locally imported MDX dictionary service ids.</summary>
    public const string MdxServiceIdPrefix = "mdx::";

    /// <summary>
    /// Whether this candidate can answer without touching the network, so a dead proxy cannot
    /// stop it.
    /// </summary>
    /// <remarks>
    /// Sound in one direction only: an id this returns false for may still bypass the proxy — a
    /// loopback Ollama, a local CLI service — because no service declares its routing. That is
    /// why callers use it to reorder candidates and never to drop them.
    /// </remarks>
    public static bool IsNetworkFree(string? serviceId) =>
        !string.IsNullOrEmpty(serviceId)
        && serviceId.StartsWith(MdxServiceIdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Order translation services for the popup, with the configured service first.
    /// </summary>
    /// <param name="configured">The configured service id ("" or null = Auto).</param>
    /// <param name="enabledInOrder">Enabled service ids in the user's display order.</param>
    /// <param name="isUsable">Whether a registered service can currently be used (configured, region-available).</param>
    /// <param name="isRegistered">Whether a service id is registered with the translation manager.</param>
    /// <returns>Usable service ids in fallback order, without duplicates.</returns>
    public static IReadOnlyList<string> SelectServiceIds(
        string? configured,
        IReadOnlyList<string> enabledInOrder,
        Func<string, bool> isUsable,
        Func<string, bool> isRegistered)
    {
        ArgumentNullException.ThrowIfNull(enabledInOrder);
        ArgumentNullException.ThrowIfNull(isUsable);
        ArgumentNullException.ThrowIfNull(isRegistered);

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id) && isRegistered(id) && isUsable(id))
            {
                candidates.Add(id);
            }
        }

        Add(configured?.Trim());

        // Auto: dictionary-capable services, enabled MDX dictionaries, then remaining enabled services.
        foreach (var preferred in PreferredDictionaryServiceIds)
        {
            var enabled = enabledInOrder.FirstOrDefault(id => string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase));
            Add(enabled);
        }

        foreach (var id in enabledInOrder.Where(id => id.StartsWith(MdxServiceIdPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            Add(id);
        }

        foreach (var id in enabledInOrder)
        {
            Add(id);
        }

        return candidates;
    }

    /// <summary>
    /// Cheap script-based guess of a single word's language, used only to pick the
    /// first/second target language (the service itself still auto-detects the source).
    /// </summary>
    public static Language GuessSourceLanguage(
        string word,
        Language firstLanguage = Language.SimplifiedChinese,
        Language secondLanguage = Language.English)
    {
        if (string.IsNullOrEmpty(word))
        {
            return Language.English;
        }

        var hasHan = false;
        var script = "Latin";
        foreach (var c in word)
        {
            if (c >= '\u3040' && c <= '\u30FF')
            {
                return Language.Japanese; // Hiragana / Katakana
            }

            if (c >= '\uAC00' && c <= '\uD7AF')
            {
                return Language.Korean; // Hangul syllables
            }

            if ((c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3400' && c <= '\u4DBF') || (c >= '\uF900' && c <= '\uFAFF'))
            {
                hasHan = true;
            }

            if (c is >= '\u0400' and <= '\u052F') script = "Cyrillic";
            else if (c is >= '\u0600' and <= '\u06FF') script = "Arabic";
            else if (c is >= '\u0370' and <= '\u03FF') script = "Greek";
            else if (c is >= '\u0590' and <= '\u05FF') script = "Hebrew";
            else if (c is >= '\u0900' and <= '\u097F') script = "Devanagari";
            else if (c is >= '\u0980' and <= '\u09FF') script = "Bengali";
            else if (c is >= '\u0B80' and <= '\u0BFF') script = "Tamil";
            else if (c is >= '\u0C00' and <= '\u0C7F') script = "Telugu";
            else if (c is >= '\u0E00' and <= '\u0E7F') script = "Thai";
        }

        if (hasHan) script = "Han";
        // A script cannot distinguish Haus from an English word, or Russian from
        // Ukrainian. Prefer a compatible language in the user's pair, first then second.
        return FromPreferences(language => GetScript(language) == script, script switch
        {
            "Han" => Language.SimplifiedChinese,
            "Cyrillic" => Language.Russian,
            "Arabic" => Language.Arabic,
            "Greek" => Language.Greek,
            "Hebrew" => Language.Hebrew,
            "Devanagari" => Language.Hindi,
            "Bengali" => Language.Bengali,
            "Tamil" => Language.Tamil,
            "Telugu" => Language.Telugu,
            "Thai" => Language.Thai,
            _ => Language.English,
        });

        Language FromPreferences(Func<Language, bool> matches, Language fallback)
        {
            if (firstLanguage != Language.Auto && matches(firstLanguage)) return firstLanguage;
            if (secondLanguage != Language.Auto && matches(secondLanguage)) return secondLanguage;
            return fallback;
        }
    }

    private static string GetScript(Language language) => language switch
    {
        Language.Auto => "Unknown",
        Language.SimplifiedChinese or Language.TraditionalChinese or Language.ClassicalChinese or Language.Japanese => "Han",
        Language.Korean => "Hangul",
        Language.Russian or Language.Ukrainian or Language.Bulgarian => "Cyrillic",
        Language.Arabic or Language.Persian or Language.Urdu => "Arabic",
        Language.Greek => "Greek",
        Language.Hebrew => "Hebrew",
        Language.Hindi => "Devanagari",
        Language.Bengali => "Bengali",
        Language.Tamil => "Tamil",
        Language.Telugu => "Telugu",
        Language.Thai => "Thai",
        _ => "Latin",
    };
}

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
    public static Language GuessSourceLanguage(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return Language.English;
        }

        var hasHan = false;
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
        }

        return hasHan ? Language.SimplifiedChinese : Language.English;
    }
}

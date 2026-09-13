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
    /// Pick the translation service that powers the popup.
    /// </summary>
    /// <param name="configured">The configured service id ("" or null = Auto).</param>
    /// <param name="enabledInOrder">Enabled service ids in the user's display order.</param>
    /// <param name="isUsable">Whether a registered service can currently be used (configured, region-available).</param>
    /// <param name="isRegistered">Whether a service id is registered with the translation manager.</param>
    /// <returns>A service id, or null when nothing usable is enabled.</returns>
    public static string? SelectServiceId(
        string? configured,
        IReadOnlyList<string> enabledInOrder,
        Func<string, bool> isUsable,
        Func<string, bool> isRegistered)
    {
        ArgumentNullException.ThrowIfNull(enabledInOrder);
        ArgumentNullException.ThrowIfNull(isUsable);
        ArgumentNullException.ThrowIfNull(isRegistered);

        var explicitId = configured?.Trim();
        if (!string.IsNullOrEmpty(explicitId) && isRegistered(explicitId) && isUsable(explicitId))
        {
            return explicitId;
        }

        // Auto: dictionary-capable services first, then any enabled MDX dictionary, then the first usable one.
        foreach (var preferred in PreferredDictionaryServiceIds)
        {
            var enabled = enabledInOrder.FirstOrDefault(id => string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase));
            if (enabled is not null && isRegistered(enabled) && isUsable(enabled))
            {
                return enabled;
            }
        }

        var mdx = enabledInOrder.FirstOrDefault(id =>
            id.StartsWith(MdxServiceIdPrefix, StringComparison.OrdinalIgnoreCase) && isRegistered(id) && isUsable(id));
        if (mdx is not null)
        {
            return mdx;
        }

        return enabledInOrder.FirstOrDefault(id => isRegistered(id) && isUsable(id));
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

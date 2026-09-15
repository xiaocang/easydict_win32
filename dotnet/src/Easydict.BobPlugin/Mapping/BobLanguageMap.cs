using Easydict.TranslationService.Models;

namespace Easydict.BobPlugin.Mapping;

/// <summary>
/// Translates between Easydict's <see cref="Language"/> and the language codes Bob plugins use.
/// Codes must be verified against https://bobtranslate.com/plugin as the reference list evolves.
/// </summary>
public static class BobLanguageMap
{
    /// <summary>Code Bob uses for "detect automatically".</summary>
    public const string AutoCode = "auto";

    private static readonly Dictionary<Language, string> ToBob = new()
    {
        [Language.Auto] = AutoCode,
        [Language.SimplifiedChinese] = "zh-Hans",
        [Language.TraditionalChinese] = "zh-Hant",
        [Language.ClassicalChinese] = "wyw",
        [Language.English] = "en",
        [Language.Japanese] = "ja",
        [Language.Korean] = "ko",
        [Language.French] = "fr",
        [Language.Spanish] = "es",
        [Language.Portuguese] = "pt",
        [Language.Italian] = "it",
        [Language.German] = "de",
        [Language.Russian] = "ru",
        [Language.Arabic] = "ar",
        [Language.Swedish] = "sv",
        [Language.Dutch] = "nl",
        [Language.Polish] = "pl",
        [Language.Turkish] = "tr",
        [Language.Thai] = "th",
        [Language.Vietnamese] = "vi",
        [Language.Indonesian] = "id",
        [Language.Malay] = "ms",
        [Language.Filipino] = "tl",
        [Language.Hindi] = "hi",
        [Language.Bengali] = "bn",
        [Language.Tamil] = "ta",
        [Language.Telugu] = "te",
        [Language.Urdu] = "ur",
        [Language.Persian] = "fa",
        [Language.Hebrew] = "he",
        [Language.Greek] = "el",
        [Language.Hungarian] = "hu",
        [Language.Finnish] = "fi",
        [Language.Danish] = "da",
        [Language.Norwegian] = "no",
        [Language.Czech] = "cs",
        [Language.Ukrainian] = "uk",
        [Language.Bulgarian] = "bg",
        [Language.Slovak] = "sk",
        [Language.Slovenian] = "sl",
        [Language.Estonian] = "et",
        [Language.Latvian] = "lv",
        [Language.Lithuanian] = "lt",
        [Language.Romanian] = "ro"
    };

    // Accepts spellings plugins use on input that are not what we emit.
    private static readonly Dictionary<string, Language> FromBobExtras = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh-CN"] = Language.SimplifiedChinese,
        ["zh-TW"] = Language.TraditionalChinese,
        ["zh-HK"] = Language.TraditionalChinese,
        ["yue"] = Language.TraditionalChinese,
        ["nb"] = Language.Norwegian,
        ["nn"] = Language.Norwegian,
        ["fil"] = Language.Filipino,
        ["pt-BR"] = Language.Portuguese,
        ["pt-PT"] = Language.Portuguese,
        ["iw"] = Language.Hebrew,
        ["in"] = Language.Indonesian
    };

    private static readonly Dictionary<string, Language> FromBob = BuildReverseMap();

    /// <summary>The Bob code for a language, or <c>null</c> when the language has no mapping.</summary>
    public static string? ToBobCode(Language language)
        => ToBob.TryGetValue(language, out var code) ? code : null;

    /// <summary>The language for a Bob code, or <c>null</c> when it is unknown.</summary>
    public static Language? FromBobCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        if (FromBob.TryGetValue(trimmed, out var language) || FromBobExtras.TryGetValue(trimmed, out language))
        {
            return language;
        }

        // Fall back to Easydict's own parser, which returns Auto for anything it cannot place.
        var parsed = LanguageExtensions.FromCode(trimmed);
        return parsed == Language.Auto && !string.Equals(trimmed, AutoCode, StringComparison.OrdinalIgnoreCase)
            ? null
            : parsed;
    }

    /// <summary>
    /// Best-effort source code for a plugin's <c>detectFrom</c> when the host has not detected a
    /// language. Plugins generally expect a concrete code, and a wrong guess is better than "auto"
    /// for the ones that reject it outright.
    ///
    /// The whole string is examined rather than its first character: Japanese text routinely opens
    /// with kanji ("日本語のテキスト"), which on its own is indistinguishable from Chinese.
    /// </summary>
    public static string GuessByScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "en";
        }

        var kana = 0;
        var hangul = 0;
        var han = 0;
        var cyrillic = 0;
        var arabic = 0;
        var hebrew = 0;
        var thai = 0;
        var devanagari = 0;
        var greek = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case >= 0x3040 and <= 0x30FF: kana++; break;
                case >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF: hangul++; break;
                case >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF: han++; break;
                case >= 0x0400 and <= 0x04FF: cyrillic++; break;
                case >= 0x0600 and <= 0x06FF: arabic++; break;
                case >= 0x0590 and <= 0x05FF: hebrew++; break;
                case >= 0x0E00 and <= 0x0E7F: thai++; break;
                case >= 0x0900 and <= 0x097F: devanagari++; break;
                case >= 0x0370 and <= 0x03FF: greek++; break;
            }
        }

        // Kana and hangul settle the question outright: neither occurs in Chinese, so even one of
        // them outweighs any amount of Han.
        if (kana > 0) return "ja";
        if (hangul > 0) return "ko";

        // Otherwise the most-used script wins, so a stray foreign character cannot flip the guess.
        var best = "en";
        var bestCount = 0;
        Consider("zh-Hans", han);
        Consider("ru", cyrillic);
        Consider("ar", arabic);
        Consider("he", hebrew);
        Consider("th", thai);
        Consider("hi", devanagari);
        Consider("el", greek);
        return best;

        void Consider(string code, int count)
        {
            if (count > bestCount)
            {
                best = code;
                bestCount = count;
            }
        }
    }

    private static Dictionary<string, Language> BuildReverseMap()
    {
        var map = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        foreach (var (language, code) in ToBob)
        {
            map[code] = language;
        }

        return map;
    }
}

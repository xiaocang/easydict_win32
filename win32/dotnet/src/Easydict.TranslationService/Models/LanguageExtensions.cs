namespace Easydict.TranslationService.Models;

/// <summary>
/// Extension methods for Language enum to provide display names and simplified code mappings.
/// </summary>
public static class LanguageExtensions
{
    /// <summary>
    /// Get user-friendly display name for a language.
    /// </summary>
    public static string GetDisplayName(this Language lang) => lang switch
    {
        Language.Auto => "Auto Detect",

        // East Asian
        Language.SimplifiedChinese => "Chinese (Simplified)",
        Language.TraditionalChinese => "Chinese (Traditional)",
        Language.ClassicalChinese => "Classical Chinese",
        Language.Japanese => "Japanese",
        Language.Korean => "Korean",

        // European - Germanic
        Language.English => "English",
        Language.German => "German",
        Language.Dutch => "Dutch",
        Language.Swedish => "Swedish",
        Language.Norwegian => "Norwegian",
        Language.Danish => "Danish",

        // European - Romance
        Language.French => "French",
        Language.Spanish => "Spanish",
        Language.Portuguese => "Portuguese",
        Language.Italian => "Italian",
        Language.Romanian => "Romanian",

        // European - Slavic
        Language.Russian => "Russian",
        Language.Polish => "Polish",
        Language.Czech => "Czech",
        Language.Ukrainian => "Ukrainian",
        Language.Bulgarian => "Bulgarian",
        Language.Slovak => "Slovak",
        Language.Slovenian => "Slovenian",

        // European - Baltic
        Language.Estonian => "Estonian",
        Language.Latvian => "Latvian",
        Language.Lithuanian => "Lithuanian",

        // European - Other
        Language.Greek => "Greek",
        Language.Hungarian => "Hungarian",
        Language.Finnish => "Finnish",
        Language.Turkish => "Turkish",

        // Middle Eastern
        Language.Arabic => "Arabic",
        Language.Persian => "Persian",
        Language.Hebrew => "Hebrew",

        // South Asian
        Language.Hindi => "Hindi",
        Language.Bengali => "Bengali",
        Language.Tamil => "Tamil",
        Language.Telugu => "Telugu",
        Language.Urdu => "Urdu",

        // Southeast Asian
        Language.Vietnamese => "Vietnamese",
        Language.Thai => "Thai",
        Language.Indonesian => "Indonesian",
        Language.Malay => "Malay",
        Language.Filipino => "Filipino",

        _ => lang.ToString()
    };

    /// <summary>
    /// Convert language to simplified code for settings storage.
    /// Uses 2-letter ISO codes where possible.
    /// </summary>
    public static string ToCode(this Language lang) => lang switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh",
        Language.TraditionalChinese => "zh-tw",
        Language.ClassicalChinese => "zh-classical",
        Language.English => "en",
        Language.Japanese => "ja",
        Language.Korean => "ko",
        Language.French => "fr",
        Language.Spanish => "es",
        Language.Portuguese => "pt",
        Language.Italian => "it",
        Language.German => "de",
        Language.Russian => "ru",
        Language.Arabic => "ar",
        Language.Swedish => "sv",
        Language.Romanian => "ro",
        Language.Thai => "th",
        Language.Dutch => "nl",
        Language.Hungarian => "hu",
        Language.Greek => "el",
        Language.Danish => "da",
        Language.Finnish => "fi",
        Language.Polish => "pl",
        Language.Czech => "cs",
        Language.Turkish => "tr",
        Language.Ukrainian => "uk",
        Language.Bulgarian => "bg",
        Language.Indonesian => "id",
        Language.Malay => "ms",
        Language.Vietnamese => "vi",
        Language.Persian => "fa",
        Language.Hindi => "hi",
        Language.Telugu => "te",
        Language.Tamil => "ta",
        Language.Urdu => "ur",
        Language.Filipino => "tl",
        Language.Bengali => "bn",
        Language.Norwegian => "no",
        Language.Hebrew => "he",
        Language.Slovak => "sk",
        Language.Slovenian => "sl",
        Language.Estonian => "et",
        Language.Latvian => "lv",
        Language.Lithuanian => "lt",
        _ => "auto"
    };

    /// <summary>
    /// Parse language from simplified code (settings format).
    /// </summary>
    public static Language FromCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Language.Auto;

        return code.ToLowerInvariant() switch
        {
            "auto" => Language.Auto,
            "zh" or "zh-cn" or "zh-hans" => Language.SimplifiedChinese,
            "zh-tw" or "zh-hant" => Language.TraditionalChinese,
            "zh-classical" => Language.ClassicalChinese,
            "en" => Language.English,
            "ja" => Language.Japanese,
            "ko" => Language.Korean,
            "fr" => Language.French,
            "es" => Language.Spanish,
            "pt" => Language.Portuguese,
            "it" => Language.Italian,
            "de" => Language.German,
            "ru" => Language.Russian,
            "ar" => Language.Arabic,
            "sv" => Language.Swedish,
            "ro" => Language.Romanian,
            "th" => Language.Thai,
            "nl" => Language.Dutch,
            "hu" => Language.Hungarian,
            "el" => Language.Greek,
            "da" => Language.Danish,
            "fi" => Language.Finnish,
            "pl" => Language.Polish,
            "cs" => Language.Czech,
            "tr" => Language.Turkish,
            "uk" => Language.Ukrainian,
            "bg" => Language.Bulgarian,
            "id" => Language.Indonesian,
            "ms" => Language.Malay,
            "vi" => Language.Vietnamese,
            "fa" => Language.Persian,
            "hi" => Language.Hindi,
            "te" => Language.Telugu,
            "ta" => Language.Tamil,
            "ur" => Language.Urdu,
            "tl" or "fil" => Language.Filipino,
            "bn" => Language.Bengali,
            "no" or "nb" => Language.Norwegian,
            "he" or "iw" => Language.Hebrew,
            "sk" => Language.Slovak,
            "sl" => Language.Slovenian,
            "et" => Language.Estonian,
            "lv" => Language.Latvian,
            "lt" => Language.Lithuanian,
            _ => Language.Auto
        };
    }
}

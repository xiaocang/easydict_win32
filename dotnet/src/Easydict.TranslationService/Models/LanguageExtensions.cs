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
    /// Get flag emoji for a language.
    /// </summary>
    public static string GetFlagEmoji(this Language lang) => lang switch
    {
        Language.SimplifiedChinese => "\U0001F1E8\U0001F1F3",  // 🇨🇳
        Language.TraditionalChinese => "\U0001F1F9\U0001F1FC",  // 🇹🇼
        Language.ClassicalChinese => "\U0001F1E8\U0001F1F3",    // 🇨🇳
        Language.English => "\U0001F1FA\U0001F1F8",              // 🇺🇸
        Language.Japanese => "\U0001F1EF\U0001F1F5",             // 🇯🇵
        Language.Korean => "\U0001F1F0\U0001F1F7",               // 🇰🇷
        Language.French => "\U0001F1EB\U0001F1F7",               // 🇫🇷
        Language.Spanish => "\U0001F1EA\U0001F1F8",              // 🇪🇸
        Language.Portuguese => "\U0001F1E7\U0001F1F7",           // 🇧🇷
        Language.Italian => "\U0001F1EE\U0001F1F9",              // 🇮🇹
        Language.German => "\U0001F1E9\U0001F1EA",               // 🇩🇪
        Language.Russian => "\U0001F1F7\U0001F1FA",              // 🇷🇺
        Language.Arabic => "\U0001F1F8\U0001F1E6",               // 🇸🇦
        Language.Swedish => "\U0001F1F8\U0001F1EA",              // 🇸🇪
        Language.Romanian => "\U0001F1F7\U0001F1F4",             // 🇷🇴
        Language.Thai => "\U0001F1F9\U0001F1ED",                 // 🇹🇭
        Language.Dutch => "\U0001F1F3\U0001F1F1",                // 🇳🇱
        Language.Hungarian => "\U0001F1ED\U0001F1FA",            // 🇭🇺
        Language.Greek => "\U0001F1EC\U0001F1F7",                // 🇬🇷
        Language.Danish => "\U0001F1E9\U0001F1F0",               // 🇩🇰
        Language.Finnish => "\U0001F1EB\U0001F1EE",              // 🇫🇮
        Language.Polish => "\U0001F1F5\U0001F1F1",               // 🇵🇱
        Language.Czech => "\U0001F1E8\U0001F1FF",                // 🇨🇿
        Language.Turkish => "\U0001F1F9\U0001F1F7",              // 🇹🇷
        Language.Ukrainian => "\U0001F1FA\U0001F1E6",            // 🇺🇦
        Language.Bulgarian => "\U0001F1E7\U0001F1EC",            // 🇧🇬
        Language.Indonesian => "\U0001F1EE\U0001F1E9",           // 🇮🇩
        Language.Malay => "\U0001F1F2\U0001F1FE",                // 🇲🇾
        Language.Vietnamese => "\U0001F1FB\U0001F1F3",           // 🇻🇳
        Language.Persian => "\U0001F1EE\U0001F1F7",              // 🇮🇷
        Language.Hindi => "\U0001F1EE\U0001F1F3",                // 🇮🇳
        Language.Telugu => "\U0001F1EE\U0001F1F3",               // 🇮🇳
        Language.Tamil => "\U0001F1EE\U0001F1F3",                // 🇮🇳
        Language.Urdu => "\U0001F1F5\U0001F1F0",                 // 🇵🇰
        Language.Filipino => "\U0001F1F5\U0001F1ED",             // 🇵🇭
        Language.Bengali => "\U0001F1E7\U0001F1E9",              // 🇧🇩
        Language.Norwegian => "\U0001F1F3\U0001F1F4",            // 🇳🇴
        Language.Hebrew => "\U0001F1EE\U0001F1F1",               // 🇮🇱
        Language.Slovak => "\U0001F1F8\U0001F1F0",               // 🇸🇰
        Language.Slovenian => "\U0001F1F8\U0001F1EE",            // 🇸🇮
        Language.Estonian => "\U0001F1EA\U0001F1EA",              // 🇪🇪
        Language.Latvian => "\U0001F1F1\U0001F1FB",              // 🇱🇻
        Language.Lithuanian => "\U0001F1F1\U0001F1F9",           // 🇱🇹
        _ => "\U0001F310"                                        // 🌐
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
    /// Convert language to BCP-47 locale tag for TTS voice selection.
    /// Returns a full locale tag (e.g., "en-US", "zh-CN") suitable for
    /// matching against system TTS voices.
    /// </summary>
    public static string ToBcp47(this Language lang) => lang switch
    {
        Language.SimplifiedChinese => "zh-CN",
        Language.TraditionalChinese => "zh-TW",
        Language.ClassicalChinese => "zh-CN",
        Language.English => "en-US",
        Language.Japanese => "ja-JP",
        Language.Korean => "ko-KR",
        Language.French => "fr-FR",
        Language.Spanish => "es-ES",
        Language.Portuguese => "pt-BR",
        Language.Italian => "it-IT",
        Language.German => "de-DE",
        Language.Russian => "ru-RU",
        Language.Arabic => "ar-SA",
        Language.Swedish => "sv-SE",
        Language.Romanian => "ro-RO",
        Language.Thai => "th-TH",
        Language.Dutch => "nl-NL",
        Language.Hungarian => "hu-HU",
        Language.Greek => "el-GR",
        Language.Danish => "da-DK",
        Language.Finnish => "fi-FI",
        Language.Polish => "pl-PL",
        Language.Czech => "cs-CZ",
        Language.Turkish => "tr-TR",
        Language.Ukrainian => "uk-UA",
        Language.Bulgarian => "bg-BG",
        Language.Indonesian => "id-ID",
        Language.Malay => "ms-MY",
        Language.Vietnamese => "vi-VN",
        Language.Persian => "fa-IR",
        Language.Hindi => "hi-IN",
        Language.Telugu => "te-IN",
        Language.Tamil => "ta-IN",
        Language.Urdu => "ur-PK",
        Language.Bengali => "bn-IN",
        Language.Norwegian => "nb-NO",
        Language.Hebrew => "he-IL",
        Language.Slovak => "sk-SK",
        Language.Slovenian => "sl-SI",
        Language.Estonian => "et-EE",
        Language.Latvian => "lv-LV",
        Language.Lithuanian => "lt-LT",
        Language.Filipino => "fil-PH",
        Language.Auto => "en-US",
        _ => "en-US"
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

namespace Easydict.TranslationService.Models;

/// <summary>
/// Supported languages for translation.
/// Language codes follow BCP-47 standard where applicable.
/// </summary>
public enum Language
{
    Auto,           // Auto-detect
    
    // East Asian
    SimplifiedChinese,
    TraditionalChinese,
    Japanese,
    Korean,
    
    // European - Germanic
    English,
    German,
    Dutch,
    Swedish,
    Norwegian,
    Danish,
    
    // European - Romance
    French,
    Spanish,
    Portuguese,
    Italian,
    Romanian,
    
    // European - Slavic
    Russian,
    Polish,
    Czech,
    Ukrainian,
    Bulgarian,
    Slovak,
    Slovenian,

    // European - Baltic
    Estonian,
    Latvian,
    Lithuanian,

    // European - Other
    Greek,
    Hungarian,
    Finnish,
    Turkish,
    
    // Middle Eastern
    Arabic,
    Persian,
    Hebrew,
    
    // South Asian
    Hindi,
    Bengali,
    Tamil,
    Telugu,
    Urdu,
    
    // Southeast Asian
    Vietnamese,
    Thai,
    Indonesian,
    Malay,
    Filipino,
    
    // Other
    ClassicalChinese,
}

/// <summary>
/// Language code mappings for different translation services.
/// </summary>
public static class LanguageCodes
{
    /// <summary>
    /// Get ISO 639-1 language code.
    /// </summary>
    public static string ToIso639(this Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh-CN",
        Language.TraditionalChinese => "zh-TW",
        Language.ClassicalChinese => "zh-CN",
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
        _ => "en"
    };

    /// <summary>
    /// Try to parse an ISO 639-1 (optionally with region tag like <c>zh-CN</c>) code into a
    /// <see cref="Language"/>. Returns <c>false</c> on null/whitespace/unknown input
    /// (where <see cref="FromIso639"/> would silently return <see cref="Language.English"/>).
    /// </summary>
    public static bool TryParseIsoCode(string? code, out Language language)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            language = Language.Auto;
            return false;
        }
        var parsed = FromIso639(code);
        // FromIso639 returns English as a hard fallback for unknowns. We need to distinguish
        // "explicit en" from "unknown → en", so re-check by looking the code up in the known set.
        var normalized = code.Trim().ToLowerInvariant();
        if (parsed == Language.English && normalized != "en")
        {
            language = Language.Auto;
            return false;
        }
        language = parsed;
        return true;
    }

    /// <summary>
    /// Parse language from ISO 639-1 code.
    /// </summary>
    public static Language FromIso639(string code) => code.ToLowerInvariant() switch
    {
        "auto" => Language.Auto,
        "zh-cn" or "zh-hans" or "zh" => Language.SimplifiedChinese,
        "zh-tw" or "zh-hant" => Language.TraditionalChinese,
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
        _ => Language.English
    };
}


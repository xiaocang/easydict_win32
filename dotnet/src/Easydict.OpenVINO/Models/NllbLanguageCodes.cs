using Easydict.TranslationService.Models;

namespace Easydict.OpenVINO.Models;

/// <summary>
/// Maps Easydict's <see cref="Language"/> enum to FLORES-200 language codes
/// (<c>eng_Latn</c>, <c>zho_Hans</c>, …) used by NLLB-200 as the target-language
/// prefix token and as the BOS forcing token for decoder generation.
/// </summary>
public static class NllbLanguageCodes
{
    private static readonly Dictionary<Language, string> _map = new()
    {
        // East Asian
        { Language.SimplifiedChinese,  "zho_Hans" },
        { Language.TraditionalChinese, "zho_Hant" },
        { Language.ClassicalChinese,   "zho_Hans" }, // FLORES-200 has no classical Chinese; fall back to simplified
        { Language.Japanese,           "jpn_Jpan" },
        { Language.Korean,             "kor_Hang" },

        // European - Germanic
        { Language.English,    "eng_Latn" },
        { Language.German,     "deu_Latn" },
        { Language.Dutch,      "nld_Latn" },
        { Language.Swedish,    "swe_Latn" },
        { Language.Norwegian,  "nob_Latn" }, // Bokmål (more widely written than Nynorsk)
        { Language.Danish,     "dan_Latn" },

        // European - Romance
        { Language.French,     "fra_Latn" },
        { Language.Spanish,    "spa_Latn" },
        { Language.Portuguese, "por_Latn" },
        { Language.Italian,    "ita_Latn" },
        { Language.Romanian,   "ron_Latn" },

        // European - Slavic
        { Language.Russian,    "rus_Cyrl" },
        { Language.Polish,     "pol_Latn" },
        { Language.Czech,      "ces_Latn" },
        { Language.Ukrainian,  "ukr_Cyrl" },
        { Language.Bulgarian,  "bul_Cyrl" },
        { Language.Slovak,     "slk_Latn" },
        { Language.Slovenian,  "slv_Latn" },

        // European - Baltic
        { Language.Estonian,   "est_Latn" },
        { Language.Latvian,    "lvs_Latn" }, // Standard Latvian
        { Language.Lithuanian, "lit_Latn" },

        // European - Other
        { Language.Greek,      "ell_Grek" },
        { Language.Hungarian,  "hun_Latn" },
        { Language.Finnish,    "fin_Latn" },
        { Language.Turkish,    "tur_Latn" },

        // Middle Eastern
        { Language.Arabic,     "arb_Arab" }, // Modern Standard Arabic
        { Language.Persian,    "pes_Arab" }, // Western Persian (Iran)
        { Language.Hebrew,     "heb_Hebr" },

        // South Asian
        { Language.Hindi,      "hin_Deva" },
        { Language.Bengali,    "ben_Beng" },
        { Language.Tamil,      "tam_Taml" },
        { Language.Telugu,     "tel_Telu" },
        { Language.Urdu,       "urd_Arab" },

        // Southeast Asian
        { Language.Vietnamese, "vie_Latn" },
        { Language.Thai,       "tha_Thai" },
        { Language.Indonesian, "ind_Latn" },
        { Language.Malay,      "zsm_Latn" }, // Standard Malay
        { Language.Filipino,   "tgl_Latn" }, // FLORES-200 lists Tagalog; Filipino is Tagalog-based
    };

    /// <summary>
    /// Languages NLLB-200 can produce. <see cref="Language.Auto"/> is excluded
    /// (NLLB requires an explicit target). All <see cref="Language"/> values that
    /// map to a FLORES-200 code are supported on both source and target sides.
    /// </summary>
    public static IReadOnlyCollection<Language> SupportedLanguages => _map.Keys;

    /// <summary>
    /// Returns the FLORES-200 code for the given language, or null if NLLB-200
    /// has no mapping (currently only <see cref="Language.Auto"/>).
    /// </summary>
    public static string? TryGetCode(Language language)
    {
        return _map.TryGetValue(language, out var code) ? code : null;
    }

    /// <summary>
    /// Returns the FLORES-200 code or throws if no mapping exists. Use after
    /// validating with <see cref="TryGetCode"/>.
    /// </summary>
    public static string GetCode(Language language)
    {
        if (_map.TryGetValue(language, out var code))
        {
            return code;
        }

        throw new ArgumentException(
            $"No FLORES-200 mapping for {language}; NLLB-200 cannot translate to/from this language.",
            nameof(language));
    }
}

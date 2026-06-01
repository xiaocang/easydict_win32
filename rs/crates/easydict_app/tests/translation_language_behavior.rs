use easydict_app::{TranslationLanguage, ALL_TRANSLATION_LANGUAGES};

#[test]
fn translation_language_to_bcp47_returns_expected_core_tags() {
    let cases = [
        (TranslationLanguage::English, "en-US"),
        (TranslationLanguage::SimplifiedChinese, "zh-CN"),
        (TranslationLanguage::TraditionalChinese, "zh-TW"),
        (TranslationLanguage::ClassicalChinese, "zh-CN"),
        (TranslationLanguage::Japanese, "ja-JP"),
        (TranslationLanguage::Korean, "ko-KR"),
        (TranslationLanguage::French, "fr-FR"),
        (TranslationLanguage::Spanish, "es-ES"),
        (TranslationLanguage::Portuguese, "pt-BR"),
        (TranslationLanguage::Italian, "it-IT"),
        (TranslationLanguage::German, "de-DE"),
        (TranslationLanguage::Russian, "ru-RU"),
        (TranslationLanguage::Arabic, "ar-SA"),
        (TranslationLanguage::Hebrew, "he-IL"),
        (TranslationLanguage::Hindi, "hi-IN"),
        (TranslationLanguage::Norwegian, "nb-NO"),
        (TranslationLanguage::Filipino, "fil-PH"),
    ];

    for (language, expected) in cases {
        assert_eq!(language.to_bcp47(), expected, "{language:?}");
    }
}

#[test]
fn translation_language_to_bcp47_returns_expected_remaining_tags() {
    let cases = [
        (TranslationLanguage::Swedish, "sv-SE"),
        (TranslationLanguage::Romanian, "ro-RO"),
        (TranslationLanguage::Thai, "th-TH"),
        (TranslationLanguage::Dutch, "nl-NL"),
        (TranslationLanguage::Hungarian, "hu-HU"),
        (TranslationLanguage::Greek, "el-GR"),
        (TranslationLanguage::Danish, "da-DK"),
        (TranslationLanguage::Finnish, "fi-FI"),
        (TranslationLanguage::Polish, "pl-PL"),
        (TranslationLanguage::Czech, "cs-CZ"),
        (TranslationLanguage::Turkish, "tr-TR"),
        (TranslationLanguage::Ukrainian, "uk-UA"),
        (TranslationLanguage::Bulgarian, "bg-BG"),
        (TranslationLanguage::Indonesian, "id-ID"),
        (TranslationLanguage::Malay, "ms-MY"),
        (TranslationLanguage::Vietnamese, "vi-VN"),
        (TranslationLanguage::Persian, "fa-IR"),
        (TranslationLanguage::Telugu, "te-IN"),
        (TranslationLanguage::Tamil, "ta-IN"),
        (TranslationLanguage::Urdu, "ur-PK"),
        (TranslationLanguage::Bengali, "bn-IN"),
        (TranslationLanguage::Slovak, "sk-SK"),
        (TranslationLanguage::Slovenian, "sl-SI"),
        (TranslationLanguage::Estonian, "et-EE"),
        (TranslationLanguage::Latvian, "lv-LV"),
        (TranslationLanguage::Lithuanian, "lt-LT"),
    ];

    for (language, expected) in cases {
        assert_eq!(language.to_bcp47(), expected, "{language:?}");
    }
}

#[test]
fn translation_language_to_bcp47_auto_falls_back_to_en_us() {
    assert_eq!(TranslationLanguage::Auto.to_bcp47(), "en-US");
}

#[test]
fn translation_language_all_bcp47_tags_are_non_empty_locale_tags() {
    for language in ALL_TRANSLATION_LANGUAGES {
        assert!(!language.to_bcp47().is_empty(), "{language:?}");
        assert!(language.to_bcp47().contains('-'), "{language:?}");
    }
}

#[test]
fn translation_language_chinese_variants_have_distinct_bcp47_tags() {
    let simplified = TranslationLanguage::SimplifiedChinese.to_bcp47();
    let traditional = TranslationLanguage::TraditionalChinese.to_bcp47();

    assert_ne!(simplified, traditional);
    assert!(simplified.starts_with("zh-"));
    assert!(traditional.starts_with("zh-"));
}

#[test]
fn translation_language_code_mappings_match_legacy_settings_aliases() {
    let cases = [
        ("auto", TranslationLanguage::Auto, "auto"),
        ("zh", TranslationLanguage::SimplifiedChinese, "zh"),
        ("zh-CN", TranslationLanguage::SimplifiedChinese, "zh"),
        ("zh-Hans", TranslationLanguage::SimplifiedChinese, "zh"),
        ("zh-TW", TranslationLanguage::TraditionalChinese, "zh-tw"),
        ("zh-Hant", TranslationLanguage::TraditionalChinese, "zh-tw"),
        (
            "zh-classical",
            TranslationLanguage::ClassicalChinese,
            "zh-classical",
        ),
        ("fil", TranslationLanguage::Filipino, "tl"),
        ("tl", TranslationLanguage::Filipino, "tl"),
        ("iw", TranslationLanguage::Hebrew, "he"),
        ("nb", TranslationLanguage::Norwegian, "no"),
        ("unknown", TranslationLanguage::Auto, "auto"),
    ];

    for (input, expected_language, expected_code) in cases {
        let language = TranslationLanguage::from_code(input);
        assert_eq!(language, expected_language, "{input}");
        assert_eq!(language.to_code(), expected_code, "{input}");
    }
}

#[test]
fn translation_language_iso639_unknown_falls_back_to_english() {
    assert_eq!(
        TranslationLanguage::from_iso639("unknown"),
        TranslationLanguage::English
    );
    assert_eq!(
        TranslationLanguage::from_iso639("definitely-not-a-language"),
        TranslationLanguage::English
    );
    assert_eq!(
        TranslationLanguage::from_iso639("auto"),
        TranslationLanguage::Auto
    );
}

#[test]
fn translation_language_display_names_match_translation_service_names() {
    let cases = [
        (TranslationLanguage::Auto, "Auto Detect"),
        (
            TranslationLanguage::SimplifiedChinese,
            "Chinese (Simplified)",
        ),
        (
            TranslationLanguage::TraditionalChinese,
            "Chinese (Traditional)",
        ),
        (TranslationLanguage::ClassicalChinese, "Classical Chinese"),
        (TranslationLanguage::English, "English"),
        (TranslationLanguage::Filipino, "Filipino"),
    ];

    for (language, expected) in cases {
        assert_eq!(language.display_name(), expected, "{language:?}");
    }
}

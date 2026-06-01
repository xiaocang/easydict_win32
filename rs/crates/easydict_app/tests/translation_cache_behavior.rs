use easydict_app::{
    displayable_phonetics, format_phonetic_text, is_youdao_word_query, merge_phonetics_into_result,
    phonetic_accent_display_label, phonetic_cache_key, plan_phonetic_enrichment, target_phonetics,
    translation_cache_entry_size_kb, translation_cache_key, Definition, Phonetic,
    PhoneticEnrichmentDecision, PhoneticEnrichmentSkipReason, PhoneticFlightRegistration,
    PhoneticFlightTracker, PhoneticMemoryCache, TranslationCacheRequest, TranslationLanguage,
    TranslationMemoryCache, TranslationResult, WordResult,
};

#[test]
fn translation_cache_key_matches_dotnet_sha256_hex() {
    let key = translation_cache_key(
        "google",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "Hello",
    );

    assert_eq!(
        key,
        "7F08F49BED9E65905CC46F9C6DA22D2817B5F467597CBC0FFB066A102E827985"
    );
}

#[test]
fn translation_memory_cache_returns_cached_result_with_from_cache_flag() {
    let mut cache = TranslationMemoryCache::new();
    let request = TranslationCacheRequest::new(
        "cache-test",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "Hello",
    );
    let result = TranslationResult::success(
        "\u{4F60}\u{597D}",
        "Hello",
        TranslationLanguage::SimplifiedChinese,
        "Cache Test",
    );

    cache.insert(&request, result);
    let cached = cache.get(&request).expect("result should be cached");

    assert!(cached.from_cache);
    assert_eq!(cached.translated_text, "\u{4F60}\u{597D}");
    assert_eq!(cache.len(), 1);
}

#[test]
fn translation_memory_cache_respects_bypass_and_clear() {
    let mut cache = TranslationMemoryCache::new();
    let request = TranslationCacheRequest::new(
        "cache-test",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "Hello",
    );
    let mut bypass_request = request.clone();
    bypass_request.bypass_cache = true;

    cache.insert(
        &bypass_request,
        TranslationResult::success(
            "\u{4F60}\u{597D}",
            "Hello",
            TranslationLanguage::SimplifiedChinese,
            "Cache Test",
        ),
    );
    assert!(cache.is_empty());

    cache.insert(
        &request,
        TranslationResult::success(
            "\u{4F60}\u{597D}",
            "Hello",
            TranslationLanguage::SimplifiedChinese,
            "Cache Test",
        ),
    );
    assert!(cache.get(&bypass_request).is_none());

    cache.clear();
    assert!(cache.get(&request).is_none());
}

#[test]
fn translation_memory_cache_evicts_least_recent_entries_over_limit() {
    let mut cache = TranslationMemoryCache::with_size_limit_kb(1);
    let first = TranslationCacheRequest::new(
        "cache-test",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "first",
    );
    let second = TranslationCacheRequest::new(
        "cache-test",
        TranslationLanguage::English,
        TranslationLanguage::SimplifiedChinese,
        "second",
    );

    cache.insert(
        &first,
        TranslationResult::success(
            "one",
            "first",
            TranslationLanguage::SimplifiedChinese,
            "Svc",
        ),
    );
    let _ = cache.get(&first);
    cache.insert(
        &second,
        TranslationResult::success(
            "two",
            "second",
            TranslationLanguage::SimplifiedChinese,
            "Svc",
        ),
    );

    assert!(cache.get(&first).is_none());
    assert_eq!(cache.get(&second).unwrap().translated_text, "two");
}

#[test]
fn translation_cache_entry_size_uses_dotnet_utf16_rounding() {
    let result =
        TranslationResult::success("ok", "Hello", TranslationLanguage::English, "Cache Test");
    let key = "abc";

    assert_eq!(translation_cache_entry_size_kb(key, "Hello", &result), 1);
}

#[test]
fn phonetic_cache_key_trims_and_lowercases_like_dotnet() {
    assert_eq!(
        phonetic_cache_key("  Hello WORLD  "),
        "phonetic:hello world"
    );
}

#[test]
fn youdao_word_query_matches_dotnet_examples() {
    let cases = [
        ("hello", true),
        ("hello world", true),
        ("test-driven", true),
        ("don't", true),
        ("This is a test sentence.", false),
        ("Hello!", false),
        ("What?", false),
        ("Line one\nLine two", false),
        ("", false),
        ("   ", false),
    ];

    for (text, expected) in cases {
        assert_eq!(is_youdao_word_query(text), expected, "{text:?}");
    }
}

#[test]
fn youdao_word_query_preserves_cjk_and_ratio_rules() {
    assert!(is_youdao_word_query("\u{4F60}"));
    assert!(is_youdao_word_query("\u{4F60}\u{597D}"));
    assert!(is_youdao_word_query("\u{4F60}\u{597D}\u{554A}"));
    assert!(!is_youdao_word_query("\u{4F60}\u{597D}\u{554A}\u{5440}"));
    assert!(!is_youdao_word_query("\u{4F60}\u{597D}abc"));
    assert!(!is_youdao_word_query("abc123"));
    assert!(is_youdao_word_query("hello, world"));
    assert!(!is_youdao_word_query("\u{4F60}\u{597D}\u{3002}"));
}

#[test]
fn phonetic_display_helpers_match_dotnet_labels_and_slash_wrapping() {
    assert_eq!(phonetic_accent_display_label(Some("US")), Some("\u{7F8E}"));
    assert_eq!(phonetic_accent_display_label(Some("UK")), Some("\u{82F1}"));
    assert_eq!(phonetic_accent_display_label(Some("src")), Some("\u{539F}"));
    assert_eq!(
        phonetic_accent_display_label(Some("dest")),
        Some("\u{8BD1}")
    );
    assert_eq!(phonetic_accent_display_label(Some("AU")), Some("AU"));
    assert_eq!(phonetic_accent_display_label(Some("")), None);
    assert_eq!(phonetic_accent_display_label(None), None);

    assert_eq!(format_phonetic_text("hello"), "/hello/");
    assert_eq!(format_phonetic_text("/hello/"), "/hello/");
    assert_eq!(format_phonetic_text("/hello"), "//hello/");
}

#[test]
fn target_phonetics_filters_to_dest_us_and_uk_with_text() {
    let result = result_with_phonetics(vec![
        Phonetic::new("source", "src"),
        Phonetic::new("dest", "dest"),
        Phonetic::new("us", "US"),
        Phonetic::new("uk", "UK"),
        Phonetic {
            text: Some(String::new()),
            accent: Some("US".to_string()),
            audio_url: None,
        },
    ]);

    assert_eq!(displayable_phonetics(&result).len(), 4);
    let target = target_phonetics(&result);

    assert_eq!(target.len(), 3);
    assert_eq!(target[0].accent.as_deref(), Some("dest"));
    assert_eq!(target[1].accent.as_deref(), Some("US"));
    assert_eq!(target[2].accent.as_deref(), Some("UK"));
}

#[test]
fn phonetic_enrichment_plan_matches_translation_manager_gates() {
    let no_phonetics = TranslationResult::success(
        "hello",
        "\u{4F60}\u{597D}",
        TranslationLanguage::English,
        "OpenAI",
    );
    assert_eq!(
        plan_phonetic_enrichment(&no_phonetics, TranslationLanguage::SimplifiedChinese),
        PhoneticEnrichmentDecision::Skip(PhoneticEnrichmentSkipReason::TargetNotEnglish)
    );

    let sentence = TranslationResult::success(
        "Hello there.",
        "\u{4F60}\u{597D}",
        TranslationLanguage::English,
        "OpenAI",
    );
    assert_eq!(
        plan_phonetic_enrichment(&sentence, TranslationLanguage::English),
        PhoneticEnrichmentDecision::Skip(PhoneticEnrichmentSkipReason::NotWordQuery)
    );

    let with_target = result_with_phonetics(vec![Phonetic::new("hello", "US")]);
    assert_eq!(
        plan_phonetic_enrichment(&with_target, TranslationLanguage::English),
        PhoneticEnrichmentDecision::Skip(PhoneticEnrichmentSkipReason::AlreadyHasTargetPhonetics)
    );

    let source_only = result_with_phonetics(vec![Phonetic::new("source", "src")]);
    assert_eq!(
        plan_phonetic_enrichment(&source_only, TranslationLanguage::English),
        PhoneticEnrichmentDecision::Fetch {
            english_word: "hello".to_string(),
            cache_key: "phonetic:hello".to_string(),
        }
    );
}

#[test]
fn merge_phonetics_preserves_existing_word_payload() {
    let mut result = result_with_phonetics(vec![Phonetic::new("source", "src")]);
    result
        .word_result
        .as_mut()
        .unwrap()
        .definitions
        .push(Definition {
            part_of_speech: Some("n.".to_string()),
            meanings: vec!["greeting".to_string()],
        });

    let merged = merge_phonetics_into_result(
        result,
        &[Phonetic {
            text: Some("hello".to_string()),
            accent: Some("US".to_string()),
            audio_url: Some("https://example.invalid/hello.mp3".to_string()),
        }],
    );

    let word = merged.word_result.unwrap();
    assert_eq!(word.phonetics.len(), 2);
    assert_eq!(word.definitions.len(), 1);
}

#[test]
fn phonetic_memory_cache_stores_non_empty_phonetics() {
    let mut cache = PhoneticMemoryCache::new();
    cache.insert("Hello", vec![Phonetic::new("hello", "US")]);

    let cached = cache.get(" hello ").expect("phonetics should be cached");
    assert_eq!(cached, vec![Phonetic::new("hello", "US")]);
}

#[test]
fn phonetic_flight_tracker_deduplicates_same_cache_key_until_complete() {
    let mut tracker = PhoneticFlightTracker::default();

    assert_eq!(tracker.begin("Hello"), PhoneticFlightRegistration::Started);
    assert_eq!(
        tracker.begin(" hello "),
        PhoneticFlightRegistration::AlreadyInFlight
    );

    assert!(tracker.complete("HELLO"));
    assert_eq!(tracker.begin("hello"), PhoneticFlightRegistration::Started);
}

fn result_with_phonetics(phonetics: Vec<Phonetic>) -> TranslationResult {
    let mut result = TranslationResult::success(
        "hello",
        "\u{4F60}\u{597D}",
        TranslationLanguage::English,
        "OpenAI",
    );
    result.word_result = Some(WordResult {
        phonetics,
        ..WordResult::default()
    });
    result
}

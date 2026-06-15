use easydict_app::{
    app_visible_translation_service_ids, default_translation_service_descriptors,
    find_translation_service_descriptor, openai_compatible_service_ids,
    translation_service_capabilities, EasydictUiState, TranslationServiceKind,
    DEFAULT_FLOATING_WINDOW_SERVICE_IDS, DEFAULT_MAIN_WINDOW_SERVICE_IDS, DEFAULT_SERVICE_ID,
};

#[test]
fn service_catalog_registers_migration_list_services_in_ui_order() {
    let mut expected = vec![
        "google",
        "google_web",
        "bing",
        "deepl",
        "youdao",
        "openai",
        "ollama",
        "builtin",
        "deepseek",
        "groq",
        "zhipu",
        "github",
        "custom-openai",
        "gemini",
        "doubao",
        "caiyun",
        "niutrans",
        "volcano",
        "linguee",
    ];
    expected.push("windows-local-ai");

    assert_eq!(app_visible_translation_service_ids(), expected);
    assert_eq!(DEFAULT_SERVICE_ID, "google");
    assert_eq!(
        DEFAULT_MAIN_WINDOW_SERVICE_IDS,
        ["google", "bing", "openai"]
    );
    assert_eq!(DEFAULT_FLOATING_WINDOW_SERVICE_IDS, ["google"]);
}

#[test]
fn service_catalog_marks_openai_compatible_services() {
    assert_eq!(
        openai_compatible_service_ids(),
        vec![
            "openai",
            "ollama",
            "builtin",
            "deepseek",
            "groq",
            "zhipu",
            "github",
            "custom-openai",
        ]
    );

    for service_id in openai_compatible_service_ids() {
        let descriptor = find_translation_service_descriptor(service_id).unwrap();
        assert_eq!(descriptor.kind, TranslationServiceKind::OpenAiCompatible);
        assert!(descriptor.streaming_capable, "{service_id}");
        assert!(descriptor.grammar_capable, "{service_id}");
    }
}

#[test]
fn service_catalog_preserves_configured_and_api_key_metadata() {
    let openai = find_translation_service_descriptor("openai").unwrap();
    assert!(!openai.configured_by_default);
    assert!(openai.requires_api_key);

    let ollama = find_translation_service_descriptor("ollama").unwrap();
    assert!(ollama.configured_by_default);
    assert!(!ollama.requires_api_key);

    let deepl = find_translation_service_descriptor("deepl").unwrap();
    assert!(deepl.configured_by_default);
    assert!(!deepl.requires_api_key);

    let caiyun = find_translation_service_descriptor("caiyun").unwrap();
    assert!(!caiyun.configured_by_default);
    assert!(caiyun.requires_api_key);
}

#[test]
fn service_catalog_exposes_streaming_and_grammar_capabilities() {
    assert_eq!(translation_service_capabilities("google"), (false, false));
    assert_eq!(translation_service_capabilities("openai"), (true, true));
    assert_eq!(translation_service_capabilities("gemini"), (true, true));
    assert_eq!(translation_service_capabilities("doubao"), (true, false));
    assert_eq!(
        translation_service_capabilities("windows-local-ai"),
        (true, true)
    );
    assert_eq!(translation_service_capabilities("unknown"), (false, false));
}

#[test]
fn service_catalog_feeds_default_window_service_state() {
    let state = EasydictUiState::default();
    let main_ids = state
        .settings
        .main_window_services
        .iter()
        .map(|service| service.service_id.as_str())
        .collect::<Vec<_>>();

    assert_eq!(main_ids, app_visible_translation_service_ids());
    assert!(window_service_enabled(&state, "google"));
    assert!(window_service_enabled(&state, "bing"));
    assert!(window_service_enabled(&state, "openai"));
    assert!(!window_service_enabled(&state, "google_web"));
    assert!(!window_service_configured(&state, "openai"));
}

#[test]
fn service_catalog_includes_linguee_by_default() {
    let ids = app_visible_translation_service_ids();
    assert!(ids.contains(&"linguee"));

    let descriptor = find_translation_service_descriptor("linguee")
        .expect("Linguee should be part of the default Rust service catalog");
    assert_eq!(descriptor.kind, TranslationServiceKind::Dictionary);
    assert!(descriptor.configured_by_default);
    assert!(!descriptor.requires_api_key);
}

#[test]
fn service_catalog_has_unique_service_ids() {
    let descriptors = default_translation_service_descriptors();
    for (index, descriptor) in descriptors.iter().enumerate() {
        assert!(
            !descriptors
                .iter()
                .skip(index + 1)
                .any(|other| other.service_id.eq_ignore_ascii_case(descriptor.service_id)),
            "duplicate service id {}",
            descriptor.service_id
        );
    }
}

#[test]
fn service_catalog_does_not_expose_retained_runtime_entries() {
    let forbidden_markers = [
        ".net",
        "compat",
        "compathost",
        "dotnet",
        "easydict.workers",
        "hostfxr",
        "powershell",
        "pwsh",
        "retained",
        "worker",
    ];

    for descriptor in default_translation_service_descriptors() {
        let visible_text = format!(
            "{} {}",
            descriptor.service_id.to_ascii_lowercase(),
            descriptor.display_name.to_ascii_lowercase()
        );
        for marker in forbidden_markers {
            assert!(
                !visible_text.contains(marker),
                "service catalog entry '{}' / '{}' should not expose retained runtime marker '{}'",
                descriptor.service_id,
                descriptor.display_name,
                marker
            );
        }
    }
}

#[test]
fn default_service_ids_resolve_to_catalog_entries() {
    let catalog_ids = app_visible_translation_service_ids();
    for service_id in DEFAULT_MAIN_WINDOW_SERVICE_IDS
        .into_iter()
        .chain(DEFAULT_FLOATING_WINDOW_SERVICE_IDS)
        .chain([DEFAULT_SERVICE_ID])
    {
        assert!(
            catalog_ids
                .iter()
                .any(|catalog_id| catalog_id.eq_ignore_ascii_case(service_id)),
            "default service id '{service_id}' should exist in the app-visible catalog"
        );
    }
}

fn window_service_enabled(state: &EasydictUiState, service_id: &str) -> bool {
    state
        .settings
        .main_window_services
        .iter()
        .find(|service| service.service_id == service_id)
        .map(|service| service.enabled)
        .unwrap_or(false)
}

fn window_service_configured(state: &EasydictUiState, service_id: &str) -> bool {
    state
        .settings
        .main_window_services
        .iter()
        .find(|service| service.service_id == service_id)
        .map(|service| service.configured)
        .unwrap_or(false)
}

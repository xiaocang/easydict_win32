#[test]
fn default_api_surface_has_no_legacy_packaged_app_dir_aliases() {
    let quick_translate = include_str!("../src/quick_translate.rs");
    let long_document = include_str!("../src/long_document.rs");
    let local_dictionary = include_str!("../src/local_dictionary.rs");
    let ocr = include_str!("../src/ocr.rs");

    for forbidden_signature in [
        "pub fn run_quick_translate_with_packaged_app_dir(",
        "pub fn run_quick_translate_service_with_packaged_app_dir(",
        "pub fn run_long_document_request_with_packaged_app_dir(",
        "pub fn run_local_dictionary_suggestion_request_with_packaged_app_dir(",
        "pub fn run_ocr_recognize_with_packaged_app_dir(",
    ] {
        let source = if forbidden_signature.contains("quick_translate") {
            quick_translate
        } else if forbidden_signature.contains("long_document") {
            long_document
        } else if forbidden_signature.contains("local_dictionary") {
            local_dictionary
        } else {
            ocr
        };
        assert!(
            !source.contains(forbidden_signature),
            "default rs API must use neutral with_app_dir helpers, not {forbidden_signature}"
        );
    }

    assert!(
        quick_translate
            .contains("run_quick_translate_service_with_packaged_app_dir_and_worker_policy"),
        "retained-worker audit helper should remain explicitly named and feature gated"
    );
    assert!(
        long_document.contains("run_long_document_request_with_packaged_app_dir_and_worker_policy"),
        "retained LongDoc audit helper should remain explicitly named and feature gated"
    );
}

#[test]
fn crate_root_retained_worker_exports_are_feature_gated() {
    let crate_root = include_str!("../src/lib.rs");

    for retained_export in [
        "pub mod compat_client;",
        "pub mod compat_protocol;",
        "run_local_dictionary_suggestion_request_with_lazy_bridge",
        "run_local_dictionary_suggestion_request_with_routed_backends",
        "run_long_document_request_with_packaged_app_dir_and_worker_policy",
        "LocalAiWorkerQuickTranslateBackend",
        "run_quick_translate_service_with_packaged_app_dir_and_worker_policy",
        "run_quick_translate_service_with_packaged_app_dir_and_worker_policy_and_foundry_resolver",
        "run_quick_translate_streaming_service_with_packaged_app_dir_and_worker_policy_and_foundry_resolver",
        "RetainedWorkerPolicy",
    ] {
        assert_crate_root_export_is_retained_worker_feature_gated(crate_root, retained_export);
    }
}

#[test]
fn default_cargo_features_do_not_enable_retained_dotnet_workers() {
    let app_manifest = include_str!("../Cargo.toml");
    let preview_manifest = include_str!("../../easydict_preview_iced/Cargo.toml");

    assert!(
        app_manifest.contains("\n[features]\ndefault = []\n"),
        "easydict_app default Cargo features must stay empty for rs portable"
    );
    assert!(
        app_manifest.contains("\nretained-dotnet-workers = []\n"),
        "retained-dotnet-workers must remain an explicit opt-in feature"
    );
    assert!(
        !preview_manifest.contains("retained-dotnet-workers"),
        "easydict_preview_iced must not enable retained .NET worker features implicitly"
    );
}

#[test]
fn default_process_spawn_surface_has_no_retained_dotnet_runtime_entries() {
    let default_sources = [
        (
            "src/browser_registrar.rs",
            include_str!("../src/browser_registrar.rs"),
        ),
        (
            "src/cli_translate.rs",
            include_str!("../src/cli_translate.rs"),
        ),
        ("src/lib.rs", include_str!("../src/lib.rs")),
        (
            "src/local_dictionary.rs",
            include_str!("../src/local_dictionary.rs"),
        ),
        (
            "src/long_document.rs",
            include_str!("../src/long_document.rs"),
        ),
        (
            "src/long_document_cli.rs",
            include_str!("../src/long_document_cli.rs"),
        ),
        (
            "src/native_bridge.rs",
            include_str!("../src/native_bridge.rs"),
        ),
        ("src/ocr.rs", include_str!("../src/ocr.rs")),
        (
            "src/openai_compatible.rs",
            include_str!("../src/openai_compatible.rs"),
        ),
        (
            "src/quick_translate.rs",
            include_str!("../src/quick_translate.rs"),
        ),
        (
            "src/retained_workers.rs",
            include_str!("../src/retained_workers.rs"),
        ),
        (
            "src/settings_migration.rs",
            include_str!("../src/settings_migration.rs"),
        ),
        (
            "src/settings_storage.rs",
            include_str!("../src/settings_storage.rs"),
        ),
    ];

    for (path, source) in default_sources {
        assert_no_retained_dotnet_runtime_entry_markers(path, production_source(source));
    }

    let crate_root = include_str!("../src/lib.rs");
    assert_crate_root_export_is_retained_worker_feature_gated(crate_root, "pub mod compat_client;");
    assert_crate_root_export_is_retained_worker_feature_gated(
        crate_root,
        "pub mod compat_protocol;",
    );

    let compat_client_tests = include_str!("compat_client.rs");
    assert!(
        compat_client_tests
            .trim_start()
            .starts_with("#![cfg(feature = \"retained-dotnet-workers\")]"),
        "retained worker process-spawn tests must stay behind retained-dotnet-workers cfg"
    );
}

fn assert_crate_root_export_is_retained_worker_feature_gated(crate_root: &str, export: &str) {
    const RETAINED_WORKER_CFG: &str = "#[cfg(feature = \"retained-dotnet-workers\")]";

    let export_offset = crate_root
        .find(export)
        .unwrap_or_else(|| panic!("crate root should still mention retained export {export}"));
    let export_line_start = crate_root[..export_offset]
        .rfind('\n')
        .map_or(0, |index| index + 1);
    let before_export_line = crate_root[..export_line_start].trim_end();
    if before_export_line.ends_with(RETAINED_WORKER_CFG) {
        return;
    }

    let pub_use_offset = crate_root[..export_offset]
        .rfind("pub use ")
        .unwrap_or_else(|| {
            panic!("crate-root retained export {export} should be in a cfg-gated pub use block")
        });
    let before_pub_use_line = crate_root[..pub_use_offset].trim_end();
    assert!(
        before_pub_use_line.ends_with(RETAINED_WORKER_CFG),
        "crate-root retained export {export} must stay behind retained-dotnet-workers cfg"
    );
}

fn assert_no_retained_dotnet_runtime_entry_markers(path: &str, source: &str) {
    for marker in [
        ".NET Runtime",
        ".NET runtime",
        ".net runtime",
        "CompatHost",
        "DOTNET_ROOT",
        "Easydict.Workers",
        "dotnet runtime",
        "dotnet.exe",
        "host\\fxr",
        "host/fxr",
        "hostfxr",
    ] {
        if let Some((line_number, _)) =
            non_comment_lines(source).find(|(_, line)| line.contains(marker))
        {
            panic!(
                "{path}:{line_number} must not expose retained .NET runtime/worker entry marker {marker:?} on the default rs surface"
            );
        }
    }
}

fn production_source(source: &str) -> &str {
    source
        .find("#[cfg(test)]")
        .map_or(source, |index| &source[..index])
}

fn non_comment_lines(source: &str) -> impl Iterator<Item = (usize, &str)> {
    source.lines().enumerate().filter_map(|(index, line)| {
        let trimmed = line.trim_start();
        (!trimmed.starts_with("//")).then_some((index + 1, line))
    })
}

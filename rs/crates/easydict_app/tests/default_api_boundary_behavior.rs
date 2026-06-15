use std::fs;
use std::path::{Path, PathBuf};

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
fn default_cli_rejects_legacy_retained_worker_options_unless_feature_gated() {
    let cli_translate = include_str!("../src/cli_translate.rs");
    let long_document_cli = include_str!("../src/long_document_cli.rs");

    for option in ["--host", "--host-arg", "--app-dir"] {
        for section in cli_legacy_option_sections(cli_translate, option) {
            assert!(
                section.contains("#[cfg(feature = \"retained-dotnet-workers\")]"),
                "legacy CLI option {option} should keep any compatibility parsing behind retained-dotnet-workers cfg:\n{section}"
            );
            assert!(
                section.contains("#[cfg(not(feature = \"retained-dotnet-workers\"))]"),
                "legacy CLI option {option} should have a default-build rejection guard:\n{section}"
            );
            assert!(
                section.contains("CliParseError::UnknownOption"),
                "legacy CLI option {option} should be unknown in default builds:\n{section}"
            );
        }
    }
    assert!(
        cli_translate.contains("default_build_rejects_legacy_host_and_app_dir_options"),
        "parser unit tests should lock default rejection of legacy retained-worker CLI options"
    );
    assert!(
        cli_translate
            .contains("retained_feature_accepts_legacy_host_without_exposing_worker_target"),
        "legacy host acceptance should only be documented by an explicitly retained-feature test"
    );
    assert!(
        !cli_translate.contains("fn accepts_legacy_host_without_exposing_worker_target"),
        "default parser tests must not keep an unqualified legacy host acceptance test"
    );
    assert!(
        !long_document_cli.contains("long = \"app-dir\""),
        "default LongDoc CLI must reject legacy --app-dir instead of accepting it as a hidden retained-worker-era no-op"
    );
}

#[test]
fn default_browser_registrar_does_not_export_legacy_host_or_root_helpers() {
    let browser_registrar = include_str!("../src/browser_registrar.rs");
    let production = production_source(browser_registrar);
    let app_root = include_str!("../src/lib.rs");

    for forbidden_public_marker in [
        "pub const LEGACY_NATIVE_HOST_NAME",
        "pub const LEGACY_BRIDGE_ROOT_NAME",
        "pub fn legacy_bridge_directory",
    ] {
        assert!(
            !production.contains(forbidden_public_marker),
            "default browser registrar API must not expose legacy .NET native host/root helper {forbidden_public_marker}"
        );
    }
    assert!(
        production.contains("pub const NATIVE_HOST_NAME: &str = RUST_NATIVE_HOST_NAME;"),
        "default browser registrar should alias the native messaging host to the rs host"
    );
    assert!(
        production.contains("pub const DEFAULT_BRIDGE_ROOT_NAME: &str = RUST_BRIDGE_ROOT_NAME;"),
        "default browser registrar should alias the bridge root to EasydictRs"
    );
    assert!(
        production.contains("value.eq_ignore_ascii_case(LEGACY_BRIDGE_ROOT_NAME)"),
        "browser registrar should still reject the legacy dotnet bridge root internally"
    );

    for forbidden_app_call in [
        "LEGACY_NATIVE_HOST_NAME",
        "LEGACY_BRIDGE_ROOT_NAME",
        "legacy_bridge_directory",
        "com.easydict.bridge",
    ] {
        assert!(
            !production_source(app_root).contains(forbidden_app_call),
            "default app runtime must not call or embed legacy browser bridge marker {forbidden_app_call}"
        );
    }
}

#[test]
fn default_runtime_policy_implementation_is_lib_owned() {
    let runtime_policy = include_str!("../src/runtime_policy.rs");

    assert!(
        runtime_policy.contains("pub use easydict_runtime_guards::"),
        "app runtime_policy module should be a thin re-export over lib/easydict-runtime-guards"
    );
    for forbidden_inline_implementation in [
        "std::env::var",
        "fn runtime_profile_from_environment",
        "fn runtime_profile_from_value",
        "fn environment_flag_is_enabled",
    ] {
        assert!(
            !runtime_policy.contains(forbidden_inline_implementation),
            "app runtime_policy module must not re-inline retained worker policy implementation marker {forbidden_inline_implementation}"
        );
    }
}

#[test]
fn default_rs_app_embeds_service_icons_from_crate_owned_resources() {
    let src_dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
    for path in rust_source_files_under(&src_dir) {
        let relative_path = relative_slash_path(&src_dir, &path);
        let source = fs::read_to_string(&path)
            .unwrap_or_else(|error| panic!("failed to read {relative_path}: {error}"));

        assert!(
            !production_source(&source).contains("dotnet/src/Easydict.WinUI"),
            "{relative_path} should not load rs app assets from the dotnet project tree"
        );
    }

    let icon_dir = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("resources")
        .join("service-icons");
    for expected_icon in [
        "Bing.scale-100.png",
        "BuiltInAI.scale-100.png",
        "Caiyun.scale-100.png",
        "CustomOpenAI.scale-100.png",
        "DeepL.scale-100.png",
        "DeepSeek.scale-100.png",
        "Doubao.scale-100.png",
        "Gemini.scale-100.png",
        "GitHubOnLight.scale-100.png",
        "Google.scale-100.png",
        "Groq.scale-100.png",
        "Linguee.scale-100.png",
        "NiuTrans.scale-100.png",
        "Ollama.scale-100.png",
        "OpenAI.scale-100.png",
        "Volcano.scale-100.png",
        "windows-local-ai.scale-100.png",
        "Youdao.scale-100.png",
        "Zhipu.scale-100.png",
    ] {
        let path = icon_dir.join(expected_icon);
        assert!(
            path.is_file(),
            "rs app should own service icon resource {}",
            path.display()
        );
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
        app_manifest.contains(
            "\nretained-dotnet-workers = [\"easydict_runtime_guards/retained-dotnet-workers\"]\n"
        ),
        "retained-dotnet-workers must remain an explicit opt-in feature that only forwards the lib-owned runtime policy feature"
    );
    assert!(
        !preview_manifest.contains("retained-dotnet-workers"),
        "easydict_preview_iced must not enable retained .NET worker features implicitly"
    );
}

#[test]
fn default_app_manifest_disables_auto_discovered_binary_entrypoints() {
    let manifest_dir = Path::new(env!("CARGO_MANIFEST_DIR"));
    let app_manifest = include_str!("../Cargo.toml");
    let expected_bins = [
        ("easydict_app", "src/main.rs"),
        (
            "easydict_browser_registrar",
            "src/bin/easydict_browser_registrar.rs",
        ),
        ("easydict_cli", "src/bin/easydict_cli.rs"),
        (
            "easydict-native-bridge",
            "src/bin/easydict_native_bridge.rs",
        ),
        ("easydict-lex-index", "src/bin/easydict_lex_index.rs"),
        ("easydict_long_doc", "src/bin/easydict_long_doc.rs"),
    ];

    assert!(
        app_manifest.contains("\nautobins = false\n"),
        "easydict_app must disable Cargo autobins so new src/bin files cannot become default rs entrypoints implicitly"
    );
    for (name, path) in expected_bins.iter() {
        assert!(
            app_manifest.contains(&format!("name = \"{name}\""))
                && app_manifest.contains(&format!("path = \"{path}\"")),
            "easydict_app manifest should explicitly allow bin {name} at {path}"
        );
    }

    let mut actual_src_bin_files = fs::read_dir(manifest_dir.join("src/bin"))
        .expect("src/bin should be readable")
        .map(|entry| {
            entry
                .expect("src/bin entry should be readable")
                .file_name()
                .to_string_lossy()
                .replace('\\', "/")
        })
        .filter(|name| name.ends_with(".rs"))
        .collect::<Vec<_>>();
    actual_src_bin_files.sort();

    let mut expected_src_bin_files = expected_bins
        .iter()
        .filter_map(|(_, path)| path.strip_prefix("src/bin/"))
        .map(str::to_string)
        .collect::<Vec<_>>();
    expected_src_bin_files.sort();

    assert_eq!(
        actual_src_bin_files, expected_src_bin_files,
        "every src/bin/*.rs file must be reviewed and listed explicitly in Cargo.toml"
    );
}

#[test]
fn default_app_keeps_foundry_sdk_adapter_inside_foundry_local_lib() {
    let app_manifest = include_str!("../Cargo.toml");
    assert!(
        app_manifest.contains(
            "easydict_foundry_local = { path = \"../../../lib/easydict-foundry-local\" }"
        ),
        "easydict_app should depend on the Foundry Local lib without enabling sdk features"
    );
    for forbidden_marker in [
        "foundry-local-sdk",
        "foundry_local_sdk",
        "features = [\"sdk\"",
        "features = [\"sdk-winml\"",
    ] {
        assert!(
            !app_manifest.contains(forbidden_marker),
            "easydict_app must not enable or depend on the Foundry Local SDK directly: {forbidden_marker}"
        );
    }

    let src_dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
    for path in rust_source_files_under(&src_dir) {
        let relative_path = relative_slash_path(&src_dir, &path);
        let source = fs::read_to_string(&path)
            .unwrap_or_else(|error| panic!("failed to read {relative_path}: {error}"));
        for forbidden_marker in ["foundry_local_sdk::", "FoundryLocalSdkProvider"] {
            assert!(
                !production_source(&source).contains(forbidden_marker),
                "{relative_path} must use the lib-owned Foundry traits instead of directly wrapping SDK marker {forbidden_marker}"
            );
        }
    }
}

#[test]
fn default_foundry_local_sdk_features_remain_lib_only_and_not_enabled_by_app_manifests() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("app crate should live under rs/crates/easydict_app");
    let foundry_manifest_path = repo_root.join("lib/easydict-foundry-local/Cargo.toml");
    let foundry_manifest = fs::read_to_string(&foundry_manifest_path).unwrap_or_else(|error| {
        panic!(
            "failed to read {}: {error}",
            foundry_manifest_path.display()
        )
    });
    assert!(
        foundry_manifest.contains("\n[features]\ndefault = []\n"),
        "Foundry Local SDK adapter must remain opt-in in the lib manifest"
    );
    assert!(
        foundry_manifest.contains("foundry-local-sdk = {")
            && foundry_manifest.contains("optional = true"),
        "foundry-local-sdk dependency must remain optional and lib-owned"
    );
    assert!(
        foundry_manifest.contains("sdk = [\"dep:foundry-local-sdk\", \"dep:tokio\"]"),
        "Foundry Local SDK feature should stay explicit in the lib manifest"
    );
    assert!(
        foundry_manifest.contains("sdk-winml = [\"sdk\", \"foundry-local-sdk/winml\"]"),
        "Foundry Local WinML SDK feature should stay explicit in the lib manifest"
    );

    for manifest_path in cargo_manifest_files_under(repo_root) {
        let relative_path = relative_slash_path(repo_root, &manifest_path);
        let manifest = fs::read_to_string(&manifest_path)
            .unwrap_or_else(|error| panic!("failed to read {relative_path}: {error}"));
        if relative_path == "lib/easydict-foundry-local/Cargo.toml" {
            continue;
        }
        assert!(
            !manifest.contains("foundry-local-sdk"),
            "{relative_path} must not depend on the Foundry SDK directly"
        );
        if manifest.contains("easydict_foundry_local") {
            for forbidden_feature in [
                "features = [\"sdk\"",
                "features=[\"sdk\"",
                "features = [\"sdk-winml\"",
                "features=[\"sdk-winml\"",
            ] {
                assert!(
                    !manifest.contains(forbidden_feature),
                    "{relative_path} must not enable Foundry SDK features on the default app/package path"
                );
            }
        }
    }
}

#[test]
fn default_app_uses_lib_owned_foundry_runtime_controller_factory() {
    let src_dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
    let mut factory_mentions = Vec::new();
    for path in rust_source_files_under(&src_dir) {
        let relative_path = relative_slash_path(&src_dir, &path);
        let source = fs::read_to_string(&path)
            .unwrap_or_else(|error| panic!("failed to read {relative_path}: {error}"));
        let production = production_source(&source);

        assert!(
            !production.contains("CommandFoundryLocalEndpointResolver::default()"),
            "{relative_path} must construct the default Foundry runtime through the lib-owned factory"
        );
        if production.contains("default_foundry_local_runtime_controller") {
            factory_mentions.push(relative_path);
        }
    }

    for expected_path in [
        "bin/easydict_cli.rs",
        "lib.rs",
        "long_document.rs",
        "openai_compatible.rs",
        "quick_translate.rs",
    ] {
        assert!(
            factory_mentions.iter().any(|path| path == expected_path),
            "{expected_path} should route default Foundry runtime construction through the lib-owned factory"
        );
    }
}

#[test]
fn default_app_manifests_do_not_link_hybrid_packaging_or_dotnet_runtime_tools() {
    let app_manifest = include_str!("../Cargo.toml");
    let preview_manifest = include_str!("../../easydict_preview_iced/Cargo.toml");

    for (manifest_name, manifest) in [
        ("easydict_app/Cargo.toml", app_manifest),
        ("easydict_preview_iced/Cargo.toml", preview_manifest),
    ] {
        for forbidden_dependency in [
            "easydict_packager",
            "easydict_msix_validate",
            "easydict_encrypt_secret",
            "easydict_icon_generator",
            "easydict_store_listings",
            "easydict_ui_parity_analyzer",
            "win_fluent_platform_win",
        ] {
            assert!(
                !manifest.contains(forbidden_dependency),
                "{manifest_name} must not link hybrid packaging/tooling crate {forbidden_dependency} into the default rs app"
            );
        }
        for forbidden_marker in [
            "extract-dotnet-runtime",
            "Extract-DotnetRuntime",
            "Package-Msix",
            "Build-RustHelpers",
            "Compress-Archive",
        ] {
            assert!(
                !manifest.contains(forbidden_marker),
                "{manifest_name} must not mention hybrid packaging/runtime marker {forbidden_marker}"
            );
        }
    }
}

#[test]
fn default_winfluent_legacy_powershell_features_stay_explicit_and_disabled_for_preview() {
    let preview_manifest = include_str!("../../easydict_preview_iced/Cargo.toml");
    let backend_manifest =
        include_str!("../../../../lib/winfluent-rs/crates/win_fluent_backend_iced/Cargo.toml");
    let platform_manifest =
        include_str!("../../../../lib/winfluent-rs/crates/win_fluent_platform_win/Cargo.toml");

    assert!(
        preview_manifest.contains(
            "win_fluent_backend_iced = { path = \"../../../lib/winfluent-rs/crates/win_fluent_backend_iced\", default-features = false }"
        ),
        "first rs portable preview binary must disable win_fluent_backend_iced default features"
    );
    assert!(
        !preview_manifest.contains("legacy-powershell-dialogs")
            && !preview_manifest.contains("legacy-powershell-tts"),
        "easydict_preview_iced must not opt into legacy PowerShell dialog/TTS features"
    );

    assert!(
        backend_manifest.contains("\n[features]\ndefault = []\n"),
        "win_fluent_backend_iced default features must stay empty"
    );
    assert!(
        backend_manifest.contains("legacy-powershell-dialogs = []")
            && backend_manifest.contains(
                "legacy-powershell-tts = [\"win_fluent_platform_win/legacy-powershell-tts\"]"
            ),
        "WinFluent legacy PowerShell dialog/TTS backends must remain explicit feature opt-ins"
    );
    assert!(
        backend_manifest.contains(
            "win_fluent_platform_win = { path = \"../win_fluent_platform_win\", default-features = false }"
        ),
        "backend should link the Windows platform adapter with default features disabled"
    );

    assert!(
        platform_manifest.contains("\n[features]\ndefault = []\n"),
        "win_fluent_platform_win default features must stay empty"
    );
    assert!(
        platform_manifest.contains("legacy-powershell-tts = []"),
        "platform PowerShell TTS backend must remain an explicit feature opt-in"
    );
}

#[test]
fn default_manifests_do_not_opt_into_retained_or_legacy_runtime_features() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("app crate should live under rs/crates/easydict_app");

    for manifest_path in cargo_manifest_files_under(repo_root) {
        let relative_path = relative_slash_path(repo_root, &manifest_path);
        let manifest = fs::read_to_string(&manifest_path)
            .unwrap_or_else(|error| panic!("failed to read {relative_path}: {error}"));

        for (index, line) in manifest.lines().enumerate() {
            let line_number = index + 1;
            let trimmed = line.trim();
            if retained_or_legacy_feature_definition_is_allowed(&relative_path, trimmed) {
                continue;
            }
            for forbidden_feature in [
                "legacy-powershell-dialogs",
                "legacy-powershell-tts",
                "retained-dotnet-workers",
                "hybrid-dotnet-runtime-packaging",
            ] {
                assert!(
                    !trimmed.contains(forbidden_feature),
                    "{relative_path}:{line_number} must not opt into retained or legacy runtime feature {forbidden_feature}: {trimmed}"
                );
            }
        }
    }
}

#[test]
fn rs_crate_manifests_disable_winfluent_default_features() {
    let repo_root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("app crate should live under rs/crates/easydict_app");
    let rs_crates_root = repo_root.join("rs").join("crates");

    for manifest_path in cargo_manifest_files_under(&rs_crates_root) {
        let relative_path = relative_slash_path(repo_root, &manifest_path);
        let manifest = fs::read_to_string(&manifest_path)
            .unwrap_or_else(|error| panic!("failed to read {relative_path}: {error}"));

        for (line_index, line) in manifest.lines().enumerate() {
            let trimmed = line.trim();
            for dependency_name in ["win_fluent_backend_iced", "win_fluent_platform_win"] {
                let dependency_prefix = format!("{dependency_name} = ");
                if !trimmed.starts_with(&dependency_prefix) {
                    continue;
                }

                assert!(
                    trimmed.contains("default-features = false"),
                    "{}:{} must keep {dependency_name} default features disabled for the first rs portable build: {trimmed}",
                    relative_path,
                    line_index + 1
                );
            }
        }
    }
}

#[test]
fn default_process_spawn_surface_only_allows_foundry_local_cli_boundary() {
    let src_dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
    for path in rust_source_files_under(&src_dir) {
        let relative_path = relative_slash_path(&src_dir, &path);
        let source = fs::read_to_string(&path)
            .unwrap_or_else(|error| panic!("failed to read {relative_path}: {error}"));

        if is_retained_dotnet_worker_source(&relative_path) {
            assert_retained_worker_module_is_feature_gated(&relative_path);
            continue;
        }

        assert_no_process_spawn_entry_points(&relative_path, production_source(&source));
    }

    let foundry_local = include_str!("../../../../lib/easydict-foundry-local/src/lib.rs");
    assert_foundry_local_process_spawn_is_cli_only(
        "lib/easydict-foundry-local/src/lib.rs",
        production_source(foundry_local),
    );
}

#[test]
fn default_process_spawn_surface_has_no_retained_dotnet_runtime_entries() {
    let src_dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
    for path in rust_source_files_under(&src_dir) {
        let relative_path = relative_slash_path(&src_dir, &path);
        let source = fs::read_to_string(&path)
            .unwrap_or_else(|error| panic!("failed to read {relative_path}: {error}"));

        if is_retained_dotnet_worker_source(&relative_path) {
            assert_retained_worker_module_is_feature_gated(&relative_path);
            continue;
        }

        assert_no_retained_dotnet_runtime_entry_markers(&relative_path, production_source(&source));
    }

    let crate_root = include_str!("../src/lib.rs");
    assert!(
        !crate_root.contains("mod retained_workers;"),
        "default rs crate root should use neutral runtime_policy module, not compile a retained_workers module"
    );
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

#[test]
fn default_bundled_helper_process_boundary_stays_inside_windows_shell_lib() {
    let source = include_str!("../../../../lib/easydict-windows-shell/src/lib.rs");
    let production = production_source(source);

    assert!(
        production.contains("pub fn run_bundled_executable("),
        "Windows shell lib should own the app's bundled-helper launch boundary"
    );
    assert!(
        production.contains("fn validate_bundled_executable_name("),
        "bundled-helper launch should validate helper names before resolving next to the app exe"
    );
    assert!(
        production.contains("fn validate_bundled_executable_target("),
        "bundled-helper launch should validate the resolved target before spawning"
    );
    assert!(
        production.contains(
            "easydict_runtime_guards::command_target_is_retained_runtime_or_script_marker(executable_name)"
        ),
        "bundled-helper names should delegate retained runtime/script detection to lib/easydict-runtime-guards"
    );
    assert!(
        production.contains("fs::symlink_metadata(executable)"),
        "bundled-helper target validation should inspect link/reparse metadata before spawn"
    );
    assert!(
        production.contains("file_type.is_symlink()")
            && production.contains("bundled_executable_target_is_reparse_point(&metadata)"),
        "bundled-helper target validation should reject symlinks and Windows reparse points"
    );
    assert!(
        production.contains("easydict_runtime_guards::bytes_contain_retained_runtime_marker(&bytes)"),
        "bundled-helper target validation should scan helper bytes for retained .NET/script markers"
    );

    let command_lines: Vec<_> = non_comment_lines(production)
        .filter(|(_, line)| line.contains("Command::new("))
        .collect();
    assert_eq!(
        command_lines.len(),
        1,
        "Windows shell lib should keep a single process-spawn boundary for bundled Rust helpers"
    );
    let (line_number, line) = command_lines[0];
    assert!(
        line.contains("Command::new(executable)"),
        "lib/easydict-windows-shell/src/lib.rs:{line_number} should spawn only the already-validated bundled helper path"
    );

    let validation_offset = production
        .find("validate_bundled_executable_target(executable)?")
        .expect("run_executable should validate target before spawn");
    let spawn_offset = production
        .find("Command::new(executable)")
        .expect("run_executable should contain the bundled helper spawn");
    assert!(
        validation_offset < spawn_offset,
        "bundled-helper target validation must run before Command::new"
    );
}

#[test]
fn default_shell_open_url_boundary_rejects_non_web_and_retained_targets() {
    let source = include_str!("../../../../lib/easydict-windows-shell/src/lib.rs");
    let production = production_source(source);

    assert!(
        production.contains("fn validate_open_url_target("),
        "Windows shell lib should validate URL targets before ShellExecuteW"
    );
    assert!(
        production.contains("lower.starts_with(\"https://\") || lower.starts_with(\"http://\")"),
        "open_url should only allow web URL schemes on the default rs shell boundary"
    );
    assert!(
        production.contains(
            "easydict_runtime_guards::command_target_is_retained_runtime_or_script_marker(trimmed)"
        ),
        "open_url should reject retained runtime/script markers through lib/easydict-runtime-guards"
    );

    let validation_offset = production
        .find("let url = validate_open_url_target(url)?")
        .expect("open_url should validate its target");
    let shell_offset = production
        .find("platform::open_url(url)")
        .expect("open_url should delegate to the platform wrapper");
    assert!(
        validation_offset < shell_offset,
        "open_url target validation must run before ShellExecuteW delegation"
    );
}

#[test]
fn default_desktop_registry_command_boundary_scans_targets_before_registry_writes() {
    let source = include_str!("../src/desktop_integration.rs");
    let production = production_source(source);

    assert!(
        production.contains("command_target_is_retained_runtime_or_script_marker(executable_path)"),
        "desktop integration should reject retained runtime/script command targets by path"
    );
    assert!(
        production.contains("fs::symlink_metadata(executable)"),
        "desktop integration should inspect shell/protocol/startup command target metadata"
    );
    assert!(
        production.contains("desktop_command_target_is_reparse_point(&metadata)"),
        "desktop integration should reject reparse-point command targets before registry writes"
    );
    assert!(
        production
            .contains("easydict_runtime_guards::bytes_contain_retained_runtime_marker(&bytes)"),
        "desktop integration should scan command target bytes for retained .NET runtime markers"
    );

    for register_fn in [
        "register_shell_verb_with_executable_path",
        "register_protocol_with_executable_path",
        "register_startup_with_executable_path",
    ] {
        let function_declaration = format!("pub fn {register_fn}");
        let section = production
            .split(&function_declaration)
            .nth(1)
            .unwrap_or_else(|| panic!("desktop integration should define {register_fn}"));
        let validation_offset = section
            .find("validate_desktop_command_executable_path(executable_path)?")
            .unwrap_or_else(|| panic!("{register_fn} should validate command target"));
        let registry_write_offset = section
            .find("write_registry_string")
            .unwrap_or_else(|| panic!("{register_fn} should write registry values"));
        assert!(
            validation_offset < registry_write_offset,
            "{register_fn} must validate command target before registry writes"
        );
    }
}

#[test]
fn production_source_scan_continues_past_early_cfg_test_items() {
    let long_document = include_str!("../src/long_document.rs");
    let production = production_source(long_document);

    assert!(
        production.contains("run_long_document_request_with_app_dir_after_native_probe"),
        "default boundary scans must not stop at early #[cfg(test)] imports/helpers"
    );
    assert!(
        !production.contains("selected_pdf_page_indexes_matches_compat_page_range_parser"),
        "default boundary scans should still exclude the trailing #[cfg(test)] mod tests block"
    );
}

#[test]
fn startup_activation_core_stays_decoupled_from_winfluent_task() {
    let activation = include_str!("../src/activation.rs");

    for forbidden in ["win_fluent", "Task<", "Task::"] {
        assert!(
            !activation.contains(forbidden),
            "startup activation parsing should stay pure Rust app core and let lib.rs wrap messages into WinFluent tasks; found {forbidden}"
        );
    }
}

#[test]
fn default_text_selection_terminal_smoke_helper_uses_non_shell_terminal_name() {
    let text_selection_tests = include_str!("text_selection_behavior.rs");

    assert!(
        text_selection_tests.contains("temp_dir.join(\"WindowsTerminal.exe\")"),
        "terminal smoke test should use a terminal-classified helper name without borrowing a shell runtime name"
    );
    for forbidden_helper in [
        "temp_dir.join(\"pwsh.exe\")",
        "temp_dir.join(\"powershell.exe\")",
    ] {
        assert!(
            !text_selection_tests.contains(forbidden_helper),
            "terminal smoke test must not copy the test binary to shell runtime helper name {forbidden_helper}"
        );
    }
}

#[test]
fn default_integration_tests_do_not_spawn_retained_runtime_or_shell_helpers() {
    let tests_dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests");
    for path in rust_source_files_under(&tests_dir) {
        let relative_path = relative_slash_path(&tests_dir, &path);
        if matches!(
            relative_path.as_str(),
            "compat_client.rs" | "default_api_boundary_behavior.rs"
        ) {
            continue;
        }

        let source = fs::read_to_string(&path)
            .unwrap_or_else(|error| panic!("failed to read {relative_path}: {error}"));
        let lines: Vec<_> = source.lines().collect();
        for (index, line) in lines.iter().enumerate() {
            let line_number = index + 1;
            if line.contains("Command::new(")
                && !line.contains("WorkerCommand::new(")
                && !default_test_process_spawn_is_allowlisted(&relative_path, line)
            {
                panic!(
                    "{relative_path}:{line_number} must not add an unreviewed default-test process spawn: {line}"
                );
            }

            if line.contains("WorkerCommand::new(")
                && !line_is_near_retained_worker_feature_gate(&lines, index)
            {
                panic!(
                    "{relative_path}:{line_number} must keep WorkerCommand test spawns behind retained-dotnet-workers cfg: {line}"
                );
            }
        }
    }
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

fn retained_or_legacy_feature_definition_is_allowed(relative_path: &str, trimmed: &str) -> bool {
    matches!(
        (relative_path, trimmed),
        (
            "lib/winfluent-rs/crates/win_fluent_backend_iced/Cargo.toml",
            "legacy-powershell-dialogs = []"
        ) | (
            "lib/winfluent-rs/crates/win_fluent_backend_iced/Cargo.toml",
            "legacy-powershell-tts = [\"win_fluent_platform_win/legacy-powershell-tts\"]"
        ) | (
            "lib/winfluent-rs/crates/win_fluent_platform_win/Cargo.toml",
            "legacy-powershell-tts = []"
        ) | (
            "lib/easydict-runtime-guards/Cargo.toml",
            "retained-dotnet-workers = []"
        ) | (
            "rs/crates/easydict_app/Cargo.toml",
            "retained-dotnet-workers = [\"easydict_runtime_guards/retained-dotnet-workers\"]"
        ) | (
            "rs/crates/easydict_packager/Cargo.toml",
            "hybrid-dotnet-runtime-packaging = [\"dep:reqwest\"]"
        )
    )
}

fn cli_legacy_option_sections(source: &str, option: &str) -> Vec<String> {
    let needle = format!("\"{option}\" => {{");
    let lines: Vec<_> = source.lines().collect();
    let mut sections = Vec::new();

    for start in lines
        .iter()
        .enumerate()
        .filter_map(|(index, line)| line.contains(&needle).then_some(index))
    {
        let mut depth = 0isize;
        let mut section = String::new();
        for line in &lines[start..] {
            section.push_str(line);
            section.push('\n');
            depth += line.chars().filter(|character| *character == '{').count() as isize;
            depth -= line.chars().filter(|character| *character == '}').count() as isize;
            if depth == 0 {
                break;
            }
        }
        sections.push(section);
    }

    assert_eq!(
        sections.len(),
        2,
        "cli_translate.rs should have exactly two parse arms for {option}: inline and split option forms"
    );
    sections
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
        "dotnet run",
        "dotnet publish",
        "PowerShell",
        "powershell",
        "pwsh",
        "wscript",
        "cscript",
        "mshta",
        "WScript.Shell",
        "VBScript",
        "JScript",
        ".ps1",
        ".vbs",
        ".vbe",
        ".jse",
        ".wsf",
        ".wsh",
        ".hta",
        "Compress-Archive",
        "BrowserHostRegistrar",
        "Easydict NativeBridge",
        "Easydict.NativeBridge",
        "Easydict.BrowserRegistrar",
        "NativeBridge.csproj",
        "BrowserRegistrar.csproj",
        "host\\fxr",
        "host/fxr",
        "hostfxr",
    ] {
        if let Some((line_number, _)) = non_comment_lines(source).find(|(_, line)| {
            line.contains(marker) && !is_text_selection_terminal_classifier_line(path, line, marker)
        }) {
            panic!(
                "{path}:{line_number} must not expose retained .NET runtime/worker or legacy script/helper marker {marker:?} on the default rs surface"
            );
        }
    }
}

fn assert_no_process_spawn_entry_points(path: &str, source: &str) {
    for marker in [
        "std::process::Command",
        "process::Command",
        "Command::new(",
        "WorkerCommand::new(",
    ] {
        if let Some((line_number, _)) =
            non_comment_lines(source).find(|(_, line)| line.contains(marker))
        {
            panic!(
                "{path}:{line_number} must not start external processes on the default rs surface; marker {marker:?} belongs in a dedicated native library or retained-dotnet-workers module"
            );
        }
    }
}

fn is_text_selection_terminal_classifier_line(path: &str, line: &str, marker: &str) -> bool {
    path == "text_selection.rs"
        && matches!(marker, "powershell" | "pwsh")
        && matches!(line.trim(), "\"powershell\"," | "\"pwsh\",")
}

fn default_test_process_spawn_is_allowlisted(path: &str, line: &str) -> bool {
    let trimmed = line.trim();
    match path {
        "cli_translate_behavior.rs" => {
            trimmed.contains("Command::new(env!(\"CARGO_BIN_EXE_easydict_cli\"))")
        }
        "long_document_cli_behavior.rs" => {
            trimmed.contains("Command::new(binary)")
                || trimmed.contains("Command::new(long_doc_cli_binary_path())")
                || trimmed.contains("Command::new(&copied_cli)")
        }
        "native_bridge_behavior.rs" => trimmed.contains("Command::new(bridge_bin)"),
        "sidecar_ipc_e2e.rs" => {
            trimmed.contains("Command::new(python_executable())")
                || trimmed.contains("Command::new(candidate)")
        }
        "text_selection_behavior.rs" => trimmed.contains("std::process::Command::new(&helper_exe)"),
        _ => false,
    }
}

fn line_is_near_retained_worker_feature_gate(lines: &[&str], index: usize) -> bool {
    let start = index.saturating_sub(8);
    lines[start..=index]
        .iter()
        .any(|line| line.contains("#[cfg(feature = \"retained-dotnet-workers\")]"))
}

fn assert_foundry_local_process_spawn_is_cli_only(path: &str, source: &str) {
    assert_no_foundry_local_runtime_markers_outside_cli_denylist(path, source);
    assert!(
        source.contains("pub const FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE"),
        "{path} should keep the Foundry Local CLI override explicit"
    );
    assert!(
        source.contains("FOUNDRY_LOCAL_DEFAULT_CLI_EXECUTABLE_NAME: &str = \"foundry\""),
        "{path} should default to the native Foundry Local CLI, not a .NET helper"
    );

    let command_lines: Vec<_> = non_comment_lines(source)
        .filter(|(_, line)| line.contains("Command::new("))
        .collect();
    let validation_lines: Vec<_> = non_comment_lines(source)
        .filter(|(_, line)| line.contains("self.validated_cli_executable_for_spawn()?"))
        .collect();
    assert_eq!(
        command_lines.len(),
        2,
        "{path} should only spawn the Foundry Local status/load and service-start CLI commands"
    );
    assert_eq!(
        validation_lines.len(),
        command_lines.len(),
        "{path} should revalidate the Foundry Local CLI target immediately before every spawn"
    );
    for ((validation_line_number, _), (line_number, line)) in
        validation_lines.iter().zip(command_lines.iter())
    {
        assert!(
            validation_line_number < line_number,
            "{path}:{line_number} must validate the configured Foundry Local CLI target before spawning it"
        );
        assert!(
            line.contains("Command::new(executable.as_ref())"),
            "{path}:{line_number} must spawn only the just-validated Foundry Local CLI target"
        );
    }
    assert!(
        source.contains("fn validated_cli_executable_for_spawn("),
        "{path} should keep spawn-time Foundry Local CLI target revalidation explicit"
    );
    assert!(
        source.contains("fn is_retained_dotnet_runtime_or_worker_command"),
        "{path} should keep a denylist for retained runtime/worker CLI overrides"
    );
    assert!(
        source.contains(
            "easydict_runtime_guards::command_target_is_retained_runtime_or_script_marker"
        ),
        "{path} should delegate retained runtime/script command classification to lib/easydict-runtime-guards"
    );
    assert!(
        source.contains("easydict_runtime_guards::bytes_contain_retained_runtime_marker(&bytes)"),
        "{path} should scan the resolved Foundry Local CLI target bytes for retained runtime/script markers before spawning"
    );
    for denied_override in [
        "\"dotnet.exe\"",
        "\"hostfxr.dll\"",
        "easydict.workers.",
        ".runtimeconfig.json",
        "/host/fxr/",
        ".ps1",
        ".vbs",
        ".vbe",
        ".jse",
        ".wsf",
        ".wsh",
        ".hta",
        "wscript.exe",
        "cscript.exe",
        "mshta.exe",
    ] {
        assert!(
            !source.contains(denied_override),
            "{path} should not re-inline retained runtime/script command marker {denied_override}"
        );
    }
}

fn assert_no_foundry_local_runtime_markers_outside_cli_denylist(path: &str, source: &str) {
    for marker in [
        ".NET Runtime",
        ".NET runtime",
        ".net runtime",
        "CompatHost",
        "DOTNET_ROOT",
        "Easydict.Workers",
        "dotnet runtime",
        "dotnet.exe",
        "dotnet run",
        "dotnet publish",
        "PowerShell",
        "powershell",
        "pwsh",
        "wscript",
        "cscript",
        "mshta",
        "WScript.Shell",
        "VBScript",
        "JScript",
        ".ps1",
        ".vbs",
        ".vbe",
        ".jse",
        ".wsf",
        ".wsh",
        ".hta",
        "Compress-Archive",
        "BrowserHostRegistrar",
        "Easydict NativeBridge",
        "Easydict.NativeBridge",
        "Easydict.BrowserRegistrar",
        "NativeBridge.csproj",
        "BrowserRegistrar.csproj",
        "host\\fxr",
        "host/fxr",
        "hostfxr",
        "hostpolicy",
        "coreclr",
        "clrjit",
        "singlefilehost",
    ] {
        if let Some((line_number, line)) = non_comment_lines(source)
            .find(|(_, line)| line.contains(marker) && !is_foundry_local_cli_denylist_line(line))
        {
            panic!(
                "{path}:{line_number} must not expose retained runtime marker {marker:?} outside the Foundry Local CLI override denylist: {line}"
            );
        }
    }
}

fn is_foundry_local_cli_denylist_line(line: &str) -> bool {
    let lower = line.to_ascii_lowercase();
    [
        "\"dotnet\"",
        "\"dotnet.exe\"",
        "\"dotnet.cmd\"",
        "\"dotnet.bat\"",
        "\"dotnet.com\"",
        "\"powershell\"",
        "\"powershell.exe\"",
        "\"powershell.cmd\"",
        "\"powershell.bat\"",
        "\"powershell.com\"",
        "\"pwsh\"",
        "\"pwsh.exe\"",
        "\"pwsh.cmd\"",
        "\"pwsh.bat\"",
        "\"pwsh.com\"",
        "\"wscript\"",
        "\"wscript.exe\"",
        "\"wscript.com\"",
        "\"cscript\"",
        "\"cscript.exe\"",
        "\"cscript.com\"",
        "\"mshta\"",
        "\"mshta.exe\"",
        "\"mshta.com\"",
        "\"hostfxr.dll\"",
        "\"hostpolicy.dll\"",
        "\"coreclr.dll\"",
        "\"clrjit.dll\"",
        "\"singlefilehost.exe\"",
        "\"system.private.corelib.dll\"",
        "easydict.compathost",
        "easydict.workers.",
        ".runtimeconfig.json",
        "/host/fxr/",
        ".ps1",
        ".vbs",
        ".vbe",
        ".jse",
        ".wsf",
        ".wsh",
        ".hta",
    ]
    .iter()
    .any(|marker| lower.contains(marker))
}

fn assert_retained_worker_module_is_feature_gated(path: &str) {
    let source_path = Path::new(env!("CARGO_MANIFEST_DIR")).join("src").join(path);
    let source = fs::read_to_string(&source_path).unwrap_or_else(|error| {
        panic!(
            "failed to read retained worker source {}: {error}",
            source_path.display()
        )
    });
    assert!(
        source
            .trim_start()
            .starts_with("#![cfg(feature = \"retained-dotnet-workers\")]"),
        "{path} must carry its own retained-dotnet-workers file-level cfg"
    );

    let crate_root = include_str!("../src/lib.rs");
    let module_name = path
        .strip_suffix(".rs")
        .expect("retained worker source path should be a Rust module");
    let module_declaration = format!("pub mod {module_name};");
    assert_crate_root_export_is_retained_worker_feature_gated(crate_root, &module_declaration);
}

fn is_retained_dotnet_worker_source(path: &str) -> bool {
    matches!(path, "compat_client.rs" | "compat_protocol.rs")
}

fn rust_source_files_under(root: &Path) -> Vec<PathBuf> {
    let mut files = Vec::new();
    collect_rust_source_files(root, &mut files);
    files.sort();
    files
}

fn cargo_manifest_files_under(root: &Path) -> Vec<PathBuf> {
    let mut files = Vec::new();
    collect_cargo_manifest_files(root, &mut files);
    files.sort();
    files
}

fn collect_cargo_manifest_files(root: &Path, files: &mut Vec<PathBuf>) {
    let Some(name) = root.file_name().and_then(|value| value.to_str()) else {
        return;
    };
    if matches!(name, ".git" | "target") {
        return;
    }

    for entry in fs::read_dir(root).unwrap_or_else(|error| {
        panic!(
            "failed to read manifest directory {}: {error}",
            root.display()
        )
    }) {
        let path = entry
            .unwrap_or_else(|error| panic!("failed to read manifest directory entry: {error}"))
            .path();
        if path.is_dir() {
            collect_cargo_manifest_files(&path, files);
        } else if path.file_name().and_then(|value| value.to_str()) == Some("Cargo.toml") {
            files.push(path);
        }
    }
}

fn collect_rust_source_files(root: &Path, files: &mut Vec<PathBuf>) {
    for entry in fs::read_dir(root).unwrap_or_else(|error| {
        panic!(
            "failed to read source directory {}: {error}",
            root.display()
        )
    }) {
        let path = entry
            .unwrap_or_else(|error| panic!("failed to read source directory entry: {error}"))
            .path();
        if path.is_dir() {
            collect_rust_source_files(&path, files);
        } else if path.extension().and_then(|value| value.to_str()) == Some("rs") {
            files.push(path);
        }
    }
}

fn relative_slash_path(base: &Path, path: &Path) -> String {
    path.strip_prefix(base)
        .unwrap_or(path)
        .to_string_lossy()
        .replace('\\', "/")
}

fn production_source(source: &str) -> &str {
    let mut pending_cfg_test_line_start = None;
    let mut line_start = 0;

    for line in source.split_inclusive('\n') {
        let trimmed = line.trim();
        if trimmed == "#[cfg(test)]" {
            pending_cfg_test_line_start = Some(line_start);
        } else if trimmed.starts_with("mod tests") {
            if let Some(start) = pending_cfg_test_line_start {
                return &source[..start];
            }
            pending_cfg_test_line_start = None;
        } else if !trimmed.is_empty() && !trimmed.starts_with("#[") {
            pending_cfg_test_line_start = None;
        }

        line_start += line.len();
    }

    source
}

fn non_comment_lines(source: &str) -> impl Iterator<Item = (usize, &str)> {
    source.lines().enumerate().filter_map(|(index, line)| {
        let trimmed = line.trim_start();
        (!trimmed.starts_with("//")).then_some((index + 1, line))
    })
}

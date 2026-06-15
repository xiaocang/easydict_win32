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
        "dotnet run",
        "dotnet publish",
        "PowerShell",
        "powershell",
        "pwsh",
        ".ps1",
        "Compress-Archive",
        "Easydict.NativeBridge",
        "Easydict.BrowserRegistrar",
        "NativeBridge.csproj",
        "BrowserRegistrar.csproj",
        "host\\fxr",
        "host/fxr",
        "hostfxr",
    ] {
        if let Some((line_number, _)) =
            non_comment_lines(source).find(|(_, line)| line.contains(marker))
        {
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
    assert_eq!(
        command_lines.len(),
        2,
        "{path} should only spawn the Foundry Local status/load and service-start CLI commands"
    );
    for (line_number, line) in command_lines {
        assert!(
            line.contains("Command::new(&self.executable_name)"),
            "{path}:{line_number} must spawn only the configured Foundry Local CLI boundary"
        );
    }
    assert!(
        source.contains("fn is_retained_dotnet_runtime_or_worker_command"),
        "{path} should keep a denylist for retained runtime/worker CLI overrides"
    );
    for denied_override in [
        "\"dotnet.exe\"",
        "\"powershell.exe\"",
        "\"pwsh.exe\"",
        "\"hostfxr.dll\"",
        "\"hostpolicy.dll\"",
        "\"coreclr.dll\"",
        "\"clrjit.dll\"",
        "\"singlefilehost.exe\"",
        "easydict.compathost",
        "easydict.workers.",
        ".runtimeconfig.json",
        "/host/fxr/",
        ".ps1",
    ] {
        assert!(
            source.contains(denied_override),
            "{path} should reject Foundry Local CLI overrides containing {denied_override}"
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
        ".ps1",
        "Compress-Archive",
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
        "\"powershell\"",
        "\"powershell.exe\"",
        "\"pwsh\"",
        "\"pwsh.exe\"",
        "\"hostfxr.dll\"",
        "\"hostpolicy.dll\"",
        "\"coreclr.dll\"",
        "\"clrjit.dll\"",
        "\"singlefilehost.exe\"",
        "easydict.compathost",
        "easydict.workers.",
        ".runtimeconfig.json",
        "/host/fxr/",
        ".ps1",
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

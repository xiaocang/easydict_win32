use easydict_packager::{
    build_rust_helpers, pack_rs_portable, validate_rs_portable_payload, BuildRustHelpersOptions,
    PackRustPortableOptions, ValidateRustPortableOptions,
};
use std::ffi::OsString;
use std::fs;
use std::io::BufReader;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use zip::ZipArchive;

static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

#[test]
fn rs_portable_release_path_forces_rust_only_runtime_profile() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let portable_script = read_text(&root.join("rs/scripts/Package-Portable.ps1"));

    assert_contains(
        &workflow,
        "default: 'rs-portable'",
        "release workflow should default to rs portable, not hybrid",
    );
    assert_contains(
        &workflow,
        "publish-rs-portable:",
        "release workflow should keep a dedicated rs portable job",
    );
    assert_contains(
        &workflow,
        "EASYDICT_RUNTIME_PROFILE: rust-only",
        "rs portable workflow job should force Easydict runtime profile",
    );
    assert_contains(
        &workflow,
        "RUNTIME_PROFILE: rust-only",
        "rs portable workflow job should force generic runtime profile",
    );
    assert_contains(
        &workflow,
        "Package-Portable.ps1",
        "workflow should use the Rust portable packaging shim",
    );
    assert_contains(
        &workflow,
        "validate-rs-portable",
        "workflow should validate the staged/ZIP rs portable payload",
    );
    assert_contains(
        &workflow,
        "RETAINED_WORKERS_ENABLED=true",
        "workflow should mark retained worker steps only after explicit hybrid validation",
    );
    assert!(
        !workflow.contains("if: env.RUNTIME_PROFILE != 'rust-only'"),
        "release workflow should not use negative rust-only checks for retained worker/runtime steps"
    );

    assert_contains(
        &portable_script,
        "$env:EASYDICT_RUNTIME_PROFILE = \"rust-only\"",
        "portable shim should force Easydict runtime profile before invoking cargo",
    );
    assert_contains(
        &portable_script,
        "$env:RUNTIME_PROFILE = \"rust-only\"",
        "portable shim should force generic runtime profile before invoking cargo",
    );
    assert_contains(
        &portable_script,
        "pack-rs-portable",
        "portable shim should delegate staging and validation to the Rust packager",
    );
    assert_contains(
        &portable_script,
        "Remove-Item Env:EASYDICT_RUNTIME_PROFILE",
        "portable shim should restore absent Easydict runtime profile after packaging",
    );
    assert_contains(
        &portable_script,
        "Remove-Item Env:RUNTIME_PROFILE",
        "portable shim should restore absent generic runtime profile after packaging",
    );
}

#[test]
fn release_workflow_default_tag_path_runs_only_rs_portable_jobs_and_gates_hybrid_assets() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let integration_tests_job = text_between(&workflow, "  integration-tests:", "  publish-msix:");
    let publish_msix_job = text_between(&workflow, "  publish-msix:", "  publish-rs-portable:");
    let publish_rs_portable_job =
        text_between(&workflow, "  publish-rs-portable:", "  create-bundle:");
    let create_bundle_job = text_between(
        &workflow,
        "  create-bundle:",
        "  create-rs-portable-release:",
    );
    let create_rs_portable_release_job = text_between(
        &workflow,
        "  create-rs-portable-release:",
        "  publish-winget:",
    );
    let publish_winget_job = workflow
        .split_once("  publish-winget:")
        .unwrap_or_else(|| panic!("missing section start: publish-winget"))
        .1;

    assert_contains(
        &workflow,
        "default: 'rs-portable'",
        "release workflow dispatch should default to the rs portable artifact set",
    );
    assert_contains(
        &workflow,
        "RELEASE_FLAVOR: ${{ github.event.inputs.release_flavor || 'rs-portable' }}",
        "tag-triggered releases should normalize the absent release flavor to rs-portable",
    );

    let publish_rs_portable_header =
        text_between(publish_rs_portable_job, "    name:", "    steps:");
    assert_not_contains(
        publish_rs_portable_header,
        "\n    if:",
        "publish-rs-portable should be scheduled on the default/tag path",
    );
    assert_contains(
        publish_rs_portable_job,
        "needs: prepare",
        "publish-rs-portable should only depend on the shared version preparation job",
    );
    assert_contains(
        create_rs_portable_release_job,
        "if: ${{ (github.event.inputs.release_flavor || 'rs-portable') == 'rs-portable' }}",
        "the rs release upload job should be positively gated to the default rs-portable flavor",
    );
    assert_contains(
        create_rs_portable_release_job,
        "needs: [prepare, publish-rs-portable]",
        "the default release upload path should not wait on hybrid .NET/MSIX jobs",
    );

    for (job_name, job) in [
        ("integration-tests", integration_tests_job),
        ("publish-msix", publish_msix_job),
        ("create-bundle", create_bundle_job),
        ("publish-winget", publish_winget_job),
    ] {
        let gate_line = job
            .lines()
            .find(|line| line.trim_start().starts_with("if:"))
            .unwrap_or_else(|| panic!("{job_name} should define a job-level release_flavor gate"));
        assert_contains(
            gate_line,
            "(github.event.inputs.release_flavor || 'rs-portable') == 'hybrid'",
            &format!("{job_name} should be positively gated to release_flavor == 'hybrid'"),
        );
        for forbidden_condition in [
            "!= 'rs-portable'",
            "!= \"rs-portable\"",
            "!= 'rust'",
            "!= \"rust\"",
            "!= 'rust-only'",
            "!= \"rust-only\"",
        ] {
            assert_not_contains(
                gate_line,
                forbidden_condition,
                &format!("{job_name} should not use a negative rust/rs-portable gate"),
            );
        }
    }
}

#[test]
fn rs_portable_release_jobs_stay_isolated_from_dotnet_artifacts() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let publish_job = text_between(&workflow, "  publish-rs-portable:", "  create-bundle:");
    let release_job = text_between(
        &workflow,
        "  create-rs-portable-release:",
        "  publish-winget:",
    );

    assert_contains(
        publish_job,
        "working-directory: rs",
        "rs portable packaging should run from the Rust workspace",
    );
    assert_contains(
        publish_job,
        "Package-Portable.ps1",
        "rs portable packaging should delegate to the Rust portable shim",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_packager --test release_contract_behavior",
        "rs portable release should run packager release-contract tests before packaging",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_app --test default_api_boundary_behavior",
        "rs portable release should run default app API/runtime boundary tests before packaging",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_app --test protocol_behavior",
        "rs portable release should run default protocol facade tests before packaging",
    );
    assert_contains(
        publish_job,
        "validate-rs-portable",
        "rs portable packaging should validate the ZIP before upload",
    );
    assert_contains(
        publish_job,
        "easydict-rs-portable-${{ matrix.platform }}",
        "rs portable upload artifact should remain separately named from legacy assets",
    );

    assert_contains(
        release_job,
        "needs: [prepare, publish-rs-portable]",
        "first rs release should depend only on version parsing and rs portable packaging",
    );
    assert_contains(
        release_job,
        "pattern: easydict-rs-portable-*",
        "first rs release should download only rs portable artifacts",
    );
    assert_contains(
        release_job,
        "rs-portable/*.zip",
        "first rs release should upload only rs portable ZIP assets",
    );

    for (section_name, section) in [
        ("publish-rs-portable", publish_job),
        ("create-rs-portable-release", release_job),
    ] {
        for forbidden_marker in [
            "actions/setup-dotnet",
            "setup-WinAppCli",
            "dotnet/",
            "dotnet\\",
            "dotnet ",
            "publish-msix",
            "create-bundle",
            "easydict-msix",
            "easydict-installer",
            "installer-packages",
            "Package-Msix.ps1",
            "Extract-DotnetRuntime.ps1",
            "Easydict.Workers.",
            "Easydict.CompatHost",
            "WinGet",
            "winget",
        ] {
            assert_not_contains(
                section,
                forbidden_marker,
                &format!(
                    "{section_name} should not touch legacy .NET/MSIX/installer release marker {forbidden_marker}"
                ),
            );
        }
    }
}

#[test]
fn root_readmes_mark_winget_as_legacy_hybrid_not_default_rs_install() {
    let root = repo_root();

    for (relative_path, expected_notice) in [
        ("README.md", "WinGet is a legacy/hybrid install path"),
        ("README_ZH.md", "legacy/hybrid"),
    ] {
        let text = read_text(&root.join(relative_path));
        let portable_index = text
            .find("easydict-rs-portable-vX.Y.Z-win-x64.zip")
            .unwrap_or_else(|| panic!("{relative_path} should recommend the rs portable ZIP"));
        let legacy_index = text.find("Legacy/Hybrid").unwrap_or_else(|| {
            panic!("{relative_path} should keep a Legacy/Hybrid install section")
        });
        let winget_index = text
            .find("winget install xiaocang.EasydictforWindows")
            .unwrap_or_else(|| panic!("{relative_path} should still document the winget command"));

        assert!(
            portable_index < legacy_index,
            "{relative_path} should present the default Rust portable ZIP before legacy/hybrid installs",
        );
        assert!(
            legacy_index < winget_index,
            "{relative_path} should place WinGet under Legacy/Hybrid, not before the default rs portable install",
        );
        assert_contains(
            &text,
            expected_notice,
            &format!("{relative_path} should label WinGet as legacy/hybrid instead of default rs install"),
        );
        let legacy_install_section = &text[legacy_index..winget_index];
        assert_contains(
            legacy_install_section,
            ".NET",
            &format!("{relative_path} should tie WinGet to the retained .NET package"),
        );
        assert_contains(
            legacy_install_section,
            "Rust",
            &format!("{relative_path} should say WinGet is not the default Rust portable install"),
        );
    }
}

#[test]
fn windows_ai_build_script_has_strict_rs_portable_binding_gate() {
    let root = repo_root();
    let build_script = read_text(&root.join("lib/easydict-windows-ai/build.rs"));

    assert_contains(
        &build_script,
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "WindowsAI build script should expose an opt-in strict binding gate for release packaging",
    );
    assert_contains(
        &build_script,
        "cargo:rerun-if-env-changed={WINDOWS_AI_REQUIRE_BINDINGS_ENV}",
        "WindowsAI build script should make Cargo rerun when the strict binding gate changes",
    );
    assert_contains(
        &build_script,
        "panic!",
        "strict WindowsAI binding mode should fail the build instead of silently shipping the unsupported fallback",
    );
    assert_contains(
        &build_script,
        "easydict_windows_ai_winrt_bindings",
        "successful binding generation should still set the native WinRT cfg",
    );
}

#[test]
fn release_orchestration_uses_rust_helpers_not_retired_dotnet_helper_projects() {
    let root = repo_root();
    let build_helpers = read_text(&root.join("dotnet/scripts/Build-RustHelpers.ps1"));

    assert_contains(
        &build_helpers,
        "-p",
        "Build-RustHelpers shim should invoke cargo with an explicit package",
    );
    assert_contains(
        &build_helpers,
        "easydict_packager",
        "Build-RustHelpers shim should delegate helper build/copy logic to the Rust packager",
    );
    assert_contains(
        &build_helpers,
        "build-rust-helpers",
        "Build-RustHelpers shim should use the Rust build-rust-helpers subcommand",
    );

    for relative_path in [
        ".github/workflows/release-publish.yml",
        ".github/workflows/arm64-msix-smoke.yml",
        "dotnet/Makefile",
        "dotnet/scripts/Build-RustHelpers.ps1",
        "dotnet/scripts/publish.ps1",
        "dotnet/scripts/package-and-install.ps1",
        "dotnet/scripts/Package-Msix.ps1",
        "rs/scripts/Package-Portable.ps1",
    ] {
        let text = read_text(&root.join(relative_path));
        for retired_marker in [
            "src/Easydict.NativeBridge",
            "src/Easydict.BrowserRegistrar",
            "src/Easydict.CompatHost",
            "Easydict.NativeBridge.csproj",
            "Easydict.BrowserRegistrar.csproj",
            "Easydict.CompatHost.csproj",
        ] {
            assert_not_contains(
                &text,
                retired_marker,
                &format!(
                    "{relative_path} must not resurrect retired .NET helper project {retired_marker}"
                ),
            );
        }
    }
}

#[test]
fn build_rust_helpers_child_cargo_is_forced_to_rust_only_runtime_profile() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let test_root = tempfile_dir("packager-build-rust-helpers-env");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_dir = test_root.join("out");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    write_fake_windows_ai_manifest_for_workspace(&workspace);
    fs::create_dir_all(&output_dir).expect("create output dir");
    write_fake_tooling_scripts(&fake_bin);
    let record_path = test_root.join("cargo-env.txt");

    let path_with_fake_tools = prepend_path(&fake_bin, environment.original_path());
    std::env::set_var("PATH", path_with_fake_tools);
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
    std::env::set_var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "0");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);

    let outcome = build_rust_helpers(&BuildRustHelpersOptions {
        rust_workspace: workspace.clone(),
        platform: "x64".to_string(),
        configuration: "Release".to_string(),
        output_dir: output_dir.clone(),
    })
    .expect("build helpers should run fake cargo and copy generated helpers");

    assert_eq!(outcome.cargo_target, "x86_64-pc-windows-msvc");
    assert_eq!(outcome.profile_dir, "release");
    let record = read_text(&record_path);
    assert_contains(
        &record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "build-rust-helpers child cargo should override inherited Easydict runtime profile",
    );
    assert_contains(
        &record,
        "RUNTIME_PROFILE=rust-only",
        "build-rust-helpers child cargo should override inherited generic runtime profile",
    );
    assert_eq!(
        record
            .lines()
            .filter(|line| *line == "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=1")
            .count(),
        2,
        "build-rust-helpers should require WindowsAI WinRT bindings for the preflight and helper build:\n{record}"
    );
    assert_contains(
        &record,
        "ARGS=check --manifest-path",
        "build-rust-helpers should preflight WindowsAI WinRT bindings before helper builds",
    );
    assert_contains(
        &record,
        "easydict-windows-ai",
        "WindowsAI preflight should target the easydict-windows-ai manifest",
    );
    assert_contains(
        &record,
        "build -p easydict_app --target x86_64-pc-windows-msvc --bin easydict-native-bridge --bin easydict_browser_registrar --bin easydict_cli --bin easydict_long_doc --release",
        "build-rust-helpers should keep the expected helper cargo command line",
    );
    for exe_name in [
        "easydict-native-bridge.exe",
        "easydict_browser_registrar.exe",
        "easydict_cli.exe",
        "easydict_long_doc.exe",
    ] {
        assert!(
            output_dir.join(exe_name).is_file(),
            "{exe_name} should be copied from fake cargo output"
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

#[test]
fn pack_rs_portable_child_cargo_is_forced_to_rust_only_runtime_profile() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let test_root = tempfile_dir("packager-pack-rs-portable-env");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_root = test_root.join("out");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    write_fake_windows_ai_manifest_for_workspace(&workspace);
    fs::create_dir_all(&output_root).expect("create output root");
    write_fake_tooling_scripts(&fake_bin);
    let record_path = test_root.join("cargo-env.txt");

    let path_with_fake_tools = prepend_path(&fake_bin, environment.original_path());
    std::env::set_var("PATH", path_with_fake_tools);
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
    std::env::set_var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "0");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);

    let outcome = pack_rs_portable(&PackRustPortableOptions {
        rust_workspace: workspace.clone(),
        platform: "x64".to_string(),
        configuration: "Release".to_string(),
        output_root: output_root.clone(),
        package_version: Some("v0.0.0-test".to_string()),
        create_zip: false,
    })
    .expect("pack-rs-portable should run fake cargo and stage a validated payload");

    assert_eq!(
        outcome.package_name,
        "easydict-rs-portable-v0.0.0-test-win-x64"
    );
    assert!(outcome.zip_path.is_none());
    assert_eq!(outcome.file_count, 6);
    assert_eq!(outcome.directory_validation_entries, 6);

    let record = read_text(&record_path);
    assert_eq!(
        record
            .lines()
            .filter(|line| *line == "EASYDICT_RUNTIME_PROFILE=rust-only")
            .count(),
        3,
        "pack-rs-portable should force Easydict rust-only env for the preflight and both child cargo builds:\n{record}"
    );
    assert_eq!(
        record
            .lines()
            .filter(|line| *line == "RUNTIME_PROFILE=rust-only")
            .count(),
        3,
        "pack-rs-portable should force generic rust-only env for the preflight and both child cargo builds:\n{record}"
    );
    assert_eq!(
        record
            .lines()
            .filter(|line| *line == "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=1")
            .count(),
        3,
        "pack-rs-portable should require WindowsAI WinRT bindings for preflight, preview, and helper builds:\n{record}"
    );
    let cargo_args = record
        .lines()
        .filter(|line| line.starts_with("ARGS="))
        .collect::<Vec<_>>();
    assert!(
        cargo_args
            .first()
            .is_some_and(|line| line.contains("check --manifest-path")
                && line.contains("easydict-windows-ai")
                && line.contains("--target x86_64-pc-windows-msvc")),
        "pack-rs-portable should preflight WindowsAI WinRT bindings before package builds:\n{record}"
    );
    assert_contains(
        &record,
        "ARGS=build -p easydict_preview_iced --target x86_64-pc-windows-msvc --release",
        "pack-rs-portable should build the preview app without enabling retained features",
    );
    assert_contains(
        &record,
        "ARGS=build -p easydict_app --target x86_64-pc-windows-msvc --bin easydict-native-bridge --bin easydict_browser_registrar --bin easydict_cli --bin easydict_long_doc --release",
        "pack-rs-portable should build Rust helpers without enabling retained features",
    );
    for forbidden_marker in [
        "retained-dotnet-workers",
        "--features",
        "--all-features",
        "Easydict.Workers",
        "CompatHost",
    ] {
        assert_not_contains(
            &record,
            forbidden_marker,
            &format!("pack-rs-portable child cargo must not enable retained runtime marker {forbidden_marker}"),
        );
    }

    let package_dir = outcome.package_dir;
    for entry in [
        "Easydict.Rust.exe",
        "easydict-native-bridge.exe",
        "easydict_browser_registrar.exe",
        "easydict_cli.exe",
        "easydict_long_doc.exe",
        "README-portable.txt",
    ] {
        assert!(
            package_dir.join(entry).is_file(),
            "{entry} should be staged in the first rs portable payload"
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

#[test]
fn pack_rs_portable_creates_and_validates_zip_without_retained_dotnet_payload() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let test_root = tempfile_dir("packager-pack-rs-portable-zip");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_root = test_root.join("out");
    let target_release = workspace
        .join("target")
        .join("x86_64-pc-windows-msvc")
        .join("release");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    write_fake_windows_ai_manifest_for_workspace(&workspace);
    write_stale_dotnet_payload_markers(&target_release);
    fs::create_dir_all(&output_root).expect("create output root");
    write_fake_tooling_scripts(&fake_bin);
    let record_path = test_root.join("cargo-env.txt");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
    std::env::set_var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "0");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);

    let outcome = pack_rs_portable(&PackRustPortableOptions {
        rust_workspace: workspace.clone(),
        platform: "x64".to_string(),
        configuration: "Release".to_string(),
        output_root: output_root.clone(),
        package_version: Some("v0.0.0-zip".to_string()),
        create_zip: true,
    })
    .expect("pack-rs-portable should stage, zip, and validate the first rs portable payload");

    let zip_path = outcome
        .zip_path
        .as_ref()
        .expect("pack-rs-portable should report the created ZIP path");
    assert!(
        zip_path.is_file(),
        "pack-rs-portable should create a real ZIP at {}",
        zip_path.display()
    );
    assert!(
        outcome.package_dir.is_dir(),
        "pack-rs-portable should keep the staged directory for validation"
    );
    assert_eq!(
        outcome.zip_validation_entries,
        Some(6),
        "pack-rs-portable should validate the ZIP payload after creating it"
    );

    let directory_validation = validate_rs_portable_payload(&ValidateRustPortableOptions {
        package_path: outcome.package_dir.clone(),
    })
    .expect("staged rs portable directory should validate");
    let zip_validation = validate_rs_portable_payload(&ValidateRustPortableOptions {
        package_path: zip_path.clone(),
    })
    .expect("created rs portable ZIP should validate");
    assert_eq!(directory_validation.checked_entries, 6);
    assert_eq!(zip_validation.checked_entries, 6);

    let expected_entries = vec![
        "Easydict.Rust.exe".to_string(),
        "README-portable.txt".to_string(),
        "easydict-native-bridge.exe".to_string(),
        "easydict_browser_registrar.exe".to_string(),
        "easydict_cli.exe".to_string(),
        "easydict_long_doc.exe".to_string(),
    ];
    let directory_entries = directory_entry_names(&outcome.package_dir);
    let zip_entries = zip_entry_names(zip_path);
    assert_eq!(
        directory_entries, expected_entries,
        "staged rs portable directory should contain only the first-release Rust payload"
    );
    assert_eq!(
        zip_entries, expected_entries,
        "created rs portable ZIP should contain only the first-release Rust payload"
    );
    assert_entries_do_not_contain_retained_dotnet_payload(&directory_entries, "staged directory");
    assert_entries_do_not_contain_retained_dotnet_payload(&zip_entries, "created ZIP");

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn pack_rs_portable_zip_extracts_to_cli_smoke_without_dotnet_or_powershell() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "EASYDICT_FAKE_CARGO_RECORD",
        "EASYDICT_PACKAGED_CLI_RECORD",
        "EASYDICT_RELEASE_FORBIDDEN_TOOL_RECORD",
    ]);
    let test_root = tempfile_dir("packager-pack-rs-portable-cli-smoke");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_root = test_root.join("out");
    let extract_dir = test_root.join("extract");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    write_fake_windows_ai_manifest_for_workspace(&workspace);
    fs::create_dir_all(&output_root).expect("create output root");
    fs::create_dir_all(&extract_dir).expect("create extract root");
    write_fake_tooling_scripts(&fake_bin);
    write_fake_release_forbidden_tool_exes(&fake_bin);
    let cargo_record_path = test_root.join("cargo-env.txt");
    let cli_record_path = test_root.join("packaged-cli.txt");
    let forbidden_tool_record = test_root.join("forbidden-tools.txt");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
    std::env::set_var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "0");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &cargo_record_path);

    let outcome = pack_rs_portable(&PackRustPortableOptions {
        rust_workspace: workspace.clone(),
        platform: "x64".to_string(),
        configuration: "Release".to_string(),
        output_root: output_root.clone(),
        package_version: Some("v0.0.0-cli-smoke".to_string()),
        create_zip: true,
    })
    .expect("pack-rs-portable should create a validated ZIP with executable Rust helpers");

    let zip_path = outcome.zip_path.as_ref().expect("created ZIP path");
    extract_zip(zip_path, &extract_dir);
    let packaged_cli = extract_dir.join("easydict_cli.exe");
    assert!(
        packaged_cli.is_file(),
        "extracted rs portable ZIP should contain easydict_cli.exe"
    );

    let output = std::process::Command::new(&packaged_cli)
        .arg("--help")
        .env("PATH", prepend_path(&fake_bin, environment.original_path()))
        .env("EASYDICT_RUNTIME_PROFILE", "rust-only")
        .env("RUNTIME_PROFILE", "rust-only")
        .env("EASYDICT_PACKAGED_CLI_RECORD", &cli_record_path)
        .env(
            "EASYDICT_RELEASE_FORBIDDEN_TOOL_RECORD",
            &forbidden_tool_record,
        )
        .output()
        .expect("run extracted packaged easydict_cli.exe");

    assert!(
        output.status.success(),
        "extracted packaged CLI smoke should succeed\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        !forbidden_tool_record.exists(),
        "extracted packaged CLI smoke must not invoke dotnet.exe, powershell.exe, or pwsh.exe"
    );
    let cli_record = read_text(&cli_record_path);
    assert_contains(
        &cli_record,
        "CLI=easydict_cli",
        "extracted packaged CLI smoke should execute the helper from the ZIP",
    );
    assert_contains(
        &cli_record,
        "ARGS=--help",
        "extracted packaged CLI smoke should pass through the requested arguments",
    );
    assert_contains(
        &cli_record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "extracted packaged CLI smoke should run under the first-release rust-only profile",
    );
    assert_contains(
        &cli_record,
        "RUNTIME_PROFILE=rust-only",
        "extracted packaged CLI smoke should run under the generic rust-only profile",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn package_portable_powershell_shim_forces_and_restores_runtime_profile() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("package-portable-shim-runtime-profile");
    let fake_bin = test_root.join("bin");
    let output_root = test_root.join("out");
    let wrapper_path = test_root.join("run-package-portable.ps1");
    let cargo_record_path = test_root.join("cargo-record.txt");
    let post_env_record_path = test_root.join("post-env.txt");
    fs::create_dir_all(&test_root).expect("create test root");
    fs::create_dir_all(&output_root).expect("create output root");
    write_fake_package_portable_tool_scripts(&fake_bin);
    write_package_portable_wrapper(
        &wrapper_path,
        &root.join("rs/scripts/Package-Portable.ps1"),
        &output_root,
        &post_env_record_path,
    );

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "outer-parent");
    std::env::set_var("RUNTIME_PROFILE", "outer-parent");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &cargo_record_path);

    let output = powershell_script_command(&wrapper_path)
        .output()
        .expect("run Package-Portable wrapper");

    assert!(
        output.status.success(),
        "Package-Portable shim wrapper should succeed with fake cargo\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let cargo_record = read_text(&cargo_record_path);
    assert_contains(
        &cargo_record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "Package-Portable shim should force Easydict runtime profile while cargo runs",
    );
    assert_contains(
        &cargo_record,
        "RUNTIME_PROFILE=rust-only",
        "Package-Portable shim should force generic runtime profile while cargo runs",
    );
    assert_contains(
        &cargo_record,
        "ARGS=run --manifest-path",
        "Package-Portable shim should invoke cargo run through the Rust packager",
    );
    for expected in [
        "-p",
        "easydict_packager",
        "pack-rs-portable",
        "--workspace",
        "--platform x64",
        "--configuration Debug",
        "--output-root",
        "--package-version v0.0.0-shim",
        "--no-zip",
    ] {
        assert_contains(
            &cargo_record,
            expected,
            "Package-Portable shim should pass the expected pack-rs-portable arguments",
        );
    }
    for forbidden in [
        "retained-dotnet-workers",
        "--features",
        "--all-features",
        "CompatHost",
    ] {
        assert_not_contains(
            &cargo_record,
            forbidden,
            "Package-Portable shim must not enable retained runtime features",
        );
    }

    let post_env_record = read_text(&post_env_record_path);
    assert_contains(
        &post_env_record,
        "POST_EASYDICT_RUNTIME_PROFILE=hybrid",
        "Package-Portable shim should restore the caller's Easydict runtime profile",
    );
    assert_contains(
        &post_env_record,
        "POST_RUNTIME_PROFILE=hybrid",
        "Package-Portable shim should restore the caller's generic runtime profile",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[test]
fn translate_long_doc_script_is_rust_only_and_rejects_dotnet_legacy_mode() {
    let root = repo_root();
    let script = read_text(&root.join("scripts/translate-long-doc.ps1"));

    assert_contains(
        &script,
        "Invoke-RustHelper",
        "LongDoc helper script should keep the packaged Rust helper path",
    );
    assert_contains(
        &script,
        "Invoke-RustCargo",
        "LongDoc helper script should keep Rust cargo development mode",
    );
    assert_contains(
        &script,
        "[string]$ResultJsonPath",
        "LongDoc helper script should accept the Rust result JSON sidecar path",
    );
    assert_contains(
        &script,
        "[switch]$RetryFailed",
        "LongDoc helper script should accept the Rust retry-failed switch",
    );
    assert_contains(
        &script,
        "\"--result-json\", $ResultJsonPath",
        "LongDoc helper script should pass the result JSON sidecar path to Rust",
    );
    assert_contains(
        &script,
        "\"--retry-failed\"",
        "LongDoc helper script should pass retry-failed to Rust",
    );
    assert_contains(
        &script,
        "ResultJsonPath is required when -RetryFailed is used.",
        "LongDoc helper script should keep retry-failed tied to a Rust sidecar",
    );
    assert_contains(
        &script,
        "-UseDotnetLegacy has been retired",
        "legacy dotnet mode should fail locally instead of launching WinUI",
    );

    for retired_marker in [
        "Invoke-DotnetLegacy",
        "New-LegacyLongDocArguments",
        "& dotnet",
        "dotnet.exe",
        "dotnet run",
        "Start-Process dotnet",
        "dotnetArguments",
        "dotnet\\src\\Easydict.WinUI",
        "dotnet/src/Easydict.WinUI",
        "Easydict.WinUI.csproj",
        "Easydict.Workers.LongDocument",
        "Easydict.CompatHost",
        "--translate-long-doc",
    ] {
        assert_not_contains(
            &script,
            retired_marker,
            &format!("scripts/translate-long-doc.ps1 must not launch legacy .NET LongDoc mode"),
        );
    }
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_invokes_rust_helper_with_retry_sidecar_arguments() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_LONG_DOC_HELPER_RECORD",
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-rust-helper-smoke");
    let fake_bin = test_root.join("bin");
    let app_dir = test_root.join("app");
    let helper_path = test_root.join("fake-easydict-long-doc.cmd");
    let record_path = test_root.join("helper-args.txt");
    let forbidden_tool_record = test_root.join("forbidden-tools.txt");
    let input_path = test_root.join("input.pdf");
    let output_path = test_root.join("translated.pdf");
    let result_json_path = test_root.join("translated-result.json");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_long_doc_helper(&helper_path);
    write_fake_forbidden_tool_scripts(&fake_bin);
    write_stale_dotnet_payload_markers(&app_dir);
    fs::write(&input_path, b"%PDF-1.7\n").expect("write input");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &record_path);
    std::env::set_var(
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        &forbidden_tool_record,
    );

    let output = translate_long_doc_script_command(&root)
        .arg("-InputFile")
        .arg(&input_path)
        .args(["-TargetLanguage", "zh-Hans", "-SourceLanguage", "en"])
        .arg("-OutputFile")
        .arg(&output_path)
        .arg("-ResultJsonPath")
        .arg(&result_json_path)
        .arg("-RetryFailed")
        .args(["-ServiceId", "google", "-OutputMode", "both"])
        .args(["-PdfExportMode", "Overlay", "-PageRange", "2-3"])
        .arg("-AppDir")
        .arg(&app_dir)
        .arg("-RustHelperPath")
        .arg(&helper_path)
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        output.status.success(),
        "translate-long-doc shim should invoke fake Rust helper successfully\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        !forbidden_tool_record.exists(),
        "Rust-helper shim path must not launch cargo/dotnet/pwsh from PATH"
    );
    let record = read_text(&record_path);
    let expected_arguments = vec![
        "--input".to_string(),
        input_path.display().to_string(),
        "--target-language".to_string(),
        "zh-Hans".to_string(),
        "--from".to_string(),
        "en".to_string(),
        "--output".to_string(),
        output_path.display().to_string(),
        "--result-json".to_string(),
        result_json_path.display().to_string(),
        "--retry-failed".to_string(),
        "--service".to_string(),
        "google".to_string(),
        "--output-mode".to_string(),
        "both".to_string(),
        "--pdf-export-mode".to_string(),
        "Overlay".to_string(),
        "--page-range".to_string(),
        "2-3".to_string(),
        "--app-dir".to_string(),
        app_dir.display().to_string(),
    ];
    for expected in expected_arguments {
        assert_contains(
            &record,
            &expected,
            "translate-long-doc shim should pass the expected Rust helper argument",
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_use_cargo_forwards_retry_sidecar_without_dotnet_tools() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_LONG_DOC_HELPER_RECORD",
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-use-cargo-retry");
    let fake_bin = test_root.join("bin");
    let app_dir = test_root.join("app");
    let cargo_record_path = test_root.join("cargo-args.txt");
    let forbidden_tool_record = test_root.join("forbidden-tools.txt");
    let input_path = test_root.join("input.pdf");
    let output_path = test_root.join("translated.pdf");
    let result_json_path = test_root.join("translated-result.json");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_long_doc_cargo_script(&fake_bin);
    write_fake_dotnet_forbidden_tool_script(&fake_bin);
    write_stale_dotnet_payload_markers(&app_dir);
    fs::write(&input_path, b"%PDF-1.7\n").expect("write input");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &cargo_record_path);
    std::env::set_var(
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        &forbidden_tool_record,
    );

    let output = translate_long_doc_script_command(&root)
        .arg("-InputFile")
        .arg(&input_path)
        .args(["-TargetLanguage", "zh-Hans", "-SourceLanguage", "en"])
        .arg("-OutputFile")
        .arg(&output_path)
        .arg("-ResultJsonPath")
        .arg(&result_json_path)
        .arg("-RetryFailed")
        .args(["-ServiceId", "google", "-OutputMode", "both"])
        .args(["-PdfExportMode", "Overlay", "-PageRange", "2-3"])
        .arg("-AppDir")
        .arg(&app_dir)
        .arg("-UseCargo")
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        output.status.success(),
        "translate-long-doc -UseCargo shim should invoke fake cargo successfully\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        !forbidden_tool_record.exists(),
        "-UseCargo LongDoc shim must not launch dotnet or retained runtime tools"
    );
    let record = read_text(&cargo_record_path);
    let expected_arguments = vec![
        "ARGS=run -p easydict_app --bin easydict_long_doc --".to_string(),
        "--input".to_string(),
        input_path.display().to_string(),
        "--target-language".to_string(),
        "zh-Hans".to_string(),
        "--from".to_string(),
        "en".to_string(),
        "--output".to_string(),
        output_path.display().to_string(),
        "--result-json".to_string(),
        result_json_path.display().to_string(),
        "--retry-failed".to_string(),
        "--service".to_string(),
        "google".to_string(),
        "--output-mode".to_string(),
        "both".to_string(),
        "--pdf-export-mode".to_string(),
        "Overlay".to_string(),
        "--page-range".to_string(),
        "2-3".to_string(),
        "--app-dir".to_string(),
        app_dir.display().to_string(),
    ];
    for expected in expected_arguments {
        assert_contains(
            &record,
            &expected,
            "translate-long-doc -UseCargo should pass the expected Rust CLI argument",
        );
    }
    for forbidden in ["dotnet", "Easydict.Workers", "CompatHost"] {
        assert_not_contains(
            &record,
            forbidden,
            "-UseCargo argument forwarding must stay on the Rust helper path",
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_rejects_retry_failed_without_sidecar_before_helper() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture(["EASYDICT_LONG_DOC_HELPER_RECORD"]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-retry-without-sidecar");
    let helper_path = test_root.join("fake-easydict-long-doc.cmd");
    let record_path = test_root.join("helper-args.txt");
    let input_path = test_root.join("input.txt");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_long_doc_helper(&helper_path);
    fs::write(&input_path, "retry me").expect("write input");
    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &record_path);

    let output = translate_long_doc_script_command(&root)
        .arg("-InputFile")
        .arg(&input_path)
        .args(["-TargetLanguage", "zh-Hans"])
        .arg("-RetryFailed")
        .arg("-RustHelperPath")
        .arg(&helper_path)
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        !output.status.success(),
        "missing ResultJsonPath should fail before helper"
    );
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert_contains(
        &stderr,
        "ResultJsonPath is required when -RetryFailed is used.",
        "retry-failed validation should explain the missing sidecar",
    );
    assert!(
        !record_path.exists(),
        "retry-failed validation must fail before invoking the Rust helper"
    );

    let _ = fs::remove_dir_all(test_root);
    drop(environment);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_rejects_legacy_dotnet_mode_before_helper() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture(["EASYDICT_LONG_DOC_HELPER_RECORD"]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-legacy-retired");
    let helper_path = test_root.join("fake-easydict-long-doc.cmd");
    let record_path = test_root.join("helper-args.txt");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_long_doc_helper(&helper_path);
    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &record_path);

    let output = translate_long_doc_script_command(&root)
        .arg("-ListServices")
        .arg("-UseDotnetLegacy")
        .arg("-RustHelperPath")
        .arg(&helper_path)
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        !output.status.success(),
        "retired legacy mode should fail before helper"
    );
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert_contains(
        &stderr,
        "-UseDotnetLegacy has been retired",
        "legacy dotnet mode should be rejected locally",
    );
    assert!(
        !record_path.exists(),
        "legacy mode validation must fail before invoking the Rust helper"
    );

    let _ = fs::remove_dir_all(test_root);
    drop(environment);
}

#[test]
fn legacy_dotnet_packaging_paths_reject_rust_only_and_require_hybrid_profile() {
    let root = repo_root();
    let makefile = read_text(&root.join("dotnet/Makefile"));
    let release_workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let winui_csproj = read_text(&root.join("dotnet/src/Easydict.WinUI/Easydict.WinUI.csproj"));
    assert_contains(
        &makefile,
        "RUNTIME_PROFILE ?=",
        "Makefile should keep the runtime profile variable explicit for callers",
    );
    assert_not_contains(
        &makefile,
        "RUNTIME_PROFILE ?= hybrid",
        "Makefile must not silently default legacy .NET/MSIX targets to hybrid",
    );
    assert_contains(
        &winui_csproj,
        "<RuntimeProfile Condition=\"'$(RuntimeProfile)' == ''\">RustOnly</RuntimeProfile>",
        "WinUI project should treat an omitted RuntimeProfile as RustOnly",
    );
    assert_not_contains(
        &winui_csproj,
        "<RuntimeProfile Condition=\"'$(RuntimeProfile)' == ''\">Hybrid</RuntimeProfile>",
        "WinUI project must not silently default omitted RuntimeProfile to Hybrid",
    );
    assert_contains(
        &makefile,
        "if [ \"$$runtime_profile\" = \"hybrid\" ]; then",
        "Makefile retained worker/runtime branches should require explicit hybrid",
    );
    assert_contains(
        &makefile,
        "only hybrid is supported for legacy .NET packaging",
        "Makefile publish targets should reject unknown runtime profiles before worker publish",
    );
    assert_contains(
        &makefile,
        "only hybrid is supported for legacy .NET/MSIX packaging",
        "Makefile MSIX targets should reject unknown runtime profiles before runtime extraction",
    );
    assert!(
        !makefile.contains(
            "if [ \"$$runtime_profile\" != \"rust-only\" ] && [ \"$$runtime_profile\" != \"rustonly\" ]"
        ),
        "Makefile should not use negative rust-only checks for retained worker/runtime branches"
    );
    let validate_msix_target = text_between(&makefile, "validate-msix:", "# Encrypt");
    assert_contains(
        validate_msix_target,
        "if [ -n \"$$runtime_profile\" ]; then",
        "Makefile validate-msix should pass runtime profile only when the caller provided one",
    );
    assert_contains(
        validate_msix_target,
        "easydict_msix_validate -- \"$(MSIX)\" --runtime-profile \"$$runtime_profile\"",
        "Makefile validate-msix should pass a normalized explicit profile when provided",
    );
    assert_contains(
        validate_msix_target,
        "easydict_msix_validate -- \"$(MSIX)\";",
        "Makefile validate-msix should omit --runtime-profile when unset so the Rust validator uses its Rust-only default",
    );
    assert_not_contains(
        validate_msix_target,
        "--runtime-profile \"$(RUNTIME_PROFILE)\"",
        "Makefile validate-msix must not pass an empty runtime profile through to the Rust validator",
    );
    assert_contains(
        &release_workflow,
        "verify-bundle-minversion",
        "release workflow should validate final MSIX bundle MinVersion through Rust",
    );
    assert_contains(
        &release_workflow,
        "--runtime-profile \"${{ env.RUNTIME_PROFILE }}\"",
        "release workflow bundle validation should reuse the normalized runtime profile",
    );
    let create_bundle_job =
        text_between(&release_workflow, "  create-bundle:", "  publish-winget:");
    assert_contains(
        create_bundle_job,
        "RUNTIME_PROFILE: ${{ github.event.inputs.runtime_profile || 'hybrid' }}",
        "create-bundle should define the runtime profile used by bundle payload validation",
    );
    assert_contains(
        create_bundle_job,
        "--runtime-profile \"${{ env.RUNTIME_PROFILE }}\"",
        "create-bundle should pass its job runtime profile into bundle validation",
    );

    for relative_path in [
        ".github/workflows/release-publish.yml",
        ".github/workflows/arm64-msix-smoke.yml",
    ] {
        let text = read_text(&root.join(relative_path));
        assert_contains(
            &text,
            "RETAINED_WORKERS_ENABLED=true",
            &format!("{relative_path} should only enable retained workers after hybrid validation"),
        );
        assert_contains(
            &text,
            "if: env.RETAINED_WORKERS_ENABLED == 'true'",
            &format!("{relative_path} should gate retained worker/runtime steps positively"),
        );
        assert!(
            !text.contains("if: env.RUNTIME_PROFILE != 'rust-only'"),
            "{relative_path} should not use negative rust-only checks for retained worker/runtime steps"
        );
    }

    for relative_path in [
        "dotnet/scripts/publish.ps1",
        "dotnet/scripts/package-and-install.ps1",
        "dotnet/scripts/Package-Msix.ps1",
        "dotnet/scripts/Build-Installer.ps1",
    ] {
        let text = read_text(&root.join(relative_path));
        assert_contains(
            &text,
            "Test-RustOnlyRuntimeProfile",
            &format!("{relative_path} should explicitly detect rust-only profile"),
        );
        assert_contains(
            &text,
            "first rs release is portable-only",
            &format!("{relative_path} should redirect rust-only callers to rs portable"),
        );
        assert_contains(
            &text,
            "Test-HybridRuntimeProfile",
            &format!("{relative_path} should explicitly detect the hybrid profile"),
        );
        assert_contains(
            &text,
            "Only Hybrid is supported",
            &format!("{relative_path} should reject unknown legacy packaging profiles"),
        );
        assert_contains(
            &text,
            "[string]$RuntimeProfile = \"\"",
            &format!("{relative_path} should require an explicit hybrid profile"),
        );
        assert_contains(
            &text,
            "RuntimeProfile must be explicitly set to Hybrid",
            &format!("{relative_path} should fail when RuntimeProfile is omitted"),
        );
        assert!(
            !text.contains("[string]$RuntimeProfile = \"Hybrid\""),
            "{relative_path} should not silently default to a runtime-producing profile"
        );
    }

    let package_msix = read_text(&root.join("dotnet/scripts/Package-Msix.ps1"));
    let prepare_args = text_between(&package_msix, "$prepareArgs = @(", "if ($MsixVersion)");
    assert_contains(
        prepare_args,
        "prepare-package-inputs",
        "Package-Msix.ps1 should prepare package inputs through the Rust MSIX helper",
    );
    assert_contains(
        prepare_args,
        "\"--runtime-profile\",",
        "Package-Msix.ps1 should pass an explicit runtime profile into prepare-package-inputs",
    );
    assert_contains(
        prepare_args,
        "\"hybrid\"",
        "legacy Package-Msix.ps1 should keep prepare-package-inputs on the explicit hybrid path",
    );

    assert_contains(
        &makefile,
        "scripts/Build-Installer.ps1 -Platform x64 -Version $(VERSION) -RuntimeProfile $(RUNTIME_PROFILE)",
        "Makefile installer-x64 should pass the explicit runtime profile into the legacy installer script",
    );
    assert_contains(
        &makefile,
        "scripts/Build-Installer.ps1 -Platform x86 -Version $(VERSION) -RuntimeProfile $(RUNTIME_PROFILE)",
        "Makefile installer-x86 should pass the explicit runtime profile into the legacy installer script",
    );
    assert_contains(
        &makefile,
        "scripts/Build-Installer.ps1 -Platform arm64 -Version $(VERSION) -RuntimeProfile $(RUNTIME_PROFILE)",
        "Makefile installer-arm64 should pass the explicit runtime profile into the legacy installer script",
    );
}

#[test]
fn dotnet_runtime_extraction_shim_requires_explicit_hybrid_profile() {
    let root = repo_root();
    let script = read_text(&root.join("dotnet/scripts/Extract-DotnetRuntime.ps1"));
    let manifest = read_text(&root.join("rs/crates/easydict_packager/Cargo.toml"));

    assert_contains(
        &script,
        "[string]$RuntimeProfile = \"\"",
        "runtime extraction shim should not silently default to a runtime-producing profile",
    );
    assert_contains(
        &script,
        "requires -RuntimeProfile Hybrid",
        "runtime extraction shim should fail when the hybrid profile is not explicit",
    );
    assert_contains(
        &script,
        "$validRuntimeProfiles = @(\"Hybrid\", \"hybrid\")",
        "runtime extraction shim should only accept the explicit hybrid spelling",
    );
    assert_contains(
        &script,
        "extract-dotnet-runtime",
        "runtime extraction shim should delegate to the Rust packager",
    );
    assert_contains(
        &script,
        "--runtime-profile",
        "runtime extraction shim must pass the runtime profile to the Rust packager",
    );
    assert_contains(
        &script,
        "$RuntimeProfile",
        "runtime extraction shim should pass the caller-provided profile value",
    );
    assert_contains(
        &script,
        "--features",
        "runtime extraction shim should opt into the hybrid-only downloader feature",
    );
    assert_contains(
        &script,
        "hybrid-dotnet-runtime-packaging",
        "runtime extraction shim should build the packager with the hybrid-only downloader feature",
    );
    assert_contains(
        &manifest,
        "reqwest = { version = \"0.12\", default-features = false, features = [\"blocking\", \"rustls-tls\"], optional = true }",
        "default easydict_packager builds should not compile the .NET runtime downloader HTTP client",
    );
    assert_contains(
        &manifest,
        "hybrid-dotnet-runtime-packaging = [\"dep:reqwest\"]",
        ".NET runtime downloading should live behind an explicit hybrid packaging feature",
    );
}

#[cfg(windows)]
#[test]
fn legacy_packaging_scripts_reject_non_hybrid_profiles_before_invoking_external_tools() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment =
        EnvironmentSnapshot::capture(["PATH", "EASYDICT_LEGACY_PACKAGING_FORBIDDEN_TOOL_RECORD"]);
    let root = repo_root();
    let test_root = tempfile_dir("legacy-packaging-profile-guard");
    let fake_bin = test_root.join("bin");
    let publish_dir = test_root.join("publish");
    let manifest_path = test_root.join("Package.appxmanifest");
    let output_msix_path = test_root.join("out").join("Easydict.msix");
    let runtime_output_dir = test_root.join("dotnet-runtime");
    let record_path = test_root.join("forbidden-tools.txt");

    fs::create_dir_all(&publish_dir).expect("create fake publish dir");
    fs::write(&manifest_path, "<Package></Package>").expect("write fake manifest");
    write_fake_legacy_packaging_forbidden_tool_scripts(&fake_bin);
    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var(
        "EASYDICT_LEGACY_PACKAGING_FORBIDDEN_TOOL_RECORD",
        &record_path,
    );

    for runtime_profile in [None, Some("rust-only")] {
        let mut publish = powershell_script_command(&root.join("dotnet/scripts/publish.ps1"));
        if let Some(profile) = runtime_profile {
            publish.args(["-RuntimeProfile", profile]);
        }
        assert_legacy_packaging_profile_rejected_before_tools(
            publish,
            "dotnet/scripts/publish.ps1",
            runtime_profile,
            &record_path,
        );

        let mut package_and_install =
            powershell_script_command(&root.join("dotnet/scripts/package-and-install.ps1"));
        package_and_install
            .args(["-Version", "0.0.0-test"])
            .arg("-SkipInstall");
        if let Some(profile) = runtime_profile {
            package_and_install.args(["-RuntimeProfile", profile]);
        }
        assert_legacy_packaging_profile_rejected_before_tools(
            package_and_install,
            "dotnet/scripts/package-and-install.ps1",
            runtime_profile,
            &record_path,
        );

        let mut package_msix =
            powershell_script_command(&root.join("dotnet/scripts/Package-Msix.ps1"));
        package_msix
            .args(["-Platform", "x64"])
            .arg("-PublishDir")
            .arg(&publish_dir)
            .arg("-ManifestPath")
            .arg(&manifest_path)
            .arg("-OutputMsixPath")
            .arg(&output_msix_path);
        if let Some(profile) = runtime_profile {
            package_msix.args(["-RuntimeProfile", profile]);
        }
        assert_legacy_packaging_profile_rejected_before_tools(
            package_msix,
            "dotnet/scripts/Package-Msix.ps1",
            runtime_profile,
            &record_path,
        );

        let mut build_installer =
            powershell_script_command(&root.join("dotnet/scripts/Build-Installer.ps1"));
        build_installer.args(["-Platform", "x64", "-Version", "0.0.0-test"]);
        if let Some(profile) = runtime_profile {
            build_installer.args(["-RuntimeProfile", profile]);
        }
        assert_legacy_packaging_profile_rejected_before_tools(
            build_installer,
            "dotnet/scripts/Build-Installer.ps1",
            runtime_profile,
            &record_path,
        );

        let mut extract_runtime =
            powershell_script_command(&root.join("dotnet/scripts/Extract-DotnetRuntime.ps1"));
        extract_runtime
            .args(["-Rid", "win-x64"])
            .arg("-OutputDir")
            .arg(&runtime_output_dir);
        if let Some(profile) = runtime_profile {
            extract_runtime.args(["-RuntimeProfile", profile]);
        }
        assert_legacy_packaging_profile_rejected_before_tools(
            extract_runtime,
            "dotnet/scripts/Extract-DotnetRuntime.ps1",
            runtime_profile,
            &record_path,
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

fn repo_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("packager crate should live under rs/crates/easydict_packager")
        .to_path_buf()
}

fn read_text(path: &Path) -> String {
    std::fs::read_to_string(path)
        .unwrap_or_else(|error| panic!("failed to read {}: {error}", path.display()))
}

fn assert_contains(haystack: &str, needle: &str, message: &str) {
    assert!(haystack.contains(needle), "{message}\nmissing: {needle}");
}

fn assert_not_contains(haystack: &str, needle: &str, message: &str) {
    assert!(!haystack.contains(needle), "{message}\nforbidden: {needle}");
}

fn directory_entry_names(root: &Path) -> Vec<String> {
    let mut entries = Vec::new();
    collect_directory_entry_names(root, root, &mut entries);
    entries.sort();
    entries
}

fn collect_directory_entry_names(root: &Path, current: &Path, entries: &mut Vec<String>) {
    let mut children = fs::read_dir(current)
        .unwrap_or_else(|error| panic!("read directory {}: {error}", current.display()))
        .collect::<Result<Vec<_>, _>>()
        .unwrap_or_else(|error| panic!("collect directory {}: {error}", current.display()));
    children.sort_by_key(|entry| entry.path());

    for child in children {
        let path = child.path();
        entries.push(relative_entry_name(root, &path));
        if path.is_dir() {
            collect_directory_entry_names(root, &path, entries);
        }
    }
}

fn zip_entry_names(zip_path: &Path) -> Vec<String> {
    let file = fs::File::open(zip_path)
        .unwrap_or_else(|error| panic!("open ZIP {}: {error}", zip_path.display()));
    let mut archive = ZipArchive::new(BufReader::new(file))
        .unwrap_or_else(|error| panic!("read ZIP {}: {error}", zip_path.display()));
    let mut entries = Vec::new();
    for index in 0..archive.len() {
        let entry = archive
            .by_index(index)
            .unwrap_or_else(|error| panic!("read ZIP entry {index}: {error}"));
        entries.push(entry.name().trim_end_matches('/').replace('\\', "/"));
    }
    entries.sort();
    entries
}

fn extract_zip(zip_path: &Path, destination: &Path) {
    let file = fs::File::open(zip_path)
        .unwrap_or_else(|error| panic!("open ZIP {}: {error}", zip_path.display()));
    let mut archive = ZipArchive::new(BufReader::new(file))
        .unwrap_or_else(|error| panic!("read ZIP {}: {error}", zip_path.display()));
    archive.extract(destination).unwrap_or_else(|error| {
        panic!(
            "extract ZIP {} to {}: {error}",
            zip_path.display(),
            destination.display()
        )
    });
}

fn relative_entry_name(root: &Path, path: &Path) -> String {
    path.strip_prefix(root)
        .unwrap_or_else(|error| {
            panic!(
                "entry {} should be under {}: {error}",
                path.display(),
                root.display()
            )
        })
        .components()
        .map(|component| component.as_os_str().to_string_lossy())
        .collect::<Vec<_>>()
        .join("/")
}

fn assert_entries_do_not_contain_retained_dotnet_payload(entries: &[String], label: &str) {
    for entry in entries {
        let normalized = entry
            .replace('\\', "/")
            .trim_matches('/')
            .to_ascii_lowercase();
        let components = normalized.split('/').collect::<Vec<_>>();
        let first = components.first().copied().unwrap_or_default();
        let file_name = components.last().copied().unwrap_or_default();
        let contains_retained_payload = first == "dotnet"
            || first == "workers"
            || normalized.contains("/host/fxr/")
            || normalized.contains("/shared/microsoft.netcore.app/")
            || normalized.contains("/shared/microsoft.windowsdesktop.app/")
            || normalized.contains("/shared/microsoft.aspnetcore.app/")
            || matches!(
                file_name,
                "createdump.exe"
                    | "dotnet.exe"
                    | "hostfxr.dll"
                    | "coreclr.dll"
                    | "hostpolicy.dll"
                    | "clrjit.dll"
                    | "mscordaccore.dll"
                    | "mscordbi.dll"
                    | "mscorlib.dll"
                    | "netstandard.dll"
                    | "system.private.corelib.dll"
            )
            || file_name.ends_with(".runtimeconfig.json")
            || file_name.ends_with(".deps.json")
            || file_name.starts_with("easydict.compathost")
            || file_name.starts_with("easydict.nativebridge")
            || file_name.starts_with("easydict.sidecarclient")
            || file_name.starts_with("easydict.workers.")
            || file_name.starts_with("easydict.winui");
        assert!(
            !contains_retained_payload,
            "{label} should not contain retained .NET runtime/worker payload entry: {entry}"
        );
    }
}

#[cfg(windows)]
fn translate_long_doc_script_command(root: &Path) -> std::process::Command {
    powershell_script_command(&root.join("scripts/translate-long-doc.ps1"))
}

#[cfg(windows)]
fn powershell_script_command(script_path: &Path) -> std::process::Command {
    let shell = if std::process::Command::new("pwsh")
        .args(["-NoProfile", "-Command", "$PSVersionTable.PSVersion"])
        .output()
        .is_ok_and(|output| output.status.success())
    {
        "pwsh"
    } else {
        "powershell"
    };
    let mut command = std::process::Command::new(shell);
    command
        .args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"])
        .arg(script_path);
    command
}

#[cfg(windows)]
fn write_fake_package_portable_tool_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake Package-Portable tool dir");
    fs::write(
        fake_bin.join("cargo.cmd"),
        "@echo off\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo EASYDICT_RUNTIME_PROFILE=%EASYDICT_RUNTIME_PROFILE%\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo RUNTIME_PROFILE=%RUNTIME_PROFILE%\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=%EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS%\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo ARGS=%*\r\n\
exit /b 0\r\n",
    )
    .expect("write fake Package-Portable cargo");
    fs::write(
        fake_bin.join("dotnet.cmd"),
        "@echo off\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo FORBIDDEN_DOTNET=%*\r\n\
exit /b 87\r\n",
    )
    .expect("write fake Package-Portable dotnet");
}

#[cfg(windows)]
fn write_package_portable_wrapper(
    wrapper_path: &Path,
    package_script: &Path,
    output_root: &Path,
    post_env_record_path: &Path,
) {
    fs::write(
        wrapper_path,
        format!(
            "$ErrorActionPreference = 'Stop'\r\n\
$env:EASYDICT_RUNTIME_PROFILE = 'hybrid'\r\n\
$env:RUNTIME_PROFILE = 'hybrid'\r\n\
& {} -Platform x64 -Configuration Debug -OutputRoot {} -PackageVersion v0.0.0-shim -NoZip\r\n\
Add-Content -LiteralPath {} -Value \"POST_EASYDICT_RUNTIME_PROFILE=$env:EASYDICT_RUNTIME_PROFILE\"\r\n\
Add-Content -LiteralPath {} -Value \"POST_RUNTIME_PROFILE=$env:RUNTIME_PROFILE\"\r\n",
            powershell_literal(package_script),
            powershell_literal(output_root),
            powershell_literal(post_env_record_path),
            powershell_literal(post_env_record_path),
        ),
    )
    .expect("write Package-Portable wrapper");
}

#[cfg(windows)]
fn powershell_literal(path: &Path) -> String {
    format!("'{}'", path.display().to_string().replace('\'', "''"))
}

#[cfg(windows)]
fn assert_legacy_packaging_profile_rejected_before_tools(
    mut command: std::process::Command,
    script_name: &str,
    runtime_profile: Option<&str>,
    record_path: &Path,
) {
    let output = command.output().expect("run legacy packaging script");
    let output_text = powershell_output_text(&output);
    assert!(
        !output.status.success(),
        "{script_name} should reject {:?} before external tools\n{output_text}",
        runtime_profile
    );
    assert_contains(
        &output_text,
        "RuntimeProfile",
        &format!("{script_name} should explain the rejected runtime profile"),
    );
    if runtime_profile.is_some() {
        assert!(
            output_text.to_ascii_lowercase().contains("portable"),
            "{script_name} should redirect rust-only callers to the rs portable path\n{output_text}",
        );
    } else {
        assert_contains(
            &output_text,
            "Hybrid",
            &format!("{script_name} should require explicit Hybrid"),
        );
    }
    assert!(
        !record_path.exists(),
        "{script_name} must reject {:?} before invoking dotnet/cargo/winapp/ISCC; record:\n{}",
        runtime_profile,
        read_text(record_path)
    );
}

#[cfg(windows)]
fn powershell_output_text(output: &std::process::Output) -> String {
    format!(
        "stdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    )
}

#[cfg(windows)]
fn write_fake_legacy_packaging_forbidden_tool_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake legacy packaging tool dir");
    for tool in ["dotnet.cmd", "cargo.cmd", "winapp.cmd", "ISCC.cmd"] {
        fs::write(
            fake_bin.join(tool),
            format!(
                "@echo off\r\n\
>>\"%EASYDICT_LEGACY_PACKAGING_FORBIDDEN_TOOL_RECORD%\" echo {tool} %*\r\n\
exit /b 87\r\n"
            ),
        )
        .expect("write fake legacy packaging tool");
    }
}

#[cfg(windows)]
fn write_fake_long_doc_helper(path: &Path) {
    fs::write(
        path,
        "@echo off\r\n\
setlocal\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo HELPER=%~f0\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo ARGS=%*\r\n\
exit /b 0\r\n",
    )
    .expect("write fake LongDoc helper");
}

#[cfg(windows)]
fn write_fake_forbidden_tool_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake forbidden tool dir");
    for tool in ["cargo.cmd", "dotnet.cmd"] {
        fs::write(
            fake_bin.join(tool),
            format!(
                "@echo off\r\n\
>>\"%EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD%\" echo {tool} %*\r\n\
exit /b 87\r\n"
            ),
        )
        .expect("write fake forbidden tool");
    }
}

#[cfg(windows)]
fn write_fake_long_doc_cargo_script(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake LongDoc cargo dir");
    fs::write(
        fake_bin.join("cargo.cmd"),
        "@echo off\r\n\
setlocal\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo CARGO=%~f0\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo ARGS=%*\r\n\
exit /b 0\r\n",
    )
    .expect("write fake LongDoc cargo");
}

#[cfg(windows)]
fn write_fake_dotnet_forbidden_tool_script(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake dotnet forbidden tool dir");
    fs::write(
        fake_bin.join("dotnet.cmd"),
        "@echo off\r\n\
>>\"%EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD%\" echo dotnet.cmd %*\r\n\
exit /b 87\r\n",
    )
    .expect("write fake dotnet forbidden tool");
}

#[cfg(windows)]
fn write_stale_dotnet_payload_markers(app_dir: &Path) {
    fs::create_dir_all(app_dir.join("workers").join("longdoc"))
        .expect("create stale LongDoc worker dir");
    fs::create_dir_all(app_dir.join("workers").join("localai"))
        .expect("create stale LocalAI worker dir");
    fs::create_dir_all(app_dir.join("dotnet").join("host").join("fxr"))
        .expect("create stale dotnet runtime host dir");
    fs::create_dir_all(
        app_dir
            .join("dotnet")
            .join("shared")
            .join("Microsoft.NETCore.App"),
    )
    .expect("create stale dotnet shared runtime dir");
    fs::write(
        app_dir.join("Easydict.CompatHost.exe"),
        b"stale compat host",
    )
    .expect("write stale CompatHost marker");
    fs::write(
        app_dir
            .join("workers")
            .join("longdoc")
            .join("Easydict.Workers.LongDoc.exe"),
        b"stale longdoc worker",
    )
    .expect("write stale LongDoc worker marker");
    fs::write(app_dir.join("dotnet").join("dotnet.exe"), b"stale dotnet")
        .expect("write stale dotnet marker");
}

fn text_between<'a>(text: &'a str, start: &str, end: &str) -> &'a str {
    let after_start = text
        .split_once(start)
        .unwrap_or_else(|| panic!("missing section start: {start}"))
        .1;
    after_start
        .split_once(end)
        .unwrap_or_else(|| panic!("missing section end: {end}"))
        .0
}

fn tempfile_dir(name: &str) -> PathBuf {
    let stamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    std::env::temp_dir().join(format!("{name}-{stamp}"))
}

fn prepend_path(first: &Path, original_path: Option<&OsString>) -> OsString {
    let mut paths = vec![first.to_path_buf()];
    if let Some(original_path) = original_path {
        paths.extend(std::env::split_paths(original_path));
    }
    std::env::join_paths(paths).expect("join fake tool PATH")
}

fn write_fake_windows_ai_manifest_for_workspace(workspace: &Path) {
    let manifest_path = workspace
        .parent()
        .expect("fake workspace should have a parent")
        .join("lib")
        .join("easydict-windows-ai")
        .join("Cargo.toml");
    fs::create_dir_all(manifest_path.parent().expect("manifest parent"))
        .expect("create fake WindowsAI manifest dir");
    fs::write(
        manifest_path,
        "[package]\nname = \"easydict_windows_ai\"\nversion = \"0.0.0\"\nedition = \"2021\"\n",
    )
    .expect("write fake WindowsAI manifest");
}

#[cfg(windows)]
fn write_fake_tooling_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake tool dir");
    let source_path = fake_bin.join("fake-tool.rs");
    fs::write(
        &source_path,
        r#"
use std::{env, fs};

fn main() {
    let exe_name = env::current_exe()
        .ok()
        .and_then(|path| path.file_stem().map(|name| name.to_string_lossy().to_string()))
        .unwrap_or_default();
    if exe_name.eq_ignore_ascii_case("rustup") {
        return;
    }
    if matches!(
        exe_name.to_ascii_lowercase().as_str(),
        "dotnet" | "powershell" | "pwsh"
    ) {
        if let Ok(record_path) = env::var("EASYDICT_RELEASE_FORBIDDEN_TOOL_RECORD") {
            let args = env::args().skip(1).collect::<Vec<_>>().join(" ");
            use std::io::Write as _;
            fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&record_path)
                .and_then(|mut file| writeln!(file, "{} {}", exe_name, args))
                .expect("append forbidden tool record");
        }
        std::process::exit(87);
    }
    if exe_name.eq_ignore_ascii_case("easydict_cli") {
        let record_path = env::var("EASYDICT_PACKAGED_CLI_RECORD").expect("packaged CLI record path");
        let args = env::args().skip(1).collect::<Vec<_>>().join(" ");
        use std::io::Write as _;
        fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&record_path)
            .and_then(|mut file| {
                writeln!(file, "CLI={}", exe_name)?;
                writeln!(file, "ARGS={}", args)?;
                writeln!(file, "EASYDICT_RUNTIME_PROFILE={}", env::var("EASYDICT_RUNTIME_PROFILE").unwrap_or_default())?;
                writeln!(file, "RUNTIME_PROFILE={}", env::var("RUNTIME_PROFILE").unwrap_or_default())
            })
            .expect("append packaged CLI record");
        println!("easydict_cli packaged smoke");
        return;
    }

    let record_path = env::var("EASYDICT_FAKE_CARGO_RECORD").expect("record path");
    let args = env::args().skip(1).collect::<Vec<_>>().join(" ");
    use std::io::Write as _;
    fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&record_path)
        .and_then(|mut file| {
            writeln!(file, "EASYDICT_RUNTIME_PROFILE={}", env::var("EASYDICT_RUNTIME_PROFILE").unwrap_or_default())?;
            writeln!(file, "RUNTIME_PROFILE={}", env::var("RUNTIME_PROFILE").unwrap_or_default())?;
            writeln!(file, "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS={}", env::var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS").unwrap_or_default())?;
            writeln!(file, "ARGS={}", args)
        })
        .expect("append cargo record");

    let target = env::current_dir()
        .expect("current dir")
        .join("target")
        .join("x86_64-pc-windows-msvc")
        .join("release");
    fs::create_dir_all(&target).expect("create fake target dir");
    let self_path = env::current_exe().expect("current exe path");
    fs::copy(&self_path, target.join("easydict_preview_iced.exe")).expect("write preview exe");
    for exe in [
        "easydict-native-bridge.exe",
        "easydict_browser_registrar.exe",
        "easydict_cli.exe",
        "easydict_long_doc.exe",
    ] {
        fs::copy(&self_path, target.join(exe)).expect("write helper exe");
    }
}
"#,
    )
    .expect("write fake tool source");
    let cargo_exe = fake_bin.join("cargo.exe");
    let status = std::process::Command::new("rustc")
        .arg(&source_path)
        .arg("-o")
        .arg(&cargo_exe)
        .status()
        .expect("compile fake cargo executable");
    assert!(status.success(), "fake cargo executable should compile");
    fs::copy(&cargo_exe, fake_bin.join("rustup.exe")).expect("copy fake rustup executable");
}

#[cfg(windows)]
fn write_fake_release_forbidden_tool_exes(fake_bin: &Path) {
    let cargo_exe = fake_bin.join("cargo.exe");
    for tool in ["dotnet.exe", "powershell.exe", "pwsh.exe"] {
        fs::copy(&cargo_exe, fake_bin.join(tool))
            .unwrap_or_else(|error| panic!("copy fake forbidden tool {tool}: {error}"));
    }
}

#[cfg(not(windows))]
fn write_fake_tooling_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake tool dir");
    write_executable(fake_bin.join("rustup"), "#!/bin/sh\nexit 0\n");
    write_executable(
        fake_bin.join("cargo"),
        "#!/bin/sh\n\
{\n\
printf 'EASYDICT_RUNTIME_PROFILE=%s\\n' \"$EASYDICT_RUNTIME_PROFILE\"\n\
printf 'RUNTIME_PROFILE=%s\\n' \"$RUNTIME_PROFILE\"\n\
printf 'EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=%s\\n' \"$EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS\"\n\
printf 'ARGS=%s\\n' \"$*\"\n\
} >> \"$EASYDICT_FAKE_CARGO_RECORD\"\n\
target=\"$PWD/target/x86_64-pc-windows-msvc/release\"\n\
mkdir -p \"$target\"\n\
printf 'fake' > \"$target/easydict_preview_iced.exe\"\n\
for f in easydict-native-bridge.exe easydict_browser_registrar.exe easydict_cli.exe easydict_long_doc.exe; do\n\
  printf 'fake' > \"$target/$f\"\n\
done\n\
exit 0\n",
    );
}

#[cfg(not(windows))]
fn write_executable(path: PathBuf, contents: &str) {
    use std::os::unix::fs::PermissionsExt;

    fs::write(&path, contents).expect("write executable script");
    let mut permissions = fs::metadata(&path)
        .expect("read executable metadata")
        .permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(path, permissions).expect("chmod executable script");
}

struct EnvironmentSnapshot {
    values: Vec<(&'static str, Option<OsString>)>,
}

impl EnvironmentSnapshot {
    fn capture<const N: usize>(names: [&'static str; N]) -> Self {
        Self {
            values: names
                .into_iter()
                .map(|name| (name, std::env::var_os(name)))
                .collect(),
        }
    }

    fn original_path(&self) -> Option<&OsString> {
        self.values
            .iter()
            .find(|(name, _)| *name == "PATH")
            .and_then(|(_, value)| value.as_ref())
    }
}

impl Drop for EnvironmentSnapshot {
    fn drop(&mut self) {
        for (name, value) in self.values.iter() {
            match value {
                Some(value) => std::env::set_var(name, value),
                None => std::env::remove_var(name),
            }
        }
    }
}

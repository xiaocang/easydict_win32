use easydict_packager::{
    build_rust_helpers, pack_rs_portable, BuildRustHelpersOptions, PackRustPortableOptions,
};
use std::ffi::OsString;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

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
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let test_root = tempfile_dir("packager-build-rust-helpers-env");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_dir = test_root.join("out");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    fs::create_dir_all(&output_dir).expect("create output dir");
    write_fake_tooling_scripts(&fake_bin);
    let record_path = test_root.join("cargo-env.txt");

    let path_with_fake_tools = prepend_path(&fake_bin, environment.original_path());
    std::env::set_var("PATH", path_with_fake_tools);
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
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
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let test_root = tempfile_dir("packager-pack-rs-portable-env");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_root = test_root.join("out");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    fs::create_dir_all(&output_root).expect("create output root");
    write_fake_tooling_scripts(&fake_bin);
    let record_path = test_root.join("cargo-env.txt");

    let path_with_fake_tools = prepend_path(&fake_bin, environment.original_path());
    std::env::set_var("PATH", path_with_fake_tools);
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
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
        2,
        "pack-rs-portable should force Easydict rust-only env for both child cargo builds:\n{record}"
    );
    assert_eq!(
        record
            .lines()
            .filter(|line| *line == "RUNTIME_PROFILE=rust-only")
            .count(),
        2,
        "pack-rs-portable should force generic rust-only env for both child cargo builds:\n{record}"
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
            writeln!(file, "ARGS={}", args)
        })
        .expect("append cargo record");

    let target = env::current_dir()
        .expect("current dir")
        .join("target")
        .join("x86_64-pc-windows-msvc")
        .join("release");
    fs::create_dir_all(&target).expect("create fake target dir");
    fs::write(target.join("easydict_preview_iced.exe"), b"fake").expect("write preview exe");
    for exe in [
        "easydict-native-bridge.exe",
        "easydict_browser_registrar.exe",
        "easydict_cli.exe",
        "easydict_long_doc.exe",
    ] {
        fs::write(target.join(exe), b"fake").expect("write helper exe");
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

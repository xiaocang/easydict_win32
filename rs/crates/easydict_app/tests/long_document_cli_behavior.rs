#![cfg(windows)]

use std::fs;
use std::path::PathBuf;
use std::process::{Command, Output};
use std::time::{SystemTime, UNIX_EPOCH};

#[test]
fn help_lists_long_document_options() {
    let output = long_doc_cli()
        .arg("--help")
        .output()
        .expect("long document CLI should run");

    assert_success(&output);
    let stdout = stdout(&output);
    for expected in [
        "--help",
        "--list-services",
        "--input",
        "--target-language",
        "--from",
        "--output",
        "--service",
        "--output-mode",
        "--layout",
        "--pdf-export-mode",
        "--page",
        "--page-range",
        "--max-concurrency",
    ] {
        assert!(
            stdout.contains(expected),
            "help should mention {expected}\nstdout:\n{stdout}"
        );
    }
    assert!(
        !stdout.contains("--app-dir"),
        "legacy no-op app-dir should stay hidden from first rs portable help\nstdout:\n{stdout}"
    );
    assert!(
        stderr(&output).trim().is_empty(),
        "help should not write diagnostics to stderr:\n{}",
        stderr(&output)
    );
}

#[test]
fn list_services_succeeds_without_document_arguments() {
    let settings_dir = unique_temp_dir("easydict-long-doc-cli-list-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = long_doc_cli()
        .arg("--list-services")
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .output()
        .expect("long document CLI should run");

    assert_success(&output);
    let stdout = stdout(&output);
    let normalized = stdout.to_ascii_lowercase();
    for expected in ["google", "openai"] {
        assert!(
            normalized.contains(expected),
            "service list should include {expected}\nstdout:\n{stdout}"
        );
    }
    assert!(
        stdout
            .lines()
            .filter(|line| !line.trim().is_empty())
            .count()
            >= 2,
        "service list should contain multiple service rows\nstdout:\n{stdout}"
    );
    assert!(
        stderr(&output).trim().is_empty(),
        "service listing should not write diagnostics to stderr:\n{}",
        stderr(&output)
    );

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn command_requires_input() {
    let output = long_doc_cli()
        .args(["--target-language", "zh-Hans", "--service", "google"])
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        stderr.to_ascii_lowercase().contains("input"),
        "stderr should explain that --input is required:\n{stderr}"
    );
    assert!(
        stdout(&output).trim().is_empty(),
        "missing input should not write output"
    );
}

#[test]
fn command_requires_target_language() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-target");
    fs::create_dir_all(&work_dir).expect("work directory should be created");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello long document").expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args(["--service", "google"])
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    let normalized = stderr.to_ascii_lowercase();
    assert!(
        normalized.contains("target") && normalized.contains("language"),
        "stderr should explain that --target-language is required:\n{stderr}"
    );
    assert!(
        stdout(&output).trim().is_empty(),
        "missing target language should not write output"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn page_and_page_range_are_mutually_exclusive() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-page-conflict");
    let app_dir = work_dir.join("app");
    fs::create_dir_all(&app_dir).expect("app directory should be created");
    let input_path = work_dir.join("sample.pdf");
    let output_path = work_dir.join("translated.txt");
    fs::write(
        &input_path,
        "%PDF-1.7\n% parse conflict should win before IO",
    )
    .expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "zh-Hans",
            "--from",
            "en",
            "--output-mode",
            "bilingual",
            "--service",
            "google",
            "--page",
            "2",
            "--page-range",
            "1-3",
            "--max-concurrency",
            "3",
            "--app-dir",
        ])
        .arg(&app_dir)
        .arg("--output")
        .arg(&output_path)
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    let normalized = stderr.to_ascii_lowercase();
    assert!(
        normalized.contains("--page") && normalized.contains("--page-range"),
        "stderr should describe the page/page-range conflict:\n{stderr}"
    );
    assert!(
        !normalized.contains("unknown option"),
        "all long document options should be recognized before conflict validation:\n{stderr}"
    );
    assert!(
        stdout(&output).trim().is_empty(),
        "conflicting page options should not write output"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn app_dir_is_legacy_noop_and_does_not_enable_retained_worker_lookup() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-appdir-no-worker");
    let app_dir = work_dir.join("app");
    let settings_dir = work_dir.join("settings");
    fs::create_dir_all(&app_dir).expect("app directory should be created");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello long document").expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "zh-Hans",
            "--from",
            "en",
            "--service",
            "windows-local-ai",
            "--app-dir",
        ])
        .arg(&app_dir)
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env("EASYDICT_FOUNDRY_LOCAL_CLI", "__missing_foundry_cli__.cmd")
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should require a Rust-native LongDoc route:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET workers"),
        "default CLI error should not mention retired retained runtime:\n{stderr}"
    );
    assert!(
        !stderr.contains("Long Document worker executable"),
        "--app-dir must not probe retained LongDoc worker paths:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "LongDoc CLI should not describe a compat host route:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn target_auto_fails_before_native_or_retained_worker_lookup() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-target-auto");
    let settings_dir = work_dir.join("settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello long document").expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "auto",
            "--from",
            "en",
            "--service",
            "windows-local-ai",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env("EASYDICT_FOUNDRY_LOCAL_CLI", "__missing_foundry_cli__.cmd")
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        stderr.contains("Long Document target language cannot be Auto"),
        "stderr should reject target Auto before provider lookup:\n{stderr}"
    );
    assert!(
        !stderr.contains("Long Document worker"),
        "target Auto should not probe retained LongDoc workers:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET workers"),
        "target Auto should not expose retained .NET worker details:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "target Auto should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn env_overrides_local_ai_provider_and_openvino_cache_dir_for_native_preflight() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-openvino-env");
    let settings_dir = work_dir.join("settings");
    let cache_dir = work_dir.join("cache");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::create_dir_all(&cache_dir).expect("cache directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello local OpenVINO long document")
        .expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "zh-Hans",
            "--from",
            "en",
            "--service",
            "windows-local-ai",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env("EASYDICT_LOCAL_AI_PROVIDER", "openvino")
        .env("EASYDICT_OPENVINO_CACHE_DIR", &cache_dir)
        .env("EASYDICT_OPENVINO_DEVICE", "GPU")
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        stderr.contains("OpenVINO runtime or NLLB-200 model is not downloaded"),
        "stderr should report the native OpenVINO download preflight:\n{stderr}"
    );
    assert!(
        stderr.contains("Download model"),
        "stderr should guide users to download the OpenVINO model:\n{stderr}"
    );
    assert!(
        !stderr.contains("requires a Rust-native route"),
        "OpenVINO env route should not fall back to generic retained worker wording:\n{stderr}"
    );
    assert!(
        !stderr.contains("Long Document worker"),
        "OpenVINO env route should not probe retained LongDoc workers:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "OpenVINO env route should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn env_overrides_foundry_local_endpoint_and_model_before_worker_lookup() {
    let work_dir = unique_temp_dir("easydict-long-doc-cli-foundry-env");
    let settings_dir = work_dir.join("settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");
    let input_path = work_dir.join("sample.txt");
    fs::write(&input_path, "Hello Foundry Local long document")
        .expect("sample input should be written");

    let output = long_doc_cli()
        .arg("--input")
        .arg(&input_path)
        .args([
            "--target-language",
            "zh-Hans",
            "--from",
            "en",
            "--service",
            "windows-local-ai",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env("EASYDICT_LOCAL_AI_PROVIDER", "foundry-local")
        .env("EASYDICT_FOUNDRY_LOCAL_ENDPOINT", "foundry-local-invalid")
        .env("EASYDICT_FOUNDRY_LOCAL_MODEL", "cli-foundry-model")
        .output()
        .expect("long document CLI should run");

    assert_failure(&output);
    let stderr = stderr(&output);
    assert!(
        !stderr.contains("requires a Rust-native route"),
        "Foundry env route should enter native LocalAI handling:\n{stderr}"
    );
    assert!(
        !stderr.contains("Long Document worker"),
        "Foundry env route should not probe retained LongDoc workers:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "Foundry env route should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

fn long_doc_cli() -> Command {
    let binary = option_env!("CARGO_BIN_EXE_easydict_long_doc")
        .expect("easydict_long_doc binary should be built for integration tests");
    Command::new(binary)
}

fn assert_success(output: &Output) {
    assert!(
        output.status.success(),
        "command should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(output),
        stderr(output)
    );
}

fn assert_failure(output: &Output) {
    assert!(
        !output.status.success(),
        "command should fail\nstdout:\n{}\nstderr:\n{}",
        stdout(output),
        stderr(output)
    );
}

fn stdout(output: &Output) -> String {
    String::from_utf8_lossy(&output.stdout).into_owned()
}

fn stderr(output: &Output) -> String {
    String::from_utf8_lossy(&output.stderr).into_owned()
}

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after unix epoch")
        .as_nanos();
    std::env::temp_dir().join(format!("{prefix}-{}-{nanos}", std::process::id()))
}

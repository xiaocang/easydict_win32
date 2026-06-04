#![cfg(windows)]

use easydict_app::{
    DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE, FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE,
    RUNTIME_PROFILE_ENVIRONMENT_VARIABLE,
};
use std::fs;
use std::path::PathBuf;
use std::process::{Command, Output};
use std::time::{SystemTime, UNIX_EPOCH};

#[test]
fn translate_command_rejects_retired_generic_worker_route() {
    let output = cli_with_missing_host("translate")
        .args([
            "--service",
            "mock",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
        ])
        .output()
        .expect("CLI should run");

    assert_retired_generic_route_error(&output);
}

#[test]
fn stream_command_rejects_retired_generic_worker_route() {
    let output = cli_with_missing_host("stream")
        .args([
            "--service",
            "mock",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Good morning",
            "--json",
        ])
        .output()
        .expect("CLI should run");

    assert_retired_generic_route_error(&output);
}

#[test]
fn grammar_command_rejects_retired_generic_worker_route() {
    let output = cli_with_missing_host("grammar")
        .args([
            "--service",
            "mock",
            "--language",
            "en",
            "--text",
            "I has a apple.",
        ])
        .output()
        .expect("CLI should run");

    assert_retired_generic_route_error(&output);
}

#[test]
fn native_openai_configuration_error_does_not_spawn_missing_worker() {
    let settings_dir = unique_temp_dir("easydict-cli-native-openai");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "openai,google",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("API key"),
        "stderr should report native configuration error:\n{stderr}"
    );
    assert!(
        !stderr.contains("worker executable not found"),
        "native OpenAI path should not try to spawn the missing worker:\n{stderr}"
    );

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_grammar_selects_first_grammar_capable_service_without_worker() {
    let settings_dir = unique_temp_dir("easydict-cli-native-grammar");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("grammar")
        .args([
            "--service",
            "google,openai",
            "--language",
            "en",
            "--text",
            "I has a apple.",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("API key"),
        "stderr should report native OpenAI configuration error:\n{stderr}"
    );
    assert!(
        !stderr.contains("worker executable not found"),
        "native grammar path should not try to spawn the missing worker:\n{stderr}"
    );

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn local_ai_cli_app_dir_no_longer_enables_retained_worker_fallback() {
    let app_dir = unique_temp_dir("easydict-cli-local-ai-app");
    let settings_dir = unique_temp_dir("easydict-cli-local-ai-settings");
    fs::create_dir_all(&app_dir).expect("app directory should be created");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "windows-local-ai",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--app-dir",
        ])
        .arg(&app_dir)
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid")
        .env(
            FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE,
            "__missing_foundry_cli__.cmd",
        )
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should require a Rust-native LocalAI route:\n{stderr}"
    );
    assert!(
        stderr.contains("retained .NET Local AI worker fallback is no longer available"),
        "stderr should report the retired CLI worker fallback:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "--app-dir should not probe retained worker paths:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "LocalAI CLI should not describe a compat host route:\n{stderr}"
    );

    let _ = fs::remove_dir_all(app_dir);
    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn local_ai_cli_default_rs_profile_disables_packaged_worker_fallback() {
    let app_dir = unique_temp_dir("easydict-cli-local-ai-default-rust-only-app");
    let settings_dir = unique_temp_dir("easydict-cli-local-ai-default-rust-only-settings");
    fs::create_dir_all(&app_dir).expect("app directory should be created");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "windows-local-ai",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--app-dir",
        ])
        .arg(&app_dir)
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env_remove(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE)
        .env(
            FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE,
            "__missing_foundry_cli__.cmd",
        )
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should report the default Rust-only worker policy:\n{stderr}"
    );
    assert!(
        stderr.contains(".NET Local AI workers"),
        "stderr should name the disabled retained runtime:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "default rs profile should not probe retained worker paths:\n{stderr}"
    );

    let _ = fs::remove_dir_all(app_dir);
    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn local_ai_cli_without_app_dir_fails_native_only_without_worker_lookup() {
    let settings_dir = unique_temp_dir("easydict-cli-local-ai-explicit-appdir-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "windows-local-ai",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid")
        .env(
            FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE,
            "__missing_foundry_cli__.cmd",
        )
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should require a Rust-native LocalAI route:\n{stderr}"
    );
    assert!(
        stderr.contains("retained .NET Local AI worker fallback is no longer available"),
        "stderr should report the retired CLI worker fallback:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "CLI should not probe retained worker paths:\n{stderr}"
    );
    assert!(!stderr.to_ascii_lowercase().contains("compat host"));

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn local_ai_cli_host_hint_no_longer_enables_retained_worker_fallback() {
    let settings_dir = unique_temp_dir("easydict-cli-local-ai-host-disabled-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "windows-local-ai",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--host",
            "C:/Tools/Easydict.Workers.LocalAi.exe",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid")
        .env(
            FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE,
            "__missing_foundry_cli__.cmd",
        )
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should require a Rust-native LocalAI route:\n{stderr}"
    );
    assert!(
        stderr.contains("retained .NET Local AI worker fallback is no longer available"),
        "stderr should explain that --host no longer opts into LocalAI worker fallback:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "legacy --host should not probe retained worker paths:\n{stderr}"
    );
    assert!(!stderr.to_ascii_lowercase().contains("compat host"));

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn local_ai_cli_fallback_honors_disabled_retained_worker_policy() {
    let app_dir = unique_temp_dir("easydict-cli-local-ai-disabled-worker-app");
    let settings_dir = unique_temp_dir("easydict-cli-local-ai-disabled-worker-settings");
    fs::create_dir_all(&app_dir).expect("app directory should be created");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "windows-local-ai",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--app-dir",
        ])
        .arg(&app_dir)
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid")
        .env(DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE, "1")
        .env(
            FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE,
            "__missing_foundry_cli__.cmd",
        )
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should report disabled retained LocalAI worker policy:\n{stderr}"
    );
    assert!(
        stderr.contains(".NET Local AI workers"),
        "stderr should name retained .NET LocalAI workers:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "disabled retained worker policy should win before packaged worker lookup:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "disabled retained worker policy should not describe a compat host route:\n{stderr}"
    );

    let _ = fs::remove_dir_all(app_dir);
    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn auto_local_ai_cli_probes_foundry_before_native_only_failure() {
    let app_dir = unique_temp_dir("easydict-cli-auto-foundry-app");
    let settings_dir = unique_temp_dir("easydict-cli-auto-foundry-settings");
    let fake_foundry_dir = unique_temp_dir("easydict-cli-fake-foundry");
    fs::create_dir_all(&app_dir).expect("app directory should be created");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::create_dir_all(&fake_foundry_dir).expect("fake Foundry directory should be created");
    let marker_path = fake_foundry_dir.join("foundry-calls.txt");
    let fake_foundry_path = fake_foundry_dir.join("foundry.cmd");
    fs::write(
        &fake_foundry_path,
        format!(
            "@echo off\r\necho %*>>\"{}\"\r\necho Foundry Local endpoint: http://127.0.0.1:1/v1/chat/completions\r\n",
            marker_path.display()
        ),
    )
    .expect("fake Foundry CLI should be written");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "windows-local-ai",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--app-dir",
        ])
        .arg(&app_dir)
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE, &fake_foundry_path)
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        fs::read_to_string(&marker_path)
            .expect("fake Foundry CLI should be called")
            .contains("service status"),
        "CLI should probe Foundry status before local failure:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "Foundry probe should happen without packaged LocalAI worker lookup:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "Foundry probe should not describe a compat host route:\n{stderr}"
    );

    let _ = fs::remove_dir_all(app_dir);
    let _ = fs::remove_dir_all(settings_dir);
    let _ = fs::remove_dir_all(fake_foundry_dir);
}

fn cli_with_missing_host(subcommand: &str) -> Command {
    let mut command = Command::new(env!("CARGO_BIN_EXE_easydict_cli"));
    command.arg(subcommand);
    command.arg("--host").arg("__missing_worker__.exe");
    command
}

fn assert_retired_generic_route_error(output: &Output) {
    assert!(
        !output.status.success(),
        "CLI should fail without retired generic worker route\nstdout:\n{}\nstderr:\n{}",
        stdout(output),
        stderr(output)
    );
    let stderr = stderr(output);
    assert!(
        stderr.contains("No Rust-native quick translate route"),
        "stderr should report retired generic route:\n{stderr}"
    );
    assert!(
        !stderr.contains("worker executable not found"),
        "retired generic route should not spawn the missing worker:\n{stderr}"
    );
    assert!(
        stdout(output).trim().is_empty(),
        "stdout should stay empty for the unsupported route"
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

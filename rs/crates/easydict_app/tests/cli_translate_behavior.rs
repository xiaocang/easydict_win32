#![cfg(windows)]

use easydict_app::FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE;
use std::fs;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpListener;
use std::path::PathBuf;
use std::process::{Command, Output, Stdio};
use std::sync::mpsc;
use std::thread;
use std::time::Duration;
use std::time::{SystemTime, UNIX_EPOCH};

const RUNTIME_PROFILE_ENVIRONMENT_VARIABLE: &str = "EASYDICT_RUNTIME_PROFILE";
const DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE: &str = "EASYDICT_DISABLE_LOCALAI_WORKER";
const LOCAL_AI_ENVIRONMENT_OVERRIDE_KEYS: &[&str] = &[
    "EASYDICT_LOCAL_AI_PROVIDER",
    "LOCAL_AI_PROVIDER",
    "EASYDICT_FOUNDRY_LOCAL_ENDPOINT",
    "FOUNDRY_LOCAL_ENDPOINT",
    "EASYDICT_FOUNDRY_LOCAL_MODEL",
    "FOUNDRY_LOCAL_MODEL",
    "EASYDICT_OPENVINO_DEVICE",
    "EASYDICT_OPEN_VINO_DEVICE",
    "OPENVINO_DEVICE",
    "EASYDICT_OPENVINO_CACHE_DIR",
    "EASYDICT_CACHE_DIR",
];

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
fn stream_command_writes_openai_chunks_before_sse_response_completes() {
    let settings_dir = unique_temp_dir("easydict-cli-openai-stream-live-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("SSE listener should bind");
    let endpoint = format!(
        "http://{}/v1/chat/completions",
        listener.local_addr().unwrap()
    );
    fs::write(
        settings_dir.join("settings.json"),
        format!(
            r#"{{
  "OpenAIApiKey": "sk-cli-stream",
  "OpenAIEndpoint": "{endpoint}",
  "OpenAIModel": "gpt-4o-mini",
  "OpenAIApiFormatOverride": "ChatCompletions"
}}"#
        ),
    )
    .expect("settings file should be created");

    let (continue_tx, continue_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = listener.accept().expect("CLI should connect");
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
        }

        stream
            .write_all(
                b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n",
            )
            .unwrap();
        stream
            .write_all("data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n".as_bytes())
            .unwrap();
        stream.flush().unwrap();
        continue_rx
            .recv_timeout(Duration::from_secs(10))
            .expect("test should allow stream completion");
        stream
            .write_all(
                "data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n\
                  data: [DONE]\n\n"
                    .as_bytes(),
            )
            .unwrap();
        stream.flush().unwrap();
    });

    let mut child = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("stream")
        .args([
            "--service",
            "openai",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("CLI should spawn");

    let stdout = child.stdout.take().expect("stdout should be piped");
    let (line_tx, line_rx) = mpsc::channel::<String>();
    let stdout_reader = thread::spawn(move || {
        let mut reader = BufReader::new(stdout);
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 {
                break;
            }
            line_tx
                .send(line.trim_end_matches(['\r', '\n']).to_string())
                .unwrap();
        }
    });

    let first_line = line_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("first chunk should be printed before SSE response completes");
    assert!(
        first_line.contains("\"event\":\"chunk\"") && first_line.contains("\"text\":\"你\""),
        "first stdout line should be the first chunk, got {first_line}"
    );

    continue_tx
        .send(())
        .expect("server should still wait for continuation");
    let second_line = line_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("second chunk should be printed after continuation");
    let done_line = line_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("done event should be printed after chunks");

    let status = child.wait().expect("CLI should exit");
    stdout_reader.join().unwrap();
    server.join().unwrap();
    let mut stderr_text = String::new();
    child
        .stderr
        .take()
        .unwrap()
        .read_to_string(&mut stderr_text)
        .unwrap();

    assert!(status.success(), "CLI failed: {stderr_text}");
    assert!(
        second_line.contains("\"event\":\"chunk\"") && second_line.contains("\"text\":\"好\""),
        "second stdout line should be the second chunk, got {second_line}"
    );
    assert!(
        done_line.contains("\"event\":\"done\"")
            && done_line.contains("\"translatedText\":\"你好\""),
        "done stdout line should contain final result, got {done_line}"
    );
    assert!(
        !stderr_text.to_ascii_lowercase().contains("compat host")
            && !stderr_text.contains("worker executable"),
        "native CLI stream should not mention retained worker paths: {stderr_text}"
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
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should require a Rust-native LocalAI route:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET Local AI workers"),
        "default CLI errors should not expose retained .NET worker details:\n{stderr}"
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
fn local_ai_stream_cli_host_hint_no_longer_enables_retained_worker_fallback() {
    let settings_dir = unique_temp_dir("easydict-cli-local-ai-stream-host-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("stream")
        .args([
            "--service",
            "windows-local-ai",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--json",
            "--host",
            "C:/Tools/workers/localai/Easydict.Workers.LocalAi.exe",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid")
        .env(
            FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE,
            "__missing_foundry_cli__.cmd",
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert_local_ai_cli_does_not_probe_retained_worker(&output, "stream --host");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn local_ai_cli_host_and_app_dir_hints_are_legacy_noops_together() {
    let app_dir = unique_temp_dir("easydict-cli-local-ai-host-appdir-app");
    let settings_dir = unique_temp_dir("easydict-cli-local-ai-host-appdir-settings");
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
            "--host",
            "C:/Tools/workers/localai/Easydict.Workers.LocalAi.exe",
            "--host-arg",
            "--trace",
            "--app-dir",
        ])
        .arg(&app_dir)
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid")
        .env(
            FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE,
            "__missing_foundry_cli__.cmd",
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert_local_ai_cli_does_not_probe_retained_worker(&output, "translate --host --app-dir");

    let _ = fs::remove_dir_all(app_dir);
    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn local_ai_batch_cli_app_dir_no_longer_enables_retained_worker_fallback() {
    let app_dir = unique_temp_dir("easydict-cli-local-ai-batch-app");
    let settings_dir = unique_temp_dir("easydict-cli-local-ai-batch-settings");
    fs::create_dir_all(&app_dir).expect("app directory should be created");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("batch")
        .args([
            "--service",
            "windows-local-ai",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello\nGood morning",
            "--app-dir",
        ])
        .arg(&app_dir)
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid")
        .env(
            FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE,
            "__missing_foundry_cli__.cmd",
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert_local_ai_cli_does_not_probe_retained_worker(&output, "batch --app-dir");

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
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should report the default Rust-only worker policy:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET Local AI workers"),
        "default CLI errors should not expose retained .NET worker details:\n{stderr}"
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
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should require a Rust-native LocalAI route:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET Local AI workers"),
        "default CLI errors should not expose retained .NET worker details:\n{stderr}"
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
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should require a Rust-native LocalAI route:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET Local AI workers"),
        "default CLI errors should not expose retained .NET worker details:\n{stderr}"
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
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "stderr should report disabled retained LocalAI worker policy:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET Local AI workers"),
        "default CLI errors should not expose retained .NET worker details:\n{stderr}"
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
        .remove_local_ai_env_overrides()
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

#[test]
fn auto_local_ai_cli_rejects_foundry_cli_override_targeting_retained_worker_before_spawn() {
    let app_dir = unique_temp_dir("easydict-cli-auto-foundry-bad-override-app");
    let settings_dir = unique_temp_dir("easydict-cli-auto-foundry-bad-override-settings");
    let fake_foundry_dir = unique_temp_dir("easydict-cli-bad-foundry-override");
    fs::create_dir_all(&app_dir).expect("app directory should be created");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::create_dir_all(&fake_foundry_dir).expect("fake Foundry directory should be created");
    let marker_path = fake_foundry_dir.join("retained-worker-cli-was-spawned.txt");
    let fake_foundry_path = fake_foundry_dir.join("Easydict.Workers.LocalAi.exe.cmd");
    fs::write(
        &fake_foundry_path,
        format!(
            "@echo off\r\necho spawned >\"{}\"\r\necho Foundry Local endpoint: http://127.0.0.1:1/v1/chat/completions\r\n",
            marker_path.display()
        ),
    )
    .expect("fake retained-worker CLI should be written");

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
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        !marker_path.exists(),
        "Foundry CLI override that points at a retained worker name must not be spawned:\n{stderr}"
    );
    assert!(!stderr.contains("Local AI worker executable not found"));
    assert!(!stderr.to_ascii_lowercase().contains("compat host"));

    let _ = fs::remove_dir_all(app_dir);
    let _ = fs::remove_dir_all(settings_dir);
    let _ = fs::remove_dir_all(fake_foundry_dir);
}

#[test]
fn local_ai_cli_env_overrides_provider_and_openvino_cache_dir_before_worker_lookup() {
    let work_dir = unique_temp_dir("easydict-cli-openvino-env");
    let settings_dir = work_dir.join("settings");
    let cache_dir = work_dir.join("cache");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::create_dir_all(&cache_dir).expect("cache directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");

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
        .remove_local_ai_env_overrides()
        .env("EASYDICT_LOCAL_AI_PROVIDER", "open_vino")
        .env("EASYDICT_OPENVINO_CACHE_DIR", &cache_dir)
        .env("EASYDICT_OPENVINO_DEVICE", "GPU")
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
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
        !stderr.contains("Local AI worker executable not found"),
        "OpenVINO env route should not probe retained LocalAI workers:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "OpenVINO env route should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn explicit_windows_ai_cli_uses_native_phi_client_before_worker_required_error() {
    let work_dir = unique_temp_dir("easydict-cli-explicit-windows-ai-native");
    let settings_dir = work_dir.join("settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");

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
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("Phi Silica"),
        "explicit WindowsAI CLI should use the native Phi client boundary:\n{stderr}"
    );
    assert!(
        !stderr.contains("requires a Rust-native route"),
        "explicit WindowsAI CLI should not stop at the generic worker-required fallback:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "explicit WindowsAI CLI should not probe retained LocalAI workers:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "explicit WindowsAI CLI should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn explicit_windows_ai_stream_cli_uses_native_phi_client_without_worker_probe() {
    let work_dir = unique_temp_dir("easydict-cli-explicit-windows-ai-stream-native");
    let settings_dir = work_dir.join("settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("stream")
        .args([
            "--service",
            "windows-local-ai",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("Phi Silica"),
        "explicit WindowsAI stream CLI should use the native Phi client boundary:\n{stderr}"
    );
    assert!(
        !stderr.contains("requires a Rust-native route"),
        "explicit WindowsAI stream CLI should not stop at the generic worker-required fallback:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "explicit WindowsAI stream CLI should not probe retained LocalAI workers:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "explicit WindowsAI stream CLI should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn openvino_local_ai_grammar_cli_fails_locally_without_worker_lookup() {
    let work_dir = unique_temp_dir("easydict-cli-openvino-grammar");
    let settings_dir = work_dir.join("settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("grammar")
        .args([
            "--service",
            "windows-local-ai",
            "--language",
            "en",
            "--text",
            "He go home.",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .remove_local_ai_env_overrides()
        .env("EASYDICT_LOCAL_AI_PROVIDER", "openvino")
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("No local AI provider supports grammar correction"),
        "stderr should report the native grammar preflight:\n{stderr}"
    );
    assert!(
        !stderr.contains("requires a Rust-native route"),
        "OpenVINO grammar should fail before generic retained-worker wording:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "OpenVINO grammar should not probe retained LocalAI workers:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET"),
        "OpenVINO grammar should not expose retained .NET worker details:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "OpenVINO grammar should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

#[test]
fn local_ai_cli_env_overrides_foundry_local_endpoint_before_worker_lookup() {
    let work_dir = unique_temp_dir("easydict-cli-foundry-env");
    let settings_dir = work_dir.join("settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"LocalAIProvider":"WindowsAI"}"#,
    )
    .expect("settings should be written");

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
        .remove_local_ai_env_overrides()
        .env("EASYDICT_LOCAL_AI_PROVIDER", "foundry-local")
        .env("EASYDICT_FOUNDRY_LOCAL_ENDPOINT", "foundry-local-invalid")
        .env("EASYDICT_FOUNDRY_LOCAL_MODEL", "cli-foundry-model")
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        !stderr.contains("requires a Rust-native route"),
        "Foundry env route should enter native LocalAI handling:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "Foundry env route should not probe retained LocalAI workers:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "Foundry env route should not describe a compat host:\n{stderr}"
    );

    let _ = fs::remove_dir_all(work_dir);
}

fn cli_with_missing_host(subcommand: &str) -> Command {
    let mut command = Command::new(env!("CARGO_BIN_EXE_easydict_cli"));
    command.arg(subcommand);
    command.arg("--host").arg("__missing_worker__.exe");
    command.remove_local_ai_env_overrides();
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

fn assert_local_ai_cli_does_not_probe_retained_worker(output: &Output, context: &str) {
    assert!(
        !output.status.success(),
        "{context} should fail locally\nstdout:\n{}\nstderr:\n{}",
        stdout(output),
        stderr(output)
    );
    let stderr = stderr(output);
    assert!(
        stderr.contains("requires a Rust-native route"),
        "{context} should require a Rust-native LocalAI route:\n{stderr}"
    );
    assert!(
        !stderr.contains(".NET Local AI workers"),
        "{context} should not expose retained .NET worker details:\n{stderr}"
    );
    assert!(
        !stderr.contains("Local AI worker executable not found"),
        "{context} should not probe retained LocalAI worker paths:\n{stderr}"
    );
    assert!(
        !stderr.to_ascii_lowercase().contains("compat host"),
        "{context} should not describe a compat host route:\n{stderr}"
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

trait LocalAiEnvCommandExt {
    fn remove_local_ai_env_overrides(&mut self) -> &mut Self;
}

impl LocalAiEnvCommandExt for Command {
    fn remove_local_ai_env_overrides(&mut self) -> &mut Self {
        for key in LOCAL_AI_ENVIRONMENT_OVERRIDE_KEYS {
            self.env_remove(key);
        }
        self
    }
}

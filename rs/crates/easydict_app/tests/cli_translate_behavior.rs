#![cfg(windows)]

use easydict_app::FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE;
use std::fs;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpListener;
use std::path::PathBuf;
use std::process::{Command, Output, Stdio};
use std::sync::mpsc;
use std::thread;
use std::time::{Duration, Instant};
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
fn translate_command_rejects_legacy_host_option_by_default() {
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

    assert_unknown_legacy_option(&output, "--host", "translate");
}

#[test]
fn stream_command_rejects_legacy_host_option_by_default() {
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

    assert_unknown_legacy_option(&output, "--host", "stream");
}

#[test]
fn grammar_command_rejects_legacy_host_option_by_default() {
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

    assert_unknown_legacy_option(&output, "--host", "grammar");
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
fn native_openai_cli_translate_succeeds_against_local_server_without_worker_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-openai-native-translate-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("OpenAI listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/v1/chat/completions",
        listener.local_addr().unwrap()
    );
    fs::write(
        settings_dir.join("settings.json"),
        format!(
            r#"{{
  "OpenAIApiKey": "sk-cli-translate",
  "OpenAIEndpoint": "{endpoint}",
  "OpenAIModel": "gpt-4o-mini",
  "OpenAIApiFormatOverride": "ChatCompletions"
}}"#
        ),
    )
    .expect("settings file should be created");

    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut headers = String::new();
        let mut content_length = 0usize;
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    content_length = value.trim().parse().unwrap();
                }
            }
            headers.push_str(&line);
        }

        let mut body = vec![0; content_length];
        reader.read_exact(&mut body).unwrap();
        let body = String::from_utf8(body).unwrap();
        request_tx.send((headers, body)).unwrap();

        stream
            .write_all(
                "HTTP/1.1 200 OK\r\n\
                 Content-Type: text/event-stream\r\n\
                 Connection: close\r\n\r\n\
                 data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n\
                 data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n\
                 data: [DONE]\n\n"
                    .as_bytes(),
            )
            .unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
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
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (_, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native OpenAI endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "OpenAI CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(request_body.contains("\"model\":\"gpt-4o-mini\""));
    assert!(request_body.contains("\"stream\":true"));
    assert!(request_body.contains("Hello"));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"你好\""));
    assert!(stdout.contains("\"serviceId\":\"openai\""));
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "native OpenAI CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_ollama_cli_translate_succeeds_against_local_endpoint_without_worker_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-ollama-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Ollama listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/v1/chat/completions",
        listener.local_addr().unwrap()
    );
    fs::write(
        settings_dir.join("settings.json"),
        format!(
            r#"{{
  "OllamaEndpoint": "{endpoint}",
  "OllamaModel": "llama-local-test"
}}"#
        ),
    )
    .expect("settings file should be created");

    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut headers = String::new();
        let mut content_length = 0usize;
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    content_length = value.trim().parse().unwrap();
                }
            }
            headers.push_str(&line);
        }

        let mut body = vec![0; content_length];
        reader.read_exact(&mut body).unwrap();
        let body = String::from_utf8(body).unwrap();
        request_tx.send((headers, body)).unwrap();

        stream
            .write_all(
                "HTTP/1.1 200 OK\r\n\
                 Content-Type: text/event-stream\r\n\
                 Connection: close\r\n\r\n\
                 data: {\"choices\":[{\"delta\":{\"content\":\"Hal\"}}]}\n\n\
                 data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n\
                 data: [DONE]\n\n"
                    .as_bytes(),
            )
            .unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "ollama",
            "--from",
            "en",
            "--to",
            "de",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request_headers, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Ollama endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Ollama CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(
        !request_headers
            .to_ascii_lowercase()
            .contains("authorization:"),
        "Ollama native route should not send an Authorization header:\n{request_headers}"
    );
    assert!(request_body.contains("\"model\":\"llama-local-test\""));
    assert!(request_body.contains("\"stream\":true"));
    assert!(request_body.contains("Hello"));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Hallo\""));
    assert!(stdout.contains("\"serviceId\":\"ollama\""));
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "native Ollama CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_custom_openai_cli_translate_succeeds_against_local_endpoint_without_worker_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-custom-openai-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Custom OpenAI listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/v1/chat/completions",
        listener.local_addr().unwrap()
    );
    fs::write(
        settings_dir.join("settings.json"),
        format!(
            r#"{{
  "CustomOpenAIApiKey": "custom-key",
  "CustomOpenAIEndpoint": "{endpoint}",
  "CustomOpenAIModel": "custom-local-model"
}}"#
        ),
    )
    .expect("settings file should be created");

    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut headers = String::new();
        let mut content_length = 0usize;
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    content_length = value.trim().parse().unwrap();
                }
            }
            headers.push_str(&line);
        }

        let mut body = vec![0; content_length];
        reader.read_exact(&mut body).unwrap();
        let body = String::from_utf8(body).unwrap();
        request_tx.send((headers, body)).unwrap();

        stream
            .write_all(
                "HTTP/1.1 200 OK\r\n\
                 Content-Type: text/event-stream\r\n\
                 Connection: close\r\n\r\n\
                 data: {\"choices\":[{\"delta\":{\"content\":\"Bon\"}}]}\n\n\
                 data: {\"choices\":[{\"delta\":{\"content\":\"jour\"}}]}\n\n\
                 data: [DONE]\n\n"
                    .as_bytes(),
            )
            .unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "custom-openai",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request_headers, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Custom OpenAI endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Custom OpenAI CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(
        request_headers
            .to_ascii_lowercase()
            .contains("authorization: bearer custom-key"),
        "Custom OpenAI native route should send the configured Authorization header:\n{request_headers}"
    );
    assert!(request_body.contains("\"model\":\"custom-local-model\""));
    assert!(request_body.contains("\"stream\":true"));
    assert!(request_body.contains("Hello"));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"custom-openai\""));
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "native Custom OpenAI CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_deepseek_cli_translate_succeeds_against_local_endpoint_without_worker_wording() {
    assert_native_fixed_openai_compatible_cli_translate_succeeds(FixedOpenAiCompatibleCliCase {
        service_id: "deepseek",
        settings_prefix: "easydict-cli-deepseek-native-settings",
        settings_json: r#"{
  "DeepSeekApiKey": "deepseek-key",
  "DeepSeekModel": "deepseek-test-model"
}"#,
        endpoint_env: "EASYDICT_TEST_OPENAI_COMPATIBLE_ENDPOINT_DEEPSEEK",
        api_key: "deepseek-key",
        model: "deepseek-test-model",
    });
}

#[test]
fn native_groq_cli_translate_succeeds_against_local_endpoint_without_worker_wording() {
    assert_native_fixed_openai_compatible_cli_translate_succeeds(FixedOpenAiCompatibleCliCase {
        service_id: "groq",
        settings_prefix: "easydict-cli-groq-native-settings",
        settings_json: r#"{
  "GroqApiKey": "groq-key",
  "GroqModel": "groq-test-model"
}"#,
        endpoint_env: "EASYDICT_TEST_OPENAI_COMPATIBLE_ENDPOINT_GROQ",
        api_key: "groq-key",
        model: "groq-test-model",
    });
}

#[test]
fn native_zhipu_cli_translate_succeeds_against_local_endpoint_without_worker_wording() {
    assert_native_fixed_openai_compatible_cli_translate_succeeds(FixedOpenAiCompatibleCliCase {
        service_id: "zhipu",
        settings_prefix: "easydict-cli-zhipu-native-settings",
        settings_json: r#"{
  "ZhipuApiKey": "zhipu-key",
  "ZhipuModel": "zhipu-test-model"
}"#,
        endpoint_env: "EASYDICT_TEST_OPENAI_COMPATIBLE_ENDPOINT_ZHIPU",
        api_key: "zhipu-key",
        model: "zhipu-test-model",
    });
}

#[test]
fn native_github_models_cli_translate_succeeds_against_local_endpoint_without_worker_wording() {
    assert_native_fixed_openai_compatible_cli_translate_succeeds(FixedOpenAiCompatibleCliCase {
        service_id: "github",
        settings_prefix: "easydict-cli-github-models-native-settings",
        settings_json: r#"{
  "GitHubModelsToken": "github-key",
  "GitHubModelsModel": "github-test-model"
}"#,
        endpoint_env: "EASYDICT_TEST_OPENAI_COMPATIBLE_ENDPOINT_GITHUB",
        api_key: "github-key",
        model: "github-test-model",
    });
}

struct FixedOpenAiCompatibleCliCase {
    service_id: &'static str,
    settings_prefix: &'static str,
    settings_json: &'static str,
    endpoint_env: &'static str,
    api_key: &'static str,
    model: &'static str,
}

fn assert_native_fixed_openai_compatible_cli_translate_succeeds(
    case: FixedOpenAiCompatibleCliCase,
) {
    let settings_dir = unique_temp_dir(case.settings_prefix);
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(settings_dir.join("settings.json"), case.settings_json)
        .expect("settings file should be created");

    let listener =
        TcpListener::bind(("127.0.0.1", 0)).expect("OpenAI-compatible listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/v1/chat/completions",
        listener.local_addr().unwrap()
    );

    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut request_headers = String::new();
        let mut content_length = 0usize;
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    content_length = value.trim().parse().unwrap();
                }
            }
            request_headers.push_str(&line);
        }

        let mut body = vec![0; content_length];
        reader.read_exact(&mut body).unwrap();
        let body = String::from_utf8(body).unwrap();
        request_tx.send((request_headers, body)).unwrap();

        stream
            .write_all(
                "HTTP/1.1 200 OK\r\n\
                 Content-Type: text/event-stream\r\n\
                 Connection: close\r\n\r\n\
                 data: {\"choices\":[{\"delta\":{\"content\":\"Bon\"}}]}\n\n\
                 data: {\"choices\":[{\"delta\":{\"content\":\"jour\"}}]}\n\n\
                 data: [DONE]\n\n"
                    .as_bytes(),
            )
            .unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            case.service_id,
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env(case.endpoint_env, &endpoint)
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request_headers, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native OpenAI-compatible endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "{} CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        case.service_id,
        stdout(&output),
        stderr(&output)
    );

    let request_headers_lower = request_headers.to_ascii_lowercase();
    assert!(
        request_headers.starts_with("POST /v1/chat/completions "),
        "{} native route should post to the debug chat-completions endpoint:\n{}",
        case.service_id,
        request_headers
    );
    assert!(
        request_headers_lower.contains(&format!("authorization: bearer {}", case.api_key)),
        "{} native route should send the configured Authorization header:\n{}",
        case.service_id,
        request_headers
    );
    assert!(request_body.contains(&format!("\"model\":\"{}\"", case.model)));
    assert!(request_body.contains("\"stream\":true"));
    assert!(request_body.contains("Hello"));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains(&format!("\"serviceId\":\"{}\"", case.service_id)));
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "native {} CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}",
            case.service_id
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_google_cli_succeeds_against_local_endpoint_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-google-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Google listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/translate_a/single",
        listener.local_addr().unwrap()
    );
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut request = String::new();
        let mut content_length = 0usize;
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    content_length = value.trim().parse().unwrap();
                }
            }
            request.push_str(&line);
        }

        let mut body = vec![0; content_length];
        reader.read_exact(&mut body).unwrap();
        request_tx
            .send((request, String::from_utf8(body).unwrap()))
            .unwrap();

        let response_body = r#"{"sentences":[{"trans":"Bonjour"}],"src":"en"}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            response_body.len(),
            response_body
        );
        stream.write_all(response.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "google",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_GOOGLE", &endpoint)
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Google endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Google CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(request.starts_with("GET /translate_a/single?"));
    assert!(request.contains("client=gtx"));
    assert!(request.contains("sl=en"));
    assert!(request.contains("tl=fr"));
    assert!(request.contains("q=Hello"));
    assert!(request_body.is_empty());

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"google\""));
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "native Google CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn default_translate_uses_native_google_without_retained_runtime_or_shell_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-default-google-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Google listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/translate_a/single",
        listener.local_addr().unwrap()
    );
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let (request, body) = read_http_request(&stream);
        request_tx.send((request, body)).unwrap();

        let response_body = r#"{"sentences":[{"trans":"Bonjour"}],"src":"en"}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            response_body.len(),
            response_body
        );
        stream.write_all(response.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args(["--from", "en", "--to", "fr", "--text", "Hello", "--json"])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_GOOGLE", &endpoint)
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("default CLI should call the native Google endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "default CLI translate should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(request.starts_with("GET /translate_a/single?"));
    assert!(request.contains("client=gtx"));
    assert!(request.contains("sl=en"));
    assert!(request.contains("tl=fr"));
    assert!(request.contains("q=Hello"));
    assert!(request_body.is_empty());

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"google\""));
    for forbidden in [
        "CompatHost",
        "Easydict.Workers",
        ".NET",
        "dotnet",
        "dotnet.exe",
        "PowerShell",
        "powershell",
        "pwsh",
        "worker executable",
        "worker-required",
        "retained runtime",
        "retained worker",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
        "hostfxr",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "default native Google CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_google_cli_rejects_target_auto_before_provider_http_or_worker_lookup() {
    let settings_dir = unique_temp_dir("easydict-cli-google-target-auto-no-http");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("unused endpoint should bind");
    let endpoint = format!(
        "http://{}/translate_a/single",
        listener.local_addr().unwrap()
    );
    drop(listener);

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "google",
            "--from",
            "en",
            "--to",
            "auto",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_GOOGLE", &endpoint)
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    assert!(
        !output.status.success(),
        "target Auto should fail locally\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(
        stdout.trim().is_empty(),
        "unsupported Google CLI should not emit JSON success output:\n{stdout}"
    );
    assert!(
        stderr.contains("Language pair not supported: English -> Auto"),
        "target Auto should fail during Rust language preflight, before HTTP:\n{stderr}"
    );
    assert_no_retained_worker_wording(&stdout, &stderr, "unsupported native Google CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_google_cli_batch_succeeds_against_local_endpoint_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-google-native-batch-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Google batch listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/translate_a/single",
        listener.local_addr().unwrap()
    );
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        for response_text in ["Bonjour", "Bonsoir"] {
            let (mut stream, _) = accept_with_timeout(listener.try_clone().unwrap());
            let (request, body) = read_http_request(&stream);
            request_tx.send((request, body)).unwrap();

            let response_body =
                format!(r#"{{"sentences":[{{"trans":"{response_text}"}}],"src":"en"}}"#);
            let response = format!(
                "HTTP/1.1 200 OK\r\n\
                 Content-Type: application/json\r\n\
                 Content-Length: {}\r\n\
                 Connection: close\r\n\r\n\
                 {}",
                response_body.len(),
                response_body
            );
            stream.write_all(response.as_bytes()).unwrap();
            stream.flush().unwrap();
        }
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("batch")
        .args([
            "--service",
            "google",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello\nGood evening",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_GOOGLE", &endpoint)
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let requests = (0..2)
        .map(|_| {
            request_rx
                .recv_timeout(Duration::from_secs(10))
                .expect("CLI should call the native Google endpoint for every batch line")
        })
        .collect::<Vec<_>>();
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Google batch CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(requests[0].0.starts_with("GET /translate_a/single?"));
    assert!(requests[0].0.contains("client=gtx"));
    assert!(requests[0].0.contains("sl=en"));
    assert!(requests[0].0.contains("tl=fr"));
    assert!(requests[0].0.contains("q=Hello"));
    assert!(requests[0].1.is_empty());
    assert!(requests[1].0.starts_with("GET /translate_a/single?"));
    assert!(requests[1].0.contains("client=gtx"));
    assert!(requests[1].0.contains("sl=en"));
    assert!(requests[1].0.contains("tl=fr"));
    assert!(
        requests[1].0.contains("q=Good+evening") || requests[1].0.contains("q=Good%20evening"),
        "second Google batch request should contain encoded source text:\n{}",
        requests[1].0
    );
    assert!(requests[1].1.is_empty());

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    let lines = stdout.lines().collect::<Vec<_>>();
    assert_eq!(
        lines.len(),
        2,
        "batch JSON mode should print one result event per input line:\n{stdout}"
    );
    assert!(lines[0].contains("\"event\":\"result\""));
    assert!(lines[0].contains("\"index\":1"));
    assert!(lines[0].contains("\"text\":\"Hello\""));
    assert!(lines[0].contains("\"translatedText\":\"Bonjour\""));
    assert!(lines[0].contains("\"serviceId\":\"google\""));
    assert!(lines[1].contains("\"event\":\"result\""));
    assert!(lines[1].contains("\"index\":2"));
    assert!(lines[1].contains("\"text\":\"Good evening\""));
    assert!(lines[1].contains("\"translatedText\":\"Bonsoir\""));
    assert!(lines[1].contains("\"serviceId\":\"google\""));
    assert_no_retained_worker_wording(&stdout, &stderr, "native Google batch CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_caiyun_cli_succeeds_against_local_api_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-caiyun-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"CaiyunApiKey":"caiyun-token"}"#,
    )
    .expect("settings file should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Caiyun listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!("http://{}/v1/translator", listener.local_addr().unwrap());
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut headers = String::new();
        let mut content_length = 0usize;
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    content_length = value.trim().parse().unwrap();
                }
            }
            headers.push_str(&line);
        }

        let mut body = vec![0; content_length];
        reader.read_exact(&mut body).unwrap();
        let body = String::from_utf8(body).unwrap();
        request_tx.send((headers, body)).unwrap();

        let response_body = r#"{"target":["你好"]}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            response_body.len(),
            response_body
        );
        stream.write_all(response.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "caiyun",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_CAIYUN", &endpoint)
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request_headers, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Caiyun endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Caiyun CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );

    let request_headers = request_headers.to_ascii_lowercase();
    assert!(request_headers.contains("x-authorization: token caiyun-token"));
    assert!(request_headers.contains("content-type: application/json"));
    assert!(request_body.contains("\"source\":[\"Hello\"]"));
    assert!(request_body.contains("\"trans_type\":\"en2zh\""));
    assert!(request_body.contains("\"media\":\"text\""));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"你好\""));
    assert!(stdout.contains("\"serviceId\":\"caiyun\""));
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "native Caiyun CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_deepl_cli_succeeds_against_local_api_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-deepl-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{
  "DeepLApiKey": "deepl-key",
  "DeepLUseFreeApi": false,
  "DeepLUseQualityOptimized": false
}"#,
    )
    .expect("settings file should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("DeepL listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!("http://{}/v2/translate", listener.local_addr().unwrap());
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut headers = String::new();
        let mut content_length = 0usize;
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    content_length = value.trim().parse().unwrap();
                }
            }
            headers.push_str(&line);
        }

        let mut body = vec![0; content_length];
        reader.read_exact(&mut body).unwrap();
        let body = String::from_utf8(body).unwrap();
        request_tx.send((headers, body)).unwrap();

        let response_body =
            r#"{"translations":[{"detected_source_language":"EN","text":"Bonjour"}]}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            response_body.len(),
            response_body
        );
        stream.write_all(response.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "deepl",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env(
            "EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_DEEPL_API",
            &endpoint,
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request_headers, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native DeepL endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "DeepL CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );

    let request_headers = request_headers.to_ascii_lowercase();
    assert!(request_headers.contains("authorization: deepl-auth-key deepl-key"));
    assert!(request_headers.contains("content-type: application/x-www-form-urlencoded"));
    assert!(request_body.contains("text=Hello"));
    assert!(request_body.contains("target_lang=FR"));
    assert!(request_body.contains("source_lang=EN"));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"deepl\""));
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "native DeepL CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_deepl_web_cli_uses_default_web_mode_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-deepl-web-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("DeepL web listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!("http://{}/jsonrpc", listener.local_addr().unwrap());
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let (request, body) = read_http_request(&stream);
        request_tx.send((request, body)).unwrap();

        let response_body = r#"{"jsonrpc":"2.0","id":100000000,"result":{"texts":[{"text":"Bonjour"}],"lang":"EN"}}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            response_body.len(),
            response_body
        );
        stream.write_all(response.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "deepl",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env(
            "EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_DEEPL_WEB",
            &endpoint,
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native DeepL web endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "DeepL web CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );

    assert!(request.starts_with("POST /jsonrpc "));
    let request_headers = request.to_ascii_lowercase();
    assert!(request_headers.contains("content-type: application/json"));
    assert!(request_headers.contains("origin: https://www.deepl.com"));
    assert!(request_headers.contains("referer: https://www.deepl.com/"));

    let payload: serde_json::Value =
        serde_json::from_str(&request_body).expect("DeepL web request body should be JSON");
    assert_eq!(payload["jsonrpc"], "2.0");
    assert_eq!(payload["method"], "LMT_handle_texts");
    assert_eq!(payload["params"]["texts"][0]["text"], "Hello");
    assert_eq!(payload["params"]["lang"]["source_lang_user_selected"], "EN");
    assert_eq!(payload["params"]["lang"]["target_lang"], "FR");

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"deepl\""));
    assert_no_retained_worker_wording(&stdout, &stderr, "native DeepL web CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_doubao_cli_translate_succeeds_against_local_sse_without_worker_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-doubao-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Doubao listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!("http://{}/api/v3/responses", listener.local_addr().unwrap());
    fs::write(
        settings_dir.join("settings.json"),
        format!(
            r#"{{
  "DoubaoApiKey": "doubao-key",
  "DoubaoEndpoint": "{endpoint}",
  "DoubaoModel": "doubao-test-model"
}}"#
        ),
    )
    .expect("settings file should be created");

    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut headers = String::new();
        let mut content_length = 0usize;
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    content_length = value.trim().parse().unwrap();
                }
            }
            headers.push_str(&line);
        }

        let mut body = vec![0; content_length];
        reader.read_exact(&mut body).unwrap();
        let body = String::from_utf8(body).unwrap();
        request_tx.send((headers, body)).unwrap();

        stream
            .write_all(
                b"HTTP/1.1 200 OK\r\n\
                  Content-Type: text/event-stream\r\n\
                  Connection: close\r\n\r\n\
                  event: response.output_text.delta\n\
                  data: {\"delta\":\"Bon\"}\n\n\
                  event: response.output_text.delta\n\
                  data: {\"delta\":\"jour\"}\n\n\
                  data: [DONE]\n\n",
            )
            .unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "doubao",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request_headers, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Doubao endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Doubao CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );

    let request_headers = request_headers.to_ascii_lowercase();
    assert!(request_headers.contains("authorization: bearer doubao-key"));
    assert!(request_body.contains("\"model\":\"doubao-test-model\""));
    assert!(request_body.contains("\"stream\":true"));
    assert!(request_body.contains("Hello"));
    assert!(request_body.contains("\"source_language\":\"en\""));
    assert!(request_body.contains("\"target_language\":\"fr\""));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"doubao\""));
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "native Doubao CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_gemini_cli_translate_succeeds_against_local_sse_without_worker_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-gemini-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Gemini listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let base_url = format!("http://{}", listener.local_addr().unwrap());
    fs::write(
        settings_dir.join("settings.json"),
        r#"{
  "GeminiApiKey": "gemini-key",
  "GeminiModel": "gemini-test-model"
}"#,
    )
    .expect("settings file should be created");

    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut request = String::new();
        let mut content_length = 0usize;
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    content_length = value.trim().parse().unwrap();
                }
            }
            request.push_str(&line);
        }

        let mut body = vec![0; content_length];
        reader.read_exact(&mut body).unwrap();
        let body = String::from_utf8(body).unwrap();
        request_tx.send((request, body)).unwrap();

        stream
            .write_all(
                b"HTTP/1.1 200 OK\r\n\
                  Content-Type: text/event-stream\r\n\
                  Connection: close\r\n\r\n\
                  data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Bon\"}]}}]}\n\n\
                  data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"jour\"}]}}]}\n\n\
                  data: [DONE]\n\n",
            )
            .unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "gemini",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env(
            "EASYDICT_TEST_CUSTOM_STREAMING_GEMINI_API_BASE_URL",
            &base_url,
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Gemini endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Gemini CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );

    assert!(request.starts_with("POST /models/gemini-test-model:streamGenerateContent?"));
    assert!(request.contains("alt=sse"));
    assert!(request.contains("key=gemini-key"));
    assert!(request_body.contains("Hello"));
    assert!(request_body.contains("Translate the following English text into French text"));
    assert!(request_body.contains("\"temperature\":"));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"gemini\""));
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "native Gemini CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_niutrans_cli_succeeds_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-niutrans-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{"NiuTransApiKey":"niu-key"}"#,
    )
    .expect("settings file should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("NiuTrans listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!("http://{}/translation", listener.local_addr().unwrap());
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut headers = String::new();
        let mut content_length = 0usize;
        loop {
            let mut line = String::new();
            let bytes = reader.read_line(&mut line).unwrap();
            if bytes == 0 || line == "\r\n" {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    content_length = value.trim().parse().unwrap();
                }
            }
            headers.push_str(&line);
        }

        let mut body = vec![0; content_length];
        reader.read_exact(&mut body).unwrap();
        let body = String::from_utf8(body).unwrap();
        request_tx.send((headers, body)).unwrap();

        stream
            .write_all(
                b"HTTP/1.1 200 OK\r\n\
                  Content-Type: application/json\r\n\
                  Content-Length: 22\r\n\
                  Connection: close\r\n\r\n\
                  {\"tgt_text\":\"Bonjour\"}",
            )
            .unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "niutrans",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env(
            "EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_NIUTRANS",
            &endpoint,
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (_, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native NiuTrans endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "NiuTrans CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(request_body.contains("\"apikey\":\"niu-key\""));
    assert!(request_body.contains("\"src_text\":\"Hello\""));
    assert!(request_body.contains("\"from\":\"en\""));
    assert!(request_body.contains("\"to\":\"fr\""));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"niutrans\""));
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "native NiuTrans CLI should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_linguee_cli_succeeds_against_local_api_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-linguee-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Linguee listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/api/v2/translations",
        listener.local_addr().unwrap()
    );
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let (request, body) = read_http_request(&stream);
        request_tx.send((request, body)).unwrap();

        let response_body =
            r#"[{"featured":true,"translations":[{"text":"Bonjour"},{"text":"Salut"}]}]"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            response_body.len(),
            response_body
        );
        stream.write_all(response.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "linguee",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_LINGUEE", &endpoint)
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Linguee endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Linguee CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(request.starts_with("GET /api/v2/translations?"));
    assert!(request.contains("query=Hello"));
    assert!(request.contains("src=en"));
    assert!(request.contains("dst=fr"));
    assert!(request_body.is_empty());

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"linguee\""));
    assert!(stdout.contains("\"serviceName\":\"Linguee Dictionary\""));
    assert!(stdout.contains("\"alternatives\":[\"Salut\"]"));
    assert_no_retained_worker_wording(&stdout, &stderr, "native Linguee CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_youdao_openapi_cli_succeeds_against_local_api_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-youdao-openapi-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{
  "YoudaoUseOfficialApi": true,
  "YoudaoAppKey": "youdao-key",
  "YoudaoAppSecret": "youdao-secret"
}"#,
    )
    .expect("settings file should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Youdao listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!("http://{}/api", listener.local_addr().unwrap());
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let (request, body) = read_http_request(&stream);
        request_tx.send((request, body)).unwrap();

        let response_body = r#"{"errorCode":"0","translation":["你好"],"l":"en2zh-CHS"}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            response_body.len(),
            response_body
        );
        stream.write_all(response.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "youdao",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env(
            "EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_YOUDAO_OPENAPI",
            &endpoint,
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Youdao OpenAPI endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Youdao OpenAPI CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(request.starts_with("POST /api "));
    assert!(request
        .to_ascii_lowercase()
        .contains("content-type: application/x-www-form-urlencoded"));
    assert!(request_body.contains("q=Hello"));
    assert!(request_body.contains("from=en"));
    assert!(request_body.contains("to=zh-CHS"));
    assert!(request_body.contains("appKey=youdao-key"));
    assert!(request_body.contains("signType=v3"));
    assert!(request_body.contains("salt="));
    assert!(request_body.contains("curtime="));
    assert!(request_body.contains("sign="));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"你好\""));
    assert!(stdout.contains("\"serviceId\":\"youdao\""));
    assert_no_retained_worker_wording(&stdout, &stderr, "native Youdao OpenAPI CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_youdao_web_dict_cli_succeeds_against_local_api_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-youdao-web-dict-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Youdao dict listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/jsonapi_s?doctype=json&jsonversion=4",
        listener.local_addr().unwrap()
    );
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let (request, body) = read_http_request(&stream);
        request_tx.send((request, body)).unwrap();

        let response_body = r#"{"ec":{"word":{"usphone":"h\u0259\u02c8lo\u028a","trs":[{"pos":"int.","tran":"\u5582\uff1b\u4f60\u597d"}]}}}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            response_body.len(),
            response_body
        );
        stream.write_all(response.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "youdao",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env(
            "EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_YOUDAO_WEB_DICT",
            &endpoint,
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Youdao web dictionary endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Youdao web dict CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(request.starts_with("POST /jsonapi_s?doctype=json&jsonversion=4 "));
    assert!(request
        .to_ascii_lowercase()
        .contains("content-type: application/x-www-form-urlencoded"));
    assert!(request_body.contains("q=hello"));
    assert!(request_body.contains("le=en"));
    assert!(request_body.contains("client=web"));
    assert!(request_body.contains("keyfrom=webdict"));
    assert!(request_body.contains("sign="));

    assert!(
        request_rx.try_recv().is_err(),
        "meaningful Youdao web dict result should not fall back to webtranslate"
    );

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"int. 喂；你好\""));
    assert!(stdout.contains("\"serviceId\":\"youdao\""));
    assert_no_retained_worker_wording(&stdout, &stderr, "native Youdao web dict CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_youdao_webtranslate_cli_runs_key_and_translate_requests_without_worker_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-youdao-webtranslate-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Youdao web listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let key_endpoint = format!("http://{}/webtranslate/key", listener.local_addr().unwrap());
    let translate_endpoint = format!("http://{}/webtranslate", listener.local_addr().unwrap());
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut key_stream, _) = accept_with_timeout(listener.try_clone().unwrap());
        let (key_request, key_body) = read_http_request(&key_stream);
        request_tx.send((key_request, key_body)).unwrap();
        let key_response_body = r#"{"code":0,"data":{"secretKey":"secret-key"}}"#;
        let key_response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            key_response_body.len(),
            key_response_body
        );
        key_stream.write_all(key_response.as_bytes()).unwrap();
        key_stream.flush().unwrap();

        let (mut translate_stream, _) = accept_with_timeout(listener);
        let (translate_request, translate_body) = read_http_request(&translate_stream);
        request_tx
            .send((translate_request, translate_body))
            .unwrap();
        let translate_response_body =
            r#"{"translateResult":[[{"tgt":"你好","src":"Hello world."}]],"code":0}"#;
        let translate_response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            translate_response_body.len(),
            translate_response_body
        );
        translate_stream
            .write_all(translate_response.as_bytes())
            .unwrap();
        translate_stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "youdao",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello world.",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env(
            "EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_YOUDAO_WEB_TRANSLATE_KEY",
            &key_endpoint,
        )
        .env(
            "EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_YOUDAO_WEB_TRANSLATE",
            &translate_endpoint,
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (key_request, key_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should request the native Youdao webtranslate key");
    let (translate_request, translate_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Youdao webtranslate endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Youdao webtranslate CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(key_request.starts_with("GET /webtranslate/key?"));
    assert!(key_request.contains("keyid=webfanyi-key-getter"));
    assert!(key_request.contains("mysticTime="));
    assert!(key_body.is_empty());
    assert!(translate_request.starts_with("POST /webtranslate "));
    assert!(translate_request
        .to_ascii_lowercase()
        .contains("content-type: application/x-www-form-urlencoded"));
    assert!(translate_body.contains("i=Hello+world."));
    assert!(translate_body.contains("from=en"));
    assert!(translate_body.contains("to=zh-CHS"));
    assert!(translate_body.contains("dictResult=true"));
    assert!(translate_body.contains("keyid=webfanyi"));
    assert!(translate_body.contains("sign="));
    assert!(translate_body.contains("mysticTime="));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"你好\""));
    assert!(stdout.contains("\"serviceId\":\"youdao\""));
    assert_no_retained_worker_wording(&stdout, &stderr, "native Youdao webtranslate CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_google_web_cli_succeeds_against_local_endpoint_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-google-web-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Google Web listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/translate_a/single",
        listener.local_addr().unwrap()
    );
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let (request, body) = read_http_request(&stream);
        request_tx.send((request, body)).unwrap();

        let response_body = r#"[ [["Bonjour","Hello",null,null,3]], null, "en" ]"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            response_body.len(),
            response_body
        );
        stream.write_all(response.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "google_web",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env(
            "EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_GOOGLE_WEB",
            &endpoint,
        )
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Google Web endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Google Web CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(request.starts_with("GET /translate_a/single?"));
    assert!(request.contains("client=gtx"));
    assert!(request.contains("sl=en"));
    assert!(request.contains("tl=fr"));
    assert!(request.contains("dt=bd"));
    assert!(request.contains("dt=t"));
    assert!(request.contains("q=Hello"));
    assert!(request_body.is_empty());

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"google_web\""));
    assert_no_retained_worker_wording(&stdout, &stderr, "native Google Web CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_volcano_cli_succeeds_against_local_api_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-volcano-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::write(
        settings_dir.join("settings.json"),
        r#"{
  "VolcanoAccessKeyId": "volcano-akid",
  "VolcanoSecretAccessKey": "volcano-secret"
}"#,
    )
    .expect("settings file should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Volcano listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!("http://{}/translate", listener.local_addr().unwrap());
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let (request, body) = read_http_request(&stream);
        request_tx.send((request, body)).unwrap();

        let response_body =
            r#"{"TranslationList":[{"Translation":"Bonjour","DetectedSourceLanguage":"en"}]}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            response_body.len(),
            response_body
        );
        stream.write_all(response.as_bytes()).unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "volcano",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env("EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_VOLCANO", &endpoint)
        .env("NO_PROXY", "127.0.0.1,localhost")
        .env("no_proxy", "127.0.0.1,localhost")
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (request, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Volcano endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Volcano CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(request.starts_with("POST /translate "));
    let request_lower = request.to_ascii_lowercase();
    assert!(request_lower.contains("content-type: application/json"));
    assert!(request_lower.contains("x-date:"));
    assert!(request_lower.contains("authorization: hmac-sha256 credential=volcano-akid/"));
    assert!(request_body.contains("\"TargetLanguage\":\"fr\""));
    assert!(request_body.contains("\"TextList\":[\"Hello\"]"));
    assert!(request_body.contains("\"SourceLanguage\":\"en\""));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"Bonjour\""));
    assert!(stdout.contains("\"serviceId\":\"volcano\""));
    assert_no_retained_worker_wording(&stdout, &stderr, "native Volcano CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn native_bing_cli_runs_two_phase_flow_without_worker_or_compat_host_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-bing-native-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");

    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("Bing listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let translator_endpoint = format!("http://{}/translator", listener.local_addr().unwrap());
    let translate_endpoint = format!("http://{}/ttranslatev3", listener.local_addr().unwrap());
    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut page_stream, _) = accept_with_timeout(listener.try_clone().unwrap());
        let (page_request, page_body) = read_http_request(&page_stream);
        request_tx.send((page_request, page_body)).unwrap();
        let page_body = r#"
<html>
<script>var _G = {IG:"A1B2C3D4E5F6",};</script>
<div class="rms_iml" data-iid="translator.5028.3"></div>
<script>var params_AbusePreventionHelper = [1700000000000,"abusetoken_XyZ",3600000];</script>
</html>
"#;
        let page_response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: text/html\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            page_body.len(),
            page_body
        );
        page_stream.write_all(page_response.as_bytes()).unwrap();
        page_stream.flush().unwrap();

        let (mut translate_stream, _) = accept_with_timeout(listener);
        let (translate_request, translate_body) = read_http_request(&translate_stream);
        request_tx
            .send((translate_request, translate_body))
            .unwrap();
        let translate_response_body = r#"[{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"你好","to":"zh-Hans"}]}]"#;
        let translate_response = format!(
            "HTTP/1.1 200 OK\r\n\
             Content-Type: application/json\r\n\
             Content-Length: {}\r\n\
             Connection: close\r\n\r\n\
             {}",
            translate_response_body.len(),
            translate_response_body
        );
        translate_stream
            .write_all(translate_response.as_bytes())
            .unwrap();
        translate_stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("translate")
        .args([
            "--service",
            "bing",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "Hello",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .env(
            "EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_BING_TRANSLATOR",
            &translator_endpoint,
        )
        .env(
            "EASYDICT_TEST_TRADITIONAL_HTTP_ENDPOINT_BING_TRANSLATE",
            &translate_endpoint,
        )
        .env("NO_PROXY", "127.0.0.1,localhost")
        .env("no_proxy", "127.0.0.1,localhost")
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (page_request, page_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should fetch the native Bing translator page");
    let (translate_request, translate_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native Bing translate endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "Bing CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(page_request.starts_with("GET /translator "));
    assert!(page_body.is_empty());
    assert!(translate_request.starts_with("POST /ttranslatev3?"));
    assert!(translate_request.contains("IG=A1B2C3D4E5F6"));
    assert!(translate_request.contains("IID=translator.5028.3"));
    assert!(translate_request.contains("SFX=1"));
    assert!(translate_request
        .to_ascii_lowercase()
        .contains("content-type: application/x-www-form-urlencoded"));
    assert!(translate_body.contains("fromLang=en"));
    assert!(translate_body.contains("to=zh-Hans"));
    assert!(translate_body.contains("text=Hello"));
    assert!(translate_body.contains("token=abusetoken_XyZ"));
    assert!(translate_body.contains("key=1700000000000"));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"translatedText\":\"你好\""));
    assert!(stdout.contains("\"serviceId\":\"bing\""));
    assert_no_retained_worker_wording(&stdout, &stderr, "native Bing CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn stream_command_writes_openai_chunks_before_sse_response_completes() {
    let settings_dir = unique_temp_dir("easydict-cli-openai-stream-live-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("SSE listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
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
        let (mut stream, _) = accept_with_timeout(listener);
        let (_request, _body) = read_http_request(&stream);

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
fn native_openai_cli_grammar_succeeds_against_local_server_without_worker_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-openai-native-grammar-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("OpenAI listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/v1/chat/completions",
        listener.local_addr().unwrap()
    );
    fs::write(
        settings_dir.join("settings.json"),
        format!(
            r#"{{
  "OpenAIApiKey": "sk-cli-grammar",
  "OpenAIEndpoint": "{endpoint}",
  "OpenAIModel": "gpt-4o-mini",
  "OpenAIApiFormatOverride": "ChatCompletions"
}}"#
        ),
    )
    .expect("settings file should be created");

    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut stream, _) = accept_with_timeout(listener);
        let (request, body) = read_http_request(&stream);
        request_tx.send((request, body)).unwrap();

        stream
            .write_all(
                "HTTP/1.1 200 OK\r\n\
                 Content-Type: text/event-stream\r\n\
                 Connection: close\r\n\r\n\
                 data: {\"choices\":[{\"delta\":{\"content\":\"[CORRECTED]I have an apple.[/CORRECTED]\\n[EXPLANATION]Use have with I and an before apple.[/EXPLANATION]\"}}]}\n\n\
                 data: [DONE]\n\n"
                    .as_bytes(),
            )
            .unwrap();
        stream.flush().unwrap();
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("grammar")
        .args([
            "--service",
            "openai",
            "--language",
            "en",
            "--text",
            "I has a apple.",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let (_, request_body) = request_rx
        .recv_timeout(Duration::from_secs(10))
        .expect("CLI should call the native OpenAI grammar endpoint");
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "OpenAI grammar CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(request_body.contains("\"model\":\"gpt-4o-mini\""));
    assert!(request_body.contains("\"stream\":true"));
    assert!(request_body.contains("I has a apple."));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    assert!(stdout.contains("\"originalText\":\"I has a apple.\""));
    assert!(stdout.contains("\"correctedText\":\"I have an apple.\""));
    assert!(stdout.contains("\"hasCorrections\":true"));
    assert!(stdout.contains("\"serviceId\":\"openai\""));
    assert_no_retained_worker_wording(&stdout, &stderr, "native OpenAI grammar CLI");

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
fn native_openai_cli_batch_succeeds_against_local_server_without_worker_wording() {
    let settings_dir = unique_temp_dir("easydict-cli-openai-native-batch-settings");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("OpenAI listener should bind");
    listener
        .set_nonblocking(true)
        .expect("listener should become nonblocking");
    let endpoint = format!(
        "http://{}/v1/chat/completions",
        listener.local_addr().unwrap()
    );
    fs::write(
        settings_dir.join("settings.json"),
        format!(
            r#"{{
  "OpenAIApiKey": "sk-cli-batch",
  "OpenAIEndpoint": "{endpoint}",
  "OpenAIModel": "gpt-4o-mini",
  "OpenAIApiFormatOverride": "ChatCompletions"
}}"#
        ),
    )
    .expect("settings file should be created");

    let (request_tx, request_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        for response_text in ["Bonjour", "Bonsoir"] {
            let (mut stream, _) = accept_with_timeout(listener.try_clone().unwrap());
            let (request, body) = read_http_request(&stream);
            request_tx.send((request, body)).unwrap();

            stream
                .write_all(
                    format!(
                        "HTTP/1.1 200 OK\r\n\
                         Content-Type: text/event-stream\r\n\
                         Connection: close\r\n\r\n\
                         data: {{\"choices\":[{{\"delta\":{{\"content\":\"{response_text}\"}}}}]}}\n\n\
                         data: [DONE]\n\n"
                    )
                    .as_bytes(),
                )
                .unwrap();
            stream.flush().unwrap();
        }
    });

    let output = Command::new(env!("CARGO_BIN_EXE_easydict_cli"))
        .arg("batch")
        .args([
            "--service",
            "openai",
            "--from",
            "en",
            "--to",
            "fr",
            "--text",
            "Hello\nGood evening",
            "--json",
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only")
        .remove_local_ai_env_overrides()
        .output()
        .expect("CLI should run");

    let requests = (0..2)
        .map(|_| {
            request_rx
                .recv_timeout(Duration::from_secs(10))
                .expect("CLI should call the native OpenAI endpoint for every batch line")
        })
        .collect::<Vec<_>>();
    server.join().expect("server should finish");

    assert!(
        output.status.success(),
        "OpenAI batch CLI should succeed\nstdout:\n{}\nstderr:\n{}",
        stdout(&output),
        stderr(&output)
    );
    assert!(requests[0].1.contains("\"model\":\"gpt-4o-mini\""));
    assert!(requests[0].1.contains("\"stream\":true"));
    assert!(requests[0].1.contains("Hello"));
    assert!(requests[1].1.contains("Good evening"));

    let stdout = stdout(&output);
    let stderr = stderr(&output);
    let lines = stdout.lines().collect::<Vec<_>>();
    assert_eq!(
        lines.len(),
        2,
        "batch JSON mode should print one result event per input line:\n{stdout}"
    );
    assert!(lines[0].contains("\"event\":\"result\""));
    assert!(lines[0].contains("\"index\":1"));
    assert!(lines[0].contains("\"text\":\"Hello\""));
    assert!(lines[0].contains("\"translatedText\":\"Bonjour\""));
    assert!(lines[0].contains("\"serviceId\":\"openai\""));
    assert!(lines[1].contains("\"event\":\"result\""));
    assert!(lines[1].contains("\"index\":2"));
    assert!(lines[1].contains("\"text\":\"Good evening\""));
    assert!(lines[1].contains("\"translatedText\":\"Bonsoir\""));
    assert!(lines[1].contains("\"serviceId\":\"openai\""));
    assert_no_retained_worker_wording(&stdout, &stderr, "native OpenAI batch CLI");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn default_cli_rejects_legacy_retained_worker_options() {
    for (mode, option, value) in [
        ("translate", "--app-dir", "C:/Tools/Easydict"),
        (
            "stream",
            "--host",
            "C:/Tools/workers/localai/Easydict.Workers.LocalAi.exe",
        ),
        ("grammar", "--host-arg", "--trace"),
        ("batch", "--app-dir", "C:/Tools/Easydict"),
    ] {
        let mut command = Command::new(env!("CARGO_BIN_EXE_easydict_cli"));
        command.arg(mode);
        match mode {
            "grammar" => {
                command.args([
                    "--service",
                    "windows-local-ai",
                    "--language",
                    "en",
                    "--text",
                ]);
                command.arg("I has a apple.");
            }
            "batch" => {
                command.args([
                    "--service",
                    "windows-local-ai",
                    "--from",
                    "en",
                    "--to",
                    "zh-Hans",
                    "--text",
                ]);
                command.arg("Hello\nGood morning");
            }
            _ => {
                command.args([
                    "--service",
                    "windows-local-ai",
                    "--from",
                    "en",
                    "--to",
                    "zh-Hans",
                    "--text",
                    "Hello",
                ]);
            }
        }
        command
            .arg(option)
            .arg(value)
            .env(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid")
            .remove_local_ai_env_overrides();

        let output = command.output().expect("CLI should run");
        assert_unknown_legacy_option(&output, option, mode);
    }
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
fn local_ai_cli_host_hint_is_rejected_by_default() {
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

    assert_unknown_legacy_option(&output, "--host", "translate --host");

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn local_ai_cli_fallback_honors_disabled_retained_worker_policy() {
    let settings_dir = unique_temp_dir("easydict-cli-local-ai-disabled-worker-settings");
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

    let _ = fs::remove_dir_all(settings_dir);
}

#[test]
fn auto_local_ai_cli_probes_foundry_before_native_only_failure() {
    let settings_dir = unique_temp_dir("easydict-cli-auto-foundry-settings");
    let fake_foundry_dir = unique_temp_dir("easydict-cli-fake-foundry");
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
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .remove_local_ai_env_overrides()
        .env("EASYDICT_LOCAL_AI_PROVIDER", "foundry-local")
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

    let _ = fs::remove_dir_all(settings_dir);
    let _ = fs::remove_dir_all(fake_foundry_dir);
}

#[test]
fn auto_local_ai_cli_rejects_foundry_cli_override_targeting_retained_worker_before_spawn() {
    let settings_dir = unique_temp_dir("easydict-cli-auto-foundry-bad-override-settings");
    let fake_foundry_dir = unique_temp_dir("easydict-cli-bad-foundry-override");
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
        ])
        .env("EASYDICT_SETTINGS_DIR", &settings_dir)
        .remove_local_ai_env_overrides()
        .env("EASYDICT_LOCAL_AI_PROVIDER", "foundry-local")
        .env(FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE, &fake_foundry_path)
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

    let _ = fs::remove_dir_all(settings_dir);
    let _ = fs::remove_dir_all(fake_foundry_dir);
}

#[test]
fn auto_local_ai_cli_rejects_foundry_cli_override_targeting_dotnet_cmd_before_spawn() {
    let settings_dir = unique_temp_dir("easydict-cli-auto-foundry-dotnet-cmd-settings");
    let fake_foundry_dir = unique_temp_dir("easydict-cli-dotnet-cmd-override");
    fs::create_dir_all(&settings_dir).expect("settings directory should be created");
    fs::create_dir_all(&fake_foundry_dir).expect("fake Foundry directory should be created");
    let marker_path = fake_foundry_dir.join("dotnet-cmd-was-spawned.txt");
    let fake_foundry_path = fake_foundry_dir.join("dotnet.cmd");
    fs::write(
        &fake_foundry_path,
        format!(
            "@echo off\r\necho spawned >\"{}\"\r\necho Foundry Local endpoint: http://127.0.0.1:1/v1/chat/completions\r\n",
            marker_path.display()
        ),
    )
    .expect("fake dotnet.cmd should be written");

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
        .env(FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE, &fake_foundry_path)
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        !marker_path.exists(),
        "Foundry CLI override that points at dotnet.cmd must not be spawned:\n{stderr}"
    );
    assert!(stderr.contains("retained runtime/worker"));
    assert!(stderr.contains("dotnet.cmd"));
    assert!(!stderr.contains("Local AI worker executable not found"));
    assert!(!stderr.to_ascii_lowercase().contains("compat host"));

    let _ = fs::remove_dir_all(settings_dir);
    let _ = fs::remove_dir_all(fake_foundry_dir);
}

#[test]
fn auto_local_ai_cli_rejects_foundry_cli_override_targeting_cmd_trampoline_before_spawn() {
    let settings_dir = unique_temp_dir("easydict-cli-auto-foundry-cmd-trampoline-settings");
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
        .remove_local_ai_env_overrides()
        .env("EASYDICT_LOCAL_AI_PROVIDER", "foundry-local")
        .env(FOUNDRY_LOCAL_CLI_ENVIRONMENT_VARIABLE, "cmd /c dotnet.exe")
        .output()
        .expect("CLI should run");

    assert!(!output.status.success());
    let stderr = stderr(&output);
    assert!(
        stderr.contains("retained runtime/worker"),
        "cmd trampoline should be rejected by the retained runtime guard:\n{stderr}"
    );
    assert!(stderr.contains("cmd /c dotnet.exe"));
    assert!(!stderr.contains("Local AI worker executable not found"));
    assert!(!stderr.to_ascii_lowercase().contains("compat host"));

    let _ = fs::remove_dir_all(settings_dir);
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
fn local_ai_cli_plain_env_aliases_route_to_native_preflight_without_worker_lookup() {
    enum ExpectedRoute {
        FoundryLocal,
        OpenVino,
    }

    let cases = [
        ("foundry_local", ExpectedRoute::FoundryLocal),
        ("open-vino", ExpectedRoute::OpenVino),
    ];

    for (provider_alias, expected_route) in cases {
        let work_dir = unique_temp_dir(&format!(
            "easydict-cli-plain-local-ai-env-{}",
            provider_alias.replace(['-', '_'], "-")
        ));
        let settings_dir = work_dir.join("settings");
        let cache_dir = work_dir.join("cache");
        fs::create_dir_all(&settings_dir).expect("settings directory should be created");
        fs::create_dir_all(&cache_dir).expect("cache directory should be created");
        fs::write(
            settings_dir.join("settings.json"),
            r#"{"LocalAIProvider":"WindowsAI"}"#,
        )
        .expect("settings should be written");

        let mut command = Command::new(env!("CARGO_BIN_EXE_easydict_cli"));
        command
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
            .env("LOCAL_AI_PROVIDER", provider_alias)
            .env("FOUNDRY_LOCAL_ENDPOINT", "foundry-local-invalid")
            .env("FOUNDRY_LOCAL_MODEL", "cli-foundry-model")
            .env("EASYDICT_CACHE_DIR", &cache_dir)
            .env("OPENVINO_DEVICE", "GPU");
        let output = command.output().expect("CLI should run");

        assert!(!output.status.success());
        let stderr = stderr(&output);
        match expected_route {
            ExpectedRoute::FoundryLocal => {
                assert!(
                    stderr.contains("OpenAI HTTP request failed"),
                    "plain Foundry aliases should route to native Foundry/OpenAI-compatible handling:\n{stderr}"
                );
            }
            ExpectedRoute::OpenVino => {
                assert!(
                    stderr.contains("OpenVINO runtime or NLLB-200 model is not downloaded"),
                    "plain OpenVINO aliases should route to native OpenVINO preflight:\n{stderr}"
                );
            }
        }
        assert!(
            !stderr.contains("requires a Rust-native route"),
            "plain LocalAI aliases should enter native provider handling:\n{stderr}"
        );
        assert!(
            !stderr.contains("Local AI worker executable not found"),
            "plain LocalAI aliases should not probe retained LocalAI workers:\n{stderr}"
        );
        assert!(
            !stderr.contains(".NET") && !stderr.to_ascii_lowercase().contains("compat host"),
            "plain LocalAI aliases should not expose retained worker details:\n{stderr}"
        );

        let _ = fs::remove_dir_all(work_dir);
    }
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

fn accept_with_timeout(listener: TcpListener) -> (std::net::TcpStream, std::net::SocketAddr) {
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        match listener.accept() {
            Ok((stream, address)) => {
                stream
                    .set_nonblocking(false)
                    .expect("accepted CLI HTTP stream should become blocking");
                return (stream, address);
            }
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                assert!(
                    Instant::now() < deadline,
                    "timed out waiting for CLI HTTP request"
                );
                thread::sleep(Duration::from_millis(20));
            }
            Err(error) => panic!("failed to accept CLI HTTP request: {error}"),
        }
    }
}

fn read_http_request(stream: &std::net::TcpStream) -> (String, String) {
    let mut reader = BufReader::new(stream.try_clone().unwrap());
    let mut headers = String::new();
    let mut content_length = 0usize;
    loop {
        let mut line = String::new();
        let bytes = reader.read_line(&mut line).unwrap();
        if bytes == 0 || line == "\r\n" {
            break;
        }
        if let Some((name, value)) = line.split_once(':') {
            if name.eq_ignore_ascii_case("content-length") {
                content_length = value.trim().parse().unwrap();
            }
        }
        headers.push_str(&line);
    }

    let mut body = vec![0; content_length];
    reader.read_exact(&mut body).unwrap();
    (headers, String::from_utf8(body).unwrap())
}

fn assert_no_retained_worker_wording(stdout: &str, stderr: &str, context: &str) {
    for forbidden in [
        "CompatHost",
        ".NET",
        "worker executable",
        "worker-required",
        "No Rust-native quick translate route",
        "requires a Rust-native route",
    ] {
        assert!(
            !stdout.contains(forbidden) && !stderr.contains(forbidden),
            "{context} should not mention {forbidden}\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
    }
}

fn assert_unknown_legacy_option(output: &Output, option: &str, context: &str) {
    assert!(
        !output.status.success(),
        "{context} should reject legacy retained-worker option {option}\nstdout:\n{}\nstderr:\n{}",
        stdout(output),
        stderr(output)
    );
    let stderr = stderr(output);
    assert!(
        stderr.contains(&format!("unknown option: {option}")),
        "{context} should fail in the CLI parser before route selection:\n{stderr}"
    );
    for forbidden in [
        "requires a Rust-native route",
        "Local AI worker executable not found",
        "CompatHost",
        "Easydict.Workers",
        "hostfxr",
    ] {
        assert!(
            !stderr.contains(forbidden),
            "{context} should reject {option} before retained-worker/runtime wording appears: {forbidden}\n{stderr}"
        );
    }
    assert!(
        stdout(output).trim().is_empty(),
        "{context} should not emit stdout when rejecting {option}"
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

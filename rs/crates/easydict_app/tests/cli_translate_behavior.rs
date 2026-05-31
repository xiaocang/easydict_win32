#![cfg(windows)]

use serde_json::Value;
use std::io::Write;
use std::path::PathBuf;
use std::process::{Command, Output, Stdio};

const MOCK_HOST_SCRIPT: &str = r#"
function Write-JsonLine($value) {
    $json = $value | ConvertTo-Json -Compress -Depth 16
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

while (($line = [Console]::In.ReadLine()) -ne $null) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    $request = $line | ConvertFrom-Json

    switch ($request.method) {
        'translate' {
            $text = [string]$request.params.text
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    translatedText = "mock:$text"
                    serviceId = 'mock'
                    serviceName = 'Mock CLI Host'
                    detectedLanguage = 'en'
                    resultKind = 'Success'
                    timingMs = 11
                }
            })
        }
        'translate_stream' {
            $text = [string]$request.params.text
            Write-JsonLine ([ordered]@{
                event = 'translate_chunk'
                id = $request.id
                data = [ordered]@{ text = 'mock:' }
            })
            Write-JsonLine ([ordered]@{
                event = 'translate_chunk'
                id = $request.id
                data = [ordered]@{ text = $text }
            })
            Write-JsonLine ([ordered]@{
                event = 'translate_done'
                id = $request.id
                data = [ordered]@{
                    translatedText = "mock:$text"
                    serviceId = 'mock'
                    serviceName = 'Mock CLI Host'
                    detectedLanguage = 'en'
                    resultKind = 'Success'
                    timingMs = 12
                }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    translatedText = "mock:$text"
                    serviceId = 'mock'
                    serviceName = 'Mock CLI Host'
                    detectedLanguage = 'en'
                    resultKind = 'Success'
                    timingMs = 12
                }
            })
        }
        'grammar_correct' {
            $text = [string]$request.params.text
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    originalText = $text
                    correctedText = 'I have an apple.'
                    explanation = 'Use have with I and an before apple.'
                    rawText = '[CORRECTED]I have an apple.[/CORRECTED]'
                    serviceId = 'mock'
                    serviceName = 'Mock CLI Host'
                    language = [string]$request.params.language
                    timingMs = 13
                    hasCorrections = $true
                }
            })
        }
        default {
            Write-JsonLine ([ordered]@{
                id = $request.id
                error = [ordered]@{
                    code = 'method_not_found'
                    message = "unknown method $($request.method)"
                }
            })
        }
    }
}
"#;

#[test]
fn translate_command_prints_plain_text_from_compat_host() {
    let output = cli_with_mock_host("translate")
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

    assert_success(&output);
    assert_eq!(stdout(&output).trim(), "mock:Hello");
}

#[test]
fn translate_command_reads_stdin_for_scripted_regression() {
    let mut child = cli_with_mock_host("translate")
        .args([
            "--service",
            "mock",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "-",
        ])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("CLI should spawn");

    child
        .stdin
        .as_mut()
        .expect("stdin should be piped")
        .write_all(b"From stdin\r\n")
        .expect("stdin write should succeed");

    let output = child.wait_with_output().expect("CLI should finish");

    assert_success(&output);
    assert_eq!(stdout(&output).trim(), "mock:From stdin");
}

#[test]
fn stream_command_emits_json_lines_for_chunks_and_done() {
    let output = cli_with_mock_host("stream")
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

    assert_success(&output);

    let lines = json_lines(&stdout(&output));
    assert_eq!(lines.len(), 3);
    assert_eq!(lines[0]["event"], "chunk");
    assert_eq!(lines[0]["text"], "mock:");
    assert_eq!(lines[1]["event"], "chunk");
    assert_eq!(lines[1]["text"], "Good morning");
    assert_eq!(lines[2]["event"], "done");
    assert_eq!(lines[2]["result"]["translatedText"], "mock:Good morning");
}

#[test]
fn grammar_command_prints_corrected_text_and_explanation() {
    let output = cli_with_mock_host("grammar")
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

    assert_success(&output);

    let stdout = stdout(&output);
    assert!(stdout.contains("I have an apple."));
    assert!(stdout.contains("Use have with I and an before apple."));
}

#[test]
fn batch_command_translates_each_non_blank_line_as_json_lines() {
    let mut child = cli_with_mock_host("batch")
        .args([
            "--service",
            "mock",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--text",
            "-",
            "--json",
        ])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("CLI should spawn");

    child
        .stdin
        .as_mut()
        .expect("stdin should be piped")
        .write_all(b"Hello\r\n\r\nGood morning\r\n")
        .expect("stdin write should succeed");

    let output = child.wait_with_output().expect("CLI should finish");

    assert_success(&output);

    let lines = json_lines(&stdout(&output));
    assert_eq!(lines.len(), 2);
    assert_eq!(lines[0]["event"], "result");
    assert_eq!(lines[0]["index"], 1);
    assert_eq!(lines[0]["text"], "Hello");
    assert_eq!(lines[0]["result"]["translatedText"], "mock:Hello");
    assert_eq!(lines[1]["index"], 2);
    assert_eq!(lines[1]["text"], "Good morning");
    assert_eq!(lines[1]["result"]["translatedText"], "mock:Good morning");
}

fn cli_with_mock_host(subcommand: &str) -> Command {
    let mut command = Command::new(env!("CARGO_BIN_EXE_easydict_cli"));
    command.arg(subcommand);
    command.arg("--host").arg(powershell_path());
    for arg in [
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-Command",
        MOCK_HOST_SCRIPT,
    ] {
        command.arg("--host-arg").arg(arg);
    }
    command
}

fn powershell_path() -> PathBuf {
    let windows_dir = std::env::var_os("WINDIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(r"C:\Windows"));
    windows_dir
        .join("System32")
        .join("WindowsPowerShell")
        .join("v1.0")
        .join("powershell.exe")
}

fn assert_success(output: &Output) {
    assert!(
        output.status.success(),
        "CLI failed with status {:?}\nstdout:\n{}\nstderr:\n{}",
        output.status.code(),
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

fn json_lines(stdout: &str) -> Vec<Value> {
    stdout
        .lines()
        .map(|line| serde_json::from_str(line).expect("line should be valid JSON"))
        .collect()
}

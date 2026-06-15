#![cfg(feature = "retained-dotnet-workers")]

use easydict_app::compat_protocol::{
    ipc_error_codes, worker_events, worker_kinds, worker_methods, ChunkEventData, ConfigureResult,
    IpcError, IpcEvent, IpcResponse, ReadyEventData, ShutdownResult, StatusEventData,
    TranslateDocumentParams, TranslateDocumentResult, TranslateParams, TranslateStreamResult,
    WORKER_PROTOCOL_VERSION_CURRENT,
};
use serde::Deserialize;
use serde_json::{json, Value};
use std::io::{self, BufRead, Write};

fn main() {
    let mode = std::env::args()
        .skip_while(|arg| arg != "--mode")
        .nth(1)
        .unwrap_or_else(|| "host".to_string());

    let result = match mode.as_str() {
        "host" => run_host(),
        "worker" => run_worker(),
        other => Err(format!("unknown mock IPC mode {other:?}")),
    };

    if let Err(error) = result {
        let _ = writeln!(io::stderr(), "easydict-ipc-mock: {error}");
        std::process::exit(1);
    }
}

fn run_host() -> Result<(), String> {
    for line in io::stdin().lock().lines() {
        let line = line.map_err(|error| error.to_string())?;
        if line.trim().is_empty() {
            continue;
        }

        let request = match serde_json::from_str::<MockRequest>(&line) {
            Ok(request) => request,
            Err(error) => {
                write_response(IpcResponse::<Value>::error(
                    "malformed",
                    IpcError::new(ipc_error_codes::INVALID_JSON, error.to_string()),
                ))?;
                continue;
            }
        };

        let id = request.id.as_str();
        match request.method.as_str() {
            worker_methods::CONFIGURE => {
                write_response(IpcResponse::ok(id, ConfigureResult { ok: true }))?;
            }
            "translate" => {
                let text = request
                    .params::<TranslateParams>()
                    .map(|params| params.text)
                    .unwrap_or_default();
                write_response(IpcResponse::ok(id, mock_translation(&text, 7)))?;
            }
            worker_methods::LOCAL_AI_TRANSLATE_STREAM => {
                let text = request
                    .params::<TranslateParams>()
                    .map(|params| params.text)
                    .unwrap_or_default();
                write_event(IpcEvent::for_request(
                    id,
                    "translate_chunk",
                    ChunkEventData {
                        text: "mock:".to_string(),
                    },
                ))?;
                write_event(IpcEvent::for_request(
                    id,
                    "translate_chunk",
                    ChunkEventData { text: text.clone() },
                ))?;
                write_event(IpcEvent::for_request(
                    id,
                    "translate_done",
                    mock_translation(&text, 8),
                ))?;
                write_response(IpcResponse::ok(id, mock_translation(&text, 8)))?;
            }
            "grammar_correct" => {
                let params = request.params::<Value>().unwrap_or_default();
                let text = params
                    .get("text")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_string();
                let language = params
                    .get("language")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_string();
                let result = mock_grammar_result(&text, &language);
                write_event(IpcEvent::for_request(
                    id,
                    "grammar_chunk",
                    json!({ "text": "[CORRECTED]" }),
                ))?;
                write_event(IpcEvent::for_request(
                    id,
                    "grammar_chunk",
                    json!({ "text": "I have an apple." }),
                ))?;
                write_event(IpcEvent::for_request(id, "grammar_done", result.clone()))?;
                write_response(IpcResponse::ok(id, result))?;
            }
            "emit_event_then_translate" => {
                let text = request
                    .params::<TranslateParams>()
                    .map(|params| params.text)
                    .unwrap_or_default();
                write_event(IpcEvent::for_request(
                    id,
                    "chunk",
                    ChunkEventData {
                        text: "mock:".to_string(),
                    },
                ))?;
                write_response(IpcResponse::ok(id, mock_translation(&text, 7)))?;
            }
            "fail_remote" => {
                write_response(IpcResponse::<Value>::error(
                    id,
                    IpcError::new(ipc_error_codes::SERVICE_ERROR, "mock service failed"),
                ))?;
            }
            "exit_now" => return Ok(()),
            _ => {
                write_response(IpcResponse::<Value>::error(
                    id,
                    IpcError::new(ipc_error_codes::METHOD_NOT_FOUND, "unknown method"),
                ))?;
            }
        }
    }
    Ok(())
}

fn run_worker() -> Result<(), String> {
    let worker_kind =
        std::env::var("MOCK_WORKER_KIND").unwrap_or_else(|_| worker_kinds::LONGDOC.to_string());
    let protocol_version = std::env::var("MOCK_WORKER_PROTOCOL_VERSION")
        .ok()
        .and_then(|value| value.parse::<u32>().ok())
        .unwrap_or(WORKER_PROTOCOL_VERSION_CURRENT);
    let capabilities = std::env::var("MOCK_WORKER_CAPABILITIES")
        .ok()
        .map(|value| {
            value
                .split(',')
                .map(str::trim)
                .filter(|value| !value.is_empty())
                .map(str::to_string)
                .collect::<Vec<_>>()
        })
        .unwrap_or_else(|| default_capabilities(&worker_kind));

    write_event(IpcEvent::new(
        worker_events::READY,
        ReadyEventData {
            worker_kind,
            worker_version: "9.9.9".to_string(),
            protocol_version,
            capabilities,
        },
    ))?;

    for line in io::stdin().lock().lines() {
        let line = line.map_err(|error| error.to_string())?;
        if line.trim().is_empty() {
            continue;
        }
        let request =
            serde_json::from_str::<MockRequest>(&line).map_err(|error| error.to_string())?;
        let id = request.id.as_str();
        match request.method.as_str() {
            worker_methods::CONFIGURE => {
                write_response(IpcResponse::ok(id, ConfigureResult { ok: true }))?;
            }
            worker_methods::LONGDOC_TRANSLATE_DOCUMENT => {
                let output_path = request
                    .params::<TranslateDocumentParams>()
                    .ok()
                    .and_then(|params| params.output_path);
                write_event(IpcEvent::for_request(
                    id,
                    worker_events::LONGDOC_STATUS,
                    StatusEventData {
                        message: "direct worker longdoc started".to_string(),
                    },
                ))?;
                write_response(IpcResponse::ok(
                    id,
                    TranslateDocumentResult {
                        state: "Completed".to_string(),
                        output_path,
                        bilingual_output_path: None,
                        total_chunks: 1,
                        succeeded_chunks: 1,
                        failed_chunk_indexes: Some(Vec::new()),
                        quality_report: None,
                        result_json_path: None,
                    },
                ))?;
            }
            worker_methods::LOCAL_AI_TRANSLATE_STREAM => {
                let text = request
                    .params::<Value>()
                    .ok()
                    .and_then(|params| {
                        params
                            .get("text")
                            .and_then(Value::as_str)
                            .map(str::to_string)
                    })
                    .unwrap_or_default();
                write_event(IpcEvent::for_request(
                    id,
                    worker_events::LOCAL_AI_CHUNK,
                    ChunkEventData {
                        text: "direct ".to_string(),
                    },
                ))?;
                write_event(IpcEvent::for_request(
                    id,
                    worker_events::LOCAL_AI_CHUNK,
                    ChunkEventData {
                        text: format!("worker {text}"),
                    },
                ))?;
                write_response(IpcResponse::ok(
                    id,
                    TranslateStreamResult {
                        done: true,
                        full_text: Some(format!("direct worker {text}")),
                    },
                ))?;
            }
            worker_methods::LOCAL_AI_GRAMMAR_STREAM => {
                write_event(IpcEvent::for_request(
                    id,
                    worker_events::LOCAL_AI_CHUNK,
                    ChunkEventData {
                        text: "[CORRECTED]Direct worker.[/CORRECTED]".to_string(),
                    },
                ))?;
                write_response(IpcResponse::ok(
                    id,
                    TranslateStreamResult {
                        done: true,
                        full_text: Some("[CORRECTED]Direct worker.[/CORRECTED]".to_string()),
                    },
                ))?;
            }
            worker_methods::CANCEL => {
                write_response(IpcResponse::ok(id, json!({ "cancelled": true })))?;
            }
            worker_methods::SHUTDOWN => {
                write_response(IpcResponse::ok(id, ShutdownResult { ok: true }))?;
                break;
            }
            method => {
                write_response(IpcResponse::<Value>::error(
                    id,
                    IpcError::new(
                        ipc_error_codes::METHOD_NOT_FOUND,
                        format!("unknown direct worker method: {method}"),
                    ),
                ))?;
            }
        }
    }
    Ok(())
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct MockRequest {
    id: String,
    method: String,
    #[serde(default)]
    params: Option<Value>,
}

impl MockRequest {
    fn params<T: serde::de::DeserializeOwned>(&self) -> Result<T, serde_json::Error> {
        serde_json::from_value(self.params.clone().unwrap_or(Value::Null))
    }
}

fn default_capabilities(worker_kind: &str) -> Vec<String> {
    let capabilities: &[&str] = if worker_kind == worker_kinds::LOCAL_AI {
        &[
            worker_methods::CONFIGURE,
            worker_methods::LOCAL_AI_TRANSLATE_STREAM,
            worker_methods::LOCAL_AI_GRAMMAR_STREAM,
            worker_methods::CANCEL,
            worker_methods::SHUTDOWN,
        ]
    } else {
        &[
            worker_methods::CONFIGURE,
            worker_methods::LONGDOC_TRANSLATE_DOCUMENT,
            worker_methods::CANCEL,
            worker_methods::SHUTDOWN,
        ]
    };
    capabilities
        .iter()
        .map(|value| (*value).to_string())
        .collect()
}

fn mock_translation(text: &str, timing_ms: i64) -> Value {
    json!({
        "translatedText": format!("mock:{text}"),
        "serviceId": "mock",
        "serviceName": "Mock Worker",
        "detectedLanguage": "English",
        "timingMs": timing_ms,
    })
}

fn mock_grammar_result(text: &str, language: &str) -> Value {
    json!({
        "originalText": text,
        "correctedText": "I have an apple.",
        "explanation": "Use have with I and an before apple.",
        "rawText": "[CORRECTED]I have an apple.[/CORRECTED]",
        "serviceId": "mock",
        "serviceName": "Mock Worker",
        "language": language,
        "timingMs": 9,
        "hasCorrections": true,
    })
}

fn write_event<D: serde::Serialize>(event: IpcEvent<D>) -> Result<(), String> {
    write_json_line(&event)
}

fn write_response<R: serde::Serialize>(response: IpcResponse<R>) -> Result<(), String> {
    write_json_line(&response)
}

fn write_json_line<T: serde::Serialize>(value: &T) -> Result<(), String> {
    let mut stdout = io::stdout().lock();
    serde_json::to_writer(&mut stdout, value).map_err(|error| error.to_string())?;
    stdout.write_all(b"\n").map_err(|error| error.to_string())?;
    stdout.flush().map_err(|error| error.to_string())
}

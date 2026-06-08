use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::Value;

pub use crate::protocol_core::*;

#[cfg(feature = "retained-dotnet-workers")]
pub const WORKER_PROTOCOL_VERSION_CURRENT: u32 = 1;

#[cfg(feature = "retained-dotnet-workers")]
pub mod worker_methods {
    pub const CONFIGURE: &str = "configure";
    pub const CANCEL: &str = "cancel";
    pub const SHUTDOWN: &str = "shutdown";
    pub const LONGDOC_TRANSLATE_DOCUMENT: &str = "translate_document";
    pub const LOCAL_AI_TRANSLATE_STREAM: &str = "translate_stream";
    pub const LOCAL_AI_GRAMMAR_STREAM: &str = "grammar_stream";
}

#[cfg(feature = "retained-dotnet-workers")]
pub mod worker_events {
    pub const READY: &str = "ready";
    pub const LONGDOC_STATUS: &str = "status";
    pub const LONGDOC_PROGRESS: &str = "progress";
    pub const LONGDOC_BLOCK_TRANSLATED: &str = "block_translated";
    pub const LOCAL_AI_CHUNK: &str = "chunk";
}

#[cfg(feature = "retained-dotnet-workers")]
pub mod worker_kinds {
    pub const LONGDOC: &str = "longdoc";
    pub const LOCAL_AI: &str = "localai";
}

#[cfg(feature = "retained-dotnet-workers")]
pub mod worker_error_codes {
    pub const CANCELLED: &str = "cancelled";
    pub const MODEL_MISSING: &str = "model_missing";
    pub const INVALID_PARAMS: &str = "invalid_params";
    pub const SERVICE_ERROR: &str = "service_error";
    pub const INTERNAL: &str = "internal_error";
    pub const VERSION_MISMATCH: &str = "version_mismatch";
}

#[cfg(feature = "retained-dotnet-workers")]
pub mod ipc_error_codes {
    pub const INVALID_JSON: &str = "invalid_json";
    pub const METHOD_NOT_FOUND: &str = "method_not_found";
    pub const INVALID_PARAMS: &str = "invalid_params";
    pub const INTERNAL_ERROR: &str = "internal_error";
    pub const SERVICE_ERROR: &str = "service_error";
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct IpcRequest<P = Value> {
    pub id: String,
    pub method: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub params: Option<P>,
}

#[cfg(feature = "retained-dotnet-workers")]
impl<P> IpcRequest<P> {
    pub fn new(id: impl Into<String>, method: impl Into<String>, params: P) -> Self {
        Self {
            id: id.into(),
            method: method.into(),
            params: Some(params),
        }
    }

    pub fn without_params(id: impl Into<String>, method: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            method: method.into(),
            params: None,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct IpcResponse<R = Value> {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<R>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<IpcError>,
}

#[cfg(feature = "retained-dotnet-workers")]
impl<R> IpcResponse<R> {
    pub fn ok(id: impl Into<String>, result: R) -> Self {
        Self {
            id: Some(id.into()),
            result: Some(result),
            error: None,
        }
    }

    pub fn error(id: impl Into<String>, error: IpcError) -> Self {
        Self {
            id: Some(id.into()),
            result: None,
            error: Some(error),
        }
    }

    pub fn is_success(&self) -> bool {
        self.error.is_none() && self.result.is_some()
    }

    pub fn is_error(&self) -> bool {
        self.error.is_some()
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct IpcError {
    pub code: String,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub details: Option<Value>,
}

#[cfg(feature = "retained-dotnet-workers")]
impl IpcError {
    pub fn new(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
            details: None,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct IpcEvent<D = Value> {
    pub event: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub data: Option<D>,
}

#[cfg(feature = "retained-dotnet-workers")]
impl<D> IpcEvent<D> {
    pub fn new(event: impl Into<String>, data: D) -> Self {
        Self {
            event: event.into(),
            id: None,
            data: Some(data),
        }
    }

    pub fn for_request(id: impl Into<String>, event: impl Into<String>, data: D) -> Self {
        Self {
            event: event.into(),
            id: Some(id.into()),
            data: Some(data),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct IpcMessage {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<IpcError>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub event: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub data: Option<Value>,
}

#[cfg(feature = "retained-dotnet-workers")]
impl IpcMessage {
    pub fn is_event(&self) -> bool {
        self.event.is_some()
    }

    pub fn is_response(&self) -> bool {
        self.id.is_some() && self.event.is_none()
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct ReadyEventData {
    pub worker_kind: String,
    pub worker_version: String,
    pub protocol_version: u32,
    pub capabilities: Vec<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct ConfigureParams {
    pub settings: SettingsSnapshot,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct ConfigureResult {
    pub ok: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct ShutdownResult {
    pub ok: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct CancelRequestParams {
    pub target_request_id: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct CancelRequestResult {
    pub cancelled: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct LocalAiTranslateParams {
    pub text: String,
    pub from_language: String,
    pub to_language: String,
    pub provider_mode: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_prompt: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub include_explanations: Option<bool>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct ChunkEventData {
    pub text: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg(feature = "retained-dotnet-workers")]
pub struct TranslateStreamResult {
    pub done: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub full_text: Option<String>,
}

#[cfg(feature = "retained-dotnet-workers")]
pub fn serialize_json_line<T: Serialize>(value: &T) -> serde_json::Result<String> {
    serialize_json(value)
}

#[cfg(feature = "retained-dotnet-workers")]
pub fn serialize_json_line_with_newline<T: Serialize>(value: &T) -> serde_json::Result<String> {
    serialize_json(value).map(|mut value| {
        value.push('\n');
        value
    })
}

#[cfg(feature = "retained-dotnet-workers")]
pub fn deserialize_json_line<T: DeserializeOwned>(line: &str) -> serde_json::Result<T> {
    deserialize_json(line)
}

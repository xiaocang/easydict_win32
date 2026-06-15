#![cfg(feature = "retained-dotnet-workers")]

use crate::compat_protocol::{
    deserialize_json_line, ipc_error_codes, serialize_json_line_with_newline, worker_events,
    worker_kinds, worker_methods, CancelRequestParams, CancelRequestResult, ChunkEventData,
    ConfigureParams, ConfigureResult, IpcError, IpcEvent, IpcMessage, IpcRequest,
    LocalAiTranslateParams, ReadyEventData, ShutdownResult, TranslateDocumentParams,
    TranslateDocumentResult, TranslateStreamResult, WORKER_PROTOCOL_VERSION_CURRENT,
};
use crate::openvino_download::{
    openvino_ep_path_injection_enabled, openvino_runtime_path_with_directory,
};
use crate::runtime_policy::{
    RetainedWorkerPolicy, LOCAL_AI_WORKER_DISABLED_MESSAGE, LONGDOC_WORKER_DISABLED_MESSAGE,
};
use easydict_nllb::{NllbModelPaths, OPENVINO_EP_ENABLE_ENVIRONMENT_VARIABLE};
use serde::de::DeserializeOwned;
use serde::Serialize;
use serde_json::Value;
use std::collections::VecDeque;
use std::fmt;
use std::io::{self, BufRead, BufReader, BufWriter, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};

pub struct WorkerCommand {
    program: PathBuf,
    args: Vec<String>,
    envs: Vec<(String, String)>,
    deferred_dotnet_root: Option<PathBuf>,
    retained_worker_kind: Option<RetainedWorkerKind>,
}

impl WorkerCommand {
    pub fn new(program: impl Into<PathBuf>) -> Self {
        Self {
            program: program.into(),
            args: Vec::new(),
            envs: Vec::new(),
            deferred_dotnet_root: None,
            retained_worker_kind: None,
        }
    }

    pub fn arg(mut self, arg: impl Into<String>) -> Self {
        self.args.push(arg.into());
        self
    }

    pub fn env(mut self, key: impl Into<String>, value: impl Into<String>) -> Self {
        self.envs.push((key.into(), value.into()));
        self
    }

    pub fn program(&self) -> &Path {
        &self.program
    }

    pub fn args(&self) -> &[String] {
        &self.args
    }

    pub fn envs(&self) -> &[(String, String)] {
        &self.envs
    }

    pub fn spawn(self) -> Result<WorkerClient, WorkerClientError> {
        WorkerClient::spawn(self)
    }

    fn retained_worker(mut self, kind: RetainedWorkerKind) -> Self {
        self.retained_worker_kind = Some(kind);
        self
    }

    fn deferred_dotnet_root(mut self, dotnet_root: impl Into<PathBuf>) -> Self {
        self.deferred_dotnet_root = Some(dotnet_root.into());
        self
    }

    fn apply_deferred_retained_worker_environment(&mut self) {
        let Some(dotnet_root) = self.deferred_dotnet_root.take() else {
            return;
        };

        if has_bundled_dotnet_runtime(&dotnet_root) {
            let dotnet_root = dotnet_root.to_string_lossy().to_string();
            self.envs
                .push(("DOTNET_ROOT".to_string(), dotnet_root.clone()));
            self.envs
                .push(("DOTNET_ROOT_X64".to_string(), dotnet_root.clone()));
            self.envs
                .push(("DOTNET_ROOT_ARM64".to_string(), dotnet_root));
        }
    }

    fn ensure_retained_worker_is_enabled(&self) -> Result<(), WorkerClientError> {
        let Some(kind) = self.retained_worker_kind else {
            return Ok(());
        };

        let policy = RetainedWorkerPolicy::from_environment();
        if kind.is_enabled_by(policy) {
            return Ok(());
        }

        Err(WorkerClientError::Protocol(kind.disabled_message()))
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum RetainedWorkerKind {
    LongDoc,
    LocalAi,
    Other,
}

impl RetainedWorkerKind {
    fn from_worker_subdir(worker_subdir: &str) -> Self {
        if worker_subdir.eq_ignore_ascii_case("longdoc") {
            return Self::LongDoc;
        }

        if worker_subdir.eq_ignore_ascii_case("localai") {
            return Self::LocalAi;
        }

        Self::Other
    }

    fn is_enabled_by(self, policy: RetainedWorkerPolicy) -> bool {
        match self {
            Self::LongDoc => policy.longdoc_worker_enabled,
            Self::LocalAi => policy.local_ai_worker_enabled,
            Self::Other => policy.longdoc_worker_enabled && policy.local_ai_worker_enabled,
        }
    }

    fn disabled_message(self) -> String {
        let base = match self {
            Self::LongDoc => LONGDOC_WORKER_DISABLED_MESSAGE,
            Self::LocalAi => LOCAL_AI_WORKER_DISABLED_MESSAGE,
            Self::Other => "Retained .NET worker requires a Rust-native route for this request.",
        };

        format!("{base} Set EASYDICT_RUNTIME_PROFILE=hybrid to enable retained .NET workers.")
    }
}

pub struct DirectWorkerFacade {
    client: WorkerClient,
}

impl DirectWorkerFacade {
    pub fn spawn_packaged_longdoc(app_dir: impl AsRef<Path>) -> Result<Self, WorkerClientError> {
        Self::spawn_packaged_worker(
            app_dir,
            worker_kinds::LONGDOC,
            "longdoc",
            "Easydict.Workers.LongDoc.exe",
            None,
        )
    }

    pub fn spawn_packaged_local_ai(app_dir: impl AsRef<Path>) -> Result<Self, WorkerClientError> {
        Self::spawn_packaged_local_ai_with_cache_base(app_dir, None)
    }

    pub fn spawn_packaged_local_ai_with_cache_base(
        app_dir: impl AsRef<Path>,
        openvino_cache_base: Option<&Path>,
    ) -> Result<Self, WorkerClientError> {
        Self::spawn_packaged_worker(
            app_dir,
            worker_kinds::LOCAL_AI,
            "localai",
            "Easydict.Workers.LocalAi.exe",
            openvino_cache_base,
        )
    }

    pub fn spawn_worker(
        command: WorkerCommand,
        expected_worker_kind: &str,
    ) -> Result<Self, WorkerClientError> {
        let mut client = command.spawn()?;
        client.wait_for_worker_ready(expected_worker_kind)?;
        Ok(Self { client })
    }

    pub fn new_ready(client: WorkerClient) -> Self {
        Self { client }
    }

    pub fn configure(
        &mut self,
        params: &ConfigureParams,
    ) -> Result<ConfigureResult, WorkerClientError> {
        self.client.send_request(worker_methods::CONFIGURE, params)
    }

    pub fn longdoc_translate(
        &mut self,
        params: &TranslateDocumentParams,
    ) -> Result<TranslateDocumentResult, WorkerClientError> {
        self.client
            .send_request(worker_methods::LONGDOC_TRANSLATE_DOCUMENT, params)
    }

    pub fn cancel_request(
        &mut self,
        target_request_id: impl Into<String>,
    ) -> Result<CancelRequestResult, WorkerClientError> {
        self.client.send_request(
            worker_methods::CANCEL,
            &CancelRequestParams {
                target_request_id: target_request_id.into(),
            },
        )
    }

    pub fn shutdown(&mut self) -> Result<ShutdownResult, WorkerClientError> {
        self.client
            .send_request_without_params(worker_methods::SHUTDOWN)
    }

    pub fn local_ai_translate_stream_observing_chunks(
        &mut self,
        params: &LocalAiTranslateParams,
        mut on_chunk: impl FnMut(ChunkEventData),
    ) -> Result<TranslateStreamResult, WorkerClientError> {
        self.send_local_ai_stream_request(
            worker_methods::LOCAL_AI_TRANSLATE_STREAM,
            params,
            &mut on_chunk,
        )
    }

    pub fn local_ai_grammar_stream_observing_chunks(
        &mut self,
        params: &LocalAiTranslateParams,
        mut on_chunk: impl FnMut(ChunkEventData),
    ) -> Result<TranslateStreamResult, WorkerClientError> {
        self.send_local_ai_stream_request(
            worker_methods::LOCAL_AI_GRAMMAR_STREAM,
            params,
            &mut on_chunk,
        )
    }

    pub fn take_events(&mut self) -> Vec<IpcEvent<Value>> {
        self.client.take_events()
    }

    pub fn into_client(self) -> WorkerClient {
        self.client
    }

    fn spawn_packaged_worker(
        app_dir: impl AsRef<Path>,
        expected_worker_kind: &str,
        worker_subdir: &str,
        worker_exe_name: &str,
        openvino_cache_base: Option<&Path>,
    ) -> Result<Self, WorkerClientError> {
        Self::spawn_worker(
            packaged_worker_command_with_openvino_cache_base(
                app_dir,
                worker_subdir,
                worker_exe_name,
                openvino_cache_base,
            ),
            expected_worker_kind,
        )
    }

    fn send_local_ai_stream_request(
        &mut self,
        method: &str,
        params: &LocalAiTranslateParams,
        on_chunk: &mut impl FnMut(ChunkEventData),
    ) -> Result<TranslateStreamResult, WorkerClientError> {
        let result = self
            .client
            .send_request_observing_events(method, params, |event| {
                if event.event != worker_events::LOCAL_AI_CHUNK {
                    return;
                }

                let Some(data) = event.data.clone() else {
                    return;
                };

                if let Ok(chunk) = serde_json::from_value::<ChunkEventData>(data) {
                    on_chunk(chunk);
                }
            });

        let _ = self.take_events();
        result
    }
}

pub struct WorkerClient {
    child: Child,
    stdin: BufWriter<ChildStdin>,
    stdout: BufReader<ChildStdout>,
    next_request_number: u64,
    events: VecDeque<IpcEvent<Value>>,
}

impl WorkerClient {
    pub fn spawn(mut command: WorkerCommand) -> Result<Self, WorkerClientError> {
        command.ensure_retained_worker_is_enabled()?;
        command.apply_deferred_retained_worker_environment();

        let mut process = Command::new(&command.program);
        process
            .args(command.args)
            .envs(command.envs.iter().map(|(key, value)| (key, value)))
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null());
        let mut child = process.spawn().map_err(WorkerClientError::Io)?;

        let stdin = child
            .stdin
            .take()
            .ok_or(WorkerClientError::MissingPipe("stdin"))?;
        let stdout = child
            .stdout
            .take()
            .ok_or(WorkerClientError::MissingPipe("stdout"))?;

        Ok(Self {
            child,
            stdin: BufWriter::new(stdin),
            stdout: BufReader::new(stdout),
            next_request_number: 0,
            events: VecDeque::new(),
        })
    }

    pub fn process_id(&self) -> u32 {
        self.child.id()
    }

    pub fn send_request<P, R>(
        &mut self,
        method: impl Into<String>,
        params: &P,
    ) -> Result<R, WorkerClientError>
    where
        P: Serialize,
        R: DeserializeOwned,
    {
        self.send_request_with_request_id(method, params, |_| {})
    }

    pub fn send_request_with_request_id<P, R, F>(
        &mut self,
        method: impl Into<String>,
        params: &P,
        on_request_id: F,
    ) -> Result<R, WorkerClientError>
    where
        P: Serialize,
        R: DeserializeOwned,
        F: FnOnce(&str),
    {
        let id = self.next_request_id();
        on_request_id(&id);
        let params = serde_json::to_value(params).map_err(WorkerClientError::Json)?;
        let request = IpcRequest::new(id.clone(), method, params);

        self.write_request(&request)?;
        self.read_response(&id)
    }

    pub fn send_request_observing_events<P, R, F>(
        &mut self,
        method: impl Into<String>,
        params: &P,
        on_event: F,
    ) -> Result<R, WorkerClientError>
    where
        P: Serialize,
        R: DeserializeOwned,
        F: FnMut(&IpcEvent<Value>),
    {
        self.send_request_observing_events_with_request_id(method, params, |_| {}, on_event)
    }

    pub fn send_request_observing_events_with_request_id<P, R, F, G>(
        &mut self,
        method: impl Into<String>,
        params: &P,
        on_request_id: G,
        on_event: F,
    ) -> Result<R, WorkerClientError>
    where
        P: Serialize,
        R: DeserializeOwned,
        F: FnMut(&IpcEvent<Value>),
        G: FnOnce(&str),
    {
        let id = self.next_request_id();
        on_request_id(&id);
        let params = serde_json::to_value(params).map_err(WorkerClientError::Json)?;
        let request = IpcRequest::new(id.clone(), method, params);

        self.write_request(&request)?;
        self.read_response_observing_events(&id, on_event)
    }

    pub fn send_request_without_params<R>(
        &mut self,
        method: impl Into<String>,
    ) -> Result<R, WorkerClientError>
    where
        R: DeserializeOwned,
    {
        let id = self.next_request_id();
        let request: IpcRequest<Value> = IpcRequest::without_params(id.clone(), method);

        self.write_request(&request)?;
        self.read_response(&id)
    }

    pub fn take_events(&mut self) -> Vec<IpcEvent<Value>> {
        self.events.drain(..).collect()
    }

    pub fn wait_for_worker_ready(
        &mut self,
        expected_worker_kind: &str,
    ) -> Result<ReadyEventData, WorkerClientError> {
        let message = self.read_message()?;
        if !message.is_event() {
            return Err(WorkerClientError::Protocol(
                "worker emitted a response before the ready event".to_string(),
            ));
        }

        let event = message
            .event
            .ok_or_else(|| WorkerClientError::Protocol("worker event omitted name".to_string()))?;
        if event != worker_events::READY {
            return Err(WorkerClientError::Protocol(format!(
                "worker emitted unexpected event {event:?} before ready"
            )));
        }

        let data = message.data.ok_or_else(|| {
            WorkerClientError::Protocol("worker ready event omitted data".to_string())
        })?;
        let ready: ReadyEventData =
            serde_json::from_value(data).map_err(WorkerClientError::Json)?;
        if ready.worker_kind != expected_worker_kind {
            return Err(WorkerClientError::Protocol(format!(
                "expected worker kind {expected_worker_kind:?} but worker reported {:?}",
                ready.worker_kind
            )));
        }

        if ready.protocol_version != WORKER_PROTOCOL_VERSION_CURRENT {
            return Err(WorkerClientError::Protocol(format!(
                "worker {} reports protocol version {}; client expects {}",
                ready.worker_kind, ready.protocol_version, WORKER_PROTOCOL_VERSION_CURRENT
            )));
        }
        validate_worker_capabilities(expected_worker_kind, &ready.capabilities)?;

        Ok(ready)
    }

    fn next_request_id(&mut self) -> String {
        self.next_request_number += 1;
        format!("rust-worker-{}", self.next_request_number)
    }

    fn write_request(&mut self, request: &IpcRequest<Value>) -> Result<(), WorkerClientError> {
        let line = serialize_json_line_with_newline(request).map_err(WorkerClientError::Json)?;
        self.stdin
            .write_all(line.as_bytes())
            .map_err(WorkerClientError::Io)?;
        self.stdin.flush().map_err(WorkerClientError::Io)
    }

    fn read_response<R>(&mut self, expected_id: &str) -> Result<R, WorkerClientError>
    where
        R: DeserializeOwned,
    {
        self.read_response_observing_events(expected_id, |_| {})
    }

    fn read_response_observing_events<R, F>(
        &mut self,
        expected_id: &str,
        mut on_event: F,
    ) -> Result<R, WorkerClientError>
    where
        R: DeserializeOwned,
        F: FnMut(&IpcEvent<Value>),
    {
        loop {
            let message = self.read_message()?;
            if message.is_event() {
                let event = IpcEvent {
                    event: message.event.expect("event checked above"),
                    id: message.id,
                    data: message.data,
                };
                on_event(&event);
                self.events.push_back(event);
                continue;
            }

            if !message.is_response() {
                return Err(WorkerClientError::Protocol(
                    "message was neither response nor event".to_string(),
                ));
            }

            let id = message
                .id
                .ok_or_else(|| WorkerClientError::Protocol("response omitted id".to_string()))?;
            if id != expected_id {
                return Err(WorkerClientError::Protocol(format!(
                    "response id {id:?} did not match expected id {expected_id:?}"
                )));
            }

            if let Some(error) = message.error {
                return Err(WorkerClientError::Remote(error));
            }

            let result = message.result.ok_or_else(|| {
                WorkerClientError::Remote(IpcError::new(
                    ipc_error_codes::INTERNAL_ERROR,
                    "response omitted result",
                ))
            })?;

            return serde_json::from_value(result).map_err(WorkerClientError::Json);
        }
    }

    fn read_message(&mut self) -> Result<IpcMessage, WorkerClientError> {
        let mut line = String::new();
        let bytes = self
            .stdout
            .read_line(&mut line)
            .map_err(WorkerClientError::Io)?;
        if bytes == 0 {
            return Err(WorkerClientError::ProcessExited);
        }

        deserialize_json_line(line.trim_end()).map_err(WorkerClientError::Json)
    }
}

fn validate_worker_capabilities(
    expected_worker_kind: &str,
    capabilities: &[String],
) -> Result<(), WorkerClientError> {
    let required = match expected_worker_kind {
        worker_kinds::LONGDOC => &[
            worker_methods::CONFIGURE,
            worker_methods::LONGDOC_TRANSLATE_DOCUMENT,
            worker_methods::CANCEL,
            worker_methods::SHUTDOWN,
        ][..],
        worker_kinds::LOCAL_AI => &[
            worker_methods::CONFIGURE,
            worker_methods::LOCAL_AI_TRANSLATE_STREAM,
            worker_methods::LOCAL_AI_GRAMMAR_STREAM,
            worker_methods::CANCEL,
            worker_methods::SHUTDOWN,
        ][..],
        _ => return Ok(()),
    };

    for capability in required {
        if !capabilities.iter().any(|actual| actual == capability) {
            return Err(WorkerClientError::Protocol(format!(
                "worker {expected_worker_kind} ready event is missing required capability {capability:?}"
            )));
        }
    }

    Ok(())
}

impl Drop for WorkerClient {
    fn drop(&mut self) {
        if matches!(self.child.try_wait(), Ok(None)) {
            let _ = self.child.kill();
            let _ = self.child.wait();
        }
    }
}

#[derive(Debug)]
pub enum WorkerClientError {
    Io(io::Error),
    Json(serde_json::Error),
    MissingPipe(&'static str),
    Remote(IpcError),
    Protocol(String),
    ProcessExited,
}

impl WorkerClientError {
    pub fn is_not_found(&self) -> bool {
        matches!(
            self,
            Self::Io(error) if error.kind() == io::ErrorKind::NotFound
        )
    }

    pub fn process_message(&self, process_label: &str) -> String {
        match self {
            Self::Io(error) => format!("{process_label} I/O error: {error}"),
            Self::Json(error) => format!("{process_label} JSON error: {error}"),
            Self::MissingPipe(pipe) => format!("{process_label} missing {pipe} pipe"),
            Self::Remote(error) => {
                format!(
                    "{process_label} remote error [{}]: {}",
                    error.code, error.message
                )
            }
            Self::Protocol(message) => format!("{process_label} protocol error: {message}"),
            Self::ProcessExited => format!("{process_label} process exited"),
        }
    }
}

impl fmt::Display for WorkerClientError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(f, "worker I/O error: {error}"),
            Self::Json(error) => write!(f, "worker JSON error: {error}"),
            Self::MissingPipe(pipe) => write!(f, "worker missing {pipe} pipe"),
            Self::Remote(error) => {
                write!(f, "worker remote error [{}]: {}", error.code, error.message)
            }
            Self::Protocol(message) => write!(f, "worker protocol error: {message}"),
            Self::ProcessExited => write!(f, "worker process exited"),
        }
    }
}

impl std::error::Error for WorkerClientError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            Self::Json(error) => Some(error),
            Self::MissingPipe(_) | Self::Remote(_) | Self::Protocol(_) | Self::ProcessExited => {
                None
            }
        }
    }
}

pub fn default_worker_path(
    app_dir: impl AsRef<Path>,
    worker_subdir: &str,
    worker_exe_name: &str,
) -> PathBuf {
    app_dir
        .as_ref()
        .join("workers")
        .join(worker_subdir)
        .join(worker_exe_name)
}

pub fn default_longdoc_worker_path(app_dir: impl AsRef<Path>) -> PathBuf {
    default_worker_path(app_dir, "longdoc", "Easydict.Workers.LongDoc.exe")
}

pub fn default_local_ai_worker_path(app_dir: impl AsRef<Path>) -> PathBuf {
    default_worker_path(app_dir, "localai", "Easydict.Workers.LocalAi.exe")
}

pub fn packaged_worker_command(
    app_dir: impl AsRef<Path>,
    worker_subdir: &str,
    worker_exe_name: &str,
) -> WorkerCommand {
    packaged_worker_command_with_openvino_cache_base(app_dir, worker_subdir, worker_exe_name, None)
}

pub fn packaged_worker_command_with_openvino_cache_base(
    app_dir: impl AsRef<Path>,
    worker_subdir: &str,
    worker_exe_name: &str,
    openvino_cache_base: Option<&Path>,
) -> WorkerCommand {
    let app_dir = app_dir.as_ref();
    let mut command =
        WorkerCommand::new(default_worker_path(app_dir, worker_subdir, worker_exe_name))
            .env(
                "EASYDICT_WORKER_SHARED_DIR",
                app_dir
                    .join("workers")
                    .join("shared")
                    .to_string_lossy()
                    .to_string(),
            )
            .env("DOTNET_CLI_TELEMETRY_OPTOUT", "1");

    command = command.retained_worker(RetainedWorkerKind::from_worker_subdir(worker_subdir));

    command = command.deferred_dotnet_root(app_dir.join("dotnet"));

    if worker_subdir.eq_ignore_ascii_case("localai") {
        let openvino_ep_value =
            std::env::var(OPENVINO_EP_ENABLE_ENVIRONMENT_VARIABLE).unwrap_or_default();
        command = command.env(
            OPENVINO_EP_ENABLE_ENVIRONMENT_VARIABLE,
            openvino_ep_value.clone(),
        );

        if openvino_ep_path_injection_enabled(Some(&openvino_ep_value)) {
            if let Some(openvino_runtime_dir) = openvino_runtime_dir_for_worker(openvino_cache_base)
            {
                let openvino_runtime_dir_text = openvino_runtime_dir.to_string_lossy().to_string();
                let existing_path = std::env::var("PATH").unwrap_or_default();
                let path =
                    openvino_runtime_path_with_directory(&existing_path, &openvino_runtime_dir)
                        .unwrap_or(existing_path);
                command = command
                    .env("EASYDICT_OPENVINO_RUNTIME_DIR", openvino_runtime_dir_text)
                    .env("PATH", path);
            }
        }
    }

    command
}

fn has_bundled_dotnet_runtime(dotnet_root: &Path) -> bool {
    dotnet_root.join("host").join("fxr").is_dir()
        && dotnet_root
            .join("shared")
            .join("Microsoft.NETCore.App")
            .is_dir()
}

fn openvino_runtime_dir_for_worker(openvino_cache_base: Option<&Path>) -> Option<PathBuf> {
    if let Some(cache_base) = openvino_cache_base {
        return Some(NllbModelPaths::from_cache_base(cache_base).runtime_dir);
    }

    std::env::var_os("LOCALAPPDATA").map(|local_app_data| {
        NllbModelPaths::from_cache_base(PathBuf::from(local_app_data).join("Easydict")).runtime_dir
    })
}

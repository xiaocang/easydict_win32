use crate::compat_protocol::{
    compat_events, compat_methods, deserialize_json_line, ipc_error_codes,
    serialize_json_line_with_newline, worker_methods, ConfigureParams, ConfigureResult,
    GrammarCorrectParams, GrammarCorrectResultDto, IpcError, IpcEvent, IpcMessage, IpcRequest,
    LocalAiTranslateParams, LocalAiTranslateResult, LocalModelStatusDto, MdxLookupParams,
    MdxLookupResult, OcrRecognizeParams, OcrResultDto, PrepareModelParams, SettingsMigrateParams,
    SettingsMigrateResult, TranslateChunkEventData, TranslateDocumentParams,
    TranslateDocumentResult, TranslateParams, TranslationResultDto,
};
use serde::de::DeserializeOwned;
use serde::Serialize;
use serde_json::Value;
use std::collections::VecDeque;
use std::fmt;
use std::io::{self, BufRead, BufReader, BufWriter, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};

pub struct CompatHostCommand {
    program: PathBuf,
    args: Vec<String>,
}

impl CompatHostCommand {
    pub fn new(program: impl Into<PathBuf>) -> Self {
        Self {
            program: program.into(),
            args: Vec::new(),
        }
    }

    pub fn packaged(app_dir: impl AsRef<Path>) -> Self {
        Self::new(default_compat_host_path(app_dir))
    }

    pub fn arg(mut self, arg: impl Into<String>) -> Self {
        self.args.push(arg.into());
        self
    }

    pub fn program(&self) -> &Path {
        &self.program
    }

    pub fn args(&self) -> &[String] {
        &self.args
    }

    pub fn spawn(self) -> Result<CompatHostClient, CompatClientError> {
        CompatHostClient::spawn(self)
    }
}

pub struct CompatHostFacade {
    client: CompatHostClient,
}

impl CompatHostFacade {
    pub fn spawn_packaged(app_dir: impl AsRef<Path>) -> Result<Self, CompatClientError> {
        CompatHostCommand::packaged(app_dir).spawn().map(Self::new)
    }

    pub fn new(client: CompatHostClient) -> Self {
        Self { client }
    }

    pub fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, CompatClientError> {
        self.client.send_request(compat_methods::TRANSLATE, params)
    }

    pub fn configure(
        &mut self,
        params: &ConfigureParams,
    ) -> Result<ConfigureResult, CompatClientError> {
        self.client.send_request(worker_methods::CONFIGURE, params)
    }

    pub fn translate_stream(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, CompatClientError> {
        self.client
            .send_request(compat_methods::TRANSLATE_STREAM, params)
    }

    pub fn translate_stream_observing_chunks(
        &mut self,
        params: &TranslateParams,
        mut on_chunk: impl FnMut(TranslateChunkEventData),
    ) -> Result<TranslationResultDto, CompatClientError> {
        let result = self.client.send_request_observing_events(
            compat_methods::TRANSLATE_STREAM,
            params,
            |event| {
                if event.event != compat_events::TRANSLATE_CHUNK {
                    return;
                }

                let Some(data) = event.data.clone() else {
                    return;
                };

                if let Ok(chunk) = serde_json::from_value::<TranslateChunkEventData>(data) {
                    on_chunk(chunk);
                }
            },
        );

        let _ = self.take_events();
        result
    }

    pub fn grammar_correct(
        &mut self,
        params: &GrammarCorrectParams,
    ) -> Result<GrammarCorrectResultDto, CompatClientError> {
        self.client
            .send_request(compat_methods::GRAMMAR_CORRECT, params)
    }

    pub fn ocr_recognize(
        &mut self,
        params: &OcrRecognizeParams,
    ) -> Result<OcrResultDto, CompatClientError> {
        self.client
            .send_request(compat_methods::OCR_RECOGNIZE, params)
    }

    pub fn longdoc_translate(
        &mut self,
        params: &TranslateDocumentParams,
    ) -> Result<TranslateDocumentResult, CompatClientError> {
        self.client
            .send_request(compat_methods::LONGDOC_TRANSLATE, params)
    }

    pub fn local_ai_prepare(
        &mut self,
        params: &PrepareModelParams,
    ) -> Result<LocalModelStatusDto, CompatClientError> {
        self.client
            .send_request(compat_methods::LOCAL_AI_PREPARE, params)
    }

    pub fn local_ai_translate(
        &mut self,
        params: &LocalAiTranslateParams,
    ) -> Result<LocalAiTranslateResult, CompatClientError> {
        self.client
            .send_request(compat_methods::LOCAL_AI_TRANSLATE, params)
    }

    pub fn mdx_lookup(
        &mut self,
        params: &MdxLookupParams,
    ) -> Result<MdxLookupResult, CompatClientError> {
        self.client.send_request(compat_methods::MDX_LOOKUP, params)
    }

    pub fn settings_migrate(
        &mut self,
        params: &SettingsMigrateParams,
    ) -> Result<SettingsMigrateResult, CompatClientError> {
        self.client
            .send_request(compat_methods::SETTINGS_MIGRATE, params)
    }

    pub fn take_events(&mut self) -> Vec<IpcEvent<Value>> {
        self.client.take_events()
    }

    pub fn into_client(self) -> CompatHostClient {
        self.client
    }
}

pub struct CompatHostClient {
    child: Child,
    stdin: BufWriter<ChildStdin>,
    stdout: BufReader<ChildStdout>,
    next_request_number: u64,
    events: VecDeque<IpcEvent<Value>>,
}

impl CompatHostClient {
    pub fn spawn(command: CompatHostCommand) -> Result<Self, CompatClientError> {
        let mut child = Command::new(&command.program)
            .args(command.args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .map_err(CompatClientError::Io)?;

        let stdin = child
            .stdin
            .take()
            .ok_or(CompatClientError::MissingPipe("stdin"))?;
        let stdout = child
            .stdout
            .take()
            .ok_or(CompatClientError::MissingPipe("stdout"))?;

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
    ) -> Result<R, CompatClientError>
    where
        P: Serialize,
        R: DeserializeOwned,
    {
        let id = self.next_request_id();
        let params = serde_json::to_value(params).map_err(CompatClientError::Json)?;
        let request = IpcRequest::new(id.clone(), method, params);

        self.write_request(&request)?;
        self.read_response(&id)
    }

    pub fn send_request_observing_events<P, R, F>(
        &mut self,
        method: impl Into<String>,
        params: &P,
        on_event: F,
    ) -> Result<R, CompatClientError>
    where
        P: Serialize,
        R: DeserializeOwned,
        F: FnMut(&IpcEvent<Value>),
    {
        let id = self.next_request_id();
        let params = serde_json::to_value(params).map_err(CompatClientError::Json)?;
        let request = IpcRequest::new(id.clone(), method, params);

        self.write_request(&request)?;
        self.read_response_observing_events(&id, on_event)
    }

    pub fn send_request_without_params<R>(
        &mut self,
        method: impl Into<String>,
    ) -> Result<R, CompatClientError>
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

    fn next_request_id(&mut self) -> String {
        self.next_request_number += 1;
        format!("rust-compat-{}", self.next_request_number)
    }

    fn write_request(&mut self, request: &IpcRequest<Value>) -> Result<(), CompatClientError> {
        let line = serialize_json_line_with_newline(request).map_err(CompatClientError::Json)?;
        self.stdin
            .write_all(line.as_bytes())
            .map_err(CompatClientError::Io)?;
        self.stdin.flush().map_err(CompatClientError::Io)
    }

    fn read_response<R>(&mut self, expected_id: &str) -> Result<R, CompatClientError>
    where
        R: DeserializeOwned,
    {
        self.read_response_observing_events(expected_id, |_| {})
    }

    fn read_response_observing_events<R, F>(
        &mut self,
        expected_id: &str,
        mut on_event: F,
    ) -> Result<R, CompatClientError>
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
                return Err(CompatClientError::Protocol(
                    "message was neither response nor event".to_string(),
                ));
            }

            let id = message
                .id
                .ok_or_else(|| CompatClientError::Protocol("response omitted id".to_string()))?;
            if id != expected_id {
                return Err(CompatClientError::Protocol(format!(
                    "response id {id:?} did not match expected id {expected_id:?}"
                )));
            }

            if let Some(error) = message.error {
                return Err(CompatClientError::Remote(error));
            }

            let result = message.result.ok_or_else(|| {
                CompatClientError::Remote(IpcError::new(
                    ipc_error_codes::INTERNAL_ERROR,
                    "response omitted result",
                ))
            })?;

            return serde_json::from_value(result).map_err(CompatClientError::Json);
        }
    }

    fn read_message(&mut self) -> Result<IpcMessage, CompatClientError> {
        let mut line = String::new();
        let bytes = self
            .stdout
            .read_line(&mut line)
            .map_err(CompatClientError::Io)?;
        if bytes == 0 {
            return Err(CompatClientError::ProcessExited);
        }

        deserialize_json_line(line.trim_end()).map_err(CompatClientError::Json)
    }
}

impl Drop for CompatHostClient {
    fn drop(&mut self) {
        if matches!(self.child.try_wait(), Ok(None)) {
            let _ = self.child.kill();
            let _ = self.child.wait();
        }
    }
}

#[derive(Debug)]
pub enum CompatClientError {
    Io(io::Error),
    Json(serde_json::Error),
    MissingPipe(&'static str),
    Remote(IpcError),
    Protocol(String),
    ProcessExited,
}

impl CompatClientError {
    pub fn is_not_found(&self) -> bool {
        matches!(
            self,
            Self::Io(error) if error.kind() == io::ErrorKind::NotFound
        )
    }
}

impl fmt::Display for CompatClientError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(f, "compat host I/O error: {error}"),
            Self::Json(error) => write!(f, "compat host JSON error: {error}"),
            Self::MissingPipe(pipe) => write!(f, "compat host missing {pipe} pipe"),
            Self::Remote(error) => {
                write!(
                    f,
                    "compat host remote error [{}]: {}",
                    error.code, error.message
                )
            }
            Self::Protocol(message) => write!(f, "compat host protocol error: {message}"),
            Self::ProcessExited => write!(f, "compat host process exited"),
        }
    }
}

impl std::error::Error for CompatClientError {
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

pub fn default_compat_host_path(app_dir: impl AsRef<Path>) -> PathBuf {
    app_dir.as_ref().join("Easydict.CompatHost.exe")
}

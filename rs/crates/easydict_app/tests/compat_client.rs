#![cfg(feature = "retained-dotnet-workers")]

use easydict_app::compat_client::{
    default_local_ai_worker_path, default_longdoc_worker_path, packaged_worker_command,
    packaged_worker_command_with_openvino_cache_base, DirectWorkerFacade, WorkerClient,
    WorkerClientError, WorkerCommand,
};
use easydict_app::compat_protocol::{
    ipc_error_codes, worker_kinds, worker_methods, ConfigureParams, LocalAiTranslateParams,
    SettingsSnapshot, StatusEventData, TranslateDocumentParams, TranslateParams,
    TranslationResultDto, WORKER_PROTOCOL_VERSION_CURRENT,
};
use easydict_app::{
    GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, RUNTIME_PROFILE_ENVIRONMENT_VARIABLE,
};
use easydict_nllb::{NllbModelPaths, OPENVINO_EP_ENABLE_ENVIRONMENT_VARIABLE};
use serde_json::Value;
use std::ffi::OsString;
use std::path::Path;
use std::sync::Mutex;

static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

fn mock_jsonl_client() -> WorkerClient {
    WorkerCommand::new("powershell.exe")
        .arg("-NoProfile")
        .arg("-ExecutionPolicy")
        .arg("Bypass")
        .arg("-Command")
        .arg(MOCK_HOST_SCRIPT)
        .spawn()
        .expect("mock worker client must spawn")
}

fn mock_worker_command(worker_kind: &str, protocol_version: u32) -> WorkerCommand {
    WorkerCommand::new("powershell.exe")
        .arg("-NoProfile")
        .arg("-ExecutionPolicy")
        .arg("Bypass")
        .arg("-Command")
        .arg(MOCK_WORKER_SCRIPT)
        .env("MOCK_WORKER_KIND", worker_kind)
        .env("MOCK_WORKER_PROTOCOL_VERSION", protocol_version.to_string())
}

fn mock_worker_command_with_capabilities(
    worker_kind: &str,
    protocol_version: u32,
    capabilities: &[&str],
) -> WorkerCommand {
    mock_worker_command(worker_kind, protocol_version)
        .env("MOCK_WORKER_CAPABILITIES", capabilities.join(","))
}

fn mock_worker(worker_kind: &str) -> DirectWorkerFacade {
    DirectWorkerFacade::spawn_worker(
        mock_worker_command(worker_kind, WORKER_PROTOCOL_VERSION_CURRENT),
        worker_kind,
    )
    .expect("mock direct worker must spawn and emit ready")
}

struct EnvVarGuard {
    key: &'static str,
    previous: Option<OsString>,
}

impl EnvVarGuard {
    fn set(key: &'static str, value: &str) -> Self {
        let previous = std::env::var_os(key);
        std::env::set_var(key, value);
        Self { key, previous }
    }

    fn remove(key: &'static str) -> Self {
        let previous = std::env::var_os(key);
        std::env::remove_var(key);
        Self { key, previous }
    }
}

impl Drop for EnvVarGuard {
    fn drop(&mut self) {
        if let Some(previous) = &self.previous {
            std::env::set_var(self.key, previous);
        } else {
            std::env::remove_var(self.key);
        }
    }
}

fn retained_worker_disabled_error(error: WorkerClientError, expected_prefix: &str) {
    assert!(
        !error.is_not_found(),
        "retained worker guard should run before executable probing"
    );
    match error {
        WorkerClientError::Protocol(message) => {
            assert!(message.contains(expected_prefix));
            assert!(message.contains("requires a Rust-native route"));
            assert!(message.contains("EASYDICT_RUNTIME_PROFILE=hybrid"));
        }
        other => panic!("expected retained worker protocol guard, got {other:?}"),
    }
}

const MOCK_HOST_SCRIPT: &str = r#"
# Packaged workers speak UTF-8 JSON Lines. Force UTF-8 on both streams so
# the mock matches that contract on non-UTF-8 default locales (e.g. zh-CN GBK consoles),
# otherwise non-ASCII payloads like translated text are emitted in the system codepage
# and fail the Rust client's UTF-8 line reader.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding = [System.Text.Encoding]::UTF8

function Write-JsonLine($value) {
    $json = $value | ConvertTo-Json -Compress -Depth 16
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

while (($line = [Console]::In.ReadLine()) -ne $null) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    try {
        $request = $line | ConvertFrom-Json
    }
    catch {
        Write-JsonLine ([ordered]@{
            id = 'malformed'
            error = [ordered]@{
                code = 'invalid_json'
                message = $_.Exception.Message
            }
        })
        continue
    }

    switch ($request.method) {
        'configure' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ ok = $true }
            })
        }
        'translate' {
            $text = [string]$request.params.text
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    translatedText = "mock:$text"
                    serviceId = 'mock'
                    serviceName = 'Mock Worker'
                    detectedLanguage = 'English'
                    timingMs = 7
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
                    serviceName = 'Mock Worker'
                    detectedLanguage = 'English'
                    timingMs = 8
                }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    translatedText = "mock:$text"
                    serviceId = 'mock'
                    serviceName = 'Mock Worker'
                    detectedLanguage = 'English'
                    timingMs = 8
                }
            })
        }
        'grammar_correct' {
            $text = [string]$request.params.text
            $language = [string]$request.params.language
            Write-JsonLine ([ordered]@{
                event = 'grammar_chunk'
                id = $request.id
                data = [ordered]@{ text = '[CORRECTED]' }
            })
            Write-JsonLine ([ordered]@{
                event = 'grammar_chunk'
                id = $request.id
                data = [ordered]@{ text = 'I have an apple.' }
            })
            Write-JsonLine ([ordered]@{
                event = 'grammar_done'
                id = $request.id
                data = [ordered]@{
                    originalText = $text
                    correctedText = 'I have an apple.'
                    explanation = 'Use have with I and an before apple.'
                    rawText = '[CORRECTED]I have an apple.[/CORRECTED]'
                    serviceId = 'mock'
                    serviceName = 'Mock Worker'
                    language = $language
                    timingMs = 9
                    hasCorrections = $true
                }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    originalText = $text
                    correctedText = 'I have an apple.'
                    explanation = 'Use have with I and an before apple.'
                    rawText = '[CORRECTED]I have an apple.[/CORRECTED]'
                    serviceId = 'mock'
                    serviceName = 'Mock Worker'
                    language = $language
                    timingMs = 9
                    hasCorrections = $true
                }
            })
        }
        'emit_event_then_translate' {
            Write-JsonLine ([ordered]@{
                event = 'chunk'
                id = $request.id
                data = [ordered]@{ text = 'mock:' }
            })

            $text = [string]$request.params.text
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    translatedText = "mock:$text"
                    serviceId = 'mock'
                    serviceName = 'Mock Worker'
                    detectedLanguage = 'English'
                    timingMs = 7
                }
            })
        }
        'fail_remote' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                error = [ordered]@{
                    code = 'service_error'
                    message = 'mock service failed'
                }
            })
        }
        'exit_now' {
            exit 0
        }
        default {
            Write-JsonLine ([ordered]@{
                id = $request.id
                error = [ordered]@{
                    code = 'method_not_found'
                    message = 'unknown method'
                }
            })
        }
    }
}
"#;

const MOCK_WORKER_SCRIPT: &str = r#"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding = [System.Text.Encoding]::UTF8

function Write-JsonLine($value) {
    $json = $value | ConvertTo-Json -Compress -Depth 16
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

$workerKind = if ([string]::IsNullOrWhiteSpace($env:MOCK_WORKER_KIND)) { 'longdoc' } else { $env:MOCK_WORKER_KIND }
$protocolVersion = if ([string]::IsNullOrWhiteSpace($env:MOCK_WORKER_PROTOCOL_VERSION)) { 1 } else { [int]$env:MOCK_WORKER_PROTOCOL_VERSION }
$capabilities = if (-not [string]::IsNullOrWhiteSpace($env:MOCK_WORKER_CAPABILITIES)) {
    @($env:MOCK_WORKER_CAPABILITIES -split ',' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    if ($workerKind -eq 'localai') {
        @('configure', 'translate_stream', 'grammar_stream', 'cancel', 'shutdown')
    }
    else {
        @('configure', 'translate_document', 'cancel', 'shutdown')
    }
}

Write-JsonLine ([ordered]@{
    event = 'ready'
    data = [ordered]@{
        workerKind = $workerKind
        workerVersion = '9.9.9'
        protocolVersion = $protocolVersion
        capabilities = $capabilities
    }
})

while (($line = [Console]::In.ReadLine()) -ne $null) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    $request = $line | ConvertFrom-Json
    switch ($request.method) {
        'configure' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ ok = $true }
            })
        }
        'translate_document' {
            $outputPath = [string]$request.params.outputPath
            Write-JsonLine ([ordered]@{
                event = 'status'
                id = $request.id
                data = [ordered]@{ message = 'direct worker longdoc started' }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    state = 'Completed'
                    outputPath = $outputPath
                    bilingualOutputPath = $null
                    totalChunks = 1
                    succeededChunks = 1
                    failedChunkIndexes = @()
                    qualityReport = $null
                }
            })
        }
        'translate_stream' {
            $text = [string]$request.params.text
            Write-JsonLine ([ordered]@{
                event = 'chunk'
                id = $request.id
                data = [ordered]@{ text = 'direct ' }
            })
            Write-JsonLine ([ordered]@{
                event = 'chunk'
                id = $request.id
                data = [ordered]@{ text = "worker $text" }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    done = $true
                    fullText = "direct worker $text"
                }
            })
        }
        'grammar_stream' {
            Write-JsonLine ([ordered]@{
                event = 'chunk'
                id = $request.id
                data = [ordered]@{ text = '[CORRECTED]Direct worker.[/CORRECTED]' }
            })
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{
                    done = $true
                    fullText = '[CORRECTED]Direct worker.[/CORRECTED]'
                }
            })
        }
        'cancel' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ cancelled = $true }
            })
        }
        'shutdown' {
            Write-JsonLine ([ordered]@{
                id = $request.id
                result = [ordered]@{ ok = $true }
            })
            break
        }
        default {
            Write-JsonLine ([ordered]@{
                id = $request.id
                error = [ordered]@{
                    code = 'method_not_found'
                    message = "unknown direct worker method: $($request.method)"
                }
            })
        }
    }
}
"#;

#[test]
fn default_worker_paths_match_packaging_contract() {
    let app_dir = Path::new(r"C:\Program Files\Easydict");

    assert_eq!(
        default_longdoc_worker_path(app_dir),
        app_dir
            .join("workers")
            .join("longdoc")
            .join("Easydict.Workers.LongDoc.exe")
    );
    assert_eq!(
        default_local_ai_worker_path(app_dir),
        app_dir
            .join("workers")
            .join("localai")
            .join("Easydict.Workers.LocalAi.exe")
    );
}

#[test]
fn packaged_worker_command_sets_shared_worker_environment() {
    let app_dir = Path::new(r"C:\Program Files\Easydict");
    let command = packaged_worker_command(app_dir, "longdoc", "Easydict.Workers.LongDoc.exe");

    assert_eq!(command.program(), default_longdoc_worker_path(app_dir));
    assert_eq!(
        command
            .envs()
            .iter()
            .find(|(key, _)| key == "EASYDICT_WORKER_SHARED_DIR")
            .map(|(_, value)| value.as_str()),
        Some(r"C:\Program Files\Easydict\workers\shared")
    );
    assert_eq!(
        command
            .envs()
            .iter()
            .find(|(key, _)| key == "DOTNET_CLI_TELEMETRY_OPTOUT")
            .map(|(_, value)| value.as_str()),
        Some("1")
    );
}

#[test]
fn packaged_worker_command_spawn_requires_hybrid_runtime_profile_before_io_probe() {
    let _environment_guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _runtime_profile = EnvVarGuard::remove(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE);
    let _generic_runtime_profile =
        EnvVarGuard::remove(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE);
    let app_dir = Path::new(r"C:\EasydictMissingPortable");

    for (worker_subdir, worker_exe_name, expected_prefix) in [
        (
            "longdoc",
            "Easydict.Workers.LongDoc.exe",
            "Long Document translation",
        ),
        (
            "localai",
            "Easydict.Workers.LocalAi.exe",
            "Windows Local AI",
        ),
    ] {
        let error = match packaged_worker_command(app_dir, worker_subdir, worker_exe_name).spawn() {
            Ok(_) => panic!("packaged retained worker must require explicit hybrid runtime"),
            Err(error) => error,
        };

        retained_worker_disabled_error(error, expected_prefix);
    }
}

#[test]
fn packaged_worker_command_spawn_respects_rust_only_runtime_profile_before_io_probe() {
    let _environment_guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _runtime_profile = EnvVarGuard::set(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only");
    let _generic_runtime_profile =
        EnvVarGuard::remove(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE);
    let app_dir = Path::new(r"C:\EasydictMissingPortable");
    let error =
        match packaged_worker_command(app_dir, "longdoc", "Easydict.Workers.LongDoc.exe").spawn() {
            Ok(_) => panic!("rust-only runtime profile must disable packaged retained workers"),
            Err(error) => error,
        };

    retained_worker_disabled_error(error, "Long Document translation");
}

#[test]
fn direct_packaged_worker_facade_requires_hybrid_runtime_profile_before_io_probe() {
    let _environment_guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _runtime_profile = EnvVarGuard::remove(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE);
    let _generic_runtime_profile =
        EnvVarGuard::remove(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE);
    let app_dir = Path::new(r"C:\EasydictMissingPortable");
    let longdoc_error = match DirectWorkerFacade::spawn_packaged_longdoc(app_dir) {
        Ok(_) => panic!("packaged LongDoc facade must require explicit hybrid runtime"),
        Err(error) => error,
    };
    let local_ai_error = match DirectWorkerFacade::spawn_packaged_local_ai(app_dir) {
        Ok(_) => panic!("packaged LocalAI facade must require explicit hybrid runtime"),
        Err(error) => error,
    };

    retained_worker_disabled_error(longdoc_error, "Long Document translation");
    retained_worker_disabled_error(local_ai_error, "Windows Local AI");
}

#[test]
fn packaged_worker_command_allows_hybrid_runtime_profile_to_reach_io_probe() {
    let _environment_guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _runtime_profile = EnvVarGuard::set(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");
    let _generic_runtime_profile =
        EnvVarGuard::remove(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE);
    let app_dir = Path::new(r"C:\EasydictMissingPortable");
    let error =
        match packaged_worker_command(app_dir, "longdoc", "Easydict.Workers.LongDoc.exe").spawn() {
            Ok(_) => panic!("missing worker executable should still fail at the I/O boundary"),
            Err(error) => error,
        };

    assert!(
        error.is_not_found(),
        "hybrid retained worker path should proceed to executable probing, got {error:?}"
    );
}

#[test]
fn packaged_local_ai_worker_command_uses_custom_openvino_cache_base() {
    let _openvino_ep_guard = EnvVarGuard::set(OPENVINO_EP_ENABLE_ENVIRONMENT_VARIABLE, "1");
    let app_dir = Path::new(r"C:\Program Files\Easydict");
    let cache_base = Path::new(r"D:\EasydictCache");
    let command = packaged_worker_command_with_openvino_cache_base(
        app_dir,
        "localai",
        "Easydict.Workers.LocalAi.exe",
        Some(cache_base),
    );
    let runtime_dir = NllbModelPaths::from_cache_base(cache_base)
        .runtime_dir
        .to_string_lossy()
        .to_string();

    assert_eq!(command.program(), default_local_ai_worker_path(app_dir));
    assert_eq!(
        command
            .envs()
            .iter()
            .find(|(key, _)| key == "EASYDICT_OPENVINO_RUNTIME_DIR")
            .map(|(_, value)| value.as_str()),
        Some(runtime_dir.as_str())
    );
    assert!(
        command
            .envs()
            .iter()
            .find(|(key, _)| key == "PATH")
            .map(|(_, value)| {
                value
                    .to_ascii_lowercase()
                    .starts_with(&runtime_dir.to_ascii_lowercase())
            })
            .unwrap_or(false),
        "LocalAI worker PATH should begin with the configured OpenVINO runtime directory"
    );
}

#[test]
fn direct_worker_rejects_protocol_mismatch_before_configure() {
    let error = match DirectWorkerFacade::spawn_worker(
        mock_worker_command(worker_kinds::LONGDOC, WORKER_PROTOCOL_VERSION_CURRENT + 1),
        worker_kinds::LONGDOC,
    ) {
        Ok(_) => panic!("protocol mismatch should fail the ready handshake"),
        Err(error) => error,
    };

    match error {
        WorkerClientError::Protocol(message) => {
            assert!(message.contains("protocol version"));
        }
        other => panic!("expected protocol error, got {other:?}"),
    }
}

#[test]
fn direct_longdoc_worker_rejects_missing_required_capability_before_configure() {
    let error = match DirectWorkerFacade::spawn_worker(
        mock_worker_command_with_capabilities(
            worker_kinds::LONGDOC,
            WORKER_PROTOCOL_VERSION_CURRENT,
            &[worker_methods::CONFIGURE, worker_methods::CANCEL],
        ),
        worker_kinds::LONGDOC,
    ) {
        Ok(_) => panic!("missing longdoc capability should fail the ready handshake"),
        Err(error) => error,
    };

    match error {
        WorkerClientError::Protocol(message) => {
            assert!(message.contains("missing required capability"));
            assert!(message.contains(worker_methods::LONGDOC_TRANSLATE_DOCUMENT));
        }
        other => panic!("expected protocol error, got {other:?}"),
    }
}

#[test]
fn direct_longdoc_worker_rejects_missing_lifecycle_capability_before_configure() {
    let error = match DirectWorkerFacade::spawn_worker(
        mock_worker_command_with_capabilities(
            worker_kinds::LONGDOC,
            WORKER_PROTOCOL_VERSION_CURRENT,
            &[
                worker_methods::CONFIGURE,
                worker_methods::LONGDOC_TRANSLATE_DOCUMENT,
                worker_methods::SHUTDOWN,
            ],
        ),
        worker_kinds::LONGDOC,
    ) {
        Ok(_) => panic!("missing longdoc cancel capability should fail the ready handshake"),
        Err(error) => error,
    };

    match error {
        WorkerClientError::Protocol(message) => {
            assert!(message.contains("missing required capability"));
            assert!(message.contains(worker_methods::CANCEL));
        }
        other => panic!("expected protocol error, got {other:?}"),
    }
}

#[test]
fn direct_local_ai_worker_rejects_missing_translate_stream_capability_before_configure() {
    let error = match DirectWorkerFacade::spawn_worker(
        mock_worker_command_with_capabilities(
            worker_kinds::LOCAL_AI,
            WORKER_PROTOCOL_VERSION_CURRENT,
            &[
                worker_methods::CONFIGURE,
                worker_methods::LOCAL_AI_GRAMMAR_STREAM,
                worker_methods::CANCEL,
                worker_methods::SHUTDOWN,
            ],
        ),
        worker_kinds::LOCAL_AI,
    ) {
        Ok(_) => panic!("missing local AI translate capability should fail the ready handshake"),
        Err(error) => error,
    };

    match error {
        WorkerClientError::Protocol(message) => {
            assert!(message.contains("missing required capability"));
            assert!(message.contains(worker_methods::LOCAL_AI_TRANSLATE_STREAM));
        }
        other => panic!("expected protocol error, got {other:?}"),
    }
}

#[test]
fn direct_local_ai_worker_rejects_missing_grammar_stream_capability_before_configure() {
    let error = match DirectWorkerFacade::spawn_worker(
        mock_worker_command_with_capabilities(
            worker_kinds::LOCAL_AI,
            WORKER_PROTOCOL_VERSION_CURRENT,
            &[
                worker_methods::CONFIGURE,
                worker_methods::LOCAL_AI_TRANSLATE_STREAM,
            ],
        ),
        worker_kinds::LOCAL_AI,
    ) {
        Ok(_) => panic!("missing local AI grammar capability should fail the ready handshake"),
        Err(error) => error,
    };

    match error {
        WorkerClientError::Protocol(message) => {
            assert!(message.contains("missing required capability"));
            assert!(message.contains(worker_methods::LOCAL_AI_GRAMMAR_STREAM));
        }
        other => panic!("expected protocol error, got {other:?}"),
    }
}

#[test]
fn direct_local_ai_worker_rejects_missing_lifecycle_capability_before_configure() {
    let error = match DirectWorkerFacade::spawn_worker(
        mock_worker_command_with_capabilities(
            worker_kinds::LOCAL_AI,
            WORKER_PROTOCOL_VERSION_CURRENT,
            &[
                worker_methods::CONFIGURE,
                worker_methods::LOCAL_AI_TRANSLATE_STREAM,
                worker_methods::LOCAL_AI_GRAMMAR_STREAM,
                worker_methods::CANCEL,
            ],
        ),
        worker_kinds::LOCAL_AI,
    ) {
        Ok(_) => panic!("missing local AI shutdown capability should fail the ready handshake"),
        Err(error) => error,
    };

    match error {
        WorkerClientError::Protocol(message) => {
            assert!(message.contains("missing required capability"));
            assert!(message.contains(worker_methods::SHUTDOWN));
        }
        other => panic!("expected protocol error, got {other:?}"),
    }
}

#[test]
fn direct_worker_allows_extra_ready_capabilities() {
    let mut facade = DirectWorkerFacade::spawn_worker(
        mock_worker_command_with_capabilities(
            worker_kinds::LONGDOC,
            WORKER_PROTOCOL_VERSION_CURRENT,
            &[
                worker_methods::CONFIGURE,
                worker_methods::LONGDOC_TRANSLATE_DOCUMENT,
                worker_methods::CANCEL,
                worker_methods::SHUTDOWN,
                "diagnostics",
            ],
        ),
        worker_kinds::LONGDOC,
    )
    .expect("extra worker capabilities should be accepted");

    let result = facade
        .configure(&ConfigureParams {
            settings: SettingsSnapshot::default(),
        })
        .expect("worker with extra capabilities should still configure");
    assert!(result.ok);
}

#[test]
fn direct_worker_facade_sends_cancel_request() {
    let mut facade = mock_worker(worker_kinds::LONGDOC);

    let result = facade
        .cancel_request("rust-worker-99")
        .expect("direct worker cancel should round-trip");

    assert!(result.cancelled);
}

#[test]
fn direct_worker_facade_sends_shutdown_without_params() {
    let mut facade = mock_worker(worker_kinds::LOCAL_AI);

    let result = facade
        .shutdown()
        .expect("direct worker shutdown should round-trip");

    assert!(result.ok);
}

#[test]
fn worker_client_reports_request_id_for_plain_requests() {
    let mut client = mock_worker_command(worker_kinds::LONGDOC, WORKER_PROTOCOL_VERSION_CURRENT)
        .spawn()
        .expect("mock worker client should spawn");
    client
        .wait_for_worker_ready(worker_kinds::LONGDOC)
        .expect("mock worker should emit ready");

    let mut observed_id = None;
    let result = client
        .send_request_with_request_id::<_, easydict_app::compat_protocol::ConfigureResult, _>(
            worker_methods::CONFIGURE,
            &ConfigureParams {
                settings: SettingsSnapshot::default(),
            },
            |id| observed_id = Some(id.to_string()),
        )
        .expect("configure should round-trip");

    assert!(result.ok);
    assert_eq!(observed_id.as_deref(), Some("rust-worker-1"));
}

#[test]
fn worker_client_reports_request_id_for_observed_event_requests() {
    let mut client = mock_worker_command(worker_kinds::LOCAL_AI, WORKER_PROTOCOL_VERSION_CURRENT)
        .spawn()
        .expect("mock worker client should spawn");
    client
        .wait_for_worker_ready(worker_kinds::LOCAL_AI)
        .expect("mock worker should emit ready");

    let mut observed_id = None;
    let mut chunk_text = Vec::new();
    let result = client
        .send_request_observing_events_with_request_id::<_, easydict_app::compat_protocol::TranslateStreamResult, _, _>(
            worker_methods::LOCAL_AI_TRANSLATE_STREAM,
            &LocalAiTranslateParams {
                text: "Hello".to_string(),
                from_language: "English".to_string(),
                to_language: "SimplifiedChinese".to_string(),
                provider_mode: "OpenVINO".to_string(),
                custom_prompt: None,
                include_explanations: None,
            },
            |id| observed_id = Some(id.to_string()),
            |event| {
                if event.event != easydict_app::compat_protocol::worker_events::LOCAL_AI_CHUNK {
                    return;
                }

                let Some(data) = event.data.clone() else {
                    return;
                };
                if let Ok(chunk) = serde_json::from_value::<easydict_app::compat_protocol::ChunkEventData>(data) {
                    chunk_text.push(chunk.text);
                }
            },
        )
        .expect("local AI stream should round-trip");

    assert!(result.done);
    assert_eq!(observed_id.as_deref(), Some("rust-worker-1"));
    assert_eq!(
        chunk_text,
        vec!["direct ".to_string(), "worker Hello".to_string()]
    );
}

#[test]
fn direct_longdoc_worker_facade_waits_ready_and_uses_worker_method() {
    let mut facade = mock_worker(worker_kinds::LONGDOC);

    let configure = facade
        .configure(&ConfigureParams {
            settings: SettingsSnapshot {
                long_doc_max_concurrency: Some(4),
                ..SettingsSnapshot::default()
            },
        })
        .expect("direct worker configure should succeed");
    assert!(configure.ok);

    let result = facade
        .longdoc_translate(&TranslateDocumentParams {
            input_path: r"C:\Temp\source.md".to_string(),
            output_path: Some(r"C:\Temp\translated.md".to_string()),
            input_mode: "Markdown".to_string(),
            from: "English".to_string(),
            to: "SimplifiedChinese".to_string(),
            service_id: "openai".to_string(),
            output_mode: "Monolingual".to_string(),
            pdf_export_mode: None,
            layout_detection: Some("Heuristic".to_string()),
            page_range: None,
            vision_endpoint: None,
            vision_api_key: None,
            vision_model: None,
            result_json_path: None,
        })
        .expect("direct worker longdoc should succeed");

    assert_eq!(result.state, "Completed");
    assert_eq!(result.total_chunks, 1);
    assert_eq!(
        result.output_path.as_deref(),
        Some(r"C:\Temp\translated.md")
    );

    let events = facade.take_events();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].event, "status");
    let status: StatusEventData =
        serde_json::from_value(events[0].data.clone().expect("status data"))
            .expect("status data parses");
    assert_eq!(status.message, "direct worker longdoc started");
}

#[test]
fn direct_local_ai_worker_facade_observes_worker_chunks() {
    let mut facade = mock_worker(worker_kinds::LOCAL_AI);

    let configure = facade
        .configure(&ConfigureParams {
            settings: SettingsSnapshot::default(),
        })
        .expect("direct local AI worker configure should succeed");
    assert!(configure.ok);

    let mut chunks = Vec::new();
    let result = facade
        .local_ai_translate_stream_observing_chunks(
            &LocalAiTranslateParams {
                text: "Hello".to_string(),
                from_language: "English".to_string(),
                to_language: "SimplifiedChinese".to_string(),
                provider_mode: "OpenVINO".to_string(),
                custom_prompt: None,
                include_explanations: None,
            },
            |chunk| chunks.push(chunk.text),
        )
        .expect("direct local AI stream should succeed");

    assert!(result.done);
    assert_eq!(result.full_text.as_deref(), Some("direct worker Hello"));
    assert_eq!(
        chunks,
        vec!["direct ".to_string(), "worker Hello".to_string()]
    );
    assert!(facade.take_events().is_empty());
}

#[test]
fn events_before_response_are_queued_for_callers() {
    let mut client = mock_jsonl_client();

    let result: TranslationResultDto = client
        .send_request(
            "emit_event_then_translate",
            &TranslateParams {
                text: "Streaming".to_string(),
                from: None,
                to: Some("zh-Hans".to_string()),
                services: None,
                custom_prompt: None,
            },
        )
        .expect("translate should succeed after event");

    assert_eq!(result.translated_text, "mock:Streaming");

    let events = client.take_events();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].event, "chunk");
    assert!(events[0]
        .id
        .as_deref()
        .is_some_and(|id| id.starts_with("rust-worker-")));
    assert_eq!(
        events[0].data.as_ref().and_then(|data| data.get("text")),
        Some(&Value::String("mock:".to_string()))
    );
    assert!(client.take_events().is_empty());
}

#[test]
fn remote_errors_preserve_protocol_code_and_message() {
    let mut client = mock_jsonl_client();

    let error = client
        .send_request::<_, TranslationResultDto>(
            "fail_remote",
            &TranslateParams {
                text: "Hello".to_string(),
                from: None,
                to: None,
                services: None,
                custom_prompt: None,
            },
        )
        .expect_err("remote failure should surface");

    match error {
        WorkerClientError::Remote(remote) => {
            assert_eq!(remote.code, ipc_error_codes::SERVICE_ERROR);
            assert_eq!(remote.message, "mock service failed");
        }
        other => panic!("expected remote error, got {other:?}"),
    }
}

#[test]
fn process_exit_before_response_is_reported() {
    let mut client = mock_jsonl_client();

    let error = client
        .send_request::<_, TranslationResultDto>(
            "exit_now",
            &TranslateParams {
                text: "Hello".to_string(),
                from: None,
                to: None,
                services: None,
                custom_prompt: None,
            },
        )
        .expect_err("process exit should surface");

    assert!(matches!(error, WorkerClientError::ProcessExited));
}

#[test]
fn missing_worker_path_is_classified_for_fallback() {
    let error = match WorkerCommand::new("__definitely_missing_easydict_worker__.exe").spawn() {
        Ok(_) => panic!("missing worker should fail"),
        Err(error) => error,
    };

    assert!(error.is_not_found());
}

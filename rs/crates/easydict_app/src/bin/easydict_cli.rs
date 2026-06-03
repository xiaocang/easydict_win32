use easydict_app::cli_translate::{
    parse_args, usage, CliMode, CliOptions, CliParseError, WorkerTarget,
};
use easydict_app::compat_client::{
    default_local_ai_worker_path, DirectWorkerFacade, WorkerClientError,
};
use easydict_app::compat_protocol::{
    GrammarCorrectParams, GrammarCorrectResultDto, SettingsSnapshot, TranslateParams,
    TranslationResultDto,
};
use easydict_app::quick_translate_request_can_route_natively;
use easydict_app::{
    auto_foundry_local_native_probe_request, default_settings_storage_path,
    find_translation_service_descriptor, load_settings_file, local_ai_quick_translate_local_error,
    run_quick_translate_service, run_quick_translate_service_with_native_route, settings_snapshot,
    CommandFoundryLocalEndpointResolver, LocalAiWorkerQuickTranslateBackend, QuickQueryMode,
    QuickTranslateBackendError, QuickTranslateExecutionKind, QuickTranslateService,
    QuickTranslateServiceRequest, QuickTranslateServiceUpdate, RetainedWorkerPolicy,
    SettingsStorageError,
};
use serde_json::json;
use std::env;
use std::fmt;
use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::process::ExitCode;

fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(CliError::Parse(CliParseError::Help)) => {
            println!("{}", usage());
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("error: {error}");
            eprintln!();
            eprintln!("{}", usage());
            ExitCode::from(2)
        }
    }
}

fn run() -> Result<(), CliError> {
    let options = parse_args(env::args().skip(1)).map_err(CliError::Parse)?;
    let text = resolve_text(&options.text)?;
    let stdout = io::stdout();
    let stderr = io::stderr();
    let mut stdout = stdout.lock();
    let mut stderr = stderr.lock();

    match options.mode {
        CliMode::Translate => {
            let result = match try_run_native_service_update(
                &options,
                text.clone(),
                QuickTranslateExecutionKind::Translate,
            )? {
                Some(update) => translation_result_from_update(update)?,
                None => return Err(unsupported_rust_route_error(&options)),
            };
            write_translation_result(&mut stdout, &mut stderr, &options, &result)?;
        }
        CliMode::Stream => {
            let result = run_stream_translation(&options, text, &mut stdout, &mut stderr)?;
            if options.verbose && !options.json {
                write_translation_metadata(&mut stderr, &result)?;
            }
        }
        CliMode::Grammar => {
            let result = match try_run_native_service_update(
                &options,
                text.clone(),
                QuickTranslateExecutionKind::GrammarCorrection,
            )? {
                Some(update) => grammar_result_from_update(&options, update)?,
                None => return Err(unsupported_rust_route_error(&options)),
            };
            write_grammar_result(&mut stdout, &mut stderr, &options, &result)?;
        }
        CliMode::Batch => {
            run_batch_translation(&options, text, &mut stdout, &mut stderr)?;
        }
    }

    Ok(())
}

fn run_batch_translation(
    options: &CliOptions,
    text: String,
    stdout: &mut impl Write,
    stderr: &mut impl Write,
) -> Result<(), CliError> {
    for (index, line) in batch_lines(&text).into_iter().enumerate() {
        let result = match try_run_native_service_update(
            options,
            line.clone(),
            QuickTranslateExecutionKind::Translate,
        )? {
            Some(update) => translation_result_from_update(update)?,
            None => return Err(unsupported_rust_route_error(options)),
        };
        write_batch_translation_result(stdout, stderr, options, index + 1, &line, &result)?;
    }

    Ok(())
}

fn run_stream_translation(
    options: &CliOptions,
    text: String,
    stdout: &mut impl Write,
    stderr: &mut impl Write,
) -> Result<TranslationResultDto, CliError> {
    if let Some(update) = try_run_native_service_update(
        options,
        text.clone(),
        QuickTranslateExecutionKind::TranslateStream,
    )? {
        return write_native_stream_update(update, stdout, stderr, options);
    }

    Err(unsupported_rust_route_error(options))
}

fn try_run_native_service_update(
    options: &CliOptions,
    text: String,
    execution_kind: QuickTranslateExecutionKind,
) -> Result<Option<QuickTranslateServiceUpdate>, CliError> {
    let Some(request) = native_service_request(options, text, execution_kind)? else {
        return Ok(None);
    };

    if let Some(error) =
        local_ai_quick_translate_local_error(&request, RetainedWorkerPolicy::from_environment())
    {
        return Err(CliError::UnsupportedRustRoute(error.to_string()));
    }

    if quick_translate_request_can_route_natively(&request) {
        return Ok(run_quick_translate_service_with_native_route(request));
    }

    let mut foundry_resolver = CommandFoundryLocalEndpointResolver::default();
    if let Some(native_request) =
        auto_foundry_local_native_probe_request(&request, &mut foundry_resolver)
    {
        return Ok(run_quick_translate_service_with_native_route(
            native_request,
        ));
    }

    if request.service.id == "windows-local-ai" {
        let facade = spawn_local_ai_worker(&options.host)?;
        let mut backend = LocalAiWorkerQuickTranslateBackend::new(facade);
        return Ok(Some(run_quick_translate_service(&mut backend, &request)));
    }

    Ok(None)
}

fn unsupported_rust_route_error(options: &CliOptions) -> CliError {
    let services = if options.services.is_empty() {
        "google".to_string()
    } else {
        options.services.join(",")
    };

    CliError::UnsupportedRustRoute(format!(
        "No Rust-native quick translate route is available for service(s): {services}"
    ))
}

fn native_service_request(
    options: &CliOptions,
    text: String,
    execution_kind: QuickTranslateExecutionKind,
) -> Result<Option<QuickTranslateServiceRequest>, CliError> {
    let Some(service_id) = native_cli_service_id(options, execution_kind) else {
        return Ok(None);
    };
    let Some(descriptor) = find_translation_service_descriptor(service_id) else {
        return Ok(None);
    };

    let service = QuickTranslateService {
        id: descriptor.service_id.to_string(),
        name: descriptor.display_name.to_string(),
        enabled_query: true,
        grammar_capable: descriptor.grammar_capable,
        streaming_capable: descriptor.streaming_capable,
    };
    let settings = cli_settings_snapshot()?;
    let params = TranslateParams {
        text: text.clone(),
        from: options.from.clone(),
        to: options.to.clone(),
        services: Some(vec![service.id.clone()]),
        custom_prompt: None,
    };
    let selected_service_id = service.id.clone();

    Ok(Some(QuickTranslateServiceRequest {
        query_id: 0,
        service,
        query_mode: if execution_kind == QuickTranslateExecutionKind::GrammarCorrection {
            QuickQueryMode::GrammarCorrection
        } else {
            QuickQueryMode::Translation
        },
        execution_kind,
        params,
        grammar_params: (execution_kind == QuickTranslateExecutionKind::GrammarCorrection).then(
            || GrammarCorrectParams {
                text,
                language: options.language.clone().or_else(|| options.from.clone()),
                services: Some(vec![selected_service_id]),
                include_explanations: true,
            },
        ),
        settings,
    }))
}

fn native_cli_service_id(
    options: &CliOptions,
    execution_kind: QuickTranslateExecutionKind,
) -> Option<&str> {
    if execution_kind == QuickTranslateExecutionKind::GrammarCorrection {
        return options
            .services
            .iter()
            .map(String::as_str)
            .find(|service_id| {
                find_translation_service_descriptor(service_id)
                    .is_some_and(|descriptor| descriptor.grammar_capable)
            });
    }

    options
        .services
        .first()
        .map(String::as_str)
        .or(Some("google"))
}

fn cli_settings_snapshot() -> Result<SettingsSnapshot, CliError> {
    let path = default_settings_storage_path();
    match load_settings_file(&path) {
        Ok(result) => Ok(settings_snapshot(&result.settings)),
        Err(SettingsStorageError::Io(error)) if error.kind() == io::ErrorKind::NotFound => {
            Ok(SettingsSnapshot::default())
        }
        Err(error) => Err(CliError::Settings(error)),
    }
}

fn translation_result_from_update(
    update: QuickTranslateServiceUpdate,
) -> Result<TranslationResultDto, CliError> {
    update.outcome.result.map_err(CliError::QuickTranslate)
}

fn grammar_result_from_update(
    options: &CliOptions,
    update: QuickTranslateServiceUpdate,
) -> Result<GrammarCorrectResultDto, CliError> {
    let service = update.outcome.service;
    let result = update.outcome.result.map_err(CliError::QuickTranslate)?;
    let preview = update.outcome.grammar_result;
    let corrected_text = preview
        .as_ref()
        .map(|preview| preview.corrected_text.clone())
        .unwrap_or_else(|| result.translated_text.clone());

    Ok(GrammarCorrectResultDto {
        original_text: preview
            .as_ref()
            .map(|preview| preview.original_text.clone())
            .unwrap_or_else(|| options.text.clone()),
        corrected_text,
        explanation: preview
            .as_ref()
            .and_then(|preview| preview.explanation.clone()),
        raw_text: Some(result.translated_text),
        service_id: result.service_id.or(Some(service.id)),
        service_name: result.service_name.or(Some(service.name)),
        language: options.language.clone().or_else(|| options.from.clone()),
        timing_ms: result.timing_ms,
        has_corrections: preview
            .as_ref()
            .is_some_and(|preview| preview.has_corrections),
    })
}

fn write_native_stream_update(
    update: QuickTranslateServiceUpdate,
    stdout: &mut impl Write,
    stderr: &mut impl Write,
    options: &CliOptions,
) -> Result<TranslationResultDto, CliError> {
    let streamed_chunks = update.outcome.streamed_chunks;
    let result = update.outcome.result.map_err(CliError::QuickTranslate)?;
    let chunks = if streamed_chunks.is_empty() && !result.translated_text.is_empty() {
        vec![result.translated_text.clone()]
    } else {
        streamed_chunks
    };

    for chunk in chunks {
        if options.json {
            writeln!(
                stdout,
                "{}",
                json!({
                    "event": "chunk",
                    "text": chunk,
                })
            )?;
        } else {
            write!(stdout, "{chunk}")?;
        }
        stdout.flush()?;
    }

    if options.json {
        writeln!(
            stdout,
            "{}",
            json!({
                "event": "done",
                "result": result,
            })
        )?;
    } else {
        writeln!(stdout)?;
        if options.verbose {
            write_translation_metadata(stderr, &result)?;
        }
    }

    Ok(result)
}

fn write_translation_result(
    stdout: &mut impl Write,
    stderr: &mut impl Write,
    options: &CliOptions,
    result: &TranslationResultDto,
) -> Result<(), CliError> {
    if options.json {
        writeln!(stdout, "{}", serde_json::to_string(result)?)?;
    } else {
        writeln!(stdout, "{}", result.translated_text)?;
        if options.verbose {
            write_translation_metadata(stderr, result)?;
        }
    }

    Ok(())
}

fn write_grammar_result(
    stdout: &mut impl Write,
    stderr: &mut impl Write,
    options: &CliOptions,
    result: &GrammarCorrectResultDto,
) -> Result<(), CliError> {
    if options.json {
        writeln!(stdout, "{}", serde_json::to_string(result)?)?;
    } else {
        writeln!(stdout, "{}", result.corrected_text)?;
        if let Some(explanation) = result.explanation.as_deref().map(str::trim) {
            if !explanation.is_empty() {
                writeln!(stdout)?;
                writeln!(stdout, "{explanation}")?;
            }
        }
        if options.verbose {
            let service = result
                .service_name
                .as_deref()
                .or(result.service_id.as_deref())
                .unwrap_or("unknown service");
            let timing = result
                .timing_ms
                .map(|value| format!("{value}ms"))
                .unwrap_or_else(|| "unknown timing".to_string());
            writeln!(stderr, "[{service} {timing}]")?;
        }
    }

    Ok(())
}

fn write_batch_translation_result(
    stdout: &mut impl Write,
    stderr: &mut impl Write,
    options: &CliOptions,
    index: usize,
    text: &str,
    result: &TranslationResultDto,
) -> Result<(), CliError> {
    if options.json {
        writeln!(
            stdout,
            "{}",
            json!({
                "event": "result",
                "index": index,
                "text": text,
                "result": result,
            })
        )?;
    } else {
        writeln!(stdout, "{}", escape_line(&result.translated_text))?;
        if options.verbose {
            write!(stderr, "[{index} ")?;
            write_translation_metadata_without_brackets(stderr, result)?;
            writeln!(stderr, "]")?;
        }
    }

    Ok(())
}

fn write_translation_metadata(
    stderr: &mut impl Write,
    result: &TranslationResultDto,
) -> Result<(), CliError> {
    write!(stderr, "[")?;
    write_translation_metadata_without_brackets(stderr, result)?;
    writeln!(stderr, "]")?;
    Ok(())
}

fn write_translation_metadata_without_brackets(
    stderr: &mut impl Write,
    result: &TranslationResultDto,
) -> Result<(), CliError> {
    let service = result
        .service_name
        .as_deref()
        .or(result.service_id.as_deref())
        .unwrap_or("unknown service");
    let timing = result
        .timing_ms
        .map(|value| format!("{value}ms"))
        .unwrap_or_else(|| "unknown timing".to_string());

    write!(stderr, "{service} {timing}")?;
    Ok(())
}

fn resolve_text(value: &str) -> Result<String, CliError> {
    if value != "-" {
        return Ok(value.to_string());
    }

    let mut text = String::new();
    io::stdin().read_to_string(&mut text)?;
    Ok(text.trim_end_matches(['\r', '\n']).to_string())
}

fn batch_lines(text: &str) -> Vec<String> {
    text.lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .map(str::to_string)
        .collect()
}

fn escape_line(text: &str) -> String {
    text.replace('\r', "\\r").replace('\n', "\\n")
}

fn spawn_local_ai_worker(target: &WorkerTarget) -> Result<DirectWorkerFacade, CliError> {
    let app_dir = resolve_local_ai_worker_app_dir(target)?;
    DirectWorkerFacade::spawn_packaged_local_ai(app_dir).map_err(CliError::LocalAiWorker)
}

fn resolve_local_ai_worker_app_dir(target: &WorkerTarget) -> Result<PathBuf, CliError> {
    match target {
        WorkerTarget::AppDir(app_dir) => {
            let worker = default_local_ai_worker_path(app_dir);
            if !worker.exists() {
                return Err(CliError::WorkerNotFound(worker));
            }
            Ok(app_dir.clone())
        }
        WorkerTarget::Auto | WorkerTarget::Program { .. } => {
            Err(CliError::WorkerRequiresExplicitAppDir)
        }
    }
}

#[derive(Debug)]
enum CliError {
    Parse(CliParseError),
    QuickTranslate(QuickTranslateBackendError),
    Settings(SettingsStorageError),
    Io(io::Error),
    Json(serde_json::Error),
    WorkerNotFound(PathBuf),
    WorkerRequiresExplicitAppDir,
    LocalAiWorker(WorkerClientError),
    UnsupportedRustRoute(String),
}

impl fmt::Display for CliError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Parse(error) => write!(formatter, "{error}"),
            Self::QuickTranslate(error) => write!(formatter, "{error}"),
            Self::Settings(error) => write!(formatter, "{error}"),
            Self::Io(error) => write!(formatter, "{error}"),
            Self::Json(error) => write!(formatter, "{error}"),
            Self::LocalAiWorker(error) => {
                write!(formatter, "{}", error.process_message("Local AI worker"))
            }
            Self::WorkerNotFound(path) => {
                write!(
                    formatter,
                    "Local AI worker executable not found: {}",
                    path.display()
                )
            }
            Self::WorkerRequiresExplicitAppDir => formatter.write_str(
                "Retained Local AI worker fallback requires explicit --app-dir; automatic worker discovery and --host hints are disabled",
            ),
            Self::UnsupportedRustRoute(message) => formatter.write_str(message),
        }
    }
}

impl std::error::Error for CliError {}

impl From<QuickTranslateBackendError> for CliError {
    fn from(error: QuickTranslateBackendError) -> Self {
        Self::QuickTranslate(error)
    }
}

impl From<SettingsStorageError> for CliError {
    fn from(error: SettingsStorageError) -> Self {
        Self::Settings(error)
    }
}

impl From<io::Error> for CliError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<serde_json::Error> for CliError {
    fn from(error: serde_json::Error) -> Self {
        Self::Json(error)
    }
}

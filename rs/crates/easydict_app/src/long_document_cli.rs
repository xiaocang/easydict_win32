use crate::long_document::{
    build_long_document_request, long_document_supported_service_descriptors,
    run_long_document_request_with_current_app_dir,
    run_long_document_request_with_packaged_app_dir, LongDocumentEvent, LongDocumentOutcome,
};
use crate::settings_storage::{
    default_settings_storage_path, load_settings_file, SettingsStorageError,
};
use crate::state::{EasydictUiState, ServiceProviderSetting, SettingsState};
use clap::Parser;
use serde_json::json;
use std::env;
use std::ffi::OsString;
use std::fmt;
use std::fs;
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::process::ExitCode;

#[derive(Debug, Parser)]
#[command(
    name = "easydict_long_doc",
    about = "Translate long documents with the Easydict Rust runtime"
)]
pub struct LongDocumentCliOptions {
    #[arg(long = "list-services", help = "List long-document-capable services")]
    list_services: bool,

    #[arg(
        short = 'i',
        long = "input",
        value_name = "FILE",
        required_unless_present = "list_services",
        help = "Input .pdf, .txt, .md, or .markdown document"
    )]
    input: Option<PathBuf>,

    #[arg(
        short = 't',
        long = "target-language",
        value_name = "LANG",
        required_unless_present = "list_services",
        help = "Target language code or name, for example zh-Hans, en, ja"
    )]
    target_language: Option<String>,

    #[arg(long = "from", value_name = "LANG", default_value = "auto")]
    source_language: String,

    #[arg(short = 'o', long = "output", value_name = "FILE")]
    output: Option<PathBuf>,

    #[arg(short = 's', long = "service", value_name = "SERVICE_ID")]
    service: Option<String>,

    #[arg(long = "output-mode", value_name = "MODE")]
    output_mode: Option<String>,

    #[arg(long = "layout", value_name = "MODE")]
    layout: Option<String>,

    #[arg(long = "pdf-export-mode", value_name = "MODE")]
    pdf_export_mode: Option<String>,

    #[arg(long = "page", value_name = "N", conflicts_with = "page_range")]
    page: Option<u32>,

    #[arg(long = "page-range", value_name = "RANGE")]
    page_range: Option<String>,

    #[arg(long = "max-concurrency", value_name = "N")]
    max_concurrency: Option<u32>,

    #[arg(long = "app-dir", value_name = "DIR")]
    app_dir: Option<PathBuf>,

    #[arg(short = 'e', long = "env-file", value_name = "FILE")]
    env_file: Option<PathBuf>,

    #[arg(long = "vision-endpoint", value_name = "URL")]
    vision_endpoint: Option<String>,

    #[arg(long = "vision-api-key", value_name = "KEY")]
    vision_api_key: Option<String>,

    #[arg(long = "vision-model", value_name = "MODEL")]
    vision_model: Option<String>,

    #[arg(long = "json", help = "Emit JSON result and event lines")]
    json: bool,
}

pub fn run_from_env() -> ExitCode {
    run_from_args(env::args_os())
}

pub fn run_from_args<I, T>(args: I) -> ExitCode
where
    I: IntoIterator<Item = T>,
    T: Into<OsString> + Clone,
{
    let options = match LongDocumentCliOptions::try_parse_from(args) {
        Ok(options) => options,
        Err(error) => {
            let code = error.exit_code();
            let _ = error.print();
            return ExitCode::from(code.try_into().unwrap_or(2));
        }
    };

    match run(options, &mut io::stdout().lock(), &mut io::stderr().lock()) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("error: {error}");
            ExitCode::from(1)
        }
    }
}

fn run(
    options: LongDocumentCliOptions,
    stdout: &mut impl Write,
    stderr: &mut impl Write,
) -> Result<(), LongDocumentCliError> {
    if options.list_services {
        return write_supported_services(stdout);
    }

    if let Some(page) = options.page {
        if page == 0 {
            return Err(LongDocumentCliError::InvalidArgument(
                "--page must be >= 1".to_string(),
            ));
        }
    }
    if let Some(max_concurrency) = options.max_concurrency {
        if max_concurrency == 0 {
            return Err(LongDocumentCliError::InvalidArgument(
                "--max-concurrency must be >= 1".to_string(),
            ));
        }
    }

    if let Some(env_file) = options.env_file.as_deref() {
        load_and_apply_env_file(env_file)?;
    }

    let mut state = build_cli_state(&options)?;
    let mut request = build_long_document_request(&state, 1)?;

    if let Some(output) = options.output.as_ref() {
        request.params.output_path = Some(path_string(output));
    }
    if let Some(layout) = options.layout.as_deref() {
        request.params.layout_detection = Some(normalize_layout_mode(layout)?);
    }
    if let Some(pdf_export_mode) = options.pdf_export_mode.as_deref() {
        request.params.pdf_export_mode = Some(normalize_pdf_export_mode(pdf_export_mode)?);
    }
    if let Some(endpoint) = non_empty_option(options.vision_endpoint.as_deref()) {
        request.params.vision_endpoint = Some(endpoint.to_string());
    }
    if let Some(api_key) = non_empty_option(options.vision_api_key.as_deref()) {
        request.params.vision_api_key = Some(api_key.to_string());
    }
    if let Some(model) = non_empty_option(options.vision_model.as_deref()) {
        request.params.vision_model = Some(model.to_string());
    }

    let outcome = if let Some(app_dir) = options.app_dir.as_ref() {
        run_long_document_request_with_packaged_app_dir(request, app_dir)
    } else {
        run_long_document_request_with_current_app_dir(request)
    };

    write_outcome(stdout, stderr, &outcome, options.json)?;
    state.long_document.last_output_path = outcome.result.as_ref().ok().and_then(|result| {
        result
            .bilingual_output_path
            .clone()
            .or_else(|| result.output_path.clone())
    });

    if let Err(error) = outcome.result {
        return Err(LongDocumentCliError::LongDocument(error.message));
    }

    Ok(())
}

fn build_cli_state(
    options: &LongDocumentCliOptions,
) -> Result<EasydictUiState, LongDocumentCliError> {
    let mut state = EasydictUiState::default();
    state.settings = load_cli_settings()?;
    apply_environment_overrides(&mut state.settings);

    let input = options
        .input
        .as_ref()
        .ok_or_else(|| LongDocumentCliError::InvalidArgument("--input is required".to_string()))?;
    let target_language = options.target_language.as_deref().ok_or_else(|| {
        LongDocumentCliError::InvalidArgument("--target-language is required".to_string())
    })?;

    state.long_document.selected_file = path_string(input);
    state.long_document.source_language = options.source_language.clone();
    state.long_document.target_language = target_language.to_string();
    state.long_document.service = selected_service_id(options);
    state.long_document.output_mode = selected_output_mode(options)?;
    state.long_document.page_range = selected_page_range(options);
    state.long_document.concurrency = selected_concurrency(options);

    if let Some(layout) = options.layout.as_deref() {
        state.settings.layout_detection_mode = normalize_layout_mode(layout)?;
    } else if let Some(layout) = env_value(&["EASYDICT_LAYOUT_DETECTION_MODE"]) {
        state.settings.layout_detection_mode = normalize_layout_mode(&layout)?;
    }

    if let Some(enabled) = env_bool(&["EASYDICT_LONGDOC_DOCUMENT_CONTEXT_PASS"]) {
        state.long_document.two_pass_context = enabled;
    }

    Ok(state)
}

fn load_cli_settings() -> Result<SettingsState, LongDocumentCliError> {
    let path = default_settings_storage_path();
    match load_settings_file(&path) {
        Ok(result) => Ok(result.settings),
        Err(SettingsStorageError::Io(error)) if error.kind() == io::ErrorKind::NotFound => {
            Ok(SettingsState::default())
        }
        Err(error) => Err(LongDocumentCliError::Settings(error)),
    }
}

fn selected_service_id(options: &LongDocumentCliOptions) -> String {
    options
        .service
        .as_deref()
        .map(str::trim)
        .filter(|service| !service.is_empty())
        .map(ToOwned::to_owned)
        .or_else(|| env_value(&["EASYDICT_SERVICE_ID", "LONGDOC_SERVICE_ID", "SERVICE_ID"]))
        .unwrap_or_else(|| "google".to_string())
}

fn selected_output_mode(options: &LongDocumentCliOptions) -> Result<String, LongDocumentCliError> {
    let value = options
        .output_mode
        .as_deref()
        .map(str::trim)
        .filter(|mode| !mode.is_empty())
        .map(ToOwned::to_owned)
        .or_else(|| env_value(&["EASYDICT_DOCUMENT_OUTPUT_MODE"]))
        .unwrap_or_else(|| "mono".to_string());
    normalize_output_mode(&value)
}

fn selected_page_range(options: &LongDocumentCliOptions) -> String {
    if let Some(page) = options.page {
        return page.to_string();
    }

    options
        .page_range
        .as_deref()
        .map(str::trim)
        .filter(|range| !range.is_empty())
        .map(ToOwned::to_owned)
        .or_else(|| env_value(&["EASYDICT_LONGDOC_PAGE_RANGE"]))
        .unwrap_or_default()
}

fn selected_concurrency(options: &LongDocumentCliOptions) -> String {
    options
        .max_concurrency
        .map(|value| value.clamp(1, 16).to_string())
        .or_else(|| env_value(&["EASYDICT_LONGDOC_MAX_CONCURRENCY"]))
        .unwrap_or_else(|| "4".to_string())
}

fn write_supported_services(stdout: &mut impl Write) -> Result<(), LongDocumentCliError> {
    let mut descriptors = long_document_supported_service_descriptors();
    descriptors.sort_by_key(|descriptor| descriptor.display_name.to_ascii_lowercase());

    if descriptors.is_empty() {
        writeln!(stdout, "No long-document-capable services available.")?;
        return Ok(());
    }

    for descriptor in descriptors {
        writeln!(
            stdout,
            "{} | {} | configuredByDefault={} | requiresApiKey={}",
            descriptor.service_id,
            descriptor.display_name,
            descriptor.configured_by_default,
            descriptor.requires_api_key
        )?;
    }

    Ok(())
}

fn write_outcome(
    stdout: &mut impl Write,
    stderr: &mut impl Write,
    outcome: &LongDocumentOutcome,
    json_output: bool,
) -> Result<(), LongDocumentCliError> {
    if json_output {
        for event in &outcome.events {
            writeln!(stdout, "{}", json_event(event)?)?;
        }
        match &outcome.result {
            Ok(result) => {
                writeln!(
                    stdout,
                    "{}",
                    serde_json::to_string(&json!({
                        "event": "done",
                        "input": outcome.input_label,
                        "result": result,
                    }))?
                )?;
            }
            Err(error) => {
                writeln!(
                    stdout,
                    "{}",
                    serde_json::to_string(&json!({
                        "event": "error",
                        "input": outcome.input_label,
                        "message": error.message,
                    }))?
                )?;
            }
        }
        return Ok(());
    }

    for event in &outcome.events {
        match event {
            LongDocumentEvent::Status(status) => {
                writeln!(stderr, "[status] {}", status.message)?;
            }
            LongDocumentEvent::Progress(progress) => {
                writeln!(
                    stderr,
                    "[progress] {} {}/{} ({:.0}%)",
                    progress.stage,
                    progress.current_block,
                    progress.total_blocks,
                    progress.percentage
                )?;
            }
            LongDocumentEvent::BlockTranslated(block) => {
                writeln!(stderr, "[block] translated chunk {}", block.chunk_index + 1)?;
            }
        }
    }

    match &outcome.result {
        Ok(result) => {
            writeln!(stdout, "State: {}", result.state)?;
            if let Some(output_path) = result.output_path.as_deref() {
                writeln!(stdout, "Output: {output_path}")?;
            }
            if let Some(bilingual_output_path) = result.bilingual_output_path.as_deref() {
                if result.output_path.as_deref() != Some(bilingual_output_path) {
                    writeln!(stdout, "Bilingual output: {bilingual_output_path}")?;
                }
            }
            writeln!(
                stdout,
                "Chunks: {}/{}",
                result.succeeded_chunks, result.total_chunks
            )?;
            if let Some(failed) = result.failed_chunk_indexes.as_ref() {
                let indexes = failed
                    .iter()
                    .map(|index| (index + 1).to_string())
                    .collect::<Vec<_>>()
                    .join(", ");
                writeln!(stdout, "Failed chunk indexes: {indexes}")?;
            }
        }
        Err(error) => {
            writeln!(stderr, "[error] {}", error.message)?;
        }
    }

    Ok(())
}

fn json_event(event: &LongDocumentEvent) -> Result<String, serde_json::Error> {
    match event {
        LongDocumentEvent::Status(data) => serde_json::to_string(&json!({
            "event": "status",
            "data": data,
        })),
        LongDocumentEvent::Progress(data) => serde_json::to_string(&json!({
            "event": "progress",
            "data": data,
        })),
        LongDocumentEvent::BlockTranslated(data) => serde_json::to_string(&json!({
            "event": "block_translated",
            "data": data,
        })),
    }
}

fn load_and_apply_env_file(path: &Path) -> Result<(), LongDocumentCliError> {
    let text = fs::read_to_string(path).map_err(|error| LongDocumentCliError::EnvFile {
        path: path.to_path_buf(),
        error,
    })?;

    for raw_line in text.lines() {
        let mut line = raw_line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if let Some(rest) = line.strip_prefix("export ") {
            line = rest.trim_start();
        }

        let Some((key, value)) = line.split_once('=') else {
            continue;
        };
        let key = key.trim();
        if key.is_empty() {
            continue;
        }

        env::set_var(key, parse_env_value(value.trim()));
    }

    Ok(())
}

fn parse_env_value(value: &str) -> String {
    if value.len() >= 2 {
        let bytes = value.as_bytes();
        if (bytes[0] == b'"' && bytes[value.len() - 1] == b'"')
            || (bytes[0] == b'\'' && bytes[value.len() - 1] == b'\'')
        {
            let inner = &value[1..value.len() - 1];
            if bytes[0] == b'"' {
                return inner
                    .replace("\\n", "\n")
                    .replace("\\r", "\r")
                    .replace("\\t", "\t")
                    .replace("\\\"", "\"");
            }
            return inner.to_string();
        }
    }

    value
        .split_once(" #")
        .map_or(value, |(prefix, _)| prefix.trim_end())
        .to_string()
}

fn apply_environment_overrides(settings: &mut SettingsState) {
    if let Some(value) = env_value(&["OPENAI_API_KEY"]) {
        settings.open_ai_api_key = value;
    }
    if let Some(value) = env_value(&["OPENAI_MODEL"]) {
        settings.open_ai_model = value;
    }
    if let Some(value) = env_value(&["OPENAI_ENDPOINT"]) {
        settings.open_ai_endpoint = value;
    } else if let Some(value) = env_value(&["OPENAI_BASE_URL", "OPENAI_API_BASE"]) {
        settings.open_ai_endpoint = normalize_responses_endpoint(&value);
    }

    if let Some(value) = env_value(&["CUSTOM_OPENAI_API_KEY"]) {
        service_provider_mut(settings, "custom-openai").api_key = value;
    }
    if let Some(value) = env_value(&["CUSTOM_OPENAI_MODEL"]) {
        service_provider_mut(settings, "custom-openai").model = value;
    }
    if let Some(value) = env_value(&["CUSTOM_OPENAI_ENDPOINT"]) {
        service_provider_mut(settings, "custom-openai").endpoint = value;
    } else if let Some(value) = env_value(&["CUSTOM_OPENAI_BASE_URL", "CUSTOM_OPENAI_API_BASE"]) {
        service_provider_mut(settings, "custom-openai").endpoint =
            normalize_chat_completions_endpoint(&value);
    }

    if let Some(value) = env_value(&["OLLAMA_ENDPOINT"]) {
        settings.ollama_endpoint = normalize_chat_completions_endpoint(&value);
    } else if let Some(value) = env_value(&["OLLAMA_BASE_URL", "OLLAMA_HOST"]) {
        settings.ollama_endpoint = normalize_chat_completions_endpoint(&value);
    }
    if let Some(value) = env_value(&["OLLAMA_MODEL"]) {
        settings.ollama_model = value;
    }

    apply_provider_env(
        settings,
        "gemini",
        &["GEMINI_API_KEY"],
        &["GEMINI_MODEL"],
        &[],
    );
    apply_provider_env(
        settings,
        "deepseek",
        &["DEEPSEEK_API_KEY"],
        &["DEEPSEEK_MODEL"],
        &[],
    );
    apply_provider_env(settings, "groq", &["GROQ_API_KEY"], &["GROQ_MODEL"], &[]);
    apply_provider_env(settings, "zhipu", &["ZHIPU_API_KEY"], &["ZHIPU_MODEL"], &[]);
    apply_provider_env(
        settings,
        "github",
        &["GITHUB_MODELS_TOKEN"],
        &["GITHUB_MODELS_MODEL"],
        &[],
    );
    apply_provider_env(
        settings,
        "doubao",
        &["DOUBAO_API_KEY", "ARK_API_KEY"],
        &["DOUBAO_MODEL"],
        &["DOUBAO_ENDPOINT", "ARK_ENDPOINT"],
    );
    apply_provider_env(settings, "builtin", &[], &["BUILTIN_AI_MODEL"], &[]);

    if let Some(value) = env_value(&["DEEPL_API_KEY"]) {
        settings.deepl_api_key = value;
    }
    if let Some(value) = env_bool(&["DEEPL_USE_FREE_API"]) {
        settings.deepl_use_free_api = value;
    }
    if let Some(value) = env_bool(&["EASYDICT_PROXY_ENABLED"]) {
        settings.proxy_enabled = value;
    }
    if let Some(value) = env_value(&["EASYDICT_PROXY_URI"]) {
        settings.proxy_url = value;
    }
    if let Some(value) = env_bool(&["EASYDICT_PROXY_BYPASS_LOCAL"]) {
        settings.proxy_bypass_local = value;
    }
    if let Some(value) = env_bool(&["EASYDICT_ENABLE_TRANSLATION_CACHE"]) {
        settings.translation_cache_enabled = value;
    }
    if let Some(value) = env_value(&["EASYDICT_LONGDOC_CUSTOM_PROMPT"]) {
        settings.custom_translation_prompt = value;
    }
}

fn apply_provider_env(
    settings: &mut SettingsState,
    service_id: &str,
    api_key_aliases: &[&str],
    model_aliases: &[&str],
    endpoint_aliases: &[&str],
) {
    if let Some(value) = env_value(api_key_aliases) {
        service_provider_mut(settings, service_id).api_key = value;
    }
    if let Some(value) = env_value(model_aliases) {
        service_provider_mut(settings, service_id).model = value;
    }
    if let Some(value) = env_value(endpoint_aliases) {
        service_provider_mut(settings, service_id).endpoint = value;
    }
}

fn service_provider_mut<'a>(
    settings: &'a mut SettingsState,
    service_id: &str,
) -> &'a mut ServiceProviderSetting {
    if let Some(index) = settings
        .service_provider_settings
        .iter()
        .position(|setting| setting.service_id == service_id)
    {
        return &mut settings.service_provider_settings[index];
    }

    settings
        .service_provider_settings
        .push(ServiceProviderSetting {
            service_id: service_id.to_string(),
            api_key: String::new(),
            endpoint: String::new(),
            model: default_provider_model(service_id).to_string(),
            status: "Not tested".to_string(),
        });
    settings
        .service_provider_settings
        .last_mut()
        .expect("provider setting was just pushed")
}

fn default_provider_model(service_id: &str) -> &'static str {
    match service_id {
        "deepseek" => "deepseek-chat",
        "groq" => "llama-3.3-70b-versatile",
        "zhipu" => "glm-4.5-flash",
        "github" => "gpt-4.1",
        "gemini" => "gemini-2.5-flash",
        "builtin" => "glm-4-flash-250414",
        "doubao" => "doubao-seed-translation-250915",
        _ => "gpt-3.5-turbo",
    }
}

fn normalize_output_mode(value: &str) -> Result<String, LongDocumentCliError> {
    match normalized_token(value).as_str() {
        "" | "monolingual" | "translated" | "mono" => Ok("mono".to_string()),
        "bilingual" | "dual" => Ok("bilingual".to_string()),
        "both" => Ok("both".to_string()),
        _ => Err(LongDocumentCliError::InvalidArgument(format!(
            "Unsupported output mode '{value}'. Use Monolingual, Bilingual, or Both."
        ))),
    }
}

fn normalize_layout_mode(value: &str) -> Result<String, LongDocumentCliError> {
    match normalized_token(value).as_str() {
        "" | "auto" => Ok("Auto".to_string()),
        "heuristic" => Ok("Heuristic".to_string()),
        "onnx" | "onnxlocal" => Ok("OnnxLocal".to_string()),
        "vision" | "visionllm" => Ok("VisionLLM".to_string()),
        _ => Err(LongDocumentCliError::InvalidArgument(format!(
            "Unsupported layout mode '{value}'. Use Auto, Heuristic, OnnxLocal, or VisionLLM."
        ))),
    }
}

fn normalize_pdf_export_mode(value: &str) -> Result<String, LongDocumentCliError> {
    match normalized_token(value).as_str() {
        "" | "mupdf" | "contentstreamreplacement" | "contentstream" => {
            Ok("ContentStreamReplacement".to_string())
        }
        "overlay" => Ok("Overlay".to_string()),
        _ => Err(LongDocumentCliError::InvalidArgument(format!(
            "Unsupported PDF export mode '{value}'. Use ContentStreamReplacement or Overlay."
        ))),
    }
}

fn normalized_token(value: &str) -> String {
    value
        .trim()
        .chars()
        .filter(|character| !matches!(character, '-' | '_' | ' '))
        .flat_map(char::to_lowercase)
        .collect()
}

fn env_value(keys: &[&str]) -> Option<String> {
    keys.iter().find_map(|key| {
        env::var(key)
            .ok()
            .map(|value| value.trim().to_string())
            .filter(|value| !value.is_empty())
    })
}

fn env_bool(keys: &[&str]) -> Option<bool> {
    let value = env_value(keys)?;
    match value.trim().to_ascii_lowercase().as_str() {
        "1" | "true" | "yes" | "on" => Some(true),
        "0" | "false" | "no" | "off" => Some(false),
        _ => None,
    }
}

fn normalize_chat_completions_endpoint(endpoint: &str) -> String {
    let endpoint = endpoint.trim().trim_end_matches('/');
    if endpoint.to_ascii_lowercase().ends_with("/chat/completions") {
        endpoint.to_string()
    } else {
        format!("{endpoint}/chat/completions")
    }
}

fn normalize_responses_endpoint(endpoint: &str) -> String {
    let endpoint = endpoint.trim().trim_end_matches('/');
    let lower = endpoint.to_ascii_lowercase();
    if lower.ends_with("/responses") || lower.ends_with("/chat/completions") {
        endpoint.to_string()
    } else {
        format!("{endpoint}/responses")
    }
}

fn non_empty_option(value: Option<&str>) -> Option<&str> {
    value.map(str::trim).filter(|value| !value.is_empty())
}

fn path_string(path: &Path) -> String {
    path.to_string_lossy().to_string()
}

#[derive(Debug)]
enum LongDocumentCliError {
    Settings(SettingsStorageError),
    Start(crate::long_document::LongDocumentStartError),
    EnvFile { path: PathBuf, error: io::Error },
    Io(io::Error),
    Json(serde_json::Error),
    InvalidArgument(String),
    LongDocument(String),
}

impl fmt::Display for LongDocumentCliError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Settings(error) => write!(formatter, "{error}"),
            Self::Start(error) => write!(formatter, "{error}"),
            Self::EnvFile { path, error } => {
                write!(
                    formatter,
                    "Could not read environment file '{}': {error}",
                    path.display()
                )
            }
            Self::Io(error) => write!(formatter, "{error}"),
            Self::Json(error) => write!(formatter, "{error}"),
            Self::InvalidArgument(message) => formatter.write_str(message),
            Self::LongDocument(message) => formatter.write_str(message),
        }
    }
}

impl std::error::Error for LongDocumentCliError {}

impl From<SettingsStorageError> for LongDocumentCliError {
    fn from(error: SettingsStorageError) -> Self {
        Self::Settings(error)
    }
}

impl From<crate::long_document::LongDocumentStartError> for LongDocumentCliError {
    fn from(error: crate::long_document::LongDocumentStartError) -> Self {
        Self::Start(error)
    }
}

impl From<io::Error> for LongDocumentCliError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<serde_json::Error> for LongDocumentCliError {
    fn from(error: serde_json::Error) -> Self {
        Self::Json(error)
    }
}

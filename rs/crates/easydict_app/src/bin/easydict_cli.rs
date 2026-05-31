use easydict_app::cli_translate::{
    common_dev_host_candidates, parse_args, usage, CliMode, CliOptions, CliParseError,
    CompatHostTarget,
};
use easydict_app::compat_client::{
    default_compat_host_path, CompatClientError, CompatHostCommand, CompatHostFacade,
};
use easydict_app::compat_protocol::{
    GrammarCorrectResultDto, TranslateChunkEventData, TranslationResultDto,
};
use serde_json::json;
use std::env;
use std::fmt;
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
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
    let mut facade = spawn_facade(&options.host)?;
    let stdout = io::stdout();
    let stderr = io::stderr();
    let mut stdout = stdout.lock();
    let mut stderr = stderr.lock();

    match options.mode {
        CliMode::Translate => {
            let result = facade.translate(&options.translate_params(text))?;
            write_translation_result(&mut stdout, &mut stderr, &options, &result)?;
        }
        CliMode::Stream => {
            let result =
                run_stream_translation(&mut facade, &options, text, &mut stdout, &mut stderr)?;
            if options.verbose && !options.json {
                write_translation_metadata(&mut stderr, &result)?;
            }
        }
        CliMode::Grammar => {
            let result = facade.grammar_correct(&options.grammar_params(text))?;
            write_grammar_result(&mut stdout, &mut stderr, &options, &result)?;
        }
        CliMode::Batch => {
            run_batch_translation(&mut facade, &options, text, &mut stdout, &mut stderr)?;
        }
    }

    Ok(())
}

fn run_batch_translation(
    facade: &mut CompatHostFacade,
    options: &CliOptions,
    text: String,
    stdout: &mut impl Write,
    stderr: &mut impl Write,
) -> Result<(), CliError> {
    for (index, line) in batch_lines(&text).into_iter().enumerate() {
        let result = facade.translate(&options.translate_params(line.clone()))?;
        write_batch_translation_result(stdout, stderr, options, index + 1, &line, &result)?;
    }

    Ok(())
}

fn run_stream_translation(
    facade: &mut CompatHostFacade,
    options: &CliOptions,
    text: String,
    stdout: &mut impl Write,
    stderr: &mut impl Write,
) -> Result<TranslationResultDto, CliError> {
    let mut write_error = None;
    let result = facade.translate_stream_observing_chunks(
        &options.translate_params(text),
        |chunk: TranslateChunkEventData| {
            if write_error.is_some() {
                return;
            }

            let write_result = if options.json {
                writeln!(
                    stdout,
                    "{}",
                    json!({
                        "event": "chunk",
                        "text": chunk.text,
                    })
                )
            } else {
                write!(stdout, "{}", chunk.text)
            }
            .and_then(|_| stdout.flush());

            if let Err(error) = write_result {
                write_error = Some(error);
            }
        },
    )?;

    if let Some(error) = write_error {
        return Err(CliError::Io(error));
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

fn spawn_facade(target: &CompatHostTarget) -> Result<CompatHostFacade, CliError> {
    let command = resolve_host_command(target)?;
    command
        .spawn()
        .map(CompatHostFacade::new)
        .map_err(CliError::Compat)
}

fn resolve_host_command(target: &CompatHostTarget) -> Result<CompatHostCommand, CliError> {
    match target {
        CompatHostTarget::Program { program, args } => {
            if !program.exists() {
                return Err(CliError::HostNotFound(program.clone()));
            }

            let mut command = CompatHostCommand::new(program.clone());
            for arg in args {
                command = command.arg(arg.clone());
            }
            Ok(command)
        }
        CompatHostTarget::AppDir(app_dir) => {
            let host = default_compat_host_path(app_dir);
            if !host.exists() {
                return Err(CliError::HostNotFound(host));
            }
            Ok(CompatHostCommand::packaged(app_dir))
        }
        CompatHostTarget::Auto => auto_host_command(),
    }
}

fn auto_host_command() -> Result<CompatHostCommand, CliError> {
    if let Some(path) = env::var_os("EASYDICT_COMPAT_HOST").map(PathBuf::from) {
        if path.exists() {
            return Ok(CompatHostCommand::new(path));
        }
        return Err(CliError::HostNotFound(path));
    }

    let exe_dir = env::current_exe()
        .ok()
        .and_then(|path| path.parent().map(Path::to_path_buf));
    if let Some(exe_dir) = exe_dir {
        let packaged = default_compat_host_path(&exe_dir);
        if packaged.exists() {
            return Ok(CompatHostCommand::packaged(exe_dir));
        }
    }

    let mut roots = Vec::new();
    if let Ok(cwd) = env::current_dir() {
        roots.push(cwd);
    }
    if let Ok(exe) = env::current_exe() {
        if let Some(exe_dir) = exe.parent() {
            roots.push(exe_dir.to_path_buf());
        }
    }

    for root in roots {
        for candidate in common_dev_host_candidates(root) {
            if candidate.exists() {
                return Ok(CompatHostCommand::new(candidate));
            }
        }
    }

    Err(CliError::HostAutoNotFound)
}

#[derive(Debug)]
enum CliError {
    Parse(CliParseError),
    Compat(CompatClientError),
    Io(io::Error),
    Json(serde_json::Error),
    HostNotFound(PathBuf),
    HostAutoNotFound,
}

impl fmt::Display for CliError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Parse(error) => write!(formatter, "{error}"),
            Self::Compat(error) => write!(formatter, "{error}"),
            Self::Io(error) => write!(formatter, "{error}"),
            Self::Json(error) => write!(formatter, "{error}"),
            Self::HostNotFound(path) => {
                write!(
                    formatter,
                    "CompatHost executable not found: {}",
                    path.display()
                )
            }
            Self::HostAutoNotFound => formatter.write_str(
                "CompatHost executable not found; pass --host, --app-dir, or EASYDICT_COMPAT_HOST",
            ),
        }
    }
}

impl std::error::Error for CliError {}

impl From<CompatClientError> for CliError {
    fn from(error: CompatClientError) -> Self {
        Self::Compat(error)
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

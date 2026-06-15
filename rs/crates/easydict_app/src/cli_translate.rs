use crate::protocol::{GrammarCorrectParams, TranslateParams};
use std::fmt;
#[cfg(feature = "retained-dotnet-workers")]
use std::path::PathBuf;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CliOptions {
    pub mode: CliMode,
    pub text: String,
    pub from: Option<String>,
    pub to: Option<String>,
    pub language: Option<String>,
    pub services: Vec<String>,
    pub json: bool,
    pub verbose: bool,
}

impl CliOptions {
    pub fn translate_params(&self, text: impl Into<String>) -> TranslateParams {
        TranslateParams {
            text: text.into(),
            from: self.from.clone(),
            to: self.to.clone(),
            services: services_param(&self.services),
            custom_prompt: None,
        }
    }

    pub fn grammar_params(&self, text: impl Into<String>) -> GrammarCorrectParams {
        GrammarCorrectParams {
            text: text.into(),
            language: self.language.clone().or_else(|| self.from.clone()),
            services: services_param(&self.services),
            include_explanations: true,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CliMode {
    Translate,
    Stream,
    Grammar,
    Batch,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum CliParseError {
    Help,
    MissingCommand,
    UnknownCommand(String),
    MissingValue(String),
    MissingText,
    UnknownOption(String),
}

impl fmt::Display for CliParseError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Help => formatter.write_str("help requested"),
            Self::MissingCommand => formatter.write_str("missing command"),
            Self::UnknownCommand(command) => write!(formatter, "unknown command: {command}"),
            Self::MissingValue(option) => write!(formatter, "missing value for {option}"),
            Self::MissingText => {
                formatter.write_str("missing text; pass --text or positional text")
            }
            Self::UnknownOption(option) => write!(formatter, "unknown option: {option}"),
        }
    }
}

impl std::error::Error for CliParseError {}

pub fn usage() -> &'static str {
    "Usage:
  easydict_cli translate [OPTIONS] --text TEXT
  easydict_cli stream [OPTIONS] --text TEXT
  easydict_cli grammar [OPTIONS] --text TEXT
  easydict_cli batch [OPTIONS] --text TEXT

Commands:
  translate    Run a complete translation request and print the final text.
  stream       Run a streaming translation request; plain mode prints chunks live.
  grammar      Run grammar correction and print the corrected text.
  batch        Translate one line per input line for quick regression checks.

Options:
  --text TEXT          Text to translate. Use '-' to read stdin.
  --from LANG          Source language, e.g. auto, en, zh-Hans.
  --to LANG            Target language for translate/stream/batch.
  --language LANG      Grammar language; defaults to --from when omitted.
  --service ID         Service id. Can be repeated or comma-separated.
  --services IDS       Alias for --service with comma-separated ids.
  --json               Emit JSON for translate/grammar, JSON Lines for stream/batch.
  --verbose            Print service/timing metadata to stderr in plain mode.
  -h, --help           Show this help.

Examples:
  easydict_cli translate --service google --from en --to zh-Hans --text \"Hello\"
  easydict_cli stream --service openai --from en --to zh-Hans --text \"Hello\"
  easydict_cli grammar --service openai --language en --text \"I has a apple.\"
  echo Hello | easydict_cli translate --service google --to zh-Hans --text -
  \"Hello`nGood morning\" | easydict_cli batch --service google --to zh-Hans --text - --json"
}

pub fn parse_args<I, S>(args: I) -> Result<CliOptions, CliParseError>
where
    I: IntoIterator<Item = S>,
    S: Into<String>,
{
    let mut args = args.into_iter().map(Into::into);
    let command = args.next().ok_or(CliParseError::MissingCommand)?;
    let mode = match command.as_str() {
        "-h" | "--help" | "help" => return Err(CliParseError::Help),
        "translate" => CliMode::Translate,
        "stream" => CliMode::Stream,
        "grammar" => CliMode::Grammar,
        "batch" => CliMode::Batch,
        _ => return Err(CliParseError::UnknownCommand(command)),
    };

    let mut text = None;
    let mut from = None;
    let mut to = None;
    let mut language = None;
    let mut services = Vec::new();
    let mut json = false;
    let mut verbose = false;
    #[cfg(feature = "retained-dotnet-workers")]
    let mut host_program = None;
    #[cfg(feature = "retained-dotnet-workers")]
    let mut host_args = Vec::new();
    #[cfg(feature = "retained-dotnet-workers")]
    let mut app_dir = None;
    let mut positional = Vec::new();
    let mut rest = args.peekable();

    while let Some(arg) = rest.next() {
        if let Some((name, value)) = split_long_option(&arg) {
            match name {
                "--text" => text = Some(value.to_string()),
                "--from" => from = Some(value.to_string()),
                "--to" => to = Some(value.to_string()),
                "--language" => language = Some(value.to_string()),
                "--service" | "--services" => push_services(&mut services, value),
                "--host" => {
                    #[cfg(feature = "retained-dotnet-workers")]
                    {
                        host_program = Some(PathBuf::from(value));
                    }
                    #[cfg(not(feature = "retained-dotnet-workers"))]
                    {
                        return Err(CliParseError::UnknownOption(name.to_string()));
                    }
                }
                "--host-arg" => {
                    #[cfg(feature = "retained-dotnet-workers")]
                    {
                        host_args.push(value.to_string());
                    }
                    #[cfg(not(feature = "retained-dotnet-workers"))]
                    {
                        return Err(CliParseError::UnknownOption(name.to_string()));
                    }
                }
                "--app-dir" => {
                    #[cfg(feature = "retained-dotnet-workers")]
                    {
                        app_dir = Some(PathBuf::from(value));
                    }
                    #[cfg(not(feature = "retained-dotnet-workers"))]
                    {
                        return Err(CliParseError::UnknownOption(name.to_string()));
                    }
                }
                _ => return Err(CliParseError::UnknownOption(name.to_string())),
            }
            continue;
        }

        match arg.as_str() {
            "-h" | "--help" => return Err(CliParseError::Help),
            "--json" => json = true,
            "--verbose" => verbose = true,
            "--text" => text = Some(next_value(&mut rest, "--text")?),
            "--from" => from = Some(next_value(&mut rest, "--from")?),
            "--to" => to = Some(next_value(&mut rest, "--to")?),
            "--language" => language = Some(next_value(&mut rest, "--language")?),
            "--service" | "--services" => {
                let value = next_value(&mut rest, arg.as_str())?;
                push_services(&mut services, &value);
            }
            "--host" => {
                #[cfg(feature = "retained-dotnet-workers")]
                {
                    host_program = Some(PathBuf::from(next_value(&mut rest, "--host")?));
                }
                #[cfg(not(feature = "retained-dotnet-workers"))]
                {
                    return Err(CliParseError::UnknownOption(arg));
                }
            }
            "--host-arg" => {
                #[cfg(feature = "retained-dotnet-workers")]
                {
                    host_args.push(next_value(&mut rest, "--host-arg")?);
                }
                #[cfg(not(feature = "retained-dotnet-workers"))]
                {
                    return Err(CliParseError::UnknownOption(arg));
                }
            }
            "--app-dir" => {
                #[cfg(feature = "retained-dotnet-workers")]
                {
                    app_dir = Some(PathBuf::from(next_value(&mut rest, "--app-dir")?));
                }
                #[cfg(not(feature = "retained-dotnet-workers"))]
                {
                    return Err(CliParseError::UnknownOption(arg));
                }
            }
            value if value.starts_with('-') => {
                return Err(CliParseError::UnknownOption(value.to_string()));
            }
            value => positional.push(value.to_string()),
        }
    }

    let text = text
        .or_else(|| (!positional.is_empty()).then(|| positional.join(" ")))
        .ok_or(CliParseError::MissingText)?;

    #[cfg(feature = "retained-dotnet-workers")]
    let _legacy_host_option = (host_program, host_args, app_dir);

    Ok(CliOptions {
        mode,
        text,
        from,
        to,
        language,
        services,
        json,
        verbose,
    })
}

fn next_value<I>(args: &mut std::iter::Peekable<I>, option: &str) -> Result<String, CliParseError>
where
    I: Iterator<Item = String>,
{
    args.next()
        .filter(|value| !value.is_empty())
        .ok_or_else(|| CliParseError::MissingValue(option.to_string()))
}

fn split_long_option(value: &str) -> Option<(&str, &str)> {
    let (name, value) = value.split_once('=')?;
    name.starts_with("--").then_some((name, value))
}

fn push_services(services: &mut Vec<String>, value: &str) {
    services.extend(
        value
            .split(',')
            .map(str::trim)
            .filter(|service| !service.is_empty())
            .map(str::to_string),
    );
}

fn services_param(services: &[String]) -> Option<Vec<String>> {
    (!services.is_empty()).then(|| services.to_vec())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_translate_with_positional_text_and_services() {
        let options = parse_args([
            "translate",
            "--from",
            "en",
            "--to=zh-Hans",
            "--service",
            "google,bing",
            "hello",
            "world",
        ])
        .expect("options parse");

        assert_eq!(options.mode, CliMode::Translate);
        assert_eq!(options.text, "hello world");
        assert_eq!(options.from.as_deref(), Some("en"));
        assert_eq!(options.to.as_deref(), Some("zh-Hans"));
        assert_eq!(options.services, ["google", "bing"]);
        assert_eq!(
            options.translate_params("Hello").services,
            Some(vec!["google".to_string(), "bing".to_string()])
        );
    }

    #[test]
    fn accepts_legacy_host_without_exposing_worker_target() {
        let options = parse_args([
            "stream",
            "--json",
            "--host",
            "C:/Tools/workers/localai/Easydict.Workers.LocalAi.exe",
            "--host-arg",
            "--trace",
            "--text",
            "Hello",
        ])
        .expect("options parse");

        assert_eq!(options.mode, CliMode::Stream);
        assert!(options.json);
        assert_eq!(options.text, "Hello");
        assert_eq!(options.services, Vec::<String>::new());
    }

    #[test]
    fn parses_grammar_language() {
        let options = parse_args([
            "grammar",
            "--language",
            "en",
            "--service=openai",
            "--text",
            "I has a apple.",
        ])
        .expect("options parse");

        assert_eq!(options.mode, CliMode::Grammar);
        assert_eq!(
            options.grammar_params("Text").language.as_deref(),
            Some("en")
        );
        assert_eq!(options.services, ["openai"]);
    }

    #[cfg(feature = "retained-dotnet-workers")]
    #[test]
    fn retained_feature_ignores_legacy_host_and_app_dir_together() {
        let options = parse_args([
            "translate",
            "--host",
            "host.exe",
            "--app-dir",
            "app",
            "--text",
            "Hello",
        ])
        .expect("legacy host/app-dir hints should parse as no-op compatibility flags");
        assert_eq!(options.mode, CliMode::Translate);
        assert_eq!(options.text, "Hello");
    }

    #[cfg(not(feature = "retained-dotnet-workers"))]
    #[test]
    fn default_build_rejects_legacy_host_and_app_dir_options() {
        for option in ["--host", "--host-arg", "--app-dir"] {
            let error = parse_args(["translate", option, "legacy", "--text", "Hello"])
                .expect_err("default CLI should reject retained-worker compatibility flags");
            assert_eq!(error, CliParseError::UnknownOption(option.to_string()));

            let inline = format!("{option}=legacy");
            let error = parse_args(["translate", inline.as_str(), "--text", "Hello"])
                .expect_err("default CLI should reject inline retained-worker compatibility flags");
            assert_eq!(error, CliParseError::UnknownOption(option.to_string()));
        }
    }

    #[test]
    fn usage_mentions_regression_commands() {
        let usage = usage();

        assert!(usage.contains("translate"));
        assert!(usage.contains("stream"));
        assert!(usage.contains("grammar"));
        assert!(usage.contains("batch"));
        assert!(usage.contains("--json"));
        assert!(!usage.contains("--host"));
        assert!(!usage.contains("--host-arg"));
        assert!(!usage.contains("--app-dir"));
    }

    #[test]
    fn parses_batch_for_line_oriented_regression() {
        let options = parse_args([
            "batch",
            "--from",
            "en",
            "--to",
            "zh-Hans",
            "--service",
            "google",
            "--json",
            "--text",
            "Hello\nGood morning",
        ])
        .expect("options parse");

        assert_eq!(options.mode, CliMode::Batch);
        assert_eq!(options.text, "Hello\nGood morning");
        assert_eq!(options.services, ["google"]);
        assert!(options.json);
    }
}

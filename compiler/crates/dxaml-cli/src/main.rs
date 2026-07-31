//! `dxamlc` — the Direct XAML compiler.
//!
//! ```text
//! dxamlc compile --input <file.xaml> [--output <dir>] [--check]
//! dxamlc --version
//! dxamlc --help
//! ```

use std::path::{Path, PathBuf};
use std::process::ExitCode;

use dxaml_cli::{compile_source, output_file_name, COMPILER_VERSION};

const USAGE: &str = "\
dxamlc — the Direct XAML compiler

USAGE:
    dxamlc compile --input <file.xaml> [--output <dir>] [--check]
    dxamlc --version
    dxamlc --help

OPTIONS:
    --input <file>    XAML document to compile. Required.
    --output <dir>    Directory to write <name>.dxir.json into. Defaults to the input's
                      directory. Ignored with --check.
    --check           Report diagnostics without writing anything.

Diagnostics are written to stderr in MSBuild's format. The exit status is 0 only when the
document compiled with no errors.";

fn main() -> ExitCode {
    let arguments: Vec<String> = std::env::args().skip(1).collect();

    match run(&arguments) {
        Ok(true) => ExitCode::SUCCESS,
        Ok(false) => ExitCode::FAILURE,
        Err(message) => {
            eprintln!("dxamlc: error: {message}");
            eprintln!();
            eprintln!("{USAGE}");
            ExitCode::FAILURE
        }
    }
}

/// `Ok(true)` when the document compiled cleanly, `Ok(false)` when it produced errors, and
/// `Err` for a problem with the invocation itself.
fn run(arguments: &[String]) -> Result<bool, String> {
    if arguments.is_empty() {
        return Err("no command given".to_string());
    }

    match arguments[0].as_str() {
        "--help" | "-h" | "help" => {
            println!("{USAGE}");
            Ok(true)
        }
        "--version" | "-V" => {
            println!("dxamlc {COMPILER_VERSION}");
            Ok(true)
        }
        "compile" => compile(&arguments[1..]),
        other => Err(format!("unknown command '{other}'")),
    }
}

fn compile(arguments: &[String]) -> Result<bool, String> {
    let mut input: Option<PathBuf> = None;
    let mut output: Option<PathBuf> = None;
    let mut check_only = false;

    let mut index = 0usize;
    while index < arguments.len() {
        match arguments[index].as_str() {
            "--input" => {
                index += 1;
                let value = arguments
                    .get(index)
                    .ok_or_else(|| "--input needs a path".to_string())?;
                input = Some(PathBuf::from(value));
            }
            "--output" => {
                index += 1;
                let value = arguments
                    .get(index)
                    .ok_or_else(|| "--output needs a path".to_string())?;
                output = Some(PathBuf::from(value));
            }
            "--check" => check_only = true,
            other => return Err(format!("unknown option '{other}'")),
        }
        index += 1;
    }

    let input = input.ok_or_else(|| "--input is required".to_string())?;

    let source = std::fs::read_to_string(&input)
        .map_err(|error| format!("cannot read {}: {error}", input.display()))?;

    let display_path = input.display().to_string();
    let result = compile_source(&source, &display_path);

    for diagnostic in &result.diagnostics {
        eprintln!("{diagnostic}");
    }

    let document = match result.document {
        Some(document) if !result.failed => document,
        _ => return Ok(false),
    };

    if check_only {
        return Ok(true);
    }

    let stem = input
        .file_stem()
        .and_then(|stem| stem.to_str())
        .ok_or_else(|| format!("{} has no usable file name", input.display()))?;

    let directory = output
        .or_else(|| input.parent().map(Path::to_path_buf))
        .unwrap_or_else(|| PathBuf::from("."));

    std::fs::create_dir_all(&directory)
        .map_err(|error| format!("cannot create {}: {error}", directory.display()))?;

    let destination = directory.join(output_file_name(stem));
    let json = document
        .to_json()
        .map_err(|error| format!("cannot serialize IR: {error}"))?;

    std::fs::write(&destination, json)
        .map_err(|error| format!("cannot write {}: {error}", destination.display()))?;

    println!("{}", destination.display());
    Ok(true)
}

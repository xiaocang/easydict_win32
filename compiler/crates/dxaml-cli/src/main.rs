//! `dxamlc` — the Direct XAML compiler.
//!
//! ```text
//! dxamlc compile --input <file.xaml> [--output <dir>] [--check]
//! dxamlc --version
//! dxamlc --help
//! ```

use std::path::{Path, PathBuf};
use std::process::ExitCode;

use dxaml_cli::{
    bindings_output_file_name, compile_source_with_paths, output_file_name, COMPILER_VERSION,
};

const USAGE: &str = "\
dxamlc — the Direct XAML compiler

USAGE:
    dxamlc compile --input <file.xaml> [--output <dir>] [--source-root <dir>] [--check]
    dxamlc --version
    dxamlc --help

OPTIONS:
    --input <file>    XAML document to compile. Required.
    --output <dir>    Directory for generated IR and C# files. Defaults to the input directory.
    --source-root     Root removed from the source path recorded in IR, for reproducible builds.
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
    let mut source_root: Option<PathBuf> = None;
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
            "--source-root" => {
                index += 1;
                let value = arguments
                    .get(index)
                    .ok_or_else(|| "--source-root needs a path".to_string())?;
                source_root = Some(PathBuf::from(value));
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
    let source_path = reproducible_source_path(&input, source_root.as_deref());
    let result = compile_source_with_paths(&source, &display_path, &source_path);

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

    let ir_destination = directory.join(output_file_name(stem));
    let json = document
        .to_json()
        .map_err(|error| format!("cannot serialize IR: {error}"))?;
    write_if_changed(&ir_destination, json.as_bytes())?;

    let bindings = match dxaml_codegen_csharp::generate(&document) {
        Ok(bindings) => bindings,
        Err(error) => {
            eprintln!("{display_path}(1,1): error DX4001: {error}");
            return Ok(false);
        }
    };
    let bindings_destination = directory.join(bindings_output_file_name(stem));
    write_if_changed(&bindings_destination, bindings.as_bytes())?;

    println!("{}", ir_destination.display());
    println!("{}", bindings_destination.display());
    Ok(true)
}

fn reproducible_source_path(input: &Path, source_root: Option<&Path>) -> String {
    let relative = source_root
        .and_then(|root| input.strip_prefix(root).ok())
        .or_else(|| input.file_name().map(Path::new))
        .unwrap_or(input);
    relative.to_string_lossy().replace('\\', "/")
}

fn write_if_changed(destination: &Path, content: &[u8]) -> Result<(), String> {
    if std::fs::read(destination).is_ok_and(|existing| existing == content) {
        return Ok(());
    }

    std::fs::write(destination, content)
        .map_err(|error| format!("cannot write {}: {error}", destination.display()))
}

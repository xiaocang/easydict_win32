use std::fs;
use std::path::PathBuf;
use std::process::Command;

use easydict_store_listings::{
    build_msstore_payload, load_store_listing_report, parse_language_filter, render_github_summary,
    render_report, StoreListingEntry, StoreListingOptions, StoreListingRenderMode,
};

fn main() {
    std::process::exit(run(std::env::args().skip(1).collect()));
}

fn run(args: Vec<String>) -> i32 {
    if args.is_empty() || args[0] == "-h" || args[0] == "--help" {
        print_usage();
        return 2;
    }

    match args[0].as_str() {
        "validate" => run_validate_or_preview(&args[1..], StoreListingRenderMode::Validate),
        "preview" => run_validate_or_preview(&args[1..], StoreListingRenderMode::Preview),
        "summary" => run_summary(&args[1..]),
        "submit" => run_submit(&args[1..]),
        unknown => {
            eprintln!("error: unknown command: {unknown}");
            print_usage();
            2
        }
    }
}

fn run_validate_or_preview(args: &[String], mode: StoreListingRenderMode) -> i32 {
    let Some(options) = parse_options(args, CommandKind::Report) else {
        return 2;
    };
    let report = match load_store_listing_report(&options.store_listing_options()) {
        Ok(report) => report,
        Err(error) => {
            eprintln!("error: {error}");
            return 1;
        }
    };

    print!("{}", render_report(&report, mode));
    if report.errors.is_empty() {
        0
    } else {
        1
    }
}

fn run_summary(args: &[String]) -> i32 {
    let Some(options) = parse_options(args, CommandKind::Summary) else {
        return 2;
    };
    let report = match load_store_listing_report(&options.store_listing_options()) {
        Ok(report) => report,
        Err(error) => {
            eprintln!("error: {error}");
            return 1;
        }
    };
    let language_filter = options.languages_argument.as_deref();
    let summary = render_github_summary(&report, &options.action, language_filter);

    if let Some(path) = &options.output {
        if let Err(error) = fs::write(path, summary) {
            eprintln!("error: {}: {error}", path.display());
            return 1;
        }
    } else {
        print!("{summary}");
    }

    0
}

fn run_submit(args: &[String]) -> i32 {
    let Some(options) = parse_options(args, CommandKind::Submit) else {
        return 2;
    };
    let report = match load_store_listing_report(&options.store_listing_options()) {
        Ok(report) => report,
        Err(error) => {
            eprintln!("error: {error}");
            return 1;
        }
    };

    println!("=== Easydict Store Listing Sync ===");
    println!("App ID: {}", report.app_id);
    println!("Mode: submit\n");
    println!("Languages: {}\n", report.target_languages.join(", "));

    let mut command_failed = false;
    for entry in &report.entries {
        match entry {
            StoreListingEntry::Missing { language, path } => {
                println!(
                    "WARNING: Listing file not found for language: {language} (expected: {})\n",
                    path.display()
                );
            }
            StoreListingEntry::Found {
                language,
                listing,
                validation,
                ..
            } => {
                println!("--- Processing: {language} ---");
                if !validation.errors.is_empty() {
                    println!("  ERRORS:");
                    for error in &validation.errors {
                        println!("    - {error}");
                    }
                }
                if !validation.warnings.is_empty() {
                    println!("  WARNINGS:");
                    for warning in &validation.warnings {
                        println!("    - {warning}");
                    }
                }
                if !validation.errors.is_empty() {
                    println!("  Skipping {language} due to validation errors\n");
                    continue;
                }

                println!("  Submitting listing update for {language}...");
                let payload = match build_msstore_payload(language, listing) {
                    Ok(payload) => payload,
                    Err(error) => {
                        eprintln!("error: {error}");
                        command_failed = true;
                        continue;
                    }
                };
                let payload_path = temp_payload_path(language);
                if let Err(error) = fs::write(&payload_path, payload) {
                    eprintln!("error: {}: {error}", payload_path.display());
                    command_failed = true;
                    continue;
                }

                let status = Command::new(&options.msstore_command)
                    .arg("submission")
                    .arg("update")
                    .arg(&report.app_id)
                    .arg("--jsonPayload")
                    .arg(&payload_path)
                    .status();
                let _ = fs::remove_file(&payload_path);

                match status {
                    Ok(status) if status.success() => {
                        println!("  Successfully updated listing for {language}\n");
                    }
                    Ok(status) => {
                        eprintln!(
                            "error: msstore submission update failed for {language} (exit code: {})",
                            status.code().unwrap_or(-1)
                        );
                        command_failed = true;
                    }
                    Err(error) => {
                        eprintln!("error: failed to run {}: {error}", options.msstore_command);
                        command_failed = true;
                    }
                }
            }
        }
    }

    println!("=== Summary ===");
    println!("Processed: {} language(s)", report.processed_count());
    println!("Errors: {}", report.errors.len());
    println!("Warnings: {}", report.warnings.len());
    if !report.errors.is_empty() {
        println!("\nAll Errors:");
        for error in &report.errors {
            println!("  - {error}");
        }
    }
    if !report.warnings.is_empty() {
        println!("\nAll Warnings:");
        for warning in &report.warnings {
            println!("  - {warning}");
        }
    }
    println!("\nDone!");

    if report.errors.is_empty() && !command_failed {
        0
    } else {
        1
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum CommandKind {
    Report,
    Summary,
    Submit,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct CliOptions {
    winstore_path: PathBuf,
    languages: Vec<String>,
    languages_argument: Option<String>,
    action: String,
    output: Option<PathBuf>,
    msstore_command: String,
}

impl CliOptions {
    fn store_listing_options(&self) -> StoreListingOptions {
        StoreListingOptions::new(self.winstore_path.clone(), self.languages.clone())
    }
}

fn parse_options(args: &[String], kind: CommandKind) -> Option<CliOptions> {
    let mut options = CliOptions {
        winstore_path: PathBuf::from(".winstore"),
        languages: Vec::new(),
        languages_argument: None,
        action: "validate".to_string(),
        output: None,
        msstore_command: "msstore".to_string(),
    };

    let mut index = 0;
    while index < args.len() {
        match args[index].as_str() {
            "--winstore-path" | "--winstore-root" => {
                let option = args[index].clone();
                let value = read_value(args, &mut index, &option)?;
                options.winstore_path = PathBuf::from(value);
            }
            "--languages" => {
                let value = read_value(args, &mut index, "--languages")?;
                options.languages = parse_language_filter(&value);
                options.languages_argument = Some(value);
            }
            "--action" if kind == CommandKind::Summary => {
                options.action = read_value(args, &mut index, "--action")?;
            }
            "--output" if kind == CommandKind::Summary => {
                let value = read_value(args, &mut index, "--output")?;
                options.output = Some(PathBuf::from(value));
            }
            "--msstore" if kind == CommandKind::Submit => {
                options.msstore_command = read_value(args, &mut index, "--msstore")?;
            }
            "-h" | "--help" => {
                print_usage();
                return None;
            }
            unknown => {
                eprintln!("error: unknown argument: {unknown}");
                print_usage();
                return None;
            }
        }
        index += 1;
    }

    Some(options)
}

fn read_value(args: &[String], index: &mut usize, option: &str) -> Option<String> {
    if *index + 1 >= args.len() {
        eprintln!("error: {option} requires a value");
        print_usage();
        return None;
    }

    *index += 1;
    Some(args[*index].clone())
}

fn temp_payload_path(language: &str) -> PathBuf {
    let language = language
        .chars()
        .map(|ch| {
            if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
                ch
            } else {
                '_'
            }
        })
        .collect::<String>();
    std::env::temp_dir().join(format!(
        "easydict-store-listing-{language}-{}.json",
        std::process::id()
    ))
}

fn print_usage() {
    println!("Usage: easydict_store_listings validate [--winstore-path <dir>] [--languages <csv>]");
    println!("       easydict_store_listings preview [--winstore-path <dir>] [--languages <csv>]");
    println!(
        "       easydict_store_listings submit [--winstore-path <dir>] [--languages <csv>] [--msstore <path>]"
    );
    println!(
        "       easydict_store_listings summary [--winstore-path <dir>] [--languages <csv>] [--action <name>] [--output <path>]"
    );
}

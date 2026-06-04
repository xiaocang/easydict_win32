use std::path::PathBuf;

use easydict_packager::{
    build_rust_helpers, download_and_extract_dotnet_runtime, zip_directory,
    BuildRustHelpersOptions, ExtractDotnetRuntimeOptions, PackageBrowserExtensionOptions,
    ZipDirectoryOptions,
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
        "zip-directory" => run_zip_directory(&args[1..]),
        "extract-dotnet-runtime" => run_extract_dotnet_runtime(&args[1..]),
        "build-rust-helpers" => run_build_rust_helpers(&args[1..]),
        "package-browser-extension" => run_package_browser_extension(&args[1..]),
        unknown => {
            eprintln!("error: unknown command: {unknown}");
            print_usage();
            2
        }
    }
}

fn run_package_browser_extension(args: &[String]) -> i32 {
    let mut extension_dir = None;
    let mut output_dir = None;
    let mut target = "All".to_string();

    let mut index = 0;
    while index < args.len() {
        match args[index].as_str() {
            "--extension-dir" => {
                let Some(value) = read_value(args, &mut index, "--extension-dir") else {
                    return 2;
                };
                extension_dir = Some(PathBuf::from(value));
            }
            "--output-dir" => {
                let Some(value) = read_value(args, &mut index, "--output-dir") else {
                    return 2;
                };
                output_dir = Some(PathBuf::from(value));
            }
            "--target" => {
                let Some(value) = read_value(args, &mut index, "--target") else {
                    return 2;
                };
                target = value;
            }
            "-h" | "--help" => {
                print_usage();
                return 2;
            }
            unknown => {
                eprintln!("error: unknown argument: {unknown}");
                print_usage();
                return 2;
            }
        }
        index += 1;
    }

    let Some(extension_dir) = extension_dir else {
        eprintln!("error: package-browser-extension requires --extension-dir");
        print_usage();
        return 2;
    };

    match easydict_packager::package_browser_extension(&PackageBrowserExtensionOptions {
        extension_dir,
        output_dir: output_dir.clone(),
        target,
    }) {
        Ok(outcome) => {
            println!(
                "Packaging Easydict OCR Browser Extension v{}",
                outcome.version
            );
            for package in outcome.packages {
                println!(
                    "  OK  {} -> {} ({:.1} KB)",
                    package.label,
                    package
                        .path
                        .file_name()
                        .and_then(|name| name.to_str())
                        .unwrap_or("<package>"),
                    bytes_to_kb(package.bytes)
                );
            }
            0
        }
        Err(error) => {
            eprintln!("error: {error}");
            1
        }
    }
}

fn run_build_rust_helpers(args: &[String]) -> i32 {
    let mut workspace = None;
    let mut platform = "x64".to_string();
    let mut configuration = "Release".to_string();
    let mut output_dir = None;

    let mut index = 0;
    while index < args.len() {
        match args[index].as_str() {
            "--workspace" => {
                let Some(value) = read_value(args, &mut index, "--workspace") else {
                    return 2;
                };
                workspace = Some(PathBuf::from(value));
            }
            "--platform" => {
                let Some(value) = read_value(args, &mut index, "--platform") else {
                    return 2;
                };
                platform = value;
            }
            "--configuration" => {
                let Some(value) = read_value(args, &mut index, "--configuration") else {
                    return 2;
                };
                configuration = value;
            }
            "--output-dir" => {
                let Some(value) = read_value(args, &mut index, "--output-dir") else {
                    return 2;
                };
                output_dir = Some(PathBuf::from(value));
            }
            "-h" | "--help" => {
                print_usage();
                return 2;
            }
            unknown => {
                eprintln!("error: unknown argument: {unknown}");
                print_usage();
                return 2;
            }
        }
        index += 1;
    }

    let Some(workspace) = workspace else {
        eprintln!("error: build-rust-helpers requires --workspace");
        print_usage();
        return 2;
    };
    let Some(output_dir) = output_dir else {
        eprintln!("error: build-rust-helpers requires --output-dir");
        print_usage();
        return 2;
    };

    match build_rust_helpers(&BuildRustHelpersOptions {
        rust_workspace: workspace,
        platform,
        configuration,
        output_dir: output_dir.clone(),
    }) {
        Ok(outcome) => {
            println!(
                "Built Rust helpers for {} ({})",
                outcome.cargo_target, outcome.profile_dir
            );
            for file_name in outcome.copied_files {
                println!("Copied {file_name} to {}", output_dir.display());
            }
            0
        }
        Err(error) => {
            eprintln!("error: {error}");
            1
        }
    }
}

fn run_extract_dotnet_runtime(args: &[String]) -> i32 {
    let mut rid = None;
    let mut output_dir = None;
    let mut version = "8.0.11".to_string();

    let mut index = 0;
    while index < args.len() {
        match args[index].as_str() {
            "--rid" => {
                let Some(value) = read_value(args, &mut index, "--rid") else {
                    return 2;
                };
                rid = Some(value);
            }
            "--output-dir" => {
                let Some(value) = read_value(args, &mut index, "--output-dir") else {
                    return 2;
                };
                output_dir = Some(PathBuf::from(value));
            }
            "--version" => {
                let Some(value) = read_value(args, &mut index, "--version") else {
                    return 2;
                };
                version = value;
            }
            "-h" | "--help" => {
                print_usage();
                return 2;
            }
            unknown => {
                eprintln!("error: unknown argument: {unknown}");
                print_usage();
                return 2;
            }
        }
        index += 1;
    }

    let Some(rid) = rid else {
        eprintln!("error: extract-dotnet-runtime requires --rid");
        print_usage();
        return 2;
    };
    let Some(output_dir) = output_dir else {
        eprintln!("error: extract-dotnet-runtime requires --output-dir");
        print_usage();
        return 2;
    };
    if !matches!(rid.as_str(), "win-x64" | "win-arm64") {
        eprintln!("error: unsupported .NET runtime RID: {rid}");
        return 1;
    }

    println!(
        "[ExtractDotnetRuntime] Downloading {}",
        easydict_packager::dotnet_runtime_url(&version, &rid)
    );
    println!(
        "[ExtractDotnetRuntime] Extracting to {}",
        output_dir.display()
    );
    match download_and_extract_dotnet_runtime(&ExtractDotnetRuntimeOptions {
        rid,
        output_dir: output_dir.clone(),
        version,
    }) {
        Ok(outcome) => {
            println!(
                "[ExtractDotnetRuntime] Bundled runtime version: {}",
                outcome.bundled_version
            );
            println!(
                "[ExtractDotnetRuntime] Bundle size: {:.1} MB",
                bytes_to_mb(outcome.total_bytes)
            );
            0
        }
        Err(error) => {
            eprintln!("error: {error}");
            1
        }
    }
}

fn run_zip_directory(args: &[String]) -> i32 {
    let mut source_dir = None;
    let mut destination_zip = None;
    let mut exclude_extensions = Vec::new();

    let mut index = 0;
    while index < args.len() {
        match args[index].as_str() {
            "--source" => {
                let Some(value) = read_value(args, &mut index, "--source") else {
                    return 2;
                };
                source_dir = Some(PathBuf::from(value));
            }
            "--destination" => {
                let Some(value) = read_value(args, &mut index, "--destination") else {
                    return 2;
                };
                destination_zip = Some(PathBuf::from(value));
            }
            "--exclude-extension" => {
                let Some(value) = read_value(args, &mut index, "--exclude-extension") else {
                    return 2;
                };
                exclude_extensions.push(value);
            }
            "-h" | "--help" => {
                print_usage();
                return 2;
            }
            unknown => {
                eprintln!("error: unknown argument: {unknown}");
                print_usage();
                return 2;
            }
        }
        index += 1;
    }

    let Some(source_dir) = source_dir else {
        eprintln!("error: zip-directory requires --source");
        print_usage();
        return 2;
    };
    let Some(destination_zip) = destination_zip else {
        eprintln!("error: zip-directory requires --destination");
        print_usage();
        return 2;
    };

    let options = ZipDirectoryOptions {
        source_dir,
        destination_zip,
        exclude_extensions,
    };
    match zip_directory(&options) {
        Ok(outcome) => {
            println!(
                "Created ZIP: {} ({} files, {} empty dirs, {} skipped, {} bytes)",
                options.destination_zip.display(),
                outcome.file_count,
                outcome.directory_count,
                outcome.skipped_count,
                outcome.bytes_written
            );
            0
        }
        Err(error) => {
            eprintln!("error: {error}");
            1
        }
    }
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

fn print_usage() {
    println!(
        "Usage: easydict_packager zip-directory --source <dir> --destination <zip> [--exclude-extension <ext> ...]"
    );
    println!(
        "       easydict_packager extract-dotnet-runtime --rid win-x64|win-arm64 --output-dir <dir> [--version <ver>]"
    );
    println!(
        "       easydict_packager build-rust-helpers --workspace <rs-dir> --platform x64|x86|arm64 --configuration Debug|Release --output-dir <dir>"
    );
    println!(
        "       easydict_packager package-browser-extension --extension-dir <dir> [--target Chrome|Firefox|All] [--output-dir <dir>]"
    );
}

fn bytes_to_mb(bytes: u64) -> f64 {
    bytes as f64 / 1024.0 / 1024.0
}

fn bytes_to_kb(bytes: u64) -> f64 {
    bytes as f64 / 1024.0
}

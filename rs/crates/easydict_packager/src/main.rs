use std::path::PathBuf;

use easydict_packager::{
    build_rust_helpers, pack_rs_portable, validate_rs_portable_payload, BuildRustHelpersOptions,
    PackRustPortableOptions, PackageBrowserExtensionOptions, ValidateRustPortableOptions,
};
#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
use easydict_packager::{
    download_and_extract_dotnet_runtime, zip_directory, ExtractDotnetRuntimeOptions,
    PackageRuntimeProfile, ZipDirectoryOptions,
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
        #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
        "zip-directory" => run_zip_directory(&args[1..]),
        #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
        "extract-dotnet-runtime" => run_extract_dotnet_runtime(&args[1..]),
        "build-rust-helpers" => run_build_rust_helpers(&args[1..]),
        "package-browser-extension" => run_package_browser_extension(&args[1..]),
        "validate-rs-portable" => run_validate_rs_portable(&args[1..]),
        "pack-rs-portable" => run_pack_rs_portable(&args[1..]),
        unknown => {
            eprintln!("error: unknown command: {unknown}");
            print_usage();
            2
        }
    }
}

fn run_pack_rs_portable(args: &[String]) -> i32 {
    let mut workspace = None;
    let mut platform = "x64".to_string();
    let mut configuration = "Release".to_string();
    let mut output_root = None;
    let mut package_version = None;
    let mut create_zip = true;

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
            "--output-root" => {
                let Some(value) = read_value(args, &mut index, "--output-root") else {
                    return 2;
                };
                output_root = Some(PathBuf::from(value));
            }
            "--package-version" => {
                let Some(value) = read_value(args, &mut index, "--package-version") else {
                    return 2;
                };
                package_version = Some(value);
            }
            "--no-zip" => create_zip = false,
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
        eprintln!("error: pack-rs-portable requires --workspace");
        print_usage();
        return 2;
    };
    let output_root = output_root.unwrap_or_else(|| workspace.join("dist"));

    match pack_rs_portable(&PackRustPortableOptions {
        rust_workspace: workspace,
        platform,
        configuration,
        output_root,
        package_version,
        create_zip,
    }) {
        Ok(outcome) => {
            println!(
                "Rust portable payload OK: {} ({} entries checked)",
                outcome.package_dir.display(),
                outcome.directory_validation_entries
            );
            if let (Some(zip_path), Some(entries)) =
                (outcome.zip_path.as_ref(), outcome.zip_validation_entries)
            {
                println!(
                    "Rust portable ZIP OK: {} ({} entries checked)",
                    zip_path.display(),
                    entries
                );
            }
            println!("Rust portable package: {}", outcome.package_dir.display());
            if let Some(zip_path) = outcome.zip_path.as_ref() {
                println!("Created Rust portable ZIP: {}", zip_path.display());
            }
            println!("Files: {}", outcome.file_count);
            println!("Size:  {:.2} MB", bytes_to_mb(outcome.total_bytes));
            0
        }
        Err(error) => {
            eprintln!("error: {error}");
            1
        }
    }
}

fn run_validate_rs_portable(args: &[String]) -> i32 {
    let mut package_path = None;

    let mut index = 0;
    while index < args.len() {
        match args[index].as_str() {
            "--package" => {
                let Some(value) = read_value(args, &mut index, "--package") else {
                    return 2;
                };
                package_path = Some(PathBuf::from(value));
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

    let Some(package_path) = package_path else {
        eprintln!("error: validate-rs-portable requires --package");
        print_usage();
        return 2;
    };

    match validate_rs_portable_payload(&ValidateRustPortableOptions {
        package_path: package_path.clone(),
    }) {
        Ok(outcome) => {
            println!(
                "Rust portable payload OK: {} ({} entries checked)",
                package_path.display(),
                outcome.checked_entries
            );
            0
        }
        Err(error) => {
            eprintln!("error: {error}");
            1
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
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    let mut include_legacy_registrar_alias = false;
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    let mut runtime_profile = None;

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
            "--include-legacy-registrar-alias" => {
                #[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
                {
                    eprintln!(
                        "error: --include-legacy-registrar-alias requires the hybrid-dotnet-runtime-packaging feature"
                    );
                    return 2;
                }
                #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
                {
                    include_legacy_registrar_alias = true;
                }
            }
            #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
            "--runtime-profile" => {
                let Some(value) = read_value(args, &mut index, "--runtime-profile") else {
                    return 2;
                };
                let Some(profile) = PackageRuntimeProfile::parse_explicit(&value) else {
                    eprintln!("error: unsupported runtime profile: {value}");
                    return 2;
                };
                runtime_profile = Some(profile);
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
        #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
        include_legacy_registrar_alias,
        #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
        runtime_profile,
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

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
fn run_extract_dotnet_runtime(args: &[String]) -> i32 {
    let mut rid = None;
    let mut output_dir = None;
    let mut version = "8.0.11".to_string();
    let mut runtime_profile = None;

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
            "--runtime-profile" => {
                let Some(value) = read_value(args, &mut index, "--runtime-profile") else {
                    return 2;
                };
                let Some(profile) = PackageRuntimeProfile::parse_explicit(&value) else {
                    eprintln!("error: unsupported runtime profile: {value}");
                    return 2;
                };
                runtime_profile = Some(profile);
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
    let Some(runtime_profile) = runtime_profile else {
        eprintln!(
            "error: extract-dotnet-runtime requires explicit --runtime-profile hybrid; rs portable packages must not bundle .NET runtime"
        );
        return 1;
    };
    if runtime_profile != PackageRuntimeProfile::Hybrid {
        eprintln!(
            "error: extract-dotnet-runtime requires explicit --runtime-profile hybrid; rs portable packages must not bundle .NET runtime"
        );
        return 1;
    }

    match download_and_extract_dotnet_runtime(&ExtractDotnetRuntimeOptions {
        rid,
        output_dir: output_dir.clone(),
        version,
        runtime_profile,
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

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
    for line in packager_usage_lines() {
        println!("{line}");
    }
}

#[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
fn packager_usage_lines() -> &'static [&'static str] {
    &[
        "Usage: easydict_packager build-rust-helpers --workspace <rs-dir> --platform x64|x86|arm64 --configuration Debug|Release --output-dir <dir>    # Rust helper executables only; legacy registrar alias requires hybrid feature",
        "       easydict_packager package-browser-extension --extension-dir <dir> [--target Chrome|Firefox|All] [--output-dir <dir>]",
        "       easydict_packager validate-rs-portable --package <dir-or-zip>",
        "       easydict_packager pack-rs-portable --workspace <rs-dir> --platform x64|x86|arm64 --configuration Debug|Release [--output-root <dir>] [--package-version <ver>] [--no-zip]",
    ]
}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
fn packager_usage_lines() -> &'static [&'static str] {
    &[
        "Usage: easydict_packager zip-directory --source <dir> --destination <zip> [--exclude-extension <ext> ...]    # legacy/hybrid ZIP helper only; never used by rs portable",
        "       easydict_packager extract-dotnet-runtime --rid win-x64|win-arm64 --output-dir <dir> [--version <ver>] --runtime-profile hybrid    # hybrid/coexistence packaging only; never used by rs portable",
        "       easydict_packager build-rust-helpers --workspace <rs-dir> --platform x64|x86|arm64 --configuration Debug|Release --output-dir <dir> [--runtime-profile hybrid --include-legacy-registrar-alias]    # legacy/hybrid alias only; never used by rs portable",
        "       easydict_packager package-browser-extension --extension-dir <dir> [--target Chrome|Firefox|All] [--output-dir <dir>]",
        "       easydict_packager validate-rs-portable --package <dir-or-zip>",
        "       easydict_packager pack-rs-portable --workspace <rs-dir> --platform x64|x86|arm64 --configuration Debug|Release [--output-root <dir>] [--package-version <ver>] [--no-zip]",
    ]
}

fn bytes_to_mb(bytes: u64) -> f64 {
    bytes as f64 / 1024.0 / 1024.0
}

fn bytes_to_kb(bytes: u64) -> f64 {
    bytes as f64 / 1024.0
}

#[cfg(test)]
mod tests {
    use easydict_packager::PackageRuntimeProfile;
    use std::fs;
    use std::path::Path;
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    use std::sync::Mutex;

    use super::*;

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

    #[test]
    #[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
    fn default_build_does_not_offer_dotnet_runtime_extraction_command() {
        let code = run(vec!["extract-dotnet-runtime".to_string()]);

        assert_eq!(code, 2);
        assert!(
            !packager_usage_lines()
                .join("\n")
                .contains("extract-dotnet-runtime"),
            "default packager usage should not expose the hybrid runtime extraction command"
        );
    }

    #[test]
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    fn extract_dotnet_runtime_rejects_missing_runtime_profile_before_download() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_runtime_profile_environment();

        let code = run_extract_dotnet_runtime(&[
            "--rid".to_string(),
            "win-x64".to_string(),
            "--output-dir".to_string(),
            "unused".to_string(),
        ]);

        assert_eq!(code, 1);
        snapshot.restore();
    }

    #[test]
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    fn extract_dotnet_runtime_rejects_explicit_rust_only_profile_before_download() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_runtime_profile_environment();

        let code = run_extract_dotnet_runtime(&[
            "--rid".to_string(),
            "win-x64".to_string(),
            "--output-dir".to_string(),
            "unused".to_string(),
            "--runtime-profile".to_string(),
            "rust-only".to_string(),
        ]);

        assert_eq!(code, 1);
        snapshot.restore();
    }

    #[test]
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    fn extract_dotnet_runtime_rejects_rust_only_environment_even_with_explicit_hybrid() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_runtime_profile_environment();
        std::env::set_var("RUNTIME_PROFILE", "rust-only");

        let code = run_extract_dotnet_runtime(&[
            "--rid".to_string(),
            "win-arm64".to_string(),
            "--output-dir".to_string(),
            "unused".to_string(),
            "--runtime-profile".to_string(),
            "hybrid".to_string(),
        ]);

        assert_eq!(code, 1);
        snapshot.restore();
    }

    #[test]
    #[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
    fn default_usage_hides_dotnet_runtime_extraction_command() {
        let usage = packager_usage_lines().join("\n");

        assert!(
            !usage.contains("extract-dotnet-runtime"),
            "default packager usage must not expose .NET runtime extraction:\n{usage}"
        );
        assert!(
            !usage.contains("zip-directory"),
            "default packager usage must not expose the legacy/hybrid standalone ZIP helper:\n{usage}"
        );
        assert!(
            usage.contains("pack-rs-portable --workspace"),
            "default usage should keep the rs portable command visible:\n{usage}"
        );
        assert!(
            !usage.contains("--include-legacy-registrar-alias"),
            "default usage must not expose the legacy BrowserHostRegistrar alias flag:\n{usage}"
        );
        assert!(
            !usage.contains("--runtime-profile"),
            "default helper usage must not expose hybrid runtime-profile knobs:\n{usage}"
        );
        assert!(
            usage.contains("legacy registrar alias requires hybrid feature"),
            "default usage should explain that the legacy alias is hybrid-feature-only:\n{usage}"
        );
    }

    #[test]
    #[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
    fn default_build_rejects_legacy_registrar_alias_flag_before_workspace() {
        let code = run(vec![
            "build-rust-helpers".to_string(),
            "--include-legacy-registrar-alias".to_string(),
        ]);

        assert_eq!(code, 2);
    }

    #[test]
    #[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
    fn default_build_rejects_runtime_profile_flag_before_workspace() {
        let code = run(vec![
            "build-rust-helpers".to_string(),
            "--runtime-profile".to_string(),
            "hybrid".to_string(),
        ]);

        assert_eq!(code, 2);
    }

    #[test]
    fn pack_rs_portable_rejects_runtime_profile_flag_before_workspace() {
        let code = run(vec![
            "pack-rs-portable".to_string(),
            "--runtime-profile".to_string(),
            "hybrid".to_string(),
        ]);

        assert_eq!(
            code, 2,
            "first-release rs portable packaging must not accept hybrid runtime-profile knobs"
        );
    }

    #[test]
    #[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
    fn default_build_rejects_legacy_zip_directory_command_before_args() {
        let code = run(vec!["zip-directory".to_string()]);

        assert_eq!(code, 2);
        assert!(
            !packager_usage_lines().join("\n").contains("zip-directory"),
            "default usage must keep zip-directory hidden"
        );
    }

    #[test]
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    fn usage_marks_dotnet_runtime_extraction_as_hybrid_only_not_rs_portable() {
        let usage = packager_usage_lines().join("\n");

        assert!(
            usage.contains(
                "extract-dotnet-runtime --rid win-x64|win-arm64 --output-dir <dir> [--version <ver>] --runtime-profile hybrid"
            ),
            "usage should require explicit hybrid profile for runtime extraction:\n{usage}"
        );
        assert!(
            usage.contains("hybrid/coexistence packaging only"),
            "usage should label runtime extraction as hybrid-only:\n{usage}"
        );
        assert!(
            usage.contains("never used by rs portable"),
            "usage should steer rs portable callers away from .NET runtime extraction:\n{usage}"
        );
        assert!(
            usage.contains("[--runtime-profile hybrid --include-legacy-registrar-alias]"),
            "hybrid-feature usage should keep the legacy alias behind the explicit hybrid profile pair:\n{usage}"
        );
        assert!(
            usage.contains("legacy/hybrid alias only; never used by rs portable"),
            "hybrid-feature usage should label the legacy alias as non-portable:\n{usage}"
        );
        assert!(
            !usage.contains("[--runtime-profile hybrid|rust-only]"),
            "usage must not suggest rust-only can extract .NET runtime:\n{usage}"
        );
        assert!(
            usage.contains("zip-directory --source <dir> --destination <zip> [--exclude-extension <ext> ...]    # legacy/hybrid ZIP helper only; never used by rs portable"),
            "hybrid-feature usage should label zip-directory as non-portable legacy packaging:\n{usage}"
        );
    }

    #[test]
    fn readme_keeps_runtime_and_generic_zip_helpers_out_of_rs_portable_checks() {
        let readme = fs::read_to_string(
            Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("..")
                .join("..")
                .join("README.md"),
        )
        .expect("rs README should be readable");
        let portable_section = readme
            .split("Rust portable package checks:")
            .nth(1)
            .and_then(|rest| {
                rest.split("Standalone packaging helper diagnostics:")
                    .next()
            })
            .expect("README should contain portable and standalone helper sections");

        assert!(
            !portable_section.contains("extract-dotnet-runtime"),
            "rs portable checks must not list .NET runtime extraction:\n{portable_section}"
        );
        assert!(
            !portable_section.contains("zip-directory --source"),
            "rs portable checks must use pack-rs-portable, not the generic zip helper:\n{portable_section}"
        );
        assert!(
            !portable_section.contains("build-rust-helpers --workspace"),
            "rs portable checks must not split helper builds from pack-rs-portable:\n{portable_section}"
        );
        assert!(
            portable_section.contains("pack-rs-portable --workspace"),
            "rs portable checks should point at the Rust-owned portable packager:\n{portable_section}"
        );
        assert!(
            readme.contains("Hybrid-only retained runtime checks:"),
            "README should keep .NET runtime extraction in a separate hybrid-only section"
        );
        assert!(
            readme.contains(
                "cargo run -p easydict_packager --features hybrid-dotnet-runtime-packaging -- zip-directory"
            ),
            "README should keep standalone zip-directory behind the hybrid packager feature"
        );
        assert!(
            readme.contains("Standalone packaging helper diagnostics:"),
            "README should keep generic helper diagnostics outside the rs portable section"
        );
        let normalized_readme = readme.split_whitespace().collect::<Vec<_>>().join(" ");
        assert!(
            normalized_readme.contains("never part of the rs portable package flow"),
            "README should explicitly state .NET runtime extraction is not for rs portable"
        );
        assert!(
            normalized_readme.contains("not the first rs portable assembly path"),
            "README should explicitly state generic helper commands are not the rs portable path"
        );
    }

    #[test]
    fn root_readmes_recommend_rs_portable_without_dotnet_runtime() {
        let repository_root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("..")
            .join("..");
        let readme = fs::read_to_string(repository_root.join("README.md"))
            .expect("root README should be readable");
        let readme_zh = fs::read_to_string(repository_root.join("README_ZH.md"))
            .expect("root Chinese README should be readable");

        let portable_section = readme
            .split("#### Portable Version (Recommended)")
            .nth(1)
            .and_then(|rest| rest.split("#### Legacy/Hybrid .NET Package").next())
            .expect("root README should contain portable and legacy package sections");
        assert!(
            portable_section.contains("easydict-rs-portable-vX.Y.Z-win-x64.zip"),
            "recommended portable section should point at the rs portable ZIP:\n{portable_section}"
        );
        assert!(
            portable_section.contains("Easydict.Rust.exe"),
            "recommended portable section should run the Rust GUI entrypoint:\n{portable_section}"
        );
        assert!(
            portable_section.contains("does not include the .NET runtime"),
            "recommended portable section should state there is no bundled .NET runtime:\n{portable_section}"
        );
        assert!(
            !portable_section.contains("Easydict.WinUI.exe")
                && !portable_section.contains(".NET runtime included"),
            "recommended portable section must not describe the legacy .NET package:\n{portable_section}"
        );

        let portable_section_zh = readme_zh
            .split("#### 便携版（推荐）")
            .nth(1)
            .and_then(|rest| rest.split("#### Legacy/Hybrid .NET 包").next())
            .expect("root Chinese README should contain portable and legacy package sections");
        assert!(
            portable_section_zh.contains("easydict-rs-portable-vX.Y.Z-win-x64.zip"),
            "Chinese portable section should point at the rs portable ZIP:\n{portable_section_zh}"
        );
        assert!(
            portable_section_zh.contains("Easydict.Rust.exe"),
            "Chinese portable section should run the Rust GUI entrypoint:\n{portable_section_zh}"
        );
        assert!(
            portable_section_zh.contains("不包含 .NET 运行时"),
            "Chinese portable section should state there is no bundled .NET runtime:\n{portable_section_zh}"
        );
        assert!(
            !portable_section_zh.contains("Easydict.WinUI.exe")
                && !portable_section_zh.contains("内含 .NET 运行时"),
            "Chinese portable section must not describe the legacy .NET package:\n{portable_section_zh}"
        );
    }

    #[test]
    fn explicit_runtime_profile_parser_accepts_hybrid_and_rust_only_aliases() {
        assert_eq!(
            PackageRuntimeProfile::parse_explicit("hybrid"),
            Some(PackageRuntimeProfile::Hybrid)
        );
        assert_eq!(
            PackageRuntimeProfile::parse_explicit("RustOnly"),
            Some(PackageRuntimeProfile::RustOnly)
        );
        assert_eq!(
            PackageRuntimeProfile::parse_explicit("rust_only"),
            Some(PackageRuntimeProfile::RustOnly)
        );
        assert_eq!(PackageRuntimeProfile::parse_explicit("dotnet"), None);
    }

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    struct EnvironmentSnapshot {
        easydict_runtime_profile: Option<String>,
        runtime_profile: Option<String>,
    }

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    impl EnvironmentSnapshot {
        fn capture() -> Self {
            Self {
                easydict_runtime_profile: std::env::var("EASYDICT_RUNTIME_PROFILE").ok(),
                runtime_profile: std::env::var("RUNTIME_PROFILE").ok(),
            }
        }

        fn restore(self) {
            restore_environment_value("EASYDICT_RUNTIME_PROFILE", self.easydict_runtime_profile);
            restore_environment_value("RUNTIME_PROFILE", self.runtime_profile);
        }
    }

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    fn clear_runtime_profile_environment() {
        std::env::remove_var("EASYDICT_RUNTIME_PROFILE");
        std::env::remove_var("RUNTIME_PROFILE");
    }

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    fn restore_environment_value(name: &str, value: Option<String>) {
        if let Some(value) = value {
            std::env::set_var(name, value);
        } else {
            std::env::remove_var(name);
        }
    }
}

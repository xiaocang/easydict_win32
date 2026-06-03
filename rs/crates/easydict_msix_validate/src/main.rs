use std::path::PathBuf;

use easydict_msix_validate::{
    dedupe_worker_shared_files, fix_msix_min_version, validate_msix, verify_bundle_min_version,
    BundleMinVersionOptions, FixMinVersionOptions, FixMinVersionOutcome, MsixValidationOptions,
    PackageRuntimeProfile, WorkerSharedDedupeStatus, DEFAULT_EXPECTED_NAME, DEFAULT_MIN_VERSION,
};

fn main() {
    std::process::exit(run(std::env::args().skip(1).collect()));
}

fn run(args: Vec<String>) -> i32 {
    if args.is_empty() || args[0] == "-h" || args[0] == "--help" {
        print_usage();
        return 2;
    }

    if args[0] == "fix-minversion" {
        return run_fix_minversion(&args[1..]);
    }
    if args[0] == "verify-bundle-minversion" {
        return run_verify_bundle_minversion(&args[1..]);
    }
    if args[0] == "dedupe-worker-shared" {
        return run_dedupe_worker_shared(&args[1..]);
    }

    run_validate(&args)
}

fn run_validate(args: &[String]) -> i32 {
    let msix_path = PathBuf::from(&args[0]);
    let mut options = MsixValidationOptions::default();

    let mut index = 1;
    while index < args.len() {
        match args[index].as_str() {
            "--expected-name" => {
                let Some(value) = read_value(&args, &mut index, "--expected-name") else {
                    return 2;
                };
                options.expected_name = value;
            }
            "--expected-publisher" => {
                let Some(value) = read_value(&args, &mut index, "--expected-publisher") else {
                    return 2;
                };
                options.expected_publisher = value;
            }
            "--min-version" => {
                let Some(value) = read_value(&args, &mut index, "--min-version") else {
                    return 2;
                };
                options.min_version = value;
            }
            "--runtime-profile" => {
                let Some(value) = read_value(&args, &mut index, "--runtime-profile") else {
                    return 2;
                };
                let Some(profile) = PackageRuntimeProfile::parse(&value) else {
                    eprintln!(
                        "error: --runtime-profile must be 'hybrid' or 'rust-only', got '{value}'"
                    );
                    print_usage();
                    return 2;
                };
                options.runtime_profile = profile;
            }
            "--rust-only" => options.runtime_profile = PackageRuntimeProfile::RustOnly,
            "--allow-unsigned" => options.allow_unsigned = true,
            unknown => {
                eprintln!("error: unknown argument: {unknown}");
                print_usage();
                return 2;
            }
        }
        index += 1;
    }

    if !msix_path.exists() {
        eprintln!("error: MSIX not found: {}", msix_path.display());
        return 2;
    }

    match validate_msix(&msix_path, &options) {
        Ok(()) => {
            println!("  [pass] PackageFamilyNameValidator");
            println!("  [pass] PackageMinimumVersionValidator");
            if options.allow_unsigned {
                println!("  [skip] PackageCertificateEkuValidator (--allow-unsigned)");
            } else {
                println!("  [pass] PackageCertificateEkuValidator");
            }
            println!("  [pass] PackagePayloadLayoutValidator");
            println!(
                "  [info] RuntimeProfile: {}",
                options.runtime_profile.as_str()
            );
            println!("OK: all checks passed for {}", msix_path.display());
            0
        }
        Err(failures) => {
            for (name, error) in &failures {
                if *name == "open" {
                    eprintln!("error: {error}");
                } else {
                    eprintln!("  [FAIL] {name}: {error}");
                }
            }
            if failures.iter().any(|(name, _)| *name == "open") {
                2
            } else {
                eprintln!(
                    "FAIL: {} check(s) failed for {}",
                    failures.len(),
                    msix_path.display()
                );
                1
            }
        }
    }
}

fn run_verify_bundle_minversion(args: &[String]) -> i32 {
    if args.is_empty() || args[0] == "-h" || args[0] == "--help" {
        print_usage();
        return 2;
    }

    let bundle_path = PathBuf::from(&args[0]);
    let mut options = BundleMinVersionOptions::default();

    let mut index = 1;
    while index < args.len() {
        match args[index].as_str() {
            "--required-min-version" => {
                let Some(value) = read_value(args, &mut index, "--required-min-version") else {
                    return 2;
                };
                options.required_min_version = value;
            }
            unknown => {
                eprintln!("error: unknown argument: {unknown}");
                print_usage();
                return 2;
            }
        }
        index += 1;
    }

    if !bundle_path.exists() {
        eprintln!("error: MSIX bundle not found: {}", bundle_path.display());
        return 2;
    }

    match verify_bundle_min_version(&bundle_path, &options) {
        Ok(report) => {
            if report.has_bundle_manifest {
                println!("  [info] AppxMetadata/AppxBundleManifest.xml present");
            } else {
                println!("  [info] AppxMetadata/AppxBundleManifest.xml not present");
            }
            for package in &report.packages {
                println!("  [pass] {}", package.path);
                if let Some(name) = &package.target_device_family_name {
                    println!("         Name: {name}");
                }
                println!("         MinVersion: {}", package.min_version);
                if let Some(max_version_tested) = &package.max_version_tested {
                    println!("         MaxVersionTested: {max_version_tested}");
                }
            }
            println!(
                "OK: {} package(s) in {} satisfy MinVersion >= {}",
                report.packages.len(),
                bundle_path.display(),
                options.required_min_version
            );
            0
        }
        Err(error) => {
            eprintln!("error: {error}");
            1
        }
    }
}

fn run_dedupe_worker_shared(args: &[String]) -> i32 {
    if args.is_empty() || args[0] == "-h" || args[0] == "--help" {
        print_usage();
        return 2;
    }
    if args.len() > 1 {
        eprintln!("error: unknown argument: {}", args[1]);
        print_usage();
        return 2;
    }

    let publish_dir = PathBuf::from(&args[0]);
    match dedupe_worker_shared_files(&publish_dir) {
        Ok(outcome) => {
            match &outcome.status {
                WorkerSharedDedupeStatus::NoWorkersDirectory { path } => {
                    println!(
                        "[DedupeWorkerShared] No workers directory found: {}",
                        path.display()
                    );
                    return 0;
                }
                WorkerSharedDedupeStatus::FewerThanTwoWorkerDirectories => {
                    println!("[DedupeWorkerShared] Fewer than two worker dirs found; skipping.");
                    return 0;
                }
                WorkerSharedDedupeStatus::Completed => {}
            }

            for file_name in &outcome.skipped_different_hashes {
                println!("[DedupeWorkerShared] Skipping {file_name} because hashes differ.");
            }
            for file in &outcome.shared_files {
                println!(
                    "[DedupeWorkerShared] Shared {} from {} workers.",
                    file.file_name, file.worker_count
                );
            }

            println!(
                "[DedupeWorkerShared] Moved {} shared files; estimated uncompressed savings: {:.1} MB",
                outcome.moved_count,
                bytes_to_mb(outcome.saved_bytes)
            );
            println!("[DedupeWorkerShared] Worker size summary:");
            for size in &outcome.worker_sizes {
                println!("  {:<8} {:>8.1} MB", size.name, bytes_to_mb(size.bytes));
            }
            0
        }
        Err(error) => {
            eprintln!("error: {error}");
            1
        }
    }
}

fn run_fix_minversion(args: &[String]) -> i32 {
    if args.is_empty() || args[0] == "-h" || args[0] == "--help" {
        print_usage();
        return 2;
    }

    let msix_path = PathBuf::from(&args[0]);
    let mut options = FixMinVersionOptions::default();

    let mut index = 1;
    while index < args.len() {
        match args[index].as_str() {
            "--min-version" => {
                let Some(value) = read_value(args, &mut index, "--min-version") else {
                    return 2;
                };
                options.min_version = value;
            }
            "--makeappx" => {
                let Some(value) = read_value(args, &mut index, "--makeappx") else {
                    return 2;
                };
                options.makeappx_path = Some(PathBuf::from(value));
            }
            unknown => {
                eprintln!("error: unknown argument: {unknown}");
                print_usage();
                return 2;
            }
        }
        index += 1;
    }

    if !msix_path.exists() {
        eprintln!("error: MSIX not found: {}", msix_path.display());
        return 2;
    }

    match fix_msix_min_version(&msix_path, &options) {
        Ok(FixMinVersionOutcome::NoChangeNeeded { current, required }) => {
            println!("Current MinVersion in MSIX: {current}");
            println!("Required MinVersion: {required}");
            println!("MinVersion is OK: {current} >= {required} (no fix required)");
            0
        }
        Ok(FixMinVersionOutcome::Repacked { previous, required }) => {
            println!("Current MinVersion in MSIX: {previous}");
            println!("Required MinVersion: {required}");
            println!("::warning::MinVersion {previous} is too low, fixed to {required}");
            println!("Re-packed MSIX with MinVersion={required}");
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

fn bytes_to_mb(bytes: u64) -> f64 {
    bytes as f64 / 1024.0 / 1024.0
}

fn print_usage() {
    println!(
        "Usage: easydict_msix_validate <path-to-msix> [--expected-name <name>] [--expected-publisher <publisher>] [--min-version <ver>] [--runtime-profile hybrid|rust-only] [--rust-only] [--allow-unsigned]"
    );
    println!(
        "       easydict_msix_validate fix-minversion <path-to-msix> [--min-version <ver>] [--makeappx <path>]"
    );
    println!(
        "       easydict_msix_validate verify-bundle-minversion <path-to-msixbundle> [--required-min-version <ver>]"
    );
    println!("       easydict_msix_validate dedupe-worker-shared <publish-dir>");
    println!("  defaults: name={DEFAULT_EXPECTED_NAME}, min-version={DEFAULT_MIN_VERSION}");
    println!(
        "  --runtime-profile rust-only: reject retained .NET workers and bundled .NET runtime payloads"
    );
    println!("  --rust-only: shortcut for --runtime-profile rust-only");
    println!(
        "  --allow-unsigned: skip the AppxSignature.p7x check (use for the release workflow which builds unsigned bundles)"
    );
}

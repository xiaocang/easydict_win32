use std::path::PathBuf;

use easydict_msix_validate::{
    dedupe_worker_shared_files, fix_msix_min_version, prepare_package_inputs, validate_msix,
    verify_bundle_min_version, BundleMinVersionOptions, FixMinVersionOptions, FixMinVersionOutcome,
    MsixValidationOptions, PackageRuntimeProfile, PreparePackageInputsOptions,
    WorkerSharedDedupeStatus, DEFAULT_EXPECTED_NAME, DEFAULT_MIN_VERSION,
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
    if args[0] == "prepare-package-inputs" {
        return run_prepare_package_inputs(&args[1..]);
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
            "--runtime-profile" => {
                let Some(value) = read_value(args, &mut index, "--runtime-profile") else {
                    return 2;
                };
                let Some(profile) = PackageRuntimeProfile::parse(&value) else {
                    eprintln!(
                        "error: --runtime-profile must be 'hybrid' or 'rust-only', got '{value}'"
                    );
                    print_usage();
                    return 2;
                };
                options.runtime_profile = Some(profile);
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
            if let Some(runtime_profile) = options.runtime_profile {
                println!(
                    "  [info] Nested package runtime payload profile: {}",
                    runtime_profile.as_str()
                );
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
        print_hybrid_usage();
        return 2;
    }

    let mut publish_dir = None;
    let mut runtime_profile = None;
    let mut index = 0;
    while index < args.len() {
        match args[index].as_str() {
            "--runtime-profile" => {
                if index + 1 >= args.len() {
                    eprintln!("error: --runtime-profile requires a value");
                    print_hybrid_usage();
                    return 2;
                }
                index += 1;
                let value = args[index].clone();
                let Some(profile) = PackageRuntimeProfile::parse(&value) else {
                    eprintln!(
                        "error: --runtime-profile must be 'hybrid' for dedupe-worker-shared, got '{value}'"
                    );
                    print_hybrid_usage();
                    return 2;
                };
                runtime_profile = Some(profile);
            }
            "--rust-only" => runtime_profile = Some(PackageRuntimeProfile::RustOnly),
            unknown if unknown.starts_with('-') => {
                eprintln!("error: unknown argument: {unknown}");
                print_hybrid_usage();
                return 2;
            }
            value => {
                if publish_dir.is_some() {
                    eprintln!("error: unexpected extra path: {value}");
                    print_hybrid_usage();
                    return 2;
                }
                publish_dir = Some(PathBuf::from(value));
            }
        }
        index += 1;
    }

    if runtime_profile != Some(PackageRuntimeProfile::Hybrid) {
        eprintln!(
            "error: dedupe-worker-shared is hybrid/coexistence packaging only; pass --runtime-profile hybrid"
        );
        return 2;
    }

    let Some(publish_dir) = publish_dir else {
        eprintln!("error: dedupe-worker-shared requires <publish-dir>");
        print_hybrid_usage();
        return 2;
    };

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

fn run_prepare_package_inputs(args: &[String]) -> i32 {
    let mut platform = None;
    let mut publish_dir = None;
    let mut manifest_path = None;
    let mut output_manifest = None;
    let mut msix_version = None;
    let mut verify_targetsize_icons = false;
    let mut runtime_profile = PackageRuntimeProfile::RustOnly;

    let mut index = 0;
    while index < args.len() {
        match args[index].as_str() {
            "--platform" => {
                let Some(value) = read_value(args, &mut index, "--platform") else {
                    return 2;
                };
                platform = Some(value);
            }
            "--publish-dir" => {
                let Some(value) = read_value(args, &mut index, "--publish-dir") else {
                    return 2;
                };
                publish_dir = Some(PathBuf::from(value));
            }
            "--manifest" => {
                let Some(value) = read_value(args, &mut index, "--manifest") else {
                    return 2;
                };
                manifest_path = Some(PathBuf::from(value));
            }
            "--output-manifest" => {
                let Some(value) = read_value(args, &mut index, "--output-manifest") else {
                    return 2;
                };
                output_manifest = Some(PathBuf::from(value));
            }
            "--msix-version" => {
                let Some(value) = read_value(args, &mut index, "--msix-version") else {
                    return 2;
                };
                if !value.trim().is_empty() {
                    msix_version = Some(value);
                }
            }
            "--verify-targetsize-icons" => verify_targetsize_icons = true,
            "--runtime-profile" => {
                let Some(value) = read_value(args, &mut index, "--runtime-profile") else {
                    return 2;
                };
                let Some(profile) = PackageRuntimeProfile::parse(&value) else {
                    eprintln!(
                        "error: --runtime-profile must be 'hybrid' or 'rust-only', got '{value}'"
                    );
                    print_usage();
                    return 2;
                };
                runtime_profile = profile;
            }
            "--rust-only" => runtime_profile = PackageRuntimeProfile::RustOnly,
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

    let Some(platform) = platform else {
        eprintln!("error: prepare-package-inputs requires --platform");
        print_usage();
        return 2;
    };
    if !matches!(platform.as_str(), "x64" | "x86" | "arm64") {
        eprintln!("error: --platform must be x64, x86, or arm64");
        return 2;
    }
    let Some(publish_dir) = publish_dir else {
        eprintln!("error: prepare-package-inputs requires --publish-dir");
        print_usage();
        return 2;
    };
    let Some(manifest_path) = manifest_path else {
        eprintln!("error: prepare-package-inputs requires --manifest");
        print_usage();
        return 2;
    };
    let Some(output_manifest) = output_manifest else {
        eprintln!("error: prepare-package-inputs requires --output-manifest");
        print_usage();
        return 2;
    };

    match prepare_package_inputs(&PreparePackageInputsOptions {
        platform,
        publish_dir,
        manifest_path,
        output_manifest,
        msix_version,
        verify_targetsize_icons,
        runtime_profile,
    }) {
        Ok(outcome) => {
            println!("[MSIX] RuntimeProfile: {}", runtime_profile.as_str());
            println!("[MSIX] Required assets verified");
            match outcome.targetsize_icon_count {
                Some(count) => println!("[MSIX] Found {count} targetsize icons"),
                None => println!("[MSIX] Targetsize icon verification skipped"),
            }
            if outcome.copied_pri {
                println!("[MSIX] Copied Easydict.WinUI.pri -> resources.pri");
            } else if outcome.resources_pri_already_present {
                println!("[MSIX] resources.pri already exists");
            } else {
                println!("[MSIX] No PRI file found; localization may be incomplete");
            }
            println!(
                "[MSIX] Prepared manifest: {}",
                outcome.output_manifest.display()
            );
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
    print!("{}", usage_text());
}

fn print_hybrid_usage() {
    print!("{}", hybrid_usage_text());
}

fn usage_text() -> String {
    format!(
        "\
Usage: easydict_msix_validate <path-to-msix> [--expected-name <name>] [--expected-publisher <publisher>] [--min-version <ver>] [--runtime-profile hybrid|rust-only] [--rust-only] [--allow-unsigned]
       easydict_msix_validate fix-minversion <path-to-msix> [--min-version <ver>] [--makeappx <path>]
       easydict_msix_validate verify-bundle-minversion <path-to-msixbundle> [--required-min-version <ver>] [--runtime-profile hybrid|rust-only]
       easydict_msix_validate prepare-package-inputs --platform x64|x86|arm64 --publish-dir <dir> --manifest <Package.appxmanifest> --output-manifest <temp-manifest> [--msix-version <ver>] [--verify-targetsize-icons] [--runtime-profile hybrid|rust-only] [--rust-only]
  defaults: name={DEFAULT_EXPECTED_NAME}, min-version={DEFAULT_MIN_VERSION}, runtime-profile=rust-only
  --runtime-profile hybrid: validate retained worker/coexistence payloads explicitly
  --runtime-profile rust-only: reject retained .NET workers and bundled .NET runtime payloads
  --rust-only: shortcut for --runtime-profile rust-only
  --allow-unsigned: skip the AppxSignature.p7x check (use for the release workflow which builds unsigned bundles)
"
    )
}

fn hybrid_usage_text() -> &'static str {
    "\
Hybrid/coexistence-only usage:
       easydict_msix_validate dedupe-worker-shared <publish-dir> --runtime-profile hybrid
"
}

#[cfg(test)]
mod tests {
    use super::*;
    use easydict_msix_validate::DEFAULT_EXPECTED_PUBLISHER;
    use std::fs::File;
    use std::io::{Cursor, Seek, Write};
    use std::path::Path;
    use tempfile::Builder;
    use zip::write::FileOptions;
    use zip::ZipWriter;

    #[test]
    fn default_usage_hides_hybrid_worker_dedupe_command() {
        let usage = usage_text();
        assert!(
            !usage.contains("dedupe-worker-shared"),
            "default usage should not advertise retained-worker dedupe:\n{usage}"
        );
        assert!(
            hybrid_usage_text().contains("dedupe-worker-shared"),
            "hybrid-specific help should still document the retained-worker maintenance command"
        );
    }

    #[test]
    fn dedupe_worker_shared_cli_requires_explicit_hybrid_profile() {
        let temp = Builder::new()
            .prefix("easydict-msix-cli-dedupe-profile-")
            .tempdir()
            .expect("create temp dir");
        let publish_dir = temp.path().to_string_lossy().into_owned();

        assert_eq!(
            run(vec![
                "dedupe-worker-shared".to_string(),
                publish_dir.clone()
            ]),
            2,
            "dedupe-worker-shared should not run without explicit hybrid profile"
        );
        assert_eq!(
            run(vec![
                "dedupe-worker-shared".to_string(),
                publish_dir.clone(),
                "--runtime-profile".to_string(),
                "rust-only".to_string(),
            ]),
            2,
            "dedupe-worker-shared should reject rust-only profile"
        );
        assert_eq!(
            run(vec![
                "dedupe-worker-shared".to_string(),
                publish_dir,
                "--runtime-profile".to_string(),
                "hybrid".to_string(),
            ]),
            0,
            "dedupe-worker-shared remains available for explicit hybrid packaging"
        );
    }

    #[test]
    fn validate_msix_cli_defaults_to_rust_only_payload_policy() {
        let temp = Builder::new()
            .prefix("easydict-msix-cli-rust-only-package-")
            .tempdir()
            .expect("create temp dir");
        let package_path = temp.path().join("Easydict-x64.msix");
        write_package(
            &package_path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            &retained_runtime_entries(),
        );

        let package = package_path.to_string_lossy().into_owned();

        assert_eq!(
            run(vec![package.clone(), "--allow-unsigned".to_string()]),
            1,
            "default validate-msix CLI must enforce rust-only payload policy"
        );
        assert_eq!(
            run(vec![
                package,
                "--allow-unsigned".to_string(),
                "--runtime-profile".to_string(),
                "hybrid".to_string(),
            ]),
            0,
            "the same retained-runtime package should only pass with an explicit hybrid profile"
        );
    }

    #[test]
    fn verify_bundle_minversion_cli_defaults_to_rust_only_payload_policy() {
        let temp = Builder::new()
            .prefix("easydict-msix-cli-rust-only-bundle-")
            .tempdir()
            .expect("create temp dir");
        let bundle_path = temp.path().join("Easydict.msixbundle");
        let package = package_bytes_with_entries(
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            &retained_runtime_entries(),
        );
        write_bundle(&bundle_path, &[("Easydict-x64.msix", &package)]);

        let bundle = bundle_path.to_string_lossy().into_owned();

        assert_eq!(
            run(vec!["verify-bundle-minversion".to_string(), bundle.clone()]),
            1,
            "default verify-bundle-minversion must enforce rust-only payload policy"
        );
        assert_eq!(
            run(vec![
                "verify-bundle-minversion".to_string(),
                bundle,
                "--runtime-profile".to_string(),
                "hybrid".to_string(),
            ]),
            0,
            "the same retained-runtime bundle should only pass with an explicit hybrid profile"
        );
    }

    #[test]
    fn prepare_package_inputs_cli_defaults_to_rust_only_and_rejects_retained_payload_before_manifest_write(
    ) {
        let temp = Builder::new()
            .prefix("easydict-msix-cli-prepare-rust-only-")
            .tempdir()
            .expect("create temp dir");
        let publish_dir = temp.path().join("publish");
        std::fs::create_dir_all(&publish_dir).expect("create publish dir");
        write_required_msix_assets(&publish_dir);
        write_file(
            &publish_dir.join("workers/longdoc/Easydict.Workers.LongDoc.exe"),
            b"stale longdoc worker",
        );
        write_file(
            &publish_dir.join("dotnet/host/fxr/8.0.11/hostfxr.dll"),
            b"stale hostfxr",
        );

        let source_manifest = temp.path().join("Package.appxmanifest");
        std::fs::write(
            &source_manifest,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
        )
        .expect("write source manifest");
        let default_output_manifest = temp.path().join("prepared.default.appxmanifest");
        let hybrid_output_manifest = temp.path().join("prepared.hybrid.appxmanifest");

        let default_args = vec![
            "prepare-package-inputs".to_string(),
            "--platform".to_string(),
            "x64".to_string(),
            "--publish-dir".to_string(),
            publish_dir.to_string_lossy().into_owned(),
            "--manifest".to_string(),
            source_manifest.to_string_lossy().into_owned(),
            "--output-manifest".to_string(),
            default_output_manifest.to_string_lossy().into_owned(),
        ];
        assert_eq!(
            run(default_args),
            1,
            "default prepare-package-inputs must enforce rust-only payload policy"
        );
        assert!(
            !default_output_manifest.exists(),
            "rust-only prepare-package-inputs must fail before writing the prepared manifest"
        );

        assert_eq!(
            run(vec![
                "prepare-package-inputs".to_string(),
                "--platform".to_string(),
                "x64".to_string(),
                "--publish-dir".to_string(),
                publish_dir.to_string_lossy().into_owned(),
                "--manifest".to_string(),
                source_manifest.to_string_lossy().into_owned(),
                "--output-manifest".to_string(),
                hybrid_output_manifest.to_string_lossy().into_owned(),
                "--runtime-profile".to_string(),
                "hybrid".to_string(),
            ]),
            0,
            "explicit hybrid prepare-package-inputs may continue with retained coexistence payloads"
        );
        assert!(
            hybrid_output_manifest.is_file(),
            "hybrid prepare-package-inputs should write the prepared manifest"
        );
    }

    fn package_bytes_with_entries(manifest: String, entries: &[(&str, &[u8])]) -> Vec<u8> {
        let cursor = Cursor::new(Vec::new());
        let mut writer = ZipWriter::new(cursor);
        let options: FileOptions<'_, ()> = FileOptions::default();
        add_file(
            &mut writer,
            "AppxManifest.xml",
            manifest.as_bytes(),
            options,
        );
        for (name, contents) in entries {
            add_file(&mut writer, name, contents, options);
        }
        writer.finish().expect("finish test package").into_inner()
    }

    fn write_package(path: &Path, manifest: String, entries: &[(&str, &[u8])]) {
        let file = File::create(path).expect("create test package");
        let mut writer = ZipWriter::new(file);
        let options: FileOptions<'_, ()> = FileOptions::default();
        add_file(
            &mut writer,
            "AppxManifest.xml",
            manifest.as_bytes(),
            options,
        );
        for (name, contents) in entries {
            add_file(&mut writer, name, contents, options);
        }
        writer.finish().expect("finish test package");
    }

    fn write_bundle(path: &Path, entries: &[(&str, &[u8])]) {
        let file = File::create(path).expect("create test bundle");
        let mut writer = ZipWriter::new(file);
        let options: FileOptions<'_, ()> = FileOptions::default();
        for (name, contents) in entries {
            add_file(&mut writer, name, contents, options);
        }
        writer.finish().expect("finish test bundle");
    }

    fn add_file<W: Write + Seek>(
        writer: &mut ZipWriter<W>,
        name: &str,
        contents: &[u8],
        options: FileOptions<'_, ()>,
    ) {
        writer.start_file(name, options).expect("start zip file");
        writer.write_all(contents).expect("write zip file");
    }

    fn write_required_msix_assets(root: &Path) {
        for asset in [
            "Assets/SplashScreen.scale-100.png",
            "Assets/LockScreenLogo.scale-100.png",
            "Assets/Square150x150Logo.scale-100.png",
            "Assets/Square44x44Logo.scale-100.png",
            "Assets/Wide310x150Logo.scale-100.png",
            "Assets/StoreLogo.png",
        ] {
            write_file(&root.join(asset), b"asset");
        }
    }

    fn write_file(path: &Path, contents: &[u8]) {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).expect("create test file parent");
        }
        std::fs::write(path, contents).expect("write test file");
    }

    fn manifest(name: &str, publisher: &str, min_version: &str, architecture: &str) -> String {
        format!(
            r#"<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="{name}" Publisher="{publisher}" Version="1.0.0.0" ProcessorArchitecture="{architecture}" />
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Universal" MinVersion="{min_version}" MaxVersionTested="10.0.22621.0" />
  </Dependencies>
</Package>"#
        )
    }

    fn retained_runtime_entries() -> Vec<(&'static str, &'static [u8])> {
        vec![
            ("easydict-native-bridge.exe", b"native-bridge"),
            ("easydict_browser_registrar.exe", b"registrar"),
            ("BrowserHostRegistrar.exe", b"registrar-alias"),
            ("easydict_cli.exe", b"cli"),
            ("easydict_long_doc.exe", b"longdoc-cli"),
            ("workers/longdoc/Easydict.Workers.LongDoc.exe", b"longdoc"),
            ("workers/localai/Easydict.Workers.LocalAi.exe", b"localai"),
            ("dotnet/host/fxr/8.0.11/hostfxr.dll", b"hostfxr"),
            (
                "dotnet/shared/Microsoft.NETCore.App/8.0.11/coreclr.dll",
                b"coreclr",
            ),
        ]
    }
}

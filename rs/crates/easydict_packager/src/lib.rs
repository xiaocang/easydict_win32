use std::fmt;
use std::fs::{self, File};
use std::io::{self, BufReader, Seek, Write};
use std::path::{Path, PathBuf};

use serde_json::Value;
use zip::write::FileOptions;
use zip::{CompressionMethod, ZipArchive, ZipWriter};

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ZipDirectoryOptions {
    pub source_dir: PathBuf,
    pub destination_zip: PathBuf,
    pub exclude_extensions: Vec<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ZipDirectoryOutcome {
    pub file_count: usize,
    pub directory_count: usize,
    pub skipped_count: usize,
    pub bytes_written: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ExtractDotnetRuntimeOptions {
    pub rid: String,
    pub output_dir: PathBuf,
    pub version: String,
    pub runtime_profile: PackageRuntimeProfile,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ExtractDotnetRuntimeOutcome {
    pub bundled_version: String,
    pub total_bytes: u64,
    pub archive_bytes: u64,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PackageRuntimeProfile {
    Hybrid,
    RustOnly,
}

impl PackageRuntimeProfile {
    pub fn parse_explicit(value: &str) -> Option<Self> {
        let normalized = normalize_runtime_profile(value);
        if normalized == "hybrid" {
            return Some(Self::Hybrid);
        }
        runtime_profile_is_rust_only(&normalized).then_some(Self::RustOnly)
    }

    fn parse_environment(value: &str) -> Self {
        let normalized = normalize_runtime_profile(value);
        if normalized == "hybrid" {
            Self::Hybrid
        } else {
            Self::RustOnly
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BuildRustHelpersOptions {
    pub rust_workspace: PathBuf,
    pub platform: String,
    pub configuration: String,
    pub output_dir: PathBuf,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BuildRustHelpersOutcome {
    pub cargo_target: String,
    pub profile_dir: String,
    pub copied_files: Vec<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PackageBrowserExtensionOptions {
    pub extension_dir: PathBuf,
    pub output_dir: Option<PathBuf>,
    pub target: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PackageBrowserExtensionOutcome {
    pub version: String,
    pub packages: Vec<BrowserExtensionPackage>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PackRustPortableOptions {
    pub rust_workspace: PathBuf,
    pub platform: String,
    pub configuration: String,
    pub output_root: PathBuf,
    pub package_version: Option<String>,
    pub create_zip: bool,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PackRustPortableOutcome {
    pub package_name: String,
    pub package_dir: PathBuf,
    pub zip_path: Option<PathBuf>,
    pub file_count: usize,
    pub total_bytes: u64,
    pub directory_validation_entries: usize,
    pub zip_validation_entries: Option<usize>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ValidateRustPortableOptions {
    pub package_path: PathBuf,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ValidateRustPortableOutcome {
    pub checked_entries: usize,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BrowserExtensionPackage {
    pub label: String,
    pub path: PathBuf,
    pub bytes: u64,
}

#[derive(Debug, Eq, PartialEq)]
pub enum ZipDirectoryError {
    SourceMissing(PathBuf),
    SourceNotDirectory(PathBuf),
    DestinationInsideSource {
        source: PathBuf,
        destination: PathBuf,
    },
    InvalidEntryPath(PathBuf),
    Io {
        path: PathBuf,
        message: String,
    },
    Zip(String),
}

#[derive(Debug, Eq, PartialEq)]
pub enum ExtractDotnetRuntimeError {
    RuntimeProfileMustBeHybrid(PackageRuntimeProfile),
    RuntimeProfileEnvironmentRustOnly { name: &'static str, value: String },
    UnsupportedRid(String),
    ArchiveTooSmall { path: PathBuf, bytes: u64 },
    InvalidArchiveEntry(String),
    MissingExpectedDirectory(PathBuf),
    MissingBundledVersion(PathBuf),
    Http(String),
    Io { path: PathBuf, message: String },
    Zip(String),
}

#[derive(Debug, Eq, PartialEq)]
pub enum BuildRustHelpersError {
    UnsupportedPlatform(String),
    UnsupportedConfiguration(String),
    WorkspaceMissing(PathBuf),
    RustupFailed { exit_code: Option<i32> },
    CargoFailed { exit_code: Option<i32> },
    MissingHelper(PathBuf),
    Io { path: PathBuf, message: String },
}

#[derive(Debug, Eq, PartialEq)]
pub enum PackageBrowserExtensionError {
    UnsupportedTarget(String),
    ExtensionDirMissing(PathBuf),
    ManifestMissing(PathBuf),
    RequiredFileMissing(PathBuf),
    MissingVersion(PathBuf),
    ManifestNotObject(PathBuf),
    InvalidManifestJson { path: PathBuf, message: String },
    InvalidEntryPath(PathBuf),
    Io { path: PathBuf, message: String },
    Zip(String),
}

#[derive(Debug, Eq, PartialEq)]
pub enum PackRustPortableError {
    UnsupportedPlatform(String),
    UnsupportedConfiguration(String),
    WorkspaceMissing(PathBuf),
    CargoFailed {
        command: &'static str,
        exit_code: Option<i32>,
    },
    MissingExecutable(PathBuf),
    UnsafeOutputPath {
        output_root: PathBuf,
        package_dir: PathBuf,
    },
    Io {
        path: PathBuf,
        message: String,
    },
    Zip(String),
    Validation(String),
}

#[derive(Debug, Eq, PartialEq)]
pub enum ValidateRustPortableError {
    PackageMissing(PathBuf),
    UnsupportedPackagePath(PathBuf),
    InvalidArchiveEntry(String),
    ForbiddenEntries(Vec<String>),
    MissingRequiredEntries(Vec<String>),
    UnexpectedEntries(Vec<String>),
    Io { path: PathBuf, message: String },
    Zip(String),
}

impl fmt::Display for ZipDirectoryError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::SourceMissing(path) => {
                write!(formatter, "source directory not found: {}", path.display())
            }
            Self::SourceNotDirectory(path) => write!(
                formatter,
                "source path is not a directory: {}",
                path.display()
            ),
            Self::DestinationInsideSource {
                source,
                destination,
            } => write!(
                formatter,
                "destination zip {} must not be inside source directory {}",
                destination.display(),
                source.display()
            ),
            Self::InvalidEntryPath(path) => {
                write!(
                    formatter,
                    "cannot derive zip entry path for {}",
                    path.display()
                )
            }
            Self::Io { path, message } => write!(formatter, "{}: {message}", path.display()),
            Self::Zip(message) => write!(formatter, "{message}"),
        }
    }
}

impl std::error::Error for ZipDirectoryError {}

impl fmt::Display for ExtractDotnetRuntimeError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::RuntimeProfileMustBeHybrid(_) => write!(
                formatter,
                "extract-dotnet-runtime requires explicit --runtime-profile hybrid; rs portable packages must not bundle .NET runtime"
            ),
            Self::RuntimeProfileEnvironmentRustOnly { .. } => write!(
                formatter,
                "extract-dotnet-runtime is disabled by rust-only runtime profile environment; rs portable packages must not bundle .NET runtime"
            ),
            Self::UnsupportedRid(rid) => {
                write!(formatter, "unsupported .NET runtime RID: {rid}")
            }
            Self::ArchiveTooSmall { path, bytes } => write!(
                formatter,
                "downloaded runtime archive is empty or too small: {} ({bytes} bytes)",
                path.display()
            ),
            Self::InvalidArchiveEntry(name) => {
                write!(
                    formatter,
                    "runtime archive contains unsafe entry path: {name}"
                )
            }
            Self::MissingExpectedDirectory(path) => {
                write!(
                    formatter,
                    "expected directory missing after extraction: {}",
                    path.display()
                )
            }
            Self::MissingBundledVersion(path) => write!(
                formatter,
                "no bundled runtime version found under {}",
                path.display()
            ),
            Self::Http(message) => write!(formatter, "{message}"),
            Self::Io { path, message } => write!(formatter, "{}: {message}", path.display()),
            Self::Zip(message) => write!(formatter, "{message}"),
        }
    }
}

impl std::error::Error for ExtractDotnetRuntimeError {}

impl fmt::Display for BuildRustHelpersError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform(platform) => {
                write!(formatter, "unsupported Rust helper platform: {platform}")
            }
            Self::UnsupportedConfiguration(configuration) => {
                write!(
                    formatter,
                    "unsupported Rust helper configuration: {configuration}"
                )
            }
            Self::WorkspaceMissing(path) => {
                write!(formatter, "Rust workspace not found at {}", path.display())
            }
            Self::RustupFailed { exit_code } => match exit_code {
                Some(code) => write!(formatter, "rustup target add failed with exit code {code}"),
                None => write!(formatter, "rustup target add failed"),
            },
            Self::CargoFailed { exit_code } => match exit_code {
                Some(code) => write!(formatter, "cargo build failed with exit code {code}"),
                None => write!(formatter, "cargo build failed"),
            },
            Self::MissingHelper(path) => {
                write!(
                    formatter,
                    "Rust helper executable was not produced: {}",
                    path.display()
                )
            }
            Self::Io { path, message } => write!(formatter, "{}: {message}", path.display()),
        }
    }
}

impl std::error::Error for BuildRustHelpersError {}

impl fmt::Display for PackageBrowserExtensionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedTarget(target) => {
                write!(formatter, "unsupported browser extension target: {target}")
            }
            Self::ExtensionDirMissing(path) => {
                write!(
                    formatter,
                    "browser extension directory not found: {}",
                    path.display()
                )
            }
            Self::ManifestMissing(path) => {
                write!(
                    formatter,
                    "browser extension manifest not found: {}",
                    path.display()
                )
            }
            Self::RequiredFileMissing(path) => {
                write!(
                    formatter,
                    "browser extension required file missing: {}",
                    path.display()
                )
            }
            Self::MissingVersion(path) => {
                write!(
                    formatter,
                    "browser extension manifest is missing version: {}",
                    path.display()
                )
            }
            Self::ManifestNotObject(path) => {
                write!(
                    formatter,
                    "browser extension manifest is not a JSON object: {}",
                    path.display()
                )
            }
            Self::InvalidManifestJson { path, message } => {
                write!(formatter, "failed to parse {}: {message}", path.display())
            }
            Self::InvalidEntryPath(path) => {
                write!(
                    formatter,
                    "cannot derive browser extension package entry for {}",
                    path.display()
                )
            }
            Self::Io { path, message } => write!(formatter, "{}: {message}", path.display()),
            Self::Zip(message) => write!(formatter, "{message}"),
        }
    }
}

impl std::error::Error for PackageBrowserExtensionError {}

impl fmt::Display for PackRustPortableError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform(platform) => {
                write!(formatter, "unsupported Rust portable platform: {platform}")
            }
            Self::UnsupportedConfiguration(configuration) => {
                write!(
                    formatter,
                    "unsupported Rust portable configuration: {configuration}"
                )
            }
            Self::WorkspaceMissing(path) => {
                write!(formatter, "Rust workspace not found at {}", path.display())
            }
            Self::CargoFailed { command, exit_code } => match exit_code {
                Some(code) => write!(formatter, "{command} failed with exit code {code}"),
                None => write!(formatter, "{command} failed"),
            },
            Self::MissingExecutable(path) => {
                write!(
                    formatter,
                    "Rust executable was not produced: {}",
                    path.display()
                )
            }
            Self::UnsafeOutputPath {
                output_root,
                package_dir,
            } => write!(
                formatter,
                "refusing to remove package directory {} outside output root {}",
                package_dir.display(),
                output_root.display()
            ),
            Self::Io { path, message } => write!(formatter, "{}: {message}", path.display()),
            Self::Zip(message) | Self::Validation(message) => write!(formatter, "{message}"),
        }
    }
}

impl std::error::Error for PackRustPortableError {}

impl fmt::Display for ValidateRustPortableError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::PackageMissing(path) => {
                write!(
                    formatter,
                    "Rust portable package path not found: {}",
                    path.display()
                )
            }
            Self::UnsupportedPackagePath(path) => write!(
                formatter,
                "Rust portable package path must be a directory or ZIP: {}",
                path.display()
            ),
            Self::InvalidArchiveEntry(name) => write!(
                formatter,
                "Rust portable ZIP contains unsafe entry path: {name}"
            ),
            Self::ForbiddenEntries(entries) => write!(
                formatter,
                "Rust portable package contains retained .NET payload entries: {}",
                entries
                    .iter()
                    .take(10)
                    .cloned()
                    .collect::<Vec<_>>()
                    .join(", ")
            ),
            Self::MissingRequiredEntries(entries) => write!(
                formatter,
                "Rust portable package is missing required entries: {}",
                entries
                    .iter()
                    .take(10)
                    .cloned()
                    .collect::<Vec<_>>()
                    .join(", ")
            ),
            Self::UnexpectedEntries(entries) => write!(
                formatter,
                "Rust portable package contains entries outside the first-release allowlist: {}",
                entries
                    .iter()
                    .take(10)
                    .cloned()
                    .collect::<Vec<_>>()
                    .join(", ")
            ),
            Self::Io { path, message } => write!(formatter, "{}: {message}", path.display()),
            Self::Zip(message) => write!(formatter, "{message}"),
        }
    }
}

impl std::error::Error for ValidateRustPortableError {}

pub fn zip_directory(
    options: &ZipDirectoryOptions,
) -> Result<ZipDirectoryOutcome, ZipDirectoryError> {
    let source_dir = canonicalize_required_dir(&options.source_dir)?;
    let destination_zip = normalize_destination_path(&options.destination_zip)?;
    if destination_zip.starts_with(&source_dir) {
        return Err(ZipDirectoryError::DestinationInsideSource {
            source: source_dir,
            destination: destination_zip,
        });
    }

    if let Some(parent) = destination_zip.parent() {
        fs::create_dir_all(parent).map_err(|error| ZipDirectoryError::Io {
            path: parent.to_path_buf(),
            message: error.to_string(),
        })?;
    }

    let file = File::create(&destination_zip).map_err(|error| ZipDirectoryError::Io {
        path: destination_zip.clone(),
        message: error.to_string(),
    })?;
    let mut writer = ZipWriter::new(file);
    let mut outcome = ZipDirectoryOutcome {
        file_count: 0,
        directory_count: 0,
        skipped_count: 0,
        bytes_written: 0,
    };
    let excludes = options
        .exclude_extensions
        .iter()
        .map(|extension| normalize_extension(extension))
        .collect::<Vec<_>>();

    zip_directory_entries(
        &source_dir,
        &source_dir,
        &excludes,
        &mut writer,
        &mut outcome,
    )?;
    writer
        .finish()
        .map_err(|error| ZipDirectoryError::Zip(error.to_string()))?;
    outcome.bytes_written = fs::metadata(&destination_zip)
        .map_err(|error| ZipDirectoryError::Io {
            path: destination_zip.clone(),
            message: error.to_string(),
        })?
        .len();

    Ok(outcome)
}

pub fn download_and_extract_dotnet_runtime(
    options: &ExtractDotnetRuntimeOptions,
) -> Result<ExtractDotnetRuntimeOutcome, ExtractDotnetRuntimeError> {
    validate_extract_dotnet_runtime_profile(options.runtime_profile)?;
    validate_runtime_rid(&options.rid)?;
    let url = dotnet_runtime_url(&options.version, &options.rid);
    let temp_file = tempfile::Builder::new()
        .prefix("dotnet-runtime-")
        .suffix(".zip")
        .tempfile()
        .map_err(|error| ExtractDotnetRuntimeError::Io {
            path: std::env::temp_dir(),
            message: error.to_string(),
        })?;
    let (mut file, temp_path) = temp_file.into_parts();

    let response = reqwest::blocking::Client::builder()
        .timeout(std::time::Duration::from_secs(300))
        .build()
        .map_err(|error| ExtractDotnetRuntimeError::Http(error.to_string()))?
        .get(&url)
        .send()
        .map_err(|error| {
            ExtractDotnetRuntimeError::Http(format!(
                "failed to download .NET {} runtime for {}: {error}",
                options.version, options.rid
            ))
        })?;
    if !response.status().is_success() {
        return Err(ExtractDotnetRuntimeError::Http(format!(
            "failed to download .NET {} runtime for {}: HTTP {}",
            options.version,
            options.rid,
            response.status()
        )));
    }
    let mut response = response;
    io::copy(&mut response, &mut file).map_err(|error| ExtractDotnetRuntimeError::Io {
        path: temp_path.to_path_buf(),
        message: error.to_string(),
    })?;
    file.flush()
        .map_err(|error| ExtractDotnetRuntimeError::Io {
            path: temp_path.to_path_buf(),
            message: error.to_string(),
        })?;
    drop(file);

    let outcome = extract_dotnet_runtime_archive(&temp_path, &options.output_dir)?;
    drop(temp_path);
    Ok(outcome)
}

pub fn build_rust_helpers(
    options: &BuildRustHelpersOptions,
) -> Result<BuildRustHelpersOutcome, BuildRustHelpersError> {
    let cargo_target = cargo_target_for_platform(&options.platform)?;
    let profile_dir = profile_dir_for_configuration(&options.configuration)?;
    if !options.rust_workspace.join("Cargo.toml").is_file() {
        return Err(BuildRustHelpersError::WorkspaceMissing(
            options.rust_workspace.clone(),
        ));
    }

    run_rustup_target_add_if_available(cargo_target)?;
    let mut cargo_args = rust_helper_cargo_args(cargo_target, &options.configuration);
    let status = std::process::Command::new("cargo")
        .args(&cargo_args)
        .envs(rust_portable_cargo_environment())
        .current_dir(&options.rust_workspace)
        .status()
        .map_err(|error| BuildRustHelpersError::Io {
            path: PathBuf::from("cargo"),
            message: error.to_string(),
        })?;
    if !status.success() {
        return Err(BuildRustHelpersError::CargoFailed {
            exit_code: status.code(),
        });
    }

    cargo_args.clear();
    copy_built_rust_helpers(
        &options.rust_workspace,
        cargo_target,
        profile_dir,
        &options.output_dir,
    )
}

pub fn package_browser_extension(
    options: &PackageBrowserExtensionOptions,
) -> Result<PackageBrowserExtensionOutcome, PackageBrowserExtensionError> {
    if !options.extension_dir.is_dir() {
        return Err(PackageBrowserExtensionError::ExtensionDirMissing(
            options.extension_dir.clone(),
        ));
    }
    let extension_dir = fs::canonicalize(&options.extension_dir).map_err(|error| {
        PackageBrowserExtensionError::Io {
            path: options.extension_dir.clone(),
            message: error.to_string(),
        }
    })?;
    for file in BROWSER_EXTENSION_COMMON_FILES {
        let path = extension_dir.join(file);
        if !path.is_file() {
            return Err(PackageBrowserExtensionError::RequiredFileMissing(path));
        }
    }

    let version = browser_extension_version(&extension_dir.join("manifest.json"))?;
    let output_dir = options
        .output_dir
        .clone()
        .unwrap_or_else(|| extension_dir.join("dist"));
    fs::create_dir_all(&output_dir).map_err(|error| PackageBrowserExtensionError::Io {
        path: output_dir.clone(),
        message: error.to_string(),
    })?;

    let mut packages = Vec::new();
    for target in browser_extension_targets(&options.target)? {
        let package =
            package_browser_extension_target(&extension_dir, &output_dir, &version, target)?;
        packages.push(package);
    }

    Ok(PackageBrowserExtensionOutcome { version, packages })
}

pub fn pack_rs_portable(
    options: &PackRustPortableOptions,
) -> Result<PackRustPortableOutcome, PackRustPortableError> {
    let cargo_target =
        cargo_target_for_platform(&options.platform).map_err(|error| match error {
            BuildRustHelpersError::UnsupportedPlatform(platform) => {
                PackRustPortableError::UnsupportedPlatform(platform)
            }
            _ => unreachable!("cargo_target_for_platform only returns unsupported platform"),
        })?;
    let profile_dir =
        profile_dir_for_configuration(&options.configuration).map_err(|error| match error {
            BuildRustHelpersError::UnsupportedConfiguration(configuration) => {
                PackRustPortableError::UnsupportedConfiguration(configuration)
            }
            _ => unreachable!("profile_dir_for_configuration only returns unsupported config"),
        })?;
    if !options.rust_workspace.join("Cargo.toml").is_file() {
        return Err(PackRustPortableError::WorkspaceMissing(
            options.rust_workspace.clone(),
        ));
    }

    let package_name =
        rust_portable_package_name(options.package_version.as_deref(), &options.platform);
    let output_root = prepare_output_root(&options.output_root)?;
    let package_dir = output_root.join(&package_name);
    let zip_path = output_root.join(format!("{package_name}.zip"));

    remove_existing_package_dir(&output_root, &package_dir)?;
    fs::create_dir_all(&package_dir).map_err(|error| PackRustPortableError::Io {
        path: package_dir.clone(),
        message: error.to_string(),
    })?;

    run_rustup_target_add_if_available(cargo_target).map_err(pack_error_from_build_error)?;
    run_pack_cargo_command(
        &options.rust_workspace,
        preview_app_cargo_args(cargo_target, &options.configuration),
        "cargo build easydict_preview_iced",
    )?;
    run_pack_cargo_command(
        &options.rust_workspace,
        rust_helper_cargo_args(cargo_target, &options.configuration),
        "cargo build Rust helper executables",
    )?;

    stage_rust_portable_payload(
        &options.rust_workspace,
        cargo_target,
        profile_dir,
        &package_dir,
    )?;
    let directory_validation = validate_rs_portable_payload(&ValidateRustPortableOptions {
        package_path: package_dir.clone(),
    })
    .map_err(|error| PackRustPortableError::Validation(error.to_string()))?;

    let zip_validation_entries = if options.create_zip {
        if zip_path.exists() {
            fs::remove_file(&zip_path).map_err(|error| PackRustPortableError::Io {
                path: zip_path.clone(),
                message: error.to_string(),
            })?;
        }
        zip_directory(&ZipDirectoryOptions {
            source_dir: package_dir.clone(),
            destination_zip: zip_path.clone(),
            exclude_extensions: Vec::new(),
        })
        .map_err(|error| PackRustPortableError::Zip(error.to_string()))?;
        let validation = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: zip_path.clone(),
        })
        .map_err(|error| PackRustPortableError::Validation(error.to_string()))?;
        Some(validation.checked_entries)
    } else {
        None
    };

    let (file_count, total_bytes) = package_file_count_and_size(&package_dir)?;
    Ok(PackRustPortableOutcome {
        package_name,
        package_dir,
        zip_path: options.create_zip.then_some(zip_path),
        file_count,
        total_bytes,
        directory_validation_entries: directory_validation.checked_entries,
        zip_validation_entries,
    })
}

pub fn validate_rs_portable_payload(
    options: &ValidateRustPortableOptions,
) -> Result<ValidateRustPortableOutcome, ValidateRustPortableError> {
    let path = &options.package_path;
    if !path.exists() {
        return Err(ValidateRustPortableError::PackageMissing(path.clone()));
    }

    let entries = if path.is_dir() {
        rust_portable_directory_entries(path)?
    } else if path.is_file()
        && path
            .extension()
            .and_then(|extension| extension.to_str())
            .is_some_and(|extension| extension.eq_ignore_ascii_case("zip"))
    {
        rust_portable_zip_entries(path)?
    } else {
        return Err(ValidateRustPortableError::UnsupportedPackagePath(
            path.clone(),
        ));
    };

    let forbidden_entries = entries
        .iter()
        .filter(|entry| rust_portable_entry_is_forbidden(entry))
        .cloned()
        .collect::<Vec<_>>();
    if !forbidden_entries.is_empty() {
        return Err(ValidateRustPortableError::ForbiddenEntries(
            forbidden_entries,
        ));
    }

    let missing_entries = RUST_PORTABLE_REQUIRED_ENTRIES
        .iter()
        .filter(|entry| !entries.iter().any(|actual| actual == **entry))
        .map(|entry| (*entry).to_string())
        .collect::<Vec<_>>();
    if !missing_entries.is_empty() {
        return Err(ValidateRustPortableError::MissingRequiredEntries(
            missing_entries,
        ));
    }

    let unexpected_entries = entries
        .iter()
        .filter(|entry| !rust_portable_entry_is_allowed(entry))
        .cloned()
        .collect::<Vec<_>>();
    if !unexpected_entries.is_empty() {
        return Err(ValidateRustPortableError::UnexpectedEntries(
            unexpected_entries,
        ));
    }

    Ok(ValidateRustPortableOutcome {
        checked_entries: entries.len(),
    })
}

pub fn copy_built_rust_helpers(
    rust_workspace: &Path,
    cargo_target: &str,
    profile_dir: &str,
    output_dir: &Path,
) -> Result<BuildRustHelpersOutcome, BuildRustHelpersError> {
    let mut copied_files =
        copy_built_rust_helper_executables(rust_workspace, cargo_target, profile_dir, output_dir)?;
    let built_dir = rust_workspace
        .join("target")
        .join(cargo_target)
        .join(profile_dir);
    let registrar_source = built_dir.join("easydict_browser_registrar.exe");
    let legacy_name = "BrowserHostRegistrar.exe";
    fs::copy(&registrar_source, output_dir.join(legacy_name)).map_err(|error| {
        BuildRustHelpersError::Io {
            path: output_dir.join(legacy_name),
            message: error.to_string(),
        }
    })?;
    copied_files.push(legacy_name.to_string());

    Ok(BuildRustHelpersOutcome {
        cargo_target: cargo_target.to_string(),
        profile_dir: profile_dir.to_string(),
        copied_files,
    })
}

fn copy_built_rust_helper_executables(
    rust_workspace: &Path,
    cargo_target: &str,
    profile_dir: &str,
    output_dir: &Path,
) -> Result<Vec<String>, BuildRustHelpersError> {
    fs::create_dir_all(output_dir).map_err(|error| BuildRustHelpersError::Io {
        path: output_dir.to_path_buf(),
        message: error.to_string(),
    })?;
    let built_dir = rust_workspace
        .join("target")
        .join(cargo_target)
        .join(profile_dir);
    let mut copied_files = Vec::new();

    for exe_name in RUST_HELPER_EXECUTABLES {
        let source = built_dir.join(exe_name);
        if !source.is_file() {
            return Err(BuildRustHelpersError::MissingHelper(source));
        }
        fs::copy(&source, output_dir.join(exe_name)).map_err(|error| {
            BuildRustHelpersError::Io {
                path: output_dir.join(exe_name),
                message: error.to_string(),
            }
        })?;
        copied_files.push((*exe_name).to_string());
    }

    Ok(copied_files)
}

pub fn extract_dotnet_runtime_archive(
    archive_path: &Path,
    output_dir: &Path,
) -> Result<ExtractDotnetRuntimeOutcome, ExtractDotnetRuntimeError> {
    let archive_bytes =
        fs::metadata(archive_path).map_err(|error| ExtractDotnetRuntimeError::Io {
            path: archive_path.to_path_buf(),
            message: error.to_string(),
        })?;
    if archive_bytes.len() < 1024 * 1024 {
        return Err(ExtractDotnetRuntimeError::ArchiveTooSmall {
            path: archive_path.to_path_buf(),
            bytes: archive_bytes.len(),
        });
    }

    fs::create_dir_all(output_dir).map_err(|error| ExtractDotnetRuntimeError::Io {
        path: output_dir.to_path_buf(),
        message: error.to_string(),
    })?;

    let file = File::open(archive_path).map_err(|error| ExtractDotnetRuntimeError::Io {
        path: archive_path.to_path_buf(),
        message: error.to_string(),
    })?;
    let mut archive = ZipArchive::new(BufReader::new(file))
        .map_err(|error| ExtractDotnetRuntimeError::Zip(error.to_string()))?;

    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|error| ExtractDotnetRuntimeError::Zip(error.to_string()))?;
        let Some(enclosed_name) = entry.enclosed_name() else {
            return Err(ExtractDotnetRuntimeError::InvalidArchiveEntry(
                entry.name().to_string(),
            ));
        };
        let destination = output_dir.join(enclosed_name);
        if entry.is_dir() {
            fs::create_dir_all(&destination).map_err(|error| ExtractDotnetRuntimeError::Io {
                path: destination,
                message: error.to_string(),
            })?;
        } else {
            if let Some(parent) = destination.parent() {
                fs::create_dir_all(parent).map_err(|error| ExtractDotnetRuntimeError::Io {
                    path: parent.to_path_buf(),
                    message: error.to_string(),
                })?;
            }
            let mut output =
                File::create(&destination).map_err(|error| ExtractDotnetRuntimeError::Io {
                    path: destination.clone(),
                    message: error.to_string(),
                })?;
            io::copy(&mut entry, &mut output).map_err(|error| ExtractDotnetRuntimeError::Io {
                path: destination,
                message: error.to_string(),
            })?;
        }
    }

    for file_name in ["LICENSE.txt", "ThirdPartyNotices.txt"] {
        let path = output_dir.join(file_name);
        match fs::remove_file(&path) {
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => {
                return Err(ExtractDotnetRuntimeError::Io {
                    path,
                    message: error.to_string(),
                })
            }
        }
    }

    let shared_dir = output_dir.join("shared").join("Microsoft.NETCore.App");
    let host_fxr_dir = output_dir.join("host").join("fxr");
    for expected in [&shared_dir, &host_fxr_dir] {
        if !expected.is_dir() {
            return Err(ExtractDotnetRuntimeError::MissingExpectedDirectory(
                expected.to_path_buf(),
            ));
        }
    }
    let bundled_version = first_directory_name(&shared_dir)?;
    let total_bytes = directory_size(output_dir)?;

    Ok(ExtractDotnetRuntimeOutcome {
        bundled_version,
        total_bytes,
        archive_bytes: archive_bytes.len(),
    })
}

pub fn dotnet_runtime_url(version: &str, rid: &str) -> String {
    format!(
        "https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/dotnet-runtime-{version}-{rid}.zip"
    )
}

fn validate_extract_dotnet_runtime_profile(
    runtime_profile: PackageRuntimeProfile,
) -> Result<(), ExtractDotnetRuntimeError> {
    if runtime_profile != PackageRuntimeProfile::Hybrid {
        return Err(ExtractDotnetRuntimeError::RuntimeProfileMustBeHybrid(
            runtime_profile,
        ));
    }

    for name in ["EASYDICT_RUNTIME_PROFILE", "RUNTIME_PROFILE"] {
        let Ok(value) = std::env::var(name) else {
            continue;
        };
        if PackageRuntimeProfile::parse_environment(&value) == PackageRuntimeProfile::RustOnly {
            return Err(
                ExtractDotnetRuntimeError::RuntimeProfileEnvironmentRustOnly { name, value },
            );
        }
    }

    Ok(())
}

fn normalize_runtime_profile(value: &str) -> String {
    value.trim().to_ascii_lowercase()
}

fn runtime_profile_is_rust_only(normalized: &str) -> bool {
    matches!(normalized, "rust-only" | "rustonly" | "rust_only")
}

pub const RUST_HELPER_EXECUTABLES: &[&str] = &[
    "easydict-native-bridge.exe",
    "easydict_browser_registrar.exe",
    "easydict_cli.exe",
    "easydict_long_doc.exe",
];

const RUST_PORTABLE_REQUIRED_ENTRIES: &[&str] = &[
    "Easydict.Rust.exe",
    "easydict-native-bridge.exe",
    "easydict_browser_registrar.exe",
    "easydict_cli.exe",
    "easydict_long_doc.exe",
    "README-portable.txt",
];

pub const BROWSER_EXTENSION_COMMON_FILES: &[&str] = &[
    "background.js",
    "setup.html",
    "setup.js",
    "icons/icon16.png",
    "icons/icon48.png",
    "icons/icon128.png",
    "_locales/en/messages.json",
    "_locales/zh_CN/messages.json",
];

pub fn cargo_target_for_platform(platform: &str) -> Result<&'static str, BuildRustHelpersError> {
    match platform {
        "x64" => Ok("x86_64-pc-windows-msvc"),
        "x86" => Ok("i686-pc-windows-msvc"),
        "arm64" => Ok("aarch64-pc-windows-msvc"),
        _ => Err(BuildRustHelpersError::UnsupportedPlatform(
            platform.to_string(),
        )),
    }
}

pub fn profile_dir_for_configuration(
    configuration: &str,
) -> Result<&'static str, BuildRustHelpersError> {
    match configuration {
        "Release" => Ok("release"),
        "Debug" => Ok("debug"),
        _ => Err(BuildRustHelpersError::UnsupportedConfiguration(
            configuration.to_string(),
        )),
    }
}

pub fn rust_helper_cargo_args(cargo_target: &str, configuration: &str) -> Vec<String> {
    let mut args = vec![
        "build".to_string(),
        "-p".to_string(),
        "easydict_app".to_string(),
        "--target".to_string(),
        cargo_target.to_string(),
    ];
    for exe_name in [
        "easydict-native-bridge",
        "easydict_browser_registrar",
        "easydict_cli",
        "easydict_long_doc",
    ] {
        args.push("--bin".to_string());
        args.push(exe_name.to_string());
    }
    if configuration == "Release" {
        args.push("--release".to_string());
    }
    args
}

pub fn rust_portable_package_name(package_version: Option<&str>, platform: &str) -> String {
    let version = package_version
        .map(str::trim)
        .filter(|version| !version.is_empty());
    match version {
        Some(version) => format!("easydict-rs-portable-{version}-win-{platform}"),
        None => format!("easydict-rs-portable-win-{platform}"),
    }
}

pub fn preview_app_cargo_args(cargo_target: &str, configuration: &str) -> Vec<String> {
    let mut args = vec![
        "build".to_string(),
        "-p".to_string(),
        "easydict_preview_iced".to_string(),
        "--target".to_string(),
        cargo_target.to_string(),
    ];
    if configuration == "Release" {
        args.push("--release".to_string());
    }
    args
}

fn run_pack_cargo_command(
    rust_workspace: &Path,
    cargo_args: Vec<String>,
    command_name: &'static str,
) -> Result<(), PackRustPortableError> {
    let status = std::process::Command::new("cargo")
        .args(&cargo_args)
        .envs(rust_portable_cargo_environment())
        .current_dir(rust_workspace)
        .status()
        .map_err(|error| PackRustPortableError::Io {
            path: PathBuf::from("cargo"),
            message: error.to_string(),
        })?;
    if !status.success() {
        return Err(PackRustPortableError::CargoFailed {
            command: command_name,
            exit_code: status.code(),
        });
    }
    Ok(())
}

fn rust_portable_cargo_environment() -> [(&'static str, &'static str); 2] {
    [
        ("EASYDICT_RUNTIME_PROFILE", "rust-only"),
        ("RUNTIME_PROFILE", "rust-only"),
    ]
}

fn stage_rust_portable_payload(
    rust_workspace: &Path,
    cargo_target: &str,
    profile_dir: &str,
    package_dir: &Path,
) -> Result<(), PackRustPortableError> {
    let built_dir = rust_workspace
        .join("target")
        .join(cargo_target)
        .join(profile_dir);
    let preview_exe = built_dir.join("easydict_preview_iced.exe");
    if !preview_exe.is_file() {
        return Err(PackRustPortableError::MissingExecutable(preview_exe));
    }

    fs::copy(&preview_exe, package_dir.join("Easydict.Rust.exe")).map_err(|error| {
        PackRustPortableError::Io {
            path: package_dir.join("Easydict.Rust.exe"),
            message: error.to_string(),
        }
    })?;

    copy_built_rust_helpers_for_portable(rust_workspace, cargo_target, profile_dir, package_dir)?;
    fs::write(
        package_dir.join("README-portable.txt"),
        rust_portable_readme(),
    )
    .map_err(|error| PackRustPortableError::Io {
        path: package_dir.join("README-portable.txt"),
        message: error.to_string(),
    })?;

    Ok(())
}

fn copy_built_rust_helpers_for_portable(
    rust_workspace: &Path,
    cargo_target: &str,
    profile_dir: &str,
    output_dir: &Path,
) -> Result<(), PackRustPortableError> {
    copy_built_rust_helper_executables(rust_workspace, cargo_target, profile_dir, output_dir)
        .map(|_| ())
        .map_err(pack_error_from_build_error)
}

fn rust_portable_readme() -> &'static str {
    "Easydict Rust portable preview\n\
==============================\n\
\n\
Entry point: Easydict.Rust.exe\n\
\n\
This first Rust package is portable-only and intentionally named separately from\n\
the .NET package so both versions can coexist on the same machine.\n\
\n\
This package does not include MSIX metadata, an installer, retained .NET workers,\n\
or a bundled .NET runtime.\n"
}

fn pack_error_from_build_error(error: BuildRustHelpersError) -> PackRustPortableError {
    match error {
        BuildRustHelpersError::UnsupportedPlatform(platform) => {
            PackRustPortableError::UnsupportedPlatform(platform)
        }
        BuildRustHelpersError::UnsupportedConfiguration(configuration) => {
            PackRustPortableError::UnsupportedConfiguration(configuration)
        }
        BuildRustHelpersError::WorkspaceMissing(path) => {
            PackRustPortableError::WorkspaceMissing(path)
        }
        BuildRustHelpersError::RustupFailed { exit_code } => PackRustPortableError::CargoFailed {
            command: "rustup target add",
            exit_code,
        },
        BuildRustHelpersError::CargoFailed { exit_code } => PackRustPortableError::CargoFailed {
            command: "cargo build Rust helper executables",
            exit_code,
        },
        BuildRustHelpersError::MissingHelper(path) => {
            PackRustPortableError::MissingExecutable(path)
        }
        BuildRustHelpersError::Io { path, message } => PackRustPortableError::Io { path, message },
    }
}

fn prepare_output_root(output_root: &Path) -> Result<PathBuf, PackRustPortableError> {
    fs::create_dir_all(output_root).map_err(|error| PackRustPortableError::Io {
        path: output_root.to_path_buf(),
        message: error.to_string(),
    })?;
    fs::canonicalize(output_root).map_err(|error| PackRustPortableError::Io {
        path: output_root.to_path_buf(),
        message: error.to_string(),
    })
}

fn remove_existing_package_dir(
    output_root: &Path,
    package_dir: &Path,
) -> Result<(), PackRustPortableError> {
    if !package_dir.exists() {
        return Ok(());
    }
    let canonical_package_dir =
        fs::canonicalize(package_dir).map_err(|error| PackRustPortableError::Io {
            path: package_dir.to_path_buf(),
            message: error.to_string(),
        })?;
    if !canonical_package_dir.starts_with(output_root) {
        return Err(PackRustPortableError::UnsafeOutputPath {
            output_root: output_root.to_path_buf(),
            package_dir: canonical_package_dir,
        });
    }
    fs::remove_dir_all(&canonical_package_dir).map_err(|error| PackRustPortableError::Io {
        path: canonical_package_dir,
        message: error.to_string(),
    })
}

fn package_file_count_and_size(package_dir: &Path) -> Result<(usize, u64), PackRustPortableError> {
    let mut count = 0;
    let mut total = 0;
    collect_package_file_count_and_size(package_dir, &mut count, &mut total)?;
    Ok((count, total))
}

fn collect_package_file_count_and_size(
    current: &Path,
    count: &mut usize,
    total: &mut u64,
) -> Result<(), PackRustPortableError> {
    for entry in fs::read_dir(current).map_err(|error| PackRustPortableError::Io {
        path: current.to_path_buf(),
        message: error.to_string(),
    })? {
        let entry = entry.map_err(|error| PackRustPortableError::Io {
            path: current.to_path_buf(),
            message: error.to_string(),
        })?;
        let path = entry.path();
        let metadata = fs::metadata(&path).map_err(|error| PackRustPortableError::Io {
            path: path.clone(),
            message: error.to_string(),
        })?;
        if metadata.is_dir() {
            collect_package_file_count_and_size(&path, count, total)?;
        } else if metadata.is_file() {
            *count += 1;
            *total += metadata.len();
        }
    }
    Ok(())
}

fn run_rustup_target_add_if_available(cargo_target: &str) -> Result<(), BuildRustHelpersError> {
    match std::process::Command::new("rustup")
        .arg("target")
        .arg("add")
        .arg(cargo_target)
        .status()
    {
        Ok(status) if status.success() => Ok(()),
        Ok(status) => Err(BuildRustHelpersError::RustupFailed {
            exit_code: status.code(),
        }),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(BuildRustHelpersError::Io {
            path: PathBuf::from("rustup"),
            message: error.to_string(),
        }),
    }
}

fn zip_directory_entries<W: Write + Seek>(
    root: &Path,
    current: &Path,
    excludes: &[String],
    writer: &mut ZipWriter<W>,
    outcome: &mut ZipDirectoryOutcome,
) -> Result<(), ZipDirectoryError> {
    let mut entries = fs::read_dir(current)
        .map_err(|error| ZipDirectoryError::Io {
            path: current.to_path_buf(),
            message: error.to_string(),
        })?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|error| ZipDirectoryError::Io {
            path: current.to_path_buf(),
            message: error.to_string(),
        })?;
    entries.sort_by_key(|entry| entry.path());

    for entry in entries {
        let path = entry.path();
        let metadata = fs::metadata(&path).map_err(|error| ZipDirectoryError::Io {
            path: path.clone(),
            message: error.to_string(),
        })?;
        if metadata.is_dir() {
            let entry_name = entry_name(root, &path)?;
            if fs::read_dir(&path)
                .map_err(|error| ZipDirectoryError::Io {
                    path: path.clone(),
                    message: error.to_string(),
                })?
                .next()
                .is_none()
            {
                writer
                    .add_directory(format!("{entry_name}/"), zip_options())
                    .map_err(|error| ZipDirectoryError::Zip(error.to_string()))?;
                outcome.directory_count += 1;
            }
            zip_directory_entries(root, &path, excludes, writer, outcome)?;
        } else if metadata.is_file() {
            if should_skip(&path, excludes) {
                outcome.skipped_count += 1;
                continue;
            }
            let entry_name = entry_name(root, &path)?;
            writer
                .start_file(entry_name, zip_options())
                .map_err(|error| ZipDirectoryError::Zip(error.to_string()))?;
            let mut file = File::open(&path).map_err(|error| ZipDirectoryError::Io {
                path: path.clone(),
                message: error.to_string(),
            })?;
            io::copy(&mut file, writer).map_err(|error| ZipDirectoryError::Io {
                path,
                message: error.to_string(),
            })?;
            outcome.file_count += 1;
        }
    }

    Ok(())
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum BrowserExtensionTarget {
    Chrome,
    Firefox,
}

impl BrowserExtensionTarget {
    fn manifest_file(self) -> &'static str {
        match self {
            Self::Chrome => "manifest.json",
            Self::Firefox => "manifest.v2.json",
        }
    }

    fn output_file_name(self, version: &str) -> String {
        match self {
            Self::Chrome => format!("easydict-ocr-chrome-v{version}.zip"),
            Self::Firefox => format!("easydict-ocr-firefox-v{version}.xpi"),
        }
    }

    fn label(self) -> &'static str {
        match self {
            Self::Chrome => "Chrome/Edge (MV3)",
            Self::Firefox => "Firefox (MV2)",
        }
    }
}

fn browser_extension_targets(
    target: &str,
) -> Result<Vec<BrowserExtensionTarget>, PackageBrowserExtensionError> {
    match target {
        "Chrome" => Ok(vec![BrowserExtensionTarget::Chrome]),
        "Firefox" => Ok(vec![BrowserExtensionTarget::Firefox]),
        "All" => Ok(vec![
            BrowserExtensionTarget::Chrome,
            BrowserExtensionTarget::Firefox,
        ]),
        _ => Err(PackageBrowserExtensionError::UnsupportedTarget(
            target.to_string(),
        )),
    }
}

fn browser_extension_version(manifest_path: &Path) -> Result<String, PackageBrowserExtensionError> {
    let manifest = read_manifest_json(manifest_path)?;
    manifest
        .get("version")
        .and_then(Value::as_str)
        .map(ToOwned::to_owned)
        .filter(|version| !version.is_empty())
        .ok_or_else(|| PackageBrowserExtensionError::MissingVersion(manifest_path.to_path_buf()))
}

fn package_browser_extension_target(
    extension_dir: &Path,
    output_dir: &Path,
    version: &str,
    target: BrowserExtensionTarget,
) -> Result<BrowserExtensionPackage, PackageBrowserExtensionError> {
    let manifest_path = extension_dir.join(target.manifest_file());
    let mut manifest = read_manifest_json(&manifest_path)?;
    let Some(manifest_object) = manifest.as_object_mut() else {
        return Err(PackageBrowserExtensionError::ManifestNotObject(
            manifest_path,
        ));
    };
    manifest_object.remove("key");
    let manifest_bytes = serde_json::to_vec_pretty(&manifest).map_err(|error| {
        PackageBrowserExtensionError::InvalidManifestJson {
            path: manifest_path.clone(),
            message: error.to_string(),
        }
    })?;

    let output_path = output_dir.join(target.output_file_name(version));
    let file = File::create(&output_path).map_err(|error| PackageBrowserExtensionError::Io {
        path: output_path.clone(),
        message: error.to_string(),
    })?;
    let mut writer = ZipWriter::new(file);
    writer
        .start_file("manifest.json", zip_options())
        .map_err(|error| PackageBrowserExtensionError::Zip(error.to_string()))?;
    writer
        .write_all(&manifest_bytes)
        .map_err(|error| PackageBrowserExtensionError::Io {
            path: output_path.clone(),
            message: error.to_string(),
        })?;

    for relative_path in BROWSER_EXTENSION_COMMON_FILES {
        let source_path = extension_dir.join(relative_path);
        let entry_name = normalize_package_entry(relative_path)?;
        writer
            .start_file(entry_name, zip_options())
            .map_err(|error| PackageBrowserExtensionError::Zip(error.to_string()))?;
        let mut source =
            File::open(&source_path).map_err(|error| PackageBrowserExtensionError::Io {
                path: source_path.clone(),
                message: error.to_string(),
            })?;
        io::copy(&mut source, &mut writer).map_err(|error| PackageBrowserExtensionError::Io {
            path: source_path,
            message: error.to_string(),
        })?;
    }

    writer
        .finish()
        .map_err(|error| PackageBrowserExtensionError::Zip(error.to_string()))?;
    let bytes = fs::metadata(&output_path)
        .map_err(|error| PackageBrowserExtensionError::Io {
            path: output_path.clone(),
            message: error.to_string(),
        })?
        .len();

    Ok(BrowserExtensionPackage {
        label: target.label().to_string(),
        path: output_path,
        bytes,
    })
}

fn rust_portable_directory_entries(
    package_dir: &Path,
) -> Result<Vec<String>, ValidateRustPortableError> {
    let package_dir =
        fs::canonicalize(package_dir).map_err(|error| ValidateRustPortableError::Io {
            path: package_dir.to_path_buf(),
            message: error.to_string(),
        })?;
    let mut entries = Vec::new();
    collect_rust_portable_directory_entries(&package_dir, &package_dir, &mut entries)?;
    entries.sort();
    Ok(entries)
}

fn collect_rust_portable_directory_entries(
    root: &Path,
    current: &Path,
    entries: &mut Vec<String>,
) -> Result<(), ValidateRustPortableError> {
    let mut children = fs::read_dir(current)
        .map_err(|error| ValidateRustPortableError::Io {
            path: current.to_path_buf(),
            message: error.to_string(),
        })?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|error| ValidateRustPortableError::Io {
            path: current.to_path_buf(),
            message: error.to_string(),
        })?;
    children.sort_by_key(|entry| entry.path());

    for child in children {
        let child_path = child.path();
        entries.push(rust_portable_entry_name(root, &child_path)?);
        if child_path.is_dir() {
            collect_rust_portable_directory_entries(root, &child_path, entries)?;
        }
    }

    Ok(())
}

fn rust_portable_zip_entries(
    archive_path: &Path,
) -> Result<Vec<String>, ValidateRustPortableError> {
    let file = File::open(archive_path).map_err(|error| ValidateRustPortableError::Io {
        path: archive_path.to_path_buf(),
        message: error.to_string(),
    })?;
    let mut archive = ZipArchive::new(BufReader::new(file))
        .map_err(|error| ValidateRustPortableError::Zip(error.to_string()))?;
    let mut entries = Vec::new();

    for index in 0..archive.len() {
        let entry = archive
            .by_index(index)
            .map_err(|error| ValidateRustPortableError::Zip(error.to_string()))?;
        let Some(enclosed_name) = entry.enclosed_name() else {
            return Err(ValidateRustPortableError::InvalidArchiveEntry(
                entry.name().to_string(),
            ));
        };
        let name = rust_portable_path_entry_name(&enclosed_name).ok_or_else(|| {
            ValidateRustPortableError::InvalidArchiveEntry(entry.name().to_string())
        })?;
        entries.push(name);
    }

    entries.sort();
    Ok(entries)
}

fn rust_portable_entry_name(root: &Path, path: &Path) -> Result<String, ValidateRustPortableError> {
    let relative = path
        .strip_prefix(root)
        .map_err(|error| ValidateRustPortableError::Io {
            path: path.to_path_buf(),
            message: error.to_string(),
        })?;
    rust_portable_path_entry_name(relative)
        .ok_or_else(|| ValidateRustPortableError::UnsupportedPackagePath(path.to_path_buf()))
}

fn rust_portable_path_entry_name(path: &Path) -> Option<String> {
    let components = path
        .components()
        .filter_map(|component| match component {
            std::path::Component::Normal(value) => value.to_str(),
            _ => None,
        })
        .collect::<Vec<_>>();
    (!components.is_empty()).then(|| components.join("/"))
}

fn rust_portable_entry_is_forbidden(entry_name: &str) -> bool {
    let normalized = entry_name.replace('\\', "/").trim_matches('/').to_string();
    let components = normalized.split('/').collect::<Vec<_>>();
    if components.is_empty() {
        return false;
    }

    let root = components[0].to_ascii_lowercase();
    if root == "dotnet" || root == "workers" {
        return true;
    }
    if rust_portable_entry_contains_dotnet_runtime_layout(&components) {
        return true;
    }

    let Some(file_name) = components.last() else {
        return false;
    };
    let file_name = file_name.to_ascii_lowercase();
    matches!(
        file_name.as_str(),
        "createdump.exe"
            | "dotnet.exe"
            | "hostfxr.dll"
            | "coreclr.dll"
            | "hostpolicy.dll"
            | "clrjit.dll"
            | "mscordaccore.dll"
            | "mscordbi.dll"
            | "mscorlib.dll"
            | "netstandard.dll"
            | "singlefilehost.exe"
            | "system.private.corelib.dll"
            | "windowsbase.dll"
            | "presentationcore.dll"
            | "presentationframework.dll"
    ) || file_name.ends_with(".runtimeconfig.json")
        || file_name.ends_with(".deps.json")
        || file_name.starts_with("easydict.compathost")
        || file_name.starts_with("easydict.nativebridge")
        || file_name.starts_with("easydict.sidecarclient")
        || file_name.starts_with("easydict.workers.")
        || file_name.starts_with("easydict.winui")
        || (file_name.starts_with("system.") && file_name.ends_with(".dll"))
        || file_name.starts_with("microsoft.csharp")
        || file_name.starts_with("microsoft.visualbasic")
        || file_name.starts_with("microsoft.win32")
        || FORBIDDEN_RUST_PORTABLE_DOTNET_ASSEMBLIES.contains(&file_name.as_str())
        || FORBIDDEN_RUST_PORTABLE_WORKER_SHARED_DOTNET_ASSEMBLIES.contains(&file_name.as_str())
}

fn rust_portable_entry_is_allowed(entry_name: &str) -> bool {
    RUST_PORTABLE_REQUIRED_ENTRIES.contains(&entry_name)
}

fn rust_portable_entry_contains_dotnet_runtime_layout(components: &[&str]) -> bool {
    components.windows(2).any(|window| {
        let parent = window[0].to_ascii_lowercase();
        let child = window[1].to_ascii_lowercase();
        (parent == "host" && child == "fxr")
            || (parent == "shared"
                && FORBIDDEN_RUST_PORTABLE_DOTNET_SHARED_FRAMEWORKS.contains(&child.as_str()))
    })
}

const FORBIDDEN_RUST_PORTABLE_DOTNET_SHARED_FRAMEWORKS: &[&str] = &[
    "microsoft.netcore.app",
    "microsoft.windowsdesktop.app",
    "microsoft.aspnetcore.app",
];

const FORBIDDEN_RUST_PORTABLE_DOTNET_ASSEMBLIES: &[&str] = &[
    "easydict.documentexport.dll",
    "easydict.llm.streaming.dll",
    "easydict.openvino.dll",
    "easydict.sidecarclient.dll",
    "easydict.translationservice.dll",
    "easydict.windowsai.dll",
    "lexindex.dll",
    "mdict.csharp.dll",
    "polyglot.textlayout.dll",
];

const FORBIDDEN_RUST_PORTABLE_WORKER_SHARED_DOTNET_ASSEMBLIES: &[&str] = &[
    "microsoft.interactiveexperiences.projection.dll",
    "microsoft.web.webview2.core.projection.dll",
    "microsoft.windows.sdk.net.dll",
    "microsoft.windows.ui.xaml.dll",
    "microsoft.winui.dll",
    "winrt.runtime.dll",
];

fn read_manifest_json(manifest_path: &Path) -> Result<Value, PackageBrowserExtensionError> {
    if !manifest_path.is_file() {
        return Err(PackageBrowserExtensionError::ManifestMissing(
            manifest_path.to_path_buf(),
        ));
    }
    let text =
        fs::read_to_string(manifest_path).map_err(|error| PackageBrowserExtensionError::Io {
            path: manifest_path.to_path_buf(),
            message: error.to_string(),
        })?;
    serde_json::from_str(&text).map_err(|error| PackageBrowserExtensionError::InvalidManifestJson {
        path: manifest_path.to_path_buf(),
        message: error.to_string(),
    })
}

fn normalize_package_entry(relative_path: &str) -> Result<String, PackageBrowserExtensionError> {
    let path = Path::new(relative_path);
    if path.components().any(|component| {
        matches!(
            component,
            std::path::Component::ParentDir
                | std::path::Component::RootDir
                | std::path::Component::Prefix(_)
        )
    }) {
        return Err(PackageBrowserExtensionError::InvalidEntryPath(
            path.to_path_buf(),
        ));
    }
    Ok(relative_path.replace('\\', "/"))
}

fn zip_options() -> FileOptions<'static, ()> {
    FileOptions::default().compression_method(CompressionMethod::Deflated)
}

fn canonicalize_required_dir(path: &Path) -> Result<PathBuf, ZipDirectoryError> {
    if !path.exists() {
        return Err(ZipDirectoryError::SourceMissing(path.to_path_buf()));
    }
    if !path.is_dir() {
        return Err(ZipDirectoryError::SourceNotDirectory(path.to_path_buf()));
    }
    fs::canonicalize(path).map_err(|error| ZipDirectoryError::Io {
        path: path.to_path_buf(),
        message: error.to_string(),
    })
}

fn normalize_destination_path(path: &Path) -> Result<PathBuf, ZipDirectoryError> {
    if let Some(parent) = path.parent() {
        if parent.exists() {
            let parent = fs::canonicalize(parent).map_err(|error| ZipDirectoryError::Io {
                path: parent.to_path_buf(),
                message: error.to_string(),
            })?;
            let Some(file_name) = path.file_name() else {
                return Err(ZipDirectoryError::InvalidEntryPath(path.to_path_buf()));
            };
            return Ok(parent.join(file_name));
        }
    }

    Ok(path.to_path_buf())
}

fn entry_name(root: &Path, path: &Path) -> Result<String, ZipDirectoryError> {
    let relative = path
        .strip_prefix(root)
        .map_err(|_| ZipDirectoryError::InvalidEntryPath(path.to_path_buf()))?;
    let name = relative
        .components()
        .map(|component| component.as_os_str().to_string_lossy())
        .collect::<Vec<_>>()
        .join("/");
    if name.is_empty() {
        return Err(ZipDirectoryError::InvalidEntryPath(path.to_path_buf()));
    }
    Ok(name)
}

fn should_skip(path: &Path, excludes: &[String]) -> bool {
    let Some(extension) = path.extension().and_then(|extension| extension.to_str()) else {
        return false;
    };
    let extension = normalize_extension(extension);
    excludes.iter().any(|exclude| exclude == &extension)
}

fn normalize_extension(extension: &str) -> String {
    extension
        .trim()
        .trim_start_matches('.')
        .to_ascii_lowercase()
}

fn validate_runtime_rid(rid: &str) -> Result<(), ExtractDotnetRuntimeError> {
    match rid {
        "win-x64" | "win-arm64" => Ok(()),
        _ => Err(ExtractDotnetRuntimeError::UnsupportedRid(rid.to_string())),
    }
}

fn first_directory_name(path: &Path) -> Result<String, ExtractDotnetRuntimeError> {
    let mut directories = fs::read_dir(path)
        .map_err(|error| ExtractDotnetRuntimeError::Io {
            path: path.to_path_buf(),
            message: error.to_string(),
        })?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|error| ExtractDotnetRuntimeError::Io {
            path: path.to_path_buf(),
            message: error.to_string(),
        })?;
    directories.sort_by_key(|entry| entry.path());
    directories
        .into_iter()
        .find(|entry| entry.path().is_dir())
        .and_then(|entry| entry.file_name().into_string().ok())
        .ok_or_else(|| ExtractDotnetRuntimeError::MissingBundledVersion(path.to_path_buf()))
}

fn directory_size(path: &Path) -> Result<u64, ExtractDotnetRuntimeError> {
    let mut total = 0;
    for entry in fs::read_dir(path).map_err(|error| ExtractDotnetRuntimeError::Io {
        path: path.to_path_buf(),
        message: error.to_string(),
    })? {
        let entry = entry.map_err(|error| ExtractDotnetRuntimeError::Io {
            path: path.to_path_buf(),
            message: error.to_string(),
        })?;
        let entry_path = entry.path();
        let metadata =
            fs::metadata(&entry_path).map_err(|error| ExtractDotnetRuntimeError::Io {
                path: entry_path.clone(),
                message: error.to_string(),
            })?;
        if metadata.is_dir() {
            total += directory_size(&entry_path)?;
        } else if metadata.is_file() {
            total += metadata.len();
        }
    }
    Ok(total)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Read;
    use std::sync::Mutex;
    use zip::ZipArchive;

    static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

    #[test]
    fn zip_directory_writes_source_contents_and_excludes_extensions() {
        let temp = tempfile_dir("zip-ok");
        write_file(&temp, "app.exe", b"exe");
        write_file(&temp, "app.pdb", b"pdb");
        write_file(&temp, "nested/data.txt", b"data");
        fs::create_dir_all(temp.join("empty")).expect("empty dir");
        let zip_path = temp.with_extension("zip");
        let options = ZipDirectoryOptions {
            source_dir: temp.clone(),
            destination_zip: zip_path.clone(),
            exclude_extensions: vec![".pdb".to_string()],
        };

        let outcome = zip_directory(&options).expect("zip directory");

        assert_eq!(outcome.file_count, 2);
        assert_eq!(outcome.directory_count, 1);
        assert_eq!(outcome.skipped_count, 1);
        assert!(outcome.bytes_written > 0);
        let entries = zip_entries(&zip_path);
        assert_eq!(entries, vec!["app.exe", "empty/", "nested/data.txt"]);
        let _ = fs::remove_dir_all(temp);
        let _ = fs::remove_file(zip_path);
    }

    #[test]
    fn zip_directory_rejects_destination_inside_source() {
        let temp = tempfile_dir("zip-destination-inside");
        let options = ZipDirectoryOptions {
            source_dir: temp.clone(),
            destination_zip: temp.join("out.zip"),
            exclude_extensions: Vec::new(),
        };

        let error = zip_directory(&options).unwrap_err();

        assert!(matches!(
            error,
            ZipDirectoryError::DestinationInsideSource { .. }
        ));
        let _ = fs::remove_dir_all(temp);
    }

    #[test]
    fn zip_directory_reports_missing_source() {
        let missing = tempfile_dir("zip-missing");
        fs::remove_dir_all(&missing).expect("remove temp");
        let options = ZipDirectoryOptions {
            source_dir: missing.clone(),
            destination_zip: missing.with_extension("zip"),
            exclude_extensions: Vec::new(),
        };

        assert_eq!(
            zip_directory(&options).unwrap_err(),
            ZipDirectoryError::SourceMissing(missing)
        );
    }

    #[test]
    fn validate_rs_portable_accepts_rust_only_directory_payload() {
        let package = tempfile_dir("rs-portable-ok");
        write_rust_portable_allowed_payload(&package);

        let outcome = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .expect("Rust-only portable payload should validate");

        assert_eq!(
            outcome.checked_entries,
            RUST_PORTABLE_REQUIRED_ENTRIES.len()
        );
        let _ = fs::remove_dir_all(package);
    }

    #[test]
    fn validate_rs_portable_rejects_missing_required_first_release_payload() {
        let package = tempfile_dir("rs-portable-missing-required");
        write_file(&package, "Easydict.Rust.exe", b"gui");
        write_file(&package, "README-portable.txt", b"readme");

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::MissingRequiredEntries(entries) = error else {
            panic!("expected missing required entries");
        };
        assert!(entries
            .iter()
            .any(|entry| entry == "easydict-native-bridge.exe"));
        assert!(entries
            .iter()
            .any(|entry| entry == "easydict_browser_registrar.exe"));
        let _ = fs::remove_dir_all(package);
    }

    #[test]
    fn validate_rs_portable_rejects_unknown_payload_outside_first_release_allowlist() {
        let package = tempfile_dir("rs-portable-unknown-payload");
        write_rust_portable_allowed_payload(&package);
        write_file(&package, "SomePayload.dll", b"unknown");
        write_file(&package, "BrowserHostRegistrar.exe", b"legacy-alias");

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::UnexpectedEntries(entries) = error else {
            panic!("expected unexpected entries");
        };
        assert_eq!(entries, vec!["BrowserHostRegistrar.exe", "SomePayload.dll"]);
        let _ = fs::remove_dir_all(package);
    }

    #[test]
    fn validate_rs_portable_rejects_retained_dotnet_directory_payload() {
        let package = tempfile_dir("rs-portable-bad-dir");
        write_rust_portable_allowed_payload(&package);
        write_file(&package, "dotnet/host/fxr/8.0.11/hostfxr.dll", b"hostfxr");
        write_file(
            &package,
            "workers/localai/Easydict.Workers.LocalAi.exe",
            b"worker",
        );
        write_file(&package, "Easydict.CompatHost.exe", b"compathost");
        write_file(&package, "System.Private.CoreLib.dll", b"corelib");
        write_file(
            &package,
            "Easydict.TranslationService.dll",
            b"translation-service",
        );
        write_file(&package, "Easydict.OpenVINO.dll", b"openvino");
        write_file(&package, "Polyglot.TextLayout.dll", b"text-layout");
        write_file(&package, "MDict.Csharp.dll", b"mdict");
        write_file(&package, "dotnet.exe", b"dotnet");
        write_file(&package, "WindowsBase.dll", b"windows-base");
        write_file(&package, "Microsoft.CSharp.dll", b"csharp");
        write_file(&package, "Microsoft.WinUI.dll", b"winui");
        write_file(&package, "WinRT.Runtime.dll", b"winrt");
        write_file(
            &package,
            "shared/Microsoft.NETCore.App/8.0.11/placeholder.txt",
            b"runtime-layout",
        );
        write_file(
            &package,
            "shared/Microsoft.AspNetCore.App/8.0.11/placeholder.txt",
            b"aspnet-runtime-layout",
        );
        write_file(&package, "host/fxr/8.0.11/placeholder.txt", b"host-layout");

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden entries");
        };
        assert!(entries.iter().any(|entry| entry == "dotnet"));
        assert!(entries.iter().any(|entry| entry == "workers"));
        assert!(entries
            .iter()
            .any(|entry| entry == "Easydict.CompatHost.exe"));
        assert!(entries
            .iter()
            .any(|entry| entry == "System.Private.CoreLib.dll"));
        assert!(entries
            .iter()
            .any(|entry| entry == "Easydict.TranslationService.dll"));
        assert!(entries.iter().any(|entry| entry == "Easydict.OpenVINO.dll"));
        assert!(entries
            .iter()
            .any(|entry| entry == "Polyglot.TextLayout.dll"));
        assert!(entries.iter().any(|entry| entry == "MDict.Csharp.dll"));
        assert!(entries.iter().any(|entry| entry == "dotnet.exe"));
        assert!(entries.iter().any(|entry| entry == "WindowsBase.dll"));
        assert!(entries.iter().any(|entry| entry == "Microsoft.CSharp.dll"));
        assert!(entries.iter().any(|entry| entry == "Microsoft.WinUI.dll"));
        assert!(entries.iter().any(|entry| entry == "WinRT.Runtime.dll"));
        assert!(entries
            .iter()
            .any(|entry| { entry == "shared/Microsoft.NETCore.App/8.0.11/placeholder.txt" }));
        assert!(entries
            .iter()
            .any(|entry| { entry == "shared/Microsoft.AspNetCore.App/8.0.11/placeholder.txt" }));
        assert!(entries
            .iter()
            .any(|entry| entry == "host/fxr/8.0.11/placeholder.txt"));
        let _ = fs::remove_dir_all(package);
    }

    #[test]
    fn validate_rs_portable_rejects_retained_dotnet_zip_payload() {
        let package = tempfile_dir("rs-portable-bad-zip");
        write_rust_portable_allowed_payload(&package);
        write_file(&package, "Easydict.WinUI.deps.json", b"deps");
        write_file(&package, "nested/Easydict.CompatHost.exe", b"compathost");
        write_file(&package, "nested/hostpolicy.dll", b"hostpolicy");
        write_file(&package, "nested/Easydict.SidecarClient.dll", b"sidecar");
        write_file(&package, "nested/Easydict.Llm.Streaming.dll", b"streaming");
        write_file(&package, "nested/Easydict.WindowsAI.dll", b"windows-ai");
        write_file(&package, "nested/LexIndex.dll", b"lex-index");
        write_file(
            &package,
            "nested/Easydict.NativeBridge.exe",
            b"nativebridge",
        );
        write_file(
            &package,
            "nested/Easydict.SidecarClient.exe",
            b"sidecar-exe",
        );
        write_file(&package, "nested/System.Text.Json.dll", b"system-json");
        write_file(&package, "nested/createdump.exe", b"createdump");
        write_file(&package, "nested/netstandard.dll", b"netstandard");
        write_file(&package, "nested/PresentationCore.dll", b"presentation");
        write_file(&package, "nested/Microsoft.Win32.Registry.dll", b"registry");
        write_file(
            &package,
            "nested/Microsoft.Windows.SDK.NET.dll",
            b"windows-sdk-net",
        );
        write_file(
            &package,
            "nested/Microsoft.Web.WebView2.Core.Projection.dll",
            b"webview2-projection",
        );
        write_file(
            &package,
            "runtime/shared/Microsoft.WindowsDesktop.App/8.0.11/placeholder.txt",
            b"desktop-runtime-layout",
        );
        write_file(
            &package,
            "runtime/host/fxr/8.0.11/placeholder.txt",
            b"host-layout",
        );
        let zip_path = package.with_extension("zip");
        zip_directory(&ZipDirectoryOptions {
            source_dir: package.clone(),
            destination_zip: zip_path.clone(),
            exclude_extensions: Vec::new(),
        })
        .expect("create test zip");

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: zip_path.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden entries");
        };
        assert!(entries
            .iter()
            .any(|entry| entry == "Easydict.WinUI.deps.json"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/Easydict.CompatHost.exe"));
        assert!(entries.iter().any(|entry| entry == "nested/hostpolicy.dll"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/Easydict.SidecarClient.dll"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/Easydict.Llm.Streaming.dll"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/Easydict.WindowsAI.dll"));
        assert!(entries.iter().any(|entry| entry == "nested/LexIndex.dll"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/Easydict.NativeBridge.exe"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/Easydict.SidecarClient.exe"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/System.Text.Json.dll"));
        assert!(entries.iter().any(|entry| entry == "nested/createdump.exe"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/netstandard.dll"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/PresentationCore.dll"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/Microsoft.Win32.Registry.dll"));
        assert!(entries
            .iter()
            .any(|entry| entry == "nested/Microsoft.Windows.SDK.NET.dll"));
        assert!(entries
            .iter()
            .any(|entry| { entry == "nested/Microsoft.Web.WebView2.Core.Projection.dll" }));
        assert!(entries.iter().any(|entry| {
            entry == "runtime/shared/Microsoft.WindowsDesktop.App/8.0.11/placeholder.txt"
        }));
        assert!(entries
            .iter()
            .any(|entry| entry == "runtime/host/fxr/8.0.11/placeholder.txt"));
        let _ = fs::remove_dir_all(package);
        let _ = fs::remove_file(zip_path);
    }

    #[test]
    fn validate_rs_portable_rejects_zip_path_traversal_entry() {
        let package = tempfile_dir("rs-portable-path-traversal");
        let zip_path = package.with_extension("zip");
        let file = File::create(&zip_path).expect("test zip should be created");
        let mut writer = ZipWriter::new(file);
        let options = FileOptions::<()>::default().compression_method(CompressionMethod::Stored);
        writer
            .start_file("../hostfxr.dll", options)
            .expect("path traversal entry should be written");
        writer
            .write_all(b"hostfxr")
            .expect("path traversal entry contents should be written");
        writer.finish().expect("test zip should be finalized");

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: zip_path.clone(),
        })
        .unwrap_err();

        assert_eq!(
            error,
            ValidateRustPortableError::InvalidArchiveEntry("../hostfxr.dll".to_string())
        );
        let _ = fs::remove_dir_all(package);
        let _ = fs::remove_file(zip_path);
    }

    #[test]
    fn rust_portable_package_name_matches_coexistence_contract() {
        assert_eq!(
            rust_portable_package_name(None, "x64"),
            "easydict-rs-portable-win-x64"
        );
        assert_eq!(
            rust_portable_package_name(Some("v1.2.3"), "arm64"),
            "easydict-rs-portable-v1.2.3-win-arm64"
        );
        assert_eq!(
            rust_portable_package_name(Some("   "), "x86"),
            "easydict-rs-portable-win-x86"
        );
    }

    #[test]
    fn rust_portable_preview_build_arguments_match_packaging_contract() {
        let release_args = preview_app_cargo_args("x86_64-pc-windows-msvc", "Release");
        assert_rust_portable_cargo_args_do_not_enable_retained_dotnet(&release_args);
        assert_eq!(
            release_args,
            vec![
                "build",
                "-p",
                "easydict_preview_iced",
                "--target",
                "x86_64-pc-windows-msvc",
                "--release"
            ]
        );
        let debug_args = preview_app_cargo_args("i686-pc-windows-msvc", "Debug");
        assert_rust_portable_cargo_args_do_not_enable_retained_dotnet(&debug_args);
        assert_eq!(
            debug_args,
            vec![
                "build",
                "-p",
                "easydict_preview_iced",
                "--target",
                "i686-pc-windows-msvc"
            ]
        );
    }

    #[test]
    fn rust_packager_cargo_environment_forces_rust_only_child_builds() {
        assert_eq!(
            rust_portable_cargo_environment(),
            [
                ("EASYDICT_RUNTIME_PROFILE", "rust-only"),
                ("RUNTIME_PROFILE", "rust-only")
            ]
        );
    }

    #[test]
    fn stage_rust_portable_payload_copies_preview_helpers_and_readme() {
        let workspace = tempfile_dir("rs-portable-stage-workspace");
        let built_dir = workspace
            .join("target")
            .join("x86_64-pc-windows-msvc")
            .join("debug");
        write_file(&built_dir, "easydict_preview_iced.exe", b"preview");
        for exe_name in RUST_HELPER_EXECUTABLES {
            write_file(&built_dir, exe_name, exe_name.as_bytes());
        }
        let package = tempfile_dir("rs-portable-stage-output");

        stage_rust_portable_payload(&workspace, "x86_64-pc-windows-msvc", "debug", &package)
            .expect("stage portable payload");

        assert_eq!(
            fs::read(package.join("Easydict.Rust.exe")).expect("read GUI alias"),
            b"preview"
        );
        for exe_name in RUST_HELPER_EXECUTABLES {
            assert!(
                package.join(exe_name).is_file(),
                "{exe_name} should be copied"
            );
        }
        assert!(
            !package.join("BrowserHostRegistrar.exe").exists(),
            "first rs portable payload should not carry the legacy registrar alias"
        );
        assert!(fs::read_to_string(package.join("README-portable.txt"))
            .expect("read readme")
            .contains("does not include MSIX metadata"));
        validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .expect("staged payload should pass validator");
        assert!(!package.join("Easydict.WinUI.exe").exists());
        assert!(!package.join("workers").exists());
        assert!(!package.join("dotnet").exists());

        let _ = fs::remove_dir_all(workspace);
        let _ = fs::remove_dir_all(package);
    }

    #[test]
    fn download_dotnet_runtime_requires_explicit_hybrid_profile_before_network() {
        let output = tempfile_dir("dotnet-runtime-profile-rust-only");
        let error = download_and_extract_dotnet_runtime(&ExtractDotnetRuntimeOptions {
            rid: "win-x64".to_string(),
            output_dir: output.clone(),
            version: "8.0.11".to_string(),
            runtime_profile: PackageRuntimeProfile::RustOnly,
        })
        .unwrap_err();

        assert_eq!(
            error,
            ExtractDotnetRuntimeError::RuntimeProfileMustBeHybrid(PackageRuntimeProfile::RustOnly)
        );
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    fn download_dotnet_runtime_rejects_rust_only_environment_before_network() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = RuntimeProfileEnvironmentSnapshot::capture();
        clear_runtime_profile_environment();
        std::env::set_var("RUNTIME_PROFILE", "rust-only");

        let output = tempfile_dir("dotnet-runtime-profile-env-rust-only");
        let error = download_and_extract_dotnet_runtime(&ExtractDotnetRuntimeOptions {
            rid: "win-x64".to_string(),
            output_dir: output.clone(),
            version: "8.0.11".to_string(),
            runtime_profile: PackageRuntimeProfile::Hybrid,
        })
        .unwrap_err();

        assert_eq!(
            error,
            ExtractDotnetRuntimeError::RuntimeProfileEnvironmentRustOnly {
                name: "RUNTIME_PROFILE",
                value: "rust-only".to_string(),
            }
        );
        assert!(
            fs::read_dir(&output)
                .expect("read temp output")
                .next()
                .is_none(),
            "runtime guard should fail before creating extracted runtime files"
        );
        let _ = fs::remove_dir_all(output);
        snapshot.restore();
    }

    #[test]
    fn extract_dotnet_runtime_archive_writes_standard_layout_and_strips_notices() {
        let source = tempfile_dir("dotnet-source");
        let hostfxr_bytes = deterministic_bytes(2 * 1024 * 1024, 0x1234_5678);
        let coreclr_bytes = deterministic_bytes(2 * 1024 * 1024, 0x8765_4321);
        write_file(&source, "host/fxr/8.0.11/hostfxr.dll", &hostfxr_bytes);
        write_file(
            &source,
            "shared/Microsoft.NETCore.App/8.0.11/coreclr.dll",
            &coreclr_bytes,
        );
        write_file(&source, "LICENSE.txt", b"license");
        write_file(&source, "ThirdPartyNotices.txt", b"notice");
        let archive = source.with_extension("zip");
        zip_directory(&ZipDirectoryOptions {
            source_dir: source.clone(),
            destination_zip: archive.clone(),
            exclude_extensions: Vec::new(),
        })
        .expect("create runtime archive");
        let output = tempfile_dir("dotnet-output");

        let outcome = extract_dotnet_runtime_archive(&archive, &output).expect("extract runtime");

        assert_eq!(outcome.bundled_version, "8.0.11");
        assert!(outcome.archive_bytes >= 1024 * 1024);
        assert!(outcome.total_bytes >= 1024 * 1024);
        assert!(output.join("host/fxr/8.0.11/hostfxr.dll").is_file());
        assert!(output
            .join("shared/Microsoft.NETCore.App/8.0.11/coreclr.dll")
            .is_file());
        assert!(!output.join("LICENSE.txt").exists());
        assert!(!output.join("ThirdPartyNotices.txt").exists());
        let _ = fs::remove_dir_all(source);
        let _ = fs::remove_dir_all(output);
        let _ = fs::remove_file(archive);
    }

    #[test]
    fn extract_dotnet_runtime_archive_rejects_missing_layout() {
        let source = tempfile_dir("dotnet-bad-source");
        let bytes = deterministic_bytes(2 * 1024 * 1024, 0xfeed_beef);
        write_file(&source, "only.bin", &bytes);
        let archive = source.with_extension("zip");
        zip_directory(&ZipDirectoryOptions {
            source_dir: source.clone(),
            destination_zip: archive.clone(),
            exclude_extensions: Vec::new(),
        })
        .expect("create runtime archive");
        let output = tempfile_dir("dotnet-bad-output");

        let error = extract_dotnet_runtime_archive(&archive, &output).unwrap_err();

        assert!(matches!(
            error,
            ExtractDotnetRuntimeError::MissingExpectedDirectory(_)
        ));
        let _ = fs::remove_dir_all(source);
        let _ = fs::remove_dir_all(output);
        let _ = fs::remove_file(archive);
    }

    #[test]
    fn rust_helper_build_arguments_match_packaging_contract() {
        assert_eq!(
            cargo_target_for_platform("x64").unwrap(),
            "x86_64-pc-windows-msvc"
        );
        assert_eq!(
            cargo_target_for_platform("x86").unwrap(),
            "i686-pc-windows-msvc"
        );
        assert_eq!(
            cargo_target_for_platform("arm64").unwrap(),
            "aarch64-pc-windows-msvc"
        );
        assert_eq!(profile_dir_for_configuration("Debug").unwrap(), "debug");
        assert_eq!(profile_dir_for_configuration("Release").unwrap(), "release");

        let args = rust_helper_cargo_args("x86_64-pc-windows-msvc", "Release");

        assert!(args.ends_with(&["--release".to_string()]));
        assert_rust_portable_cargo_args_do_not_enable_retained_dotnet(&args);
        for bin in [
            "easydict-native-bridge",
            "easydict_browser_registrar",
            "easydict_cli",
            "easydict_long_doc",
        ] {
            assert!(args.windows(2).any(|pair| pair == ["--bin", bin]));
        }
    }

    #[test]
    fn copy_built_rust_helpers_copies_helpers_and_legacy_registrar_alias() {
        let workspace = tempfile_dir("rust-helper-workspace");
        let built_dir = workspace
            .join("target")
            .join("x86_64-pc-windows-msvc")
            .join("debug");
        for exe_name in RUST_HELPER_EXECUTABLES {
            write_file(&built_dir, exe_name, exe_name.as_bytes());
        }
        let output = tempfile_dir("rust-helper-output");

        let outcome =
            copy_built_rust_helpers(&workspace, "x86_64-pc-windows-msvc", "debug", &output)
                .expect("copy helpers");

        assert_eq!(outcome.copied_files.len(), 5);
        for exe_name in RUST_HELPER_EXECUTABLES {
            assert_eq!(
                fs::read(output.join(exe_name)).expect("read copied helper"),
                exe_name.as_bytes()
            );
        }
        assert_eq!(
            fs::read(output.join("BrowserHostRegistrar.exe")).expect("read alias"),
            b"easydict_browser_registrar.exe"
        );
        let _ = fs::remove_dir_all(workspace);
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    fn copy_built_rust_helpers_reports_missing_helper() {
        let workspace = tempfile_dir("rust-helper-missing-workspace");
        let output = tempfile_dir("rust-helper-missing-output");

        let error = copy_built_rust_helpers(&workspace, "x86_64-pc-windows-msvc", "debug", &output)
            .unwrap_err();

        assert!(matches!(error, BuildRustHelpersError::MissingHelper(_)));
        let _ = fs::remove_dir_all(workspace);
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    fn package_browser_extension_builds_chrome_and_firefox_packages() {
        let extension = browser_extension_source("browser-extension-all");
        let output = tempfile_dir("browser-extension-output");

        let outcome = package_browser_extension(&PackageBrowserExtensionOptions {
            extension_dir: extension.clone(),
            output_dir: Some(output.clone()),
            target: "All".to_string(),
        })
        .expect("package extension");

        assert_eq!(outcome.version, "1.2.3");
        assert_eq!(outcome.packages.len(), 2);

        let chrome = output.join("easydict-ocr-chrome-v1.2.3.zip");
        let firefox = output.join("easydict-ocr-firefox-v1.2.3.xpi");
        assert!(chrome.is_file());
        assert!(firefox.is_file());
        assert_eq!(zip_entries(&chrome), expected_browser_extension_entries());
        assert_eq!(zip_entries(&firefox), expected_browser_extension_entries());

        let chrome_manifest: serde_json::Value =
            serde_json::from_str(&zip_entry_text(&chrome, "manifest.json")).unwrap();
        let firefox_manifest: serde_json::Value =
            serde_json::from_str(&zip_entry_text(&firefox, "manifest.json")).unwrap();
        assert_eq!(chrome_manifest["manifest_version"], 3);
        assert_eq!(firefox_manifest["manifest_version"], 2);
        assert!(chrome_manifest.get("key").is_none());
        assert!(firefox_manifest.get("key").is_none());
        assert_eq!(zip_entry_text(&chrome, "background.js"), "bridge();");

        let _ = fs::remove_dir_all(extension);
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    fn package_browser_extension_reports_missing_common_file() {
        let extension = browser_extension_source("browser-extension-missing");
        fs::remove_file(extension.join("setup.js")).expect("remove setup");

        let error = package_browser_extension(&PackageBrowserExtensionOptions {
            extension_dir: extension.clone(),
            output_dir: None,
            target: "Chrome".to_string(),
        })
        .unwrap_err();

        let PackageBrowserExtensionError::RequiredFileMissing(path) = error else {
            panic!("expected missing common file error");
        };
        assert_eq!(
            path.file_name().and_then(|name| name.to_str()),
            Some("setup.js")
        );
        let _ = fs::remove_dir_all(extension);
    }

    #[test]
    fn package_browser_extension_rejects_unknown_target() {
        let extension = browser_extension_source("browser-extension-target");

        let error = package_browser_extension(&PackageBrowserExtensionOptions {
            extension_dir: extension.clone(),
            output_dir: None,
            target: "Safari".to_string(),
        })
        .unwrap_err();

        assert_eq!(
            error,
            PackageBrowserExtensionError::UnsupportedTarget("Safari".to_string())
        );
        let _ = fs::remove_dir_all(extension);
    }

    fn tempfile_dir(name: &str) -> PathBuf {
        let path =
            std::env::temp_dir().join(format!("easydict-packager-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).expect("create temp dir");
        path
    }

    fn write_file(root: &Path, relative_path: &str, bytes: &[u8]) {
        let path = root.join(relative_path);
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).expect("create parent");
        }
        fs::write(path, bytes).expect("write file");
    }

    fn write_rust_portable_allowed_payload(root: &Path) {
        for entry in RUST_PORTABLE_REQUIRED_ENTRIES {
            write_file(root, entry, entry.as_bytes());
        }
    }

    struct RuntimeProfileEnvironmentSnapshot {
        easydict_runtime_profile: Option<String>,
        runtime_profile: Option<String>,
    }

    impl RuntimeProfileEnvironmentSnapshot {
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

    fn clear_runtime_profile_environment() {
        std::env::remove_var("EASYDICT_RUNTIME_PROFILE");
        std::env::remove_var("RUNTIME_PROFILE");
    }

    fn restore_environment_value(name: &str, value: Option<String>) {
        if let Some(value) = value {
            std::env::set_var(name, value);
        } else {
            std::env::remove_var(name);
        }
    }

    fn browser_extension_source(name: &str) -> PathBuf {
        let root = tempfile_dir(name);
        write_file(
            &root,
            "manifest.json",
            br#"{
  "manifest_version": 3,
  "version": "1.2.3",
  "key": "web-store-managed"
}"#,
        );
        write_file(
            &root,
            "manifest.v2.json",
            br#"{
  "manifest_version": 2,
  "version": "1.2.3",
  "key": "temporary"
}"#,
        );
        write_file(&root, "background.js", b"bridge();");
        write_file(&root, "setup.html", b"<main></main>");
        write_file(&root, "setup.js", b"setup();");
        write_file(&root, "icons/icon16.png", b"16");
        write_file(&root, "icons/icon48.png", b"48");
        write_file(&root, "icons/icon128.png", b"128");
        write_file(
            &root,
            "_locales/en/messages.json",
            br#"{"name":{"message":"Easy"}}"#,
        );
        write_file(
            &root,
            "_locales/zh_CN/messages.json",
            br#"{"name":{"message":"Easy CN"}}"#,
        );
        write_file(&root, "icons/icon256.png", b"not packaged");
        root
    }

    fn expected_browser_extension_entries() -> Vec<String> {
        let mut entries = vec!["manifest.json".to_string()];
        entries.extend(
            BROWSER_EXTENSION_COMMON_FILES
                .iter()
                .map(|entry| entry.to_string()),
        );
        entries
    }

    fn deterministic_bytes(length: usize, seed: u32) -> Vec<u8> {
        let mut value = seed;
        let mut bytes = Vec::with_capacity(length);
        for _ in 0..length {
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            bytes.push((value & 0xff) as u8);
        }
        bytes
    }

    fn zip_entries(path: &Path) -> Vec<String> {
        let file = File::open(path).expect("open zip");
        let mut archive = ZipArchive::new(file).expect("zip archive");
        let mut entries = Vec::new();
        for index in 0..archive.len() {
            let entry = archive.by_index(index).expect("zip entry");
            entries.push(entry.name().to_string());
        }
        entries
    }

    fn zip_entry_text(path: &Path, entry_name: &str) -> String {
        let file = File::open(path).expect("open zip");
        let mut archive = ZipArchive::new(file).expect("zip archive");
        let mut entry = archive.by_name(entry_name).expect("zip entry");
        let mut text = String::new();
        entry.read_to_string(&mut text).expect("read zip entry");
        text
    }

    fn assert_rust_portable_cargo_args_do_not_enable_retained_dotnet(args: &[String]) {
        assert!(
            !args.iter().any(|arg| arg == "--features"
                || arg == "--all-features"
                || arg.contains("retained-dotnet-workers")),
            "rs portable cargo args must keep retained .NET worker bridge disabled: {args:?}"
        );
    }
}

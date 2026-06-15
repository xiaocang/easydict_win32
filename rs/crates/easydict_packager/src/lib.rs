use std::collections::HashMap;
use std::fmt;
use std::fs::{self, File};
use std::io::{self, BufReader, Read, Seek, Write};
use std::path::{Path, PathBuf};

use serde_json::Value;
use sha2::{Digest, Sha256};
use zip::write::FileOptions;
use zip::{CompressionMethod, ZipArchive, ZipWriter};

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ZipDirectoryOptions {
    pub source_dir: PathBuf,
    pub destination_zip: PathBuf,
    pub exclude_extensions: Vec<String>,
}

#[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
#[derive(Clone, Debug, Eq, PartialEq)]
struct ZipDirectoryOptions {
    source_dir: PathBuf,
    destination_zip: PathBuf,
    exclude_extensions: Vec<String>,
}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ZipDirectoryOutcome {
    pub file_count: usize,
    pub directory_count: usize,
    pub skipped_count: usize,
    pub bytes_written: u64,
}

#[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
#[derive(Clone, Debug, Eq, PartialEq)]
struct ZipDirectoryOutcome {
    file_count: usize,
    directory_count: usize,
    skipped_count: usize,
    bytes_written: u64,
}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ExtractDotnetRuntimeOptions {
    pub rid: String,
    pub output_dir: PathBuf,
    pub version: String,
    pub runtime_profile: PackageRuntimeProfile,
}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    pub include_legacy_registrar_alias: bool,
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    pub runtime_profile: Option<PackageRuntimeProfile>,
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
pub struct WriteSha256ChecksumOptions {
    pub package_path: PathBuf,
    pub checksum_path: Option<PathBuf>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WriteSha256ChecksumOutcome {
    pub checksum_path: PathBuf,
    pub checksum: String,
    pub package_name: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VerifySha256ChecksumOptions {
    pub package_path: PathBuf,
    pub checksum_path: PathBuf,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VerifySha256ChecksumOutcome {
    pub checksum: String,
    pub package_name: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BrowserExtensionPackage {
    pub label: String,
    pub path: PathBuf,
    pub bytes: u64,
}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
#[derive(Debug, Eq, PartialEq)]
pub enum ZipDirectoryError {
    SourceMissing(PathBuf),
    SourceNotDirectory(PathBuf),
    DestinationInsideSource {
        source: PathBuf,
        destination: PathBuf,
    },
    InvalidEntryPath(PathBuf),
    UnsupportedDirectoryEntry(PathBuf),
    Io {
        path: PathBuf,
        message: String,
    },
    Zip(String),
}

#[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
#[derive(Debug, Eq, PartialEq)]
enum ZipDirectoryError {
    SourceMissing(PathBuf),
    SourceNotDirectory(PathBuf),
    DestinationInsideSource {
        source: PathBuf,
        destination: PathBuf,
    },
    InvalidEntryPath(PathBuf),
    UnsupportedDirectoryEntry(PathBuf),
    Io {
        path: PathBuf,
        message: String,
    },
    Zip(String),
}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    LegacyRegistrarAliasRequiresHybridPackagingFeature,
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    LegacyRegistrarAliasRequiresHybridProfile(Option<PackageRuntimeProfile>),
    WorkspaceMissing(PathBuf),
    WindowsAiManifestMissing(PathBuf),
    RustupFailed {
        exit_code: Option<i32>,
    },
    CargoFailed {
        exit_code: Option<i32>,
    },
    MissingHelper(PathBuf),
    UnsafeBuildArtifactSource {
        source: PathBuf,
        build_dir: PathBuf,
    },
    Io {
        path: PathBuf,
        message: String,
    },
}

#[derive(Debug, Eq, PartialEq)]
pub enum PackageBrowserExtensionError {
    UnsupportedTarget(String),
    ExtensionDirMissing(PathBuf),
    ManifestMissing(PathBuf),
    RequiredFileMissing(PathBuf),
    UnsupportedSourceEntry(PathBuf),
    RetainedRuntimeMarker(PathBuf),
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
    WindowsAiManifestMissing(PathBuf),
    CargoFailed {
        command: &'static str,
        exit_code: Option<i32>,
    },
    MissingExecutable(PathBuf),
    UnsafeBuildArtifactSource {
        source: PathBuf,
        build_dir: PathBuf,
    },
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
    UnsupportedDirectoryEntry(PathBuf),
    ForbiddenEntries(Vec<String>),
    MissingRequiredEntries(Vec<String>),
    UnexpectedEntries(Vec<String>),
    Io { path: PathBuf, message: String },
    Zip(String),
}

#[derive(Debug, Eq, PartialEq)]
pub enum Sha256ChecksumError {
    PackageMissing(PathBuf),
    PackageNotFile(PathBuf),
    ChecksumMissing(PathBuf),
    ChecksumNotFile(PathBuf),
    MissingPackageFileName(PathBuf),
    InvalidChecksumFile(PathBuf),
    PackageNameMismatch {
        checksum_path: PathBuf,
        expected: String,
        actual: String,
    },
    ChecksumMismatch {
        package_path: PathBuf,
        expected: String,
        actual: String,
    },
    Io {
        path: PathBuf,
        message: String,
    },
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
            Self::UnsupportedDirectoryEntry(path) => write!(
                formatter,
                "zip-directory does not support symlink or reparse-point entries: {}",
                path.display()
            ),
            Self::Io { path, message } => write!(formatter, "{}: {message}", path.display()),
            Self::Zip(message) => write!(formatter, "{message}"),
        }
    }
}

impl std::error::Error for ZipDirectoryError {}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
            #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
            Self::LegacyRegistrarAliasRequiresHybridProfile(_) => write!(
                formatter,
                "legacy BrowserHostRegistrar.exe alias requires explicit --runtime-profile hybrid"
            ),
            #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
            Self::LegacyRegistrarAliasRequiresHybridPackagingFeature => write!(
                formatter,
                "legacy BrowserHostRegistrar.exe alias requires the hybrid-dotnet-runtime-packaging feature"
            ),
            Self::WorkspaceMissing(path) => {
                write!(formatter, "Rust workspace not found at {}", path.display())
            }
            Self::WindowsAiManifestMissing(path) => write!(
                formatter,
                "WindowsAI WinRT binding preflight manifest not found at {}",
                path.display()
            ),
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
            Self::UnsafeBuildArtifactSource { source, build_dir } => write!(
                formatter,
                "Rust build artifact {} resolves outside build output directory {}",
                source.display(),
                build_dir.display()
            ),
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
            Self::UnsupportedSourceEntry(path) => write!(
                formatter,
                "browser extension package does not support symlink or reparse-point source entries: {}",
                path.display()
            ),
            Self::RetainedRuntimeMarker(path) => write!(
                formatter,
                "browser extension source file contains retained .NET runtime marker: {}",
                path.display()
            ),
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
            Self::WindowsAiManifestMissing(path) => write!(
                formatter,
                "WindowsAI WinRT binding preflight manifest not found at {}",
                path.display()
            ),
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
            Self::UnsafeBuildArtifactSource { source, build_dir } => write!(
                formatter,
                "Rust build artifact {} resolves outside build output directory {}",
                source.display(),
                build_dir.display()
            ),
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
            Self::UnsupportedDirectoryEntry(path) => write!(
                formatter,
                "Rust portable package contains an unsupported directory entry: {}",
                path.display()
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

impl fmt::Display for Sha256ChecksumError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::PackageMissing(path) => {
                write!(formatter, "package file not found: {}", path.display())
            }
            Self::PackageNotFile(path) => {
                write!(formatter, "package path is not a file: {}", path.display())
            }
            Self::ChecksumMissing(path) => {
                write!(formatter, "checksum file not found: {}", path.display())
            }
            Self::ChecksumNotFile(path) => {
                write!(formatter, "checksum path is not a file: {}", path.display())
            }
            Self::MissingPackageFileName(path) => write!(
                formatter,
                "package path has no file name for checksum output: {}",
                path.display()
            ),
            Self::InvalidChecksumFile(path) => {
                write!(
                    formatter,
                    "invalid SHA256 checksum file: {}",
                    path.display()
                )
            }
            Self::PackageNameMismatch {
                checksum_path,
                expected,
                actual,
            } => write!(
                formatter,
                "checksum file {} references {actual}, expected {expected}",
                checksum_path.display()
            ),
            Self::ChecksumMismatch {
                package_path,
                expected,
                actual,
            } => write!(
                formatter,
                "SHA256 mismatch for {}: expected {expected}, got {actual}",
                package_path.display()
            ),
            Self::Io { path, message } => write!(formatter, "{}: {message}", path.display()),
        }
    }
}

impl std::error::Error for Sha256ChecksumError {}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
pub fn zip_directory(
    options: &ZipDirectoryOptions,
) -> Result<ZipDirectoryOutcome, ZipDirectoryError> {
    zip_directory_impl(options)
}

#[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
fn zip_directory(options: &ZipDirectoryOptions) -> Result<ZipDirectoryOutcome, ZipDirectoryError> {
    zip_directory_impl(options)
}

fn zip_directory_impl(
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

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    validate_legacy_registrar_alias_runtime_profile(
        options.include_legacy_registrar_alias,
        options.runtime_profile,
    )?;
    if !options.rust_workspace.join("Cargo.toml").is_file() {
        return Err(BuildRustHelpersError::WorkspaceMissing(
            options.rust_workspace.clone(),
        ));
    }
    let windows_ai_manifest = windows_ai_manifest_path_for_workspace(&options.rust_workspace);
    if !windows_ai_manifest.is_file() {
        return Err(BuildRustHelpersError::WindowsAiManifestMissing(
            windows_ai_manifest,
        ));
    }

    run_rustup_target_add_if_available(cargo_target)?;
    run_build_cargo_command(
        &options.rust_workspace,
        windows_ai_bindings_preflight_cargo_args(&windows_ai_manifest, cargo_target),
    )?;
    run_build_cargo_command(
        &options.rust_workspace,
        rust_helper_cargo_args(cargo_target, &options.configuration),
    )?;
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    {
        copy_built_rust_helpers(
            &options.rust_workspace,
            cargo_target,
            profile_dir,
            &options.output_dir,
            options.include_legacy_registrar_alias,
            options.runtime_profile,
        )
    }
    #[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
    {
        copy_built_rust_helpers(
            &options.rust_workspace,
            cargo_target,
            profile_dir,
            &options.output_dir,
        )
    }
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
        ensure_browser_extension_source_entry_supported(&path)?;
    }

    let targets = browser_extension_targets(&options.target)?;
    let version = browser_extension_version(&extension_dir.join("manifest.json"))?;
    let common_files = browser_extension_common_file_bytes(&extension_dir)?;
    let prepared_targets = targets
        .into_iter()
        .map(|target| prepare_browser_extension_target(&extension_dir, target))
        .collect::<Result<Vec<_>, _>>()?;
    let output_dir = options
        .output_dir
        .clone()
        .unwrap_or_else(|| extension_dir.join("dist"));
    fs::create_dir_all(&output_dir).map_err(|error| PackageBrowserExtensionError::Io {
        path: output_dir.clone(),
        message: error.to_string(),
    })?;

    let mut packages = Vec::new();
    for prepared_target in prepared_targets {
        let package = package_browser_extension_target(
            &extension_dir,
            &output_dir,
            &version,
            prepared_target,
            &common_files,
        )?;
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
    let windows_ai_manifest = windows_ai_manifest_path_for_workspace(&options.rust_workspace);
    if !windows_ai_manifest.is_file() {
        return Err(PackRustPortableError::WindowsAiManifestMissing(
            windows_ai_manifest,
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
        windows_ai_bindings_preflight_cargo_args(&windows_ai_manifest, cargo_target),
        "cargo check WindowsAI WinRT bindings",
    )?;
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

    let mut missing_entries = RUST_PORTABLE_REQUIRED_ENTRIES
        .iter()
        .filter(|entry| !entries.iter().any(|actual| actual == **entry))
        .map(|entry| (*entry).to_string())
        .collect::<Vec<_>>();
    missing_entries.extend(rust_portable_invalid_required_entries(path)?);
    missing_entries.sort();
    missing_entries.dedup();
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

    let retained_marker_entries = rust_portable_allowlisted_entry_retained_marker_entries(path)?;
    if !retained_marker_entries.is_empty() {
        return Err(ValidateRustPortableError::ForbiddenEntries(
            retained_marker_entries,
        ));
    }

    Ok(ValidateRustPortableOutcome {
        checked_entries: entries.len(),
    })
}

pub fn write_sha256_checksum(
    options: &WriteSha256ChecksumOptions,
) -> Result<WriteSha256ChecksumOutcome, Sha256ChecksumError> {
    ensure_checksum_package_file(&options.package_path)?;
    let package_name = checksum_package_name(&options.package_path)?;
    let checksum = sha256_file_hex(&options.package_path)?;
    let checksum_path = options
        .checksum_path
        .clone()
        .unwrap_or_else(|| options.package_path.with_extension("zip.sha256"));

    if let Some(parent) = checksum_path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
    {
        fs::create_dir_all(parent).map_err(|error| Sha256ChecksumError::Io {
            path: parent.to_path_buf(),
            message: error.to_string(),
        })?;
    }
    let contents = format!("{checksum}  {package_name}\n");
    fs::write(&checksum_path, contents).map_err(|error| Sha256ChecksumError::Io {
        path: checksum_path.clone(),
        message: error.to_string(),
    })?;

    Ok(WriteSha256ChecksumOutcome {
        checksum_path,
        checksum,
        package_name,
    })
}

pub fn verify_sha256_checksum(
    options: &VerifySha256ChecksumOptions,
) -> Result<VerifySha256ChecksumOutcome, Sha256ChecksumError> {
    ensure_checksum_package_file(&options.package_path)?;
    if !options.checksum_path.exists() {
        return Err(Sha256ChecksumError::ChecksumMissing(
            options.checksum_path.clone(),
        ));
    }
    if !options.checksum_path.is_file() {
        return Err(Sha256ChecksumError::ChecksumNotFile(
            options.checksum_path.clone(),
        ));
    }

    let expected_package_name = checksum_package_name(&options.package_path)?;
    let text =
        fs::read_to_string(&options.checksum_path).map_err(|error| Sha256ChecksumError::Io {
            path: options.checksum_path.clone(),
            message: error.to_string(),
        })?;
    let (expected_checksum, actual_package_name) =
        parse_single_sha256_checksum_line(&text, &options.checksum_path)?;
    if actual_package_name != expected_package_name {
        return Err(Sha256ChecksumError::PackageNameMismatch {
            checksum_path: options.checksum_path.clone(),
            expected: expected_package_name,
            actual: actual_package_name,
        });
    }

    let actual_checksum = sha256_file_hex(&options.package_path)?;
    if actual_checksum != expected_checksum {
        return Err(Sha256ChecksumError::ChecksumMismatch {
            package_path: options.package_path.clone(),
            expected: expected_checksum,
            actual: actual_checksum,
        });
    }

    Ok(VerifySha256ChecksumOutcome {
        checksum: actual_checksum,
        package_name: actual_package_name,
    })
}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
pub fn copy_built_rust_helpers(
    rust_workspace: &Path,
    cargo_target: &str,
    profile_dir: &str,
    output_dir: &Path,
    include_legacy_registrar_alias: bool,
    runtime_profile: Option<PackageRuntimeProfile>,
) -> Result<BuildRustHelpersOutcome, BuildRustHelpersError> {
    copy_built_rust_helpers_impl(
        rust_workspace,
        cargo_target,
        profile_dir,
        output_dir,
        include_legacy_registrar_alias,
        runtime_profile,
    )
}

#[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
pub fn copy_built_rust_helpers(
    rust_workspace: &Path,
    cargo_target: &str,
    profile_dir: &str,
    output_dir: &Path,
) -> Result<BuildRustHelpersOutcome, BuildRustHelpersError> {
    copy_built_rust_helpers_impl(
        rust_workspace,
        cargo_target,
        profile_dir,
        output_dir,
        false,
        None,
    )
}

fn copy_built_rust_helpers_impl(
    rust_workspace: &Path,
    cargo_target: &str,
    profile_dir: &str,
    output_dir: &Path,
    include_legacy_registrar_alias: bool,
    runtime_profile: Option<PackageRuntimeProfile>,
) -> Result<BuildRustHelpersOutcome, BuildRustHelpersError> {
    validate_legacy_registrar_alias_runtime_profile(
        include_legacy_registrar_alias,
        runtime_profile,
    )?;
    let copied_files =
        copy_built_rust_helper_executables(rust_workspace, cargo_target, profile_dir, output_dir)?;
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    let mut copied_files = copied_files;
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    if include_legacy_registrar_alias {
        let built_dir = rust_workspace
            .join("target")
            .join(cargo_target)
            .join(profile_dir);
        let registrar_source = built_dir.join("easydict_browser_registrar.exe");
        ensure_build_artifact_source_within_build_dir(&registrar_source, &built_dir)?;
        let legacy_name = "BrowserHostRegistrar.exe";
        fs::copy(&registrar_source, output_dir.join(legacy_name)).map_err(|error| {
            BuildRustHelpersError::Io {
                path: output_dir.join(legacy_name),
                message: error.to_string(),
            }
        })?;
        copied_files.push(legacy_name.to_string());
    }

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
        ensure_build_artifact_source_within_build_dir(&source, &built_dir)?;
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

fn ensure_build_artifact_source_within_build_dir(
    source: &Path,
    built_dir: &Path,
) -> Result<(), BuildRustHelpersError> {
    let is_within =
        build_artifact_source_is_within_build_dir_with_canonicalizer(source, built_dir, |path| {
            fs::canonicalize(path)
        })
        .map_err(|(path, error)| BuildRustHelpersError::Io {
            path,
            message: error.to_string(),
        })?;

    if is_within {
        Ok(())
    } else {
        Err(BuildRustHelpersError::UnsafeBuildArtifactSource {
            source: source.to_path_buf(),
            build_dir: built_dir.to_path_buf(),
        })
    }
}

fn build_artifact_source_is_within_build_dir_with_canonicalizer<F, E>(
    source: &Path,
    built_dir: &Path,
    mut canonicalize: F,
) -> Result<bool, (PathBuf, E)>
where
    F: FnMut(&Path) -> Result<PathBuf, E>,
{
    let canonical_source = canonicalize(source).map_err(|error| (source.to_path_buf(), error))?;
    let canonical_built_dir =
        canonicalize(built_dir).map_err(|error| (built_dir.to_path_buf(), error))?;
    Ok(canonical_source.starts_with(canonical_built_dir))
}

fn validate_legacy_registrar_alias_runtime_profile(
    include_legacy_registrar_alias: bool,
    runtime_profile: Option<PackageRuntimeProfile>,
) -> Result<(), BuildRustHelpersError> {
    #[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
    {
        let _ = runtime_profile;
        debug_assert!(
            !include_legacy_registrar_alias,
            "default helper copy path should not request the legacy registrar alias"
        );
    }

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    if include_legacy_registrar_alias {
        if runtime_profile != Some(PackageRuntimeProfile::Hybrid) {
            return Err(
                BuildRustHelpersError::LegacyRegistrarAliasRequiresHybridProfile(runtime_profile),
            );
        }
    }
    Ok(())
}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
pub fn dotnet_runtime_url(version: &str, rid: &str) -> String {
    format!(
        "https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/dotnet-runtime-{version}-{rid}.zip"
    )
}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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

const RUST_PORTABLE_APP_ICON_ENTRY: &str = "AppIcon.ico";
const WINDOWS_AI_REQUIRE_WINRT_BINDINGS_ENV: &str = "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS";

const RUST_PORTABLE_REQUIRED_ENTRIES: &[&str] = &[
    "Easydict.Rust.exe",
    "easydict-native-bridge.exe",
    "easydict_browser_registrar.exe",
    "easydict_cli.exe",
    "easydict_long_doc.exe",
    RUST_PORTABLE_APP_ICON_ENTRY,
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

fn run_build_cargo_command(
    rust_workspace: &Path,
    cargo_args: Vec<String>,
) -> Result<(), BuildRustHelpersError> {
    let status = std::process::Command::new("cargo")
        .args(&cargo_args)
        .envs(rust_portable_cargo_environment())
        .current_dir(rust_workspace)
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
    Ok(())
}

fn rust_portable_cargo_environment() -> [(&'static str, &'static str); 3] {
    [
        ("EASYDICT_RUNTIME_PROFILE", "rust-only"),
        ("RUNTIME_PROFILE", "rust-only"),
        (WINDOWS_AI_REQUIRE_WINRT_BINDINGS_ENV, "1"),
    ]
}

fn windows_ai_bindings_preflight_cargo_args(
    windows_ai_manifest: &Path,
    cargo_target: &str,
) -> Vec<String> {
    vec![
        "check".to_string(),
        "--manifest-path".to_string(),
        windows_ai_manifest.to_string_lossy().to_string(),
        "--target".to_string(),
        cargo_target.to_string(),
    ]
}

fn windows_ai_manifest_path_for_workspace(rust_workspace: &Path) -> PathBuf {
    rust_workspace
        .parent()
        .unwrap_or(rust_workspace)
        .join("lib")
        .join("easydict-windows-ai")
        .join("Cargo.toml")
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
    ensure_build_artifact_source_within_build_dir(&preview_exe, &built_dir)
        .map_err(pack_error_from_build_error)?;

    fs::copy(&preview_exe, package_dir.join("Easydict.Rust.exe")).map_err(|error| {
        PackRustPortableError::Io {
            path: package_dir.join("Easydict.Rust.exe"),
            message: error.to_string(),
        }
    })?;

    copy_built_rust_helpers_for_portable(rust_workspace, cargo_target, profile_dir, package_dir)?;
    copy_rust_portable_app_icon(rust_workspace, package_dir)?;
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

fn copy_rust_portable_app_icon(
    rust_workspace: &Path,
    package_dir: &Path,
) -> Result<(), PackRustPortableError> {
    let source = rust_workspace
        .join("crates")
        .join("easydict_app")
        .join("resources")
        .join(RUST_PORTABLE_APP_ICON_ENTRY);
    fs::copy(&source, package_dir.join(RUST_PORTABLE_APP_ICON_ENTRY)).map_err(|error| {
        PackRustPortableError::Io {
            path: source,
            message: error.to_string(),
        }
    })?;

    Ok(())
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
        #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
        BuildRustHelpersError::LegacyRegistrarAliasRequiresHybridProfile(_) => {
            PackRustPortableError::Validation(
                "legacy BrowserHostRegistrar.exe alias requires explicit --runtime-profile hybrid"
                    .to_string(),
            )
        }
        #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
        BuildRustHelpersError::LegacyRegistrarAliasRequiresHybridPackagingFeature => {
            PackRustPortableError::Validation(
                "legacy BrowserHostRegistrar.exe alias requires the hybrid-dotnet-runtime-packaging feature"
                    .to_string(),
            )
        }
        BuildRustHelpersError::WindowsAiManifestMissing(path) => {
            PackRustPortableError::WindowsAiManifestMissing(path)
        }
        BuildRustHelpersError::RustupFailed { exit_code } => PackRustPortableError::CargoFailed {
            command: "rustup target add",
            exit_code,
        },
        BuildRustHelpersError::CargoFailed { exit_code } => PackRustPortableError::CargoFailed {
            command: "cargo build Rust helper executables",
            exit_code,
        },
        BuildRustHelpersError::UnsafeBuildArtifactSource { source, build_dir } => {
            PackRustPortableError::UnsafeBuildArtifactSource { source, build_dir }
        }
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

fn ensure_checksum_package_file(package_path: &Path) -> Result<(), Sha256ChecksumError> {
    if !package_path.exists() {
        return Err(Sha256ChecksumError::PackageMissing(
            package_path.to_path_buf(),
        ));
    }
    if !package_path.is_file() {
        return Err(Sha256ChecksumError::PackageNotFile(
            package_path.to_path_buf(),
        ));
    }
    Ok(())
}

fn checksum_package_name(package_path: &Path) -> Result<String, Sha256ChecksumError> {
    package_path
        .file_name()
        .and_then(|name| name.to_str())
        .map(ToOwned::to_owned)
        .ok_or_else(|| Sha256ChecksumError::MissingPackageFileName(package_path.to_path_buf()))
}

fn sha256_file_hex(path: &Path) -> Result<String, Sha256ChecksumError> {
    let file = File::open(path).map_err(|error| Sha256ChecksumError::Io {
        path: path.to_path_buf(),
        message: error.to_string(),
    })?;
    let mut reader = BufReader::new(file);
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let bytes_read = reader
            .read(&mut buffer)
            .map_err(|error| Sha256ChecksumError::Io {
                path: path.to_path_buf(),
                message: error.to_string(),
            })?;
        if bytes_read == 0 {
            break;
        }
        hasher.update(&buffer[..bytes_read]);
    }
    Ok(format!("{:x}", hasher.finalize()))
}

fn parse_single_sha256_checksum_line(
    text: &str,
    checksum_path: &Path,
) -> Result<(String, String), Sha256ChecksumError> {
    let mut lines = text.lines().map(str::trim).filter(|line| !line.is_empty());
    let Some(line) = lines.next() else {
        return Err(Sha256ChecksumError::InvalidChecksumFile(
            checksum_path.to_path_buf(),
        ));
    };
    if lines.next().is_some() {
        return Err(Sha256ChecksumError::InvalidChecksumFile(
            checksum_path.to_path_buf(),
        ));
    }

    let mut parts = line.split_whitespace();
    let Some(checksum) = parts.next() else {
        return Err(Sha256ChecksumError::InvalidChecksumFile(
            checksum_path.to_path_buf(),
        ));
    };
    if checksum.len() != 64 || !checksum.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(Sha256ChecksumError::InvalidChecksumFile(
            checksum_path.to_path_buf(),
        ));
    }
    let package_name = parts.collect::<Vec<_>>().join(" ");
    if package_name.is_empty() {
        return Err(Sha256ChecksumError::InvalidChecksumFile(
            checksum_path.to_path_buf(),
        ));
    }
    Ok((
        checksum.to_ascii_lowercase(),
        package_name.trim_start_matches('*').to_string(),
    ))
}

fn run_rustup_target_add_if_available(cargo_target: &str) -> Result<(), BuildRustHelpersError> {
    match std::process::Command::new("rustup")
        .arg("target")
        .arg("add")
        .arg(cargo_target)
        .envs(rust_portable_cargo_environment())
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
        let file_type = entry.file_type().map_err(|error| ZipDirectoryError::Io {
            path: path.clone(),
            message: error.to_string(),
        })?;
        if zip_directory_entry_is_unsupported_by_flags(
            file_type.is_symlink(),
            zip_directory_entry_is_reparse_point(&path)?,
        ) {
            return Err(ZipDirectoryError::UnsupportedDirectoryEntry(path));
        }

        if file_type.is_dir() {
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
        } else if file_type.is_file() {
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

fn zip_directory_entry_is_unsupported_by_flags(is_symlink: bool, is_reparse_point: bool) -> bool {
    directory_entry_is_unsupported_by_flags(is_symlink, is_reparse_point)
}

fn zip_directory_entry_is_reparse_point(path: &Path) -> Result<bool, ZipDirectoryError> {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;

        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        let metadata = fs::symlink_metadata(path).map_err(|error| ZipDirectoryError::Io {
            path: path.to_path_buf(),
            message: error.to_string(),
        })?;
        Ok(metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0)
    }

    #[cfg(not(windows))]
    {
        let _ = path;
        Ok(false)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum BrowserExtensionTarget {
    Chrome,
    Firefox,
}

struct PreparedBrowserExtensionTarget {
    target: BrowserExtensionTarget,
    manifest_bytes: Vec<u8>,
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

fn prepare_browser_extension_target(
    extension_dir: &Path,
    target: BrowserExtensionTarget,
) -> Result<PreparedBrowserExtensionTarget, PackageBrowserExtensionError> {
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
    Ok(PreparedBrowserExtensionTarget {
        target,
        manifest_bytes,
    })
}

fn package_browser_extension_target(
    extension_dir: &Path,
    output_dir: &Path,
    version: &str,
    prepared_target: PreparedBrowserExtensionTarget,
    common_files: &HashMap<&'static str, Vec<u8>>,
) -> Result<BrowserExtensionPackage, PackageBrowserExtensionError> {
    let target = prepared_target.target;
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
        .write_all(&prepared_target.manifest_bytes)
        .map_err(|error| PackageBrowserExtensionError::Io {
            path: output_path.clone(),
            message: error.to_string(),
        })?;

    for relative_path in BROWSER_EXTENSION_COMMON_FILES {
        let entry_name = normalize_package_entry(relative_path)?;
        writer
            .start_file(entry_name, zip_options())
            .map_err(|error| PackageBrowserExtensionError::Zip(error.to_string()))?;
        let source_bytes = common_files
            .get(*relative_path)
            .expect("browser extension common file should be preloaded");
        writer
            .write_all(source_bytes)
            .map_err(|error| PackageBrowserExtensionError::Io {
                path: extension_dir.join(relative_path),
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

fn browser_extension_common_file_bytes(
    extension_dir: &Path,
) -> Result<HashMap<&'static str, Vec<u8>>, PackageBrowserExtensionError> {
    let mut files = HashMap::new();
    for relative_path in BROWSER_EXTENSION_COMMON_FILES {
        let source_path = extension_dir.join(relative_path);
        let bytes = read_browser_extension_source_file_bytes(&source_path)?;
        files.insert(*relative_path, bytes);
    }
    Ok(files)
}

fn read_browser_extension_source_file_bytes(
    path: &Path,
) -> Result<Vec<u8>, PackageBrowserExtensionError> {
    ensure_browser_extension_source_entry_supported(path)?;
    let bytes = fs::read(path).map_err(|error| PackageBrowserExtensionError::Io {
        path: path.to_path_buf(),
        message: error.to_string(),
    })?;
    if bytes_contain_retained_runtime_marker(&bytes) {
        return Err(PackageBrowserExtensionError::RetainedRuntimeMarker(
            path.to_path_buf(),
        ));
    }
    Ok(bytes)
}

fn ensure_browser_extension_source_entry_supported(
    path: &Path,
) -> Result<(), PackageBrowserExtensionError> {
    let metadata =
        fs::symlink_metadata(path).map_err(|error| PackageBrowserExtensionError::Io {
            path: path.to_path_buf(),
            message: error.to_string(),
        })?;
    if browser_extension_source_entry_is_unsupported_by_flags(
        metadata.file_type().is_symlink(),
        browser_extension_source_entry_is_reparse_point(path)?,
    ) {
        return Err(PackageBrowserExtensionError::UnsupportedSourceEntry(
            path.to_path_buf(),
        ));
    }
    Ok(())
}

fn browser_extension_source_entry_is_unsupported_by_flags(
    is_symlink: bool,
    is_reparse_point: bool,
) -> bool {
    directory_entry_is_unsupported_by_flags(is_symlink, is_reparse_point)
}

fn browser_extension_source_entry_is_reparse_point(
    path: &Path,
) -> Result<bool, PackageBrowserExtensionError> {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;

        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        let metadata =
            fs::symlink_metadata(path).map_err(|error| PackageBrowserExtensionError::Io {
                path: path.to_path_buf(),
                message: error.to_string(),
            })?;
        Ok(metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0)
    }

    #[cfg(not(windows))]
    {
        let _ = path;
        Ok(false)
    }
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
        let file_type = child
            .file_type()
            .map_err(|error| ValidateRustPortableError::Io {
                path: child_path.clone(),
                message: error.to_string(),
            })?;
        if rust_portable_directory_entry_is_unsupported_by_flags(
            file_type.is_symlink(),
            rust_portable_directory_entry_is_reparse_point(&child_path)?,
        ) {
            return Err(ValidateRustPortableError::UnsupportedDirectoryEntry(
                child_path,
            ));
        }

        entries.push(rust_portable_entry_name(root, &child_path)?);
        if file_type.is_dir() {
            collect_rust_portable_directory_entries(root, &child_path, entries)?;
        }
    }

    Ok(())
}

fn rust_portable_directory_entry_is_unsupported_by_flags(
    is_symlink: bool,
    is_reparse_point: bool,
) -> bool {
    directory_entry_is_unsupported_by_flags(is_symlink, is_reparse_point)
}

fn directory_entry_is_unsupported_by_flags(is_symlink: bool, is_reparse_point: bool) -> bool {
    is_symlink || is_reparse_point
}

fn rust_portable_directory_entry_is_reparse_point(
    path: &Path,
) -> Result<bool, ValidateRustPortableError> {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;

        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        let metadata =
            fs::symlink_metadata(path).map_err(|error| ValidateRustPortableError::Io {
                path: path.to_path_buf(),
                message: error.to_string(),
            })?;
        Ok(metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0)
    }

    #[cfg(not(windows))]
    {
        let _ = path;
        Ok(false)
    }
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
        let original_name = entry.name().to_string();
        if archive_entry_path_is_unsafe(&original_name) {
            return Err(ValidateRustPortableError::InvalidArchiveEntry(
                original_name,
            ));
        }
        if entry.is_symlink() {
            return Err(ValidateRustPortableError::InvalidArchiveEntry(
                original_name,
            ));
        }
        let Some(enclosed_name) = entry.enclosed_name() else {
            return Err(ValidateRustPortableError::InvalidArchiveEntry(
                original_name,
            ));
        };
        let name = rust_portable_path_entry_name(&enclosed_name)
            .ok_or_else(|| ValidateRustPortableError::InvalidArchiveEntry(original_name.clone()))?;
        entries.push(name);
    }

    entries.sort();
    Ok(entries)
}

fn rust_portable_invalid_required_entries(
    package_path: &Path,
) -> Result<Vec<String>, ValidateRustPortableError> {
    if package_path.is_dir() {
        rust_portable_directory_invalid_required_entries(package_path)
    } else {
        rust_portable_zip_invalid_required_entries(package_path)
    }
}

fn rust_portable_directory_invalid_required_entries(
    package_dir: &Path,
) -> Result<Vec<String>, ValidateRustPortableError> {
    let mut invalid_entries = Vec::new();
    for entry_name in RUST_PORTABLE_REQUIRED_ENTRIES {
        let path = rust_portable_entry_path(package_dir, entry_name);
        let metadata = match fs::metadata(&path) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                invalid_entries.push((*entry_name).to_string());
                continue;
            }
            Err(error) => {
                return Err(ValidateRustPortableError::Io {
                    path,
                    message: error.to_string(),
                });
            }
        };

        if !metadata.is_file() || metadata.len() == 0 {
            invalid_entries.push((*entry_name).to_string());
        }
    }

    Ok(invalid_entries)
}

fn rust_portable_zip_invalid_required_entries(
    archive_path: &Path,
) -> Result<Vec<String>, ValidateRustPortableError> {
    let file = File::open(archive_path).map_err(|error| ValidateRustPortableError::Io {
        path: archive_path.to_path_buf(),
        message: error.to_string(),
    })?;
    let mut archive = ZipArchive::new(BufReader::new(file))
        .map_err(|error| ValidateRustPortableError::Zip(error.to_string()))?;
    let mut required_entries = RUST_PORTABLE_REQUIRED_ENTRIES
        .iter()
        .map(|entry_name| (*entry_name, false))
        .collect::<Vec<_>>();

    for index in 0..archive.len() {
        let entry = archive
            .by_index(index)
            .map_err(|error| ValidateRustPortableError::Zip(error.to_string()))?;
        let original_name = entry.name().to_string();
        if archive_entry_path_is_unsafe(&original_name) || entry.is_symlink() {
            return Err(ValidateRustPortableError::InvalidArchiveEntry(
                original_name,
            ));
        }
        let Some(enclosed_name) = entry.enclosed_name() else {
            return Err(ValidateRustPortableError::InvalidArchiveEntry(
                original_name,
            ));
        };
        let name = rust_portable_path_entry_name(&enclosed_name)
            .ok_or_else(|| ValidateRustPortableError::InvalidArchiveEntry(original_name.clone()))?;
        let Some((_, is_valid)) = required_entries
            .iter_mut()
            .find(|(entry_name, _)| *entry_name == name)
        else {
            continue;
        };

        if !entry.is_dir() && entry.size() > 0 {
            *is_valid = true;
        }
    }

    Ok(required_entries
        .into_iter()
        .filter_map(|(entry_name, is_valid)| (!is_valid).then_some(entry_name.to_string()))
        .collect())
}

fn rust_portable_allowlisted_entry_retained_marker_entries(
    package_path: &Path,
) -> Result<Vec<String>, ValidateRustPortableError> {
    if package_path.is_dir() {
        rust_portable_directory_retained_marker_entries(package_path)
    } else {
        rust_portable_zip_retained_marker_entries(package_path)
    }
}

fn rust_portable_directory_retained_marker_entries(
    package_dir: &Path,
) -> Result<Vec<String>, ValidateRustPortableError> {
    let mut entries = Vec::new();
    for entry_name in RUST_PORTABLE_REQUIRED_ENTRIES {
        let path = rust_portable_entry_path(package_dir, entry_name);
        let bytes = fs::read(&path).map_err(|error| ValidateRustPortableError::Io {
            path,
            message: error.to_string(),
        })?;
        if rust_portable_bytes_contain_retained_marker(&bytes) {
            entries.push((*entry_name).to_string());
        }
    }
    entries.sort();
    entries.dedup();
    Ok(entries)
}

fn rust_portable_zip_retained_marker_entries(
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
        let mut entry = archive
            .by_index(index)
            .map_err(|error| ValidateRustPortableError::Zip(error.to_string()))?;
        let original_name = entry.name().to_string();
        if archive_entry_path_is_unsafe(&original_name) || entry.is_symlink() {
            return Err(ValidateRustPortableError::InvalidArchiveEntry(
                original_name,
            ));
        }
        let Some(enclosed_name) = entry.enclosed_name() else {
            return Err(ValidateRustPortableError::InvalidArchiveEntry(
                original_name,
            ));
        };
        let name = rust_portable_path_entry_name(&enclosed_name)
            .ok_or_else(|| ValidateRustPortableError::InvalidArchiveEntry(original_name.clone()))?;
        if !rust_portable_entry_is_allowed(&name) {
            continue;
        }

        let mut bytes = Vec::new();
        entry
            .read_to_end(&mut bytes)
            .map_err(|error| ValidateRustPortableError::Zip(error.to_string()))?;
        if rust_portable_bytes_contain_retained_marker(&bytes) {
            entries.push(name);
        }
    }

    entries.sort();
    entries.dedup();
    Ok(entries)
}

fn rust_portable_entry_path(root: &Path, entry_name: &str) -> PathBuf {
    entry_name
        .split('/')
        .fold(root.to_path_buf(), |mut path, part| {
            path.push(part);
            path
        })
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

fn archive_entry_path_is_unsafe(path: &str) -> bool {
    let path = path.replace('\\', "/");
    if path.is_empty() || path.starts_with('/') {
        return true;
    }

    path.split('/').any(|part| {
        part == ".."
            || (part.len() >= 2
                && part.as_bytes()[0].is_ascii_alphabetic()
                && part.as_bytes()[1] == b':')
    })
}

fn rust_portable_entry_is_forbidden(entry_name: &str) -> bool {
    easydict_runtime_guards::path_entry_is_retained_runtime_payload_marker(entry_name)
}

fn rust_portable_entry_is_allowed(entry_name: &str) -> bool {
    RUST_PORTABLE_REQUIRED_ENTRIES.contains(&entry_name)
}

fn bytes_contain_retained_runtime_marker(bytes: &[u8]) -> bool {
    easydict_runtime_guards::bytes_contain_retained_runtime_marker(bytes)
}

fn rust_portable_bytes_contain_retained_marker(bytes: &[u8]) -> bool {
    bytes_contain_retained_runtime_marker(bytes)
}

fn read_manifest_json(manifest_path: &Path) -> Result<Value, PackageBrowserExtensionError> {
    if !manifest_path.is_file() {
        return Err(PackageBrowserExtensionError::ManifestMissing(
            manifest_path.to_path_buf(),
        ));
    }
    let bytes = read_browser_extension_source_file_bytes(manifest_path)?;
    let text = String::from_utf8(bytes).map_err(|error| PackageBrowserExtensionError::Io {
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

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
fn validate_runtime_rid(rid: &str) -> Result<(), ExtractDotnetRuntimeError> {
    match rid {
        "win-x64" | "win-arm64" => Ok(()),
        _ => Err(ExtractDotnetRuntimeError::UnsupportedRid(rid.to_string())),
    }
}

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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

#[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    use std::sync::Mutex;
    use zip::ZipArchive;

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
    fn zip_directory_entry_policy_rejects_links_and_reparse_points() {
        assert!(!zip_directory_entry_is_unsupported_by_flags(false, false));
        assert!(zip_directory_entry_is_unsupported_by_flags(true, false));
        assert!(zip_directory_entry_is_unsupported_by_flags(false, true));
        assert!(zip_directory_entry_is_unsupported_by_flags(true, true));
    }

    #[test]
    fn zip_directory_rejects_linked_runtime_roots() {
        let source = tempfile_dir("zip-linked-runtime-source");
        let target = tempfile_dir("zip-linked-runtime-target");
        write_file(&source, "app.exe", b"rust helper");
        write_file(&target, "host/fxr/8.0.11/hostfxr.dll", b"stale hostfxr");
        let linked_runtime_root = source.join("dotnet");
        if let Err(error) = create_directory_symlink(&target, &linked_runtime_root) {
            eprintln!(
                "skipping linked runtime root integration path; symlink creation failed: {error}"
            );
            let _ = fs::remove_dir_all(source);
            let _ = fs::remove_dir_all(target);
            return;
        }
        let zip_path = source.with_extension("zip");

        let error = zip_directory(&ZipDirectoryOptions {
            source_dir: source.clone(),
            destination_zip: zip_path.clone(),
            exclude_extensions: Vec::new(),
        })
        .unwrap_err();

        let ZipDirectoryError::UnsupportedDirectoryEntry(path) = error else {
            panic!("expected unsupported directory entry error");
        };
        assert_eq!(
            path.file_name().and_then(|name| name.to_str()),
            Some("dotnet")
        );
        let _ = fs::remove_file(zip_path);
        let _ = fs::remove_dir_all(source);
        let _ = fs::remove_dir_all(target);
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
    fn write_sha256_checksum_emits_sha256sum_compatible_file() {
        let temp = tempfile_dir("rs-portable-sha256-write");
        let package_name = "easydict-rs-portable-v0.0.0-win-x64.zip";
        let package_path = temp.join(package_name);
        let checksum_path = temp.join("checksums-x64.sha256");
        fs::write(&package_path, b"abc").expect("write package bytes");

        let outcome = write_sha256_checksum(&WriteSha256ChecksumOptions {
            package_path: package_path.clone(),
            checksum_path: Some(checksum_path.clone()),
        })
        .expect("write checksum");

        assert_eq!(
            outcome.checksum,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(outcome.package_name, package_name);
        assert_eq!(outcome.checksum_path, checksum_path);
        assert_eq!(
            fs::read_to_string(&checksum_path).expect("read checksum"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  easydict-rs-portable-v0.0.0-win-x64.zip\n"
        );

        let verified = verify_sha256_checksum(&VerifySha256ChecksumOptions {
            package_path,
            checksum_path,
        })
        .expect("verify checksum");
        assert_eq!(verified.checksum, outcome.checksum);
        assert_eq!(verified.package_name, package_name);
        let _ = fs::remove_dir_all(temp);
    }

    #[test]
    fn verify_sha256_checksum_rejects_mismatch_before_release_upload() {
        let temp = tempfile_dir("rs-portable-sha256-mismatch");
        let package_path = temp.join("easydict-rs-portable-v0.0.0-win-x64.zip");
        let checksum_path = temp.join("checksums-x64.sha256");
        fs::write(&package_path, b"abc").expect("write package bytes");
        fs::write(
            &checksum_path,
            "0000000000000000000000000000000000000000000000000000000000000000  easydict-rs-portable-v0.0.0-win-x64.zip\n",
        )
        .expect("write stale checksum");

        let error = verify_sha256_checksum(&VerifySha256ChecksumOptions {
            package_path: package_path.clone(),
            checksum_path,
        })
        .unwrap_err();

        let Sha256ChecksumError::ChecksumMismatch {
            package_path: actual_path,
            expected,
            actual,
        } = error
        else {
            panic!("expected checksum mismatch");
        };
        assert_eq!(actual_path, package_path);
        assert_eq!(
            expected,
            "0000000000000000000000000000000000000000000000000000000000000000"
        );
        assert_eq!(
            actual,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        let _ = fs::remove_dir_all(temp);
    }

    #[test]
    fn validate_rs_portable_directory_entry_policy_rejects_links_and_reparse_points() {
        assert!(!rust_portable_directory_entry_is_unsupported_by_flags(
            false, false
        ));
        assert!(rust_portable_directory_entry_is_unsupported_by_flags(
            true, false
        ));
        assert!(rust_portable_directory_entry_is_unsupported_by_flags(
            false, true
        ));
        assert!(rust_portable_directory_entry_is_unsupported_by_flags(
            true, true
        ));
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
        assert!(entries.iter().any(|entry| entry == "AppIcon.ico"));
        let _ = fs::remove_dir_all(package);
    }

    #[test]
    fn validate_rs_portable_rejects_required_exe_directory_or_empty_entries() {
        let package = tempfile_dir("rs-portable-invalid-required-entry-shape");
        write_rust_portable_allowed_payload(&package);
        fs::remove_file(rust_portable_entry_path(&package, "easydict_cli.exe"))
            .expect("remove helper exe fixture");
        fs::create_dir(rust_portable_entry_path(&package, "easydict_cli.exe"))
            .expect("create directory with helper exe name");
        fs::write(
            rust_portable_entry_path(&package, "easydict_long_doc.exe"),
            b"",
        )
        .expect("write empty helper exe fixture");

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::MissingRequiredEntries(entries) = error else {
            panic!("expected missing/invalid required entries");
        };
        assert_eq!(
            entries,
            vec![
                "easydict_cli.exe".to_string(),
                "easydict_long_doc.exe".to_string()
            ]
        );

        let zip_path = package.with_extension("zip");
        zip_directory(&ZipDirectoryOptions {
            source_dir: package.clone(),
            destination_zip: zip_path.clone(),
            exclude_extensions: Vec::new(),
        })
        .expect("create invalid required entry shape zip");
        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: zip_path.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::MissingRequiredEntries(entries) = error else {
            panic!("expected missing/invalid ZIP required entries");
        };
        assert_eq!(
            entries,
            vec![
                "easydict_cli.exe".to_string(),
                "easydict_long_doc.exe".to_string()
            ]
        );
        let _ = fs::remove_dir_all(package);
        let _ = fs::remove_file(zip_path);
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
    fn validate_rs_portable_rejects_allowlisted_exe_that_contains_dotnet_host_markers() {
        let package = tempfile_dir("rs-portable-allowlisted-exe-dotnet-marker");
        write_rust_portable_allowed_payload(&package);
        let marker_payload = b"native launcher bytes\n\
hostfxr.dll\n\
HostPolicy.DLL\n\
CoreCLR.DLL\n\
clrjit.dll\n\
singlefilehost.exe\n\
System.Private.CoreLib\n\
Microsoft.NETCore.App\n\
.runtimeconfig.json\n\
.deps.json\n\
This application requires .NET\n\
Easydict.CompatHost\n\
Easydict.Workers.LocalAi\n\
powershell.exe\n\
PwSh.ExE\n\
System.Speech.Synthesis\n\
System.Windows.Forms\n\
System.Management.Automation\n\
WIN_FLUENT_TTS_TEXT\n";
        for entry in [
            "Easydict.Rust.exe",
            "easydict-native-bridge.exe",
            "easydict_browser_registrar.exe",
            "easydict_cli.exe",
            "easydict_long_doc.exe",
        ] {
            write_file(&package, entry, marker_payload);
        }

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden entries");
        };
        assert_eq!(
            entries,
            vec![
                "Easydict.Rust.exe",
                "easydict-native-bridge.exe",
                "easydict_browser_registrar.exe",
                "easydict_cli.exe",
                "easydict_long_doc.exe",
            ]
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
            panic!("expected forbidden ZIP entries");
        };
        assert_eq!(
            entries,
            vec![
                "Easydict.Rust.exe",
                "easydict-native-bridge.exe",
                "easydict_browser_registrar.exe",
                "easydict_cli.exe",
                "easydict_long_doc.exe",
            ]
        );
        let _ = fs::remove_dir_all(package);
        let _ = fs::remove_file(zip_path);
    }

    #[test]
    fn validate_rs_portable_rejects_allowlisted_non_exe_that_contains_retained_markers() {
        let package = tempfile_dir("rs-portable-allowlisted-non-exe-retained-marker");
        write_rust_portable_allowed_payload(&package);
        write_file(
            &package,
            "AppIcon.ico",
            b"renamed retained runtime payload: hostfxr.dll coreclr.dll",
        );
        write_file(
            &package,
            "README-portable.txt",
            b"renamed retained script payload: wscript.exe WScript.Shell",
        );

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden entries");
        };
        assert_eq!(
            entries,
            vec!["AppIcon.ico".to_string(), "README-portable.txt".to_string()]
        );

        let zip_path = package.with_extension("zip");
        zip_directory(&ZipDirectoryOptions {
            source_dir: package.clone(),
            destination_zip: zip_path.clone(),
            exclude_extensions: Vec::new(),
        })
        .expect("create non-exe retained marker test zip");
        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: zip_path.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden ZIP entries");
        };
        assert_eq!(
            entries,
            vec!["AppIcon.ico".to_string(), "README-portable.txt".to_string()]
        );
        let _ = fs::remove_dir_all(package);
        let _ = fs::remove_file(zip_path);
    }

    #[test]
    fn validate_rs_portable_rejects_allowlisted_exe_that_contains_utf16le_dotnet_markers() {
        let package = tempfile_dir("rs-portable-allowlisted-exe-utf16-dotnet-marker");
        write_rust_portable_allowed_payload(&package);
        write_file(
            &package,
            "easydict_cli.exe",
            &utf16le_ascii_bytes("This application requires .NET"),
        );
        write_file(
            &package,
            "easydict_browser_registrar.exe",
            &utf16le_ascii_bytes("stale dialog marker: System.Windows.Forms"),
        );

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden entries");
        };
        assert_eq!(
            entries,
            vec!["easydict_browser_registrar.exe", "easydict_cli.exe"]
        );

        let zip_path = package.with_extension("zip");
        zip_directory(&ZipDirectoryOptions {
            source_dir: package.clone(),
            destination_zip: zip_path.clone(),
            exclude_extensions: Vec::new(),
        })
        .expect("create UTF-16 marker test zip");
        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: zip_path.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden ZIP entries");
        };
        assert_eq!(
            entries,
            vec!["easydict_browser_registrar.exe", "easydict_cli.exe"]
        );
        let _ = fs::remove_dir_all(package);
        let _ = fs::remove_file(zip_path);
    }

    #[test]
    fn validate_rs_portable_rejects_allowlisted_exe_that_contains_script_tts_markers() {
        let package = tempfile_dir("rs-portable-allowlisted-exe-script-tts-marker");
        write_rust_portable_allowed_payload(&package);
        write_file(
            &package,
            "Easydict.Rust.exe",
            b"stale script backend marker: powershell.exe Add-Type -AssemblyName System.Speech",
        );
        write_file(
            &package,
            "easydict_cli.exe",
            &utf16le_ascii_bytes("stale TTS marker: WIN_FLUENT_TTS_TEXT pwsh.exe"),
        );

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden entries");
        };
        assert_eq!(entries, vec!["Easydict.Rust.exe", "easydict_cli.exe"]);

        let zip_path = package.with_extension("zip");
        zip_directory(&ZipDirectoryOptions {
            source_dir: package.clone(),
            destination_zip: zip_path.clone(),
            exclude_extensions: Vec::new(),
        })
        .expect("create script marker test zip");
        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: zip_path.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden ZIP entries");
        };
        assert_eq!(entries, vec!["Easydict.Rust.exe", "easydict_cli.exe"]);
        let _ = fs::remove_dir_all(package);
        let _ = fs::remove_file(zip_path);
    }

    #[test]
    fn validate_rs_portable_rejects_allowlisted_exe_that_contains_wsh_hta_script_markers() {
        let package = tempfile_dir("rs-portable-allowlisted-exe-wsh-hta-marker");
        write_rust_portable_allowed_payload(&package);
        write_file(
            &package,
            "Easydict.Rust.exe",
            b"stale WSH/HTA helper marker: wscript.exe WScript.Shell HTA:APPLICATION",
        );
        write_file(
            &package,
            "easydict_cli.exe",
            &utf16le_ascii_bytes("stale HTA helper marker: mshta.exe VBScript"),
        );

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: package.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden entries");
        };
        assert_eq!(entries, vec!["Easydict.Rust.exe", "easydict_cli.exe"]);

        let zip_path = package.with_extension("zip");
        zip_directory(&ZipDirectoryOptions {
            source_dir: package.clone(),
            destination_zip: zip_path.clone(),
            exclude_extensions: Vec::new(),
        })
        .expect("create WSH/HTA script marker test zip");
        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: zip_path.clone(),
        })
        .unwrap_err();

        let ValidateRustPortableError::ForbiddenEntries(entries) = error else {
            panic!("expected forbidden ZIP entries");
        };
        assert_eq!(entries, vec!["Easydict.Rust.exe", "easydict_cli.exe"]);
        let _ = fs::remove_dir_all(package);
        let _ = fs::remove_file(zip_path);
    }

    #[test]
    fn rs_portable_content_marker_scanner_is_lib_owned() {
        let source = include_str!("lib.rs");

        assert!(
            source
                .contains("easydict_runtime_guards::bytes_contain_retained_runtime_marker(bytes)"),
            "rs portable executable content scan should delegate to lib/easydict-runtime-guards"
        );
        for forbidden in [
            concat!("RUST_PORTABLE_DOTNET", "_CONTENT_MARKERS"),
            concat!("rust_portable_bytes_contain_ascii", "_case_insensitive"),
            concat!(
                "rust_portable_bytes_contain_utf16le",
                "_ascii_case_insensitive"
            ),
        ] {
            assert!(
                !source.contains(forbidden),
                "packager should not re-inline retained runtime byte scanner marker {forbidden}"
            );
        }
    }

    #[test]
    fn rs_portable_path_marker_classifier_is_lib_owned() {
        let source = include_str!("lib.rs");

        assert!(
            source.contains(
                "easydict_runtime_guards::path_entry_is_retained_runtime_payload_marker(entry_name)"
            ),
            "rs portable path marker scan should delegate to lib/easydict-runtime-guards"
        );
        for forbidden in [
            concat!("FORBIDDEN_RUST_PORTABLE_DOTNET", "_SHARED_FRAMEWORKS"),
            concat!("FORBIDDEN_RUST_PORTABLE_DOTNET", "_ASSEMBLIES"),
            concat!(
                "FORBIDDEN_RUST_PORTABLE_WORKER_SHARED",
                "_DOTNET_ASSEMBLIES"
            ),
            concat!("fn rust_portable_entry_contains_dotnet", "_runtime_layout"),
        ] {
            assert!(
                !source.contains(forbidden),
                "packager should not re-inline retained runtime path marker {forbidden}"
            );
        }
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
    fn validate_rs_portable_rejects_unsafe_zip_entry_paths_before_payload_allowlist() {
        for unsafe_entry in [
            "../hostfxr.dll",
            "/workers/localai/Easydict.Workers.LocalAi.exe",
            "C:/workers/localai/Easydict.Workers.LocalAi.exe",
            "C:workers/localai/Easydict.Workers.LocalAi.exe",
        ] {
            let package = tempfile_dir(&format!(
                "rs-portable-unsafe-{}",
                unsafe_entry
                    .chars()
                    .map(|ch| if ch.is_ascii_alphanumeric() { ch } else { '-' })
                    .collect::<String>()
            ));
            let zip_path = package.with_extension("zip");
            let file = File::create(&zip_path).expect("test zip should be created");
            let mut writer = ZipWriter::new(file);
            let options =
                FileOptions::<()>::default().compression_method(CompressionMethod::Stored);
            writer
                .start_file(unsafe_entry, options)
                .expect("unsafe entry should be written");
            writer
                .write_all(b"retained runtime residue")
                .expect("unsafe entry contents should be written");
            writer.finish().expect("test zip should be finalized");

            let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
                package_path: zip_path.clone(),
            })
            .unwrap_err();

            assert_eq!(
                error,
                ValidateRustPortableError::InvalidArchiveEntry(unsafe_entry.to_string())
            );
            let _ = fs::remove_dir_all(package);
            let _ = fs::remove_file(zip_path);
        }
    }

    #[test]
    fn validate_rs_portable_rejects_zip_symlink_entry_before_payload_allowlist() {
        let package = tempfile_dir("rs-portable-zip-symlink");
        let zip_path = package.with_extension("zip");
        let file = File::create(&zip_path).expect("test zip should be created");
        let mut writer = ZipWriter::new(file);
        let options = FileOptions::<()>::default().compression_method(CompressionMethod::Stored);
        for entry in RUST_PORTABLE_REQUIRED_ENTRIES {
            writer
                .start_file(entry, options)
                .expect("required entry should be written");
            writer
                .write_all(entry.as_bytes())
                .expect("required entry contents should be written");
        }
        writer
            .add_symlink(
                "assets/support.bin",
                "dotnet/host/fxr/8.0.11/hostfxr.dll",
                FileOptions::<()>::default(),
            )
            .expect("symlink entry should be written");
        writer.finish().expect("test zip should be finalized");

        let error = validate_rs_portable_payload(&ValidateRustPortableOptions {
            package_path: zip_path.clone(),
        })
        .unwrap_err();

        assert_eq!(
            error,
            ValidateRustPortableError::InvalidArchiveEntry("assets/support.bin".to_string())
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
                ("RUNTIME_PROFILE", "rust-only"),
                ("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "1")
            ]
        );
    }

    #[test]
    fn build_rust_helpers_reports_missing_windows_ai_preflight_manifest_before_cargo() {
        let workspace = tempfile_dir("build-helpers-missing-windows-ai-manifest");
        write_file(&workspace, "Cargo.toml", b"[workspace]\n");
        let output_dir = tempfile_dir("build-helpers-missing-windows-ai-output");

        let expected_manifest = windows_ai_manifest_path_for_workspace(&workspace);
        let error = build_rust_helpers(&BuildRustHelpersOptions {
            rust_workspace: workspace.clone(),
            platform: "x64".to_string(),
            configuration: "Release".to_string(),
            output_dir,
            #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
            include_legacy_registrar_alias: false,
            #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
            runtime_profile: None,
        })
        .unwrap_err();

        assert_eq!(
            error,
            BuildRustHelpersError::WindowsAiManifestMissing(expected_manifest)
        );
        let _ = fs::remove_dir_all(workspace);
    }

    #[test]
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    fn build_rust_helpers_rejects_legacy_registrar_alias_without_explicit_hybrid_before_workspace()
    {
        for runtime_profile in [None, Some(PackageRuntimeProfile::RustOnly)] {
            let test_root = tempfile_dir("build-helpers-legacy-alias-profile");
            let _ = fs::remove_dir_all(&test_root);
            let workspace = test_root.join("missing-workspace");
            let output_dir = test_root.join("out");

            let error = build_rust_helpers(&BuildRustHelpersOptions {
                rust_workspace: workspace.clone(),
                platform: "x64".to_string(),
                configuration: "Release".to_string(),
                output_dir: output_dir.clone(),
                include_legacy_registrar_alias: true,
                runtime_profile,
            })
            .unwrap_err();

            assert_eq!(
                error,
                BuildRustHelpersError::LegacyRegistrarAliasRequiresHybridProfile(runtime_profile)
            );
            assert!(
                !output_dir.exists(),
                "legacy alias profile guard should fail before output or cargo side effects"
            );
            let _ = fs::remove_dir_all(test_root);
        }
    }

    #[test]
    fn pack_rs_portable_reports_missing_windows_ai_preflight_manifest_before_output() {
        let workspace = tempfile_dir("pack-rs-missing-windows-ai-manifest");
        write_file(&workspace, "Cargo.toml", b"[workspace]\n");
        let output_root = tempfile_dir("pack-rs-missing-windows-ai-output");

        let expected_manifest = windows_ai_manifest_path_for_workspace(&workspace);
        let error = pack_rs_portable(&PackRustPortableOptions {
            rust_workspace: workspace.clone(),
            platform: "x64".to_string(),
            configuration: "Release".to_string(),
            output_root: output_root.clone(),
            package_version: Some("v0.0.0-test".to_string()),
            create_zip: false,
        })
        .unwrap_err();

        assert_eq!(
            error,
            PackRustPortableError::WindowsAiManifestMissing(expected_manifest)
        );
        assert!(
            !output_root
                .join("easydict-rs-portable-v0.0.0-test-win-x64")
                .exists(),
            "missing WindowsAI preflight manifest should fail before creating the portable staging directory"
        );
        assert!(
            !output_root
                .join("easydict-rs-portable-v0.0.0-test-win-x64.zip")
                .exists(),
            "missing WindowsAI preflight manifest should fail before creating the portable ZIP"
        );
        let _ = fs::remove_dir_all(workspace);
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
        write_file(
            &workspace,
            "crates/easydict_app/resources/AppIcon.ico",
            b"icon",
        );
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
        assert_eq!(
            fs::read(package.join("AppIcon.ico")).expect("read app icon"),
            b"icon"
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
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
    fn copy_built_rust_helpers_copies_helpers_without_legacy_registrar_alias_by_default() {
        let workspace = tempfile_dir("rust-helper-workspace");
        let built_dir = workspace
            .join("target")
            .join("x86_64-pc-windows-msvc")
            .join("debug");
        for exe_name in RUST_HELPER_EXECUTABLES {
            write_file(&built_dir, exe_name, exe_name.as_bytes());
        }
        let output = tempfile_dir("rust-helper-output");

        #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
        let outcome = copy_built_rust_helpers(
            &workspace,
            "x86_64-pc-windows-msvc",
            "debug",
            &output,
            false,
            None,
        )
        .expect("copy helpers");
        #[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
        let outcome =
            copy_built_rust_helpers(&workspace, "x86_64-pc-windows-msvc", "debug", &output)
                .expect("copy helpers");

        assert_eq!(outcome.copied_files.len(), RUST_HELPER_EXECUTABLES.len());
        for exe_name in RUST_HELPER_EXECUTABLES {
            assert_eq!(
                fs::read(output.join(exe_name)).expect("read copied helper"),
                exe_name.as_bytes()
            );
        }
        assert!(
            !output.join("BrowserHostRegistrar.exe").exists(),
            "default rust-only helper build should not copy the legacy registrar alias"
        );
        let _ = fs::remove_dir_all(workspace);
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    fn copy_built_rust_helpers_copies_legacy_registrar_alias_when_explicit() {
        let workspace = tempfile_dir("rust-helper-workspace-legacy-alias");
        let built_dir = workspace
            .join("target")
            .join("x86_64-pc-windows-msvc")
            .join("debug");
        for exe_name in RUST_HELPER_EXECUTABLES {
            write_file(&built_dir, exe_name, exe_name.as_bytes());
        }
        let output = tempfile_dir("rust-helper-output-legacy-alias");

        let outcome = copy_built_rust_helpers(
            &workspace,
            "x86_64-pc-windows-msvc",
            "debug",
            &output,
            true,
            Some(PackageRuntimeProfile::Hybrid),
        )
        .expect("copy helpers with legacy alias");

        assert_eq!(
            outcome.copied_files.len(),
            RUST_HELPER_EXECUTABLES.len() + 1
        );
        assert_eq!(
            fs::read(output.join("BrowserHostRegistrar.exe")).expect("read alias"),
            b"easydict_browser_registrar.exe"
        );
        let _ = fs::remove_dir_all(workspace);
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    fn build_artifact_source_guard_rejects_canonical_target_outside_build_dir() {
        let build_dir = PathBuf::from(r"C:\workspace\rs\target\x86_64-pc-windows-msvc\debug");
        let requested = build_dir.join("easydict_long_doc.exe");
        let canonical_target =
            PathBuf::from(r"C:\workspace\dotnet\publish\workers\Easydict.Workers.LongDoc.exe");

        let result = build_artifact_source_is_within_build_dir_with_canonicalizer(
            &requested,
            &build_dir,
            |path| {
                if path == requested {
                    Ok::<PathBuf, std::io::Error>(canonical_target.clone())
                } else {
                    Ok::<PathBuf, std::io::Error>(path.to_path_buf())
                }
            },
        )
        .expect("canonicalize simulated paths");

        assert!(!result);
    }

    #[test]
    fn copy_built_rust_helpers_reports_missing_helper() {
        let workspace = tempfile_dir("rust-helper-missing-workspace");
        let output = tempfile_dir("rust-helper-missing-output");

        #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
        let error = copy_built_rust_helpers(
            &workspace,
            "x86_64-pc-windows-msvc",
            "debug",
            &output,
            false,
            None,
        )
        .unwrap_err();
        #[cfg(not(feature = "hybrid-dotnet-runtime-packaging"))]
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
    fn package_browser_extension_ignores_retained_native_host_and_runtime_residue() {
        let extension = browser_extension_source("browser-extension-runtime-residue");
        let output = tempfile_dir("browser-extension-runtime-residue-output");
        for (relative_path, bytes) in [
            (
                "Easydict.NativeBridge.exe",
                b"legacy native bridge" as &[u8],
            ),
            ("Easydict.CompatHost.exe", b"compat host"),
            ("BrowserHostRegistrar.exe", b"legacy registrar"),
            ("workers/localai/Easydict.Workers.LocalAi.exe", b"worker"),
            ("dotnet/host/fxr/8.0.11/hostfxr.dll", b"hostfxr"),
            (
                "dotnet/shared/Microsoft.NETCore.App/8.0.11/coreclr.dll",
                b"coreclr",
            ),
        ] {
            write_file(&extension, relative_path, bytes);
        }

        package_browser_extension(&PackageBrowserExtensionOptions {
            extension_dir: extension.clone(),
            output_dir: Some(output.clone()),
            target: "All".to_string(),
        })
        .expect("package extension");

        for package in [
            output.join("easydict-ocr-chrome-v1.2.3.zip"),
            output.join("easydict-ocr-firefox-v1.2.3.xpi"),
        ] {
            let entries = zip_entries(&package);
            assert_eq!(entries, expected_browser_extension_entries());
            for forbidden in [
                "Easydict.NativeBridge.exe",
                "Easydict.CompatHost.exe",
                "BrowserHostRegistrar.exe",
                "workers/localai/Easydict.Workers.LocalAi.exe",
                "dotnet/host/fxr/8.0.11/hostfxr.dll",
                "dotnet/shared/Microsoft.NETCore.App/8.0.11/coreclr.dll",
            ] {
                assert!(
                    !entries.iter().any(|entry| entry == forbidden),
                    "browser extension package must ignore retained runtime residue: {forbidden}"
                );
            }
        }

        let _ = fs::remove_dir_all(extension);
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    fn package_browser_extension_rejects_linked_common_file_before_creating_package() {
        let extension = browser_extension_source("browser-extension-linked-common-file");
        let output = tempfile_dir("browser-extension-linked-common-file-output");
        let linked_source = extension.join("background.js");
        let retained_payload = extension.join("dotnet/host/fxr/8.0.11/hostfxr.dll");
        if let Some(parent) = retained_payload.parent() {
            fs::create_dir_all(parent).expect("create retained payload parent");
        }
        fs::write(&retained_payload, b"hostfxr").expect("write retained payload");
        fs::remove_file(&linked_source).expect("remove fixture source");
        if let Err(error) = create_file_symlink(&retained_payload, &linked_source) {
            eprintln!(
                "skipping linked browser extension source path; symlink creation failed: {error}"
            );
            let _ = fs::remove_dir_all(extension);
            let _ = fs::remove_dir_all(output);
            return;
        }

        let error = package_browser_extension(&PackageBrowserExtensionOptions {
            extension_dir: extension.clone(),
            output_dir: Some(output.clone()),
            target: "Chrome".to_string(),
        })
        .unwrap_err();

        let PackageBrowserExtensionError::UnsupportedSourceEntry(path) = error else {
            panic!("expected unsupported source entry error");
        };
        assert_eq!(
            path.file_name().and_then(|name| name.to_str()),
            Some("background.js")
        );
        assert!(
            !output.join("easydict-ocr-chrome-v1.2.3.zip").exists(),
            "linked source entries should fail before creating the package archive"
        );

        let _ = fs::remove_dir_all(extension);
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    fn package_browser_extension_rejects_retained_runtime_markers_inside_common_files() {
        let extension = browser_extension_source("browser-extension-retained-marker-common-file");
        let output = tempfile_dir("browser-extension-retained-marker-common-file-output");
        write_file(
            &extension,
            "background.js",
            b"bridge(); hostfxr.dll This application requires .NET",
        );

        let error = package_browser_extension(&PackageBrowserExtensionOptions {
            extension_dir: extension.clone(),
            output_dir: Some(output.clone()),
            target: "Chrome".to_string(),
        })
        .unwrap_err();

        let PackageBrowserExtensionError::RetainedRuntimeMarker(path) = error else {
            panic!("expected retained runtime marker error");
        };
        assert_eq!(
            path.file_name().and_then(|name| name.to_str()),
            Some("background.js")
        );
        assert!(
            !output.join("easydict-ocr-chrome-v1.2.3.zip").exists(),
            "retained runtime source markers should fail before creating the package archive"
        );

        let _ = fs::remove_dir_all(extension);
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    fn package_browser_extension_rejects_retained_runtime_markers_inside_manifest_files() {
        let extension = browser_extension_source("browser-extension-retained-marker-manifest");
        let output = tempfile_dir("browser-extension-retained-marker-manifest-output");
        write_file(
            &extension,
            "manifest.json",
            br#"{
  "manifest_version": 3,
  "version": "1.2.3",
  "description": "stale hostfxr.dll marker"
}"#,
        );

        let error = package_browser_extension(&PackageBrowserExtensionOptions {
            extension_dir: extension.clone(),
            output_dir: Some(output.clone()),
            target: "Chrome".to_string(),
        })
        .unwrap_err();

        let PackageBrowserExtensionError::RetainedRuntimeMarker(path) = error else {
            panic!("expected retained runtime marker error");
        };
        assert_eq!(
            path.file_name().and_then(|name| name.to_str()),
            Some("manifest.json")
        );
        assert!(
            !output.join("easydict-ocr-chrome-v1.2.3.zip").exists(),
            "retained runtime manifest markers should fail before creating the package archive"
        );

        let _ = fs::remove_dir_all(extension);
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    fn package_browser_extension_rejects_retained_runtime_markers_inside_firefox_manifest() {
        let extension =
            browser_extension_source("browser-extension-retained-marker-firefox-manifest");
        let output = tempfile_dir("browser-extension-retained-marker-firefox-manifest-output");
        write_file(
            &extension,
            "manifest.v2.json",
            br#"{
  "manifest_version": 2,
  "version": "1.2.3",
  "description": "stale This application requires .NET marker"
}"#,
        );

        let error = package_browser_extension(&PackageBrowserExtensionOptions {
            extension_dir: extension.clone(),
            output_dir: Some(output.clone()),
            target: "Firefox".to_string(),
        })
        .unwrap_err();

        let PackageBrowserExtensionError::RetainedRuntimeMarker(path) = error else {
            panic!("expected retained runtime marker error");
        };
        assert_eq!(
            path.file_name().and_then(|name| name.to_str()),
            Some("manifest.v2.json")
        );
        assert!(
            !output.join("easydict-ocr-firefox-v1.2.3.xpi").exists(),
            "retained runtime Firefox manifest markers should fail before creating the package archive"
        );

        let _ = fs::remove_dir_all(extension);
        let _ = fs::remove_dir_all(output);
    }

    #[test]
    fn package_browser_extension_all_targets_rejects_firefox_manifest_marker_before_any_archive() {
        let extension = browser_extension_source("browser-extension-retained-marker-all-targets");
        let output = tempfile_dir("browser-extension-retained-marker-all-targets-output");
        write_file(
            &extension,
            "manifest.v2.json",
            br#"{
  "manifest_version": 2,
  "version": "1.2.3",
  "description": "stale hostfxr.dll marker"
}"#,
        );

        let error = package_browser_extension(&PackageBrowserExtensionOptions {
            extension_dir: extension.clone(),
            output_dir: Some(output.clone()),
            target: "All".to_string(),
        })
        .unwrap_err();

        let PackageBrowserExtensionError::RetainedRuntimeMarker(path) = error else {
            panic!("expected retained runtime marker error");
        };
        assert_eq!(
            path.file_name().and_then(|name| name.to_str()),
            Some("manifest.v2.json")
        );
        assert!(
            !output.join("easydict-ocr-chrome-v1.2.3.zip").exists(),
            "all-target preflight should fail before creating the Chrome package archive"
        );
        assert!(
            !output.join("easydict-ocr-firefox-v1.2.3.xpi").exists(),
            "all-target preflight should fail before creating the Firefox package archive"
        );

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

    fn utf16le_ascii_bytes(value: &str) -> Vec<u8> {
        value.encode_utf16().flat_map(u16::to_le_bytes).collect()
    }

    #[cfg(windows)]
    fn create_directory_symlink(target: &Path, link: &Path) -> std::io::Result<()> {
        std::os::windows::fs::symlink_dir(target, link)
    }

    #[cfg(not(windows))]
    fn create_directory_symlink(target: &Path, link: &Path) -> std::io::Result<()> {
        std::os::unix::fs::symlink(target, link)
    }

    #[cfg(windows)]
    fn create_file_symlink(target: &Path, link: &Path) -> std::io::Result<()> {
        std::os::windows::fs::symlink_file(target, link)
    }

    #[cfg(not(windows))]
    fn create_file_symlink(target: &Path, link: &Path) -> std::io::Result<()> {
        std::os::unix::fs::symlink(target, link)
    }

    fn write_rust_portable_allowed_payload(root: &Path) {
        for entry in RUST_PORTABLE_REQUIRED_ENTRIES {
            write_file(root, entry, entry.as_bytes());
        }
    }

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
    struct RuntimeProfileEnvironmentSnapshot {
        easydict_runtime_profile: Option<String>,
        runtime_profile: Option<String>,
    }

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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

    #[cfg(feature = "hybrid-dotnet-runtime-packaging")]
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
                || arg.contains("retained-dotnet-workers")
                || arg.contains("hybrid-dotnet-runtime-packaging")
                || arg.contains("legacy-powershell-dialogs")
                || arg.contains("legacy-powershell-tts")),
            "rs portable cargo args must keep retained .NET worker and legacy PowerShell features disabled: {args:?}"
        );
    }
}

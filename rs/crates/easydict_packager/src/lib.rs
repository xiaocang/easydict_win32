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
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ExtractDotnetRuntimeOutcome {
    pub bundled_version: String,
    pub total_bytes: u64,
    pub archive_bytes: u64,
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

pub fn copy_built_rust_helpers(
    rust_workspace: &Path,
    cargo_target: &str,
    profile_dir: &str,
    output_dir: &Path,
) -> Result<BuildRustHelpersOutcome, BuildRustHelpersError> {
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

pub const RUST_HELPER_EXECUTABLES: &[&str] = &[
    "easydict-native-bridge.exe",
    "easydict_browser_registrar.exe",
    "easydict_cli.exe",
    "easydict_long_doc.exe",
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
    use zip::ZipArchive;

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
}

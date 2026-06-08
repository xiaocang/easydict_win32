use std::ffi::OsString;
use std::fmt;
use std::fs::{self, File};
use std::io::{self, Cursor, Read, Seek};
use std::path::{Path, PathBuf};
use std::process::Command;

use quick_xml::events::{BytesStart, Event};
use quick_xml::name::QName;
use quick_xml::{Reader, Writer, XmlVersion};
use sha2::{Digest, Sha256};
use tempfile::TempDir;
use zip::ZipArchive;

pub const DEFAULT_EXPECTED_NAME: &str = "xiaocang.EasydictforWindows";
pub const DEFAULT_EXPECTED_PUBLISHER: &str = "CN=33FC47D7-8283-45FC-BB5D-297D1476BB29";
pub const DEFAULT_MIN_VERSION: &str = "10.0.19041.0";

const PAYLOAD_LAYOUT_VALIDATOR: &str = "PackagePayloadLayoutValidator";
const REQUIRED_RUST_HELPERS: &[&str] = &[
    "easydict-native-bridge.exe",
    "easydict_browser_registrar.exe",
    "BrowserHostRegistrar.exe",
    "easydict_cli.exe",
    "easydict_long_doc.exe",
];
const REQUIRED_RETAINED_WORKER_PAYLOADS: &[&str] = &[
    "workers/longdoc/Easydict.Workers.LongDoc.exe",
    "workers/localai/Easydict.Workers.LocalAi.exe",
];
const RUST_ONLY_FORBIDDEN_PREFIXES: &[&str] = &["workers/", "dotnet/"];
const RUST_ONLY_FORBIDDEN_RUNTIME_FILE_NAMES: &[&str] = &[
    "createdump.exe",
    "dotnet.exe",
    "hostfxr.dll",
    "coreclr.dll",
    "hostpolicy.dll",
    "clrjit.dll",
    "mscordaccore.dll",
    "mscordbi.dll",
    "mscorlib.dll",
    "netstandard.dll",
    "singlefilehost.exe",
    "system.private.corelib.dll",
    "windowsbase.dll",
    "presentationcore.dll",
    "presentationframework.dll",
];
const RUST_ONLY_FORBIDDEN_DOTNET_SHARED_FRAMEWORKS: &[&str] = &[
    "shared/microsoft.netcore.app/",
    "shared/microsoft.windowsdesktop.app/",
    "shared/microsoft.aspnetcore.app/",
];
const RUST_ONLY_FORBIDDEN_WORKER_SHARED_FILE_NAMES: &[&str] = &[
    "microsoft.interactiveexperiences.projection.dll",
    "microsoft.web.webview2.core.projection.dll",
    "microsoft.windows.sdk.net.dll",
    "microsoft.windows.ui.xaml.dll",
    "microsoft.winui.dll",
    "winrt.runtime.dll",
];
const RUST_ONLY_FORBIDDEN_DOTNET_ASSEMBLY_FILE_NAMES: &[&str] = &[
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
const RUST_ONLY_FORBIDDEN_REASON: &str =
    "Rust-only packages must not ship retained .NET workers or bundled .NET runtime";
const NO_RETAINED_WORKERS_FORBIDDEN_REASON: &str =
    "Packages without retained .NET workers must not ship retained workers or bundled .NET runtime";
const FORBIDDEN_PAYLOAD_FILE_NAMES: &[(&str, &str)] = &[
    (
        "easydict.workers.ocr.exe",
        "OCR is Rust-native and must not ship the retired .NET OCR worker",
    ),
    (
        "easydict.nativebridge.exe",
        "browser Native Messaging host is now easydict-native-bridge.exe",
    ),
    (
        "easydict.browserregistrar.exe",
        "browser registrar is now easydict_browser_registrar.exe with BrowserHostRegistrar.exe alias",
    ),
    (
        "msixvalidate.exe",
        "MSIX validation is now the Rust easydict_msix_validate tool",
    ),
    (
        "encryptsecret.exe",
        "secret encryption is now the Rust easydict_encrypt_secret tool",
    ),
    (
        "pdftoimages.exe",
        "PDF image conversion is now the Rust easydict_pdf_to_images tool",
    ),
];
const FORBIDDEN_PAYLOAD_FILE_PREFIXES: &[(&str, &str)] = &[(
    "easydict.compathost",
    ".NET CompatHost has been removed; Rust must start retained workers directly",
)];
const FORBIDDEN_ROOT_LONGDOC_PAYLOADS: &[&str] = &[
    "easydict.documentexport.dll",
    "mupdf.net.dll",
    "pdfsharpcore.dll",
    "uglytoad.pdfpig.dll",
    "libskiasharp.dll",
    "skiasharp.dll",
];
const WORKER_SHARED_DIRS: &[&str] = &["longdoc", "localai"];
const WORKER_SHARED_ALLOWLIST: &[&str] = &[
    "Microsoft.Windows.SDK.NET.dll",
    "WinRT.Runtime.dll",
    "Microsoft.Windows.UI.Xaml.dll",
    "Microsoft.WinUI.dll",
    "Microsoft.InteractiveExperiences.Projection.dll",
    "Microsoft.Web.WebView2.Core.Projection.dll",
];
const REQUIRED_MSIX_ASSETS: &[&str] = &[
    "Assets/SplashScreen.scale-100.png",
    "Assets/LockScreenLogo.scale-100.png",
    "Assets/Square150x150Logo.scale-100.png",
    "Assets/Square44x44Logo.scale-100.png",
    "Assets/Wide310x150Logo.scale-100.png",
    "Assets/StoreLogo.png",
];
const MIN_TARGETSIZE_ICON_COUNT: usize = 10;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MsixValidationOptions {
    pub expected_name: String,
    pub expected_publisher: String,
    pub min_version: String,
    pub allow_unsigned: bool,
    pub runtime_profile: PackageRuntimeProfile,
}

impl Default for MsixValidationOptions {
    fn default() -> Self {
        Self {
            expected_name: DEFAULT_EXPECTED_NAME.to_string(),
            expected_publisher: DEFAULT_EXPECTED_PUBLISHER.to_string(),
            min_version: DEFAULT_MIN_VERSION.to_string(),
            allow_unsigned: false,
            runtime_profile: PackageRuntimeProfile::RustOnly,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PackageRuntimeProfile {
    Hybrid,
    RustOnly,
}

impl PackageRuntimeProfile {
    pub fn parse(value: &str) -> Option<Self> {
        match value.trim().to_ascii_lowercase().as_str() {
            "hybrid" => Some(Self::Hybrid),
            "rust-only" | "rustonly" | "rust_only" => Some(Self::RustOnly),
            _ => None,
        }
    }

    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Hybrid => "hybrid",
            Self::RustOnly => "rust-only",
        }
    }
}

#[derive(Debug, Eq, PartialEq)]
pub enum ValidationError {
    MissingManifest,
    MissingIdentity,
    IdentityNameMismatch {
        actual: String,
        expected: String,
    },
    IdentityPublisherMismatch {
        actual: String,
        expected: String,
    },
    MissingTargetDeviceFamily,
    MissingMinVersion,
    InvalidActualMinVersion(String),
    InvalidExpectedMinVersion(String),
    MinVersionTooLow {
        actual: VersionParts,
        expected: VersionParts,
    },
    MissingSignature,
    EmptySignature,
    MissingRequiredPayload {
        path: String,
    },
    ForbiddenPayload {
        path: String,
        reason: &'static str,
    },
    Xml(String),
    Zip(String),
    Io(String),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FixMinVersionOptions {
    pub min_version: String,
    pub makeappx_path: Option<PathBuf>,
}

impl Default for FixMinVersionOptions {
    fn default() -> Self {
        Self {
            min_version: DEFAULT_MIN_VERSION.to_string(),
            makeappx_path: None,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum FixMinVersionOutcome {
    NoChangeNeeded { current: String, required: String },
    Repacked { previous: String, required: String },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BundleMinVersionOptions {
    pub required_min_version: String,
}

impl Default for BundleMinVersionOptions {
    fn default() -> Self {
        Self {
            required_min_version: DEFAULT_MIN_VERSION.to_string(),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BundleMinVersionReport {
    pub has_bundle_manifest: bool,
    pub packages: Vec<BundlePackageMinVersion>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BundlePackageMinVersion {
    pub path: String,
    pub target_device_family_name: Option<String>,
    pub min_version: String,
    pub max_version_tested: Option<String>,
}

#[derive(Debug, Eq, PartialEq)]
pub enum BundleMinVersionError {
    MissingPackagePayload,
    InvalidRequiredMinVersion(String),
    PackageManifest {
        package: String,
        error: ValidationError,
    },
    Zip(String),
    Io(String),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WorkerSharedDedupeOutcome {
    pub status: WorkerSharedDedupeStatus,
    pub moved_count: usize,
    pub saved_bytes: u64,
    pub shared_files: Vec<WorkerSharedFile>,
    pub skipped_different_hashes: Vec<String>,
    pub worker_sizes: Vec<WorkerDirectorySize>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum WorkerSharedDedupeStatus {
    NoWorkersDirectory { path: PathBuf },
    FewerThanTwoWorkerDirectories,
    Completed,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WorkerSharedFile {
    pub file_name: String,
    pub worker_count: usize,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WorkerDirectorySize {
    pub name: String,
    pub bytes: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PreparePackageInputsOptions {
    pub platform: String,
    pub publish_dir: PathBuf,
    pub manifest_path: PathBuf,
    pub output_manifest: PathBuf,
    pub msix_version: Option<String>,
    pub verify_targetsize_icons: bool,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PreparePackageInputsOutcome {
    pub output_manifest: PathBuf,
    pub copied_pri: bool,
    pub resources_pri_already_present: bool,
    pub targetsize_icon_count: Option<usize>,
}

#[derive(Debug, Eq, PartialEq)]
pub enum PreparePackageInputsError {
    PublishDirMissing(PathBuf),
    ManifestMissing(PathBuf),
    MissingRequiredAssets(Vec<String>),
    NotEnoughTargetsizeIcons { found: usize, required: usize },
    MissingIdentity,
    Io { path: PathBuf, message: String },
    Xml(String),
}

#[derive(Debug, Eq, PartialEq)]
pub enum WorkerSharedDedupeError {
    Io { path: PathBuf, message: String },
}

#[derive(Debug, Eq, PartialEq)]
pub enum FixMinVersionError {
    MissingManifest,
    MissingTargetDeviceFamily,
    MissingMinVersion,
    InvalidActualMinVersion(String),
    InvalidExpectedMinVersion(String),
    MakeAppxNotFound,
    MakeAppxFailed { exit_code: Option<i32> },
    Xml(String),
    Zip(String),
    Io(String),
}

impl fmt::Display for FixMinVersionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingManifest => {
                write!(formatter, "AppxManifest.xml not found in MSIX package")
            }
            Self::MissingTargetDeviceFamily => {
                write!(
                    formatter,
                    "TargetDeviceFamily not found in AppxManifest.xml"
                )
            }
            Self::MissingMinVersion => {
                write!(formatter, "TargetDeviceFamily MinVersion attribute missing")
            }
            Self::InvalidActualMinVersion(value) => {
                write!(
                    formatter,
                    "TargetDeviceFamily MinVersion '{value}' is unparseable"
                )
            }
            Self::InvalidExpectedMinVersion(value) => {
                write!(formatter, "required MinVersion '{value}' is unparseable")
            }
            Self::MakeAppxNotFound => write!(
                formatter,
                "MakeAppx.exe not found in Windows SDK or NuGet cache; cannot re-pack MSIX"
            ),
            Self::MakeAppxFailed { exit_code } => match exit_code {
                Some(code) => write!(formatter, "MakeAppx pack failed with exit code {code}"),
                None => write!(formatter, "MakeAppx pack failed"),
            },
            Self::Xml(message) | Self::Zip(message) | Self::Io(message) => {
                write!(formatter, "{message}")
            }
        }
    }
}

impl std::error::Error for FixMinVersionError {}

impl fmt::Display for BundleMinVersionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingPackagePayload => write!(
                formatter,
                "MSIX bundle does not contain any nested .appx or .msix package payloads"
            ),
            Self::InvalidRequiredMinVersion(value) => {
                write!(formatter, "required MinVersion '{value}' is unparseable")
            }
            Self::PackageManifest { package, error } => {
                write!(formatter, "{package}: {error}")
            }
            Self::Zip(message) | Self::Io(message) => write!(formatter, "{message}"),
        }
    }
}

impl std::error::Error for BundleMinVersionError {}

impl fmt::Display for WorkerSharedDedupeError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io { path, message } => {
                write!(formatter, "{}: {message}", path.display())
            }
        }
    }
}

impl std::error::Error for WorkerSharedDedupeError {}

impl fmt::Display for PreparePackageInputsError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::PublishDirMissing(path) => {
                write!(formatter, "PublishDir not found: {}", path.display())
            }
            Self::ManifestMissing(path) => {
                write!(formatter, "Manifest not found: {}", path.display())
            }
            Self::MissingRequiredAssets(assets) => {
                write!(
                    formatter,
                    "missing required MSIX assets: {}",
                    assets.join(", ")
                )
            }
            Self::NotEnoughTargetsizeIcons { found, required } => write!(
                formatter,
                "expected >={required} targetsize icons, found {found}"
            ),
            Self::MissingIdentity => write!(formatter, "<Identity> element missing"),
            Self::Io { path, message } => write!(formatter, "{}: {message}", path.display()),
            Self::Xml(message) => write!(formatter, "{message}"),
        }
    }
}

impl std::error::Error for PreparePackageInputsError {}

impl fmt::Display for ValidationError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingManifest => write!(formatter, "AppxManifest.xml not found inside MSIX"),
            Self::MissingIdentity => write!(formatter, "<Identity> element missing"),
            Self::IdentityNameMismatch { actual, expected } => {
                write!(formatter, "Identity Name '{actual}' != expected '{expected}'")
            }
            Self::IdentityPublisherMismatch { actual, expected } => {
                write!(
                    formatter,
                    "Identity Publisher '{actual}' != expected '{expected}'"
                )
            }
            Self::MissingTargetDeviceFamily => {
                write!(formatter, "<TargetDeviceFamily> element missing")
            }
            Self::MissingMinVersion => {
                write!(formatter, "TargetDeviceFamily MinVersion attribute missing")
            }
            Self::InvalidActualMinVersion(value) => {
                write!(formatter, "TargetDeviceFamily MinVersion '{value}' is unparseable")
            }
            Self::InvalidExpectedMinVersion(value) => {
                write!(formatter, "--min-version '{value}' is unparseable")
            }
            Self::MinVersionTooLow { actual, expected } => write!(
                formatter,
                "TargetDeviceFamily MinVersion '{actual}' < required '{expected}' (catches Fix-MsixMinVersion regressions)"
            ),
            Self::MissingSignature => {
                write!(formatter, "AppxSignature.p7x not present - bundle is unsigned")
            }
            Self::EmptySignature => write!(formatter, "AppxSignature.p7x is empty"),
            Self::MissingRequiredPayload { path } => {
                write!(formatter, "required MSIX payload '{path}' is missing")
            }
            Self::ForbiddenPayload { path, reason } => {
                write!(formatter, "forbidden MSIX payload '{path}' is present: {reason}")
            }
            Self::Xml(message) | Self::Zip(message) | Self::Io(message) => {
                write!(formatter, "{message}")
            }
        }
    }
}

impl std::error::Error for ValidationError {}

#[derive(Clone, Debug, Eq, PartialEq)]
struct ManifestIdentity {
    name: String,
    publisher: String,
    processor_architecture: Option<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct ManifestInfo {
    identity: Option<ManifestIdentity>,
    target_device_family_name: Option<String>,
    target_device_family_min_version: Option<String>,
    target_device_family_max_version_tested: Option<String>,
    has_target_device_family: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Ord, PartialOrd)]
pub struct VersionParts([u64; 4]);

impl fmt::Display for VersionParts {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            formatter,
            "{}.{}.{}.{}",
            self.0[0], self.0[1], self.0[2], self.0[3]
        )
    }
}

pub fn validate_msix(
    path: impl AsRef<Path>,
    options: &MsixValidationOptions,
) -> Result<(), Vec<(&'static str, ValidationError)>> {
    let mut archive = match open_msix_archive(path.as_ref()) {
        Ok(archive) => archive,
        Err(error) => return Err(vec![("open", error)]),
    };

    let manifest = match read_appx_manifest(&mut archive) {
        Ok(manifest) => manifest,
        Err(error) => return Err(vec![("open", error)]),
    };

    let mut failures = Vec::new();
    if let Err(error) = validate_identity(&manifest, options) {
        failures.push(("PackageFamilyNameValidator", error));
    }
    if let Err(error) = validate_min_version(&manifest, &options.min_version) {
        failures.push(("PackageMinimumVersionValidator", error));
    }
    if !options.allow_unsigned {
        if let Err(error) = validate_signature(&mut archive) {
            failures.push(("PackageCertificateEkuValidator", error));
        }
    }
    match archive_payload_index(&mut archive)
        .and_then(|payload| validate_payload_layout(&manifest, &payload, options.runtime_profile))
    {
        Ok(()) => {}
        Err(error) => failures.push((PAYLOAD_LAYOUT_VALIDATOR, error)),
    }

    if failures.is_empty() {
        Ok(())
    } else {
        Err(failures)
    }
}

pub fn fix_msix_min_version(
    path: impl AsRef<Path>,
    options: &FixMinVersionOptions,
) -> Result<FixMinVersionOutcome, FixMinVersionError> {
    let path = path.as_ref();
    let required = parse_version(&options.min_version).ok_or_else(|| {
        FixMinVersionError::InvalidExpectedMinVersion(options.min_version.clone())
    })?;

    let work_dir = tempfile::Builder::new()
        .prefix("easydict-msix-minversion-")
        .tempdir()
        .map_err(|error| FixMinVersionError::Io(error.to_string()))?;
    extract_msix(path, work_dir.path())?;

    let manifest_path = work_dir.path().join("AppxManifest.xml");
    if !manifest_path.exists() {
        return Err(FixMinVersionError::MissingManifest);
    }

    let manifest_xml = fs::read_to_string(&manifest_path)
        .map_err(|error| FixMinVersionError::Io(error.to_string()))?;
    let fix = rewrite_manifest_min_version(&manifest_xml, &options.min_version, required)?;
    if !fix.changed {
        return Ok(FixMinVersionOutcome::NoChangeNeeded {
            current: fix.previous_min_version,
            required: options.min_version.clone(),
        });
    }

    fs::write(&manifest_path, fix.xml)
        .map_err(|error| FixMinVersionError::Io(error.to_string()))?;

    let makeappx = match options.makeappx_path.as_ref() {
        Some(path) => path.clone(),
        None => find_makeappx().ok_or(FixMinVersionError::MakeAppxNotFound)?,
    };
    repack_msix_with_makeappx(path, work_dir, &makeappx)?;

    Ok(FixMinVersionOutcome::Repacked {
        previous: fix.previous_min_version,
        required: options.min_version.clone(),
    })
}

pub fn verify_bundle_min_version(
    path: impl AsRef<Path>,
    options: &BundleMinVersionOptions,
) -> Result<BundleMinVersionReport, BundleMinVersionError> {
    let required = parse_version(&options.required_min_version).ok_or_else(|| {
        BundleMinVersionError::InvalidRequiredMinVersion(options.required_min_version.clone())
    })?;

    let file =
        File::open(path.as_ref()).map_err(|error| BundleMinVersionError::Io(error.to_string()))?;
    let mut bundle =
        ZipArchive::new(file).map_err(|error| BundleMinVersionError::Zip(error.to_string()))?;
    let mut package_payloads = Vec::new();
    let mut has_bundle_manifest = false;

    for index in 0..bundle.len() {
        let mut entry = bundle
            .by_index(index)
            .map_err(|error| BundleMinVersionError::Zip(error.to_string()))?;
        let entry_name = entry.name().to_string();
        let normalized = normalize_archive_path(&entry_name);
        if normalized == "appxmetadata/appxbundlemanifest.xml" {
            has_bundle_manifest = true;
        }
        if entry.is_dir() || !is_bundle_package_payload(&normalized) {
            continue;
        }

        let mut bytes = Vec::new();
        entry
            .read_to_end(&mut bytes)
            .map_err(|error| BundleMinVersionError::Io(error.to_string()))?;
        package_payloads.push((entry_name, bytes));
    }

    if package_payloads.is_empty() {
        return Err(BundleMinVersionError::MissingPackagePayload);
    }

    let mut packages = Vec::with_capacity(package_payloads.len());
    for (package, bytes) in package_payloads {
        let cursor = Cursor::new(bytes);
        let mut archive =
            ZipArchive::new(cursor).map_err(|error| BundleMinVersionError::PackageManifest {
                package: package.clone(),
                error: ValidationError::Zip(error.to_string()),
            })?;
        let manifest = read_appx_manifest(&mut archive).map_err(|error| {
            BundleMinVersionError::PackageManifest {
                package: package.clone(),
                error,
            }
        })?;

        validate_min_version(&manifest, &options.required_min_version).map_err(|error| {
            BundleMinVersionError::PackageManifest {
                package: package.clone(),
                error,
            }
        })?;
        let min_version = manifest
            .target_device_family_min_version
            .clone()
            .ok_or_else(|| BundleMinVersionError::PackageManifest {
                package: package.clone(),
                error: ValidationError::MissingMinVersion,
            })?;
        let actual =
            parse_version(&min_version).ok_or_else(|| BundleMinVersionError::PackageManifest {
                package: package.clone(),
                error: ValidationError::InvalidActualMinVersion(min_version.clone()),
            })?;
        if actual < required {
            return Err(BundleMinVersionError::PackageManifest {
                package,
                error: ValidationError::MinVersionTooLow {
                    actual,
                    expected: required,
                },
            });
        }

        packages.push(BundlePackageMinVersion {
            path: package,
            target_device_family_name: manifest.target_device_family_name,
            min_version,
            max_version_tested: manifest.target_device_family_max_version_tested,
        });
    }

    Ok(BundleMinVersionReport {
        has_bundle_manifest,
        packages,
    })
}

pub fn dedupe_worker_shared_files(
    publish_dir: impl AsRef<Path>,
) -> Result<WorkerSharedDedupeOutcome, WorkerSharedDedupeError> {
    let publish_dir = publish_dir.as_ref();
    let workers_dir = publish_dir.join("workers");
    if !workers_dir.exists() {
        return Ok(WorkerSharedDedupeOutcome {
            status: WorkerSharedDedupeStatus::NoWorkersDirectory { path: workers_dir },
            moved_count: 0,
            saved_bytes: 0,
            shared_files: Vec::new(),
            skipped_different_hashes: Vec::new(),
            worker_sizes: Vec::new(),
        });
    }

    let worker_dirs = WORKER_SHARED_DIRS
        .iter()
        .map(|name| workers_dir.join(name))
        .filter(|path| path.is_dir())
        .collect::<Vec<_>>();
    if worker_dirs.len() < 2 {
        return Ok(WorkerSharedDedupeOutcome {
            status: WorkerSharedDedupeStatus::FewerThanTwoWorkerDirectories,
            moved_count: 0,
            saved_bytes: 0,
            shared_files: Vec::new(),
            skipped_different_hashes: Vec::new(),
            worker_sizes: worker_directory_sizes(&workers_dir)?,
        });
    }

    let shared_dir = workers_dir.join("shared");
    create_dir_all(&shared_dir)?;

    let mut shared_files = Vec::new();
    let mut skipped_different_hashes = Vec::new();
    let mut saved_bytes = 0;

    for file_name in WORKER_SHARED_ALLOWLIST {
        let matches = worker_dirs
            .iter()
            .map(|dir| dir.join(file_name))
            .filter(|path| path.is_file())
            .collect::<Vec<_>>();
        if matches.len() < 2 {
            continue;
        }

        let mut hashes = Vec::with_capacity(matches.len());
        for path in &matches {
            hashes.push(sha256_lower(path)?);
        }
        if !hashes.iter().all(|hash| hash == &hashes[0]) {
            skipped_different_hashes.push((*file_name).to_string());
            continue;
        }

        let source_metadata = metadata(&matches[0])?;
        let shared_path = shared_dir.join(file_name);
        copy_file(&matches[0], &shared_path)?;
        for path in &matches {
            remove_file(path)?;
        }

        saved_bytes += (matches.len().saturating_sub(1) as u64) * source_metadata.len();
        shared_files.push(WorkerSharedFile {
            file_name: (*file_name).to_string(),
            worker_count: matches.len(),
        });
    }

    Ok(WorkerSharedDedupeOutcome {
        status: WorkerSharedDedupeStatus::Completed,
        moved_count: shared_files.len(),
        saved_bytes,
        shared_files,
        skipped_different_hashes,
        worker_sizes: worker_directory_sizes(&workers_dir)?,
    })
}

pub fn prepare_package_inputs(
    options: &PreparePackageInputsOptions,
) -> Result<PreparePackageInputsOutcome, PreparePackageInputsError> {
    if !options.publish_dir.is_dir() {
        return Err(PreparePackageInputsError::PublishDirMissing(
            options.publish_dir.clone(),
        ));
    }
    if !options.manifest_path.is_file() {
        return Err(PreparePackageInputsError::ManifestMissing(
            options.manifest_path.clone(),
        ));
    }

    verify_required_msix_assets(&options.publish_dir)?;
    let targetsize_icon_count = if options.verify_targetsize_icons {
        Some(verify_targetsize_icons(&options.publish_dir)?)
    } else {
        None
    };
    let (copied_pri, resources_pri_already_present) =
        normalize_resources_pri(&options.publish_dir)?;
    let manifest_xml = fs::read_to_string(&options.manifest_path).map_err(|error| {
        PreparePackageInputsError::Io {
            path: options.manifest_path.clone(),
            message: error.to_string(),
        }
    })?;
    let rewritten = rewrite_identity_for_package(
        &manifest_xml,
        &options.platform,
        options.msix_version.as_deref(),
    )?;
    if let Some(parent) = options.output_manifest.parent() {
        fs::create_dir_all(parent).map_err(|error| PreparePackageInputsError::Io {
            path: parent.to_path_buf(),
            message: error.to_string(),
        })?;
    }
    fs::write(&options.output_manifest, rewritten).map_err(|error| {
        PreparePackageInputsError::Io {
            path: options.output_manifest.clone(),
            message: error.to_string(),
        }
    })?;

    Ok(PreparePackageInputsOutcome {
        output_manifest: options.output_manifest.clone(),
        copied_pri,
        resources_pri_already_present,
        targetsize_icon_count,
    })
}

fn verify_required_msix_assets(publish_dir: &Path) -> Result<(), PreparePackageInputsError> {
    let missing = REQUIRED_MSIX_ASSETS
        .iter()
        .filter(|asset| !publish_dir.join(asset).is_file())
        .map(|asset| (*asset).to_string())
        .collect::<Vec<_>>();
    if missing.is_empty() {
        Ok(())
    } else {
        Err(PreparePackageInputsError::MissingRequiredAssets(missing))
    }
}

fn verify_targetsize_icons(publish_dir: &Path) -> Result<usize, PreparePackageInputsError> {
    let assets_dir = publish_dir.join("Assets");
    let mut count = 0;
    for entry in fs::read_dir(&assets_dir).map_err(|error| PreparePackageInputsError::Io {
        path: assets_dir.clone(),
        message: error.to_string(),
    })? {
        let entry = entry.map_err(|error| PreparePackageInputsError::Io {
            path: assets_dir.clone(),
            message: error.to_string(),
        })?;
        let file_name = entry.file_name().to_string_lossy().to_ascii_lowercase();
        if file_name.contains("targetsize") && file_name.ends_with(".png") {
            count += 1;
        }
    }
    if count < MIN_TARGETSIZE_ICON_COUNT {
        return Err(PreparePackageInputsError::NotEnoughTargetsizeIcons {
            found: count,
            required: MIN_TARGETSIZE_ICON_COUNT,
        });
    }
    Ok(count)
}

fn normalize_resources_pri(publish_dir: &Path) -> Result<(bool, bool), PreparePackageInputsError> {
    let source = publish_dir.join("Easydict.WinUI.pri");
    let target = publish_dir.join("resources.pri");
    if source.is_file() {
        fs::copy(&source, &target).map_err(|error| PreparePackageInputsError::Io {
            path: target,
            message: error.to_string(),
        })?;
        Ok((true, false))
    } else {
        Ok((false, target.is_file()))
    }
}

fn rewrite_identity_for_package(
    xml: &str,
    platform: &str,
    msix_version: Option<&str>,
) -> Result<String, PreparePackageInputsError> {
    let mut reader = Reader::from_str(xml);
    reader.config_mut().trim_text(false);
    let mut writer = Writer::new(Vec::with_capacity(xml.len()));
    let mut found_identity = false;

    loop {
        match reader.read_event() {
            Ok(Event::Start(element)) => {
                if element.local_name().as_ref() == b"Identity" {
                    found_identity = true;
                    let element =
                        rewrite_identity_element(&reader, element, platform, msix_version)?;
                    writer
                        .write_event(Event::Start(element))
                        .map_err(|error| PreparePackageInputsError::Xml(error.to_string()))?;
                } else {
                    writer
                        .write_event(Event::Start(element))
                        .map_err(|error| PreparePackageInputsError::Xml(error.to_string()))?;
                }
            }
            Ok(Event::Empty(element)) => {
                if element.local_name().as_ref() == b"Identity" {
                    found_identity = true;
                    let element =
                        rewrite_identity_element(&reader, element, platform, msix_version)?;
                    writer
                        .write_event(Event::Empty(element))
                        .map_err(|error| PreparePackageInputsError::Xml(error.to_string()))?;
                } else {
                    writer
                        .write_event(Event::Empty(element))
                        .map_err(|error| PreparePackageInputsError::Xml(error.to_string()))?;
                }
            }
            Ok(Event::Eof) => break,
            Ok(event) => writer
                .write_event(event)
                .map_err(|error| PreparePackageInputsError::Xml(error.to_string()))?,
            Err(error) => return Err(PreparePackageInputsError::Xml(error.to_string())),
        }
    }

    if !found_identity {
        return Err(PreparePackageInputsError::MissingIdentity);
    }

    String::from_utf8(writer.into_inner())
        .map_err(|error| PreparePackageInputsError::Xml(error.to_string()))
}

fn rewrite_identity_element<'a>(
    reader: &Reader<&[u8]>,
    mut element: BytesStart<'a>,
    platform: &str,
    msix_version: Option<&str>,
) -> Result<BytesStart<'a>, PreparePackageInputsError> {
    let mut attributes = Vec::new();
    let mut saw_architecture = false;
    let mut saw_version = false;

    for attribute in element.attributes() {
        let attribute =
            attribute.map_err(|error| PreparePackageInputsError::Xml(error.to_string()))?;
        let key = std::str::from_utf8(attribute.key.as_ref())
            .map_err(|error| PreparePackageInputsError::Xml(error.to_string()))?
            .to_string();
        let value = attribute
            .decoded_and_normalized_value(XmlVersion::Implicit1_0, reader.decoder())
            .map_err(|error| PreparePackageInputsError::Xml(error.to_string()))?
            .into_owned();
        if attribute.key == QName(b"ProcessorArchitecture") {
            saw_architecture = true;
            attributes.push((key, platform.to_string()));
        } else if attribute.key == QName(b"Version") {
            saw_version = true;
            attributes.push((key, msix_version.map(str::to_string).unwrap_or(value)));
        } else {
            attributes.push((key, value));
        }
    }

    if !saw_architecture {
        attributes.push((String::from("ProcessorArchitecture"), platform.to_string()));
    }
    if !saw_version {
        if let Some(version) = msix_version {
            attributes.push((String::from("Version"), version.to_string()));
        }
    }

    element.clear_attributes();
    for (key, value) in &attributes {
        element.push_attribute((key.as_str(), value.as_str()));
    }
    Ok(element)
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct ManifestMinVersionRewrite {
    xml: String,
    previous_min_version: String,
    changed: bool,
}

fn rewrite_manifest_min_version(
    xml: &str,
    min_version: &str,
    required: VersionParts,
) -> Result<ManifestMinVersionRewrite, FixMinVersionError> {
    let mut reader = Reader::from_str(xml);
    reader.config_mut().trim_text(false);
    let mut writer = Writer::new(Vec::with_capacity(xml.len()));
    let mut in_dependencies = false;
    let mut found_target_device_family = false;
    let mut previous_min_version = None;
    let mut changed = false;

    loop {
        match reader.read_event() {
            Ok(Event::Start(element)) => {
                let local_name = element.local_name();
                if local_name.as_ref() == b"Dependencies" {
                    in_dependencies = true;
                }

                if in_dependencies && local_name.as_ref() == b"TargetDeviceFamily" {
                    found_target_device_family = true;
                    let (element, previous, did_change) = rewrite_target_device_family_element(
                        &reader,
                        element,
                        min_version,
                        required,
                    )?;
                    previous_min_version = Some(previous);
                    changed |= did_change;
                    writer
                        .write_event(Event::Start(element))
                        .map_err(|error| FixMinVersionError::Xml(error.to_string()))?;
                } else {
                    writer
                        .write_event(Event::Start(element))
                        .map_err(|error| FixMinVersionError::Xml(error.to_string()))?;
                }
            }
            Ok(Event::Empty(element)) => {
                let local_name = element.local_name();
                if in_dependencies && local_name.as_ref() == b"TargetDeviceFamily" {
                    found_target_device_family = true;
                    let (element, previous, did_change) = rewrite_target_device_family_element(
                        &reader,
                        element,
                        min_version,
                        required,
                    )?;
                    previous_min_version = Some(previous);
                    changed |= did_change;
                    writer
                        .write_event(Event::Empty(element))
                        .map_err(|error| FixMinVersionError::Xml(error.to_string()))?;
                } else {
                    writer
                        .write_event(Event::Empty(element))
                        .map_err(|error| FixMinVersionError::Xml(error.to_string()))?;
                }
            }
            Ok(Event::End(element)) => {
                if element.local_name().as_ref() == b"Dependencies" {
                    in_dependencies = false;
                }
                writer
                    .write_event(Event::End(element))
                    .map_err(|error| FixMinVersionError::Xml(error.to_string()))?;
            }
            Ok(Event::Eof) => break,
            Ok(event) => writer
                .write_event(event)
                .map_err(|error| FixMinVersionError::Xml(error.to_string()))?,
            Err(error) => return Err(FixMinVersionError::Xml(error.to_string())),
        }
    }

    if !found_target_device_family {
        return Err(FixMinVersionError::MissingTargetDeviceFamily);
    }

    let previous_min_version = previous_min_version.ok_or(FixMinVersionError::MissingMinVersion)?;
    let xml = String::from_utf8(writer.into_inner())
        .map_err(|error| FixMinVersionError::Xml(error.to_string()))?;
    Ok(ManifestMinVersionRewrite {
        xml,
        previous_min_version,
        changed,
    })
}

fn rewrite_target_device_family_element<'a>(
    reader: &Reader<&[u8]>,
    mut element: BytesStart<'a>,
    min_version: &str,
    required: VersionParts,
) -> Result<(BytesStart<'a>, String, bool), FixMinVersionError> {
    let current = attribute_value(reader, &element, b"MinVersion")
        .map_err(|error| FixMinVersionError::Xml(error.to_string()))?
        .ok_or(FixMinVersionError::MissingMinVersion)?;
    let actual = parse_version(&current)
        .ok_or_else(|| FixMinVersionError::InvalidActualMinVersion(current.clone()))?;
    if actual >= required {
        return Ok((element, current, false));
    }

    let mut attributes = Vec::new();
    for attribute in element.attributes() {
        let attribute = attribute.map_err(|error| FixMinVersionError::Xml(error.to_string()))?;
        if attribute.key == QName(b"MinVersion") {
            attributes.push((String::from("MinVersion"), min_version.to_string()));
        } else {
            let key = std::str::from_utf8(attribute.key.as_ref())
                .map_err(|error| FixMinVersionError::Xml(error.to_string()))?
                .to_string();
            let value = attribute
                .decoded_and_normalized_value(XmlVersion::Implicit1_0, reader.decoder())
                .map_err(|error| FixMinVersionError::Xml(error.to_string()))?
                .into_owned();
            attributes.push((key, value));
        }
    }

    element.clear_attributes();
    for (key, value) in &attributes {
        element.push_attribute((key.as_str(), value.as_str()));
    }

    Ok((element, current, true))
}

fn extract_msix(msix_path: &Path, destination: &Path) -> Result<(), FixMinVersionError> {
    let file = File::open(msix_path).map_err(|error| FixMinVersionError::Io(error.to_string()))?;
    let mut archive =
        ZipArchive::new(file).map_err(|error| FixMinVersionError::Zip(error.to_string()))?;

    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .map_err(|error| FixMinVersionError::Zip(error.to_string()))?;
        let Some(enclosed_name) = entry.enclosed_name() else {
            return Err(FixMinVersionError::Zip(format!(
                "unsafe archive entry path: {}",
                entry.name()
            )));
        };
        let output_path = destination.join(enclosed_name);
        if entry.is_dir() {
            fs::create_dir_all(&output_path)
                .map_err(|error| FixMinVersionError::Io(error.to_string()))?;
            continue;
        }
        if let Some(parent) = output_path.parent() {
            fs::create_dir_all(parent)
                .map_err(|error| FixMinVersionError::Io(error.to_string()))?;
        }
        let mut output = File::create(&output_path)
            .map_err(|error| FixMinVersionError::Io(error.to_string()))?;
        std::io::copy(&mut entry, &mut output)
            .map_err(|error| FixMinVersionError::Io(error.to_string()))?;
    }

    Ok(())
}

fn repack_msix_with_makeappx(
    msix_path: &Path,
    work_dir: TempDir,
    makeappx: &Path,
) -> Result<(), FixMinVersionError> {
    let temp_output = temporary_repack_path(msix_path);
    let _cleanup_temp = TempFileCleanup(temp_output.clone());
    let status = Command::new(makeappx)
        .arg("pack")
        .arg("/d")
        .arg(work_dir.path())
        .arg("/p")
        .arg(&temp_output)
        .arg("/o")
        .status()
        .map_err(|error| FixMinVersionError::Io(error.to_string()))?;
    if !status.success() {
        return Err(FixMinVersionError::MakeAppxFailed {
            exit_code: status.code(),
        });
    }

    replace_file_preserving_original_on_failure(&temp_output, msix_path)?;
    Ok(())
}

fn temporary_repack_path(msix_path: &Path) -> PathBuf {
    let mut name = msix_path
        .file_name()
        .map(OsString::from)
        .unwrap_or_else(|| OsString::from("package.msix"));
    name.push(format!(".{}.fixed.tmp", std::process::id()));
    msix_path.with_file_name(name)
}

fn replace_file_preserving_original_on_failure(
    replacement: &Path,
    destination: &Path,
) -> Result<(), FixMinVersionError> {
    let backup = temporary_backup_path(destination);
    fs::rename(destination, &backup).map_err(|error| FixMinVersionError::Io(error.to_string()))?;
    match fs::rename(replacement, destination) {
        Ok(()) => {
            let _ = fs::remove_file(&backup);
            Ok(())
        }
        Err(error) => {
            let _ = fs::rename(&backup, destination);
            Err(FixMinVersionError::Io(error.to_string()))
        }
    }
}

fn temporary_backup_path(path: &Path) -> PathBuf {
    let mut name = path
        .file_name()
        .map(OsString::from)
        .unwrap_or_else(|| OsString::from("package.msix"));
    name.push(format!(".{}.backup", std::process::id()));
    path.with_file_name(name)
}

struct TempFileCleanup(PathBuf);

impl Drop for TempFileCleanup {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}

fn find_makeappx() -> Option<PathBuf> {
    let mut candidates = Vec::new();
    collect_makeappx_candidates(
        Path::new(r"C:\Program Files (x86)\Windows Kits\10\bin"),
        true,
        &mut candidates,
    );
    if let Some(user_profile) = std::env::var_os("USERPROFILE") {
        collect_makeappx_candidates(
            &PathBuf::from(user_profile).join(r".nuget\packages\microsoft.windows.sdk.buildtools"),
            false,
            &mut candidates,
        );
    }

    candidates.sort_by(|left, right| right.0.cmp(&left.0));
    candidates.into_iter().map(|(_, path)| path).next()
}

fn collect_makeappx_candidates(
    root: &Path,
    sdk_layout: bool,
    candidates: &mut Vec<(VersionParts, PathBuf)>,
) {
    let Ok(entries) = fs::read_dir(root) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_dir() {
            continue;
        }
        if sdk_layout {
            if let Some(version) = path
                .file_name()
                .and_then(|name| name.to_str())
                .and_then(parse_version)
            {
                let makeappx = path.join(r"x64\MakeAppx.exe");
                if makeappx.exists() {
                    candidates.push((version, makeappx));
                }
            }
        } else {
            collect_nuget_makeappx_candidates(&path, candidates);
        }
    }
}

fn collect_nuget_makeappx_candidates(
    package_version_dir: &Path,
    candidates: &mut Vec<(VersionParts, PathBuf)>,
) {
    let Ok(bin_entries) = fs::read_dir(package_version_dir.join("bin")) else {
        return;
    };
    for bin_entry in bin_entries.flatten() {
        let bin_path = bin_entry.path();
        if !bin_path.is_dir() {
            continue;
        }
        let Some(version) = bin_path
            .file_name()
            .and_then(|name| name.to_str())
            .and_then(parse_version)
        else {
            continue;
        };
        let makeappx = bin_path.join(r"x64\MakeAppx.exe");
        if makeappx.exists() {
            candidates.push((version, makeappx));
        }
    }
}

fn open_msix_archive(path: &Path) -> Result<ZipArchive<File>, ValidationError> {
    let file = File::open(path).map_err(|error| ValidationError::Io(error.to_string()))?;
    ZipArchive::new(file).map_err(|error| ValidationError::Zip(error.to_string()))
}

fn read_appx_manifest<R: Read + Seek>(
    archive: &mut ZipArchive<R>,
) -> Result<ManifestInfo, ValidationError> {
    let mut entry = archive
        .by_name("AppxManifest.xml")
        .map_err(|_| ValidationError::MissingManifest)?;
    let mut xml = String::new();
    entry
        .read_to_string(&mut xml)
        .map_err(|error| ValidationError::Io(error.to_string()))?;
    parse_manifest(&xml)
}

fn parse_manifest(xml: &str) -> Result<ManifestInfo, ValidationError> {
    let mut reader = Reader::from_str(xml);
    reader.config_mut().trim_text(true);
    let mut identity = None;
    let mut has_target_device_family = false;
    let mut target_device_family_name = None;
    let mut target_device_family_min_version = None;
    let mut target_device_family_max_version_tested = None;

    loop {
        match reader.read_event() {
            Ok(Event::Start(element)) | Ok(Event::Empty(element)) => {
                let name = element.local_name();
                if name.as_ref() == b"Identity" {
                    identity = Some(ManifestIdentity {
                        name: attribute_value(&reader, &element, b"Name")?.unwrap_or_default(),
                        publisher: attribute_value(&reader, &element, b"Publisher")?
                            .unwrap_or_default(),
                        processor_architecture: attribute_value(
                            &reader,
                            &element,
                            b"ProcessorArchitecture",
                        )?,
                    });
                } else if name.as_ref() == b"TargetDeviceFamily" {
                    has_target_device_family = true;
                    target_device_family_name = attribute_value(&reader, &element, b"Name")?;
                    target_device_family_min_version =
                        attribute_value(&reader, &element, b"MinVersion")?;
                    target_device_family_max_version_tested =
                        attribute_value(&reader, &element, b"MaxVersionTested")?;
                }
            }
            Ok(Event::Eof) => break,
            Err(error) => return Err(ValidationError::Xml(error.to_string())),
            _ => {}
        }
    }

    Ok(ManifestInfo {
        identity,
        target_device_family_name,
        target_device_family_min_version,
        target_device_family_max_version_tested,
        has_target_device_family,
    })
}

fn attribute_value(
    reader: &Reader<&[u8]>,
    element: &quick_xml::events::BytesStart<'_>,
    attribute_name: &[u8],
) -> Result<Option<String>, ValidationError> {
    for attribute in element.attributes() {
        let attribute = attribute.map_err(|error| ValidationError::Xml(error.to_string()))?;
        if attribute.key == QName(attribute_name) {
            return attribute
                .decoded_and_normalized_value(XmlVersion::Implicit1_0, reader.decoder())
                .map(|value| Some(value.into_owned()))
                .map_err(|error| ValidationError::Xml(error.to_string()));
        }
    }
    Ok(None)
}

fn validate_identity(
    manifest: &ManifestInfo,
    options: &MsixValidationOptions,
) -> Result<(), ValidationError> {
    let identity = manifest
        .identity
        .as_ref()
        .ok_or(ValidationError::MissingIdentity)?;
    if identity.name != options.expected_name {
        return Err(ValidationError::IdentityNameMismatch {
            actual: identity.name.clone(),
            expected: options.expected_name.clone(),
        });
    }
    if identity.publisher != options.expected_publisher {
        return Err(ValidationError::IdentityPublisherMismatch {
            actual: identity.publisher.clone(),
            expected: options.expected_publisher.clone(),
        });
    }
    Ok(())
}

fn validate_min_version(manifest: &ManifestInfo, min_version: &str) -> Result<(), ValidationError> {
    if !manifest.has_target_device_family {
        return Err(ValidationError::MissingTargetDeviceFamily);
    }

    let actual = manifest
        .target_device_family_min_version
        .as_deref()
        .ok_or(ValidationError::MissingMinVersion)?;
    let actual = parse_version(actual)
        .ok_or_else(|| ValidationError::InvalidActualMinVersion(actual.to_string()))?;
    let expected = parse_version(min_version)
        .ok_or_else(|| ValidationError::InvalidExpectedMinVersion(min_version.to_string()))?;

    if actual < expected {
        return Err(ValidationError::MinVersionTooLow { actual, expected });
    }

    Ok(())
}

fn parse_version(value: &str) -> Option<VersionParts> {
    let parts = value
        .split('.')
        .map(str::trim)
        .map(str::parse::<u64>)
        .collect::<Result<Vec<_>, _>>()
        .ok()?;
    if parts.is_empty() || parts.len() > 4 {
        return None;
    }

    let mut normalized = [0; 4];
    for (index, part) in parts.into_iter().enumerate() {
        normalized[index] = part;
    }
    Some(VersionParts(normalized))
}

fn validate_signature<R: Read + Seek>(archive: &mut ZipArchive<R>) -> Result<(), ValidationError> {
    let entry = archive
        .by_name("AppxSignature.p7x")
        .map_err(|_| ValidationError::MissingSignature)?;
    if entry.size() == 0 {
        return Err(ValidationError::EmptySignature);
    }
    Ok(())
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct ArchivePayloadIndex {
    entries: Vec<ArchivePayloadEntry>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct ArchivePayloadEntry {
    original: String,
    normalized: String,
}

impl ArchivePayloadIndex {
    fn contains_exact(&self, path: &str) -> bool {
        let normalized = normalize_archive_path(path);
        self.entries
            .iter()
            .any(|entry| entry.normalized == normalized)
    }

    fn first_under(&self, prefix: &str) -> Option<&ArchivePayloadEntry> {
        let prefix = normalize_archive_path(prefix);
        self.entries
            .iter()
            .find(|entry| entry.normalized.starts_with(&prefix))
    }

    fn first_file_named(&self, file_name: &str) -> Option<&ArchivePayloadEntry> {
        let file_name = file_name.to_ascii_lowercase();
        self.entries
            .iter()
            .find(|entry| entry.file_name() == Some(file_name.as_str()))
    }

    fn first_file_prefixed(&self, file_prefix: &str) -> Option<&ArchivePayloadEntry> {
        let file_prefix = file_prefix.to_ascii_lowercase();
        self.entries.iter().find(|entry| {
            entry
                .file_name()
                .is_some_and(|file_name| file_name.starts_with(&file_prefix))
        })
    }

    fn first_root_file_named(&self, file_name: &str) -> Option<&ArchivePayloadEntry> {
        let file_name = file_name.to_ascii_lowercase();
        self.entries.iter().find(|entry| {
            !entry.normalized.contains('/') && entry.file_name() == Some(file_name.as_str())
        })
    }

    fn has_prefix_and_suffix(&self, prefix: &str, suffix: &str) -> bool {
        let prefix = normalize_archive_path(prefix);
        let suffix = suffix.to_ascii_lowercase();
        self.entries.iter().any(|entry| {
            entry.normalized.starts_with(&prefix) && entry.normalized.ends_with(&suffix)
        })
    }
}

impl ArchivePayloadEntry {
    fn file_name(&self) -> Option<&str> {
        self.normalized
            .rsplit('/')
            .next()
            .filter(|name| !name.is_empty())
    }
}

fn archive_payload_index<R: Read + Seek>(
    archive: &mut ZipArchive<R>,
) -> Result<ArchivePayloadIndex, ValidationError> {
    let mut entries = Vec::with_capacity(archive.len());
    for index in 0..archive.len() {
        let entry = archive
            .by_index(index)
            .map_err(|error| ValidationError::Zip(error.to_string()))?;
        let original = entry.name().to_string();
        entries.push(ArchivePayloadEntry {
            normalized: normalize_archive_path(&original),
            original,
        });
    }

    Ok(ArchivePayloadIndex { entries })
}

fn validate_payload_layout(
    manifest: &ManifestInfo,
    payload: &ArchivePayloadIndex,
    runtime_profile: PackageRuntimeProfile,
) -> Result<(), ValidationError> {
    for required in REQUIRED_RUST_HELPERS {
        if !payload.contains_exact(required) {
            return Err(ValidationError::MissingRequiredPayload {
                path: (*required).to_string(),
            });
        }
    }

    for (file_prefix, reason) in FORBIDDEN_PAYLOAD_FILE_PREFIXES {
        if let Some(entry) = payload.first_file_prefixed(file_prefix) {
            return Err(ValidationError::ForbiddenPayload {
                path: entry.original.clone(),
                reason,
            });
        }
    }

    for (file_name, reason) in FORBIDDEN_PAYLOAD_FILE_NAMES {
        if let Some(entry) = payload.first_file_named(file_name) {
            return Err(ValidationError::ForbiddenPayload {
                path: entry.original.clone(),
                reason,
            });
        }
    }

    if let Some(entry) = payload.first_under("workers/ocr/") {
        return Err(ValidationError::ForbiddenPayload {
            path: entry.original.clone(),
            reason: "OCR is Rust-native and must not ship workers/ocr",
        });
    }

    for file_name in FORBIDDEN_ROOT_LONGDOC_PAYLOADS {
        if let Some(entry) = payload.first_root_file_named(file_name) {
            return Err(ValidationError::ForbiddenPayload {
                path: entry.original.clone(),
                reason: "LongDoc PDF/export dependencies must stay isolated in the worker payload",
            });
        }
    }

    match runtime_profile {
        PackageRuntimeProfile::Hybrid => validate_hybrid_runtime_payload(manifest, payload)?,
        PackageRuntimeProfile::RustOnly => validate_rust_only_runtime_payload(payload)?,
    }

    Ok(())
}

fn validate_hybrid_runtime_payload(
    manifest: &ManifestInfo,
    payload: &ArchivePayloadIndex,
) -> Result<(), ValidationError> {
    if !retained_workers_required(manifest) {
        return validate_retained_runtime_payload_absent(
            payload,
            NO_RETAINED_WORKERS_FORBIDDEN_REASON,
        );
    }

    for required in REQUIRED_RETAINED_WORKER_PAYLOADS {
        if !payload.contains_exact(required) {
            return Err(ValidationError::MissingRequiredPayload {
                path: (*required).to_string(),
            });
        }
    }

    if !payload.has_prefix_and_suffix("dotnet/host/fxr/", "/hostfxr.dll") {
        return Err(ValidationError::MissingRequiredPayload {
            path: "dotnet/host/fxr/*/hostfxr.dll".to_string(),
        });
    }

    if !payload.has_prefix_and_suffix("dotnet/shared/Microsoft.NETCore.App/", "/coreclr.dll") {
        return Err(ValidationError::MissingRequiredPayload {
            path: "dotnet/shared/Microsoft.NETCore.App/*/coreclr.dll".to_string(),
        });
    }

    Ok(())
}

fn validate_rust_only_runtime_payload(
    payload: &ArchivePayloadIndex,
) -> Result<(), ValidationError> {
    validate_retained_runtime_payload_absent(payload, RUST_ONLY_FORBIDDEN_REASON)
}

fn validate_retained_runtime_payload_absent(
    payload: &ArchivePayloadIndex,
    reason: &'static str,
) -> Result<(), ValidationError> {
    for prefix in RUST_ONLY_FORBIDDEN_PREFIXES {
        if let Some(entry) = payload.first_under(prefix) {
            return Err(ValidationError::ForbiddenPayload {
                path: entry.original.clone(),
                reason,
            });
        }
    }

    if let Some(entry) = payload
        .entries
        .iter()
        .find(|entry| rust_only_forbidden_runtime_marker(entry))
    {
        return Err(ValidationError::ForbiddenPayload {
            path: entry.original.clone(),
            reason,
        });
    }

    Ok(())
}

fn rust_only_forbidden_runtime_marker(entry: &ArchivePayloadEntry) -> bool {
    let Some(file_name) = entry.file_name() else {
        return false;
    };

    file_name.ends_with(".runtimeconfig.json")
        || file_name.ends_with(".deps.json")
        || file_name.starts_with("easydict.workers.")
        || file_name.starts_with("easydict.nativebridge")
        || file_name.starts_with("easydict.sidecarclient")
        || file_name.starts_with("easydict.winui")
        || (file_name.starts_with("system.") && file_name.ends_with(".dll"))
        || file_name.starts_with("microsoft.csharp")
        || file_name.starts_with("microsoft.visualbasic")
        || file_name.starts_with("microsoft.win32")
        || RUST_ONLY_FORBIDDEN_RUNTIME_FILE_NAMES.contains(&file_name)
        || RUST_ONLY_FORBIDDEN_WORKER_SHARED_FILE_NAMES.contains(&file_name)
        || RUST_ONLY_FORBIDDEN_DOTNET_ASSEMBLY_FILE_NAMES.contains(&file_name)
        || RUST_ONLY_FORBIDDEN_DOTNET_SHARED_FRAMEWORKS
            .iter()
            .any(|prefix| entry.normalized.contains(prefix))
        || entry.normalized.contains("host/fxr/")
}

fn retained_workers_required(manifest: &ManifestInfo) -> bool {
    let Some(identity) = manifest.identity.as_ref() else {
        return false;
    };
    let Some(architecture) = identity.processor_architecture.as_deref() else {
        return false;
    };

    matches!(architecture.to_ascii_lowercase().as_str(), "x64" | "arm64")
}

fn normalize_archive_path(path: &str) -> String {
    let path = path.replace('\\', "/");
    let path = path.trim_start_matches("./");
    path.to_ascii_lowercase()
}

fn is_bundle_package_payload(normalized_path: &str) -> bool {
    normalized_path.ends_with(".appx") || normalized_path.ends_with(".msix")
}

fn sha256_lower(path: &Path) -> Result<String, WorkerSharedDedupeError> {
    let mut file = open_file(path)?;
    let mut hasher = Sha256::new();
    io::copy(&mut file, &mut hasher).map_err(|error| WorkerSharedDedupeError::Io {
        path: path.to_path_buf(),
        message: error.to_string(),
    })?;
    Ok(format!("{:x}", hasher.finalize()))
}

fn worker_directory_sizes(
    workers_dir: &Path,
) -> Result<Vec<WorkerDirectorySize>, WorkerSharedDedupeError> {
    let mut sizes = Vec::new();
    let entries = read_dir_entries(workers_dir)?;
    for entry in entries {
        let path = entry.path();
        if !path.is_dir() {
            continue;
        }
        let name = entry.file_name().to_string_lossy().into_owned();
        sizes.push(WorkerDirectorySize {
            name,
            bytes: directory_size(&path)?,
        });
    }
    sizes.sort_by(|left, right| left.name.cmp(&right.name));
    Ok(sizes)
}

fn directory_size(path: &Path) -> Result<u64, WorkerSharedDedupeError> {
    let mut total = 0;
    for entry in read_dir_entries(path)? {
        let entry_path = entry.path();
        let metadata = metadata(&entry_path)?;
        if metadata.is_dir() {
            total += directory_size(&entry_path)?;
        } else if metadata.is_file() {
            total += metadata.len();
        }
    }
    Ok(total)
}

fn read_dir_entries(path: &Path) -> Result<Vec<fs::DirEntry>, WorkerSharedDedupeError> {
    fs::read_dir(path)
        .map_err(|error| WorkerSharedDedupeError::Io {
            path: path.to_path_buf(),
            message: error.to_string(),
        })?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|error| WorkerSharedDedupeError::Io {
            path: path.to_path_buf(),
            message: error.to_string(),
        })
}

fn open_file(path: &Path) -> Result<File, WorkerSharedDedupeError> {
    File::open(path).map_err(|error| WorkerSharedDedupeError::Io {
        path: path.to_path_buf(),
        message: error.to_string(),
    })
}

fn metadata(path: &Path) -> Result<fs::Metadata, WorkerSharedDedupeError> {
    fs::metadata(path).map_err(|error| WorkerSharedDedupeError::Io {
        path: path.to_path_buf(),
        message: error.to_string(),
    })
}

fn create_dir_all(path: &Path) -> Result<(), WorkerSharedDedupeError> {
    fs::create_dir_all(path).map_err(|error| WorkerSharedDedupeError::Io {
        path: path.to_path_buf(),
        message: error.to_string(),
    })
}

fn copy_file(source: &Path, destination: &Path) -> Result<(), WorkerSharedDedupeError> {
    fs::copy(source, destination)
        .map(|_| ())
        .map_err(|error| WorkerSharedDedupeError::Io {
            path: destination.to_path_buf(),
            message: error.to_string(),
        })
}

fn remove_file(path: &Path) -> Result<(), WorkerSharedDedupeError> {
    fs::remove_file(path).map_err(|error| WorkerSharedDedupeError::Io {
        path: path.to_path_buf(),
        message: error.to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::io::{Seek, Write};
    use std::time::{SystemTime, UNIX_EPOCH};
    use zip::write::FileOptions;
    use zip::ZipWriter;

    fn hybrid_validation_options() -> MsixValidationOptions {
        MsixValidationOptions {
            runtime_profile: PackageRuntimeProfile::Hybrid,
            ..MsixValidationOptions::default()
        }
    }

    #[test]
    fn validation_options_default_to_rust_only_profile() {
        assert_eq!(
            MsixValidationOptions::default().runtime_profile,
            PackageRuntimeProfile::RustOnly
        );
    }

    #[test]
    fn validates_identity_min_version_and_signature() {
        let path = temp_msix_path("valid");
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            Some(b"sig"),
            &x64_payload_entries(),
        );

        let options = hybrid_validation_options();
        let result = validate_msix(&path, &options);

        assert!(result.is_ok());
        let _ = fs::remove_file(path);
    }

    #[test]
    fn allow_unsigned_skips_signature_check() {
        let path = temp_msix_path("unsigned");
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            None,
            &x64_payload_entries(),
        );
        let options = MsixValidationOptions {
            allow_unsigned: true,
            ..hybrid_validation_options()
        };

        let result = validate_msix(&path, &options);

        assert!(result.is_ok());
        let _ = fs::remove_file(path);
    }

    #[test]
    fn rejects_unsigned_bundle_by_default() {
        let path = temp_msix_path("missing-signature");
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            None,
            &x64_payload_entries(),
        );

        let options = hybrid_validation_options();
        let failures = validate_msix(&path, &options).unwrap_err();

        assert_eq!(
            failures,
            vec![(
                "PackageCertificateEkuValidator",
                ValidationError::MissingSignature
            )]
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn reports_identity_and_min_version_failures() {
        let path = temp_msix_path("invalid");
        write_msix(
            &path,
            manifest("wrong.Name", "CN=wrong", "10.0.10000.0", "x64"),
            Some(b"sig"),
            &x64_payload_entries(),
        );

        let options = hybrid_validation_options();
        let failures = validate_msix(&path, &options).unwrap_err();

        assert!(matches!(
            &failures[0],
            (
                "PackageFamilyNameValidator",
                ValidationError::IdentityNameMismatch { .. }
            )
        ));
        assert!(matches!(
            &failures[1],
            (
                "PackageMinimumVersionValidator",
                ValidationError::MinVersionTooLow { .. }
            )
        ));
        let _ = fs::remove_file(path);
    }

    #[test]
    fn x86_package_requires_rust_helpers_but_not_retained_workers_or_dotnet_runtime() {
        let path = temp_msix_path("x86-no-workers");
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x86",
            ),
            Some(b"sig"),
            &rust_helper_entries(),
        );

        let result = validate_msix(&path, &MsixValidationOptions::default());

        assert!(result.is_ok());
        let _ = fs::remove_file(path);
    }

    #[test]
    fn hybrid_x86_package_rejects_stale_retained_workers_or_dotnet_runtime() {
        let path = temp_msix_path("x86-stale-retained-workers");
        let mut entries = rust_helper_entries();
        entries.push(("workers/longdoc/Easydict.Workers.LongDoc.exe", b"longdoc"));
        entries.push(("dotnet/host/fxr/8.0.11/hostfxr.dll", b"hostfxr"));
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x86",
            ),
            Some(b"sig"),
            &entries,
        );

        let options = hybrid_validation_options();
        let failures = validate_msix(&path, &options).unwrap_err();

        assert_eq!(
            failures,
            vec![(
                PAYLOAD_LAYOUT_VALIDATOR,
                ValidationError::ForbiddenPayload {
                    path: "workers/longdoc/Easydict.Workers.LongDoc.exe".to_string(),
                    reason: "Packages without retained .NET workers must not ship retained workers or bundled .NET runtime"
                }
            )]
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn x64_and_arm64_packages_require_retained_workers_and_shared_dotnet_runtime() {
        let path = temp_msix_path("missing-retained-workers");
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "arm64",
            ),
            Some(b"sig"),
            &rust_helper_entries(),
        );

        let options = hybrid_validation_options();
        let failures = validate_msix(&path, &options).unwrap_err();

        assert_eq!(
            failures,
            vec![(
                PAYLOAD_LAYOUT_VALIDATOR,
                ValidationError::MissingRequiredPayload {
                    path: "workers/longdoc/Easydict.Workers.LongDoc.exe".to_string()
                }
            )]
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn rust_only_x64_package_accepts_rust_helpers_without_retained_workers_or_dotnet_runtime() {
        let path = temp_msix_path("rust-only-x64");
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            Some(b"sig"),
            &rust_helper_entries(),
        );
        let options = MsixValidationOptions {
            runtime_profile: PackageRuntimeProfile::RustOnly,
            ..MsixValidationOptions::default()
        };

        let result = validate_msix(&path, &options);

        assert!(result.is_ok());
        let _ = fs::remove_file(path);
    }

    #[test]
    fn rust_only_package_rejects_retained_workers_and_dotnet_runtime() {
        let path = temp_msix_path("rust-only-forbidden-runtime");
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "arm64",
            ),
            Some(b"sig"),
            &x64_payload_entries(),
        );
        let options = MsixValidationOptions {
            runtime_profile: PackageRuntimeProfile::RustOnly,
            ..MsixValidationOptions::default()
        };

        let failures = validate_msix(&path, &options).unwrap_err();

        assert_eq!(
            failures,
            vec![(
                PAYLOAD_LAYOUT_VALIDATOR,
                ValidationError::ForbiddenPayload {
                    path: "workers/longdoc/Easydict.Workers.LongDoc.exe".to_string(),
                    reason: "Rust-only packages must not ship retained .NET workers or bundled .NET runtime"
                }
            )]
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn rust_only_package_rejects_dotnet_runtime_even_without_workers() {
        let path = temp_msix_path("rust-only-dotnet-runtime");
        let mut entries = rust_helper_entries();
        entries.push(("dotnet/host/fxr/8.0.11/hostfxr.dll", b"hostfxr"));
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            Some(b"sig"),
            &entries,
        );
        let options = MsixValidationOptions {
            runtime_profile: PackageRuntimeProfile::RustOnly,
            ..MsixValidationOptions::default()
        };

        let failures = validate_msix(&path, &options).unwrap_err();

        assert_eq!(
            failures,
            vec![(
                PAYLOAD_LAYOUT_VALIDATOR,
                ValidationError::ForbiddenPayload {
                    path: "dotnet/host/fxr/8.0.11/hostfxr.dll".to_string(),
                    reason: "Rust-only packages must not ship retained .NET workers or bundled .NET runtime"
                }
            )]
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn rust_only_package_rejects_any_workers_directory_residue() {
        let path = temp_msix_path("rust-only-workers-shared");
        let mut entries = rust_helper_entries();
        entries.push(("workers/shared/Microsoft.WinUI.dll", b"shared"));
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            Some(b"sig"),
            &entries,
        );
        let options = MsixValidationOptions {
            runtime_profile: PackageRuntimeProfile::RustOnly,
            ..MsixValidationOptions::default()
        };

        let failures = validate_msix(&path, &options).unwrap_err();

        assert_eq!(
            failures,
            vec![(
                PAYLOAD_LAYOUT_VALIDATOR,
                ValidationError::ForbiddenPayload {
                    path: "workers/shared/Microsoft.WinUI.dll".to_string(),
                    reason: "Rust-only packages must not ship retained .NET workers or bundled .NET runtime"
                }
            )]
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn rust_only_package_rejects_root_worker_runtimeconfig_marker() {
        let path = temp_msix_path("rust-only-root-runtimeconfig");
        let mut entries = rust_helper_entries();
        entries.push((
            "Easydict.Workers.LocalAi.runtimeconfig.json",
            b"{\"runtimeOptions\":{}}",
        ));
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            Some(b"sig"),
            &entries,
        );
        let options = MsixValidationOptions {
            runtime_profile: PackageRuntimeProfile::RustOnly,
            ..MsixValidationOptions::default()
        };

        let failures = validate_msix(&path, &options).unwrap_err();

        assert_eq!(
            failures,
            vec![(
                PAYLOAD_LAYOUT_VALIDATOR,
                ValidationError::ForbiddenPayload {
                    path: "Easydict.Workers.LocalAi.runtimeconfig.json".to_string(),
                    reason: "Rust-only packages must not ship retained .NET workers or bundled .NET runtime"
                }
            )]
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn rust_only_package_rejects_dotnet_runtime_markers_outside_dotnet_directory() {
        let path = temp_msix_path("rust-only-runtime-marker");
        let mut entries = rust_helper_entries();
        entries.push((
            "shared/Microsoft.NETCore.App/8.0.11/System.Private.CoreLib.dll",
            b"corelib",
        ));
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            Some(b"sig"),
            &entries,
        );
        let options = MsixValidationOptions {
            runtime_profile: PackageRuntimeProfile::RustOnly,
            ..MsixValidationOptions::default()
        };

        let failures = validate_msix(&path, &options).unwrap_err();

        assert_eq!(
            failures,
            vec![(
                PAYLOAD_LAYOUT_VALIDATOR,
                ValidationError::ForbiddenPayload {
                    path: "shared/Microsoft.NETCore.App/8.0.11/System.Private.CoreLib.dll"
                        .to_string(),
                    reason: "Rust-only packages must not ship retained .NET workers or bundled .NET runtime"
                }
            )]
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn rust_only_package_rejects_windows_desktop_runtime_layout_residue() {
        let path = temp_msix_path("rust-only-windowsdesktop-runtime-layout");
        let mut entries = rust_helper_entries();
        entries.push((
            "runtime/shared/Microsoft.WindowsDesktop.App/8.0.11/PresentationCore.dll",
            b"presentation-core",
        ));
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            Some(b"sig"),
            &entries,
        );
        let options = MsixValidationOptions {
            runtime_profile: PackageRuntimeProfile::RustOnly,
            ..MsixValidationOptions::default()
        };

        let failures = validate_msix(&path, &options).unwrap_err();

        assert_eq!(
            failures,
            vec![(
                PAYLOAD_LAYOUT_VALIDATOR,
                ValidationError::ForbiddenPayload {
                    path: "runtime/shared/Microsoft.WindowsDesktop.App/8.0.11/PresentationCore.dll"
                        .to_string(),
                    reason: "Rust-only packages must not ship retained .NET workers or bundled .NET runtime"
                }
            )]
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn rust_only_package_rejects_loose_dotnet_assemblies_and_legacy_helpers() {
        for (payload, reason) in [
            (
                "Easydict.NativeBridge.exe",
                "browser Native Messaging host is now easydict-native-bridge.exe",
            ),
            (
                "Easydict.SidecarClient.exe",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Easydict.WinUI.exe",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Easydict.TranslationService.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Easydict.DocumentExport.dll",
                "LongDoc PDF/export dependencies must stay isolated in the worker payload",
            ),
            (
                "Easydict.Llm.Streaming.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Easydict.OpenVINO.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Easydict.WindowsAI.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "LexIndex.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "MDict.Csharp.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Polyglot.TextLayout.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "System.Text.Json.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Microsoft.CSharp.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Microsoft.Win32.Registry.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "WindowsBase.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Microsoft.WinUI.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Microsoft.Windows.SDK.NET.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "WinRT.Runtime.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "Microsoft.Web.WebView2.Core.Projection.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "netstandard.dll",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
            (
                "createdump.exe",
                "Rust-only packages must not ship retained .NET workers or bundled .NET runtime",
            ),
        ] {
            let path = temp_msix_path(&format!(
                "rust-only-loose-dotnet-{}",
                payload.replace('.', "-")
            ));
            let mut entries = rust_helper_entries();
            entries.push((payload, b"dotnet-payload"));
            write_msix(
                &path,
                manifest(
                    DEFAULT_EXPECTED_NAME,
                    DEFAULT_EXPECTED_PUBLISHER,
                    DEFAULT_MIN_VERSION,
                    "x64",
                ),
                Some(b"sig"),
                &entries,
            );
            let options = MsixValidationOptions {
                runtime_profile: PackageRuntimeProfile::RustOnly,
                ..MsixValidationOptions::default()
            };

            let failures = validate_msix(&path, &options).unwrap_err();

            assert_eq!(
                failures,
                vec![(
                    PAYLOAD_LAYOUT_VALIDATOR,
                    ValidationError::ForbiddenPayload {
                        path: payload.to_string(),
                        reason,
                    }
                )],
                "{payload} should be forbidden in rust-only MSIX payload"
            );
            let _ = fs::remove_file(path);
        }
    }

    #[test]
    fn runtime_profile_parser_accepts_hybrid_and_rust_only_spellings() {
        assert_eq!(
            PackageRuntimeProfile::parse("hybrid"),
            Some(PackageRuntimeProfile::Hybrid)
        );
        assert_eq!(
            PackageRuntimeProfile::parse("rust-only"),
            Some(PackageRuntimeProfile::RustOnly)
        );
        assert_eq!(
            PackageRuntimeProfile::parse("rust_only"),
            Some(PackageRuntimeProfile::RustOnly)
        );
        assert_eq!(PackageRuntimeProfile::parse("dotnet"), None);
    }

    #[test]
    fn rejects_retired_comphost_ocr_worker_and_dotnet_tool_payloads() {
        for compat_host_payload in [
            "Easydict.CompatHost.exe",
            "Easydict.CompatHost.dll",
            "Easydict.CompatHost.pdb",
            "Easydict.CompatHost.runtimeconfig.json",
            "Easydict.CompatHost.deps.json",
        ] {
            let path = temp_msix_path(&format!(
                "forbidden-runtime-payloads-{}",
                compat_host_payload.replace('.', "-")
            ));
            let mut entries = x64_payload_entries();
            entries.extend([
                (compat_host_payload, b"compat" as &[u8]),
                ("workers/ocr/Easydict.Workers.Ocr.exe", b"ocr"),
                ("tools/MsixValidate/MsixValidate.exe", b"tool"),
            ]);
            write_msix(
                &path,
                manifest(
                    DEFAULT_EXPECTED_NAME,
                    DEFAULT_EXPECTED_PUBLISHER,
                    DEFAULT_MIN_VERSION,
                    "x64",
                ),
                Some(b"sig"),
                &entries,
            );

            let options = hybrid_validation_options();
            let failures = validate_msix(&path, &options).unwrap_err();

            assert_eq!(
                failures,
                vec![(
                    PAYLOAD_LAYOUT_VALIDATOR,
                    ValidationError::ForbiddenPayload {
                        path: compat_host_payload.to_string(),
                        reason: ".NET CompatHost has been removed; Rust must start retained workers directly"
                    }
                )]
            );
            let _ = fs::remove_file(path);
        }
    }

    #[test]
    fn rejects_in_proc_longdoc_payload_at_msix_root_but_allows_worker_copy() {
        let path = temp_msix_path("root-longdoc-payload");
        let mut entries = x64_payload_entries();
        entries.extend([
            ("workers/longdoc/MuPDF.NET.dll", b"worker-pdf" as &[u8]),
            ("MuPDF.NET.dll", b"root-pdf"),
        ]);
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            Some(b"sig"),
            &entries,
        );

        let options = hybrid_validation_options();
        let failures = validate_msix(&path, &options).unwrap_err();

        assert_eq!(
            failures,
            vec![(
                PAYLOAD_LAYOUT_VALIDATOR,
                ValidationError::ForbiddenPayload {
                    path: "MuPDF.NET.dll".to_string(),
                    reason:
                        "LongDoc PDF/export dependencies must stay isolated in the worker payload"
                }
            )]
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn fix_minversion_rewrites_low_version_and_preserves_manifest_fields() {
        let path = temp_msix_path("fix-low");
        let fake = fake_makeappx_path("success");
        let log_path = fake.with_extension("log");
        write_fake_makeappx(&fake, false);
        write_msix(
            &path,
            manifest_with_fields("10.0.10000.0"),
            Some(b"sig"),
            &x64_payload_entries(),
        );
        let options = FixMinVersionOptions {
            makeappx_path: Some(fake.clone()),
            ..FixMinVersionOptions::default()
        };

        let outcome = fix_msix_min_version(&path, &options).expect("fix minversion");

        assert_eq!(
            outcome,
            FixMinVersionOutcome::Repacked {
                previous: "10.0.10000.0".to_string(),
                required: DEFAULT_MIN_VERSION.to_string()
            }
        );
        let args = fs::read_to_string(&log_path)
            .expect("read fake makeappx log")
            .trim()
            .to_string();
        assert!(args.contains("pack|/d|"));
        assert!(args.contains("|/p|"));
        assert!(args.ends_with("|/o"));
        let xml = read_manifest_xml_from_msix(&path);
        assert!(xml.contains(r#"MinVersion="10.0.19041.0""#));
        assert!(xml.contains(r#"Version="1.2.3.4""#));
        assert!(xml.contains(r#"ProcessorArchitecture="x64""#));
        assert!(xml.contains(r#"Publisher="CN=publisher""#));
        assert!(xml.contains(r#"MaxVersionTested="10.0.22621.0""#));
        assert!(xml.contains(r#"uap10:RuntimeBehavior="packagedClassicApp""#));
        let _ = fs::remove_file(path);
        let _ = fs::remove_file(fake);
        let _ = fs::remove_file(log_path);
    }

    #[test]
    fn fix_minversion_noops_for_equal_or_higher_version_without_makeappx() {
        for (name, min_version) in [("equal", DEFAULT_MIN_VERSION), ("higher", "10.0.22621.0")] {
            let path = temp_msix_path(name);
            write_msix(
                &path,
                manifest_with_fields(min_version),
                Some(b"sig"),
                &x64_payload_entries(),
            );
            let before = fs::read(&path).expect("read package before no-op");
            let options = FixMinVersionOptions {
                makeappx_path: Some(PathBuf::from(r"Z:\missing\MakeAppx.exe")),
                ..FixMinVersionOptions::default()
            };

            let outcome = fix_msix_min_version(&path, &options).expect("no-op minversion");

            assert_eq!(
                outcome,
                FixMinVersionOutcome::NoChangeNeeded {
                    current: min_version.to_string(),
                    required: DEFAULT_MIN_VERSION.to_string()
                }
            );
            assert_eq!(fs::read(&path).expect("read package after no-op"), before);
            let _ = fs::remove_file(path);
        }
    }

    #[test]
    fn fix_minversion_reports_manifest_shape_errors() {
        let missing_manifest = temp_msix_path("missing-manifest");
        write_msix_without_manifest(&missing_manifest);
        assert_eq!(
            fix_msix_min_version(&missing_manifest, &FixMinVersionOptions::default()).unwrap_err(),
            FixMinVersionError::MissingManifest
        );

        let missing_tdf = temp_msix_path("missing-tdf");
        write_msix(
            &missing_tdf,
            r#"<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Dependencies /></Package>"#.to_string(),
            Some(b"sig"),
            &[],
        );
        assert_eq!(
            fix_msix_min_version(&missing_tdf, &FixMinVersionOptions::default()).unwrap_err(),
            FixMinVersionError::MissingTargetDeviceFamily
        );

        let missing_min = temp_msix_path("missing-min");
        write_msix(
            &missing_min,
            r#"<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Dependencies><TargetDeviceFamily Name="Windows.Universal" /></Dependencies></Package>"#.to_string(),
            Some(b"sig"),
            &[],
        );
        assert_eq!(
            fix_msix_min_version(&missing_min, &FixMinVersionOptions::default()).unwrap_err(),
            FixMinVersionError::MissingMinVersion
        );

        let invalid_min = temp_msix_path("invalid-min");
        write_msix(
            &invalid_min,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                "not-a-version",
                "x64",
            ),
            Some(b"sig"),
            &[],
        );
        assert_eq!(
            fix_msix_min_version(&invalid_min, &FixMinVersionOptions::default()).unwrap_err(),
            FixMinVersionError::InvalidActualMinVersion("not-a-version".to_string())
        );

        let invalid_required = temp_msix_path("invalid-required");
        write_msix(
            &invalid_required,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                DEFAULT_MIN_VERSION,
                "x64",
            ),
            Some(b"sig"),
            &[],
        );
        let options = FixMinVersionOptions {
            min_version: "nope".to_string(),
            ..FixMinVersionOptions::default()
        };
        assert_eq!(
            fix_msix_min_version(&invalid_required, &options).unwrap_err(),
            FixMinVersionError::InvalidExpectedMinVersion("nope".to_string())
        );

        for path in [
            missing_manifest,
            missing_tdf,
            missing_min,
            invalid_min,
            invalid_required,
        ] {
            let _ = fs::remove_file(path);
        }
    }

    #[test]
    fn fix_minversion_makeappx_failure_preserves_original_package() {
        let path = temp_msix_path("makeappx-fails");
        let fake = fake_makeappx_path("failure");
        write_fake_makeappx(&fake, true);
        write_msix(
            &path,
            manifest(
                DEFAULT_EXPECTED_NAME,
                DEFAULT_EXPECTED_PUBLISHER,
                "10.0.10000.0",
                "x64",
            ),
            Some(b"sig"),
            &x64_payload_entries(),
        );
        let before = fs::read(&path).expect("read package before failed fix");
        let options = FixMinVersionOptions {
            makeappx_path: Some(fake.clone()),
            ..FixMinVersionOptions::default()
        };

        let error = fix_msix_min_version(&path, &options).unwrap_err();

        assert_eq!(
            error,
            FixMinVersionError::MakeAppxFailed { exit_code: Some(7) }
        );
        assert_eq!(
            fs::read(&path).expect("read package after failed fix"),
            before
        );
        let _ = fs::remove_file(path);
        let _ = fs::remove_file(fake);
    }

    #[test]
    fn prepare_package_inputs_rewrites_identity_and_preserves_minversion() {
        let temp = tempfile::Builder::new()
            .prefix("easydict-msix-prepare-")
            .tempdir()
            .expect("temp publish dir");
        create_required_msix_assets(temp.path());
        for index in 0..MIN_TARGETSIZE_ICON_COUNT {
            write_test_file(
                temp.path(),
                &format!("Assets/AppIcon.targetsize-{index}.png"),
                b"icon",
            );
        }
        write_test_file(temp.path(), "Easydict.WinUI.pri", b"pri");
        let source_manifest = temp.path().join("Package.appxmanifest");
        fs::write(&source_manifest, manifest_with_fields(DEFAULT_MIN_VERSION))
            .expect("write source manifest");
        let output_manifest = temp.path().join("Package.x64.appxmanifest");

        let outcome = prepare_package_inputs(&PreparePackageInputsOptions {
            platform: "arm64".to_string(),
            publish_dir: temp.path().to_path_buf(),
            manifest_path: source_manifest,
            output_manifest: output_manifest.clone(),
            msix_version: Some("2.3.4.5".to_string()),
            verify_targetsize_icons: true,
        })
        .expect("prepare package inputs");

        assert_eq!(outcome.output_manifest, output_manifest);
        assert!(outcome.copied_pri);
        assert!(!outcome.resources_pri_already_present);
        assert_eq!(
            outcome.targetsize_icon_count,
            Some(MIN_TARGETSIZE_ICON_COUNT)
        );
        assert_eq!(
            fs::read(temp.path().join("resources.pri")).expect("read copied pri"),
            b"pri"
        );
        let xml = fs::read_to_string(&outcome.output_manifest).expect("read prepared manifest");
        assert!(xml.contains(r#"ProcessorArchitecture="arm64""#));
        assert!(xml.contains(r#"Version="2.3.4.5""#));
        assert!(xml.contains(r#"MinVersion="10.0.19041.0""#));
        assert!(xml.contains(r#"MaxVersionTested="10.0.22621.0""#));
    }

    #[test]
    fn prepare_package_inputs_reports_missing_assets_and_targetsize_icons() {
        let temp = tempfile::Builder::new()
            .prefix("easydict-msix-prepare-missing-")
            .tempdir()
            .expect("temp publish dir");
        let source_manifest = temp.path().join("Package.appxmanifest");
        fs::write(&source_manifest, manifest_with_fields(DEFAULT_MIN_VERSION))
            .expect("write source manifest");

        let missing_assets = prepare_package_inputs(&PreparePackageInputsOptions {
            platform: "x64".to_string(),
            publish_dir: temp.path().to_path_buf(),
            manifest_path: source_manifest.clone(),
            output_manifest: temp.path().join("out.appxmanifest"),
            msix_version: None,
            verify_targetsize_icons: false,
        })
        .unwrap_err();

        assert_eq!(
            missing_assets,
            PreparePackageInputsError::MissingRequiredAssets(
                REQUIRED_MSIX_ASSETS
                    .iter()
                    .map(|asset| (*asset).to_string())
                    .collect()
            )
        );

        create_required_msix_assets(temp.path());
        let not_enough_icons = prepare_package_inputs(&PreparePackageInputsOptions {
            platform: "x64".to_string(),
            publish_dir: temp.path().to_path_buf(),
            manifest_path: source_manifest,
            output_manifest: temp.path().join("out.appxmanifest"),
            msix_version: None,
            verify_targetsize_icons: true,
        })
        .unwrap_err();

        assert_eq!(
            not_enough_icons,
            PreparePackageInputsError::NotEnoughTargetsizeIcons {
                found: 0,
                required: MIN_TARGETSIZE_ICON_COUNT
            }
        );
    }

    #[test]
    fn verify_bundle_minversion_accepts_nested_appx_and_msix_payloads() {
        let path = temp_msix_path("bundle-ok").with_extension("msixbundle");
        let x64_package = package_bytes(manifest(
            DEFAULT_EXPECTED_NAME,
            DEFAULT_EXPECTED_PUBLISHER,
            DEFAULT_MIN_VERSION,
            "x64",
        ));
        let arm64_package = package_bytes(manifest(
            DEFAULT_EXPECTED_NAME,
            DEFAULT_EXPECTED_PUBLISHER,
            "10.0.22621.0",
            "arm64",
        ));
        write_bundle(
            &path,
            &[
                (
                    "AppxMetadata/AppxBundleManifest.xml",
                    br#"<Bundle></Bundle>"# as &[u8],
                ),
                ("Easydict-x64.appx", &x64_package),
                ("nested/Easydict-arm64.msix", &arm64_package),
            ],
        );

        let report = verify_bundle_min_version(&path, &BundleMinVersionOptions::default()).unwrap();

        assert!(report.has_bundle_manifest);
        assert_eq!(report.packages.len(), 2);
        assert_eq!(report.packages[0].path, "Easydict-x64.appx");
        assert_eq!(
            report.packages[0].target_device_family_name.as_deref(),
            Some("Windows.Universal")
        );
        assert_eq!(report.packages[0].min_version, DEFAULT_MIN_VERSION);
        assert_eq!(
            report.packages[0].max_version_tested.as_deref(),
            Some("10.0.22621.0")
        );
        assert_eq!(report.packages[1].path, "nested/Easydict-arm64.msix");
        assert_eq!(report.packages[1].min_version, "10.0.22621.0");
        let _ = fs::remove_file(path);
    }

    #[test]
    fn verify_bundle_minversion_rejects_low_nested_package_minversion() {
        let path = temp_msix_path("bundle-low").with_extension("msixbundle");
        let low_package = package_bytes(manifest(
            DEFAULT_EXPECTED_NAME,
            DEFAULT_EXPECTED_PUBLISHER,
            "10.0.10000.0",
            "x64",
        ));
        write_bundle(&path, &[("Easydict-x64.appx", &low_package)]);

        let error =
            verify_bundle_min_version(&path, &BundleMinVersionOptions::default()).unwrap_err();

        assert_eq!(
            error,
            BundleMinVersionError::PackageManifest {
                package: "Easydict-x64.appx".to_string(),
                error: ValidationError::MinVersionTooLow {
                    actual: VersionParts([10, 0, 10000, 0]),
                    expected: VersionParts([10, 0, 19041, 0])
                }
            }
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn verify_bundle_minversion_reports_missing_payload_or_bad_required_version() {
        let path = temp_msix_path("bundle-empty").with_extension("msixbundle");
        write_bundle(
            &path,
            &[("AppxMetadata/AppxBundleManifest.xml", b"<Bundle />")],
        );

        assert_eq!(
            verify_bundle_min_version(&path, &BundleMinVersionOptions::default()).unwrap_err(),
            BundleMinVersionError::MissingPackagePayload
        );

        let options = BundleMinVersionOptions {
            required_min_version: "bad-version".to_string(),
        };
        assert_eq!(
            verify_bundle_min_version(&path, &options).unwrap_err(),
            BundleMinVersionError::InvalidRequiredMinVersion("bad-version".to_string())
        );
        let _ = fs::remove_file(path);
    }

    #[test]
    fn dedupe_worker_shared_files_noops_without_two_worker_directories() {
        let temp = tempfile::Builder::new()
            .prefix("easydict-dedupe-no-workers-")
            .tempdir()
            .expect("temp publish dir");

        let outcome = dedupe_worker_shared_files(temp.path()).expect("dedupe without workers");

        assert_eq!(outcome.moved_count, 0);
        assert_eq!(
            outcome.status,
            WorkerSharedDedupeStatus::NoWorkersDirectory {
                path: temp.path().join("workers")
            }
        );

        write_test_file(
            temp.path(),
            "workers/longdoc/Microsoft.WinUI.dll",
            b"single-worker",
        );
        let outcome = dedupe_worker_shared_files(temp.path()).expect("dedupe one worker");

        assert_eq!(outcome.moved_count, 0);
        assert_eq!(
            outcome.status,
            WorkerSharedDedupeStatus::FewerThanTwoWorkerDirectories
        );
        assert_eq!(
            outcome.worker_sizes,
            vec![WorkerDirectorySize {
                name: "longdoc".to_string(),
                bytes: b"single-worker".len() as u64
            }]
        );
    }

    #[test]
    fn dedupe_worker_shared_files_moves_only_allowlisted_identical_files() {
        let temp = tempfile::Builder::new()
            .prefix("easydict-dedupe-workers-")
            .tempdir()
            .expect("temp publish dir");
        write_test_file(
            temp.path(),
            "workers/longdoc/Microsoft.WinUI.dll",
            b"same-winui",
        );
        write_test_file(
            temp.path(),
            "workers/localai/Microsoft.WinUI.dll",
            b"same-winui",
        );
        write_test_file(
            temp.path(),
            "workers/longdoc/Microsoft.Windows.SDK.NET.dll",
            b"longdoc-sdk",
        );
        write_test_file(
            temp.path(),
            "workers/localai/Microsoft.Windows.SDK.NET.dll",
            b"localai-sdk",
        );
        write_test_file(temp.path(), "workers/longdoc/Other.dll", b"same-other");
        write_test_file(temp.path(), "workers/localai/Other.dll", b"same-other");

        let outcome = dedupe_worker_shared_files(temp.path()).expect("dedupe workers");

        assert_eq!(outcome.status, WorkerSharedDedupeStatus::Completed);
        assert_eq!(outcome.moved_count, 1);
        assert_eq!(outcome.saved_bytes, b"same-winui".len() as u64);
        assert_eq!(
            outcome.shared_files,
            vec![WorkerSharedFile {
                file_name: "Microsoft.WinUI.dll".to_string(),
                worker_count: 2
            }]
        );
        assert_eq!(
            outcome.skipped_different_hashes,
            vec!["Microsoft.Windows.SDK.NET.dll".to_string()]
        );
        assert!(temp
            .path()
            .join("workers/shared/Microsoft.WinUI.dll")
            .exists());
        assert!(!temp
            .path()
            .join("workers/longdoc/Microsoft.WinUI.dll")
            .exists());
        assert!(!temp
            .path()
            .join("workers/localai/Microsoft.WinUI.dll")
            .exists());
        assert!(temp.path().join("workers/longdoc/Other.dll").exists());
        assert!(temp.path().join("workers/localai/Other.dll").exists());
        assert!(temp
            .path()
            .join("workers/longdoc/Microsoft.Windows.SDK.NET.dll")
            .exists());
        assert!(outcome
            .worker_sizes
            .iter()
            .any(|size| size.name == "shared" && size.bytes == b"same-winui".len() as u64));
    }

    fn temp_msix_path(name: &str) -> std::path::PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system time after unix epoch")
            .as_nanos();
        std::env::temp_dir().join(format!(
            "easydict-msix-validate-{name}-{}-{nanos}.msix",
            std::process::id()
        ))
    }

    fn write_msix(
        path: &Path,
        manifest: String,
        signature: Option<&[u8]>,
        entries: &[(&str, &[u8])],
    ) {
        let file = File::create(path).expect("create test msix");
        let mut writer = ZipWriter::new(file);
        let options: FileOptions<'_, ()> = FileOptions::default();
        add_file(
            &mut writer,
            "AppxManifest.xml",
            manifest.as_bytes(),
            options,
        );
        if let Some(signature) = signature {
            add_file(&mut writer, "AppxSignature.p7x", signature, options);
        }
        for (name, contents) in entries {
            add_file(&mut writer, name, contents, options);
        }
        writer.finish().expect("finish test msix");
    }

    fn package_bytes(manifest: String) -> Vec<u8> {
        let cursor = Cursor::new(Vec::new());
        let mut writer = ZipWriter::new(cursor);
        let options: FileOptions<'_, ()> = FileOptions::default();
        add_file(
            &mut writer,
            "AppxManifest.xml",
            manifest.as_bytes(),
            options,
        );
        writer.finish().expect("finish test package").into_inner()
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

    fn write_test_file(root: &Path, relative_path: &str, contents: &[u8]) {
        let path = root.join(relative_path);
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).expect("create test parent");
        }
        fs::write(path, contents).expect("write test file");
    }

    fn create_required_msix_assets(root: &Path) {
        for asset in REQUIRED_MSIX_ASSETS {
            write_test_file(root, asset, b"asset");
        }
    }

    fn write_msix_without_manifest(path: &Path) {
        let file = File::create(path).expect("create test msix");
        let mut writer = ZipWriter::new(file);
        let options: FileOptions<'_, ()> = FileOptions::default();
        add_file(&mut writer, "payload.txt", b"payload", options);
        writer.finish().expect("finish test msix");
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

    fn manifest_with_fields(min_version: &str) -> String {
        format!(
            r#"<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10">
  <Identity Name="{DEFAULT_EXPECTED_NAME}" Publisher="CN=publisher" Version="1.2.3.4" ProcessorArchitecture="x64" />
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Universal" MinVersion="{min_version}" MaxVersionTested="10.0.22621.0" />
  </Dependencies>
  <Applications>
    <Application Id="App" uap10:RuntimeBehavior="packagedClassicApp" />
  </Applications>
</Package>"#
        )
    }

    fn read_manifest_xml_from_msix(path: &Path) -> String {
        let file = File::open(path).expect("open msix");
        let mut archive = ZipArchive::new(file).expect("open zip");
        let mut entry = archive.by_name("AppxManifest.xml").expect("manifest entry");
        let mut xml = String::new();
        entry.read_to_string(&mut xml).expect("read manifest");
        xml
    }

    fn fake_makeappx_path(name: &str) -> PathBuf {
        let mut path = temp_msix_path(name);
        path.set_extension(if cfg!(windows) { "cmd" } else { "sh" });
        path
    }

    fn write_fake_makeappx(path: &Path, fail: bool) {
        if cfg!(windows) {
            let exit = if fail { "exit /b 7" } else { "" };
            fs::write(
                path,
                format!(
                    "@echo off\r\n\
                     echo %1^|%2^|%3^|%4^|%5^|%6>{log}\r\n\
                     {exit}\r\n\
                     powershell -NoProfile -ExecutionPolicy Bypass -Command \"Compress-Archive -Path '%~3\\*' -DestinationPath '%~5' -Force\"\r\n",
                    log = path.with_extension("log").display()
                ),
            )
            .expect("write fake makeappx cmd");
        } else {
            let exit = if fail { "exit 7" } else { "" };
            fs::write(
                path,
                format!(
                    "#!/usr/bin/env sh\nprintf '%s|%s|%s|%s|%s|%s' \"$1\" \"$2\" \"$3\" \"$4\" \"$5\" \"$6\" > '{}'\n{}\n(cd \"$3\" && zip -qr \"$5\" .)\n",
                    path.with_extension("log").display(),
                    exit
                ),
            )
            .expect("write fake makeappx sh");
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                let mut permissions = fs::metadata(path).expect("fake metadata").permissions();
                permissions.set_mode(0o755);
                fs::set_permissions(path, permissions).expect("chmod fake");
            }
        }
    }

    fn rust_helper_entries() -> Vec<(&'static str, &'static [u8])> {
        vec![
            ("easydict-native-bridge.exe", b"native-bridge"),
            ("easydict_browser_registrar.exe", b"registrar"),
            ("BrowserHostRegistrar.exe", b"registrar-alias"),
            ("easydict_cli.exe", b"cli"),
            ("easydict_long_doc.exe", b"longdoc-cli"),
        ]
    }

    fn x64_payload_entries() -> Vec<(&'static str, &'static [u8])> {
        let mut entries = rust_helper_entries();
        entries.extend([
            (
                "workers/longdoc/Easydict.Workers.LongDoc.exe",
                b"longdoc" as &[u8],
            ),
            ("workers/localai/Easydict.Workers.LocalAi.exe", b"localai"),
            ("dotnet/host/fxr/8.0.11/hostfxr.dll", b"hostfxr"),
            (
                "dotnet/shared/Microsoft.NETCore.App/8.0.11/coreclr.dll",
                b"coreclr",
            ),
        ]);
        entries
    }
}

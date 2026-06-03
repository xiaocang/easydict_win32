use std::fmt;
use std::fs::{self, File};
use std::io::{self, BufReader, Seek, Write};
use std::path::{Path, PathBuf};

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
}

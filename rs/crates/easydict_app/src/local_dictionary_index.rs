use crate::lex_index::{LexIndex, LexIndexError};
use crate::state::ImportedMdxDictionary;
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const CURRENT_INDEX_FORMAT_VERSION: i32 = 1;
pub const INDEX_FILE_NAME: &str = "index.bin";
pub const MANIFEST_FILE_NAME: &str = "manifest.json";
pub const DEFAULT_NORMALIZATION_ID: &str = "nfkc-lower-invariant-v1";
static TEMP_FILE_COUNTER: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LocalDictionaryIndexDescriptor {
    pub service_id: String,
    pub display_name: String,
    pub file_path: String,
    pub is_encrypted: bool,
    pub regcode: Option<String>,
    pub email: Option<String>,
}

impl LocalDictionaryIndexDescriptor {
    pub fn is_queryable_from_credentials(&self) -> bool {
        !self.is_encrypted
            || (self
                .regcode
                .as_deref()
                .is_some_and(|value| !value.trim().is_empty())
                && self
                    .email
                    .as_deref()
                    .is_some_and(|value| !value.trim().is_empty()))
    }
}

impl From<&ImportedMdxDictionary> for LocalDictionaryIndexDescriptor {
    fn from(dictionary: &ImportedMdxDictionary) -> Self {
        Self {
            service_id: dictionary.service_id.clone(),
            display_name: dictionary.display_name.clone(),
            file_path: dictionary.file_path.clone(),
            is_encrypted: dictionary.is_encrypted,
            regcode: dictionary.regcode.clone(),
            email: dictionary.email.clone(),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LocalDictionaryIndexSuggestionItem {
    pub key: String,
    pub dict_display_name: String,
    pub dict_service_id: String,
}

#[derive(Debug)]
pub enum LocalDictionaryIndexError {
    EmptyServiceId,
    Io(std::io::Error),
    Json(serde_json::Error),
    KeyEnumeration(String),
    LexIndex(LexIndexError),
}

impl fmt::Display for LocalDictionaryIndexError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::EmptyServiceId => formatter.write_str("Local dictionary service id is empty."),
            Self::Io(error) => write!(formatter, "Local dictionary index I/O failed: {error}"),
            Self::Json(error) => {
                write!(
                    formatter,
                    "Local dictionary index manifest JSON failed: {error}"
                )
            }
            Self::KeyEnumeration(message) => {
                write!(
                    formatter,
                    "Local dictionary key enumeration failed: {message}"
                )
            }
            Self::LexIndex(error) => write!(formatter, "Local dictionary LexIndex failed: {error}"),
        }
    }
}

impl std::error::Error for LocalDictionaryIndexError {}

impl From<std::io::Error> for LocalDictionaryIndexError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<serde_json::Error> for LocalDictionaryIndexError {
    fn from(error: serde_json::Error) -> Self {
        Self::Json(error)
    }
}

impl From<LexIndexError> for LocalDictionaryIndexError {
    fn from(error: LexIndexError) -> Self {
        Self::LexIndex(error)
    }
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct LocalDictionaryIndexManifest {
    pub service_id: String,
    pub source_path: String,
    pub source_last_write_utc: String,
    pub source_length: u64,
    pub index_format_version: i32,
    pub normalization_id: String,
    pub entry_count: usize,
}

impl LocalDictionaryIndexManifest {
    pub fn matches(&self, other: &Self) -> bool {
        self.service_id == other.service_id
            && self.source_path.eq_ignore_ascii_case(&other.source_path)
            && self.source_last_write_utc == other.source_last_write_utc
            && self.source_length == other.source_length
            && self.index_format_version == other.index_format_version
            && self.normalization_id == other.normalization_id
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct ServiceDescriptor {
    display_name: String,
    source_path: String,
    is_queryable: bool,
}

#[derive(Debug, Eq, PartialEq)]
struct LoadedIndexEntry {
    index: LexIndex,
    manifest: LocalDictionaryIndexManifest,
    display_name: String,
    is_queryable: bool,
}

#[derive(Debug)]
pub struct LocalDictionaryIndexService {
    index_root: PathBuf,
    service_descriptors: HashMap<String, ServiceDescriptor>,
    loaded_indexes: HashMap<String, LoadedIndexEntry>,
}

impl LocalDictionaryIndexService {
    pub fn new() -> Result<Self, LocalDictionaryIndexError> {
        Self::with_index_root(default_local_dictionary_index_root())
    }

    pub fn with_index_root(
        index_root: impl Into<PathBuf>,
    ) -> Result<Self, LocalDictionaryIndexError> {
        let index_root = index_root.into();
        fs::create_dir_all(&index_root)?;
        Ok(Self {
            index_root,
            service_descriptors: HashMap::new(),
            loaded_indexes: HashMap::new(),
        })
    }

    pub fn index_root(&self) -> &Path {
        &self.index_root
    }

    pub fn dictionary_folder(
        &self,
        service_id: &str,
    ) -> Result<PathBuf, LocalDictionaryIndexError> {
        if service_id.trim().is_empty() {
            return Err(LocalDictionaryIndexError::EmptyServiceId);
        }

        Ok(self.index_root.join(escape_data_string(service_id)))
    }

    pub fn ensure_index_from_keys(
        &mut self,
        dictionary: &LocalDictionaryIndexDescriptor,
        keys: impl IntoIterator<Item = impl AsRef<str>>,
    ) -> Result<(), LocalDictionaryIndexError> {
        self.ensure_index(dictionary, true, keys)
    }

    pub fn ensure_index(
        &mut self,
        dictionary: &LocalDictionaryIndexDescriptor,
        can_enumerate_keys: bool,
        keys: impl IntoIterator<Item = impl AsRef<str>>,
    ) -> Result<(), LocalDictionaryIndexError> {
        self.ensure_index_with_key_loader(dictionary, can_enumerate_keys, || {
            Ok::<Vec<String>, LocalDictionaryIndexError>(
                keys.into_iter()
                    .map(|key| key.as_ref().to_string())
                    .collect(),
            )
        })
    }

    pub fn ensure_index_with_key_loader<E, F>(
        &mut self,
        dictionary: &LocalDictionaryIndexDescriptor,
        can_enumerate_keys: bool,
        load_keys: F,
    ) -> Result<(), LocalDictionaryIndexError>
    where
        E: fmt::Display,
        F: FnOnce() -> Result<Vec<String>, E>,
    {
        self.upsert_descriptor(dictionary, can_enumerate_keys);

        if !can_enumerate_keys || !Path::new(&dictionary.file_path).exists() {
            return Ok(());
        }

        let folder_path = self.dictionary_folder(&dictionary.service_id)?;
        let index_path = folder_path.join(INDEX_FILE_NAME);
        let manifest_path = folder_path.join(MANIFEST_FILE_NAME);
        fs::create_dir_all(&folder_path)?;

        let source_info = fs::metadata(&dictionary.file_path)?;
        let mut current_fingerprint = LocalDictionaryIndexManifest {
            service_id: dictionary.service_id.clone(),
            source_path: dictionary.file_path.clone(),
            source_last_write_utc: format_system_time_utc(source_info.modified()?),
            source_length: source_info.len(),
            index_format_version: CURRENT_INDEX_FORMAT_VERSION,
            normalization_id: DEFAULT_NORMALIZATION_ID.to_string(),
            entry_count: 0,
        };

        if let Some(existing_manifest) = load_manifest(&manifest_path) {
            if index_path.exists() && existing_manifest.matches(&current_fingerprint) {
                self.loaded_indexes.remove(&dictionary.service_id);
                return Ok(());
            }
        }

        let temp_index_path = temp_file_path(&folder_path, INDEX_FILE_NAME);
        let temp_manifest_path = temp_file_path(&folder_path, MANIFEST_FILE_NAME);

        let build_result = (|| {
            let keys = load_keys()
                .map_err(|error| LocalDictionaryIndexError::KeyEnumeration(error.to_string()))?;
            let built_index = LexIndex::from_keys(keys);
            fs::write(&temp_index_path, built_index.to_bytes())?;

            current_fingerprint.entry_count = built_index.metadata().entry_count;
            let manifest_json = serde_json::to_string_pretty(&current_fingerprint)?;
            fs::write(&temp_manifest_path, manifest_json)?;

            replace_file(&temp_index_path, &index_path)?;
            replace_file(&temp_manifest_path, &manifest_path)?;

            self.loaded_indexes.insert(
                dictionary.service_id.clone(),
                LoadedIndexEntry {
                    index: built_index,
                    manifest: current_fingerprint,
                    display_name: dictionary.display_name.clone(),
                    is_queryable: true,
                },
            );

            Ok(())
        })();

        let _ = fs::remove_file(&temp_index_path);
        let _ = fs::remove_file(&temp_manifest_path);
        build_result
    }

    pub fn register_descriptor(&mut self, dictionary: &LocalDictionaryIndexDescriptor) {
        self.upsert_descriptor(dictionary, dictionary.is_queryable_from_credentials());
    }

    pub fn remove_dictionary(&mut self, service_id: &str, delete_files: bool) {
        if service_id.trim().is_empty() {
            return;
        }

        self.service_descriptors.remove(service_id);
        self.loaded_indexes.remove(service_id);

        if delete_files {
            if let Ok(folder_path) = self.dictionary_folder(service_id) {
                let _ = fs::remove_dir_all(folder_path);
            }
        }
    }

    pub fn complete(
        &mut self,
        prefix: &str,
        service_ids: &[impl AsRef<str>],
        limit: usize,
    ) -> Vec<LocalDictionaryIndexSuggestionItem> {
        self.query(prefix, service_ids, limit, |index, query, result_limit| {
            index.complete(query, result_limit)
        })
    }

    pub fn match_pattern(
        &mut self,
        pattern: &str,
        service_ids: &[impl AsRef<str>],
        limit: usize,
    ) -> Vec<LocalDictionaryIndexSuggestionItem> {
        self.query(pattern, service_ids, limit, |index, query, result_limit| {
            index.match_pattern(query, result_limit)
        })
    }

    fn query(
        &mut self,
        query: &str,
        service_ids: &[impl AsRef<str>],
        limit: usize,
        executor: impl Fn(&LexIndex, &str, usize) -> Vec<String>,
    ) -> Vec<LocalDictionaryIndexSuggestionItem> {
        if query.trim().is_empty() || service_ids.is_empty() || limit == 0 {
            return Vec::new();
        }

        let mut results = Vec::with_capacity(limit.min(20));
        let mut seen_keys = HashSet::new();

        for service_id in service_ids {
            let service_id = service_id.as_ref();
            if !self.ensure_queryable_index_loaded(service_id) {
                continue;
            };

            let (keys, display_name) = {
                let Some(entry) = self.loaded_indexes.get(service_id) else {
                    continue;
                };
                if !entry.is_queryable {
                    continue;
                }
                (
                    executor(&entry.index, query, limit),
                    entry.display_name.clone(),
                )
            };

            for key in keys {
                if !seen_keys.insert(key.to_lowercase()) {
                    continue;
                }

                results.push(LocalDictionaryIndexSuggestionItem {
                    key,
                    dict_display_name: display_name.clone(),
                    dict_service_id: service_id.to_string(),
                });

                if results.len() >= limit {
                    return results;
                }
            }
        }

        results
    }

    fn ensure_queryable_index_loaded(&mut self, service_id: &str) -> bool {
        if let Some(entry) = self.loaded_indexes.get(service_id) {
            return entry.is_queryable;
        }

        if self
            .service_descriptors
            .get(service_id)
            .is_some_and(|descriptor| !descriptor.is_queryable)
        {
            return false;
        }

        let Some(folder_path) = self.dictionary_folder(service_id).ok() else {
            return false;
        };
        let index_path = folder_path.join(INDEX_FILE_NAME);
        let manifest_path = folder_path.join(MANIFEST_FILE_NAME);
        if !index_path.exists() || !manifest_path.exists() {
            return false;
        }

        let Some(manifest) = load_manifest(&manifest_path) else {
            return false;
        };
        if manifest.index_format_version != CURRENT_INDEX_FORMAT_VERSION {
            return false;
        }

        let mut display_name = manifest.service_id.clone();
        let mut is_queryable = true;
        if let Some(descriptor) = self.service_descriptors.get(service_id) {
            display_name = descriptor.display_name.clone();
            is_queryable = descriptor.is_queryable;
        }
        if !is_queryable {
            return false;
        }

        let Some(index) = LexIndex::open(&index_path).ok() else {
            return false;
        };
        let entry = LoadedIndexEntry {
            index,
            manifest,
            display_name,
            is_queryable: true,
        };
        self.loaded_indexes.insert(service_id.to_string(), entry);
        true
    }

    fn upsert_descriptor(
        &mut self,
        dictionary: &LocalDictionaryIndexDescriptor,
        is_queryable: bool,
    ) {
        let descriptor = ServiceDescriptor {
            display_name: dictionary.display_name.clone(),
            source_path: dictionary.file_path.clone(),
            is_queryable,
        };
        self.service_descriptors
            .insert(dictionary.service_id.clone(), descriptor);

        if let Some(existing) = self.loaded_indexes.get_mut(&dictionary.service_id) {
            existing.display_name = dictionary.display_name.clone();
            existing.is_queryable = is_queryable;
        }
    }
}

pub fn default_local_dictionary_index_root() -> PathBuf {
    // Keep the local dictionary index cache shared with the .NET app: the
    // on-disk LXDX/index manifest format is intentionally compatible, and this
    // cache does not wake or depend on a .NET runtime.
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir)
        .join("Easydict")
        .join("mdx_index")
}

pub fn escape_data_string(value: &str) -> String {
    let mut escaped = String::new();
    for byte in value.as_bytes() {
        match *byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                escaped.push(*byte as char);
            }
            other => {
                escaped.push('%');
                escaped.push(hex_digit(other >> 4));
                escaped.push(hex_digit(other & 0x0F));
            }
        }
    }
    escaped
}

fn load_manifest(path: &Path) -> Option<LocalDictionaryIndexManifest> {
    let json = fs::read_to_string(path).ok()?;
    serde_json::from_str(&json).ok()
}

fn replace_file(source: &Path, destination: &Path) -> std::io::Result<()> {
    match fs::remove_file(destination) {
        Ok(()) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(error),
    }
    fs::rename(source, destination)
}

fn temp_file_path(folder_path: &Path, file_name: &str) -> PathBuf {
    let counter = TEMP_FILE_COUNTER.fetch_add(1, Ordering::Relaxed);
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    folder_path.join(format!(
        "{file_name}.tmp.{}.{}.{}",
        std::process::id(),
        counter,
        nanos
    ))
}

fn hex_digit(value: u8) -> char {
    match value {
        0..=9 => (b'0' + value) as char,
        10..=15 => (b'A' + (value - 10)) as char,
        _ => unreachable!("hex digit nibble should be in range"),
    }
}

fn format_system_time_utc(value: SystemTime) -> String {
    let (seconds, nanos) = unix_seconds_and_nanos(value);
    let days = seconds.div_euclid(86_400);
    let seconds_of_day = seconds.rem_euclid(86_400);
    let (year, month, day) = civil_from_days(days);
    let hour = seconds_of_day / 3_600;
    let minute = (seconds_of_day % 3_600) / 60;
    let second = seconds_of_day % 60;

    let mut formatted = format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}");
    let ticks = nanos / 100;
    if ticks > 0 {
        let mut fraction = format!("{ticks:07}");
        while fraction.ends_with('0') {
            fraction.pop();
        }
        formatted.push('.');
        formatted.push_str(&fraction);
    }
    formatted.push('Z');
    formatted
}

fn unix_seconds_and_nanos(value: SystemTime) -> (i64, u32) {
    match value.duration_since(UNIX_EPOCH) {
        Ok(duration) => (duration.as_secs() as i64, duration.subsec_nanos()),
        Err(error) => {
            let duration = error.duration();
            if duration.subsec_nanos() == 0 {
                (-(duration.as_secs() as i64), 0)
            } else {
                (
                    -(duration.as_secs() as i64) - 1,
                    Duration::from_secs(1)
                        .checked_sub(Duration::from_nanos(duration.subsec_nanos() as u64))
                        .unwrap_or_default()
                        .subsec_nanos(),
                )
            }
        }
    }
}

fn civil_from_days(days_since_unix_epoch: i64) -> (i32, u32, u32) {
    let z = days_since_unix_epoch + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let day_of_era = z - era * 146_097;
    let year_of_era =
        (day_of_era - day_of_era / 1_460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let mut year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_piece = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_piece + 2) / 5 + 1;
    let month = month_piece + if month_piece < 10 { 3 } else { -9 };
    if month <= 2 {
        year += 1;
    }

    (year as i32, month as u32, day as u32)
}

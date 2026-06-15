use crate::app_data::default_user_data_directory;
use crate::translation_language::TranslationLanguage;
use ring::digest::{digest, SHA256};
use rusqlite::{params, Connection, OptionalExtension};
use std::collections::{HashMap, HashSet};
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

pub const TRANSLATION_CACHE_LIMIT_KB: usize = 8 * 1024;
pub const PHONETIC_CACHE_LIMIT_KB: usize = 512;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TranslationResultKind {
    Success,
    NoResult,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TranslationResult {
    pub translated_text: String,
    pub original_text: String,
    pub detected_language: TranslationLanguage,
    pub target_language: TranslationLanguage,
    pub service_name: String,
    pub result_kind: TranslationResultKind,
    pub info_message: Option<String>,
    pub timing_ms: i64,
    pub from_cache: bool,
    pub alternatives: Vec<String>,
    pub word_result: Option<WordResult>,
    pub raw_html: Option<String>,
}

impl TranslationResult {
    pub fn success(
        translated_text: impl Into<String>,
        original_text: impl Into<String>,
        target_language: TranslationLanguage,
        service_name: impl Into<String>,
    ) -> Self {
        Self {
            translated_text: translated_text.into(),
            original_text: original_text.into(),
            detected_language: TranslationLanguage::Auto,
            target_language,
            service_name: service_name.into(),
            result_kind: TranslationResultKind::Success,
            info_message: None,
            timing_ms: 0,
            from_cache: false,
            alternatives: Vec::new(),
            word_result: None,
            raw_html: None,
        }
    }

    pub fn with_from_cache(mut self, from_cache: bool) -> Self {
        self.from_cache = from_cache;
        self
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct WordResult {
    pub phonetics: Vec<Phonetic>,
    pub definitions: Vec<Definition>,
    pub examples: Vec<String>,
    pub word_forms: Vec<WordForm>,
    pub synonyms: Vec<Synonym>,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct Phonetic {
    pub text: Option<String>,
    pub audio_url: Option<String>,
    pub accent: Option<String>,
}

impl Phonetic {
    pub fn new(text: impl Into<String>, accent: impl Into<String>) -> Self {
        Self {
            text: Some(text.into()),
            audio_url: None,
            accent: Some(accent.into()),
        }
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct Definition {
    pub part_of_speech: Option<String>,
    pub meanings: Vec<String>,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct WordForm {
    pub name: Option<String>,
    pub value: Option<String>,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct Synonym {
    pub part_of_speech: Option<String>,
    pub meaning: Option<String>,
    pub words: Vec<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TranslationCacheRequest {
    pub service_id: String,
    pub from_language: TranslationLanguage,
    pub to_language: TranslationLanguage,
    pub text: String,
    pub bypass_cache: bool,
}

impl TranslationCacheRequest {
    pub fn new(
        service_id: impl Into<String>,
        from_language: TranslationLanguage,
        to_language: TranslationLanguage,
        text: impl Into<String>,
    ) -> Self {
        Self {
            service_id: service_id.into(),
            from_language,
            to_language,
            text: text.into(),
            bypass_cache: false,
        }
    }

    pub fn cache_key(&self) -> String {
        translation_cache_key(
            &self.service_id,
            self.from_language,
            self.to_language,
            &self.text,
        )
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct TranslationCacheEntry {
    result: TranslationResult,
    size_kb: usize,
    last_access: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TranslationMemoryCache {
    entries: HashMap<String, TranslationCacheEntry>,
    size_limit_kb: usize,
    total_size_kb: usize,
    tick: u64,
}

impl Default for TranslationMemoryCache {
    fn default() -> Self {
        Self::new()
    }
}

impl TranslationMemoryCache {
    pub fn new() -> Self {
        Self::with_size_limit_kb(TRANSLATION_CACHE_LIMIT_KB)
    }

    pub fn with_size_limit_kb(size_limit_kb: usize) -> Self {
        Self {
            entries: HashMap::new(),
            size_limit_kb,
            total_size_kb: 0,
            tick: 0,
        }
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn total_size_kb(&self) -> usize {
        self.total_size_kb
    }

    pub fn get(&mut self, request: &TranslationCacheRequest) -> Option<TranslationResult> {
        if request.bypass_cache {
            return None;
        }

        self.get_by_key(&request.cache_key())
    }

    pub fn get_by_key(&mut self, cache_key: &str) -> Option<TranslationResult> {
        self.tick = self.tick.saturating_add(1);
        let entry = self.entries.get_mut(cache_key)?;
        entry.last_access = self.tick;
        Some(entry.result.clone().with_from_cache(true))
    }

    pub fn insert(&mut self, request: &TranslationCacheRequest, result: TranslationResult) {
        if request.bypass_cache {
            return;
        }

        let cache_key = request.cache_key();
        let size_kb = translation_cache_entry_size_kb(&cache_key, &request.text, &result);
        self.insert_by_key(cache_key, size_kb, result);
    }

    pub fn insert_by_key(
        &mut self,
        cache_key: impl Into<String>,
        size_kb: usize,
        result: TranslationResult,
    ) {
        if self.size_limit_kb == 0 {
            return;
        }

        let cache_key = cache_key.into();
        if let Some(previous) = self.entries.remove(&cache_key) {
            self.total_size_kb = self.total_size_kb.saturating_sub(previous.size_kb);
        }

        self.tick = self.tick.saturating_add(1);
        let stored = result.with_from_cache(false);
        self.total_size_kb = self.total_size_kb.saturating_add(size_kb);
        self.entries.insert(
            cache_key,
            TranslationCacheEntry {
                result: stored,
                size_kb,
                last_access: self.tick,
            },
        );
        self.evict_over_limit();
    }

    pub fn clear(&mut self) {
        self.entries.clear();
        self.total_size_kb = 0;
    }

    fn evict_over_limit(&mut self) {
        while self.total_size_kb > self.size_limit_kb {
            let Some(oldest_key) = self
                .entries
                .iter()
                .min_by_key(|(_, entry)| entry.last_access)
                .map(|(key, _)| key.clone())
            else {
                self.total_size_kb = 0;
                return;
            };

            if let Some(removed) = self.entries.remove(&oldest_key) {
                self.total_size_kb = self.total_size_kb.saturating_sub(removed.size_kb);
            }
        }
    }
}

#[derive(Debug)]
pub enum PersistentTranslationCacheError {
    Io(std::io::Error),
    Sqlite(rusqlite::Error),
}

impl fmt::Display for PersistentTranslationCacheError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "{error}"),
            Self::Sqlite(error) => write!(formatter, "{error}"),
        }
    }
}

impl From<std::io::Error> for PersistentTranslationCacheError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<rusqlite::Error> for PersistentTranslationCacheError {
    fn from(error: rusqlite::Error) -> Self {
        Self::Sqlite(error)
    }
}

pub struct LongDocumentTranslationCache {
    connection: Connection,
}

impl LongDocumentTranslationCache {
    pub fn open(db_path: impl AsRef<Path>) -> Result<Self, PersistentTranslationCacheError> {
        let db_path = db_path.as_ref();
        if let Some(parent) = db_path
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
        {
            fs::create_dir_all(parent)?;
        }

        let connection = Connection::open(db_path)?;
        let cache = Self { connection };
        cache.initialize()?;
        Ok(cache)
    }

    pub fn try_get(
        &mut self,
        service_id: &str,
        from_language: &str,
        to_language: &str,
        source_hash: &str,
    ) -> Result<Option<String>, PersistentTranslationCacheError> {
        let translated_text = self
            .connection
            .query_row(
                "SELECT translated_text FROM translation_cache \
                 WHERE service_id = ?1 AND from_lang = ?2 AND to_lang = ?3 AND source_hash = ?4",
                params![service_id, from_language, to_language, source_hash],
                |row| row.get::<_, String>(0),
            )
            .optional()?;

        if translated_text.is_some() {
            self.connection.execute(
                "UPDATE translation_cache \
                 SET hit_count = hit_count + 1, last_used_utc = ?5 \
                 WHERE service_id = ?1 AND from_lang = ?2 AND to_lang = ?3 AND source_hash = ?4",
                params![
                    service_id,
                    from_language,
                    to_language,
                    source_hash,
                    persistent_cache_now(),
                ],
            )?;
        }

        Ok(translated_text)
    }

    pub fn set(
        &mut self,
        service_id: &str,
        from_language: &str,
        to_language: &str,
        source_hash: &str,
        source_text: &str,
        translated_text: &str,
    ) -> Result<(), PersistentTranslationCacheError> {
        let now = persistent_cache_now();
        self.connection.execute(
            "INSERT INTO translation_cache \
             (service_id, from_lang, to_lang, source_hash, source_text, translated_text, created_utc, last_used_utc, hit_count) \
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?7, 0) \
             ON CONFLICT(service_id, from_lang, to_lang, source_hash) \
             DO UPDATE SET translated_text = ?6, last_used_utc = ?7, hit_count = hit_count + 1",
            params![
                service_id,
                from_language,
                to_language,
                source_hash,
                source_text,
                translated_text,
                now,
            ],
        )?;
        Ok(())
    }

    pub fn entry_count(&self) -> Result<i64, PersistentTranslationCacheError> {
        Ok(self
            .connection
            .query_row("SELECT COUNT(*) FROM translation_cache", [], |row| {
                row.get::<_, i64>(0)
            })?)
    }

    pub fn clear(&mut self) -> Result<(), PersistentTranslationCacheError> {
        self.connection
            .execute("DELETE FROM translation_cache", [])?;
        Ok(())
    }

    fn initialize(&self) -> Result<(), PersistentTranslationCacheError> {
        self.connection.execute_batch(
            "CREATE TABLE IF NOT EXISTS translation_cache (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                service_id TEXT NOT NULL,
                from_lang TEXT NOT NULL,
                to_lang TEXT NOT NULL,
                source_hash TEXT NOT NULL,
                source_text TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                last_used_utc TEXT NOT NULL,
                hit_count INTEGER DEFAULT 0,
                UNIQUE(service_id, from_lang, to_lang, source_hash)
            );
            CREATE INDEX IF NOT EXISTS idx_cache_lookup
                ON translation_cache(service_id, from_lang, to_lang, source_hash);",
        )?;
        Ok(())
    }
}

pub fn long_document_translation_cache_path(cache_dir: Option<&str>) -> PathBuf {
    cache_dir
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .unwrap_or_else(default_translation_cache_directory)
        .join("translation_cache.db")
}

pub fn long_document_source_hash(text: &str) -> String {
    uppercase_sha256_hex(text.as_bytes())
}

fn default_translation_cache_directory() -> PathBuf {
    default_user_data_directory()
}

fn persistent_cache_now() -> String {
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default();
    format!("unix-ms:{millis}")
}

#[derive(Clone, Debug)]
struct PhoneticCacheEntry {
    phonetics: Vec<Phonetic>,
    size_kb: usize,
    last_access: u64,
}

#[derive(Clone, Debug)]
pub struct PhoneticMemoryCache {
    entries: HashMap<String, PhoneticCacheEntry>,
    size_limit_kb: usize,
    total_size_kb: usize,
    tick: u64,
}

impl Default for PhoneticMemoryCache {
    fn default() -> Self {
        Self::new()
    }
}

impl PhoneticMemoryCache {
    pub fn new() -> Self {
        Self::with_size_limit_kb(PHONETIC_CACHE_LIMIT_KB)
    }

    pub fn with_size_limit_kb(size_limit_kb: usize) -> Self {
        Self {
            entries: HashMap::new(),
            size_limit_kb,
            total_size_kb: 0,
            tick: 0,
        }
    }

    pub fn get(&mut self, english_word: &str) -> Option<Vec<Phonetic>> {
        self.get_by_key(&phonetic_cache_key(english_word))
    }

    pub fn get_by_key(&mut self, cache_key: &str) -> Option<Vec<Phonetic>> {
        self.tick = self.tick.saturating_add(1);
        let entry = self.entries.get_mut(cache_key)?;
        entry.last_access = self.tick;
        Some(entry.phonetics.clone())
    }

    pub fn insert(&mut self, english_word: &str, phonetics: Vec<Phonetic>) {
        let cache_key = phonetic_cache_key(english_word);
        let size_kb = phonetic_cache_entry_size_kb(&cache_key, &phonetics);
        self.insert_by_key(cache_key, size_kb, phonetics);
    }

    pub fn insert_by_key(
        &mut self,
        cache_key: impl Into<String>,
        size_kb: usize,
        phonetics: Vec<Phonetic>,
    ) {
        if self.size_limit_kb == 0 || phonetics.is_empty() {
            return;
        }

        let cache_key = cache_key.into();
        if let Some(previous) = self.entries.remove(&cache_key) {
            self.total_size_kb = self.total_size_kb.saturating_sub(previous.size_kb);
        }

        self.tick = self.tick.saturating_add(1);
        self.total_size_kb = self.total_size_kb.saturating_add(size_kb);
        self.entries.insert(
            cache_key,
            PhoneticCacheEntry {
                phonetics,
                size_kb,
                last_access: self.tick,
            },
        );
        self.evict_over_limit();
    }

    pub fn clear(&mut self) {
        self.entries.clear();
        self.total_size_kb = 0;
    }

    fn evict_over_limit(&mut self) {
        while self.total_size_kb > self.size_limit_kb {
            let Some(oldest_key) = self
                .entries
                .iter()
                .min_by_key(|(_, entry)| entry.last_access)
                .map(|(key, _)| key.clone())
            else {
                self.total_size_kb = 0;
                return;
            };

            if let Some(removed) = self.entries.remove(&oldest_key) {
                self.total_size_kb = self.total_size_kb.saturating_sub(removed.size_kb);
            }
        }
    }
}

#[derive(Clone, Debug, Default)]
pub struct PhoneticFlightTracker {
    in_flight: HashSet<String>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PhoneticFlightRegistration {
    Started,
    AlreadyInFlight,
}

impl PhoneticFlightTracker {
    pub fn begin(&mut self, english_word: &str) -> PhoneticFlightRegistration {
        self.begin_key(phonetic_cache_key(english_word))
    }

    pub fn begin_key(&mut self, cache_key: impl Into<String>) -> PhoneticFlightRegistration {
        if self.in_flight.insert(cache_key.into()) {
            PhoneticFlightRegistration::Started
        } else {
            PhoneticFlightRegistration::AlreadyInFlight
        }
    }

    pub fn complete(&mut self, english_word: &str) -> bool {
        self.complete_key(&phonetic_cache_key(english_word))
    }

    pub fn complete_key(&mut self, cache_key: &str) -> bool {
        self.in_flight.remove(cache_key)
    }

    pub fn clear(&mut self) {
        self.in_flight.clear();
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PhoneticEnrichmentDecision {
    Fetch {
        english_word: String,
        cache_key: String,
    },
    Skip(PhoneticEnrichmentSkipReason),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PhoneticEnrichmentSkipReason {
    TargetNotEnglish,
    EmptyTranslatedText,
    NotWordQuery,
    AlreadyHasTargetPhonetics,
}

pub fn translation_cache_key(
    service_id: &str,
    from_language: TranslationLanguage,
    to_language: TranslationLanguage,
    text: &str,
) -> String {
    let raw = format!(
        "{}|{}|{}|{}",
        service_id,
        dotnet_language_name(from_language),
        dotnet_language_name(to_language),
        text
    );
    uppercase_sha256_hex(raw.as_bytes())
}

pub fn phonetic_cache_key(english_word: &str) -> String {
    format!("phonetic:{}", english_word.trim().to_lowercase())
}

pub fn is_youdao_word_query(text: &str) -> bool {
    if text.trim().is_empty() {
        return false;
    }

    let trimmed = text.trim();
    if trimmed.encode_utf16().count() > 50 {
        return false;
    }

    if trimmed.contains('\n')
        || trimmed.contains('.')
        || trimmed.contains('!')
        || trimmed.contains('?')
        || trimmed.contains('\u{3002}')
        || trimmed.contains('\u{FF01}')
        || trimmed.contains('\u{FF1F}')
    {
        return false;
    }

    let trimmed_len = trimmed.chars().count();
    let cjk_count = trimmed.chars().filter(|c| is_cjk_character(*c)).count();
    if cjk_count > 0 {
        return cjk_count <= 3 && cjk_count == trimmed_len;
    }

    let word_chars = trimmed
        .chars()
        .filter(|c| c.is_alphabetic() || *c == '-' || *c == '\'' || *c == ' ')
        .count();
    word_chars * 10 >= trimmed_len * 8
}

pub fn phonetic_accent_display_label(accent: Option<&str>) -> Option<&str> {
    match accent {
        Some("US") => Some("\u{7F8E}"),
        Some("UK") => Some("\u{82F1}"),
        Some("src") => Some("\u{539F}"),
        Some("dest") => Some("\u{8BD1}"),
        Some("") | None => None,
        Some(other) => Some(other),
    }
}

pub fn format_phonetic_text(text: &str) -> String {
    if text.starts_with('/') && text.ends_with('/') {
        return text.to_string();
    }

    format!("/{text}/")
}

pub fn displayable_phonetics(result: &TranslationResult) -> Vec<Phonetic> {
    result
        .word_result
        .as_ref()
        .map(|word| {
            word.phonetics
                .iter()
                .filter(|phonetic| has_text(phonetic.text.as_deref()))
                .cloned()
                .collect()
        })
        .unwrap_or_default()
}

pub fn target_phonetics(result: &TranslationResult) -> Vec<Phonetic> {
    result
        .word_result
        .as_ref()
        .map(|word| {
            word.phonetics
                .iter()
                .filter(|phonetic| {
                    has_text(phonetic.text.as_deref())
                        && matches!(phonetic.accent.as_deref(), Some("dest" | "US" | "UK"))
                })
                .cloned()
                .collect()
        })
        .unwrap_or_default()
}

pub fn plan_phonetic_enrichment(
    result: &TranslationResult,
    request_to_language: TranslationLanguage,
) -> PhoneticEnrichmentDecision {
    if request_to_language != TranslationLanguage::English {
        return PhoneticEnrichmentDecision::Skip(PhoneticEnrichmentSkipReason::TargetNotEnglish);
    }

    let translated_text = result.translated_text.trim();
    if translated_text.is_empty() {
        return PhoneticEnrichmentDecision::Skip(PhoneticEnrichmentSkipReason::EmptyTranslatedText);
    }

    if !is_youdao_word_query(translated_text) {
        return PhoneticEnrichmentDecision::Skip(PhoneticEnrichmentSkipReason::NotWordQuery);
    }

    if !target_phonetics(result).is_empty() {
        return PhoneticEnrichmentDecision::Skip(
            PhoneticEnrichmentSkipReason::AlreadyHasTargetPhonetics,
        );
    }

    PhoneticEnrichmentDecision::Fetch {
        english_word: translated_text.to_string(),
        cache_key: phonetic_cache_key(translated_text),
    }
}

pub fn merge_phonetics_into_result(
    mut result: TranslationResult,
    phonetics_to_add: &[Phonetic],
) -> TranslationResult {
    let mut word_result = result.word_result.take().unwrap_or_default();
    word_result
        .phonetics
        .extend(phonetics_to_add.iter().cloned());
    result.word_result = Some(word_result);
    result
}

pub fn translation_cache_entry_size_kb(
    cache_key: &str,
    request_text: &str,
    result: &TranslationResult,
) -> usize {
    let mut bytes = estimate_utf16_bytes(Some(cache_key))
        + estimate_utf16_bytes(Some(request_text))
        + estimate_utf16_bytes(Some(&result.original_text))
        + estimate_utf16_bytes(Some(&result.translated_text))
        + estimate_utf16_bytes(Some(&result.service_name))
        + estimate_utf16_bytes(result.info_message.as_deref())
        + estimate_utf16_bytes(result.raw_html.as_deref())
        + estimate_strings_utf16_bytes(&result.alternatives);

    if let Some(word_result) = &result.word_result {
        bytes += estimate_phonetics_utf16_bytes(&word_result.phonetics)
            + estimate_definitions_utf16_bytes(&word_result.definitions)
            + estimate_strings_utf16_bytes(&word_result.examples)
            + estimate_word_forms_utf16_bytes(&word_result.word_forms)
            + estimate_synonyms_utf16_bytes(&word_result.synonyms);
    }

    to_cache_kilobytes(bytes)
}

pub fn phonetic_cache_entry_size_kb(cache_key: &str, phonetics: &[Phonetic]) -> usize {
    to_cache_kilobytes(
        estimate_utf16_bytes(Some(cache_key)) + estimate_phonetics_utf16_bytes(phonetics),
    )
}

fn uppercase_sha256_hex(bytes: &[u8]) -> String {
    let digest = digest(&SHA256, bytes);
    let mut encoded = String::with_capacity(digest.as_ref().len() * 2);
    for byte in digest.as_ref() {
        encoded.push_str(&format!("{byte:02X}"));
    }
    encoded
}

fn dotnet_language_name(language: TranslationLanguage) -> &'static str {
    match language {
        TranslationLanguage::Auto => "Auto",
        TranslationLanguage::SimplifiedChinese => "SimplifiedChinese",
        TranslationLanguage::TraditionalChinese => "TraditionalChinese",
        TranslationLanguage::Japanese => "Japanese",
        TranslationLanguage::Korean => "Korean",
        TranslationLanguage::English => "English",
        TranslationLanguage::German => "German",
        TranslationLanguage::Dutch => "Dutch",
        TranslationLanguage::Swedish => "Swedish",
        TranslationLanguage::Norwegian => "Norwegian",
        TranslationLanguage::Danish => "Danish",
        TranslationLanguage::French => "French",
        TranslationLanguage::Spanish => "Spanish",
        TranslationLanguage::Portuguese => "Portuguese",
        TranslationLanguage::Italian => "Italian",
        TranslationLanguage::Romanian => "Romanian",
        TranslationLanguage::Russian => "Russian",
        TranslationLanguage::Polish => "Polish",
        TranslationLanguage::Czech => "Czech",
        TranslationLanguage::Ukrainian => "Ukrainian",
        TranslationLanguage::Bulgarian => "Bulgarian",
        TranslationLanguage::Slovak => "Slovak",
        TranslationLanguage::Slovenian => "Slovenian",
        TranslationLanguage::Estonian => "Estonian",
        TranslationLanguage::Latvian => "Latvian",
        TranslationLanguage::Lithuanian => "Lithuanian",
        TranslationLanguage::Greek => "Greek",
        TranslationLanguage::Hungarian => "Hungarian",
        TranslationLanguage::Finnish => "Finnish",
        TranslationLanguage::Turkish => "Turkish",
        TranslationLanguage::Arabic => "Arabic",
        TranslationLanguage::Persian => "Persian",
        TranslationLanguage::Hebrew => "Hebrew",
        TranslationLanguage::Hindi => "Hindi",
        TranslationLanguage::Bengali => "Bengali",
        TranslationLanguage::Tamil => "Tamil",
        TranslationLanguage::Telugu => "Telugu",
        TranslationLanguage::Urdu => "Urdu",
        TranslationLanguage::Vietnamese => "Vietnamese",
        TranslationLanguage::Thai => "Thai",
        TranslationLanguage::Indonesian => "Indonesian",
        TranslationLanguage::Malay => "Malay",
        TranslationLanguage::Filipino => "Filipino",
        TranslationLanguage::ClassicalChinese => "ClassicalChinese",
    }
}

fn is_cjk_character(c: char) -> bool {
    ('\u{4E00}'..='\u{9FFF}').contains(&c)
        || ('\u{3400}'..='\u{4DBF}').contains(&c)
        || ('\u{3040}'..='\u{309F}').contains(&c)
        || ('\u{30A0}'..='\u{30FF}').contains(&c)
        || ('\u{AC00}'..='\u{D7AF}').contains(&c)
}

fn has_text(value: Option<&str>) -> bool {
    value.is_some_and(|text| !text.is_empty())
}

fn estimate_utf16_bytes(value: Option<&str>) -> usize {
    value.map_or(0, |text| text.encode_utf16().count() * 2)
}

fn estimate_strings_utf16_bytes(values: &[String]) -> usize {
    values
        .iter()
        .map(|value| estimate_utf16_bytes(Some(value)))
        .sum()
}

fn estimate_phonetics_utf16_bytes(values: &[Phonetic]) -> usize {
    values
        .iter()
        .map(|value| {
            estimate_utf16_bytes(value.text.as_deref())
                + estimate_utf16_bytes(value.audio_url.as_deref())
                + estimate_utf16_bytes(value.accent.as_deref())
        })
        .sum()
}

fn estimate_definitions_utf16_bytes(values: &[Definition]) -> usize {
    values
        .iter()
        .map(|value| {
            estimate_utf16_bytes(value.part_of_speech.as_deref())
                + estimate_strings_utf16_bytes(&value.meanings)
        })
        .sum()
}

fn estimate_word_forms_utf16_bytes(values: &[WordForm]) -> usize {
    values
        .iter()
        .map(|value| {
            estimate_utf16_bytes(value.name.as_deref())
                + estimate_utf16_bytes(value.value.as_deref())
        })
        .sum()
}

fn estimate_synonyms_utf16_bytes(values: &[Synonym]) -> usize {
    values
        .iter()
        .map(|value| {
            estimate_utf16_bytes(value.part_of_speech.as_deref())
                + estimate_utf16_bytes(value.meaning.as_deref())
                + estimate_strings_utf16_bytes(&value.words)
        })
        .sum()
}

fn to_cache_kilobytes(bytes: usize) -> usize {
    ((bytes + 1023) / 1024).max(1)
}

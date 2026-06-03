use std::collections::BTreeMap;
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

const TITLE_MAX_LENGTH: usize = 256;
const SHORT_DESCRIPTION_MAX_LENGTH: usize = 100;
const DESCRIPTION_MAX_LENGTH: usize = 10_000;
const FEATURE_MAX_LENGTH: usize = 200;
const FEATURE_MAX_COUNT: usize = 20;
const KEYWORD_MAX_LENGTH: usize = 40;
const KEYWORD_MAX_COUNT: usize = 7;
const RELEASE_NOTES_MAX_LENGTH: usize = 1_500;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StoreListingOptions {
    pub winstore_path: PathBuf,
    pub languages: Vec<String>,
}

impl StoreListingOptions {
    pub fn new(winstore_path: PathBuf, languages: Vec<String>) -> Self {
        Self {
            winstore_path,
            languages,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StoreListingReport {
    pub app_id: String,
    pub primary_language: String,
    pub target_languages: Vec<String>,
    pub entries: Vec<StoreListingEntry>,
    pub errors: Vec<String>,
    pub warnings: Vec<String>,
}

impl StoreListingReport {
    pub fn processed_count(&self) -> usize {
        self.entries
            .iter()
            .filter(|entry| matches!(entry, StoreListingEntry::Found { .. }))
            .count()
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum StoreListingEntry {
    Found {
        language: String,
        path: PathBuf,
        listing: StoreListing,
        validation: ListingValidation,
    },
    Missing {
        language: String,
        path: PathBuf,
    },
}

impl StoreListingEntry {
    pub fn language(&self) -> &str {
        match self {
            Self::Found { language, .. } | Self::Missing { language, .. } => language,
        }
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ListingValidation {
    pub errors: Vec<String>,
    pub warnings: Vec<String>,
}

#[derive(Clone, Debug, Default, Deserialize, Eq, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct StoreListing {
    #[serde(default)]
    pub language: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub short_title: String,
    #[serde(default)]
    pub sort_title: String,
    #[serde(default)]
    pub voice_title: String,
    #[serde(default)]
    pub short_description: String,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub features: Vec<String>,
    #[serde(default)]
    pub keywords: Vec<String>,
    #[serde(default)]
    pub release_notes: String,
    #[serde(default)]
    pub copyright_and_trademark_info: String,
    #[serde(default)]
    pub additional_license_terms: String,
    #[serde(default)]
    pub developed_by: String,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StoreListingRenderMode {
    Validate,
    Preview,
}

impl StoreListingRenderMode {
    fn as_str(self) -> &'static str {
        match self {
            Self::Validate => "validate",
            Self::Preview => "preview",
        }
    }
}

#[derive(Debug, Eq, PartialEq)]
pub enum StoreListingError {
    ConfigNotFound(PathBuf),
    Io { path: PathBuf, message: String },
    ConfigJson { path: PathBuf, message: String },
    ListingYaml { path: PathBuf, message: String },
    MissingAppId(PathBuf),
    MissingConfiguredLanguages(PathBuf),
    PayloadJson(String),
}

impl fmt::Display for StoreListingError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::ConfigNotFound(path) => {
                write!(formatter, "store configuration not found: {}", path.display())
            }
            Self::Io { path, message } => write!(formatter, "{}: {message}", path.display()),
            Self::ConfigJson { path, message } => {
                write!(formatter, "failed to parse {}: {message}", path.display())
            }
            Self::ListingYaml { path, message } => {
                write!(formatter, "failed to parse {}: {message}", path.display())
            }
            Self::MissingAppId(path) => {
                write!(formatter, "store configuration is missing app.id: {}", path.display())
            }
            Self::MissingConfiguredLanguages(path) => write!(
                formatter,
                "store configuration is missing listing.languages: {}",
                path.display()
            ),
            Self::PayloadJson(message) => {
                write!(formatter, "failed to create msstore payload: {message}")
            }
        }
    }
}

impl std::error::Error for StoreListingError {}

pub fn parse_language_filter(value: &str) -> Vec<String> {
    value
        .split(',')
        .map(str::trim)
        .filter(|language| !language.is_empty())
        .map(ToOwned::to_owned)
        .collect()
}

pub fn load_store_listing_report(
    options: &StoreListingOptions,
) -> Result<StoreListingReport, StoreListingError> {
    let config_path = options.winstore_path.join("store-config.json");
    if !config_path.exists() {
        return Err(StoreListingError::ConfigNotFound(config_path));
    }

    let config_text = read_text(&config_path)?;
    let config = serde_json::from_str::<StoreConfig>(&config_text).map_err(|error| {
        StoreListingError::ConfigJson {
            path: config_path.clone(),
            message: error.to_string(),
        }
    })?;

    if config.app.id.trim().is_empty() {
        return Err(StoreListingError::MissingAppId(config_path));
    }

    let target_languages = if options.languages.is_empty() {
        if config.listing.languages.is_empty() {
            return Err(StoreListingError::MissingConfiguredLanguages(config_path));
        }
        config.listing.languages.clone()
    } else {
        options.languages.clone()
    };

    let listing_base = if config.listing.base_path.trim().is_empty() {
        "listings"
    } else {
        config.listing.base_path.trim()
    };
    let listings_path = options.winstore_path.join(listing_base);
    let mut entries = Vec::new();
    let mut all_errors = Vec::new();
    let mut all_warnings = Vec::new();

    for language in &target_languages {
        let listing_path = listings_path.join(format!("{language}.yaml"));
        if !listing_path.exists() {
            let warning = format!("[{language}] Listing file not found");
            all_warnings.push(warning);
            entries.push(StoreListingEntry::Missing {
                language: language.clone(),
                path: listing_path,
            });
            continue;
        }

        let listing_text = read_text(&listing_path)?;
        let listing =
            serde_norway::from_str::<StoreListing>(&listing_text).map_err(|error| {
                StoreListingError::ListingYaml {
                    path: listing_path.clone(),
                    message: error.to_string(),
                }
            })?;
        let validation = validate_listing(&listing, language);
        all_errors.extend(validation.errors.iter().cloned());
        all_warnings.extend(validation.warnings.iter().cloned());
        entries.push(StoreListingEntry::Found {
            language: language.clone(),
            path: listing_path,
            listing,
            validation,
        });
    }

    Ok(StoreListingReport {
        app_id: config.app.id,
        primary_language: config.listing.primary_language,
        target_languages,
        entries,
        errors: all_errors,
        warnings: all_warnings,
    })
}

pub fn validate_listing(listing: &StoreListing, language: &str) -> ListingValidation {
    let mut errors = Vec::new();
    let mut warnings = Vec::new();

    if listing.title.trim().is_empty() {
        errors.push(format!("[{language}] Missing required field: title"));
    }
    if listing.description.trim().is_empty() {
        errors.push(format!(
            "[{language}] Missing required field: description"
        ));
    }
    if listing.short_description.trim().is_empty() {
        warnings.push(format!(
            "[{language}] Missing recommended field: shortDescription"
        ));
    }

    if text_len(&listing.title) > TITLE_MAX_LENGTH {
        errors.push(format!(
            "[{language}] Title exceeds max length ({}/{TITLE_MAX_LENGTH})",
            text_len(&listing.title)
        ));
    }
    if !listing.short_description.is_empty()
        && text_len(&listing.short_description) > SHORT_DESCRIPTION_MAX_LENGTH
    {
        errors.push(format!(
            "[{language}] Short description exceeds max length ({}/{SHORT_DESCRIPTION_MAX_LENGTH})",
            text_len(&listing.short_description)
        ));
    }
    if text_len(&listing.description) > DESCRIPTION_MAX_LENGTH {
        errors.push(format!(
            "[{language}] Description exceeds max length ({}/{DESCRIPTION_MAX_LENGTH})",
            text_len(&listing.description)
        ));
    }

    if listing.features.len() > FEATURE_MAX_COUNT {
        errors.push(format!(
            "[{language}] Too many features ({}/{FEATURE_MAX_COUNT})",
            listing.features.len()
        ));
    }
    for feature in &listing.features {
        if text_len(feature) > FEATURE_MAX_LENGTH {
            errors.push(format!(
                "[{language}] Feature exceeds max length ({}/{FEATURE_MAX_LENGTH}): {}...",
                text_len(feature),
                preview_chars(feature, 50)
            ));
        }
    }

    if listing.keywords.len() > KEYWORD_MAX_COUNT {
        warnings.push(format!(
            "[{language}] Too many keywords ({}/{KEYWORD_MAX_COUNT}), only first {KEYWORD_MAX_COUNT} will be used",
            listing.keywords.len()
        ));
    }
    for keyword in &listing.keywords {
        if text_len(keyword) > KEYWORD_MAX_LENGTH {
            errors.push(format!(
                "[{language}] Keyword exceeds max length ({}/{KEYWORD_MAX_LENGTH}): {keyword}",
                text_len(keyword)
            ));
        }
    }

    if !listing.release_notes.is_empty()
        && text_len(&listing.release_notes) > RELEASE_NOTES_MAX_LENGTH
    {
        errors.push(format!(
            "[{language}] Release notes exceed max length ({}/{RELEASE_NOTES_MAX_LENGTH})",
            text_len(&listing.release_notes)
        ));
    }

    ListingValidation { errors, warnings }
}

pub fn render_report(report: &StoreListingReport, mode: StoreListingRenderMode) -> String {
    let mut output = String::new();
    push_header(&mut output, report, mode.as_str());

    for entry in &report.entries {
        match entry {
            StoreListingEntry::Missing { language, path } => {
                output.push_str(&format!(
                    "WARNING: Listing file not found for language: {language} (expected: {})\n\n",
                    path.display()
                ));
            }
            StoreListingEntry::Found {
                language,
                listing,
                validation,
                ..
            } => {
                output.push_str(&format!("--- Processing: {language} ---\n"));
                push_validation_messages(&mut output, validation);
                match mode {
                    StoreListingRenderMode::Validate => {
                        if validation.errors.is_empty() {
                            output.push_str("  OK: Listing is valid\n");
                            output.push_str(&format!("    Title: {}\n", listing.title));
                            output.push_str(&format!(
                                "    Short: {}\n",
                                listing.short_description
                            ));
                            output.push_str(&format!(
                                "    Description: {} chars\n",
                                text_len(&listing.description)
                            ));
                            output.push_str(&format!("    Features: {}\n", listing.features.len()));
                            output.push_str(&format!("    Keywords: {}\n", listing.keywords.len()));
                        }
                    }
                    StoreListingRenderMode::Preview => {
                        output.push_str(&format!("  Title: {}\n", listing.title));
                        output.push_str(&format!(
                            "  Short Description: {}\n",
                            listing.short_description
                        ));
                        output.push_str(&format!(
                            "  Description ({} chars):\n",
                            text_len(&listing.description)
                        ));
                        output.push_str(&format!(
                            "    {}...\n",
                            preview_chars(&listing.description, 200)
                        ));
                        output.push_str(&format!("  Features ({}):\n", listing.features.len()));
                        for feature in &listing.features {
                            output.push_str(&format!("    - {feature}\n"));
                        }
                        output.push_str(&format!(
                            "  Keywords: {}\n",
                            listing.keywords.join(", ")
                        ));
                    }
                }
                output.push('\n');
            }
        }
    }

    push_summary(&mut output, report);
    output
}

pub fn render_github_summary(
    report: &StoreListingReport,
    action: &str,
    language_filter: Option<&str>,
) -> String {
    let mut output = String::new();
    output.push_str(&format!("## Store Listings - {action}\n\n"));
    output.push_str("| Language | File | Status |\n");
    output.push_str("|----------|------|--------|\n");

    for entry in &report.entries {
        match entry {
            StoreListingEntry::Found {
                language, listing, ..
            } => {
                output.push_str(&format!(
                    "| {language} | `{language}.yaml` | {} |\n",
                    markdown_cell(&listing.title)
                ));
            }
            StoreListingEntry::Missing { language, .. } => {
                output.push_str(&format!("| {language} | `{language}.yaml` | Missing |\n"));
            }
        }
    }

    output.push_str("\n### Configuration\n");
    output.push_str(&format!("- **App ID**: {}\n", report.app_id));
    output.push_str(&format!(
        "- **Primary Language**: {}\n",
        report.primary_language
    ));
    output.push_str(&format!("- **Action**: {action}\n"));
    output.push_str(&format!(
        "- **Languages Filter**: {}\n",
        language_filter
            .filter(|value| !value.trim().is_empty())
            .unwrap_or("all")
    ));
    output
}

pub fn build_msstore_payload(
    language: &str,
    listing: &StoreListing,
) -> Result<String, StoreListingError> {
    let mut base_listing = serde_json::Map::new();
    insert_non_empty(&mut base_listing, "title", &listing.title);
    insert_non_empty(&mut base_listing, "shortTitle", &listing.short_title);
    insert_non_empty(&mut base_listing, "description", &listing.description);
    insert_non_empty(
        &mut base_listing,
        "shortDescription",
        &listing.short_description,
    );
    base_listing.insert("features".to_string(), json!(listing.features));
    base_listing.insert(
        "keywords".to_string(),
        json!(
            listing
                .keywords
                .iter()
                .take(KEYWORD_MAX_COUNT)
                .cloned()
                .collect::<Vec<_>>()
        ),
    );
    insert_non_empty(
        &mut base_listing,
        "copyrightAndTrademarkInfo",
        &listing.copyright_and_trademark_info,
    );
    insert_non_empty(&mut base_listing, "developedBy", &listing.developed_by);
    insert_non_empty(&mut base_listing, "releaseNotes", &listing.release_notes);

    let mut language_listing = serde_json::Map::new();
    language_listing.insert("baseListing".to_string(), Value::Object(base_listing));

    let mut listings = BTreeMap::new();
    listings.insert(language.to_string(), Value::Object(language_listing));

    serde_json::to_string_pretty(&json!({ "listings": listings }))
        .map_err(|error| StoreListingError::PayloadJson(error.to_string()))
}

fn read_text(path: &Path) -> Result<String, StoreListingError> {
    fs::read_to_string(path).map_err(|error| StoreListingError::Io {
        path: path.to_path_buf(),
        message: error.to_string(),
    })
}

fn push_header(output: &mut String, report: &StoreListingReport, mode: &str) {
    output.push_str("=== Easydict Store Listing Sync ===\n");
    output.push_str(&format!("App ID: {}\n", report.app_id));
    output.push_str(&format!("Mode: {mode}\n\n"));
    output.push_str(&format!(
        "Languages: {}\n\n",
        report.target_languages.join(", ")
    ));
}

fn push_validation_messages(output: &mut String, validation: &ListingValidation) {
    if !validation.errors.is_empty() {
        output.push_str("  ERRORS:\n");
        for error in &validation.errors {
            output.push_str(&format!("    - {error}\n"));
        }
    }
    if !validation.warnings.is_empty() {
        output.push_str("  WARNINGS:\n");
        for warning in &validation.warnings {
            output.push_str(&format!("    - {warning}\n"));
        }
    }
}

fn push_summary(output: &mut String, report: &StoreListingReport) {
    output.push_str("=== Summary ===\n");
    output.push_str(&format!("Processed: {} language(s)\n", report.processed_count()));
    output.push_str(&format!("Errors: {}\n", report.errors.len()));
    output.push_str(&format!("Warnings: {}\n", report.warnings.len()));

    if !report.errors.is_empty() {
        output.push_str("\nAll Errors:\n");
        for error in &report.errors {
            output.push_str(&format!("  - {error}\n"));
        }
    }

    if !report.warnings.is_empty() {
        output.push_str("\nAll Warnings:\n");
        for warning in &report.warnings {
            output.push_str(&format!("  - {warning}\n"));
        }
    }

    output.push_str("\nDone!\n");
}

fn insert_non_empty(map: &mut serde_json::Map<String, Value>, key: &str, value: &str) {
    if !value.is_empty() {
        map.insert(key.to_string(), json!(value));
    }
}

fn preview_chars(value: &str, limit: usize) -> String {
    value.chars().take(limit).collect()
}

fn text_len(value: &str) -> usize {
    value.chars().count()
}

fn markdown_cell(value: &str) -> String {
    value.replace('|', "\\|").replace('\n', " ")
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq)]
struct StoreConfig {
    app: StoreAppConfig,
    #[serde(default)]
    listing: StoreListingConfig,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq)]
struct StoreAppConfig {
    #[serde(default)]
    id: String,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq)]
#[serde(rename_all = "camelCase")]
struct StoreListingConfig {
    #[serde(default = "default_listing_base_path")]
    base_path: String,
    #[serde(default)]
    languages: Vec<String>,
    #[serde(default)]
    primary_language: String,
}

impl Default for StoreListingConfig {
    fn default() -> Self {
        Self {
            base_path: default_listing_base_path(),
            languages: Vec::new(),
            primary_language: String::new(),
        }
    }
}

fn default_listing_base_path() -> String {
    "listings".to_string()
}

#[derive(Serialize)]
struct _SerdeCompileGuard;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validates_listings_and_reports_missing_files_as_warnings() {
        let temp = temp_winstore("validate");
        write_config(&temp, &["en-us", "zh-cn"]);
        write_file(
            &temp.join("listings").join("en-us.yaml"),
            r#"
title: Easydict
description: Good description
features:
- Feature one
keywords:
- one
- two
- three
- four
- five
- six
- seven
- eight
"#,
        );

        let report = load_store_listing_report(&StoreListingOptions::new(temp.clone(), Vec::new()))
            .expect("load report");

        assert_eq!(report.processed_count(), 1);
        assert!(report.errors.is_empty());
        assert!(report
            .warnings
            .contains(&"[en-us] Missing recommended field: shortDescription".to_string()));
        assert!(report
            .warnings
            .contains(&"[en-us] Too many keywords (8/7), only first 7 will be used".to_string()));
        assert!(report
            .warnings
            .contains(&"[zh-cn] Listing file not found".to_string()));
        let _ = fs::remove_dir_all(temp);
    }

    #[test]
    fn build_payload_truncates_keywords_and_keeps_release_notes() {
        let listing = StoreListing {
            title: "Easydict".to_string(),
            short_title: "Easy".to_string(),
            short_description: "Translate quickly".to_string(),
            description: "Long description".to_string(),
            features: vec!["Fast".to_string()],
            keywords: vec![
                "one".to_string(),
                "two".to_string(),
                "three".to_string(),
                "four".to_string(),
                "five".to_string(),
                "six".to_string(),
                "seven".to_string(),
                "eight".to_string(),
            ],
            release_notes: "New release".to_string(),
            copyright_and_trademark_info: "Copyright".to_string(),
            developed_by: "xiaocang".to_string(),
            ..StoreListing::default()
        };

        let json = build_msstore_payload("en-us", &listing).expect("payload");
        let value = serde_json::from_str::<Value>(&json).expect("payload json");

        assert_eq!(value["listings"]["en-us"]["baseListing"]["title"], "Easydict");
        assert_eq!(
            value["listings"]["en-us"]["baseListing"]["keywords"]
                .as_array()
                .expect("keywords")
                .len(),
            7
        );
        assert_eq!(
            value["listings"]["en-us"]["baseListing"]["releaseNotes"],
            "New release"
        );
    }

    #[test]
    fn summary_renders_github_markdown_without_yaml_modules() {
        let temp = temp_winstore("summary");
        write_config(&temp, &["en-us"]);
        write_file(
            &temp.join("listings").join("en-us.yaml"),
            r#"
title: Easydict for Windows
shortDescription: Translate quickly
description: Good description
"#,
        );
        let report = load_store_listing_report(&StoreListingOptions::new(temp.clone(), Vec::new()))
            .expect("load report");

        let summary = render_github_summary(&report, "preview", Some("en-us"));

        assert!(summary.contains("## Store Listings - preview"));
        assert!(summary.contains("| en-us | `en-us.yaml` | Easydict for Windows |"));
        assert!(summary.contains("- **Languages Filter**: en-us"));
        let _ = fs::remove_dir_all(temp);
    }

    fn temp_winstore(label: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "easydict-store-listings-{label}-{}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(path.join("listings")).expect("create listings");
        path
    }

    fn write_config(root: &Path, languages: &[&str]) {
        let language_json = languages
            .iter()
            .map(|language| format!("\"{language}\""))
            .collect::<Vec<_>>()
            .join(", ");
        write_file(
            &root.join("store-config.json"),
            &format!(
                r#"{{
  "app": {{ "id": "9p7nqvxf9dzj" }},
  "listing": {{
    "basePath": "listings",
    "languages": [{language_json}],
    "primaryLanguage": "en-us"
  }}
}}"#
            ),
        );
    }

    fn write_file(path: &Path, text: &str) {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).expect("create parent");
        }
        fs::write(path, text.trim_start()).expect("write file");
    }
}

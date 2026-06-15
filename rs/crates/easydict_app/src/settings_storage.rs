use crate::app_data::legacy_user_data_directory;
use crate::credential_protection::{
    get_or_create_persisted_machine_id_with_legacy_fallback, protect_credential,
    unprotect_or_return_plaintext_with_machine_id,
};
use crate::mdx_native::discover_mdd_file_paths;
use crate::protocol::normalize_local_ai_provider_mode;
use crate::settings_migration::{
    default_rust_settings_path, migrate_settings_json, SettingsMigrationError,
};
use crate::state::{
    HotkeySetting, ImportedMdxDictionary, ServiceProviderSetting, SettingsState,
    WindowServiceSetting,
};
use serde_json::{Map, Value};
use std::collections::HashSet;
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use win_fluent::prelude::ThemeMode;

const SENSITIVE_SETTING_KEYS: &[&str] = &[
    "DeepLApiKey",
    "OpenAIApiKey",
    "OcrApiKey",
    "CaiyunApiKey",
    "NiuTransApiKey",
    "YoudaoAppKey",
    "YoudaoAppSecret",
    "VolcanoAccessKeyId",
    "VolcanoSecretAccessKey",
    "DeepSeekApiKey",
    "GroqApiKey",
    "ZhipuApiKey",
    "GitHubModelsToken",
    "GeminiApiKey",
    "CustomOpenAIApiKey",
    "BuiltInAIApiKey",
    "DoubaoApiKey",
];

#[derive(Clone, Debug, PartialEq)]
pub struct SettingsLoadResult {
    pub settings: SettingsState,
    pub warnings: Vec<String>,
}

#[derive(Debug)]
pub enum SettingsStorageError {
    Credential(String),
    Io(std::io::Error),
    Migration(SettingsMigrationError),
    Serialize(serde_json::Error),
}

impl fmt::Display for SettingsStorageError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Credential(message) => {
                write!(formatter, "Credential protection failed: {message}")
            }
            Self::Io(error) => write!(formatter, "Settings storage I/O failed: {error}"),
            Self::Migration(error) => write!(formatter, "Settings migration failed: {error}"),
            Self::Serialize(error) => write!(formatter, "Settings serialization failed: {error}"),
        }
    }
}

impl From<std::io::Error> for SettingsStorageError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<serde_json::Error> for SettingsStorageError {
    fn from(error: serde_json::Error) -> Self {
        Self::Serialize(error)
    }
}

impl From<SettingsMigrationError> for SettingsStorageError {
    fn from(error: SettingsMigrationError) -> Self {
        Self::Migration(error)
    }
}

pub fn default_settings_storage_path() -> PathBuf {
    default_rust_settings_path()
}

pub fn load_settings_file(
    path: impl AsRef<Path>,
) -> Result<SettingsLoadResult, SettingsStorageError> {
    let path = path.as_ref();
    let json = fs::read_to_string(path)?;
    let (migrated_json, changed) = migrate_settings_json(&json)?;
    let machine_id = default_storage_machine_id();
    let mut root = serde_json::from_str::<Value>(&migrated_json)?
        .as_object()
        .cloned()
        .unwrap_or_default();
    let sensitive_changed = normalize_sensitive_settings(&mut root, &machine_id)?;
    let local_ai_provider_changed = normalize_local_ai_provider_storage(&mut root);

    let normalized_json = if changed || sensitive_changed || local_ai_provider_changed {
        let normalized_json = serde_json::to_string_pretty(&Value::Object(root))?;
        fs::write(path, &normalized_json)?;
        normalized_json
    } else {
        migrated_json
    };

    load_settings_json_with_machine_id(&normalized_json, &machine_id)
}

fn normalize_sensitive_settings(
    root: &mut Map<String, Value>,
    machine_id: &str,
) -> Result<bool, SettingsStorageError> {
    let mut changed = false;
    for key in SENSITIVE_SETTING_KEYS {
        let Some(stored) = string_value(root, key).filter(|value| !value.is_empty()) else {
            continue;
        };
        let plaintext = unprotect_or_return_plaintext_with_machine_id(Some(&stored), machine_id);
        if !plaintext.needs_migration {
            continue;
        }

        let protected = protect_credential(plaintext.value.as_deref().unwrap_or_default())
            .map_err(|error| SettingsStorageError::Credential(error.to_string()))?;
        if protected != stored {
            root.insert((*key).to_string(), Value::String(protected));
            changed = true;
        }
    }

    Ok(changed)
}

fn normalize_local_ai_provider_storage(root: &mut Map<String, Value>) -> bool {
    let Some(stored) = string_value(root, "LocalAIProvider") else {
        return false;
    };
    let normalized = normalize_local_ai_provider_mode(Some(&stored)).to_string();
    if normalized == stored {
        return false;
    }

    root.insert("LocalAIProvider".to_string(), Value::String(normalized));
    true
}

pub fn save_settings_file(
    path: impl AsRef<Path>,
    settings: &SettingsState,
) -> Result<(), SettingsStorageError> {
    let path = path.as_ref();
    if let Some(parent) = path.parent() {
        if !parent.as_os_str().is_empty() {
            fs::create_dir_all(parent)?;
        }
    }

    fs::write(path, save_settings_json(settings)?)?;
    Ok(())
}

pub fn load_settings_json(json: &str) -> Result<SettingsLoadResult, SettingsStorageError> {
    let machine_id = default_storage_machine_id();
    load_settings_json_with_machine_id(json, &machine_id)
}

pub fn load_settings_json_with_machine_id(
    json: &str,
    machine_id: &str,
) -> Result<SettingsLoadResult, SettingsStorageError> {
    let (json, _) = migrate_settings_json(json)?;
    let root = serde_json::from_str::<Value>(&json)?;
    let mut root = root.as_object().cloned().unwrap_or_default();
    normalize_local_ai_provider_storage(&mut root);
    let mut settings = SettingsState::default();
    let mut warnings = Vec::new();

    apply_scalar_settings(&root, &mut settings);
    apply_sensitive_settings(&root, machine_id, &mut settings, &mut warnings);
    settings.imported_mdx_dictionaries = imported_mdx_dictionaries(&root);
    discover_missing_mdd_file_paths(&mut settings.imported_mdx_dictionaries);
    apply_window_service_settings(&root, &mut settings);

    Ok(SettingsLoadResult { settings, warnings })
}

pub fn save_settings_json(settings: &SettingsState) -> Result<String, SettingsStorageError> {
    let mut root = Map::new();
    write_scalar_settings(&mut root, settings);
    write_sensitive_settings(&mut root, settings)?;
    write_window_service_settings(&mut root, settings);
    write_imported_mdx_dictionaries(&mut root, settings);
    Ok(serde_json::to_string_pretty(&Value::Object(root))?)
}

fn apply_scalar_settings(root: &Map<String, Value>, settings: &mut SettingsState) {
    if let Some(value) = string_value(root, "AppTheme") {
        settings.theme = theme_from_storage(&value);
    }
    if let Some(value) = string_value(root, "UILanguage") {
        settings.ui_language = value;
    }
    if let Some(value) = string_value(root, "FirstLanguage") {
        settings.first_language = value;
    }
    if let Some(value) = string_value(root, "SecondLanguage") {
        settings.second_language = value;
    }
    if settings
        .first_language
        .eq_ignore_ascii_case(&settings.second_language)
    {
        settings.first_language = "zh".to_string();
        settings.second_language = "en".to_string();
    }
    if let Some(value) = bool_value(root, "AutoSelectTargetLanguage") {
        settings.auto_select_target_language = value;
    }
    if let Some(value) = string_list(root, "SelectedLanguages") {
        if value.len() >= 2 {
            settings.selected_languages = value;
        }
    }
    if let Some(value) = bool_value(root, "MinimizeToTray") {
        settings.minimize_to_tray = value;
    }
    if let Some(value) = bool_value(root, "MinimizeToTrayOnStartup") {
        settings.start_minimized = value;
    }
    if let Some(value) = bool_value_any(root, &["ClipboardMonitoring", "MonitorClipboard"]) {
        settings.monitor_clipboard = value;
    }
    if let Some(value) = bool_value(root, "MouseSelectionTranslate") {
        settings.mouse_selection_translate = value;
    }
    if let Some(value) = excluded_apps_text(root) {
        settings.mouse_selection_excluded_apps = value;
    }
    if let Some(value) = bool_value(root, "ShellContextMenu") {
        settings.shell_context_menu = value;
    }
    if let Some(value) = bool_value(root, "LaunchAtStartup") {
        settings.launch_at_startup = value;
    }
    if let Some(value) = bool_value(root, "EnableInternationalServices") {
        settings.enable_international_services = value;
    }
    if let Some(value) = bool_value(root, "HideEmptyServiceResults") {
        settings.hide_empty_service_results = value;
    }
    if let Some(value) = number_or_string(root, "TtsSpeed") {
        settings.tts_speed = value;
    }
    if let Some(value) = bool_value(root, "AutoPlayTranslation") {
        settings.auto_play_translation = value;
    }
    if let Some(value) = string_value(root, "OcrLanguage") {
        settings.ocr_language = value;
    }
    if let Some(value) = ocr_engine_value(root) {
        settings.ocr_engine = value;
    }
    if let Some(value) = string_value(root, "OcrEndpoint") {
        settings.ocr_endpoint = value;
    }
    if let Some(value) = string_value(root, "OcrModel") {
        settings.ocr_model = value;
    }
    if let Some(value) = string_value(root, "OcrSystemPrompt") {
        settings.ocr_system_prompt = value;
    }
    if let Some(value) = string_value(root, "LayoutDetectionMode") {
        settings.layout_detection_mode = value;
    }
    if let Some(value) = string_value_any(root, &["VisionLayoutServiceId", "VisionLayoutService"]) {
        settings.vision_layout_service = value;
    }
    if let Some(value) = string_value(root, "FormulaFontPattern") {
        settings.formula_font_pattern = value;
    }
    if let Some(value) = string_value(root, "FormulaCharPattern") {
        settings.formula_char_pattern = value;
    }
    if let Some(value) = bool_value(root, "EnableTranslationCache") {
        settings.translation_cache_enabled = value;
    }
    if let Some(value) = string_value_any(root, &["LongDocCustomPrompt", "CustomTranslationPrompt"])
    {
        settings.custom_translation_prompt = value;
    }
    if let Some(value) = bool_value(root, "ProxyEnabled") {
        settings.proxy_enabled = value;
    }
    if let Some(value) = string_value(root, "ProxyUri") {
        settings.proxy_url = value;
    }
    if let Some(value) = bool_value(root, "ProxyBypassLocal") {
        settings.proxy_bypass_local = value;
    }
    if let Some(value) = bool_value(root, "DeepLUseFreeApi") {
        settings.deepl_use_free_api = value;
    }
    if let Some(value) = bool_value(root, "DeepLUseQualityOptimized") {
        settings.deepl_use_quality_optimized = value;
    }
    if let Some(value) = string_value(root, "OpenAIEndpoint") {
        settings.open_ai_endpoint = value;
    }
    if let Some(value) = string_value(root, "OpenAIModel") {
        settings.open_ai_model = value;
    }
    if let Some(value) = string_value(root, "OpenAIApiFormatOverride") {
        settings.open_ai_api_format_override = value;
    }
    if let Some(value) = string_value(root, "DeviceId") {
        settings.device_id = value;
    }
    if let Some(value) = string_value(root, "DeviceToken") {
        settings.device_token = value;
    }
    if let Some(value) = string_value(root, "OllamaEndpoint") {
        settings.ollama_endpoint = value;
    }
    if let Some(value) = string_value(root, "OllamaModel") {
        settings.ollama_model = value;
    }
    if let Some(value) = string_value(root, "LocalAIProvider") {
        settings.local_ai_provider = value;
    }
    if let Some(value) = string_value(root, "FoundryLocalEndpoint") {
        settings.foundry_local_endpoint = value;
    }
    if let Some(value) = string_value(root, "FoundryLocalModel") {
        settings.foundry_local_model = value;
    }
    if let Some(value) = string_value(root, "OpenVinoDevice") {
        settings.open_vino_device = value;
    }
    if let Some(value) = bool_value(root, "YoudaoUseOfficialApi") {
        settings.youdao_use_official_api = value;
    }
    apply_provider_scalar(root, settings, "deepseek", "DeepSeekModel", None);
    apply_provider_scalar(root, settings, "groq", "GroqModel", None);
    apply_provider_scalar(root, settings, "zhipu", "ZhipuModel", None);
    apply_provider_scalar(root, settings, "github", "GitHubModelsModel", None);
    apply_provider_scalar(root, settings, "gemini", "GeminiModel", None);
    apply_provider_scalar(
        root,
        settings,
        "custom-openai",
        "CustomOpenAIModel",
        Some("CustomOpenAIEndpoint"),
    );
    apply_provider_scalar(root, settings, "builtin", "BuiltInAIModel", None);
    apply_provider_scalar(
        root,
        settings,
        "doubao",
        "DoubaoModel",
        Some("DoubaoEndpoint"),
    );
    apply_hotkey(
        root,
        &mut settings.show_main_hotkey,
        "ShowWindowHotkey",
        "EnableShowWindowHotkey",
    );
    apply_hotkey(
        root,
        &mut settings.translate_clipboard_hotkey,
        "TranslateSelectionHotkey",
        "EnableTranslateSelectionHotkey",
    );
    apply_hotkey(
        root,
        &mut settings.show_mini_hotkey,
        "ShowMiniWindowHotkey",
        "EnableShowMiniWindowHotkey",
    );
    apply_hotkey(
        root,
        &mut settings.show_fixed_hotkey,
        "ShowFixedWindowHotkey",
        "EnableShowFixedWindowHotkey",
    );
    apply_hotkey(
        root,
        &mut settings.ocr_translate_hotkey,
        "OcrTranslateHotkey",
        "EnableOcrTranslateHotkey",
    );
    apply_hotkey(
        root,
        &mut settings.silent_ocr_hotkey,
        "SilentOcrHotkey",
        "EnableSilentOcrHotkey",
    );
    if let Some(value) = bool_value(root, "MiniWindowAutoClose") {
        settings.mini_auto_close = value;
    }
    if let Some(value) = bool_value(root, "FixedWindowAlwaysOnTop") {
        settings.fixed_always_on_top = value;
    }
    if let Some(value) = bool_value(root, "EnableLocalDictionarySuggestions") {
        settings.local_dictionary_suggestions = value;
    }
}

fn apply_sensitive_settings(
    root: &Map<String, Value>,
    machine_id: &str,
    settings: &mut SettingsState,
    warnings: &mut Vec<String>,
) {
    settings.deepl_api_key = sensitive_value(root, "DeepLApiKey", machine_id, warnings);
    settings.open_ai_api_key = sensitive_value(root, "OpenAIApiKey", machine_id, warnings);
    settings.ocr_api_key = sensitive_value(root, "OcrApiKey", machine_id, warnings);
    settings.caiyun_api_key = sensitive_value(root, "CaiyunApiKey", machine_id, warnings);
    settings.niu_trans_api_key = sensitive_value(root, "NiuTransApiKey", machine_id, warnings);
    settings.youdao_app_key = sensitive_value(root, "YoudaoAppKey", machine_id, warnings);
    settings.youdao_app_secret = sensitive_value(root, "YoudaoAppSecret", machine_id, warnings);
    settings.volcano_access_key_id =
        sensitive_value(root, "VolcanoAccessKeyId", machine_id, warnings);
    settings.volcano_secret_access_key =
        sensitive_value(root, "VolcanoSecretAccessKey", machine_id, warnings);
    set_provider_api_key(
        settings,
        "deepseek",
        sensitive_value(root, "DeepSeekApiKey", machine_id, warnings),
    );
    set_provider_api_key(
        settings,
        "groq",
        sensitive_value(root, "GroqApiKey", machine_id, warnings),
    );
    set_provider_api_key(
        settings,
        "zhipu",
        sensitive_value(root, "ZhipuApiKey", machine_id, warnings),
    );
    set_provider_api_key(
        settings,
        "github",
        sensitive_value(root, "GitHubModelsToken", machine_id, warnings),
    );
    set_provider_api_key(
        settings,
        "gemini",
        sensitive_value(root, "GeminiApiKey", machine_id, warnings),
    );
    set_provider_api_key(
        settings,
        "custom-openai",
        sensitive_value(root, "CustomOpenAIApiKey", machine_id, warnings),
    );
    set_provider_api_key(
        settings,
        "builtin",
        sensitive_value(root, "BuiltInAIApiKey", machine_id, warnings),
    );
    set_provider_api_key(
        settings,
        "doubao",
        sensitive_value(root, "DoubaoApiKey", machine_id, warnings),
    );
}

fn write_scalar_settings(root: &mut Map<String, Value>, settings: &SettingsState) {
    insert_string(root, "AppTheme", theme_to_storage(settings.theme));
    insert_string(root, "UILanguage", &settings.ui_language);
    insert_string(root, "FirstLanguage", &settings.first_language);
    insert_string(root, "SecondLanguage", &settings.second_language);
    insert_bool(
        root,
        "AutoSelectTargetLanguage",
        settings.auto_select_target_language,
    );
    insert_string_array(root, "SelectedLanguages", &settings.selected_languages);
    insert_bool(root, "MinimizeToTray", settings.minimize_to_tray);
    insert_bool(root, "MinimizeToTrayOnStartup", settings.start_minimized);
    insert_bool(root, "ClipboardMonitoring", settings.monitor_clipboard);
    insert_bool(
        root,
        "MouseSelectionTranslate",
        settings.mouse_selection_translate,
    );
    insert_string_array(
        root,
        "MouseSelectionExcludedApps",
        &excluded_apps_list(&settings.mouse_selection_excluded_apps),
    );
    insert_bool(root, "ShellContextMenu", settings.shell_context_menu);
    insert_bool(root, "LaunchAtStartup", settings.launch_at_startup);
    insert_bool(
        root,
        "EnableInternationalServices",
        settings.enable_international_services,
    );
    insert_bool(
        root,
        "HideEmptyServiceResults",
        settings.hide_empty_service_results,
    );
    insert_f64_or_string(root, "TtsSpeed", &settings.tts_speed);
    insert_bool(root, "AutoPlayTranslation", settings.auto_play_translation);
    insert_string(root, "OcrLanguage", &settings.ocr_language);
    insert_i64(
        root,
        "OcrEngine",
        ocr_engine_to_legacy_int(&settings.ocr_engine),
    );
    insert_string(root, "OcrEndpoint", &settings.ocr_endpoint);
    insert_string(root, "OcrModel", &settings.ocr_model);
    insert_string(root, "OcrSystemPrompt", &settings.ocr_system_prompt);
    insert_string(root, "LayoutDetectionMode", &settings.layout_detection_mode);
    insert_string(
        root,
        "VisionLayoutServiceId",
        &settings.vision_layout_service,
    );
    insert_string(root, "FormulaFontPattern", &settings.formula_font_pattern);
    insert_string(root, "FormulaCharPattern", &settings.formula_char_pattern);
    insert_bool(
        root,
        "EnableTranslationCache",
        settings.translation_cache_enabled,
    );
    insert_string(
        root,
        "LongDocCustomPrompt",
        &settings.custom_translation_prompt,
    );
    insert_bool(root, "ProxyEnabled", settings.proxy_enabled);
    insert_string(root, "ProxyUri", &settings.proxy_url);
    insert_bool(root, "ProxyBypassLocal", settings.proxy_bypass_local);
    insert_bool(root, "DeepLUseFreeApi", settings.deepl_use_free_api);
    insert_bool(
        root,
        "DeepLUseQualityOptimized",
        settings.deepl_use_quality_optimized,
    );
    insert_string(root, "OpenAIEndpoint", &settings.open_ai_endpoint);
    insert_string(root, "OpenAIModel", &settings.open_ai_model);
    insert_string(
        root,
        "OpenAIApiFormatOverride",
        &settings.open_ai_api_format_override,
    );
    insert_string(root, "DeviceId", &settings.device_id);
    insert_string(root, "DeviceToken", &settings.device_token);
    insert_string(root, "OllamaEndpoint", &settings.ollama_endpoint);
    insert_string(root, "OllamaModel", &settings.ollama_model);
    insert_string(
        root,
        "LocalAIProvider",
        normalize_local_ai_provider_mode(Some(&settings.local_ai_provider)),
    );
    insert_string(
        root,
        "FoundryLocalEndpoint",
        &settings.foundry_local_endpoint,
    );
    insert_string(root, "FoundryLocalModel", &settings.foundry_local_model);
    insert_string(root, "OpenVinoDevice", &settings.open_vino_device);
    insert_bool(
        root,
        "YoudaoUseOfficialApi",
        settings.youdao_use_official_api,
    );
    write_provider_scalar(root, settings, "deepseek", "DeepSeekModel", None);
    write_provider_scalar(root, settings, "groq", "GroqModel", None);
    write_provider_scalar(root, settings, "zhipu", "ZhipuModel", None);
    write_provider_scalar(root, settings, "github", "GitHubModelsModel", None);
    write_provider_scalar(root, settings, "gemini", "GeminiModel", None);
    write_provider_scalar(
        root,
        settings,
        "custom-openai",
        "CustomOpenAIModel",
        Some("CustomOpenAIEndpoint"),
    );
    write_provider_scalar(root, settings, "builtin", "BuiltInAIModel", None);
    write_provider_scalar(
        root,
        settings,
        "doubao",
        "DoubaoModel",
        Some("DoubaoEndpoint"),
    );
    write_hotkey(
        root,
        &settings.show_main_hotkey,
        "ShowWindowHotkey",
        "EnableShowWindowHotkey",
    );
    write_hotkey(
        root,
        &settings.translate_clipboard_hotkey,
        "TranslateSelectionHotkey",
        "EnableTranslateSelectionHotkey",
    );
    write_hotkey(
        root,
        &settings.show_mini_hotkey,
        "ShowMiniWindowHotkey",
        "EnableShowMiniWindowHotkey",
    );
    write_hotkey(
        root,
        &settings.show_fixed_hotkey,
        "ShowFixedWindowHotkey",
        "EnableShowFixedWindowHotkey",
    );
    write_hotkey(
        root,
        &settings.ocr_translate_hotkey,
        "OcrTranslateHotkey",
        "EnableOcrTranslateHotkey",
    );
    write_hotkey(
        root,
        &settings.silent_ocr_hotkey,
        "SilentOcrHotkey",
        "EnableSilentOcrHotkey",
    );
    insert_bool(root, "MiniWindowAutoClose", settings.mini_auto_close);
    insert_bool(root, "FixedWindowAlwaysOnTop", settings.fixed_always_on_top);
    insert_bool(
        root,
        "EnableLocalDictionarySuggestions",
        settings.local_dictionary_suggestions,
    );
}

fn write_sensitive_settings(
    root: &mut Map<String, Value>,
    settings: &SettingsState,
) -> Result<(), SettingsStorageError> {
    insert_sensitive(root, "DeepLApiKey", &settings.deepl_api_key)?;
    insert_sensitive(root, "OpenAIApiKey", &settings.open_ai_api_key)?;
    insert_sensitive(root, "OcrApiKey", &settings.ocr_api_key)?;
    insert_sensitive(root, "CaiyunApiKey", &settings.caiyun_api_key)?;
    insert_sensitive(root, "NiuTransApiKey", &settings.niu_trans_api_key)?;
    insert_sensitive(root, "YoudaoAppKey", &settings.youdao_app_key)?;
    insert_sensitive(root, "YoudaoAppSecret", &settings.youdao_app_secret)?;
    insert_sensitive(root, "VolcanoAccessKeyId", &settings.volcano_access_key_id)?;
    insert_sensitive(
        root,
        "VolcanoSecretAccessKey",
        &settings.volcano_secret_access_key,
    )?;
    insert_provider_sensitive(root, settings, "deepseek", "DeepSeekApiKey")?;
    insert_provider_sensitive(root, settings, "groq", "GroqApiKey")?;
    insert_provider_sensitive(root, settings, "zhipu", "ZhipuApiKey")?;
    insert_provider_sensitive(root, settings, "github", "GitHubModelsToken")?;
    insert_provider_sensitive(root, settings, "gemini", "GeminiApiKey")?;
    insert_provider_sensitive(root, settings, "custom-openai", "CustomOpenAIApiKey")?;
    insert_provider_sensitive(root, settings, "builtin", "BuiltInAIApiKey")?;
    insert_provider_sensitive(root, settings, "doubao", "DoubaoApiKey")?;
    Ok(())
}

fn write_window_service_settings(root: &mut Map<String, Value>, settings: &SettingsState) {
    insert_string_array(
        root,
        "MainWindowEnabledServices",
        &enabled_service_ids(&settings.main_window_services),
    );
    insert_string_array(
        root,
        "MiniWindowEnabledServices",
        &enabled_service_ids(&settings.mini_window_services),
    );
    insert_string_array(
        root,
        "FixedWindowEnabledServices",
        &enabled_service_ids(&settings.fixed_window_services),
    );
    root.insert(
        "MainWindowServiceEnabledQuery".to_string(),
        Value::Object(enabled_query_map(&settings.main_window_services)),
    );
    root.insert(
        "MiniWindowServiceEnabledQuery".to_string(),
        Value::Object(enabled_query_map(&settings.mini_window_services)),
    );
    root.insert(
        "FixedWindowServiceEnabledQuery".to_string(),
        Value::Object(enabled_query_map(&settings.fixed_window_services)),
    );
}

fn write_imported_mdx_dictionaries(root: &mut Map<String, Value>, settings: &SettingsState) {
    let dictionaries = settings
        .imported_mdx_dictionaries
        .iter()
        .map(|dictionary| {
            let mut item = Map::new();
            insert_string(&mut item, "ServiceId", &dictionary.service_id);
            insert_string(&mut item, "DisplayName", &dictionary.display_name);
            insert_string(&mut item, "FilePath", &dictionary.file_path);
            insert_bool(&mut item, "IsEncrypted", dictionary.is_encrypted);
            insert_optional_string(&mut item, "Regcode", dictionary.regcode.as_deref());
            insert_optional_string(&mut item, "Email", dictionary.email.as_deref());
            insert_string_array(&mut item, "MddFilePaths", &dictionary.mdd_file_paths);
            Value::Object(item)
        })
        .collect::<Vec<_>>();
    root.insert(
        "ImportedMdxDictionaries".to_string(),
        Value::Array(dictionaries),
    );
}

fn apply_window_service_settings(root: &Map<String, Value>, settings: &mut SettingsState) {
    let dictionaries = settings.imported_mdx_dictionaries.clone();
    apply_window_service_list(
        &mut settings.main_window_services,
        string_list(root, "MainWindowEnabledServices"),
        string_bool_map(root, "MainWindowServiceEnabledQuery"),
        &dictionaries,
    );
    apply_window_service_list(
        &mut settings.mini_window_services,
        string_list(root, "MiniWindowEnabledServices"),
        string_bool_map(root, "MiniWindowServiceEnabledQuery"),
        &dictionaries,
    );
    apply_window_service_list(
        &mut settings.fixed_window_services,
        string_list(root, "FixedWindowEnabledServices"),
        string_bool_map(root, "FixedWindowServiceEnabledQuery"),
        &dictionaries,
    );
}

fn apply_window_service_list(
    services: &mut Vec<WindowServiceSetting>,
    enabled_ids: Option<Vec<String>>,
    enabled_query: Option<Map<String, Value>>,
    dictionaries: &[ImportedMdxDictionary],
) {
    if let Some(enabled_ids) = enabled_ids {
        for service in services.iter_mut() {
            service.enabled = enabled_ids
                .iter()
                .any(|enabled_id| enabled_id.eq_ignore_ascii_case(&service.service_id));
        }

        for enabled_id in enabled_ids {
            if services
                .iter()
                .any(|service| service.service_id.eq_ignore_ascii_case(&enabled_id))
            {
                continue;
            }

            let display_name = dictionaries
                .iter()
                .find(|dictionary| dictionary.service_id.eq_ignore_ascii_case(&enabled_id))
                .map(|dictionary| dictionary.display_name.clone())
                .unwrap_or_else(|| enabled_id.clone());
            services.push(WindowServiceSetting {
                service_id: enabled_id,
                display_name,
                enabled: true,
                enabled_query: true,
                configured: true,
            });
        }
    }

    if let Some(enabled_query) = enabled_query {
        for service in services.iter_mut() {
            if let Some(value) = bool_from_map(&enabled_query, &service.service_id) {
                service.enabled_query = value;
            }
        }
    }
}

fn imported_mdx_dictionaries(root: &Map<String, Value>) -> Vec<ImportedMdxDictionary> {
    root.get("ImportedMdxDictionaries")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(Value::as_object)
        .filter_map(|item| {
            let service_id = string_from_map(item, "ServiceId")?;
            let display_name =
                string_from_map(item, "DisplayName").unwrap_or_else(|| service_id.clone());
            let file_path = string_from_map(item, "FilePath")?;
            Some(ImportedMdxDictionary {
                service_id,
                display_name,
                file_path,
                is_encrypted: bool_from_map(item, "IsEncrypted").unwrap_or(false),
                regcode: string_from_map(item, "Regcode").filter(|value| !value.is_empty()),
                email: string_from_map(item, "Email").filter(|value| !value.is_empty()),
                mdd_file_paths: string_list_from_map(item, "MddFilePaths").unwrap_or_default(),
            })
        })
        .collect()
}

fn discover_missing_mdd_file_paths(dictionaries: &mut [ImportedMdxDictionary]) {
    for dictionary in dictionaries {
        let mut seen = dictionary
            .mdd_file_paths
            .iter()
            .map(|path| path.trim().to_ascii_lowercase())
            .collect::<HashSet<_>>();
        for discovered in discover_mdd_file_paths(&dictionary.file_path) {
            if seen.insert(discovered.trim().to_ascii_lowercase()) {
                dictionary.mdd_file_paths.push(discovered);
            }
        }
    }
}

fn sensitive_value(
    root: &Map<String, Value>,
    key: &str,
    machine_id: &str,
    warnings: &mut Vec<String>,
) -> String {
    let Some(stored) = string_value(root, key) else {
        return String::new();
    };
    let plaintext = unprotect_or_return_plaintext_with_machine_id(Some(&stored), machine_id);
    if plaintext.needs_migration {
        warnings.push(format!("{key} needs credential normalization"));
    }
    if plaintext.decrypt_failed {
        warnings.push(format!("{key} could not be decrypted"));
    }
    plaintext.value.unwrap_or_default()
}

fn insert_sensitive(
    root: &mut Map<String, Value>,
    key: &str,
    plaintext: &str,
) -> Result<(), SettingsStorageError> {
    let value = protect_credential(plaintext)
        .map_err(|error| SettingsStorageError::Credential(error.to_string()))?;
    root.insert(key.to_string(), Value::String(value));
    Ok(())
}

fn insert_provider_sensitive(
    root: &mut Map<String, Value>,
    settings: &SettingsState,
    service_id: &str,
    key: &str,
) -> Result<(), SettingsStorageError> {
    let value = provider_setting(settings, service_id)
        .map(|setting| setting.api_key.as_str())
        .unwrap_or_default();
    insert_sensitive(root, key, value)
}

fn provider_setting<'a>(
    settings: &'a SettingsState,
    service_id: &str,
) -> Option<&'a ServiceProviderSetting> {
    settings
        .service_provider_settings
        .iter()
        .find(|setting| setting.service_id == service_id)
}

fn provider_setting_mut<'a>(
    settings: &'a mut SettingsState,
    service_id: &str,
) -> Option<&'a mut ServiceProviderSetting> {
    settings
        .service_provider_settings
        .iter_mut()
        .find(|setting| setting.service_id == service_id)
}

fn set_provider_api_key(settings: &mut SettingsState, service_id: &str, api_key: String) {
    if let Some(setting) = provider_setting_mut(settings, service_id) {
        setting.api_key = api_key;
    }
}

fn apply_provider_scalar(
    root: &Map<String, Value>,
    settings: &mut SettingsState,
    service_id: &str,
    model_key: &str,
    endpoint_key: Option<&str>,
) {
    if let Some(setting) = provider_setting_mut(settings, service_id) {
        if let Some(value) = string_value(root, model_key) {
            setting.model = value;
        }
        if let Some(endpoint_key) = endpoint_key {
            if let Some(value) = string_value(root, endpoint_key) {
                setting.endpoint = value;
            }
        }
    }
}

fn write_provider_scalar(
    root: &mut Map<String, Value>,
    settings: &SettingsState,
    service_id: &str,
    model_key: &str,
    endpoint_key: Option<&str>,
) {
    if let Some(setting) = provider_setting(settings, service_id) {
        insert_string(root, model_key, &setting.model);
        if let Some(endpoint_key) = endpoint_key {
            insert_string(root, endpoint_key, &setting.endpoint);
        }
    }
}

fn apply_hotkey(
    root: &Map<String, Value>,
    hotkey: &mut HotkeySetting,
    shortcut_key: &str,
    enabled_key: &str,
) {
    if let Some(value) = string_value(root, shortcut_key) {
        hotkey.shortcut = value;
    }
    if let Some(value) = bool_value(root, enabled_key) {
        hotkey.enabled = value;
    }
}

fn write_hotkey(
    root: &mut Map<String, Value>,
    hotkey: &HotkeySetting,
    shortcut_key: &str,
    enabled_key: &str,
) {
    insert_string(root, shortcut_key, &hotkey.shortcut);
    insert_bool(root, enabled_key, hotkey.enabled);
}

fn enabled_service_ids(services: &[WindowServiceSetting]) -> Vec<String> {
    services
        .iter()
        .filter(|service| service.enabled)
        .map(|service| service.service_id.clone())
        .collect()
}

fn enabled_query_map(services: &[WindowServiceSetting]) -> Map<String, Value> {
    services
        .iter()
        .map(|service| {
            (
                service.service_id.clone(),
                Value::Bool(service.enabled_query),
            )
        })
        .collect()
}

fn string_value(root: &Map<String, Value>, key: &str) -> Option<String> {
    string_from_map(root, key)
}

fn string_value_any(root: &Map<String, Value>, keys: &[&str]) -> Option<String> {
    keys.iter().find_map(|key| string_value(root, key))
}

fn string_from_map(root: &Map<String, Value>, key: &str) -> Option<String> {
    root.get(key).and_then(|value| match value {
        Value::String(value) => Some(value.clone()),
        Value::Number(value) => Some(value.to_string()),
        Value::Bool(value) => Some(value.to_string()),
        _ => None,
    })
}

fn number_or_string(root: &Map<String, Value>, key: &str) -> Option<String> {
    root.get(key).and_then(|value| match value {
        Value::String(value) => Some(value.clone()),
        Value::Number(value) => Some(value.to_string()),
        _ => None,
    })
}

fn bool_value(root: &Map<String, Value>, key: &str) -> Option<bool> {
    bool_from_map(root, key)
}

fn bool_value_any(root: &Map<String, Value>, keys: &[&str]) -> Option<bool> {
    keys.iter().find_map(|key| bool_value(root, key))
}

fn bool_from_map(root: &Map<String, Value>, key: &str) -> Option<bool> {
    root.get(key).and_then(|value| match value {
        Value::Bool(value) => Some(*value),
        Value::String(value) => match value.trim().to_ascii_lowercase().as_str() {
            "true" | "1" | "yes" | "on" => Some(true),
            "false" | "0" | "no" | "off" => Some(false),
            _ => None,
        },
        _ => None,
    })
}

fn string_list(root: &Map<String, Value>, key: &str) -> Option<Vec<String>> {
    string_list_from_map(root, key)
}

fn string_list_from_map(root: &Map<String, Value>, key: &str) -> Option<Vec<String>> {
    let value = root.get(key)?;
    if let Some(array) = value.as_array() {
        return Some(
            array
                .iter()
                .filter_map(|item| item.as_str().map(str::to_string))
                .collect(),
        );
    }

    value.as_str().map(|value| {
        value
            .split([',', ';', '\n'])
            .map(str::trim)
            .filter(|part| !part.is_empty())
            .map(str::to_string)
            .collect()
    })
}

fn string_bool_map(root: &Map<String, Value>, key: &str) -> Option<Map<String, Value>> {
    root.get(key).and_then(Value::as_object).cloned()
}

fn insert_string(root: &mut Map<String, Value>, key: &str, value: &str) {
    root.insert(key.to_string(), Value::String(value.to_string()));
}

fn insert_optional_string(root: &mut Map<String, Value>, key: &str, value: Option<&str>) {
    root.insert(
        key.to_string(),
        value
            .map(|value| Value::String(value.to_string()))
            .unwrap_or(Value::Null),
    );
}

fn insert_bool(root: &mut Map<String, Value>, key: &str, value: bool) {
    root.insert(key.to_string(), Value::Bool(value));
}

fn insert_i64(root: &mut Map<String, Value>, key: &str, value: i64) {
    root.insert(key.to_string(), Value::Number(value.into()));
}

fn insert_f64_or_string(root: &mut Map<String, Value>, key: &str, value: &str) {
    if let Ok(number) = value.parse::<f64>() {
        if let Some(number) = serde_json::Number::from_f64(number) {
            root.insert(key.to_string(), Value::Number(number));
            return;
        }
    }
    insert_string(root, key, value);
}

fn insert_string_array(root: &mut Map<String, Value>, key: &str, values: &[String]) {
    root.insert(
        key.to_string(),
        Value::Array(
            values
                .iter()
                .map(|value| Value::String(value.clone()))
                .collect(),
        ),
    );
}

fn excluded_apps_text(root: &Map<String, Value>) -> Option<String> {
    string_list(root, "MouseSelectionExcludedApps").map(|values| values.join("\n"))
}

fn excluded_apps_list(value: &str) -> Vec<String> {
    value
        .split([',', ';', '\n'])
        .map(str::trim)
        .filter(|part| !part.is_empty())
        .map(str::to_string)
        .collect()
}

fn ocr_engine_value(root: &Map<String, Value>) -> Option<String> {
    root.get("OcrEngine").and_then(|value| match value {
        Value::Number(number) => match number.as_i64() {
            Some(0) => Some("WindowsNative".to_string()),
            Some(1) => Some("Ollama".to_string()),
            Some(2) => Some("CustomApi".to_string()),
            _ => None,
        },
        Value::String(value) => Some(
            match value.trim().to_ascii_lowercase().as_str() {
                "0" | "windows" | "windowsnative" | "windows-native" => "WindowsNative",
                "1" | "ollama" => "Ollama",
                "2" | "custom" | "customapi" | "custom-api" => "CustomApi",
                _ => value,
            }
            .to_string(),
        ),
        _ => None,
    })
}

fn ocr_engine_to_legacy_int(value: &str) -> i64 {
    match value.trim().to_ascii_lowercase().as_str() {
        "ollama" => 1,
        "customapi" | "custom-api" => 2,
        _ => 0,
    }
}

fn theme_from_storage(value: &str) -> ThemeMode {
    match value.trim().to_ascii_lowercase().as_str() {
        "system" => ThemeMode::System,
        "dark" => ThemeMode::Dark,
        "minimal" => ThemeMode::Minimal,
        "highcontrast" | "high-contrast" => ThemeMode::HighContrast,
        _ => ThemeMode::Light,
    }
}

fn theme_to_storage(theme: ThemeMode) -> &'static str {
    match theme {
        ThemeMode::System => "System",
        ThemeMode::Light => "Light",
        ThemeMode::Dark => "Dark",
        ThemeMode::Minimal => "Minimal",
        ThemeMode::HighContrast => "HighContrast",
    }
}

fn default_storage_machine_id() -> String {
    let directory = default_settings_storage_path()
        .parent()
        .map(Path::to_path_buf)
        .unwrap_or_else(|| PathBuf::from("."));
    get_or_create_persisted_machine_id_with_legacy_fallback(directory, legacy_user_data_directory())
}

use easydict_app::{
    load_settings_file, load_settings_json_with_machine_id, protect_credential_legacy,
    save_settings_file, save_settings_json, ImportedMdxDictionary, SettingsState,
};
use serde_json::{json, Value};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};
use win_fluent::prelude::ThemeMode;

#[cfg(windows)]
#[test]
fn settings_storage_saves_legacy_keys_and_protects_sensitive_values() {
    let mut settings = SettingsState::default();
    settings.theme = ThemeMode::Dark;
    settings.ui_language = "zh-CN".to_string();
    settings.first_language = "ja".to_string();
    settings.second_language = "en".to_string();
    settings.selected_languages = vec!["ja".to_string(), "en".to_string(), "zh-Hans".to_string()];
    settings.proxy_enabled = true;
    settings.proxy_url = "http://127.0.0.1:7890".to_string();
    settings.proxy_bypass_local = false;
    settings.monitor_clipboard = true;
    settings.launch_at_startup = true;
    settings.open_ai_api_key = "sk-openai".to_string();
    settings.device_id = "device-id".to_string();
    settings.device_token = "device-token".to_string();
    settings.deepl_api_key = "deepl-secret".to_string();
    settings.ocr_api_key = "ocr-secret".to_string();
    settings.ocr_engine = "CustomApi".to_string();
    settings.ocr_endpoint = "https://ocr.example.test/v1/responses".to_string();
    settings.local_ai_provider = "FoundryLocal".to_string();
    settings.foundry_local_endpoint = "http://127.0.0.1:5273/v1".to_string();
    settings.vision_layout_service = "openai".to_string();
    settings.custom_translation_prompt = "Keep legal terminology stable.".to_string();
    settings.show_mini_hotkey.shortcut = "Ctrl+Alt+N".to_string();
    settings.show_mini_hotkey.enabled = false;
    settings
        .imported_mdx_dictionaries
        .push(ImportedMdxDictionary {
            service_id: "mdx::demo".to_string(),
            display_name: "Demo Dictionary".to_string(),
            file_path: r"C:\Dicts\demo.mdx".to_string(),
            is_encrypted: true,
            regcode: Some("reg".to_string()),
            email: Some("reader@example.test".to_string()),
            mdd_file_paths: vec![r"C:\Dicts\demo.mdd".to_string()],
        });
    if let Some(deepseek) = settings
        .service_provider_settings
        .iter_mut()
        .find(|setting| setting.service_id == "deepseek")
    {
        deepseek.api_key = "deepseek-secret".to_string();
        deepseek.model = "deepseek-v3".to_string();
    }
    if let Some(custom) = settings
        .service_provider_settings
        .iter_mut()
        .find(|setting| setting.service_id == "custom-openai")
    {
        custom.api_key = "custom-secret".to_string();
        custom.endpoint = "https://custom.example.test/v1/chat/completions".to_string();
        custom.model = "custom-model".to_string();
    }
    for service in &mut settings.main_window_services {
        service.enabled = service.service_id == "openai" || service.service_id == "deepseek";
        if service.service_id == "openai" {
            service.enabled_query = false;
        }
    }
    settings
        .main_window_services
        .push(easydict_app::state::WindowServiceSetting {
            service_id: "mdx::demo".to_string(),
            display_name: "Demo Dictionary".to_string(),
            enabled: true,
            enabled_query: true,
            configured: true,
        });

    let json = save_settings_json(&settings).expect("settings should serialize");
    let root: Value = serde_json::from_str(&json).unwrap();

    assert_eq!(root["AppTheme"], "Dark");
    assert_eq!(root["OcrEngine"], 2);
    assert_eq!(root["ProxyUri"], "http://127.0.0.1:7890");
    assert_eq!(root["ClipboardMonitoring"], true);
    assert!(root.get("MonitorClipboard").is_none());
    assert_eq!(root["LaunchAtStartup"], true);
    assert_eq!(root["DeviceId"], "device-id");
    assert_eq!(root["DeviceToken"], "device-token");
    assert_eq!(root["VisionLayoutServiceId"], "openai");
    assert!(root.get("VisionLayoutService").is_none());
    assert_eq!(
        root["LongDocCustomPrompt"],
        "Keep legal terminology stable."
    );
    assert!(root.get("CustomTranslationPrompt").is_none());
    assert_eq!(root["ShowMiniWindowHotkey"], "Ctrl+Alt+N");
    assert_eq!(root["EnableShowMiniWindowHotkey"], false);
    assert!(root["MainWindowEnabledServices"]
        .as_array()
        .unwrap()
        .iter()
        .any(|value| value.as_str() == Some("mdx::demo")));
    assert_eq!(root["MainWindowServiceEnabledQuery"]["openai"], false);
    assert_protected(&root, "OpenAIApiKey", "sk-openai");
    assert_protected(&root, "DeepLApiKey", "deepl-secret");
    assert_protected(&root, "DeepSeekApiKey", "deepseek-secret");
    assert_protected(&root, "CustomOpenAIApiKey", "custom-secret");
    assert_protected(&root, "OcrApiKey", "ocr-secret");
    assert!(!json.contains("sk-openai"));
    assert!(!json.contains("deepseek-secret"));
    assert!(!json.contains("custom-secret"));
}

#[test]
fn settings_storage_loads_migrated_legacy_json_and_decrypts_old_credentials() {
    let deep_l = protect_credential_legacy("deepl-legacy", "stable-machine-id").unwrap();
    let custom = protect_credential_legacy("custom-legacy", "stable-machine-id").unwrap();
    let json = format!(
        r#"{{
  "WindowWidth": 640,
  "WindowHeight": 720,
  "AppTheme": "Minimal",
  "UILanguage": "ja-JP",
  "FirstLanguage": "en",
  "SecondLanguage": "en",
  "SelectedLanguages": ["en", "ja", "zh-Hans"],
  "OpenAIApiKey": "plain-openai",
  "DeviceId": "legacy-device-id",
  "DeviceToken": "legacy-device-token",
  "DeepLApiKey": "{deep_l}",
  "CustomOpenAIApiKey": "{custom}",
  "CustomOpenAIEndpoint": "https://custom.example.test/v1/responses",
  "CustomOpenAIModel": "custom-model",
  "ProxyEnabled": true,
  "ProxyUri": "http://localhost:8080",
  "ProxyBypassLocal": false,
  "ClipboardMonitoring": true,
  "LaunchAtStartup": true,
  "OcrEngine": 1,
  "OcrLanguage": "ko",
  "VisionLayoutServiceId": "openai",
  "LongDocCustomPrompt": "Keep names literal.",
  "MainWindowEnabledServices": ["openvino-local-ai", "custom-openai"],
  "MainWindowServiceEnabledQuery": {{ "openvino-local-ai": false, "custom-openai": true }},
  "ImportedMdxDictionaries": [
    {{
      "ServiceId": "mdx::demo",
      "DisplayName": "Demo Dictionary",
      "FilePath": "C:\\Dicts\\demo.mdx",
      "IsEncrypted": true,
      "Regcode": "reg",
      "Email": "reader@example.test",
      "MddFilePaths": ["C:\\Dicts\\demo.mdd"]
    }}
  ]
}}"#
    );

    let result =
        load_settings_json_with_machine_id(&json, "stable-machine-id").expect("settings load");
    let settings = result.settings;

    assert_eq!(settings.theme, ThemeMode::Minimal);
    assert_eq!(settings.ui_language, "ja-JP");
    assert_eq!(settings.first_language, "zh");
    assert_eq!(settings.second_language, "en");
    assert_eq!(settings.open_ai_api_key, "plain-openai");
    assert_eq!(settings.device_id, "legacy-device-id");
    assert_eq!(settings.device_token, "legacy-device-token");
    assert_eq!(settings.deepl_api_key, "deepl-legacy");
    let custom_provider = settings
        .service_provider_settings
        .iter()
        .find(|setting| setting.service_id == "custom-openai")
        .unwrap();
    assert_eq!(custom_provider.api_key, "custom-legacy");
    assert_eq!(
        custom_provider.endpoint,
        "https://custom.example.test/v1/responses"
    );
    assert_eq!(custom_provider.model, "custom-model");
    assert!(settings.proxy_enabled);
    assert_eq!(settings.proxy_url, "http://localhost:8080");
    assert!(!settings.proxy_bypass_local);
    assert!(settings.monitor_clipboard);
    assert!(settings.launch_at_startup);
    assert_eq!(settings.ocr_engine, "Ollama");
    assert_eq!(settings.ocr_language, "ko");
    assert_eq!(settings.vision_layout_service, "openai");
    assert_eq!(settings.custom_translation_prompt, "Keep names literal.");
    assert_eq!(settings.local_ai_provider, "OpenVINO");
    assert!(settings
        .main_window_services
        .iter()
        .any(|service| service.service_id == "windows-local-ai"
            && service.enabled
            && !service.enabled_query));
    assert!(settings
        .main_window_services
        .iter()
        .any(|service| service.service_id == "custom-openai" && service.enabled));
    assert_eq!(settings.imported_mdx_dictionaries.len(), 1);
    assert_eq!(
        settings.imported_mdx_dictionaries[0].mdd_file_paths,
        vec![r"C:\Dicts\demo.mdd".to_string()]
    );
    assert_eq!(
        settings.imported_mdx_dictionaries[0].file_path,
        r"C:\Dicts\demo.mdx"
    );
    assert!(result
        .warnings
        .iter()
        .any(|warning| warning.contains("OpenAIApiKey needs credential normalization")));
    assert!(result
        .warnings
        .iter()
        .any(|warning| warning.contains("DeepLApiKey needs credential normalization")));
}

#[test]
fn settings_storage_load_discovers_mdd_for_legacy_mdx_entries_without_saved_paths() {
    let temp = TempDir::new("settings-storage-mdd-discovery");
    let mdx_path = temp.path().join("Legacy Dict.mdx");
    let mdd_path = temp.path().join("Legacy Dict.mdd");
    let numbered_mdd_path = temp.path().join("Legacy Dict.2.MDD");
    let ignored_mdd_path = temp.path().join("Legacy Dict.assets.mdd");
    fs::write(&mdx_path, b"mdx").expect("MDX file should be created");
    fs::write(&mdd_path, b"mdd").expect("MDD file should be created");
    fs::write(&numbered_mdd_path, b"mdd2").expect("numbered MDD file should be created");
    fs::write(&ignored_mdd_path, b"ignored").expect("ignored MDD file should be created");
    let json = json!({
        "ImportedMdxDictionaries": [{
            "ServiceId": "mdx::legacy-dict",
            "DisplayName": "Legacy Dict",
            "FilePath": mdx_path.to_string_lossy(),
            "IsEncrypted": false
        }]
    })
    .to_string();

    let settings = load_settings_json_with_machine_id(&json, "stable-machine-id")
        .expect("settings load")
        .settings;

    assert_eq!(settings.imported_mdx_dictionaries.len(), 1);
    assert_eq!(
        settings.imported_mdx_dictionaries[0].mdd_file_paths,
        vec![
            mdd_path.to_string_lossy().into_owned(),
            numbered_mdd_path.to_string_lossy().into_owned()
        ]
    );
}

#[test]
fn settings_storage_save_never_writes_runtime_only_worker_isolation_keys() {
    let settings = SettingsState::default();

    let json = save_settings_json(&settings).expect("settings should serialize");
    let root: Value = serde_json::from_str(&json).unwrap();

    for key in ["UseLongDocWorker", "UseLocalAiWorker", "UseOcrWorker"] {
        assert!(
            root.get(key).is_none(),
            "rs settings storage must not reintroduce retained worker isolation key {key}"
        );
        assert!(
            !json.contains(key),
            "serialized rs settings must not contain retained worker isolation key {key}"
        );
    }
}

#[test]
fn settings_storage_load_file_persists_runtime_only_worker_key_cleanup() {
    let temp = TempDir::new("settings-storage-worker-cleanup");
    let path = temp.path().join("settings.json");
    fs::write(
        &path,
        r#"{
  "UseLongDocWorker": true,
  "UseLocalAiWorker": false,
  "UseOcrWorker": true,
  "MainWindowEnabledServices": ["openvino-local-ai"],
  "MainWindowServiceEnabledQuery": { "openvino-local-ai": false }
}"#,
    )
    .unwrap();

    let loaded = load_settings_file(&path).expect("settings load").settings;
    assert_eq!(loaded.local_ai_provider, "OpenVINO");
    assert!(loaded
        .main_window_services
        .iter()
        .any(|service| service.service_id == "windows-local-ai"
            && service.enabled
            && !service.enabled_query));

    let persisted = fs::read_to_string(&path).unwrap();
    let root: Value = serde_json::from_str(&persisted).unwrap();
    for key in ["UseLongDocWorker", "UseLocalAiWorker", "UseOcrWorker"] {
        assert!(
            root.get(key).is_none(),
            "loading rs settings should persist cleanup for retained worker key {key}"
        );
        assert!(
            !persisted.contains(key),
            "settings file should not keep stale retained worker key {key} after load"
        );
    }
    assert_array_contains(&root["MainWindowEnabledServices"], "windows-local-ai");
    assert_array_not_contains(&root["MainWindowEnabledServices"], "openvino-local-ai");
}

#[cfg(windows)]
#[test]
fn settings_storage_roundtrips_file_with_decrypted_runtime_state() {
    let temp = TempDir::new("settings-storage-roundtrip");
    let path = temp.path().join("settings.json");
    let mut settings = SettingsState::default();
    settings.open_ai_api_key = "sk-file-roundtrip".to_string();
    settings.proxy_enabled = true;
    settings.proxy_url = "http://proxy.example.test:8080".to_string();
    settings.selected_languages = vec!["en".to_string(), "fr".to_string()];

    save_settings_file(&path, &settings).expect("settings save");
    let raw = fs::read_to_string(&path).unwrap();
    assert!(!raw.contains("sk-file-roundtrip"));

    let loaded = load_settings_file(&path).expect("settings load").settings;
    assert_eq!(loaded.open_ai_api_key, "sk-file-roundtrip");
    assert!(loaded.proxy_enabled);
    assert_eq!(loaded.proxy_url, "http://proxy.example.test:8080");
    assert_eq!(loaded.selected_languages, vec!["en", "fr"]);
}

#[cfg(windows)]
fn assert_protected(root: &Value, key: &str, plaintext: &str) {
    let value = root[key].as_str().unwrap();
    assert!(value.starts_with("edcred1:user:"));
    assert!(!value.contains(plaintext));
}

fn assert_array_contains(value: &Value, expected: &str) {
    assert!(
        value
            .as_array()
            .unwrap()
            .iter()
            .any(|item| item.as_str() == Some(expected)),
        "expected array to contain {expected}: {value}"
    );
}

fn assert_array_not_contains(value: &Value, expected: &str) {
    assert!(
        !value
            .as_array()
            .unwrap()
            .iter()
            .any(|item| item.as_str() == Some(expected)),
        "expected array not to contain {expected}: {value}"
    );
}

struct TempDir {
    path: PathBuf,
}

impl TempDir {
    fn new(label: &str) -> Self {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path =
            std::env::temp_dir().join(format!("easydict-{label}-{}-{unique}", std::process::id()));
        fs::create_dir_all(&path).unwrap();
        Self { path }
    }

    fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}

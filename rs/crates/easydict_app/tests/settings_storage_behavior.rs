use easydict_app::{
    default_settings_storage_path, load_settings_file, load_settings_json_with_machine_id,
    protect_credential_legacy, save_settings_file, save_settings_json, ImportedMdxDictionary,
    SettingsState,
};
use serde_json::{json, Value};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};
use win_fluent::prelude::ThemeMode;

#[cfg(windows)]
use base64::{engine::general_purpose, Engine as _};
#[cfg(windows)]
use easydict_app::{protect_credential, try_unprotect_credential};
#[cfg(windows)]
use easydict_windows_credentials::{protect_data, unprotect_data, DataProtectionScope};

static ENV_LOCK: Mutex<()> = Mutex::new(());

#[test]
fn settings_storage_default_path_uses_rs_specific_directory() {
    let _env_lock = ENV_LOCK.lock().unwrap();
    let temp = TempDir::new("settings-storage-default-path");
    let _settings_guard = EnvVarGuard::set("EASYDICT_SETTINGS_DIR", String::new());
    let _local_app_data_guard =
        EnvVarGuard::set("LOCALAPPDATA", temp.path().to_string_lossy().to_string());

    assert_eq!(
        default_settings_storage_path(),
        temp.path().join("EasydictRs").join("settings.json")
    );
}

#[test]
fn settings_storage_default_path_honors_settings_directory_env() {
    let _env_lock = ENV_LOCK.lock().unwrap();
    let temp = TempDir::new("settings-storage-env-path");
    let settings_dir = temp.path().join("configured-settings");
    let _guard = EnvVarGuard::set(
        "EASYDICT_SETTINGS_DIR",
        settings_dir.to_string_lossy().to_string(),
    );

    assert_eq!(
        default_settings_storage_path(),
        settings_dir.join("settings.json")
    );
}

#[test]
fn settings_storage_load_file_reports_machine_id_persistence_warnings() {
    let _env_lock = ENV_LOCK.lock().unwrap();
    let temp = TempDir::new("settings-storage-machine-id-warning");
    let blocked_settings_dir = temp.path().join("blocked-settings-dir");
    let settings_path = temp.path().join("settings.json");
    fs::write(&blocked_settings_dir, "not a directory").unwrap();
    fs::write(&settings_path, "{}").unwrap();
    let _guard = EnvVarGuard::set(
        "EASYDICT_SETTINGS_DIR",
        blocked_settings_dir.to_string_lossy().to_string(),
    );

    let result =
        load_settings_file(&settings_path).expect("settings load should remain best-effort");

    assert!(
        result
            .warnings
            .iter()
            .any(|warning| warning.contains("Could not create machine-id directory")),
        "machine-id directory warning should be surfaced: {:?}",
        result.warnings
    );
    assert!(
        result
            .warnings
            .iter()
            .any(|warning| warning.contains("Could not persist machine-id")
                || warning.contains("Could not copy legacy machine-id")),
        "machine-id write warning should be surfaced: {:?}",
        result.warnings
    );
}

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
fn settings_storage_normalizes_local_ai_provider_aliases() {
    for (stored, expected) in [
        ("windows-ai", "WindowsAI"),
        ("phi_silica", "WindowsAI"),
        ("foundry-local", "FoundryLocal"),
        ("local-ai", "FoundryLocal"),
        ("open_vino", "OpenVINO"),
        ("unknown-legacy-worker", "Auto"),
    ] {
        let result = load_settings_json_with_machine_id(
            &format!(r#"{{ "LocalAIProvider": "{stored}" }}"#),
            "stable-machine-id",
        )
        .expect("settings load");
        assert_eq!(
            result.settings.local_ai_provider, expected,
            "stored LocalAIProvider alias {stored:?} should normalize"
        );
    }

    let mut settings = SettingsState::default();
    settings.local_ai_provider = "open_vino".to_string();
    let json = save_settings_json(&settings).expect("settings save");
    let root: Value = serde_json::from_str(&json).expect("saved settings json");
    assert_eq!(root["LocalAIProvider"], "OpenVINO");
}

#[test]
fn settings_storage_load_file_persists_local_ai_provider_alias_cleanup() {
    let temp = TempDir::new("settings-storage-local-ai-provider-cleanup");
    let path = temp.path().join("settings.json");
    fs::write(
        &path,
        r#"{
  "LocalAIProvider": "foundry-local",
  "FoundryLocalEndpoint": "http://127.0.0.1:5273/v1"
}"#,
    )
    .unwrap();

    let loaded = load_settings_file(&path).expect("settings load").settings;
    assert_eq!(loaded.local_ai_provider, "FoundryLocal");

    let persisted = fs::read_to_string(&path).unwrap();
    let root: Value = serde_json::from_str(&persisted).unwrap();
    assert_eq!(root["LocalAIProvider"], "FoundryLocal");
    assert!(
        !persisted.contains("foundry-local"),
        "settings file should not keep stale LocalAIProvider alias after load"
    );
}

#[cfg(windows)]
#[test]
fn settings_storage_load_file_normalizes_pending_sensitive_values_without_rewriting_stable_dpapi() {
    let _env_lock = ENV_LOCK.lock().unwrap();
    let temp = TempDir::new("settings-storage-sensitive-normalization");
    let _guard = EnvVarGuard::set(
        "EASYDICT_SETTINGS_DIR",
        temp.path().to_string_lossy().to_string(),
    );
    let machine_id = easydict_app::get_or_create_persisted_machine_id(temp.path());
    let already_protected = protect_credential("sk-already-protected").unwrap();
    let nested_protected = protect_credential(&protect_credential("sk-nested-protected").unwrap())
        .expect("nested credential should protect");
    let legacy_protected = protect_credential_legacy("deepl-legacy", &machine_id).unwrap();
    let path = temp.path().join("settings.json");
    fs::write(
        &path,
        format!(
            r#"{{
  "OpenAIApiKey": "{already_protected}",
  "CustomOpenAIApiKey": "{nested_protected}",
  "DeepLApiKey": "{legacy_protected}",
  "OcrApiKey": "plain-ocr-secret",
  "UseLocalAiWorker": true
}}"#
        ),
    )
    .unwrap();

    let result = load_settings_file(&path).expect("settings file should load");

    assert_eq!(result.settings.open_ai_api_key, "sk-already-protected");
    assert_eq!(result.settings.deepl_api_key, "deepl-legacy");
    assert_eq!(result.settings.ocr_api_key, "plain-ocr-secret");
    let custom_provider = result
        .settings
        .service_provider_settings
        .iter()
        .find(|setting| setting.service_id == "custom-openai")
        .unwrap();
    assert_eq!(custom_provider.api_key, "sk-nested-protected");
    assert!(
        !result
            .warnings
            .iter()
            .any(|warning| warning.contains("needs credential normalization")),
        "load_settings_file should return the post-normalization state: {:?}",
        result.warnings
    );

    let persisted = fs::read_to_string(&path).unwrap();
    let root: Value = serde_json::from_str(&persisted).unwrap();
    assert_eq!(
        root["OpenAIApiKey"].as_str(),
        Some(already_protected.as_str())
    );
    assert_ne!(
        root["CustomOpenAIApiKey"].as_str(),
        Some(nested_protected.as_str())
    );
    assert_ne!(
        root["DeepLApiKey"].as_str(),
        Some(legacy_protected.as_str())
    );
    assert_ne!(root["OcrApiKey"].as_str(), Some("plain-ocr-secret"));
    for key in ["CustomOpenAIApiKey", "DeepLApiKey", "OcrApiKey"] {
        assert!(root[key].as_str().unwrap().starts_with("edcred1:user:"));
    }
    assert_eq!(
        try_unprotect_credential(root["CustomOpenAIApiKey"].as_str().unwrap()).as_deref(),
        Some("sk-nested-protected")
    );
    assert_eq!(
        try_unprotect_credential(root["DeepLApiKey"].as_str().unwrap()).as_deref(),
        Some("deepl-legacy")
    );
    assert_eq!(
        try_unprotect_credential(root["OcrApiKey"].as_str().unwrap()).as_deref(),
        Some("plain-ocr-secret")
    );
    assert!(root.get("UseLocalAiWorker").is_none());
    for plaintext in ["sk-nested-protected", "deepl-legacy", "plain-ocr-secret"] {
        assert!(
            !persisted.contains(plaintext),
            "settings file should not retain plaintext {plaintext}"
        );
    }
}

#[cfg(windows)]
#[test]
fn settings_storage_load_file_rewrites_legacy_winui_dpapi_credentials_to_rs_entropy() {
    let _env_lock = ENV_LOCK.lock().unwrap();
    let temp = TempDir::new("settings-storage-legacy-winui-dpapi-normalization");
    let _guard = EnvVarGuard::set(
        "EASYDICT_SETTINGS_DIR",
        temp.path().to_string_lossy().to_string(),
    );
    let legacy_dpapi = legacy_winui_dpapi_value("deepl-legacy");
    let path = temp.path().join("settings.json");
    fs::write(&path, format!(r#"{{ "DeepLApiKey": "{legacy_dpapi}" }}"#)).unwrap();

    let result = load_settings_file(&path).expect("settings file should load");

    assert_eq!(result.settings.deepl_api_key, "deepl-legacy");
    let persisted = fs::read_to_string(&path).unwrap();
    let root: Value = serde_json::from_str(&persisted).unwrap();
    let rewritten = root["DeepLApiKey"].as_str().unwrap();
    assert_ne!(rewritten, legacy_dpapi);
    assert!(rewritten.starts_with("edcred1:user:"));
    assert_eq!(
        try_unprotect_credential(rewritten).as_deref(),
        Some("deepl-legacy")
    );

    let payload = rewritten
        .strip_prefix("edcred1:user:")
        .expect("rewritten DPAPI payload should keep current-user prefix");
    let protected_bytes = general_purpose::STANDARD.decode(payload).unwrap();
    assert!(
        unprotect_data(
            &protected_bytes,
            b"Easydict.WinUI.LocalSettingsCredential.v2:user",
            DataProtectionScope::CurrentUser,
        )
        .is_err(),
        "settings normalization should rewrite legacy WinUI DPAPI entropy to the rs purpose"
    );
}

#[cfg(windows)]
#[test]
fn settings_storage_load_file_uses_legacy_machine_id_fallback_for_migrated_rs_settings() {
    let _env_lock = ENV_LOCK.lock().unwrap();
    let temp = TempDir::new("settings-storage-legacy-machine-id-fallback");
    let _settings_guard = EnvVarGuard::set("EASYDICT_SETTINGS_DIR", String::new());
    let _local_app_data_guard =
        EnvVarGuard::set("LOCALAPPDATA", temp.path().to_string_lossy().to_string());
    let legacy_dir = temp.path().join("Easydict");
    let rs_dir = temp.path().join("EasydictRs");
    fs::create_dir_all(&legacy_dir).unwrap();
    fs::create_dir_all(&rs_dir).unwrap();
    fs::write(
        legacy_dir.join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME),
        "legacy-machine-id",
    )
    .unwrap();
    let legacy_secret = protect_credential_legacy("deepl-legacy", "legacy-machine-id").unwrap();
    let path = rs_dir.join("settings.json");
    fs::write(&path, format!(r#"{{ "DeepLApiKey": "{legacy_secret}" }}"#)).unwrap();

    let result = load_settings_file(&path).expect("settings file should load");

    assert_eq!(result.settings.deepl_api_key, "deepl-legacy");
    assert_eq!(
        fs::read_to_string(rs_dir.join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME))
            .unwrap(),
        "legacy-machine-id"
    );
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
fn settings_storage_load_discovers_real_corpus_mdd_from_env_for_legacy_mdx_entry() {
    let Some(mdx_path) = real_corpus_path("RS_MDICT_TEST_MDX") else {
        return;
    };
    let Some(mdd_path) = real_corpus_path("RS_MDICT_TEST_MDD") else {
        return;
    };
    let json = json!({
        "ImportedMdxDictionaries": [{
            "ServiceId": "mdx::collins-cobuild-english-usage",
            "DisplayName": "Collins COBUILD English Usage",
            "FilePath": mdx_path.to_string_lossy(),
            "IsEncrypted": true
        }]
    })
    .to_string();

    let settings = load_settings_json_with_machine_id(&json, "stable-machine-id")
        .expect("settings load")
        .settings;

    assert_eq!(settings.imported_mdx_dictionaries.len(), 1);
    assert_eq!(
        settings.imported_mdx_dictionaries[0].mdd_file_paths,
        vec![mdd_path.to_string_lossy().into_owned()]
    );
}

#[test]
fn settings_storage_load_merges_discovered_companion_mdds_with_saved_paths() {
    let temp = TempDir::new("settings-storage-mdd-merge");
    let mdx_path = temp.path().join("Demo Dict.mdx");
    let saved_mdd_path = temp.path().join("Demo Dict.mdd");
    let discovered_mdd_path = temp.path().join("Demo Dict.4.mdd");
    let manual_mdd_path = temp.path().join("Manual Audio.mdd");
    fs::write(&mdx_path, b"mdx").expect("MDX file should be created");
    fs::write(&saved_mdd_path, b"saved mdd").expect("saved MDD file should be created");
    fs::write(&discovered_mdd_path, b"new mdd").expect("numbered MDD file should be created");
    fs::write(&manual_mdd_path, b"manual mdd").expect("manual MDD file should be created");

    let json = json!({
        "ImportedMdxDictionaries": [{
            "ServiceId": "mdx::demo-dict",
            "DisplayName": "Demo Dict",
            "FilePath": mdx_path.to_string_lossy(),
            "IsEncrypted": false,
            "MddFilePaths": [
                saved_mdd_path.to_string_lossy(),
                manual_mdd_path.to_string_lossy()
            ]
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
            saved_mdd_path.to_string_lossy().into_owned(),
            manual_mdd_path.to_string_lossy().into_owned(),
            discovered_mdd_path.to_string_lossy().into_owned()
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

#[test]
fn settings_storage_load_file_migrates_legacy_foundry_local_service() {
    let temp = TempDir::new("settings-storage-foundry-local-migration");
    let path = temp.path().join("settings.json");
    fs::write(
        &path,
        r#"{
  "MainWindowEnabledServices": ["foundry-local", "custom-openai"],
  "MainWindowServiceEnabledQuery": { "foundry-local": false, "custom-openai": true }
}"#,
    )
    .unwrap();

    let loaded = load_settings_file(&path).expect("settings load").settings;
    assert_eq!(loaded.local_ai_provider, "FoundryLocal");
    assert!(loaded
        .main_window_services
        .iter()
        .any(|service| service.service_id == "windows-local-ai"
            && service.enabled
            && !service.enabled_query));
    assert!(loaded
        .main_window_services
        .iter()
        .any(|service| service.service_id == "custom-openai"
            && service.enabled
            && service.enabled_query));

    let persisted = fs::read_to_string(&path).unwrap();
    let root: Value = serde_json::from_str(&persisted).unwrap();
    assert_eq!(root["LocalAIProvider"], "FoundryLocal");
    assert_array_contains(&root["MainWindowEnabledServices"], "windows-local-ai");
    assert_array_contains(&root["MainWindowEnabledServices"], "custom-openai");
    assert_array_not_contains(&root["MainWindowEnabledServices"], "foundry-local");
    assert_eq!(
        root["MainWindowServiceEnabledQuery"]["windows-local-ai"],
        false
    );
    assert!(root["MainWindowServiceEnabledQuery"]
        .get("foundry-local")
        .is_none());
}

#[test]
fn settings_storage_load_file_merges_multiple_legacy_local_ai_ids_without_guessing_provider() {
    let temp = TempDir::new("settings-storage-local-ai-legacy-conflict");
    let path = temp.path().join("settings.json");
    fs::write(
        &path,
        r#"{
  "MainWindowEnabledServices": ["openvino-local-ai", "foundry-local"],
  "MainWindowServiceEnabledQuery": {
    "openvino-local-ai": false,
    "foundry-local": true
  }
}"#,
    )
    .unwrap();

    let loaded = load_settings_file(&path).expect("settings load").settings;
    assert_eq!(
        loaded.local_ai_provider, "Auto",
        "conflicting legacy LocalAI service ids should not force a provider preference"
    );
    assert!(loaded
        .main_window_services
        .iter()
        .any(|service| service.service_id == "windows-local-ai"
            && service.enabled
            && !service.enabled_query));

    let persisted = fs::read_to_string(&path).unwrap();
    let root: Value = serde_json::from_str(&persisted).unwrap();
    assert!(root.get("LocalAIProvider").is_none());
    assert_array_contains(&root["MainWindowEnabledServices"], "windows-local-ai");
    assert_array_not_contains(&root["MainWindowEnabledServices"], "openvino-local-ai");
    assert_array_not_contains(&root["MainWindowEnabledServices"], "foundry-local");
    assert_eq!(
        root["MainWindowServiceEnabledQuery"]["windows-local-ai"],
        false
    );
    assert!(root["MainWindowServiceEnabledQuery"]
        .get("openvino-local-ai")
        .is_none());
    assert!(root["MainWindowServiceEnabledQuery"]
        .get("foundry-local")
        .is_none());
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

fn real_corpus_path(env_name: &str) -> Option<PathBuf> {
    match std::env::var(env_name) {
        Ok(path) if !path.trim().is_empty() => Some(PathBuf::from(path)),
        _ => {
            eprintln!("Skipping real-corpus test; set {env_name} to a local MDX/MDD file path");
            None
        }
    }
}

#[cfg(windows)]
fn legacy_winui_dpapi_value(plaintext: &str) -> String {
    let protected_bytes = protect_data(
        plaintext.as_bytes(),
        b"Easydict.WinUI.LocalSettingsCredential.v2:user",
        DataProtectionScope::CurrentUser,
    )
    .expect("legacy WinUI DPAPI test value should protect");

    format!(
        "edcred1:user:{}",
        general_purpose::STANDARD.encode(protected_bytes)
    )
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

struct EnvVarGuard {
    key: &'static str,
    previous: Option<String>,
}

impl EnvVarGuard {
    fn set(key: &'static str, value: String) -> Self {
        let previous = std::env::var(key).ok();
        std::env::set_var(key, value);
        Self { key, previous }
    }
}

impl Drop for EnvVarGuard {
    fn drop(&mut self) {
        if let Some(previous) = &self.previous {
            std::env::set_var(self.key, previous);
        } else {
            std::env::remove_var(self.key);
        }
    }
}

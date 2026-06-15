use easydict_app::{
    default_local_dictionary_index_root, LocalDictionaryIndexDescriptor,
    LocalDictionaryIndexManifest, LocalDictionaryIndexService, CURRENT_INDEX_FORMAT_VERSION,
    DEFAULT_NORMALIZATION_ID, INDEX_FILE_NAME, MANIFEST_FILE_NAME,
};
use std::fs;
use std::path::PathBuf;
use std::sync::{Arc, Barrier, Mutex};
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};

static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

#[test]
fn native_local_dictionary_index_default_root_uses_legacy_cache_for_dotnet_coexistence() {
    let _environment_guard = ENVIRONMENT_LOCK.lock().unwrap();
    let local_app_data = TempDir::new("local-dictionary-index-default-root");
    let _local_app_data_guard = EnvVarGuard::set("LOCALAPPDATA", local_app_data.path_string());

    let expected = local_app_data.path.join("Easydict").join("mdx_index");
    assert_eq!(default_local_dictionary_index_root(), expected);

    let service = LocalDictionaryIndexService::new().unwrap();
    assert_eq!(service.index_root(), expected.as_path());
    assert!(expected.exists());
    assert!(!local_app_data
        .path
        .join("EasydictRs")
        .join("mdx_index")
        .exists());
}

#[test]
fn native_local_dictionary_index_builds_index_and_manifest() {
    let temp = TempDir::new("local-dictionary-index-build");
    let source_path = temp.source_file("dict-a.mdx", "seed");
    let dictionary = descriptor("mdx::a", "Dictionary A", &source_path);
    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();

    service
        .ensure_index_from_keys(&dictionary, ["apple", "application"])
        .unwrap();

    let folder_path = temp.path.join("mdx%3A%3Aa");
    let index_path = folder_path.join(INDEX_FILE_NAME);
    let manifest_path = folder_path.join(MANIFEST_FILE_NAME);
    assert!(index_path.exists());
    assert!(manifest_path.exists());

    let manifest: LocalDictionaryIndexManifest =
        serde_json::from_str(&fs::read_to_string(manifest_path).unwrap()).unwrap();
    assert_eq!(manifest.service_id, dictionary.service_id);
    assert_eq!(manifest.source_path, dictionary.file_path);
    assert_eq!(manifest.entry_count, 2);
    assert_eq!(manifest.index_format_version, CURRENT_INDEX_FORMAT_VERSION);
    assert_eq!(manifest.normalization_id, DEFAULT_NORMALIZATION_ID);

    let results = service.complete("app", &[dictionary.service_id.as_str()], 10);
    assert_eq!(
        results
            .iter()
            .map(|item| item.key.as_str())
            .collect::<Vec<_>>(),
        ["apple", "application"]
    );
    assert!(results
        .iter()
        .all(|item| item.dict_display_name == dictionary.display_name));
}

#[test]
fn native_local_dictionary_index_skips_rebuild_when_fingerprint_matches() {
    let temp = TempDir::new("local-dictionary-index-skip");
    let source_path = temp.source_file("dict-skip.mdx", "seed");
    let dictionary = descriptor("mdx::skip", "Dictionary Skip", &source_path);
    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();

    service
        .ensure_index_from_keys(&dictionary, ["apple"])
        .unwrap();
    service
        .ensure_index_from_keys(&dictionary, ["apricot"])
        .unwrap();

    let results = service.complete("ap", &[dictionary.service_id.as_str()], 10);
    assert_eq!(
        results
            .iter()
            .map(|item| item.key.as_str())
            .collect::<Vec<_>>(),
        ["apple"]
    );
}

#[test]
fn native_local_dictionary_index_reuses_csharp_pascalcase_manifest_when_fingerprint_matches() {
    let temp = TempDir::new("local-dictionary-index-csharp-manifest");
    let source_path = temp.source_file("dict-csharp.mdx", "seed");
    let dictionary = descriptor("mdx::csharp", "Dictionary CSharp", &source_path);
    let mut builder = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    builder
        .ensure_index_from_keys(&dictionary, ["apple", "application"])
        .unwrap();

    let folder_path = temp.path.join("mdx%3A%3Acsharp");
    let manifest_path = folder_path.join(MANIFEST_FILE_NAME);
    let manifest: LocalDictionaryIndexManifest =
        serde_json::from_str(&fs::read_to_string(&manifest_path).unwrap()).unwrap();
    fs::write(&manifest_path, csharp_pascalcase_manifest_json(&manifest)).unwrap();

    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    service
        .ensure_index_with_key_loader(&dictionary, true, || {
            Err::<Vec<String>, _>("loader should not be called when fingerprint matches")
        })
        .unwrap();

    let results = service.complete("app", &[dictionary.service_id.as_str()], 10);
    assert_eq!(
        results
            .iter()
            .map(|item| item.key.as_str())
            .collect::<Vec<_>>(),
        ["apple", "application"]
    );
    assert!(results
        .iter()
        .all(|item| item.dict_display_name == dictionary.display_name));
}

#[test]
fn native_local_dictionary_index_rebuilds_when_source_fingerprint_changes() {
    let temp = TempDir::new("local-dictionary-index-rebuild");
    let source_path = temp.source_file("dict-rebuild.mdx", "seed");
    let dictionary = descriptor("mdx::rebuild", "Dictionary Rebuild", &source_path);
    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();

    service
        .ensure_index_from_keys(&dictionary, ["apple"])
        .unwrap();
    fs::write(&source_path, "seed changed").unwrap();
    service
        .ensure_index_from_keys(&dictionary, ["apple", "apricot"])
        .unwrap();

    let results = service.complete("ap", &[dictionary.service_id.as_str()], 10);
    assert_eq!(
        results
            .iter()
            .map(|item| item.key.as_str())
            .collect::<Vec<_>>(),
        ["apple", "apricot"]
    );
}

#[test]
fn native_local_dictionary_index_skips_unqueryable_encrypted_dictionary_until_keys_are_available() {
    let temp = TempDir::new("local-dictionary-index-encrypted");
    let source_path = temp.source_file("dict-encrypted.mdx", "seed");
    let dictionary = descriptor("mdx::encrypted", "Dictionary Encrypted", &source_path)
        .encrypted_without_credentials();
    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();

    service
        .ensure_index(&dictionary, false, ["apple", "apartment"])
        .unwrap();

    assert!(!temp.path.join("mdx%3A%3Aencrypted").exists());
    assert!(service
        .complete("ap", &[dictionary.service_id.as_str()], 10)
        .is_empty());

    service
        .ensure_index(&dictionary, true, ["apple", "apartment"])
        .unwrap();
    let results = service.complete("ap", &[dictionary.service_id.as_str()], 10);
    assert_eq!(
        results
            .iter()
            .map(|item| item.key.as_str())
            .collect::<Vec<_>>(),
        ["apartment", "apple"]
    );
}

#[test]
fn native_local_dictionary_index_registers_existing_index_without_reopening_dictionary_source() {
    let temp = TempDir::new("local-dictionary-index-register");
    let source_path = temp.source_file("dict-existing.mdx", "seed");
    let dictionary = descriptor("mdx::existing", "Existing Dictionary", &source_path);
    let mut builder = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    builder
        .ensure_index_from_keys(&dictionary, ["apple", "application"])
        .unwrap();

    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    service.register_descriptor(&dictionary);

    let results = service.complete("app", &[dictionary.service_id.as_str()], 10);
    assert_eq!(
        results
            .iter()
            .map(|item| item.key.as_str())
            .collect::<Vec<_>>(),
        ["apple", "application"]
    );
    assert!(results
        .iter()
        .all(|item| item.dict_display_name == dictionary.display_name));
}

#[test]
fn native_local_dictionary_index_blocks_registered_encrypted_dictionary_without_credentials() {
    let temp = TempDir::new("local-dictionary-index-block-encrypted");
    let source_path = temp.source_file("dict-existing-encrypted.mdx", "seed");
    let dictionary = descriptor(
        "mdx::existing-encrypted",
        "Encrypted Existing Dictionary",
        &source_path,
    );
    let mut builder = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    builder
        .ensure_index_from_keys(&dictionary, ["apple", "application"])
        .unwrap();

    let encrypted_dictionary = dictionary.encrypted_without_credentials();
    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    service.register_descriptor(&encrypted_dictionary);

    assert!(service
        .complete("app", &[encrypted_dictionary.service_id.as_str()], 10)
        .is_empty());
}

#[test]
fn native_local_dictionary_index_merges_results_in_requested_order_and_deduplicates_keys() {
    let temp = TempDir::new("local-dictionary-index-dedupe");
    let source_a = temp.source_file("dict-a.mdx", "a");
    let source_b = temp.source_file("dict-b.mdx", "b");
    let dictionary_a = descriptor("mdx::d:a", "Dictionary A", &source_a);
    let dictionary_b = descriptor("mdx::d:b", "Dictionary B", &source_b);
    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    service
        .ensure_index_from_keys(&dictionary_a, ["apple", "application", "apply"])
        .unwrap();
    service
        .ensure_index_from_keys(&dictionary_b, ["Apple", "appendix"])
        .unwrap();

    let results = service.complete(
        "app",
        &[
            dictionary_b.service_id.as_str(),
            dictionary_a.service_id.as_str(),
        ],
        10,
    );

    assert_eq!(
        results
            .iter()
            .map(|item| item.key.as_str())
            .collect::<Vec<_>>(),
        ["appendix", "Apple", "application", "apply"]
    );
    assert_eq!(results[0].dict_service_id, dictionary_b.service_id);
    assert_eq!(
        results
            .iter()
            .find(|item| item.key.eq_ignore_ascii_case("apple"))
            .unwrap()
            .dict_service_id,
        dictionary_b.service_id
    );
}

#[test]
fn native_local_dictionary_index_matches_wildcards_across_multiple_indexes() {
    let temp = TempDir::new("local-dictionary-index-match");
    let source_a = temp.source_file("dict-match-a.mdx", "a");
    let source_b = temp.source_file("dict-match-b.mdx", "b");
    let dictionary_a = descriptor("mdx::e:a", "Dictionary A", &source_a);
    let dictionary_b = descriptor("mdx::e:b", "Dictionary B", &source_b);
    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    service
        .ensure_index_from_keys(&dictionary_a, ["tealight", "teapot"])
        .unwrap();
    service
        .ensure_index_from_keys(&dictionary_b, ["teatime", "teatray"])
        .unwrap();

    let results = service.match_pattern(
        "tea*",
        &[
            dictionary_a.service_id.as_str(),
            dictionary_b.service_id.as_str(),
        ],
        10,
    );

    assert_eq!(
        results
            .iter()
            .map(|item| item.key.as_str())
            .collect::<Vec<_>>(),
        ["tealight", "teapot", "teatime", "teatray"]
    );
    assert_eq!(results[0].dict_service_id, dictionary_a.service_id);
    assert_eq!(results[2].dict_service_id, dictionary_b.service_id);
}

#[test]
fn native_local_dictionary_index_skips_corrupt_manifest_or_index_without_crashing() {
    let temp = TempDir::new("local-dictionary-index-corrupt");
    let source_path = temp.source_file("dict-corrupt.mdx", "seed");
    let dictionary = descriptor("mdx::corrupt", "Dictionary Corrupt", &source_path);
    let mut builder = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    builder
        .ensure_index_from_keys(&dictionary, ["apple", "application"])
        .unwrap();

    let folder_path = temp.path.join("mdx%3A%3Acorrupt");
    fs::write(folder_path.join(INDEX_FILE_NAME), [1, 2, 3, 4]).unwrap();
    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    service.register_descriptor(&dictionary);
    assert!(service
        .complete("app", &[dictionary.service_id.as_str()], 10)
        .is_empty());

    fs::write(folder_path.join(MANIFEST_FILE_NAME), "{ not json").unwrap();
    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    service.register_descriptor(&dictionary);
    assert!(service
        .complete("app", &[dictionary.service_id.as_str()], 10)
        .is_empty());
}

#[test]
fn native_local_dictionary_index_remove_dictionary_deletes_index_folder() {
    let temp = TempDir::new("local-dictionary-index-remove");
    let source_path = temp.source_file("dict-remove.mdx", "seed");
    let dictionary = descriptor("mdx::remove", "Dictionary Remove", &source_path);
    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    service
        .ensure_index_from_keys(&dictionary, ["apple"])
        .unwrap();
    let folder_path = temp.path.join("mdx%3A%3Aremove");
    assert!(folder_path.exists());

    service.remove_dictionary(&dictionary.service_id, true);

    assert!(!folder_path.exists());
    assert!(service
        .complete("app", &[dictionary.service_id.as_str()], 10)
        .is_empty());
}

#[test]
fn native_local_dictionary_index_concurrent_builds_leave_valid_index() {
    let temp = TempDir::new("local-dictionary-index-concurrent");
    let source_path = temp.source_file("dict-concurrent.mdx", "seed");
    let dictionary = descriptor("mdx::concurrent", "Dictionary Concurrent", &source_path);
    let barrier = Arc::new(Barrier::new(2));

    let handles = [
        vec!["apple".to_string(), "application".to_string()],
        vec!["apricot".to_string(), "apartment".to_string()],
    ]
    .into_iter()
    .map(|keys| {
        let root = temp.path.clone();
        let dictionary = dictionary.clone();
        let barrier = Arc::clone(&barrier);
        thread::spawn(move || {
            let mut service = LocalDictionaryIndexService::with_index_root(root).unwrap();
            service
                .ensure_index_with_key_loader(&dictionary, true, || {
                    barrier.wait();
                    Ok::<_, String>(keys)
                })
                .unwrap();
        })
    })
    .collect::<Vec<_>>();

    for handle in handles {
        handle.join().unwrap();
    }

    let mut service = LocalDictionaryIndexService::with_index_root(temp.path.clone()).unwrap();
    service.register_descriptor(&dictionary);
    let results = service.complete("ap", &[dictionary.service_id.as_str()], 10);
    assert!(!results.is_empty());
    assert!(results
        .iter()
        .all(|item| item.dict_display_name == dictionary.display_name));

    let folder_path = temp.path.join("mdx%3A%3Aconcurrent");
    let leftovers = fs::read_dir(folder_path)
        .unwrap()
        .filter_map(Result::ok)
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .filter(|name| name.contains(".tmp."))
        .collect::<Vec<_>>();
    assert!(leftovers.is_empty(), "leftover temp files: {leftovers:?}");
}

fn descriptor(
    service_id: &str,
    display_name: &str,
    file_path: &str,
) -> LocalDictionaryIndexDescriptor {
    LocalDictionaryIndexDescriptor {
        service_id: service_id.to_string(),
        display_name: display_name.to_string(),
        file_path: file_path.to_string(),
        is_encrypted: false,
        regcode: None,
        email: None,
    }
}

fn csharp_pascalcase_manifest_json(manifest: &LocalDictionaryIndexManifest) -> String {
    format!(
        concat!(
            "{{\n",
            "  \"ServiceId\": {},\n",
            "  \"SourcePath\": {},\n",
            "  \"SourceLastWriteUtc\": {},\n",
            "  \"SourceLength\": {},\n",
            "  \"IndexFormatVersion\": {},\n",
            "  \"NormalizationId\": {},\n",
            "  \"EntryCount\": {}\n",
            "}}"
        ),
        serde_json::to_string(&manifest.service_id).unwrap(),
        serde_json::to_string(&manifest.source_path).unwrap(),
        serde_json::to_string(&manifest.source_last_write_utc).unwrap(),
        manifest.source_length,
        manifest.index_format_version,
        serde_json::to_string(&manifest.normalization_id).unwrap(),
        manifest.entry_count
    )
}

trait EncryptedDescriptor {
    fn encrypted_without_credentials(self) -> Self;
}

impl EncryptedDescriptor for LocalDictionaryIndexDescriptor {
    fn encrypted_without_credentials(mut self) -> Self {
        self.is_encrypted = true;
        self.regcode = None;
        self.email = None;
        self
    }
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

    fn source_file(&self, file_name: &str, content: &str) -> String {
        let path = self.path.join(file_name);
        fs::write(&path, content).unwrap();
        path.to_string_lossy().into_owned()
    }

    fn path_string(&self) -> String {
        self.path.to_string_lossy().into_owned()
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

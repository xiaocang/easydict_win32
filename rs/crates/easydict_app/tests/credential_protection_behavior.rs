use easydict_app::{
    get_or_create_persisted_machine_id, is_protected_credential, protect_credential_legacy,
    try_unprotect_credential_legacy, try_unprotect_credential_with_machine_id,
    unprotect_or_return_plaintext_with_machine_id, CredentialProtectionScope,
    MAX_NESTED_PROTECTED_VALUE_DEPTH,
};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[cfg(windows)]
use easydict_app::{protect_credential, protect_credential_with_scope, try_unprotect_credential};

#[cfg(windows)]
#[test]
fn credential_protection_current_user_scope_roundtrips_without_plaintext_storage() {
    const PLAINTEXT: &str = "sk-test-local-api-key";

    let protected_value = protect_credential(PLAINTEXT).unwrap();

    assert!(protected_value.starts_with("edcred1:user:"));
    assert!(!protected_value.contains(PLAINTEXT));
    assert_eq!(
        try_unprotect_credential(&protected_value).as_deref(),
        Some(PLAINTEXT)
    );
}

#[cfg(windows)]
#[test]
fn credential_protection_local_machine_scope_records_machine_scope() {
    const PLAINTEXT: &str = "sk-test-shared-api-key";

    let protected_value =
        protect_credential_with_scope(PLAINTEXT, CredentialProtectionScope::LocalMachine).unwrap();

    assert!(protected_value.starts_with("edcred1:machine:"));
    assert!(!protected_value.contains(PLAINTEXT));
    assert_eq!(
        try_unprotect_credential(&protected_value).as_deref(),
        Some(PLAINTEXT)
    );
}

#[cfg(windows)]
#[test]
fn credential_protection_tampered_dpapi_value_fails() {
    let protected_value = protect_credential("sk-test-local-api-key").unwrap();
    let tampered = format!("{}AAAA", &protected_value[..protected_value.len() - 4]);

    assert_eq!(try_unprotect_credential(&tampered), None);
}

#[test]
fn credential_protection_only_matches_versioned_values() {
    assert!(!is_protected_credential(Some("plain-old-api-key")));
    assert!(!is_protected_credential(Some("")));
    assert!(!is_protected_credential(None));
    assert!(is_protected_credential(Some("edcred1:user:payload")));
    assert!(is_protected_credential(Some("edloc1:payload")));
}

#[test]
fn credential_protection_legacy_plaintext_requests_migration() {
    let value = unprotect_or_return_plaintext_with_machine_id(
        Some("plain-old-api-key"),
        "stable-machine-id",
    );

    assert_eq!(value.value.as_deref(), Some("plain-old-api-key"));
    assert!(value.needs_migration);
    assert!(!value.decrypt_failed);
}

#[cfg(windows)]
#[test]
fn credential_protection_dpapi_value_returns_plaintext_without_migration() {
    let protected_value = protect_credential("sk-test-local-api-key").unwrap();

    let value =
        unprotect_or_return_plaintext_with_machine_id(Some(&protected_value), "stable-machine-id");

    assert_eq!(value.value.as_deref(), Some("sk-test-local-api-key"));
    assert!(!value.needs_migration);
    assert!(!value.decrypt_failed);
}

#[cfg(windows)]
#[test]
fn credential_protection_nested_dpapi_value_returns_plaintext_and_requests_migration() {
    let inner = protect_credential("sk-test-local-api-key").unwrap();
    let nested = protect_credential(&inner).unwrap();

    let value = unprotect_or_return_plaintext_with_machine_id(Some(&nested), "stable-machine-id");

    assert_eq!(value.value.as_deref(), Some("sk-test-local-api-key"));
    assert!(value.needs_migration);
    assert!(!value.decrypt_failed);
    assert_eq!(
        try_unprotect_credential_with_machine_id(&nested, "stable-machine-id").as_deref(),
        Some("sk-test-local-api-key")
    );
}

#[cfg(windows)]
#[test]
fn credential_protection_too_many_nested_values_fails() {
    let mut nested = "sk-test-local-api-key".to_string();
    for _ in 0..=MAX_NESTED_PROTECTED_VALUE_DEPTH {
        nested = protect_credential(&nested).unwrap();
    }

    let value = unprotect_or_return_plaintext_with_machine_id(Some(&nested), "stable-machine-id");

    assert_eq!(value.value, None);
    assert!(value.decrypt_failed);
}

#[test]
fn credential_protection_legacy_value_roundtrips_and_requests_migration() {
    let protected_value =
        protect_credential_legacy("sk-test-local-api-key", "stable-machine-id").unwrap();

    assert!(protected_value.starts_with("edloc1:"));
    assert_eq!(
        try_unprotect_credential_legacy(&protected_value, "stable-machine-id").as_deref(),
        Some("sk-test-local-api-key")
    );

    let value =
        unprotect_or_return_plaintext_with_machine_id(Some(&protected_value), "stable-machine-id");
    assert_eq!(value.value.as_deref(), Some("sk-test-local-api-key"));
    assert!(value.needs_migration);
    assert!(!value.decrypt_failed);
}

#[test]
fn credential_protection_legacy_value_with_different_machine_id_fails() {
    let protected_value =
        protect_credential_legacy("sk-test-local-api-key", "stable-machine-id").unwrap();

    assert_eq!(
        try_unprotect_credential_legacy(&protected_value, "different-machine-id"),
        None
    );
}

#[test]
fn credential_protection_machine_id_reads_existing_file() {
    let temp = TempDir::new("credential-machine-id-existing");
    let path = temp
        .path()
        .join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME);
    fs::write(path, "persisted-machine-id").unwrap();

    let machine_id = get_or_create_persisted_machine_id(temp.path());

    assert_eq!(machine_id, "persisted-machine-id");
}

#[test]
fn credential_protection_machine_id_migrates_legacy_file() {
    let temp = TempDir::new("credential-machine-id-legacy");
    fs::write(temp.path().join("local-machine-id"), "legacy-machine-id").unwrap();

    let machine_id = get_or_create_persisted_machine_id(temp.path());

    assert_eq!(machine_id, "legacy-machine-id");
    assert_eq!(
        fs::read_to_string(
            temp.path()
                .join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME)
        )
        .unwrap(),
        "legacy-machine-id"
    );
}

#[test]
fn credential_protection_machine_id_creates_file() {
    let temp = TempDir::new("credential-machine-id-create");

    let machine_id = get_or_create_persisted_machine_id(temp.path());

    assert!(!machine_id.trim().is_empty());
    assert_eq!(
        fs::read_to_string(
            temp.path()
                .join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME)
        )
        .unwrap(),
        machine_id
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

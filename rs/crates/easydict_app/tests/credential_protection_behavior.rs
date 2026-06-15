use easydict_app::{
    get_or_create_persisted_machine_id, get_or_create_persisted_machine_id_with_legacy_fallback,
    is_protected_credential, protect_credential_legacy, try_unprotect_credential_legacy,
    try_unprotect_credential_with_machine_id, unprotect_or_return_plaintext_with_machine_id,
    CredentialProtectionScope, MAX_NESTED_PROTECTED_VALUE_DEPTH,
};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[cfg(windows)]
use base64::{engine::general_purpose, Engine as _};
#[cfg(windows)]
use easydict_app::{protect_credential, protect_credential_with_scope, try_unprotect_credential};
#[cfg(windows)]
use easydict_windows_credentials::{protect_data, unprotect_data, DataProtectionScope};

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
fn credential_protection_new_dpapi_values_use_rs_entropy_with_legacy_fallback() {
    const PLAINTEXT: &str = "sk-test-local-api-key";
    let protected_value = protect_credential(PLAINTEXT).unwrap();
    let payload = protected_value
        .strip_prefix("edcred1:user:")
        .expect("current-user DPAPI payload should have expected prefix");
    let protected_bytes = general_purpose::STANDARD.decode(payload).unwrap();

    assert!(
        unprotect_data(
            &protected_bytes,
            b"Easydict.WinUI.LocalSettingsCredential.v2:user",
            DataProtectionScope::CurrentUser,
        )
        .is_err(),
        "new rs credentials should not be decryptable with the legacy WinUI DPAPI purpose"
    );

    let legacy_bytes = protect_data(
        PLAINTEXT.as_bytes(),
        b"Easydict.WinUI.LocalSettingsCredential.v2:user",
        DataProtectionScope::CurrentUser,
    )
    .unwrap();
    let legacy_dpapi_value = format!(
        "edcred1:user:{}",
        general_purpose::STANDARD.encode(legacy_bytes)
    );

    assert_eq!(
        try_unprotect_credential(&legacy_dpapi_value).as_deref(),
        Some(PLAINTEXT)
    );
    let normalized =
        unprotect_or_return_plaintext_with_machine_id(Some(&legacy_dpapi_value), "machine-id");
    assert_eq!(normalized.value.as_deref(), Some(PLAINTEXT));
    assert!(
        normalized.needs_migration,
        "legacy WinUI DPAPI values should be rewritten with the rs purpose on settings load"
    );
    assert!(!normalized.decrypt_failed);
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
fn credential_protection_empty_values_are_neutral() {
    assert_eq!(
        protect_credential_legacy("", "stable-machine-id").unwrap(),
        ""
    );

    for stored_value in [None, Some("")] {
        let value =
            unprotect_or_return_plaintext_with_machine_id(stored_value, "stable-machine-id");

        assert_eq!(value.value, None);
        assert!(!value.needs_migration);
        assert!(!value.decrypt_failed);
    }
}

#[cfg(windows)]
#[test]
fn credential_protection_dpapi_empty_value_is_neutral() {
    assert_eq!(protect_credential("").unwrap(), "");
}

#[test]
fn credential_protection_invalid_dpapi_headers_fail_locally() {
    for protected_value in [
        "edcred1:bad:payload",
        "edcred1:user:",
        "edcred1::payload",
        "edcred1:machine:",
    ] {
        assert_eq!(
            try_unprotect_credential_with_machine_id(protected_value, "stable-machine-id"),
            None
        );
        let value = unprotect_or_return_plaintext_with_machine_id(
            Some(protected_value),
            "stable-machine-id",
        );
        assert_eq!(value.value, None);
        assert!(!value.needs_migration);
        assert!(value.decrypt_failed);
    }
}

#[test]
fn credential_protection_source_no_longer_imports_winfluent_platform() {
    let source = include_str!("../src/credential_protection.rs");

    assert!(!source.contains("win_fluent_platform_win"));
    assert!(!source.contains("WindowsPlatformAdapter"));
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
fn credential_protection_malformed_base64_payloads_fail_locally() {
    for protected_value in [
        "edloc1:not base64!!!",
        "edloc1:AA",
        "edcred1:user:not base64!!!",
        "edcred1:user:AA",
    ] {
        assert_eq!(
            try_unprotect_credential_legacy(protected_value, "stable-machine-id"),
            None
        );
        let value = unprotect_or_return_plaintext_with_machine_id(
            Some(protected_value),
            "stable-machine-id",
        );
        assert_eq!(value.value, None);
        assert!(!value.needs_migration);
        assert!(value.decrypt_failed);
    }
}

#[test]
fn credential_protection_legacy_base64_requires_padding() {
    let protected_value = protect_credential_legacy("x", "stable-machine-id").unwrap();
    let unpadded = protected_value.trim_end_matches('=');

    assert_ne!(unpadded, protected_value);
    assert_eq!(
        try_unprotect_credential_legacy(unpadded, "stable-machine-id"),
        None
    );
}

#[test]
fn credential_protection_legacy_base64_allows_historical_trailing_bits() {
    let protected_value = protect_credential_legacy("x", "stable-machine-id").unwrap();
    let noncanonical = flip_last_base64_trailing_bit(&protected_value);

    assert_ne!(noncanonical, protected_value);
    assert_eq!(
        try_unprotect_credential_legacy(&noncanonical, "stable-machine-id").as_deref(),
        Some("x")
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

#[test]
fn credential_protection_machine_id_uses_rs_directory_before_legacy_fallback() {
    let temp = TempDir::new("credential-machine-id-rs-first");
    let rs_dir = temp.path().join("EasydictRs");
    let legacy_dir = temp.path().join("Easydict");
    fs::create_dir_all(&rs_dir).unwrap();
    fs::create_dir_all(&legacy_dir).unwrap();
    fs::write(
        rs_dir.join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME),
        "rs-machine-id",
    )
    .unwrap();
    fs::write(
        legacy_dir.join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME),
        "legacy-machine-id",
    )
    .unwrap();

    let machine_id = get_or_create_persisted_machine_id_with_legacy_fallback(&rs_dir, &legacy_dir);

    assert_eq!(machine_id, "rs-machine-id");
}

#[test]
fn credential_protection_machine_id_copies_legacy_fallback_into_rs_directory() {
    let temp = TempDir::new("credential-machine-id-legacy-fallback");
    let rs_dir = temp.path().join("EasydictRs");
    let legacy_dir = temp.path().join("Easydict");
    fs::create_dir_all(&legacy_dir).unwrap();
    fs::write(
        legacy_dir.join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME),
        "legacy-machine-id",
    )
    .unwrap();

    let machine_id = get_or_create_persisted_machine_id_with_legacy_fallback(&rs_dir, &legacy_dir);

    assert_eq!(machine_id, "legacy-machine-id");
    assert_eq!(
        fs::read_to_string(rs_dir.join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME))
            .unwrap(),
        "legacy-machine-id"
    );
}

#[test]
fn credential_protection_machine_id_reads_legacy_local_file_without_writing_legacy_directory() {
    let temp = TempDir::new("credential-machine-id-legacy-local-read-only");
    let rs_dir = temp.path().join("EasydictRs");
    let legacy_dir = temp.path().join("Easydict");
    fs::create_dir_all(&legacy_dir).unwrap();
    fs::write(legacy_dir.join("local-machine-id"), "legacy-machine-id").unwrap();

    let machine_id = get_or_create_persisted_machine_id_with_legacy_fallback(&rs_dir, &legacy_dir);

    assert_eq!(machine_id, "legacy-machine-id");
    assert_eq!(
        fs::read_to_string(rs_dir.join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME))
            .unwrap(),
        "legacy-machine-id"
    );
    assert!(
        !legacy_dir
            .join(easydict_app::credential_protection::MACHINE_ID_FILE_NAME)
            .exists(),
        "legacy fallback must not write a new machine-id into the dotnet data directory"
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

fn flip_last_base64_trailing_bit(value: &str) -> String {
    const ALPHABET: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    let mut bytes = value.as_bytes().to_vec();
    let base64_start = value.rfind(':').map(|index| index + 1).unwrap_or(0);
    let index = bytes
        .iter()
        .enumerate()
        .rev()
        .find_map(|(index, byte)| (*byte != b'=' && index >= base64_start).then_some(index))
        .expect("base64 payload should contain a non-padding symbol");
    let value = ALPHABET
        .iter()
        .position(|byte| *byte == bytes[index])
        .expect("payload should use the standard base64 alphabet");

    bytes[index] = ALPHABET[value ^ 1];
    String::from_utf8(bytes).expect("mutated payload should remain UTF-8")
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}

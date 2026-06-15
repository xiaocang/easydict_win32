use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

use base64::{
    alphabet,
    engine::general_purpose::{self, GeneralPurpose, GeneralPurposeConfig},
    Engine as _,
};
use easydict_windows_credentials::{
    protect_data, read_local_machine_registry_value_string, unprotect_data, DataProtectionScope,
    WindowsCredentialsError,
};
use ring::aead::{self, Aad, LessSafeKey, Nonce, UnboundKey};
use ring::digest;
use ring::rand::{SecureRandom, SystemRandom};

const PROTECTED_VALUE_PREFIX: &str = "edcred1:";
const LEGACY_PROTECTED_VALUE_PREFIX: &str = "edloc1:";
const LEGACY_KEY_PURPOSE: &str = "Easydict.WinUI.LocalSettingsCredentialKey.v1";
const DPAPI_PURPOSE: &str = "Easydict.WinUI.LocalSettingsCredential.v2";
const USER_SCOPE_NAME: &str = "user";
const MACHINE_SCOPE_NAME: &str = "machine";
pub const MACHINE_ID_FILE_NAME: &str = "machine-id";
const LEGACY_MACHINE_ID_FILE_NAME: &str = "local-machine-id";
const LEGACY_NONCE_SIZE_BYTES: usize = 12;
const LEGACY_TAG_SIZE_BYTES: usize = 16;
pub const MAX_NESTED_PROTECTED_VALUE_DEPTH: usize = 4;
const COMPAT_BASE64: GeneralPurpose = GeneralPurpose::new(
    &alphabet::STANDARD,
    GeneralPurposeConfig::new().with_decode_allow_trailing_bits(true),
);

static MACHINE_ID: OnceLock<String> = OnceLock::new();

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CredentialProtectionScope {
    CurrentUser,
    LocalMachine,
}

impl CredentialProtectionScope {
    fn as_wire_name(self) -> &'static str {
        match self {
            Self::CurrentUser => USER_SCOPE_NAME,
            Self::LocalMachine => MACHINE_SCOPE_NAME,
        }
    }

    fn platform_scope(self) -> DataProtectionScope {
        match self {
            Self::CurrentUser => DataProtectionScope::CurrentUser,
            Self::LocalMachine => DataProtectionScope::LocalMachine,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CredentialPlaintext {
    pub value: Option<String>,
    pub needs_migration: bool,
    pub decrypt_failed: bool,
}

#[derive(Debug)]
pub enum CredentialProtectionError {
    Platform(WindowsCredentialsError),
    InvalidProtectedValue,
    InvalidUtf8,
    LegacyCrypto,
    Random,
}

impl fmt::Display for CredentialProtectionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Platform(error) => write!(formatter, "Credential protection failed: {error:?}"),
            Self::InvalidProtectedValue => formatter.write_str("Credential payload is invalid"),
            Self::InvalidUtf8 => formatter.write_str("Credential plaintext is not valid UTF-8"),
            Self::LegacyCrypto => formatter.write_str("Legacy credential payload is invalid"),
            Self::Random => formatter.write_str("Could not generate credential nonce"),
        }
    }
}

impl From<WindowsCredentialsError> for CredentialProtectionError {
    fn from(error: WindowsCredentialsError) -> Self {
        Self::Platform(error)
    }
}

pub fn is_protected_credential(value: Option<&str>) -> bool {
    value.is_some_and(|value| {
        value.starts_with(PROTECTED_VALUE_PREFIX)
            || value.starts_with(LEGACY_PROTECTED_VALUE_PREFIX)
    })
}

pub fn protect_credential(plaintext: &str) -> Result<String, CredentialProtectionError> {
    protect_credential_with_scope(plaintext, CredentialProtectionScope::CurrentUser)
}

pub fn protect_credential_with_scope(
    plaintext: &str,
    scope: CredentialProtectionScope,
) -> Result<String, CredentialProtectionError> {
    if plaintext.is_empty() {
        return Ok(String::new());
    }

    let protected_bytes = protect_data(
        plaintext.as_bytes(),
        &dpapi_optional_entropy(scope),
        scope.platform_scope(),
    )?;

    Ok(format!(
        "{PROTECTED_VALUE_PREFIX}{}:{}",
        scope.as_wire_name(),
        base64_encode(&protected_bytes)
    ))
}

pub fn protect_credential_legacy(
    plaintext: &str,
    machine_id: &str,
) -> Result<String, CredentialProtectionError> {
    if plaintext.is_empty() {
        return Ok(String::new());
    }

    let mut nonce_bytes = [0_u8; LEGACY_NONCE_SIZE_BYTES];
    SystemRandom::new()
        .fill(&mut nonce_bytes)
        .map_err(|_| CredentialProtectionError::Random)?;

    let key = legacy_aead_key(machine_id)?;
    let nonce = Nonce::assume_unique_for_key(nonce_bytes);
    let mut ciphertext = plaintext.as_bytes().to_vec();
    let tag = key
        .seal_in_place_separate_tag(
            nonce,
            Aad::from(LEGACY_KEY_PURPOSE.as_bytes()),
            &mut ciphertext,
        )
        .map_err(|_| CredentialProtectionError::LegacyCrypto)?;

    let mut payload =
        Vec::with_capacity(LEGACY_NONCE_SIZE_BYTES + LEGACY_TAG_SIZE_BYTES + ciphertext.len());
    payload.extend_from_slice(&nonce_bytes);
    payload.extend_from_slice(tag.as_ref());
    payload.extend_from_slice(&ciphertext);

    Ok(format!(
        "{LEGACY_PROTECTED_VALUE_PREFIX}{}",
        base64_encode(&payload)
    ))
}

pub fn try_unprotect_credential(protected_value: &str) -> Option<String> {
    if !is_protected_credential(Some(protected_value)) {
        return None;
    }

    try_unprotect_nested_with_machine_id(protected_value, Some(default_machine_id()))
        .map(|result| result.0)
}

pub fn try_unprotect_credential_with_machine_id(
    protected_value: &str,
    machine_id: &str,
) -> Option<String> {
    if !is_protected_credential(Some(protected_value)) {
        return None;
    }

    try_unprotect_nested_with_machine_id(protected_value, Some(machine_id)).map(|result| result.0)
}

pub fn try_unprotect_credential_legacy(protected_value: &str, machine_id: &str) -> Option<String> {
    try_unprotect_legacy(protected_value, machine_id).ok()
}

pub fn unprotect_or_return_plaintext(stored_value: Option<&str>) -> CredentialPlaintext {
    unprotect_or_return_plaintext_with_machine_id(stored_value, default_machine_id())
}

pub fn unprotect_or_return_plaintext_with_machine_id(
    stored_value: Option<&str>,
    machine_id: &str,
) -> CredentialPlaintext {
    let Some(stored_value) = stored_value.filter(|value| !value.is_empty()) else {
        return CredentialPlaintext {
            value: None,
            needs_migration: false,
            decrypt_failed: false,
        };
    };

    if is_protected_credential(Some(stored_value)) {
        if let Some((value, needs_migration)) =
            try_unprotect_nested_with_machine_id(stored_value, Some(machine_id))
        {
            return CredentialPlaintext {
                value: (!value.is_empty()).then_some(value),
                needs_migration,
                decrypt_failed: false,
            };
        }

        return CredentialPlaintext {
            value: None,
            needs_migration: false,
            decrypt_failed: true,
        };
    }

    CredentialPlaintext {
        value: Some(stored_value.to_string()),
        needs_migration: true,
        decrypt_failed: false,
    }
}

fn try_unprotect_nested_with_machine_id(
    protected_value: &str,
    machine_id: Option<&str>,
) -> Option<(String, bool)> {
    let mut current_value = protected_value.to_string();
    let mut needs_migration = false;

    for depth in 0..MAX_NESTED_PROTECTED_VALUE_DEPTH {
        let (unprotected_value, used_legacy_protection) =
            try_unprotect_single(&current_value, machine_id)?;
        if used_legacy_protection || depth > 0 {
            needs_migration = true;
        }

        current_value = unprotected_value;
        if !is_protected_credential(Some(&current_value)) {
            return Some((current_value, needs_migration));
        }

        needs_migration = true;
    }

    None
}

fn try_unprotect_single(protected_value: &str, machine_id: Option<&str>) -> Option<(String, bool)> {
    if let Some(plaintext) = try_unprotect_dpapi(protected_value) {
        return Some((plaintext, false));
    }

    if let Some(machine_id) = machine_id {
        if let Some(plaintext) = try_unprotect_credential_legacy(protected_value, machine_id) {
            return Some((plaintext, true));
        }
    }

    None
}

fn try_unprotect_dpapi(protected_value: &str) -> Option<String> {
    let (scope, payload) = parse_dpapi_protected_value(protected_value)?;
    let protected_bytes = base64_decode(payload).ok()?;
    let plaintext = unprotect_data(
        &protected_bytes,
        &dpapi_optional_entropy(scope),
        scope.platform_scope(),
    )
    .ok()?;

    String::from_utf8(plaintext).ok()
}

fn try_unprotect_legacy(
    protected_value: &str,
    machine_id: &str,
) -> Result<String, CredentialProtectionError> {
    let payload = protected_value
        .strip_prefix(LEGACY_PROTECTED_VALUE_PREFIX)
        .ok_or(CredentialProtectionError::InvalidProtectedValue)
        .and_then(base64_decode)?;

    if payload.len() <= LEGACY_NONCE_SIZE_BYTES + LEGACY_TAG_SIZE_BYTES {
        return Err(CredentialProtectionError::InvalidProtectedValue);
    }

    let nonce_bytes: [u8; LEGACY_NONCE_SIZE_BYTES] = payload[..LEGACY_NONCE_SIZE_BYTES]
        .try_into()
        .map_err(|_| CredentialProtectionError::InvalidProtectedValue)?;
    let tag_bytes: [u8; LEGACY_TAG_SIZE_BYTES] = payload
        [LEGACY_NONCE_SIZE_BYTES..LEGACY_NONCE_SIZE_BYTES + LEGACY_TAG_SIZE_BYTES]
        .try_into()
        .map_err(|_| CredentialProtectionError::InvalidProtectedValue)?;
    let mut ciphertext = payload[(LEGACY_NONCE_SIZE_BYTES + LEGACY_TAG_SIZE_BYTES)..].to_vec();

    let key = legacy_aead_key(machine_id)?;
    let plaintext = key
        .open_in_place_separate_tag(
            Nonce::assume_unique_for_key(nonce_bytes),
            Aad::from(LEGACY_KEY_PURPOSE.as_bytes()),
            aead::Tag::from(tag_bytes),
            &mut ciphertext,
            0..,
        )
        .map_err(|_| CredentialProtectionError::LegacyCrypto)?;

    String::from_utf8(plaintext.to_vec()).map_err(|_| CredentialProtectionError::InvalidUtf8)
}

fn legacy_aead_key(machine_id: &str) -> Result<LessSafeKey, CredentialProtectionError> {
    let key_material = format!("{LEGACY_KEY_PURPOSE}:{machine_id}");
    let digest = digest::digest(&digest::SHA256, key_material.as_bytes());
    let unbound = UnboundKey::new(&aead::AES_256_GCM, digest.as_ref())
        .map_err(|_| CredentialProtectionError::LegacyCrypto)?;
    Ok(LessSafeKey::new(unbound))
}

fn parse_dpapi_protected_value(value: &str) -> Option<(CredentialProtectionScope, &str)> {
    let rest = value.strip_prefix(PROTECTED_VALUE_PREFIX)?;
    let separator = rest.find(':')?;
    if separator == 0 || separator == rest.len() - 1 {
        return None;
    }

    let scope = match &rest[..separator] {
        USER_SCOPE_NAME => CredentialProtectionScope::CurrentUser,
        MACHINE_SCOPE_NAME => CredentialProtectionScope::LocalMachine,
        _ => return None,
    };

    Some((scope, &rest[(separator + 1)..]))
}

fn dpapi_optional_entropy(scope: CredentialProtectionScope) -> Vec<u8> {
    format!("{DPAPI_PURPOSE}:{}", scope.as_wire_name()).into_bytes()
}

pub fn get_or_create_persisted_machine_id(directory: impl AsRef<Path>) -> String {
    let directory = directory.as_ref();
    fs::create_dir_all(directory).ok();

    let path = directory.join(MACHINE_ID_FILE_NAME);
    if let Some(existing) = read_non_empty_trimmed(&path) {
        return existing;
    }

    let legacy_path = directory.join(LEGACY_MACHINE_ID_FILE_NAME);
    if let Some(legacy) = read_non_empty_trimmed(&legacy_path) {
        fs::write(&path, &legacy).ok();
        return legacy;
    }

    let created = machine_guid_hash().unwrap_or_else(create_machine_id);
    if fs::write(&path, &created).is_ok() {
        return created;
    }

    std::env::var("COMPUTERNAME")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .unwrap_or(created)
}

fn default_machine_id() -> &'static str {
    MACHINE_ID
        .get_or_init(|| get_or_create_persisted_machine_id(default_data_directory()))
        .as_str()
}

fn default_data_directory() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Easydict")
}

fn read_non_empty_trimmed(path: &Path) -> Option<String> {
    fs::read_to_string(path)
        .ok()
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
}

fn create_machine_id() -> String {
    let mut bytes = [0_u8; 16];
    if SystemRandom::new().fill(&mut bytes).is_ok() {
        return hex_encode(&bytes);
    }

    let fallback = format!(
        "{}:{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or_default()
    );
    hex_encode(digest::digest(&digest::SHA256, fallback.as_bytes()).as_ref())
}

fn machine_guid_hash() -> Option<String> {
    let machine_guid = read_local_machine_registry_value_string(
        r"SOFTWARE\Microsoft\Cryptography",
        Some("MachineGuid"),
    )
    .ok()
    .flatten()?;
    let machine_guid = machine_guid.trim();
    if machine_guid.is_empty() {
        return None;
    }

    Some(hex_encode(
        digest::digest(&digest::SHA256, machine_guid.as_bytes()).as_ref(),
    ))
}

fn hex_encode(bytes: &[u8]) -> String {
    const TABLE: &[u8; 16] = b"0123456789abcdef";
    let mut encoded = String::with_capacity(bytes.len() * 2);
    for &byte in bytes {
        encoded.push(TABLE[(byte >> 4) as usize] as char);
        encoded.push(TABLE[(byte & 0x0f) as usize] as char);
    }
    encoded
}

fn base64_encode(bytes: &[u8]) -> String {
    general_purpose::STANDARD.encode(bytes)
}

fn base64_decode(value: &str) -> Result<Vec<u8>, CredentialProtectionError> {
    if value.is_empty() {
        return Err(CredentialProtectionError::InvalidProtectedValue);
    }

    COMPAT_BASE64
        .decode(value.as_bytes())
        .map_err(|_| CredentialProtectionError::InvalidProtectedValue)
}

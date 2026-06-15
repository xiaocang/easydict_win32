use crate::protocol::{
    ImportedMdxDictionarySnapshot, MdxLookupEntry, MdxLookupParams, MdxLookupResult,
    SettingsSnapshot,
};
use base64::engine::general_purpose::STANDARD as BASE64;
use base64::Engine;
use regex::{Captures, Regex};
use ripemd::{Digest, Ripemd128};
use std::collections::HashMap;
use std::fmt;
use std::fs::{self, File};
use std::io::Read;
use std::path::{Path, PathBuf};

const MAX_REDIRECT_HOPS: usize = 5;
const MAX_FUZZY_ENTRIES: usize = 20;
const FUZZY_DISTANCE: usize = 3;
const MAX_MDX_HEADER_BYTES: usize = 4 * 1024 * 1024;
const SALSA_BLOCK_BYTES: usize = 64;
const SALSA_ROUNDS: usize = 8;
const SALSA_SIGMA: [u32; 4] = [0x6170_7865, 0x3320_646e, 0x7962_2d32, 0x6b20_6574];
const SALSA_TAU: [u32; 4] = [0x6170_7865, 0x3120_646e, 0x7962_2d36, 0x6b20_6574];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MdxEncryptionMode {
    None,
    RecordBlock,
    KeyInfoBlock,
    RecordAndKeyInfoBlock,
    Unknown,
}

impl MdxEncryptionMode {
    fn is_encrypted(self) -> bool {
        self != Self::None
    }

    fn requires_credentials(self) -> bool {
        self == Self::RecordBlock
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct NativeMdxLookupError {
    message: String,
}

impl NativeMdxLookupError {
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl fmt::Display for NativeMdxLookupError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for NativeMdxLookupError {}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct NativeMddResourceError {
    message: String,
}

impl NativeMddResourceError {
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl fmt::Display for NativeMddResourceError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for NativeMddResourceError {}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct NativeMddResource {
    pub key: String,
    pub data: Vec<u8>,
    pub mime_type: String,
}

pub trait NativeMdxDictionaryReader {
    fn lookup(&mut self, query: &str) -> Result<Option<(String, String)>, NativeMdxLookupError>;

    fn all_keys(&mut self) -> Result<Vec<String>, NativeMdxLookupError>;

    fn fuzzy_keys(
        &mut self,
        query: &str,
        max_results: usize,
        max_distance: usize,
    ) -> Result<Vec<String>, NativeMdxLookupError>;
}

pub trait NativeMdxDictionaryReaderFactory {
    type Reader: NativeMdxDictionaryReader;

    fn open(
        &mut self,
        dictionary: &ImportedMdxDictionarySnapshot,
    ) -> Result<Self::Reader, NativeMdxLookupError>;
}

pub trait NativeMddResourceReader {
    fn locate_raw(
        &mut self,
        resource_key: &str,
    ) -> Result<Option<(String, Vec<u8>)>, NativeMddResourceError>;
}

pub trait NativeMddResourceReaderFactory {
    type Reader: NativeMddResourceReader;

    fn open_mdd(&mut self, path: &str) -> Result<Self::Reader, NativeMddResourceError>;
}

#[derive(Default)]
pub struct RsMdictReaderFactory;

pub struct RsMdictReader {
    mdx: rust_mdict::Mdx,
}

#[derive(Default)]
pub struct RsMdictMddReaderFactory;

pub struct RsMdictMddReader {
    mdd: rust_mdict::Mdd,
}

impl NativeMdxDictionaryReaderFactory for RsMdictReaderFactory {
    type Reader = RsMdictReader;

    fn open(
        &mut self,
        dictionary: &ImportedMdxDictionarySnapshot,
    ) -> Result<Self::Reader, NativeMdxLookupError> {
        let path = dictionary.file_path.trim();
        if path.is_empty() {
            return Err(NativeMdxLookupError::new(
                "MDX dictionary file path cannot be empty",
            ));
        }

        if !Path::new(path).exists() {
            return Err(NativeMdxLookupError::new("MDX dictionary file not found."));
        }

        let mdx = if native_mdx_dictionary_uses_passcode_native_route(dictionary) {
            let key = mdx_key_header_decryption_key(dictionary)?;
            rust_mdict::Mdx::new_with_key_header_transform(
                path.to_string(),
                move |key_header, _| {
                    mdx_salsa20_8(key_header, &key)
                        .map_err(|error| rust_mdict::MdictError::DecryptionError(error.to_string()))
                },
            )
        } else {
            rust_mdict::Mdx::new(path)
        }
        .map_err(|error| NativeMdxLookupError::new(error.to_string()))?;
        Ok(RsMdictReader { mdx })
    }
}

impl NativeMdxDictionaryReader for RsMdictReader {
    fn lookup(&mut self, query: &str) -> Result<Option<(String, String)>, NativeMdxLookupError> {
        Ok(self
            .mdx
            .lookup(query)
            .map(|result| (result.key_text, result.definition)))
    }

    fn all_keys(&mut self) -> Result<Vec<String>, NativeMdxLookupError> {
        Ok(self
            .mdx
            .keywords()
            .into_iter()
            .map(str::to_string)
            .collect())
    }

    fn fuzzy_keys(
        &mut self,
        query: &str,
        max_results: usize,
        max_distance: usize,
    ) -> Result<Vec<String>, NativeMdxLookupError> {
        Ok(self
            .mdx
            .fuzzy_search(query, max_results, max_distance)
            .into_iter()
            .map(|word| word.item.key_text)
            .collect())
    }
}

impl NativeMddResourceReaderFactory for RsMdictMddReaderFactory {
    type Reader = RsMdictMddReader;

    fn open_mdd(&mut self, path: &str) -> Result<Self::Reader, NativeMddResourceError> {
        let path = path.trim();
        if path.is_empty() {
            return Err(NativeMddResourceError::new(
                "MDD resource file path cannot be empty",
            ));
        }

        if !Path::new(path).exists() {
            return Err(NativeMddResourceError::new("MDD resource file not found."));
        }

        let mdd = rust_mdict::Mdd::new(path)
            .map_err(|error| NativeMddResourceError::new(error.to_string()))?;
        Ok(RsMdictMddReader { mdd })
    }
}

impl NativeMddResourceReader for RsMdictMddReader {
    fn locate_raw(
        &mut self,
        resource_key: &str,
    ) -> Result<Option<(String, Vec<u8>)>, NativeMddResourceError> {
        self.mdd
            .locate_resource_result(resource_key)
            .map(|resource| resource.map(|resource| (resource.key, resource.data)))
            .map_err(|error| NativeMddResourceError::new(error.to_string()))
    }
}

pub fn native_mdx_service_can_route_natively(
    service_id: &str,
    settings: &SettingsSnapshot,
) -> bool {
    find_dictionary(settings, service_id)
        .map(native_mdx_dictionary_can_route_natively)
        .unwrap_or(false)
}

pub fn native_mdx_lookup_can_route(params: &MdxLookupParams, settings: &SettingsSnapshot) -> bool {
    native_mdx_service_can_route_natively(&params.dictionary_id, settings)
}

pub fn native_mdx_lookup_needs_credentials(
    params: &MdxLookupParams,
    settings: &SettingsSnapshot,
) -> bool {
    find_dictionary(settings, &params.dictionary_id)
        .map(native_mdx_dictionary_needs_credentials)
        .unwrap_or(false)
}

pub fn native_mdx_lookup_local_input_error(
    params: &MdxLookupParams,
    settings: &SettingsSnapshot,
) -> Option<NativeMdxLookupError> {
    find_dictionary(settings, &params.dictionary_id)
        .and_then(native_mdx_dictionary_local_input_error)
}

pub fn native_mdx_dictionary_can_route_natively(
    dictionary: &ImportedMdxDictionarySnapshot,
) -> bool {
    if !dictionary.service_id.starts_with("mdx::") {
        return false;
    }

    if !dictionary.is_encrypted {
        return true;
    }

    mdx_dictionary_encryption_mode(dictionary)
        .map(|mode| match mode {
            MdxEncryptionMode::None | MdxEncryptionMode::KeyInfoBlock => true,
            MdxEncryptionMode::RecordBlock => {
                native_mdx_dictionary_has_credentials(dictionary)
                    && native_mdx_dictionary_credential_error(dictionary).is_none()
            }
            MdxEncryptionMode::RecordAndKeyInfoBlock | MdxEncryptionMode::Unknown => false,
        })
        .unwrap_or(false)
}

pub fn native_mdx_dictionary_needs_credentials(dictionary: &ImportedMdxDictionarySnapshot) -> bool {
    native_mdx_dictionary_requires_credentials(dictionary)
        && !native_mdx_dictionary_has_credentials(dictionary)
}

fn native_mdx_dictionary_requires_credentials(dictionary: &ImportedMdxDictionarySnapshot) -> bool {
    if !dictionary.service_id.starts_with("mdx::") || !dictionary.is_encrypted {
        return false;
    }

    mdx_dictionary_encryption_mode(dictionary)
        .map(MdxEncryptionMode::requires_credentials)
        .unwrap_or(true)
}

fn native_mdx_dictionary_has_credentials(dictionary: &ImportedMdxDictionarySnapshot) -> bool {
    !dictionary
        .regcode
        .as_deref()
        .map(str::trim)
        .unwrap_or_default()
        .is_empty()
        && !dictionary
            .email
            .as_deref()
            .map(str::trim)
            .unwrap_or_default()
            .is_empty()
}

fn native_mdx_dictionary_uses_passcode_native_route(
    dictionary: &ImportedMdxDictionarySnapshot,
) -> bool {
    dictionary.is_encrypted
        && native_mdx_dictionary_has_credentials(dictionary)
        && mdx_dictionary_encryption_mode(dictionary)
            .map(|mode| mode == MdxEncryptionMode::RecordBlock)
            .unwrap_or(false)
}

fn native_mdx_dictionary_local_input_error(
    dictionary: &ImportedMdxDictionarySnapshot,
) -> Option<NativeMdxLookupError> {
    if !dictionary.service_id.starts_with("mdx::") {
        return None;
    }

    let path = dictionary.file_path.trim();
    if path.is_empty() {
        return Some(NativeMdxLookupError::new(
            "MDX dictionary file path cannot be empty",
        ));
    }

    if !Path::new(path).exists() {
        return Some(NativeMdxLookupError::new("MDX dictionary file not found."));
    }

    if dictionary.is_encrypted {
        match mdx_dictionary_encryption_mode(dictionary) {
            Ok(mode) => {
                if matches!(
                    mode,
                    MdxEncryptionMode::RecordAndKeyInfoBlock | MdxEncryptionMode::Unknown
                ) {
                    return Some(NativeMdxLookupError::new(format!(
                        "MDX dictionary encryption mode {mode:?} is not supported by the Rust-native MDX reader"
                    )));
                }
            }
            Err(error) => return Some(error),
        }
    }

    if native_mdx_dictionary_requires_credentials(dictionary)
        && native_mdx_dictionary_has_credentials(dictionary)
    {
        if let Some(error) = native_mdx_dictionary_credential_error(dictionary) {
            return Some(error);
        }
    }

    None
}

fn mdx_dictionary_encryption_mode(
    dictionary: &ImportedMdxDictionarySnapshot,
) -> Result<MdxEncryptionMode, NativeMdxLookupError> {
    detect_mdx_file_encryption_mode(dictionary.file_path.trim())
}

fn native_mdx_dictionary_credential_error(
    dictionary: &ImportedMdxDictionarySnapshot,
) -> Option<NativeMdxLookupError> {
    let regcode = dictionary.regcode.as_deref().unwrap_or_default();
    let regcode = match mdx_decode_base64_regcode(regcode) {
        Ok(regcode) => regcode,
        Err(error) => return Some(error),
    };

    if regcode.len() != 16 && regcode.len() != 32 {
        return Some(NativeMdxLookupError::new(
            "MDX registration code must decode to a 16 or 32 byte Salsa key",
        ));
    }

    None
}

fn mdx_key_header_decryption_key(
    dictionary: &ImportedMdxDictionarySnapshot,
) -> Result<Vec<u8>, NativeMdxLookupError> {
    let regcode = mdx_decode_base64_regcode(dictionary.regcode.as_deref().unwrap_or_default())?;
    if regcode.len() != 16 && regcode.len() != 32 {
        return Err(NativeMdxLookupError::new(
            "MDX registration code must decode to a 16 or 32 byte Salsa key",
        ));
    }

    let user_id = dictionary
        .email
        .as_deref()
        .map(str::trim)
        .unwrap_or_default();
    if user_id.is_empty() {
        return Err(NativeMdxLookupError::new(
            "MDX dictionary credentials are required before lookup",
        ));
    }

    if mdx_dictionary_register_by(dictionary)?
        .as_deref()
        .is_some_and(|register_by| register_by.eq_ignore_ascii_case("EMail"))
    {
        mdx_decrypt_regcode_by_email(&regcode, user_id)
    } else {
        mdx_decrypt_regcode_by_device_id(&regcode, user_id.as_bytes())
    }
}

pub fn mdx_decode_base64_regcode(regcode: &str) -> Result<Vec<u8>, NativeMdxLookupError> {
    base64::engine::general_purpose::STANDARD
        .decode(regcode.trim())
        .map_err(|error| {
            NativeMdxLookupError::new(format!(
                "MDX registration code is not valid Base64: {error}"
            ))
        })
}

pub fn mdx_ripemd128(input: &[u8]) -> [u8; 16] {
    let digest = Ripemd128::digest(input);
    let mut output = [0u8; 16];
    output.copy_from_slice(&digest);
    output
}

pub fn mdx_fast_decrypt(data: &[u8], key: &[u8]) -> Result<Vec<u8>, NativeMdxLookupError> {
    if key.is_empty() {
        return Err(NativeMdxLookupError::new(
            "MDX fast-decrypt key cannot be empty",
        ));
    }

    let mut output = data.to_vec();
    let mut previous = 0x36u8;
    for (index, byte) in output.iter_mut().enumerate() {
        let original = *byte;
        let mut decrypted = original.rotate_left(4);
        decrypted ^= previous;
        decrypted ^= (index & 0xff) as u8;
        decrypted ^= key[index % key.len()];
        previous = original;
        *byte = decrypted;
    }

    Ok(output)
}

pub fn mdx_decrypt_block(comp_block: &[u8]) -> Result<Vec<u8>, NativeMdxLookupError> {
    if comp_block.len() < 8 {
        return Err(NativeMdxLookupError::new(
            "MDX encrypted block must be at least 8 bytes",
        ));
    }

    let mut key_input = [0u8; 8];
    key_input[..4].copy_from_slice(&comp_block[4..8]);
    key_input[4] ^= 0x95;
    key_input[5] ^= 0x36;

    let key = mdx_ripemd128(&key_input);
    let decrypted_tail = mdx_fast_decrypt(&comp_block[8..], &key)?;

    let mut output = Vec::with_capacity(comp_block.len());
    output.extend_from_slice(&comp_block[..8]);
    output.extend_from_slice(&decrypted_tail);
    Ok(output)
}

pub fn mdx_decrypt_regcode_by_email(
    regcode: &[u8],
    email: &str,
) -> Result<Vec<u8>, NativeMdxLookupError> {
    let mut email_bytes = Vec::with_capacity(email.len() * 2);
    for code_unit in email.encode_utf16() {
        email_bytes.extend_from_slice(&code_unit.to_le_bytes());
    }

    let digest = mdx_ripemd128(&email_bytes);
    mdx_salsa20_8(regcode, &digest)
}

pub fn mdx_decrypt_regcode_by_device_id(
    regcode: &[u8],
    device_id: &[u8],
) -> Result<Vec<u8>, NativeMdxLookupError> {
    let digest = mdx_ripemd128(device_id);
    mdx_salsa20_8(regcode, &digest)
}

pub fn mdx_salsa20_8(data: &[u8], key: &[u8]) -> Result<Vec<u8>, NativeMdxLookupError> {
    if key.len() != 16 && key.len() != 32 {
        return Err(NativeMdxLookupError::new(
            "MDX Salsa20/8 key must be 16 or 32 bytes",
        ));
    }

    let mut output = data.to_vec();
    let mut counter = 0u64;
    for chunk in output.chunks_mut(SALSA_BLOCK_BYTES) {
        let block = mdx_salsa20_8_block(key, counter)?;
        for (byte, stream) in chunk.iter_mut().zip(block) {
            *byte ^= stream;
        }
        counter = counter.wrapping_add(1);
    }

    Ok(output)
}

pub fn run_native_mdx_lookup(
    params: &MdxLookupParams,
    settings: &SettingsSnapshot,
) -> Result<MdxLookupResult, NativeMdxLookupError> {
    let mut factory = RsMdictReaderFactory;
    let mut mdd_factory = RsMdictMddReaderFactory;
    run_native_mdx_lookup_with_factories(&mut factory, &mut mdd_factory, params, settings)
}

pub fn detect_mdx_file_is_encrypted(path: impl AsRef<Path>) -> Result<bool, NativeMdxLookupError> {
    Ok(detect_mdx_file_encryption_mode(path)?.is_encrypted())
}

pub fn detect_mdx_file_encryption_mode(
    path: impl AsRef<Path>,
) -> Result<MdxEncryptionMode, NativeMdxLookupError> {
    let header_text = read_mdx_header_text(path)?;
    mdx_header_encryption_mode(&header_text)
}

fn mdx_dictionary_register_by(
    dictionary: &ImportedMdxDictionarySnapshot,
) -> Result<Option<String>, NativeMdxLookupError> {
    let header_text = read_mdx_header_text(dictionary.file_path.trim())?;
    let attributes = mdx_header_attributes(&header_text)?;
    Ok(mdx_header_attribute(&attributes, "RegisterBy"))
}

fn read_mdx_header_text(path: impl AsRef<Path>) -> Result<String, NativeMdxLookupError> {
    let mut file = File::open(path.as_ref()).map_err(|error| {
        NativeMdxLookupError::new(format!("Could not open MDX dictionary header: {error}"))
    })?;
    let mut header_len = [0u8; 4];
    file.read_exact(&mut header_len).map_err(|error| {
        NativeMdxLookupError::new(format!(
            "Could not read MDX dictionary header size: {error}"
        ))
    })?;

    let header_len = u32::from_be_bytes(header_len) as usize;
    if header_len == 0 || header_len > MAX_MDX_HEADER_BYTES {
        return Err(NativeMdxLookupError::new(
            "MDX dictionary header size is invalid",
        ));
    }

    let mut header_bytes = vec![0u8; header_len];
    file.read_exact(&mut header_bytes).map_err(|error| {
        NativeMdxLookupError::new(format!("Could not read MDX dictionary header: {error}"))
    })?;
    Ok(decode_mdx_header_text(&header_bytes))
}

pub fn run_native_mdd_resource_lookup(
    dictionary: &ImportedMdxDictionarySnapshot,
    resource_key: &str,
) -> Result<Option<NativeMddResource>, NativeMddResourceError> {
    let mut factory = RsMdictMddReaderFactory;
    run_native_mdd_resource_lookup_with_factory(&mut factory, dictionary, resource_key)
}

pub fn run_native_mdd_resource_lookup_with_factory<F: NativeMddResourceReaderFactory>(
    factory: &mut F,
    dictionary: &ImportedMdxDictionarySnapshot,
    resource_key: &str,
) -> Result<Option<NativeMddResource>, NativeMddResourceError> {
    if resource_key.trim().is_empty() {
        return Ok(None);
    }

    let resource_key = normalize_mdd_resource_key(resource_key)?;
    for path in &dictionary.mdd_file_paths {
        let Ok(mut reader) = factory.open_mdd(path) else {
            continue;
        };

        let Ok(Some((resolved_key, data))) = reader.locate_raw(&resource_key) else {
            continue;
        };

        return Ok(Some(NativeMddResource {
            mime_type: mime_type_for_mdd_resource_key(&resolved_key),
            key: resolved_key,
            data,
        }));
    }

    Ok(None)
}

pub fn run_native_mdx_lookup_with_factory<F: NativeMdxDictionaryReaderFactory>(
    factory: &mut F,
    params: &MdxLookupParams,
    settings: &SettingsSnapshot,
) -> Result<MdxLookupResult, NativeMdxLookupError> {
    let mut mdd_factory = RsMdictMddReaderFactory;
    run_native_mdx_lookup_with_factories(factory, &mut mdd_factory, params, settings)
}

pub fn run_native_mdx_lookup_with_factory_and_mdd_policy<F: NativeMdxDictionaryReaderFactory>(
    factory: &mut F,
    params: &MdxLookupParams,
    settings: &SettingsSnapshot,
    include_mdd_resources: bool,
) -> Result<MdxLookupResult, NativeMdxLookupError> {
    let mut mdd_factory = RsMdictMddReaderFactory;
    run_native_mdx_lookup_with_factories_and_mdd_policy(
        factory,
        &mut mdd_factory,
        params,
        settings,
        include_mdd_resources,
    )
}

pub fn run_native_mdx_lookup_with_factories<
    F: NativeMdxDictionaryReaderFactory,
    M: NativeMddResourceReaderFactory,
>(
    factory: &mut F,
    mdd_factory: &mut M,
    params: &MdxLookupParams,
    settings: &SettingsSnapshot,
) -> Result<MdxLookupResult, NativeMdxLookupError> {
    run_native_mdx_lookup_with_factories_and_mdd_policy(
        factory,
        mdd_factory,
        params,
        settings,
        true,
    )
}

pub fn run_native_mdx_lookup_with_factories_and_mdd_policy<
    F: NativeMdxDictionaryReaderFactory,
    M: NativeMddResourceReaderFactory,
>(
    factory: &mut F,
    mdd_factory: &mut M,
    params: &MdxLookupParams,
    settings: &SettingsSnapshot,
    include_mdd_resources: bool,
) -> Result<MdxLookupResult, NativeMdxLookupError> {
    let Some(dictionary) = find_dictionary(settings, &params.dictionary_id) else {
        return Ok(MdxLookupResult {
            entries: Vec::new(),
            mdd_resources_inlined: false,
        });
    };

    if native_mdx_dictionary_needs_credentials(dictionary) {
        return Err(NativeMdxLookupError::new(
            "MDX dictionary credentials are required before lookup",
        ));
    }

    if !native_mdx_dictionary_can_route_natively(dictionary) {
        if let Some(error) = native_mdx_dictionary_local_input_error(dictionary) {
            return Err(error);
        }

        return Err(NativeMdxLookupError::new(
            "MDX dictionary is not supported by the Rust-native MDX reader",
        ));
    }

    let query = params.query.trim();
    if query.is_empty() {
        return Err(NativeMdxLookupError::new(
            "MDX lookup query cannot be empty",
        ));
    }

    let mut reader = factory.open(dictionary)?;
    let mut entries = if params.fuzzy {
        lookup_fuzzy(&mut reader, query, &dictionary.display_name)?
    } else {
        lookup_exact(&mut reader, query, &dictionary.display_name)?
    };

    let mut mdd_resources_inlined = false;
    if include_mdd_resources {
        mdd_resources_inlined =
            inline_mdd_resources_for_entries(mdd_factory, dictionary, &mut entries)
                .map_err(|error| NativeMdxLookupError::new(error.to_string()))?;
    }

    Ok(MdxLookupResult {
        entries,
        mdd_resources_inlined,
    })
}

pub fn inline_mdd_resources_in_html(
    dictionary: &ImportedMdxDictionarySnapshot,
    html: &str,
) -> Result<String, NativeMddResourceError> {
    let mut factory = RsMdictMddReaderFactory;
    inline_mdd_resources_in_html_with_factory(&mut factory, dictionary, html)
}

pub fn inline_mdd_resources_in_html_with_factory<F: NativeMddResourceReaderFactory>(
    factory: &mut F,
    dictionary: &ImportedMdxDictionarySnapshot,
    html: &str,
) -> Result<String, NativeMddResourceError> {
    if dictionary.mdd_file_paths.is_empty() || html.trim().is_empty() {
        return Ok(html.to_string());
    }

    let mut readers = open_mdd_readers(factory, dictionary);
    if readers.is_empty() {
        return Ok(html.to_string());
    }

    inline_mdd_resources_in_html_with_readers(&mut readers, html)
}

pub fn normalize_mdd_resource_key(resource_key: &str) -> Result<String, NativeMddResourceError> {
    let normalized = rust_mdict::normalize_mdd_resource_key(resource_key);
    if normalized.is_empty() {
        return Err(NativeMddResourceError::new(
            "MDD resource key cannot be empty",
        ));
    }

    Ok(normalized)
}

pub fn mime_type_for_mdd_resource_key(resource_key: &str) -> String {
    rust_mdict::mime_type_for_mdd_resource_key(resource_key).to_string()
}

pub fn discover_mdd_file_paths(mdx_file_path: &str) -> Vec<String> {
    let mdx_path = Path::new(mdx_file_path);
    let Some(directory) = mdx_path
        .parent()
        .filter(|directory| !directory.as_os_str().is_empty() && directory.is_dir())
    else {
        return Vec::new();
    };
    let Some(base_name) = mdx_path
        .file_stem()
        .and_then(|stem| stem.to_str())
        .filter(|stem| !stem.trim().is_empty())
    else {
        return Vec::new();
    };

    let mut discovered = fs::read_dir(directory)
        .ok()
        .into_iter()
        .flat_map(|entries| entries.filter_map(|entry| entry.ok()))
        .filter_map(|entry| {
            let path = entry.path();
            if !path.is_file()
                || path
                    .extension()
                    .and_then(|extension| extension.to_str())
                    .map(|extension| !extension.eq_ignore_ascii_case("mdd"))
                    .unwrap_or(true)
            {
                return None;
            }

            let stem = path.file_stem()?.to_str()?;
            let order = mdd_companion_order(base_name, stem)?;
            Some((order, path))
        })
        .collect::<Vec<_>>();
    discovered.sort_by(|(left_order, left_path), (right_order, right_path)| {
        left_order.cmp(right_order).then_with(|| {
            path_to_string(left_path.clone()).cmp(&path_to_string(right_path.clone()))
        })
    });

    discovered
        .into_iter()
        .map(|(_, path)| path_to_string(path))
        .collect()
}

fn mdd_companion_order(base_name: &str, stem: &str) -> Option<(u8, u32)> {
    if stem.eq_ignore_ascii_case(base_name) {
        return Some((0, 0));
    }

    let stem_prefix = stem.get(..base_name.len())?;
    if !stem_prefix.eq_ignore_ascii_case(base_name) {
        return None;
    }
    let suffix = stem.get(base_name.len()..)?;
    let suffix = suffix.strip_prefix('.')?;
    let number = suffix.parse::<u32>().ok()?;
    (number > 0).then_some((1, number))
}

fn path_to_string(path: PathBuf) -> String {
    path.to_string_lossy().into_owned()
}

fn inline_mdd_resources_for_entries<F: NativeMddResourceReaderFactory>(
    factory: &mut F,
    dictionary: &ImportedMdxDictionarySnapshot,
    entries: &mut [MdxLookupEntry],
) -> Result<bool, NativeMddResourceError> {
    if dictionary.mdd_file_paths.is_empty() {
        return Ok(false);
    }

    let mut readers = open_mdd_readers(factory, dictionary);
    if readers.is_empty() {
        return Ok(false);
    }

    let mut changed = false;
    for entry in entries {
        let rewritten = inline_mdd_resources_in_html_with_readers(&mut readers, &entry.html)?;
        if rewritten != entry.html {
            changed = true;
        }
        entry.html = rewritten;
    }

    Ok(changed)
}

fn open_mdd_readers<F: NativeMddResourceReaderFactory>(
    factory: &mut F,
    dictionary: &ImportedMdxDictionarySnapshot,
) -> Vec<F::Reader> {
    dictionary
        .mdd_file_paths
        .iter()
        .filter_map(|path| factory.open_mdd(path).ok())
        .collect()
}

fn inline_mdd_resources_in_html_with_readers<R: NativeMddResourceReader>(
    readers: &mut [R],
    html: &str,
) -> Result<String, NativeMddResourceError> {
    rewrite_html_resource_references(html, |reference| {
        lookup_mdd_resource_in_readers(readers, reference)
    })
}

fn rewrite_html_resource_references<F>(
    html: &str,
    mut lookup: F,
) -> Result<String, NativeMddResourceError>
where
    F: FnMut(&str) -> Result<Option<NativeMddResource>, NativeMddResourceError>,
{
    let attr_regex = Regex::new(
        r#"(?i)\b(src|href|poster|background|data-src|data-original|data-lazy-src|data-original-src)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'<>]+))"#,
    )
        .map_err(|error| NativeMddResourceError::new(error.to_string()))?;
    let rewritten = attr_regex
        .replace_all(html, |captures: &Captures<'_>| {
            let full = captures.get(0).map(|match_| match_.as_str()).unwrap_or("");
            let Some(name) = captures.get(1).map(|match_| match_.as_str()) else {
                return full.to_string();
            };
            let Some((quote, value)) = captured_attribute_value(captures) else {
                return full.to_string();
            };

            match lookup(value) {
                Ok(Some(resource)) => {
                    format_html_attribute(name, quote, &mdd_resource_data_url(resource))
                }
                _ => full.to_string(),
            }
        })
        .into_owned();

    let srcset_regex =
        Regex::new(r#"(?i)\b(srcset|data-srcset)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'<>]+))"#)
            .map_err(|error| NativeMddResourceError::new(error.to_string()))?;
    let rewritten = srcset_regex
        .replace_all(&rewritten, |captures: &Captures<'_>| {
            let full = captures.get(0).map(|match_| match_.as_str()).unwrap_or("");
            let Some(name) = captures.get(1).map(|match_| match_.as_str()) else {
                return full.to_string();
            };
            let Some((quote, value)) = captured_attribute_value(captures) else {
                return full.to_string();
            };

            let rewritten_value = rewrite_srcset_value(value, &mut lookup);
            if rewritten_value == value {
                full.to_string()
            } else {
                format_html_attribute(name, quote, &rewritten_value)
            }
        })
        .into_owned();

    let css_url_regex = Regex::new(r#"(?i)url\(\s*(['"]?)([^'")]+)(['"]?)\s*\)"#)
        .map_err(|error| NativeMddResourceError::new(error.to_string()))?;
    Ok(css_url_regex
        .replace_all(&rewritten, |captures: &Captures<'_>| {
            let full = captures.get(0).map(|match_| match_.as_str()).unwrap_or("");
            let Some(value) = captures.get(2).map(|match_| match_.as_str()) else {
                return full.to_string();
            };

            match lookup(value) {
                Ok(Some(resource)) => format!("url('{}')", mdd_resource_data_url(resource)),
                _ => full.to_string(),
            }
        })
        .into_owned())
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum HtmlAttributeQuote {
    Double,
    Single,
    Unquoted,
}

fn captured_attribute_value<'html>(
    captures: &Captures<'html>,
) -> Option<(HtmlAttributeQuote, &'html str)> {
    if let Some(value) = captures.get(2).map(|match_| match_.as_str()) {
        return Some((HtmlAttributeQuote::Double, value));
    }

    if let Some(value) = captures.get(3).map(|match_| match_.as_str()) {
        return Some((HtmlAttributeQuote::Single, value));
    }

    captures
        .get(4)
        .map(|match_| (HtmlAttributeQuote::Unquoted, match_.as_str()))
}

fn format_html_attribute(name: &str, quote: HtmlAttributeQuote, value: &str) -> String {
    match quote {
        HtmlAttributeQuote::Double | HtmlAttributeQuote::Unquoted => format!(r#"{name}="{value}""#),
        HtmlAttributeQuote::Single => format!("{name}='{value}'"),
    }
}

fn rewrite_srcset_value<F>(value: &str, lookup: &mut F) -> String
where
    F: FnMut(&str) -> Result<Option<NativeMddResource>, NativeMddResourceError>,
{
    let mut changed = false;
    let mut rewritten = String::with_capacity(value.len());

    for (index, candidate) in value.split(',').enumerate() {
        if index > 0 {
            rewritten.push(',');
        }

        match rewrite_srcset_candidate(candidate, lookup) {
            Some(candidate) => {
                changed = true;
                rewritten.push_str(&candidate);
            }
            None => rewritten.push_str(candidate),
        }
    }

    if changed {
        rewritten
    } else {
        value.to_string()
    }
}

fn rewrite_srcset_candidate<F>(candidate: &str, lookup: &mut F) -> Option<String>
where
    F: FnMut(&str) -> Result<Option<NativeMddResource>, NativeMddResourceError>,
{
    let leading_len = candidate.len() - candidate.trim_start().len();
    let trailing_len = candidate.len() - candidate.trim_end().len();
    let trimmed_end = candidate.len().saturating_sub(trailing_len);
    let trimmed = &candidate[leading_len..trimmed_end];
    if trimmed.is_empty() {
        return None;
    }

    let url_end = trimmed.find(char::is_whitespace).unwrap_or(trimmed.len());
    let url = &trimmed[..url_end];
    if url.is_empty() {
        return None;
    }

    let resource = lookup(url).ok().flatten()?;
    let mut output = String::with_capacity(candidate.len());
    output.push_str(&candidate[..leading_len]);
    output.push_str(&mdd_resource_data_url(resource));
    output.push_str(&trimmed[url_end..]);
    output.push_str(&candidate[trimmed_end..]);
    Some(output)
}

fn lookup_mdd_resource_in_readers<R: NativeMddResourceReader>(
    readers: &mut [R],
    resource_reference: &str,
) -> Result<Option<NativeMddResource>, NativeMddResourceError> {
    let Some(resource_key) = mdd_resource_reference_to_key(resource_reference)? else {
        return Ok(None);
    };

    for reader in readers {
        let Ok(Some((resolved_key, data))) = reader.locate_raw(&resource_key) else {
            continue;
        };

        return Ok(Some(NativeMddResource {
            mime_type: mime_type_for_mdd_resource_key(&resolved_key),
            key: resolved_key,
            data,
        }));
    }

    Ok(None)
}

fn mdd_resource_reference_to_key(
    resource_reference: &str,
) -> Result<Option<String>, NativeMddResourceError> {
    let reference = resource_reference.trim();
    if reference.is_empty() || should_skip_mdd_resource_reference(reference) {
        return Ok(None);
    }

    let path = dictassets_resource_path(reference)
        .or_else(|| mdict_sound_resource_path(reference))
        .unwrap_or(reference);
    let path = strip_query_and_fragment(path);
    if path.trim().is_empty() {
        return Ok(None);
    }

    let path = percent_decode_ascii(path);
    let path = strip_leading_dot_relative_segments(path.trim_start_matches('/'));
    if path.trim().is_empty() {
        return Ok(None);
    }

    normalize_mdd_resource_key(path).map(Some)
}

fn should_skip_mdd_resource_reference(reference: &str) -> bool {
    let lower = reference.to_ascii_lowercase();
    if lower.starts_with('#')
        || lower.starts_with("data:")
        || lower.starts_with("javascript:")
        || lower.starts_with("mailto:")
        || lower.starts_with("tel:")
    {
        return true;
    }

    if lower.starts_with("http://dictassets/")
        || lower.starts_with("https://dictassets/")
        || mdict_sound_resource_path(reference).is_some()
    {
        return false;
    }

    looks_like_absolute_uri(reference)
}

fn dictassets_resource_path(reference: &str) -> Option<&str> {
    let lower = reference.to_ascii_lowercase();
    for prefix in ["https://dictassets/", "http://dictassets/"] {
        if lower.starts_with(prefix) {
            return Some(&reference[prefix.len()..]);
        }
    }

    None
}

fn mdict_sound_resource_path(reference: &str) -> Option<&str> {
    let lower = reference.to_ascii_lowercase();
    for prefix in ["sound://", "sound:/", "sound:"] {
        if lower.starts_with(prefix) {
            return Some(&reference[prefix.len()..]);
        }
    }

    None
}

fn looks_like_absolute_uri(reference: &str) -> bool {
    let Some(colon_index) = reference.find(':') else {
        return false;
    };

    let first_path_separator = reference.find(['/', '\\']).unwrap_or(reference.len());
    colon_index < first_path_separator
}

fn strip_query_and_fragment(reference: &str) -> &str {
    reference
        .find(['?', '#'])
        .map(|index| &reference[..index])
        .unwrap_or(reference)
}

fn strip_leading_dot_relative_segments(mut path: &str) -> &str {
    loop {
        if let Some(rest) = path.strip_prefix("./").or_else(|| path.strip_prefix(".\\")) {
            path = rest;
        } else {
            return path;
        }
    }
}

fn percent_decode_ascii(value: &str) -> String {
    let bytes = value.as_bytes();
    let mut output = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'%' && index + 2 < bytes.len() {
            if let (Some(high), Some(low)) =
                (hex_value(bytes[index + 1]), hex_value(bytes[index + 2]))
            {
                output.push((high << 4) | low);
                index += 3;
                continue;
            }
        }

        output.push(bytes[index]);
        index += 1;
    }

    String::from_utf8_lossy(&output).to_string()
}

fn hex_value(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        b'A'..=b'F' => Some(byte - b'A' + 10),
        _ => None,
    }
}

fn mdd_resource_data_url(resource: NativeMddResource) -> String {
    format!(
        "data:{};base64,{}",
        resource.mime_type,
        BASE64.encode(resource.data)
    )
}

fn find_dictionary<'a>(
    settings: &'a SettingsSnapshot,
    dictionary_id: &str,
) -> Option<&'a ImportedMdxDictionarySnapshot> {
    settings
        .imported_mdx_dictionaries
        .as_ref()?
        .iter()
        .find(|dictionary| dictionary.service_id.eq_ignore_ascii_case(dictionary_id))
}

fn lookup_exact<R: NativeMdxDictionaryReader>(
    reader: &mut R,
    query: &str,
    dictionary_name: &str,
) -> Result<Vec<MdxLookupEntry>, NativeMdxLookupError> {
    let Some((key, html)) = resolve_definition(reader, query)? else {
        return Ok(Vec::new());
    };

    Ok(vec![MdxLookupEntry {
        key,
        html,
        dictionary_name: Some(dictionary_name.to_string()),
    }])
}

fn lookup_fuzzy<R: NativeMdxDictionaryReader>(
    reader: &mut R,
    query: &str,
    dictionary_name: &str,
) -> Result<Vec<MdxLookupEntry>, NativeMdxLookupError> {
    let mut entries = Vec::new();
    for candidate in reader.fuzzy_keys(query, MAX_FUZZY_ENTRIES, FUZZY_DISTANCE)? {
        if entries.len() >= MAX_FUZZY_ENTRIES {
            break;
        }

        let Some((key, html)) = resolve_definition(reader, &candidate)? else {
            continue;
        };

        entries.push(MdxLookupEntry {
            key,
            html,
            dictionary_name: Some(dictionary_name.to_string()),
        });
    }

    Ok(entries)
}

fn resolve_definition<R: NativeMdxDictionaryReader>(
    reader: &mut R,
    query: &str,
) -> Result<Option<(String, String)>, NativeMdxLookupError> {
    let mut current = query.to_string();
    let mut resolved_key = query.to_string();

    for _ in 0..MAX_REDIRECT_HOPS {
        let Some((key, definition)) = reader.lookup(&current)? else {
            return Ok(None);
        };

        if definition.trim().is_empty() {
            return Ok(None);
        }

        if let Some(target) = redirect_target(&definition) {
            current = target;
            resolved_key = current.clone();
            continue;
        }

        if key.trim().is_empty() {
            return Ok(Some((resolved_key, definition)));
        }

        return Ok(Some((key, definition)));
    }

    Ok(None)
}

fn redirect_target(definition: &str) -> Option<String> {
    definition
        .trim()
        .strip_prefix("@@@LINK=")
        .map(str::trim)
        .filter(|target| !target.is_empty())
        .map(str::to_string)
}

fn decode_mdx_header_text(header_bytes: &[u8]) -> String {
    if header_bytes.len() % 2 == 0 {
        let utf16 = header_bytes
            .chunks_exact(2)
            .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]))
            .collect::<Vec<_>>();
        String::from_utf16_lossy(&utf16)
            .trim_end_matches('\0')
            .to_string()
    } else {
        String::from_utf8_lossy(header_bytes)
            .trim_end_matches('\0')
            .to_string()
    }
}

fn mdx_header_encryption_mode(
    header_text: &str,
) -> Result<MdxEncryptionMode, NativeMdxLookupError> {
    let attributes = mdx_header_attributes(header_text)?;
    let Some(value) = mdx_header_attribute(&attributes, "Encrypted") else {
        return Ok(MdxEncryptionMode::None);
    };

    let normalized = value.trim().to_ascii_lowercase();
    Ok(match normalized.as_str() {
        "" | "0" | "no" | "false" => MdxEncryptionMode::None,
        "yes" | "1" => MdxEncryptionMode::RecordBlock,
        "2" => MdxEncryptionMode::KeyInfoBlock,
        "3" => MdxEncryptionMode::RecordAndKeyInfoBlock,
        _ => MdxEncryptionMode::Unknown,
    })
}

fn mdx_header_attributes(
    header_text: &str,
) -> Result<HashMap<String, String>, NativeMdxLookupError> {
    rust_mdict::parse_header(header_text)
        .map_err(|error| NativeMdxLookupError::new(error.to_string()))
}

fn mdx_header_attribute(attributes: &HashMap<String, String>, attribute: &str) -> Option<String> {
    attributes
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(attribute))
        .map(|(_, value)| value.clone())
}

fn mdx_salsa20_8_block(
    key: &[u8],
    counter: u64,
) -> Result<[u8; SALSA_BLOCK_BYTES], NativeMdxLookupError> {
    let initial = mdx_salsa20_state(key, counter)?;
    let mut state = initial;

    for _ in 0..SALSA_ROUNDS / 2 {
        quarter_round(&mut state, 0, 4, 8, 12);
        quarter_round(&mut state, 5, 9, 13, 1);
        quarter_round(&mut state, 10, 14, 2, 6);
        quarter_round(&mut state, 15, 3, 7, 11);
        quarter_round(&mut state, 0, 1, 2, 3);
        quarter_round(&mut state, 5, 6, 7, 4);
        quarter_round(&mut state, 10, 11, 8, 9);
        quarter_round(&mut state, 15, 12, 13, 14);
    }

    let mut output = [0u8; SALSA_BLOCK_BYTES];
    for index in 0..state.len() {
        output[index * 4..index * 4 + 4]
            .copy_from_slice(&state[index].wrapping_add(initial[index]).to_le_bytes());
    }
    Ok(output)
}

fn mdx_salsa20_state(key: &[u8], counter: u64) -> Result<[u32; 16], NativeMdxLookupError> {
    let constants = match key.len() {
        16 => SALSA_TAU,
        32 => SALSA_SIGMA,
        _ => {
            return Err(NativeMdxLookupError::new(
                "MDX Salsa20/8 key must be 16 or 32 bytes",
            ));
        }
    };

    let mut state = [0u32; 16];
    state[0] = constants[0];
    state[5] = constants[1];
    state[10] = constants[2];
    state[15] = constants[3];
    read_key_words(&key[..16], &mut state[1..5]);
    if key.len() == 16 {
        read_key_words(key, &mut state[11..15]);
    } else {
        read_key_words(&key[16..], &mut state[11..15]);
    }
    state[6] = 0;
    state[7] = 0;
    state[8] = counter as u32;
    state[9] = (counter >> 32) as u32;

    Ok(state)
}

fn read_key_words(source: &[u8], destination: &mut [u32]) {
    for (slot, chunk) in destination.iter_mut().zip(source.chunks_exact(4)) {
        *slot = u32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]);
    }
}

fn quarter_round(state: &mut [u32; 16], a: usize, b: usize, c: usize, d: usize) {
    state[b] ^= state[a].wrapping_add(state[d]).rotate_left(7);
    state[c] ^= state[b].wrapping_add(state[a]).rotate_left(9);
    state[d] ^= state[c].wrapping_add(state[b]).rotate_left(13);
    state[a] ^= state[d].wrapping_add(state[c]).rotate_left(18);
}

//! MDD resource file implementation
//!
//! This module provides the MDD resource file parsing and locating functionality.
//! MDD files store binary resources like images, audio files, CSS, etc.

use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};
use std::path::Path;

use crate::error::Result;
use crate::mdict_base::MdictBase;
use crate::types::*;

/// MDD resource file parser
pub struct Mdd {
    /// Base parser
    base: MdictBase,
}

impl Mdd {
    /// Create a new MDD parser from file path
    pub fn new<P: AsRef<Path>>(filepath: P) -> Result<Self> {
        let base = MdictBase::new(filepath, FileExt::Mdd)?;
        Ok(Mdd { base })
    }

    /// Create a new MDD parser with a caller-provided key header transform.
    ///
    /// Credential-encrypted MDD files use the same encrypted key-header shape
    /// as MDX files. Callers that already own the credential algorithm can
    /// supply the decrypting transform here while reusing the MDD parser and
    /// raw resource lookup path unchanged.
    pub fn new_with_key_header_transform<P, F>(filepath: P, key_header_transform: F) -> Result<Self>
    where
        P: AsRef<Path>,
        F: FnOnce(&[u8], &DictHeader) -> Result<Vec<u8>> + 'static,
    {
        let base =
            MdictBase::new_with_key_header_transform(filepath, FileExt::Mdd, key_header_transform)?;
        Ok(Mdd { base })
    }

    /// Get dictionary header
    pub fn header(&self) -> &DictHeader {
        &self.base.header
    }

    /// Get dictionary metadata
    pub fn meta(&self) -> &DictMeta {
        &self.base.meta
    }

    /// Get total number of resources
    pub fn resource_count(&self) -> usize {
        self.base.keyword_list.len()
    }

    /// Get all resource keys
    pub fn resource_keys(&self) -> Vec<&str> {
        self.base
            .keyword_list
            .iter()
            .map(|k| k.key_text.as_str())
            .collect()
    }

    /// Locate a resource and return its data as base64
    pub fn locate(&mut self, resource_key: &str) -> Option<LookupResult> {
        self.locate_result(resource_key).ok().flatten()
    }

    /// Locate a resource and return base64 data, preserving IO/decompression errors.
    pub fn locate_result(&mut self, resource_key: &str) -> Result<Option<LookupResult>> {
        let Some((resolved_key, data_bytes)) = self.locate_raw_result(resource_key)? else {
            return Ok(None);
        };

        Ok(Some(LookupResult {
            key_text: resolved_key,
            definition: BASE64.encode(&data_bytes),
        }))
    }

    /// Locate a resource and return raw bytes
    pub fn locate_raw(&mut self, resource_key: &str) -> Option<Vec<u8>> {
        self.locate_raw_result(resource_key)
            .ok()
            .flatten()
            .map(|(_, data)| data)
    }

    /// Locate a resource and return its resolved key plus raw bytes.
    pub fn locate_raw_result(&mut self, resource_key: &str) -> Result<Option<(String, Vec<u8>)>> {
        let Some(keyword_item) = self.base.lookup_mdd_resource_by_key(resource_key) else {
            return Ok(None);
        };
        let keyword_item = keyword_item.clone();
        let data = self.base.lookup_record_by_keyword(&keyword_item)?;
        Ok(Some((keyword_item.key_text, data)))
    }

    /// Locate a resource and return structured metadata plus raw bytes.
    pub fn locate_resource_result(&mut self, resource_key: &str) -> Result<Option<MddResource>> {
        let Some((key, data)) = self.locate_raw_result(resource_key)? else {
            return Ok(None);
        };

        Ok(Some(MddResource::from_key_and_data(key, data)))
    }

    /// Find resources with the given prefix
    pub fn prefix(&mut self, prefix: &str) -> Vec<LookupResult> {
        let prefix = normalize_mdd_prefix(prefix);
        // Clone keywords to avoid borrowing issues
        let keywords: Vec<KeyWordItem> = self
            .base
            .get_prefix_keywords(&prefix)
            .into_iter()
            .cloned()
            .collect();
        let mut results = Vec::new();

        for keyword in keywords {
            if let Ok(data_bytes) = self.base.lookup_record_by_keyword(&keyword) {
                let definition = BASE64.encode(&data_bytes);

                results.push(LookupResult {
                    key_text: keyword.key_text,
                    definition,
                });
            }
        }

        results
    }

    /// Find resource keys with the given prefix (keys only, no data)
    pub fn prefix_keys(&self, prefix: &str) -> Vec<String> {
        let prefix = normalize_mdd_prefix(prefix);
        self.base
            .get_prefix_keywords(&prefix)
            .into_iter()
            .map(|k| k.key_text.clone())
            .collect()
    }

    /// Check if a resource exists
    pub fn contains(&self, resource_key: &str) -> bool {
        self.base.lookup_mdd_resource_by_key(resource_key).is_some()
    }

    /// Get the file path
    pub fn filepath(&self) -> &str {
        &self.base.filepath
    }

    /// Get resource info without loading data
    pub fn get_resource_info(&self, resource_key: &str) -> Option<ResourceInfo> {
        let keyword_item = self.base.lookup_mdd_resource_by_key(resource_key)?;

        Some(ResourceInfo::from_key(keyword_item.key_text.clone()))
    }
}

fn normalize_mdd_prefix(prefix: &str) -> String {
    normalize_mdd_resource_key(prefix)
}

#[cfg(test)]
impl Mdd {
    fn from_base_for_test(base: MdictBase) -> Self {
        Self { base }
    }
}

/// Resource information
#[derive(Debug, Clone, Eq, PartialEq)]
pub struct ResourceInfo {
    /// Resource key
    pub key: String,
    /// File extension
    pub extension: String,
    /// MIME type
    pub mime_type: String,
}

impl ResourceInfo {
    /// Create resource metadata from a resolved MDD resource key.
    pub fn from_key(key: impl Into<String>) -> Self {
        let key = key.into();
        let extension = extension_for_mdd_resource_key(&key);
        let mime_type = mime_type_for_mdd_resource_key(&key).to_string();
        Self {
            key,
            extension,
            mime_type,
        }
    }
}

/// MDD resource data and metadata.
#[derive(Debug, Clone, Eq, PartialEq)]
pub struct MddResource {
    /// Resolved resource key from the MDD file.
    pub key: String,
    /// Raw resource payload bytes.
    pub data: Vec<u8>,
    /// File extension inferred from the resolved key.
    pub extension: String,
    /// MIME type inferred from the resolved key.
    pub mime_type: String,
}

impl MddResource {
    /// Create resource data and metadata from a resolved MDD resource key.
    pub fn from_key_and_data(key: impl Into<String>, data: Vec<u8>) -> Self {
        let key = key.into();
        let info = ResourceInfo::from_key(key.clone());
        Self {
            key,
            data,
            extension: info.extension,
            mime_type: info.mime_type,
        }
    }
}

/// Normalize an MDD resource key to the canonical leading-backslash form.
pub fn normalize_mdd_resource_key(resource_key: &str) -> String {
    let trimmed = resource_key.trim().replace('/', "\\");
    if trimmed.is_empty() || trimmed.starts_with('\\') {
        trimmed
    } else {
        format!("\\{trimmed}")
    }
}

/// Infer the MIME type for an MDD resource key.
pub fn mime_type_for_mdd_resource_key(resource_key: &str) -> &'static str {
    match extension_for_mdd_resource_key(resource_key).as_str() {
        "jpg" | "jpeg" => "image/jpeg",
        "png" => "image/png",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        "svg" => "image/svg+xml",
        "webp" => "image/webp",
        "ico" => "image/x-icon",
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "ogg" => "audio/ogg",
        "spx" => "audio/speex",
        "css" => "text/css",
        "js" => "application/javascript",
        "html" | "htm" => "text/html",
        "ttf" => "font/ttf",
        "woff" => "font/woff",
        "woff2" => "font/woff2",
        "eot" => "application/vnd.ms-fontobject",
        _ => "application/octet-stream",
    }
}

fn extension_for_mdd_resource_key(resource_key: &str) -> String {
    resource_key
        .rsplit(['\\', '/'])
        .next()
        .and_then(|file_name| file_name.rsplit_once('.').map(|(_, extension)| extension))
        .map(str::to_lowercase)
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};
    use flate2::{write::ZlibEncoder, Compression};
    use std::io::{Seek, SeekFrom, Write};

    use crate::error::MdictError;
    use crate::mdict_base::MdictBase;
    use crate::ripemd128::ripemd128;
    use crate::types::{FileExt, KeyWordItem, RecordInfo};

    use super::{mime_type_for_mdd_resource_key, normalize_mdd_resource_key, Mdd};

    #[test]
    fn test_mdd_creation() {
        // This test requires an actual MDD file
        // let mdd = Mdd::new("test.mdd");
        // assert!(mdd.is_ok());
    }

    #[test]
    fn mdd_new_can_read_minimal_v2_resource_file() {
        let mut file = tempfile::NamedTempFile::new().expect("temp MDD file");
        write_minimal_mdd(
            file.as_file_mut(),
            &[
                (r"\images\logo.png", b"\x89PNG".as_slice()),
                (r"\payload", b"raw".as_slice()),
                (r"\styles\dict.css", b"body{}".as_slice()),
            ],
        );
        file.as_file_mut().flush().expect("flush MDD file");

        let mut mdd = Mdd::new(file.path()).expect("minimal MDD should open");

        assert_eq!(mdd.resource_count(), 3);
        assert!(mdd.contains("images/logo.png"));
        assert_eq!(
            mdd.resource_keys(),
            vec![r"\images\logo.png", r"\payload", r"\styles\dict.css"]
        );
        assert_eq!(mdd.prefix_keys("styles/"), vec![r"\styles\dict.css"]);
        assert_eq!(mdd.prefix_keys("payload"), vec![r"\payload"]);

        let (resolved_key, data) = mdd
            .locate_raw_result("/styles/dict.css")
            .expect("raw MDD lookup should not fail")
            .expect("CSS resource should exist");
        assert_eq!(resolved_key, r"\styles\dict.css");
        assert_eq!(data, b"body{}");

        let resource = mdd
            .locate_resource_result("images/logo.png")
            .expect("structured MDD resource lookup should not fail")
            .expect("PNG resource should exist");
        assert_eq!(resource.key, r"\images\logo.png");
        assert_eq!(resource.data, b"\x89PNG");
        assert_eq!(resource.extension, "png");
        assert_eq!(resource.mime_type, "image/png");

        let located = mdd
            .locate("images/logo.png")
            .expect("PNG resource should exist");
        assert_eq!(located.key_text, r"\images\logo.png");
        assert_eq!(located.definition, "iVBORw==");

        let css_info = mdd
            .get_resource_info("styles/dict.css")
            .expect("CSS resource info should exist");
        assert_eq!(css_info.extension, "css");
        assert_eq!(css_info.mime_type, "text/css");

        let payload_info = mdd
            .get_resource_info("payload")
            .expect("extensionless resource info should exist");
        assert_eq!(payload_info.extension, "");
        assert_eq!(payload_info.mime_type, "application/octet-stream");

        assert_eq!(
            normalize_mdd_resource_key("images/logo.png"),
            r"\images\logo.png"
        );
        assert_eq!(
            normalize_mdd_resource_key(r"\styles\dict.css"),
            r"\styles\dict.css"
        );
        assert_eq!(
            mime_type_for_mdd_resource_key(r"\styles\dict.css"),
            "text/css"
        );
    }

    #[test]
    fn mdd_new_can_read_zlib_record_block_with_non_ascii_and_large_resources() {
        let mut file = tempfile::NamedTempFile::new().expect("temp compressed MDD file");
        let logo = large_patterned_payload(b"\x89PNG", 8192);
        let audio = large_patterned_payload(b"ID3", 4096);
        write_minimal_mdd_with_record_block(
            file.as_file_mut(),
            &[
                (r"\audio\発音.mp3", audio.as_slice()),
                (r"\images\标志.png", logo.as_slice()),
                (
                    r"\styles\主题.css",
                    "body{font-family:\"Noto Sans\"}".as_bytes(),
                ),
            ],
            zlib_block,
        );
        file.as_file_mut().flush().expect("flush MDD file");

        let mut mdd = Mdd::new(file.path()).expect("compressed MDD should open");

        assert_eq!(mdd.resource_count(), 3);
        assert!(mdd.contains("images/标志.png"));
        assert_eq!(mdd.prefix_keys("images/标"), vec![r"\images\标志.png"]);

        let image = mdd
            .locate_resource_result("images/标志.png")
            .expect("non-ASCII image lookup should not fail")
            .expect("non-ASCII image resource should exist");
        assert_eq!(image.key, r"\images\标志.png");
        assert_eq!(image.mime_type, "image/png");
        assert_eq!(image.data, logo);

        let audio_result = mdd.locate("audio/発音.mp3").expect("audio should exist");
        assert_eq!(audio_result.key_text, r"\audio\発音.mp3");
        assert_eq!(
            BASE64
                .decode(audio_result.definition.as_bytes())
                .expect("MDD locate output should be standard base64"),
            audio
        );

        let css_info = mdd
            .get_resource_info("styles/主题.css")
            .expect("CSS resource info should exist");
        assert_eq!(css_info.key, r"\styles\主题.css");
        assert_eq!(css_info.mime_type, "text/css");
    }

    #[test]
    fn locate_raw_result_can_span_multiple_record_blocks() {
        let mut file = tempfile::tempfile().expect("tempfile");
        file.write_all(&uncompressed_record_block(b"abcde"))
            .expect("first record block");
        file.write_all(&uncompressed_record_block(b"fghij"))
            .expect("second record block");
        file.seek(SeekFrom::Start(0)).expect("rewind");

        let mut base = MdictBase::from_test_file(file, FileExt::Mdd);
        base.keyword_list = vec![KeyWordItem {
            record_start_offset: 3,
            record_end_offset: 8,
            key_text: r"\images\cross.png".to_string(),
            key_block_idx: 0,
        }];
        base.record_info_list = vec![
            RecordInfo {
                pack_size: 13,
                pack_accumulate_offset: 0,
                unpack_size: 5,
                unpack_accumulate_offset: 0,
            },
            RecordInfo {
                pack_size: 13,
                pack_accumulate_offset: 13,
                unpack_size: 5,
                unpack_accumulate_offset: 5,
            },
        ];

        let mut mdd = Mdd::from_base_for_test(base);
        let (resolved_key, data) = mdd
            .locate_raw_result("images/cross.png")
            .expect("cross-block MDD resource lookup")
            .expect("resource should exist");

        assert_eq!(resolved_key, r"\images\cross.png");
        assert_eq!(data, b"defgh");
    }

    #[test]
    fn new_with_key_header_transform_reads_record_encrypted_mdd_resource() {
        let mut file = tempfile::NamedTempFile::new().expect("temp encrypted MDD file");
        write_record_encrypted_mdd_with_xor_key_header(
            file.as_file_mut(),
            &[(r"\images\secret.png", b"\x89PNG".as_slice())],
        );
        file.as_file_mut()
            .flush()
            .expect("flush encrypted MDD file");

        let without_transform = match Mdd::new(file.path()) {
            Ok(_) => panic!("encrypted MDD should need key"),
            Err(error) => error,
        };
        assert!(matches!(
            without_transform,
            MdictError::EncryptedFileRequiresPasscode
        ));

        let mut mdd = Mdd::new_with_key_header_transform(file.path(), |encrypted, header| {
            assert_eq!(header.get("Encrypted").map(String::as_str), Some("1"));
            Ok(xor_key_header(encrypted))
        })
        .expect("encrypted MDD should open with caller transform");

        let resource = mdd
            .locate_resource_result("images/secret.png")
            .expect("encrypted MDD lookup should not fail")
            .expect("encrypted MDD resource should exist");

        assert_eq!(resource.key, r"\images\secret.png");
        assert_eq!(resource.data, b"\x89PNG");
        assert_eq!(resource.mime_type, "image/png");
    }

    #[test]
    fn mdd_lookup_respects_case_sensitive_and_strip_key_headers() {
        let mut case_sensitive = MdictBase::from_test_file(
            tempfile::tempfile().expect("case-sensitive MDD file"),
            FileExt::Mdd,
        );
        case_sensitive
            .header
            .insert("KeyCaseSensitive".to_string(), "Yes".to_string());
        case_sensitive.keyword_list = vec![KeyWordItem {
            record_start_offset: 0,
            record_end_offset: 0,
            key_text: r"\Images\Logo.PNG".to_string(),
            key_block_idx: 0,
        }];
        let case_sensitive = Mdd::from_base_for_test(case_sensitive);

        assert!(case_sensitive.contains(r"\Images\Logo.PNG"));
        assert!(!case_sensitive.contains("images/logo.png"));

        let mut no_strip = MdictBase::from_test_file(
            tempfile::tempfile().expect("no-strip MDD file"),
            FileExt::Mdd,
        );
        no_strip
            .header
            .insert("StripKey".to_string(), "No".to_string());
        no_strip.keyword_list = vec![KeyWordItem {
            record_start_offset: 0,
            record_end_offset: 0,
            key_text: r"\images\logo large.png".to_string(),
            key_block_idx: 0,
        }];
        let no_strip = Mdd::from_base_for_test(no_strip);

        assert!(no_strip.contains("images/logo large.png"));
        assert!(!no_strip.contains("images/logolarge.png"));
        assert_eq!(
            no_strip.prefix_keys("images/logo "),
            vec![r"\images\logo large.png"]
        );
        assert!(no_strip.prefix_keys("images/logol").is_empty());
    }

    fn uncompressed_record_block(payload: &[u8]) -> Vec<u8> {
        let mut block = vec![0, 0, 0, 0, 0, 0, 0, 0];
        block.extend_from_slice(payload);
        block
    }

    fn write_minimal_mdd(file: &mut std::fs::File, resources: &[(&str, &[u8])]) {
        write_minimal_mdd_with_record_block(file, resources, uncompressed_record_block);
    }

    fn write_record_encrypted_mdd_with_xor_key_header(
        file: &mut std::fs::File,
        resources: &[(&str, &[u8])],
    ) {
        assert!(!resources.is_empty());

        let header_text = r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" />"#;
        let header_bytes = utf16le(header_text);
        write_u32_be(file, header_bytes.len() as u32);
        file.write_all(&header_bytes)
            .expect("encrypted MDD header bytes");
        file.write_all(&[0, 0, 0, 0])
            .expect("encrypted MDD header checksum");

        let mut key_block_payload = Vec::new();
        let mut record_payload = Vec::new();
        for (key, data) in resources {
            write_u64_be_vec(&mut key_block_payload, record_payload.len() as u64);
            key_block_payload.extend_from_slice(&utf16le(key));
            key_block_payload.extend_from_slice(&[0, 0]);
            record_payload.extend_from_slice(data);
        }

        let key_block = uncompressed_record_block(&key_block_payload);
        let key_info_payload = key_info_payload(
            resources.first().expect("first resource").0,
            resources.last().expect("last resource").0,
            resources.len() as u64,
            key_block.len() as u64,
            key_block_payload.len() as u64,
        );
        let key_info = zlib_block(&key_info_payload);

        let mut key_header = Vec::new();
        write_u64_be_vec(&mut key_header, 1);
        write_u64_be_vec(&mut key_header, resources.len() as u64);
        write_u64_be_vec(&mut key_header, key_info_payload.len() as u64);
        write_u64_be_vec(&mut key_header, key_info.len() as u64);
        write_u64_be_vec(&mut key_header, key_block.len() as u64);
        file.write_all(&xor_key_header(&key_header))
            .expect("encrypted MDD key header");
        file.write_all(&[0, 0, 0, 0])
            .expect("encrypted MDD key header checksum");

        file.write_all(&key_info).expect("encrypted MDD key info");
        file.write_all(&key_block).expect("encrypted MDD key block");

        let record_block = mdx_encrypt_block(&uncompressed_record_block(&record_payload));
        write_u64_be(file, 1);
        write_u64_be(file, resources.len() as u64);
        write_u64_be(file, 16);
        write_u64_be(file, record_block.len() as u64);
        write_u64_be(file, record_block.len() as u64);
        write_u64_be(file, record_payload.len() as u64);
        file.write_all(&record_block)
            .expect("encrypted MDD record block");
    }

    fn write_minimal_mdd_with_record_block(
        file: &mut std::fs::File,
        resources: &[(&str, &[u8])],
        record_block_builder: fn(&[u8]) -> Vec<u8>,
    ) {
        assert!(!resources.is_empty());

        let header_text = r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" KeyCaseSensitive="No" StripKey="Yes" />"#;
        let header_bytes = utf16le(header_text);
        write_u32_be(file, header_bytes.len() as u32);
        file.write_all(&header_bytes).expect("header bytes");
        file.write_all(&[0, 0, 0, 0]).expect("header checksum");

        let mut key_block_payload = Vec::new();
        let mut record_payload = Vec::new();
        for (key, data) in resources {
            write_u64_be_vec(&mut key_block_payload, record_payload.len() as u64);
            key_block_payload.extend_from_slice(&utf16le(key));
            key_block_payload.extend_from_slice(&[0, 0]);
            record_payload.extend_from_slice(data);
        }

        let key_block = uncompressed_record_block(&key_block_payload);
        let key_info_payload = key_info_payload(
            resources.first().expect("first resource").0,
            resources.last().expect("last resource").0,
            resources.len() as u64,
            key_block.len() as u64,
            key_block_payload.len() as u64,
        );
        let key_info = zlib_block(&key_info_payload);

        write_u64_be(file, 1);
        write_u64_be(file, resources.len() as u64);
        write_u64_be(file, key_info_payload.len() as u64);
        write_u64_be(file, key_info.len() as u64);
        write_u64_be(file, key_block.len() as u64);
        file.write_all(&[0, 0, 0, 0]).expect("key header checksum");

        file.write_all(&key_info).expect("key info");
        file.write_all(&key_block).expect("key block");

        let record_block = record_block_builder(&record_payload);
        write_u64_be(file, 1);
        write_u64_be(file, resources.len() as u64);
        write_u64_be(file, 16);
        write_u64_be(file, record_block.len() as u64);
        write_u64_be(file, record_block.len() as u64);
        write_u64_be(file, record_payload.len() as u64);
        file.write_all(&record_block).expect("record block");
    }

    fn large_patterned_payload(prefix: &[u8], len: usize) -> Vec<u8> {
        let mut payload = Vec::with_capacity(len.max(prefix.len()));
        payload.extend_from_slice(prefix);
        while payload.len() < len {
            payload.push((payload.len() % 251) as u8);
        }
        payload
    }

    fn key_info_payload(
        first_key: &str,
        last_key: &str,
        resource_count: u64,
        key_block_pack_size: u64,
        key_block_unpack_size: u64,
    ) -> Vec<u8> {
        let mut payload = Vec::new();
        write_u64_be_vec(&mut payload, resource_count);
        write_u16_be_vec(&mut payload, utf16_code_units(first_key) as u16);
        payload.extend_from_slice(&utf16le(first_key));
        payload.extend_from_slice(&[0, 0]);
        write_u16_be_vec(&mut payload, utf16_code_units(last_key) as u16);
        payload.extend_from_slice(&utf16le(last_key));
        payload.extend_from_slice(&[0, 0]);
        write_u64_be_vec(&mut payload, key_block_pack_size);
        write_u64_be_vec(&mut payload, key_block_unpack_size);
        payload
    }

    fn zlib_block(payload: &[u8]) -> Vec<u8> {
        let mut encoder = ZlibEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(payload).expect("compress key info");
        let compressed = encoder.finish().expect("finish compression");
        let mut block = vec![2, 0, 0, 0, 0, 0, 0, 0];
        block.extend_from_slice(&compressed);
        block
    }

    fn mdx_encrypt_block(plain_block: &[u8]) -> Vec<u8> {
        let mut key_input = [0u8; 8];
        key_input[..4].copy_from_slice(&plain_block[4..8]);
        key_input[4] ^= 0x95;
        key_input[5] ^= 0x36;
        let key = ripemd128(&key_input);

        let mut encrypted = Vec::with_capacity(plain_block.len());
        encrypted.extend_from_slice(&plain_block[..8]);
        encrypted.extend_from_slice(&mdx_fast_encrypt(&plain_block[8..], &key));
        encrypted
    }

    fn mdx_fast_encrypt(data: &[u8], key: &[u8]) -> Vec<u8> {
        let mut output = Vec::with_capacity(data.len());
        let mut previous = 0x36u8;
        for (index, byte) in data.iter().enumerate() {
            let encrypted =
                (byte ^ previous ^ ((index & 0xff) as u8) ^ key[index % key.len()]).rotate_right(4);
            previous = encrypted;
            output.push(encrypted);
        }
        output
    }

    fn xor_key_header(bytes: &[u8]) -> Vec<u8> {
        bytes.iter().map(|byte| byte ^ 0xa5).collect()
    }

    fn utf16le(value: &str) -> Vec<u8> {
        value
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect::<Vec<_>>()
    }

    fn utf16_code_units(value: &str) -> usize {
        value.encode_utf16().count()
    }

    fn write_u16_be_vec(output: &mut Vec<u8>, value: u16) {
        output.extend_from_slice(&value.to_be_bytes());
    }

    fn write_u64_be_vec(output: &mut Vec<u8>, value: u64) {
        output.extend_from_slice(&value.to_be_bytes());
    }

    fn write_u32_be(file: &mut std::fs::File, value: u32) {
        file.write_all(&value.to_be_bytes()).expect("u32");
    }

    fn write_u64_be(file: &mut std::fs::File, value: u64) {
        file.write_all(&value.to_be_bytes()).expect("u64");
    }
}

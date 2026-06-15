//! MDict base implementation
//!
//! This module provides the core parsing functionality for MDX/MDD files.

use flate2::read::ZlibDecoder;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

use crate::error::{MdictError, Result};
use crate::lzo;
use crate::types::*;
use crate::utils::{self, bytes_to_number, decode_string, decode_utf16le, parse_header};

type KeyHeaderTransform = Box<dyn FnOnce(&[u8], &DictHeader) -> Result<Vec<u8>>>;

/// MDict base parser
pub struct MdictBase {
    /// File handle
    file: File,
    /// File path
    pub filepath: String,
    /// Dictionary metadata
    pub meta: DictMeta,
    /// Dictionary header attributes
    pub header: DictHeader,
    /// Key header information
    pub key_header: KeyHeader,
    /// Key block info list
    pub key_info_list: Vec<KeyInfoItem>,
    /// Keyword list (all keywords)
    pub keyword_list: Vec<KeyWordItem>,
    /// Record header information
    pub record_header: RecordHeader,
    /// Record block info list
    pub record_info_list: Vec<RecordInfo>,

    // Internal offsets
    header_end_offset: u64,
    key_header_start_offset: u64,
    key_header_end_offset: u64,
    key_block_info_start_offset: u64,
    key_block_info_end_offset: u64,
    record_header_start_offset: u64,
    record_header_end_offset: u64,
    record_info_start_offset: u64,
    record_info_end_offset: u64,
    record_block_start_offset: u64,
    key_header_transform: Option<KeyHeaderTransform>,
}

impl MdictBase {
    /// Create a new MdictBase from file path
    pub fn new<P: AsRef<Path>>(filepath: P, ext: FileExt) -> Result<Self> {
        Self::new_with_optional_key_header_transform(filepath, ext, None)
    }

    /// Create a new MdictBase with a caller-provided key header transform.
    ///
    /// This is used for credential-encrypted MDX files whose key header must be
    /// decrypted before the ordinary MDict header fields can be parsed.
    pub fn new_with_key_header_transform<P, F>(
        filepath: P,
        ext: FileExt,
        key_header_transform: F,
    ) -> Result<Self>
    where
        P: AsRef<Path>,
        F: FnOnce(&[u8], &DictHeader) -> Result<Vec<u8>> + 'static,
    {
        Self::new_with_optional_key_header_transform(
            filepath,
            ext,
            Some(Box::new(key_header_transform)),
        )
    }

    fn new_with_optional_key_header_transform<P>(
        filepath: P,
        ext: FileExt,
        key_header_transform: Option<KeyHeaderTransform>,
    ) -> Result<Self>
    where
        P: AsRef<Path>,
    {
        let path = filepath.as_ref();
        let file = File::open(path)?;
        let filepath_str = path.to_string_lossy().to_string();

        let mut base = MdictBase {
            file,
            filepath: filepath_str,
            meta: DictMeta {
                ext,
                ..Default::default()
            },
            header: DictHeader::new(),
            key_header: KeyHeader::default(),
            key_info_list: Vec::new(),
            keyword_list: Vec::new(),
            record_header: RecordHeader::default(),
            record_info_list: Vec::new(),
            header_end_offset: 0,
            key_header_start_offset: 0,
            key_header_end_offset: 0,
            key_block_info_start_offset: 0,
            key_block_info_end_offset: 0,
            record_header_start_offset: 0,
            record_header_end_offset: 0,
            record_info_start_offset: 0,
            record_info_end_offset: 0,
            record_block_start_offset: 0,
            key_header_transform,
        };

        base.read_dict()?;
        Ok(base)
    }

    /// Read and parse the dictionary file
    fn read_dict(&mut self) -> Result<()> {
        // Step 1: Read header
        self.read_header()?;

        // Step 2: Read key header
        self.read_key_header()?;

        // Step 3: Read key block info
        self.read_key_infos()?;

        // Step 4: Read all key blocks
        self.read_key_blocks()?;

        // Step 5: Read record header
        self.read_record_header()?;

        // Step 6: Read record block info
        self.read_record_infos()?;

        self.sort_keywords_for_lookup();

        Ok(())
    }

    /// Read buffer from file at offset
    fn read_buffer(&mut self, offset: u64, length: usize) -> Result<Vec<u8>> {
        self.file.seek(SeekFrom::Start(offset))?;
        let mut buffer = vec![0u8; length];
        self.file.read_exact(&mut buffer)?;
        Ok(buffer)
    }

    /// Read header section
    fn read_header(&mut self) -> Result<()> {
        // [0:4] - 4 bytes header length (big-endian)
        let header_size_buf = self.read_buffer(0, 4)?;
        let header_byte_size = bytes_to_number(&header_size_buf) as usize;

        // [4:header_byte_size + 4] - header content
        let header_buffer = self.read_buffer(4, header_byte_size)?;

        // [header_bytes_size + 4:header_bytes_size + 8] - Adler32 checksum (skip)
        self.header_end_offset = (header_byte_size + 4 + 4) as u64;
        self.key_header_start_offset = self.header_end_offset;

        // Decode UTF-16LE header text
        let header_text = decode_utf16le(&header_buffer)?;

        // Parse XML header attributes
        self.header = parse_header(&header_text)?;

        // Set default values
        if !self.header.contains_key("KeyCaseSensitive") {
            self.header
                .insert("KeyCaseSensitive".to_string(), "No".to_string());
        }
        if !self.header.contains_key("StripKey") {
            self.header
                .insert("StripKey".to_string(), "Yes".to_string());
        }

        // Determine encryption type
        self.meta.encrypt = parse_encrypt_type(self.header.get("Encrypted").map(String::as_str))?;

        // Determine version and number format
        let version_str = self
            .header
            .get("GeneratedByEngineVersion")
            .map(|s| s.as_str())
            .unwrap_or("1.2");
        self.meta.version = version_str.parse::<f64>().unwrap_or(1.2);

        if self.meta.version >= 2.0 {
            self.meta.num_width = 8;
            self.meta.num_fmt = NumFmt::Uint64;
        } else {
            self.meta.num_width = 4;
            self.meta.num_fmt = NumFmt::Uint32;
        }

        // Determine encoding
        let encoding_str = self
            .header
            .get("Encoding")
            .map(|s| s.to_lowercase())
            .unwrap_or_default();

        self.meta.encoding = match encoding_str.as_str() {
            "" => Encoding::Utf8,
            "gbk" | "gb2312" => Encoding::Gb18030,
            "big5" => Encoding::Big5,
            "utf16" | "utf-16" => Encoding::Utf16Le,
            _ => Encoding::Utf8,
        };

        // MDD files always use UTF-16LE
        if self.meta.ext == FileExt::Mdd {
            self.meta.encoding = Encoding::Utf16Le;
        }

        Ok(())
    }

    /// Read key header section
    fn read_key_header(&mut self) -> Result<()> {
        self.key_header_start_offset = self.header_end_offset;

        // Version >= 2.0: 5 * 8 bytes, otherwise 4 * 4 bytes
        let header_meta_size = if self.meta.version >= 2.0 {
            8 * 5
        } else {
            4 * 4
        };
        let mut key_header_buf =
            self.read_buffer(self.key_header_start_offset, header_meta_size)?;

        // Check encryption
        if self.meta.encrypt == EncryptType::RecordBlock && self.meta.passcode.is_none() {
            let Some(transform) = self.key_header_transform.take() else {
                return Err(MdictError::EncryptedFileRequiresPasscode);
            };

            key_header_buf = transform(&key_header_buf, &self.header)?;
            if key_header_buf.len() != header_meta_size {
                return Err(MdictError::DecryptionError(format!(
                    "decrypted key header length {} did not match expected length {header_meta_size}",
                    key_header_buf.len()
                )));
            }
        }

        let mut offset = 0;
        let num_width = self.meta.num_width;

        // [0:8/4] - Number of keyword blocks
        self.key_header.keyword_blocks_num =
            bytes_to_number(&key_header_buf[offset..offset + num_width]);
        offset += num_width;

        // [8:16/4:8] - Total number of keywords
        self.key_header.keyword_num = bytes_to_number(&key_header_buf[offset..offset + num_width]);
        offset += num_width;

        // [16:24/8:12] - KeyBlockInfo decompressed size (v2.0+ only)
        if self.meta.version >= 2.0 {
            self.key_header.key_info_unpack_size =
                bytes_to_number(&key_header_buf[offset..offset + num_width]);
            offset += num_width;
        }

        // [24:32/12:16] - KeyBlockInfo compressed size
        self.key_header.key_info_packed_size =
            bytes_to_number(&key_header_buf[offset..offset + num_width]);
        offset += num_width;

        // [32:40/16:20] - Total size of all KeyBlocks
        self.key_header.keyword_block_packed_size =
            bytes_to_number(&key_header_buf[offset..offset + num_width]);

        // Calculate end offset (v2.0 has additional 4 bytes checksum)
        self.key_header_end_offset = self.key_header_start_offset + header_meta_size as u64;
        if self.meta.version >= 2.0 {
            self.key_header_end_offset += 4;
        }

        self.key_block_info_start_offset = self.key_header_end_offset;

        Ok(())
    }

    /// Read key block info section
    fn read_key_infos(&mut self) -> Result<()> {
        let key_info_size = self.key_header.key_info_packed_size as usize;
        let mut key_info_buf = self.read_buffer(self.key_block_info_start_offset, key_info_size)?;

        // Handle v2.0 compression and encryption
        if self.meta.version >= 2.0 {
            // Check compression type
            let comp_type = CompressionType::from_bytes(&key_info_buf);

            // Handle encryption
            if self.meta.encrypt == EncryptType::KeyInfoBlock {
                key_info_buf = utils::mdx_decrypt(&key_info_buf);
            }

            // Handle compression
            if comp_type == Some(CompressionType::Zlib) {
                let compressed_data = &key_info_buf[8..];
                let mut decoder = ZlibDecoder::new(compressed_data);
                let mut decompressed = Vec::new();
                decoder.read_to_end(&mut decompressed)?;
                key_info_buf = decompressed;
            }
        }

        // Decode key block info
        self.key_info_list = self.decode_key_info(&key_info_buf)?;

        self.key_block_info_end_offset = self.key_block_info_start_offset + key_info_size as u64;

        Ok(())
    }

    /// Decode key block info buffer
    fn decode_key_info(&self, key_info_buf: &[u8]) -> Result<Vec<KeyInfoItem>> {
        let key_block_num = self.key_header.keyword_blocks_num as usize;
        let mut key_block_info_list = Vec::with_capacity(key_block_num);

        let mut entries_count = 0u64;
        let mut kb_count = 0usize;
        let mut index_offset = 0usize;
        let mut kb_pack_size_accu = 0u64;
        let mut kb_unpack_size_accu = 0u64;

        let num_width = self.meta.num_width;
        let is_utf16 = self.meta.encoding == Encoding::Utf16Le;

        while kb_count < key_block_num {
            // Read number of entries in this block
            let block_word_count =
                bytes_to_number(&key_info_buf[index_offset..index_offset + num_width]);
            index_offset += num_width;

            // Read first word size
            let first_word_size_raw =
                bytes_to_number(&key_info_buf[index_offset..index_offset + num_width / 4]) as usize;
            index_offset += num_width / 4;

            // Adjust for encoding
            let first_word_size = if self.meta.version >= 2.0 {
                if is_utf16 {
                    (first_word_size_raw + 1) * 2
                } else {
                    first_word_size_raw + 1
                }
            } else if is_utf16 {
                first_word_size_raw * 2
            } else {
                first_word_size_raw
            };

            // Read first word
            let first_word_buffer = &key_info_buf[index_offset..index_offset + first_word_size];
            index_offset += first_word_size;

            // Read last word size
            let last_word_size_raw =
                bytes_to_number(&key_info_buf[index_offset..index_offset + num_width / 4]) as usize;
            index_offset += num_width / 4;

            let last_word_size = if self.meta.version >= 2.0 {
                if is_utf16 {
                    (last_word_size_raw + 1) * 2
                } else {
                    last_word_size_raw + 1
                }
            } else if is_utf16 {
                last_word_size_raw * 2
            } else {
                last_word_size_raw
            };

            // Read last word
            let last_word_buffer = &key_info_buf[index_offset..index_offset + last_word_size];
            index_offset += last_word_size;

            // Read pack size
            let pack_size = bytes_to_number(&key_info_buf[index_offset..index_offset + num_width]);
            index_offset += num_width;

            // Read unpack size
            let unpack_size =
                bytes_to_number(&key_info_buf[index_offset..index_offset + num_width]);
            index_offset += num_width;

            // Decode first and last keys
            let first_key = decode_string(first_word_buffer, self.meta.encoding)
                .unwrap_or_default()
                .trim_end_matches('\0')
                .to_string();
            let last_key = decode_string(last_word_buffer, self.meta.encoding)
                .unwrap_or_default()
                .trim_end_matches('\0')
                .to_string();

            key_block_info_list.push(KeyInfoItem {
                first_key,
                last_key,
                key_block_pack_size: pack_size,
                key_block_pack_accumulator: kb_pack_size_accu,
                key_block_unpack_size: unpack_size,
                key_block_unpack_accumulator: kb_unpack_size_accu,
                key_block_entries_num: block_word_count,
                key_block_entries_num_accumulator: entries_count,
                key_block_info_index: kb_count,
            });

            kb_count += 1;
            entries_count += block_word_count;
            kb_pack_size_accu += pack_size;
            kb_unpack_size_accu += unpack_size;
        }

        Ok(key_block_info_list)
    }

    /// Read all key blocks
    fn read_key_blocks(&mut self) -> Result<()> {
        let key_block_start = self.key_block_info_end_offset;

        // Collect key info data first to avoid borrowing issues
        let key_info_data: Vec<(usize, u64, usize, usize)> = self
            .key_info_list
            .iter()
            .enumerate()
            .map(|(idx, ki)| {
                (
                    idx,
                    ki.key_block_pack_accumulator,
                    ki.key_block_pack_size as usize,
                    ki.key_block_unpack_size as usize,
                )
            })
            .collect();

        for (idx, pack_accum, packed_size, unpack_size) in key_info_data {
            let offset = key_block_start + pack_accum;

            let packed_buf = self.read_buffer(offset, packed_size)?;
            let unpacked_buf = self.unpack_key_block(&packed_buf, unpack_size)?;

            let mut keywords = self.split_key_block(&unpacked_buf, idx)?;
            self.keyword_list.append(&mut keywords);
        }

        // Set record end offsets
        for i in 1..self.keyword_list.len() {
            self.keyword_list[i - 1].record_end_offset = self.keyword_list[i].record_start_offset;
        }

        Ok(())
    }

    /// Unpack a key block
    fn unpack_key_block(&self, packed_buf: &[u8], unpack_size: usize) -> Result<Vec<u8>> {
        let comp_type = CompressionType::from_bytes(packed_buf).ok_or_else(|| {
            MdictError::InvalidCompressionType(u32::from_le_bytes([
                packed_buf[0],
                packed_buf[1],
                packed_buf[2],
                packed_buf[3],
            ]))
        })?;

        match comp_type {
            CompressionType::None => Ok(packed_buf[8..].to_vec()),
            CompressionType::Lzo => lzo::decompress(&packed_buf[8..], unpack_size),
            CompressionType::Zlib => {
                let mut decoder = ZlibDecoder::new(&packed_buf[8..]);
                let mut decompressed = Vec::new();
                decoder.read_to_end(&mut decompressed)?;
                Ok(decompressed)
            }
        }
    }

    /// Split key block into individual keywords
    fn split_key_block(&self, key_block: &[u8], key_block_idx: usize) -> Result<Vec<KeyWordItem>> {
        let width = if self.meta.encoding == Encoding::Utf16Le || self.meta.ext == FileExt::Mdd {
            2
        } else {
            1
        };

        let mut key_list = Vec::new();
        let mut key_start_index = 0;
        let num_width = self.meta.num_width;

        while key_start_index < key_block.len() {
            // Read record offset
            if key_start_index + num_width > key_block.len() {
                break;
            }
            let meaning_offset =
                bytes_to_number(&key_block[key_start_index..key_start_index + num_width]);

            // Find key end (null terminator)
            let mut key_end_index = None;
            let mut i = key_start_index + num_width;
            while i < key_block.len() {
                if (width == 1 && key_block[i] == 0)
                    || (width == 2
                        && i + 1 < key_block.len()
                        && key_block[i] == 0
                        && key_block[i + 1] == 0)
                {
                    key_end_index = Some(i);
                    break;
                }
                i += width;
            }

            let key_end = match key_end_index {
                Some(idx) => idx,
                None => break,
            };

            // Extract key text
            let key_text_buffer = &key_block[key_start_index + num_width..key_end];
            let key_text = decode_string(key_text_buffer, self.meta.encoding).unwrap_or_default();

            key_list.push(KeyWordItem {
                record_start_offset: meaning_offset,
                record_end_offset: 0, // Will be set later
                key_text,
                key_block_idx,
            });

            key_start_index = key_end + width;
        }

        Ok(key_list)
    }

    /// Read record header section
    fn read_record_header(&mut self) -> Result<()> {
        self.record_header_start_offset =
            self.key_block_info_end_offset + self.key_header.keyword_block_packed_size;

        let record_header_len = if self.meta.version >= 2.0 {
            4 * 8
        } else {
            4 * 4
        };
        self.record_header_end_offset = self.record_header_start_offset + record_header_len as u64;

        let record_header_buf =
            self.read_buffer(self.record_header_start_offset, record_header_len)?;

        let mut offset = 0;
        let num_width = self.meta.num_width;

        // [0:8/4] - Number of record blocks
        self.record_header.record_blocks_num =
            bytes_to_number(&record_header_buf[offset..offset + num_width]);
        offset += num_width;

        // [8:16/4:8] - Number of entries
        self.record_header.entries_num =
            bytes_to_number(&record_header_buf[offset..offset + num_width]);
        offset += num_width;

        // [16:24/8:12] - Record info compressed size
        self.record_header.record_info_comp_size =
            bytes_to_number(&record_header_buf[offset..offset + num_width]);
        offset += num_width;

        // [24:32/12:16] - Record block total compressed size
        self.record_header.record_block_comp_size =
            bytes_to_number(&record_header_buf[offset..offset + num_width]);

        Ok(())
    }

    /// Read record block info section
    fn read_record_infos(&mut self) -> Result<()> {
        self.record_info_start_offset = self.record_header_end_offset;

        let record_info_size = self.record_header.record_info_comp_size as usize;
        let record_info_buf = self.read_buffer(self.record_info_start_offset, record_info_size)?;

        let mut record_info_list = Vec::new();
        let mut offset = 0;
        let mut compressed_adder = 0u64;
        let mut decompression_adder = 0u64;
        let num_width = self.meta.num_width;

        for _ in 0..self.record_header.record_blocks_num {
            // Read pack size
            let pack_size = bytes_to_number(&record_info_buf[offset..offset + num_width]);
            offset += num_width;

            // Read unpack size
            let unpack_size = bytes_to_number(&record_info_buf[offset..offset + num_width]);
            offset += num_width;

            record_info_list.push(RecordInfo {
                pack_size,
                pack_accumulate_offset: compressed_adder,
                unpack_size,
                unpack_accumulate_offset: decompression_adder,
            });

            compressed_adder += pack_size;
            decompression_adder += unpack_size;
        }

        self.record_info_list = record_info_list;
        self.record_info_end_offset = self.record_info_start_offset + record_info_size as u64;
        self.record_block_start_offset = self.record_info_end_offset;

        Ok(())
    }

    /// Strip key for comparison
    pub fn strip(&self, key: &str) -> String {
        self.key_normalization().normalize(key)
    }

    /// Compare two keys
    pub fn compare_keys(&self, a: &str, b: &str) -> std::cmp::Ordering {
        self.key_normalization().compare(a, b)
    }

    /// Binary search for keyword by word
    pub fn lookup_keyword_by_word(&self, word: &str, is_associate: bool) -> Option<&KeyWordItem> {
        let list = &self.keyword_list;
        if list.is_empty() {
            return None;
        }

        match list.binary_search_by(|item| self.compare_keys(&item.key_text, word)) {
            Ok(index) => Some(&list[index]),
            Err(index) if is_associate => {
                let nearest = index.min(list.len().saturating_sub(1));
                Some(&list[nearest])
            }
            Err(_) => None,
        }
    }

    /// Look up an MDD resource key while preserving resource-file semantics.
    ///
    /// Ordinary MDict strip rules are useful as a fallback, but MDD resources
    /// can legitimately share the same stem with different extensions (for
    /// example `dict.css` and `dict.js`). Prefer exact canonical path matches
    /// inside the normalized-equal group before falling back to stripped lookup.
    pub fn lookup_mdd_resource_by_key(&self, resource_key: &str) -> Option<&KeyWordItem> {
        if self.meta.ext != FileExt::Mdd {
            return self.lookup_keyword_by_word(resource_key, false);
        }

        let list = &self.keyword_list;
        if list.is_empty() {
            return None;
        }

        let lookup_key = crate::mdd::normalize_mdd_resource_key(resource_key);
        let normalization = self.key_normalization();
        let normalized_lookup = normalization.normalize(&lookup_key);
        let index = list
            .binary_search_by(|item| {
                normalization
                    .normalize(&item.key_text)
                    .cmp(&normalized_lookup)
            })
            .ok()?;

        let mut group_start = index;
        while group_start > 0
            && normalization.compare(&list[group_start - 1].key_text, &lookup_key)
                == std::cmp::Ordering::Equal
        {
            group_start -= 1;
        }

        let mut group_end = index + 1;
        while group_end < list.len()
            && normalization.compare(&list[group_end].key_text, &lookup_key)
                == std::cmp::Ordering::Equal
        {
            group_end += 1;
        }

        list[group_start..group_end]
            .iter()
            .find(|item| self.mdd_resource_keys_equal(&item.key_text, &lookup_key))
            .or_else(|| list.get(index))
    }

    /// Find record block index by record start offset
    pub fn find_record_block_index(&self, record_start: u64) -> usize {
        let mut left = 0;
        let mut right = self.record_info_list.len();

        while left < right {
            let mid = left + (right - left) / 2;
            if record_start >= self.record_info_list[mid].unpack_accumulate_offset {
                left = mid + 1;
            } else {
                right = mid;
            }
        }

        if left > 0 {
            left - 1
        } else {
            0
        }
    }

    /// Lookup record by keyword item
    pub fn lookup_record_by_keyword(&mut self, item: &KeyWordItem) -> Result<Vec<u8>> {
        if self.record_info_list.is_empty() {
            return Err(MdictError::InvalidFormat(
                "record block info list is empty".to_string(),
            ));
        }

        let record_start = item.record_start_offset;
        let record_end = self.effective_record_end_offset(item)?;
        if record_end <= record_start {
            return Ok(Vec::new());
        }

        let start_block_index = self.find_record_block_index(record_start);
        let end_block_index = self.find_record_block_index(record_end.saturating_sub(1));
        let mut result = Vec::with_capacity((record_end - record_start) as usize);

        for block_index in start_block_index..=end_block_index {
            let (block_start, unpacked_buffer) = self.read_unpacked_record_block(block_index)?;
            let block_end = block_start + unpacked_buffer.len() as u64;
            let slice_start = record_start.max(block_start);
            let slice_end = record_end.min(block_end);

            if slice_start >= slice_end {
                continue;
            }

            let local_start = (slice_start - block_start) as usize;
            let local_end = (slice_end - block_start) as usize;
            result.extend_from_slice(&unpacked_buffer[local_start..local_end]);
        }

        Ok(result)
    }

    fn read_unpacked_record_block(&mut self, record_block_index: usize) -> Result<(u64, Vec<u8>)> {
        let record_info = self
            .record_info_list
            .get(record_block_index)
            .ok_or_else(|| {
                MdictError::InvalidFormat(format!(
                    "record block index {record_block_index} out of range"
                ))
            })?;

        let pack_accumulate_offset = record_info.pack_accumulate_offset;
        let pack_size = record_info.pack_size as usize;
        let unpack_size = record_info.unpack_size as usize;
        let unpack_accumulate_offset = record_info.unpack_accumulate_offset;

        let offset = self.record_block_start_offset + pack_accumulate_offset;
        let record_buffer = self.read_buffer(offset, pack_size)?;
        let unpacked_buffer = self.decompress_record_block(&record_buffer, unpack_size)?;

        Ok((unpack_accumulate_offset, unpacked_buffer))
    }

    fn effective_record_end_offset(&self, item: &KeyWordItem) -> Result<u64> {
        if item.record_end_offset > 0 {
            return Ok(item.record_end_offset);
        }

        self.record_info_list
            .last()
            .map(|info| info.unpack_accumulate_offset + info.unpack_size)
            .ok_or_else(|| MdictError::InvalidFormat("record block info list is empty".to_string()))
    }

    /// Decompress record block
    fn decompress_record_block(&self, record_buffer: &[u8], unpack_size: usize) -> Result<Vec<u8>> {
        let comp_type = CompressionType::from_bytes(record_buffer).ok_or_else(|| {
            MdictError::InvalidCompressionType(u32::from_le_bytes([
                record_buffer[0],
                record_buffer[1],
                record_buffer[2],
                record_buffer[3],
            ]))
        })?;

        match comp_type {
            CompressionType::None => {
                let data = if self.meta.encrypt == EncryptType::RecordBlock {
                    utils::mdx_decrypt(record_buffer)
                } else {
                    record_buffer.to_vec()
                };
                Ok(data[8..].to_vec())
            }
            CompressionType::Lzo => {
                let data = if self.meta.encrypt == EncryptType::RecordBlock {
                    utils::mdx_decrypt(record_buffer)
                } else {
                    record_buffer.to_vec()
                };
                lzo::decompress(&data[8..], unpack_size)
            }
            CompressionType::Zlib => {
                let data = if self.meta.encrypt == EncryptType::RecordBlock {
                    utils::mdx_decrypt(record_buffer)
                } else {
                    record_buffer.to_vec()
                };
                let mut decoder = ZlibDecoder::new(&data[8..]);
                let mut decompressed = Vec::new();
                decoder.read_to_end(&mut decompressed)?;
                Ok(decompressed)
            }
        }
    }

    /// Get keywords that start with the given prefix
    pub fn get_prefix_keywords(&self, prefix: &str) -> Vec<&KeyWordItem> {
        let normalization = self.key_normalization();
        let normalized_prefix = normalization.normalize(prefix);
        self.keyword_list
            .iter()
            .filter(|item| {
                normalization
                    .normalize(&item.key_text)
                    .starts_with(&normalized_prefix)
            })
            .collect()
    }

    /// Get associated keywords (same key block)
    pub fn get_associated_keywords(&self, word: &str) -> Vec<&KeyWordItem> {
        if let Some(item) = self.lookup_keyword_by_word(word, true) {
            let block_idx = item.key_block_idx;
            self.keyword_list
                .iter()
                .filter(|kw| kw.key_block_idx == block_idx)
                .collect()
        } else {
            Vec::new()
        }
    }

    fn sort_keywords_for_lookup(&mut self) {
        let normalization = self.key_normalization();
        self.keyword_list
            .sort_by(|a, b| normalization.compare(&a.key_text, &b.key_text));
    }

    fn key_normalization(&self) -> KeyNormalization {
        KeyNormalization {
            ext: self.meta.ext,
            strip_key: header_yes_no(&self.header, "StripKey", true),
            case_sensitive: header_yes_no(&self.header, "KeyCaseSensitive", false),
        }
    }

    fn mdd_resource_keys_equal(&self, a: &str, b: &str) -> bool {
        let a = crate::mdd::normalize_mdd_resource_key(a);
        let b = crate::mdd::normalize_mdd_resource_key(b);
        if header_yes_no(&self.header, "KeyCaseSensitive", false) {
            a == b
        } else {
            a.eq_ignore_ascii_case(&b)
        }
    }
}

#[cfg(test)]
impl MdictBase {
    pub(crate) fn from_test_file(file: File, ext: FileExt) -> Self {
        MdictBase {
            file,
            filepath: String::new(),
            meta: DictMeta {
                ext,
                ..Default::default()
            },
            header: DictHeader::new(),
            key_header: KeyHeader::default(),
            key_info_list: Vec::new(),
            keyword_list: Vec::new(),
            record_header: RecordHeader::default(),
            record_info_list: Vec::new(),
            header_end_offset: 0,
            key_header_start_offset: 0,
            key_header_end_offset: 0,
            key_block_info_start_offset: 0,
            key_block_info_end_offset: 0,
            record_header_start_offset: 0,
            record_header_end_offset: 0,
            record_info_start_offset: 0,
            record_info_end_offset: 0,
            record_block_start_offset: 0,
            key_header_transform: None,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct KeyNormalization {
    ext: FileExt,
    strip_key: bool,
    case_sensitive: bool,
}

impl KeyNormalization {
    fn normalize(self, key: &str) -> String {
        utils::normalize_mdict_key_for_lookup(
            key,
            self.ext == FileExt::Mdd,
            self.strip_key,
            self.case_sensitive,
        )
    }

    fn compare(self, a: &str, b: &str) -> std::cmp::Ordering {
        self.normalize(a).cmp(&self.normalize(b))
    }
}

fn header_yes_no(header: &DictHeader, key: &str, default: bool) -> bool {
    match header.get(key).map(|value| value.trim()) {
        Some(value) if value.eq_ignore_ascii_case("Yes") => true,
        Some(value) if value.eq_ignore_ascii_case("No") => false,
        _ => default,
    }
}

fn parse_encrypt_type(value: Option<&str>) -> Result<EncryptType> {
    let value = value.map(str::trim).unwrap_or_default();
    if value.is_empty() || value.eq_ignore_ascii_case("No") {
        return Ok(EncryptType::None);
    }
    if value.eq_ignore_ascii_case("Yes") {
        return Ok(EncryptType::RecordBlock);
    }

    match value {
        "0" => Ok(EncryptType::None),
        "1" => Ok(EncryptType::RecordBlock),
        "2" => Ok(EncryptType::KeyInfoBlock),
        _ => Err(MdictError::UnsupportedEncryptionType(value.to_string())),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keyword_sort_and_lookup_respect_strip_key_no_case_sensitive_header() {
        let mut base = test_base_with_header(
            &[("StripKey", "No"), ("KeyCaseSensitive", "Yes")],
            &["apple", "Apple"],
            FileExt::Mdx,
        );

        base.sort_keywords_for_lookup();

        assert_eq!(
            base.compare_keys("Apple", "apple"),
            std::cmp::Ordering::Less
        );
        assert_eq!(test_keyword_texts(&base), ["Apple", "apple"]);
        assert_eq!(
            base.lookup_keyword_by_word("Apple", false)
                .expect("exact uppercase key")
                .key_text,
            "Apple"
        );
        assert_eq!(
            base.lookup_keyword_by_word("apple", false)
                .expect("exact lowercase key")
                .key_text,
            "apple"
        );
        assert!(base.lookup_keyword_by_word("APPLE", false).is_none());
    }

    #[test]
    fn strip_key_yes_can_still_be_case_sensitive() {
        let mut base = test_base_with_header(
            &[("StripKey", "Yes"), ("KeyCaseSensitive", "Yes")],
            &["Co-Operate"],
            FileExt::Mdx,
        );

        base.sort_keywords_for_lookup();

        assert_eq!(base.strip("Co-Operate"), "CoOperate");
        assert_eq!(
            base.lookup_keyword_by_word("CoOperate", false)
                .expect("stripped exact key")
                .key_text,
            "Co-Operate"
        );
        assert!(base.lookup_keyword_by_word("cooperate", false).is_none());
    }

    #[test]
    fn prefix_uses_same_strip_rules_as_exact_lookup() {
        let mut base = test_base_with_header(&[], &["co-operate"], FileExt::Mdx);
        base.sort_keywords_for_lookup();

        assert_eq!(
            base.get_prefix_keywords("coo")
                .into_iter()
                .map(|item| item.key_text.as_str())
                .collect::<Vec<_>>(),
            ["co-operate"]
        );

        let mut no_strip =
            test_base_with_header(&[("StripKey", "No")], &["co-operate"], FileExt::Mdx);
        no_strip.sort_keywords_for_lookup();

        assert!(no_strip.get_prefix_keywords("coo").is_empty());
        assert_eq!(
            no_strip
                .get_prefix_keywords("co-")
                .into_iter()
                .map(|item| item.key_text.as_str())
                .collect::<Vec<_>>(),
            ["co-operate"]
        );
    }

    #[test]
    fn mdd_resource_lookup_prefers_exact_resource_path_over_stripped_collision() {
        let mut base = test_base_with_header(
            &[],
            &[r"\styles\dict.css", r"\styles\dict.js"],
            FileExt::Mdd,
        );
        base.sort_keywords_for_lookup();

        assert_eq!(
            base.lookup_mdd_resource_by_key("styles/dict.js")
                .expect("js resource")
                .key_text,
            r"\styles\dict.js"
        );
        assert_eq!(
            base.lookup_mdd_resource_by_key(r"/styles/dict.css")
                .expect("css resource")
                .key_text,
            r"\styles\dict.css"
        );
    }

    #[test]
    fn record_lookup_can_span_multiple_record_blocks() {
        use std::io::{Seek, SeekFrom, Write};

        let mut file = tempfile::tempfile().expect("tempfile");
        file.write_all(&uncompressed_record_block(b"abcde"))
            .expect("first record block");
        file.write_all(&uncompressed_record_block(b"fghij"))
            .expect("second record block");
        file.seek(SeekFrom::Start(0)).expect("rewind");

        let mut base = test_base_with_file(file, FileExt::Mdx);
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
        base.record_block_start_offset = 0;

        let item = KeyWordItem {
            record_start_offset: 3,
            record_end_offset: 8,
            key_text: "cross".to_string(),
            key_block_idx: 0,
        };

        assert_eq!(
            base.lookup_record_by_keyword(&item)
                .expect("cross-block record"),
            b"defgh"
        );
    }

    #[test]
    fn final_record_uses_total_unpacked_size_as_end_offset() {
        use std::io::{Seek, SeekFrom, Write};

        let mut file = tempfile::tempfile().expect("tempfile");
        file.write_all(&uncompressed_record_block(b"abcde"))
            .expect("first record block");
        file.write_all(&uncompressed_record_block(b"fghij"))
            .expect("second record block");
        file.seek(SeekFrom::Start(0)).expect("rewind");

        let mut base = test_base_with_file(file, FileExt::Mdx);
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
        base.record_block_start_offset = 0;

        let item = KeyWordItem {
            record_start_offset: 7,
            record_end_offset: 0,
            key_text: "tail".to_string(),
            key_block_idx: 0,
        };

        assert_eq!(
            base.lookup_record_by_keyword(&item).expect("tail record"),
            b"hij"
        );
    }

    #[test]
    fn encrypted_header_values_are_case_insensitive_and_explicitly_unsupported() {
        assert_eq!(parse_encrypt_type(None).unwrap(), EncryptType::None);
        assert_eq!(parse_encrypt_type(Some("")).unwrap(), EncryptType::None);
        assert_eq!(parse_encrypt_type(Some("no")).unwrap(), EncryptType::None);
        assert_eq!(
            parse_encrypt_type(Some("YES")).unwrap(),
            EncryptType::RecordBlock
        );
        assert_eq!(
            parse_encrypt_type(Some("1")).unwrap(),
            EncryptType::RecordBlock
        );
        assert_eq!(
            parse_encrypt_type(Some("2")).unwrap(),
            EncryptType::KeyInfoBlock
        );

        assert!(matches!(
            parse_encrypt_type(Some("3")).unwrap_err(),
            MdictError::UnsupportedEncryptionType(_)
        ));
        assert!(matches!(
            parse_encrypt_type(Some("surprise")).unwrap_err(),
            MdictError::UnsupportedEncryptionType(_)
        ));
    }

    fn test_base_with_header(
        header_entries: &[(&str, &str)],
        keywords: &[&str],
        ext: FileExt,
    ) -> MdictBase {
        let mut base = test_base_with_file(tempfile::tempfile().expect("tempfile"), ext);
        for (key, value) in header_entries {
            base.header.insert((*key).to_string(), (*value).to_string());
        }
        base.keyword_list = keywords
            .iter()
            .enumerate()
            .map(|(index, key_text)| KeyWordItem {
                record_start_offset: index as u64,
                record_end_offset: index as u64 + 1,
                key_text: (*key_text).to_string(),
                key_block_idx: 0,
            })
            .collect();
        base
    }

    fn test_base_with_file(file: std::fs::File, ext: FileExt) -> MdictBase {
        MdictBase::from_test_file(file, ext)
    }

    fn uncompressed_record_block(payload: &[u8]) -> Vec<u8> {
        let mut block = vec![0, 0, 0, 0, 0, 0, 0, 0];
        block.extend_from_slice(payload);
        block
    }

    fn test_keyword_texts(base: &MdictBase) -> Vec<&str> {
        base.keyword_list
            .iter()
            .map(|item| item.key_text.as_str())
            .collect()
    }
}

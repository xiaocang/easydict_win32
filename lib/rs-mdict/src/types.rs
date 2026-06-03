//! Core data types for mdict parsing

use std::collections::HashMap;

/// File extension type
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FileExt {
    Mdx,
    Mdd,
}

impl FileExt {
    pub fn as_str(&self) -> &'static str {
        match self {
            FileExt::Mdx => "mdx",
            FileExt::Mdd => "mdd",
        }
    }
}

/// Encoding type
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Encoding {
    #[default]
    Utf8,
    Utf16Le,
    Gb18030,
    Big5,
}

/// Encryption type
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EncryptType {
    None = 0,
    RecordBlock = 1,
    KeyInfoBlock = 2,
}

impl From<u8> for EncryptType {
    fn from(value: u8) -> Self {
        match value {
            0 => EncryptType::None,
            1 => EncryptType::RecordBlock,
            2 => EncryptType::KeyInfoBlock,
            _ => EncryptType::None,
        }
    }
}

/// Compression type
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CompressionType {
    None,
    Lzo,
    Zlib,
}

impl CompressionType {
    pub fn from_bytes(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < 4 {
            return None;
        }
        match [bytes[0], bytes[1], bytes[2], bytes[3]] {
            [0x00, 0x00, 0x00, 0x00] => Some(CompressionType::None),
            [0x01, 0x00, 0x00, 0x00] => Some(CompressionType::Lzo),
            [0x02, 0x00, 0x00, 0x00] => Some(CompressionType::Zlib),
            _ => None,
        }
    }
}

/// Number format based on version
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NumFmt {
    Uint32,
    Uint64,
}

impl NumFmt {
    pub fn width(&self) -> usize {
        match self {
            NumFmt::Uint32 => 4,
            NumFmt::Uint64 => 8,
        }
    }
}

/// Dictionary metadata
#[derive(Debug, Clone)]
pub struct DictMeta {
    pub version: f64,
    pub encoding: Encoding,
    pub encrypt: EncryptType,
    pub num_fmt: NumFmt,
    pub num_width: usize,
    pub ext: FileExt,
    pub passcode: Option<String>,
}

impl Default for DictMeta {
    fn default() -> Self {
        DictMeta {
            version: 1.2,
            encoding: Encoding::Utf8,
            encrypt: EncryptType::None,
            num_fmt: NumFmt::Uint32,
            num_width: 4,
            ext: FileExt::Mdx,
            passcode: None,
        }
    }
}

/// Dictionary header attributes
pub type DictHeader = HashMap<String, String>;

/// Key header information
#[derive(Debug, Clone, Default)]
pub struct KeyHeader {
    /// Number of keyword blocks
    pub keyword_blocks_num: u64,
    /// Total number of keywords
    pub keyword_num: u64,
    /// Decompressed size of key info
    pub key_info_unpack_size: u64,
    /// Compressed size of key info
    pub key_info_packed_size: u64,
    /// Total size of all key blocks
    pub keyword_block_packed_size: u64,
}

/// Key block info item
#[derive(Debug, Clone)]
pub struct KeyInfoItem {
    /// First keyword in this block
    pub first_key: String,
    /// Last keyword in this block
    pub last_key: String,
    /// Compressed size of this key block
    pub key_block_pack_size: u64,
    /// Accumulated compressed offset
    pub key_block_pack_accumulator: u64,
    /// Decompressed size of this key block
    pub key_block_unpack_size: u64,
    /// Accumulated decompressed offset
    pub key_block_unpack_accumulator: u64,
    /// Number of entries in this block
    pub key_block_entries_num: u64,
    /// Accumulated entry count
    pub key_block_entries_num_accumulator: u64,
    /// Index of this key block info
    pub key_block_info_index: usize,
}

/// Keyword item
#[derive(Debug, Clone)]
pub struct KeyWordItem {
    /// Start offset in record block
    pub record_start_offset: u64,
    /// End offset in record block
    pub record_end_offset: u64,
    /// Keyword text
    pub key_text: String,
    /// Index of the key block this keyword belongs to
    pub key_block_idx: usize,
}

/// Record header information
#[derive(Debug, Clone, Default)]
pub struct RecordHeader {
    /// Number of record blocks
    pub record_blocks_num: u64,
    /// Total number of entries
    pub entries_num: u64,
    /// Compressed size of record info
    pub record_info_comp_size: u64,
    /// Total compressed size of record blocks
    pub record_block_comp_size: u64,
}

/// Record block info
#[derive(Debug, Clone)]
pub struct RecordInfo {
    /// Compressed size
    pub pack_size: u64,
    /// Accumulated compressed offset
    pub pack_accumulate_offset: u64,
    /// Decompressed size
    pub unpack_size: u64,
    /// Accumulated decompressed offset
    pub unpack_accumulate_offset: u64,
}

/// Lookup result
#[derive(Debug, Clone)]
pub struct LookupResult {
    /// The keyword
    pub key_text: String,
    /// The definition or resource data
    pub definition: String,
}

/// Fuzzy search result
#[derive(Debug, Clone)]
pub struct FuzzyWord {
    /// The keyword item
    pub item: KeyWordItem,
    /// Edit distance
    pub edit_distance: usize,
}

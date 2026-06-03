//! Error types for the mdict library

use thiserror::Error;

/// Custom error type for mdict operations
#[derive(Error, Debug)]
pub enum MdictError {
    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),

    #[error("Invalid file format: {0}")]
    InvalidFormat(String),

    #[error("Unsupported version: {0}")]
    UnsupportedVersion(f64),

    #[error("Decompression error: {0}")]
    DecompressionError(String),

    #[error("Decryption error: {0}")]
    DecryptionError(String),

    #[error("Encoding error: {0}")]
    EncodingError(String),

    #[error("Header parse error: {0}")]
    HeaderParseError(String),

    #[error("Key not found: {0}")]
    KeyNotFound(String),

    #[error("Invalid compression type: {0:08x}")]
    InvalidCompressionType(u32),

    #[error("Encrypted file requires passcode")]
    EncryptedFileRequiresPasscode,
}

/// Result type alias for mdict operations
pub type Result<T> = std::result::Result<T, MdictError>;

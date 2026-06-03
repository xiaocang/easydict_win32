//! Utility functions for mdict parsing

use crate::error::{MdictError, Result};
use crate::types::Encoding;
use encoding_rs::{BIG5, GB18030, UTF_16LE};
use regex::Regex;
use std::collections::HashMap;

/// Read big-endian u8 from bytes
pub fn read_u8(bytes: &[u8]) -> u8 {
    bytes[0]
}

/// Read big-endian u16 from bytes
pub fn read_u16_be(bytes: &[u8]) -> u16 {
    ((bytes[0] as u16) << 8) | (bytes[1] as u16)
}

/// Read big-endian u32 from bytes
pub fn read_u32_be(bytes: &[u8]) -> u32 {
    ((bytes[0] as u32) << 24)
        | ((bytes[1] as u32) << 16)
        | ((bytes[2] as u32) << 8)
        | (bytes[3] as u32)
}

/// Read big-endian u64 from bytes
pub fn read_u64_be(bytes: &[u8]) -> u64 {
    ((bytes[0] as u64) << 56)
        | ((bytes[1] as u64) << 48)
        | ((bytes[2] as u64) << 40)
        | ((bytes[3] as u64) << 32)
        | ((bytes[4] as u64) << 24)
        | ((bytes[5] as u64) << 16)
        | ((bytes[6] as u64) << 8)
        | (bytes[7] as u64)
}

/// Read number from bytes based on length
pub fn bytes_to_number(data: &[u8]) -> u64 {
    match data.len() {
        1 => read_u8(data) as u64,
        2 => read_u16_be(data) as u64,
        4 => read_u32_be(data) as u64,
        8 => read_u64_be(data),
        _ => 0,
    }
}

/// Decode bytes to string based on encoding
pub fn decode_string(bytes: &[u8], encoding: Encoding) -> Result<String> {
    match encoding {
        Encoding::Utf8 => {
            String::from_utf8(bytes.to_vec()).map_err(|e| MdictError::EncodingError(e.to_string()))
        }
        Encoding::Utf16Le => {
            let (result, _, had_errors) = UTF_16LE.decode(bytes);
            if had_errors {
                Err(MdictError::EncodingError(
                    "UTF-16LE decode error".to_string(),
                ))
            } else {
                Ok(result.into_owned())
            }
        }
        Encoding::Gb18030 => {
            let (result, _, had_errors) = GB18030.decode(bytes);
            if had_errors {
                Err(MdictError::EncodingError(
                    "GB18030 decode error".to_string(),
                ))
            } else {
                Ok(result.into_owned())
            }
        }
        Encoding::Big5 => {
            let (result, _, had_errors) = BIG5.decode(bytes);
            if had_errors {
                Err(MdictError::EncodingError("BIG5 decode error".to_string()))
            } else {
                Ok(result.into_owned())
            }
        }
    }
}

/// Decode UTF-16LE bytes to string
pub fn decode_utf16le(bytes: &[u8]) -> Result<String> {
    let (result, _, had_errors) = UTF_16LE.decode(bytes);
    if had_errors {
        Err(MdictError::EncodingError(
            "UTF-16LE decode error".to_string(),
        ))
    } else {
        Ok(result.into_owned())
    }
}

/// Unescape HTML entities
pub fn unescape_entities(text: &str) -> String {
    text.replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&amp;", "&")
        .replace("&quot;", "\"")
        .replace("&apos;", "'")
}

/// Parse header XML text to attributes
pub fn parse_header(header_text: &str) -> Result<HashMap<String, String>> {
    let mut header_attr: HashMap<String, String> = HashMap::new();

    // Match all attributes in format: key="value"
    let re = Regex::new(r#"(\w+)="((?:[^"\\]|\\.)*)""#)
        .map_err(|e| MdictError::HeaderParseError(e.to_string()))?;

    for cap in re.captures_iter(header_text) {
        let key = cap
            .get(1)
            .map(|m| m.as_str().to_string())
            .unwrap_or_default();
        let value = cap
            .get(2)
            .map(|m| unescape_entities(m.as_str()))
            .unwrap_or_default();
        header_attr.insert(key, value);
    }

    Ok(header_attr)
}

/// Calculate Levenshtein distance between two strings
pub fn levenshtein_distance(a: &str, b: &str) -> usize {
    let a_chars: Vec<char> = a.chars().collect();
    let b_chars: Vec<char> = b.chars().collect();
    let m = a_chars.len();
    let n = b_chars.len();

    if m == 0 {
        return n;
    }
    if n == 0 {
        return m;
    }

    let mut dp = vec![vec![0usize; n + 1]; m + 1];

    // Initialize boundaries
    for (i, row) in dp.iter_mut().enumerate().take(m + 1) {
        row[0] = i;
    }

    for (j, val) in dp[0].iter_mut().enumerate().take(n + 1) {
        *val = j;
    }

    // Dynamic programming
    for i in 1..=m {
        for j in 1..=n {
            if a_chars[i - 1] != b_chars[j - 1] {
                dp[i][j] = 1 + dp[i - 1][j].min(dp[i][j - 1]).min(dp[i - 1][j - 1]);
            } else {
                dp[i][j] = dp[i - 1][j - 1];
            }
        }
    }

    dp[m][n]
}

/// Strip punctuation and normalize key for comparison
pub fn strip_key(key: &str, is_mdd: bool) -> String {
    let mut result = key.to_lowercase();

    if is_mdd {
        // For MDD: remove extension and special characters
        if let Some(pos) = result.rfind('.') {
            result = result[..pos].to_string();
        }
        result = result.replace(['(', ')', '.', ',', ' ', '\'', '/', '@'], "");
        result = result.replace('_', "!");
    } else {
        // For MDX: remove punctuation
        result = result.replace(
            [
                '(', ')', '.', ',', '-', '&', ' ', '\'', '/', '\\', '@', '_', '$', '!',
            ],
            "",
        );
    }

    result.trim().to_string()
}

/// Fast XOR decryption
pub fn fast_decrypt(data: &mut [u8], key: &[u8]) {
    let mut previous: u8 = 0x36;
    for (i, byte) in data.iter_mut().enumerate() {
        let t = (*byte).rotate_left(4) ^ previous ^ (i as u8) ^ key[i % key.len()];
        previous = *byte;
        *byte = t;
    }
}

/// MDX decryption using RIPEMD-128
pub fn mdx_decrypt(comp_block: &[u8]) -> Vec<u8> {
    use crate::ripemd128::ripemd128;

    if comp_block.len() < 8 {
        return comp_block.to_vec();
    }

    // Extract key from bytes 4-8
    let mut key_buffer = [0u8; 8];
    key_buffer[..4].copy_from_slice(&comp_block[4..8]);

    // XOR with fixed values
    key_buffer[4] ^= 0x95;
    key_buffer[5] ^= 0x36;
    key_buffer[6] ^= 0x00;
    key_buffer[7] ^= 0x00;

    // Calculate RIPEMD-128 hash as the actual key
    let key = ripemd128(&key_buffer);

    // Decrypt the data
    let mut result = Vec::with_capacity(comp_block.len());
    result.extend_from_slice(&comp_block[..8]);

    let mut decrypted = comp_block[8..].to_vec();
    fast_decrypt(&mut decrypted, &key);
    result.extend_from_slice(&decrypted);

    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_read_u32_be() {
        let bytes = [0x00, 0x00, 0x04, 0xa6];
        assert_eq!(bytes_to_number(&bytes), 1190);
    }

    #[test]
    fn test_read_u64_be() {
        // Test case from js-mdict
        let bytes = [0x00, 0x00, 0x04, 0xa6, 0x01, 0x02, 0x03, 0x04];
        assert_eq!(bytes_to_number(&bytes), 5111027991300);
    }

    #[test]
    fn test_read_u64_be_2() {
        let bytes = [0x00, 0x00, 0x04, 0xa6, 0x00, 0x00, 0x01, 0x64];
        assert_eq!(bytes_to_number(&bytes), 5111011082596);
    }

    #[test]
    fn test_read_u64_be_max_safe() {
        // Max safe integer in JS: 2^53 - 1 = 9007199254740991
        let bytes = [0x00, 0x1f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff];
        assert_eq!(bytes_to_number(&bytes), 9007199254740991);
    }

    #[test]
    fn test_read_u64_be_various() {
        // Test various byte positions
        let bytes1 = [0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00];
        assert_eq!(bytes_to_number(&bytes1), 0x1000000000);

        let bytes2 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01];
        assert_eq!(bytes_to_number(&bytes2), 0x01);

        let bytes3 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00];
        assert_eq!(bytes_to_number(&bytes3), 0x0100);

        let bytes4 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00];
        assert_eq!(bytes_to_number(&bytes4), 0x010000);

        let bytes5 = [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
        assert_eq!(bytes_to_number(&bytes5), 0x01000000);

        let bytes6 = [0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00];
        assert_eq!(bytes_to_number(&bytes6), 0x0100000000);

        let bytes7 = [0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];
        assert_eq!(bytes_to_number(&bytes7), 0x010000000000);

        let bytes8 = [0x00, 0x00, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00];
        assert_eq!(bytes_to_number(&bytes8), 0x110000000000);

        let bytes9 = [0x00, 0x01, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00];
        assert_eq!(bytes_to_number(&bytes9), 0x01110000000000);

        let bytes10 = [0x00, 0x11, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00];
        assert_eq!(bytes_to_number(&bytes10), 0x11110000000000);

        let bytes11 = [0x00, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11];
        assert_eq!(bytes_to_number(&bytes11), 0x11111111111111);
    }

    #[test]
    fn test_read_u16_be() {
        let bytes1 = [0x00, 0x20];
        assert_eq!(bytes_to_number(&bytes1), 0x20);

        let bytes2 = [0x20, 0x20];
        assert_eq!(bytes_to_number(&bytes2), 0x2020);
    }

    #[test]
    fn test_read_u8() {
        let bytes1 = [0x1a];
        assert_eq!(bytes_to_number(&bytes1), 0x1a);

        let bytes2 = [0x20];
        assert_eq!(bytes_to_number(&bytes2), 0x20);
    }

    #[test]
    fn test_levenshtein_distance() {
        assert_eq!(levenshtein_distance("hello", "hello"), 0);
        assert_eq!(levenshtein_distance("hello", "helo"), 1);
        assert_eq!(levenshtein_distance("hello", "world"), 4);
        assert_eq!(levenshtein_distance("", "abc"), 3);
        assert_eq!(levenshtein_distance("abc", ""), 3);
    }
}

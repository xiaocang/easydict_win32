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
        // Find resource in the list
        let keyword_item = self.base.lookup_keyword_by_word(resource_key, false)?;
        let keyword_item = keyword_item.clone();

        // Get the resource data
        let data_bytes = self.base.lookup_record_by_keyword(&keyword_item).ok()?;

        // Encode as base64
        let definition = BASE64.encode(&data_bytes);

        Some(LookupResult {
            key_text: keyword_item.key_text.clone(),
            definition,
        })
    }

    /// Locate a resource and return raw bytes
    pub fn locate_raw(&mut self, resource_key: &str) -> Option<Vec<u8>> {
        // Find resource in the list
        let keyword_item = self.base.lookup_keyword_by_word(resource_key, false)?;
        let keyword_item = keyword_item.clone();

        // Get the resource data
        self.base.lookup_record_by_keyword(&keyword_item).ok()
    }

    /// Find resources with the given prefix
    pub fn prefix(&mut self, prefix: &str) -> Vec<LookupResult> {
        // Clone keywords to avoid borrowing issues
        let keywords: Vec<KeyWordItem> = self
            .base
            .get_prefix_keywords(prefix)
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
        self.base
            .get_prefix_keywords(prefix)
            .into_iter()
            .map(|k| k.key_text.clone())
            .collect()
    }

    /// Check if a resource exists
    pub fn contains(&self, resource_key: &str) -> bool {
        self.base
            .lookup_keyword_by_word(resource_key, false)
            .is_some()
    }

    /// Get the file path
    pub fn filepath(&self) -> &str {
        &self.base.filepath
    }

    /// Get resource info without loading data
    pub fn get_resource_info(&self, resource_key: &str) -> Option<ResourceInfo> {
        let keyword_item = self.base.lookup_keyword_by_word(resource_key, false)?;

        // Extract file extension from key
        let extension = resource_key
            .rsplit('.')
            .next()
            .map(|s| s.to_lowercase())
            .unwrap_or_default();

        // Determine MIME type
        let mime_type = match extension.as_str() {
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
        .to_string();

        Some(ResourceInfo {
            key: keyword_item.key_text.clone(),
            extension,
            mime_type,
        })
    }
}

/// Resource information
#[derive(Debug, Clone)]
pub struct ResourceInfo {
    /// Resource key
    pub key: String,
    /// File extension
    pub extension: String,
    /// MIME type
    pub mime_type: String,
}

#[cfg(test)]
mod tests {
    #[test]
    fn test_mdd_creation() {
        // This test requires an actual MDD file
        // let mdd = Mdd::new("test.mdd");
        // assert!(mdd.is_ok());
    }
}

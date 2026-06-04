//! MDX dictionary implementation
//!
//! This module provides the MDX dictionary parsing and querying functionality.

use std::path::Path;

use crate::error::Result;
use crate::mdict_base::MdictBase;
use crate::types::*;
use crate::utils::{decode_string, levenshtein_distance};

/// MDX dictionary parser
pub struct Mdx {
    /// Base parser
    base: MdictBase,
}

impl Mdx {
    /// Create a new MDX parser from file path
    pub fn new<P: AsRef<Path>>(filepath: P) -> Result<Self> {
        let base = MdictBase::new(filepath, FileExt::Mdx)?;
        Ok(Mdx { base })
    }

    /// Create a new MDX parser with a caller-provided key header transform.
    ///
    /// Credential-encrypted MDX files store the key header encrypted with a
    /// passcode-derived stream key. Callers that already own the credential
    /// algorithm can supply the decrypting transform here while reusing the
    /// rest of the MDX parser unchanged.
    pub fn new_with_key_header_transform<P, F>(filepath: P, key_header_transform: F) -> Result<Self>
    where
        P: AsRef<Path>,
        F: FnOnce(&[u8], &DictHeader) -> Result<Vec<u8>> + 'static,
    {
        let base =
            MdictBase::new_with_key_header_transform(filepath, FileExt::Mdx, key_header_transform)?;
        Ok(Mdx { base })
    }

    /// Get dictionary header
    pub fn header(&self) -> &DictHeader {
        &self.base.header
    }

    /// Get dictionary metadata
    pub fn meta(&self) -> &DictMeta {
        &self.base.meta
    }

    /// Get total number of keywords
    pub fn keyword_count(&self) -> usize {
        self.base.keyword_list.len()
    }

    /// Get all keywords
    pub fn keywords(&self) -> Vec<&str> {
        self.base
            .keyword_list
            .iter()
            .map(|k| k.key_text.as_str())
            .collect()
    }

    /// Lookup a word and return its definition
    pub fn lookup(&mut self, word: &str) -> Option<LookupResult> {
        // Find keyword in the list
        let keyword_item = self.base.lookup_keyword_by_word(word, false)?;
        let keyword_item = keyword_item.clone();

        // Get the definition
        let def_bytes = self.base.lookup_record_by_keyword(&keyword_item).ok()?;

        // Decode the definition
        let definition = decode_string(&def_bytes, self.base.meta.encoding)
            .unwrap_or_else(|_| String::from_utf8_lossy(&def_bytes).to_string());

        Some(LookupResult {
            key_text: keyword_item.key_text.clone(),
            definition,
        })
    }

    /// Find words with the given prefix
    pub fn prefix(&mut self, prefix: &str) -> Vec<LookupResult> {
        // Clone keywords to avoid borrowing issues
        let keywords: Vec<KeyWordItem> = self
            .base
            .get_prefix_keywords(prefix)
            .into_iter()
            .cloned()
            .collect();
        let mut results = Vec::new();
        let encoding = self.base.meta.encoding;

        for keyword in keywords {
            if let Ok(def_bytes) = self.base.lookup_record_by_keyword(&keyword) {
                let definition = decode_string(&def_bytes, encoding)
                    .unwrap_or_else(|_| String::from_utf8_lossy(&def_bytes).to_string());

                results.push(LookupResult {
                    key_text: keyword.key_text,
                    definition,
                });
            }
        }

        results
    }

    /// Find words with the given prefix (keys only, no definitions)
    pub fn prefix_keys(&self, prefix: &str) -> Vec<String> {
        self.base
            .get_prefix_keywords(prefix)
            .into_iter()
            .map(|k| k.key_text.clone())
            .collect()
    }

    /// Suggest similar words based on edit distance
    pub fn suggest(&self, word: &str, max_distance: usize) -> Vec<String> {
        if max_distance > 5 {
            return Vec::new();
        }

        let stripped_word = self.base.strip(word);

        // Get associated keywords
        let keywords = self.base.get_associated_keywords(word);

        let mut suggestions: Vec<(String, usize)> = keywords
            .into_iter()
            .filter_map(|item| {
                let stripped_key = self.base.strip(&item.key_text);
                let distance = levenshtein_distance(&stripped_key, &stripped_word);
                if distance <= max_distance {
                    Some((item.key_text.clone(), distance))
                } else {
                    None
                }
            })
            .collect();

        // Sort by edit distance
        suggestions.sort_by_key(|(_, d)| *d);

        suggestions.into_iter().map(|(s, _)| s).collect()
    }

    /// Fuzzy search with edit distance
    pub fn fuzzy_search(
        &self,
        word: &str,
        max_results: usize,
        max_distance: usize,
    ) -> Vec<FuzzyWord> {
        let stripped_word = self.base.strip(word);

        // Get associated keywords
        let keywords = self.base.get_associated_keywords(word);

        let mut fuzzy_words: Vec<FuzzyWord> = keywords
            .into_iter()
            .filter_map(|item| {
                let stripped_key = self.base.strip(&item.key_text);
                let distance = levenshtein_distance(&stripped_key, &stripped_word);
                if distance <= max_distance {
                    Some(FuzzyWord {
                        item: item.clone(),
                        edit_distance: distance,
                    })
                } else {
                    None
                }
            })
            .collect();

        // Sort by edit distance
        fuzzy_words.sort_by_key(|fw| fw.edit_distance);

        // Limit results
        fuzzy_words.truncate(max_results);

        fuzzy_words
    }

    /// Get definition for a fuzzy word
    pub fn get_definition(&mut self, item: &KeyWordItem) -> Option<String> {
        let def_bytes = self.base.lookup_record_by_keyword(item).ok()?;

        let definition = decode_string(&def_bytes, self.base.meta.encoding)
            .unwrap_or_else(|_| String::from_utf8_lossy(&def_bytes).to_string());

        Some(definition)
    }

    /// Check if a word exists in the dictionary
    pub fn contains(&self, word: &str) -> bool {
        self.base.lookup_keyword_by_word(word, false).is_some()
    }

    /// Get the file path
    pub fn filepath(&self) -> &str {
        &self.base.filepath
    }

    /// Get associated keywords (words in the same key block)
    /// This is useful for finding related words near the searched word
    pub fn associate(&self, word: &str) -> Vec<&KeyWordItem> {
        self.base.get_associated_keywords(word)
    }

    /// Lookup keyword by word (returns KeyWordItem with block info)
    pub fn lookup_keyword(&self, word: &str) -> Option<&KeyWordItem> {
        self.base.lookup_keyword_by_word(word, false)
    }

    /// Fetch definition for a keyword item
    pub fn fetch(&mut self, item: &KeyWordItem) -> Option<LookupResult> {
        let def_bytes = self.base.lookup_record_by_keyword(item).ok()?;

        let definition = decode_string(&def_bytes, self.base.meta.encoding)
            .unwrap_or_else(|_| String::from_utf8_lossy(&def_bytes).to_string());

        Some(LookupResult {
            key_text: item.key_text.clone(),
            definition,
        })
    }

    /// Get all keyword items
    pub fn keyword_list(&self) -> &[KeyWordItem] {
        &self.base.keyword_list
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn test_mdx_creation() {
        // This test requires an actual MDX file
        // let mdx = Mdx::new("test.mdx");
        // assert!(mdx.is_ok());
    }
}

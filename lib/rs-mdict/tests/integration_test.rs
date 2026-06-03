//! Integration tests for rust-mdict using real dictionary files

use rust_mdict::{Mdd, Mdx};

const MDX_PATH: &str = "/Users/fuyanxu/Documents/dict/牛津高阶英汉双解词典（第9版）.mdx";
const MDD_PATH: &str = "/Users/fuyanxu/Documents/dict/牛津高阶英汉双解词典（第9版）.mdd";

#[test]
fn test_mdx_load() {
    let mdx = Mdx::new(MDX_PATH);
    assert!(mdx.is_ok(), "Failed to load MDX file: {:?}", mdx.err());

    let mdx = mdx.unwrap();
    println!("MDX loaded successfully!");
    println!("  Version: {}", mdx.meta().version);
    println!("  Encoding: {:?}", mdx.meta().encoding);
    println!("  Keywords: {}", mdx.keyword_count());
}

#[test]
fn test_mdx_info() {
    let mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    println!("=== MDX Dictionary Info ===");
    println!("File: {}", mdx.filepath());
    println!("Version: {}", mdx.meta().version);
    println!("Encoding: {:?}", mdx.meta().encoding);
    println!("Encryption: {:?}", mdx.meta().encrypt);
    println!("Total keywords: {}", mdx.keyword_count());

    println!("\nHeader attributes:");
    for (key, value) in mdx.header() {
        // Truncate long values safely (handle multi-byte chars)
        let display_value = if value.chars().count() > 100 {
            let truncated: String = value.chars().take(100).collect();
            format!("{}...", truncated)
        } else {
            value.clone()
        };
        println!("  {}: {}", key, display_value);
    }
}

#[test]
fn test_mdx_lookup_hello() {
    let mut mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    let result = mdx.lookup("hello");
    assert!(result.is_some(), "Word 'hello' not found");

    let result = result.unwrap();
    println!("=== Lookup 'hello' ===");
    println!("Key: {}", result.key_text);
    println!("Definition length: {} chars", result.definition.len());
    println!(
        "Definition preview: {}...",
        &result.definition[..result.definition.len().min(500)]
    );
}

#[test]
fn test_mdx_lookup_world() {
    let mut mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    let result = mdx.lookup("world");
    assert!(result.is_some(), "Word 'world' not found");

    let result = result.unwrap();
    println!("=== Lookup 'world' ===");
    println!("Key: {}", result.key_text);
    println!("Definition length: {} chars", result.definition.len());
}

#[test]
fn test_mdx_lookup_apple() {
    let mut mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    let result = mdx.lookup("apple");
    assert!(result.is_some(), "Word 'apple' not found");

    let result = result.unwrap();
    println!("=== Lookup 'apple' ===");
    println!("Key: {}", result.key_text);
    println!("Definition length: {} chars", result.definition.len());
}

#[test]
fn test_mdx_prefix_search() {
    let mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    let keys = mdx.prefix_keys("hel");
    println!("=== Prefix search 'hel' ===");
    println!("Found {} words", keys.len());

    for key in keys.iter().take(20) {
        println!("  {}", key);
    }

    assert!(!keys.is_empty(), "No words found with prefix 'hel'");
    assert!(keys.iter().any(|k| k.to_lowercase().starts_with("hel")));
}

#[test]
fn test_mdx_prefix_search_app() {
    let mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    let keys = mdx.prefix_keys("app");
    println!("=== Prefix search 'app' ===");
    println!("Found {} words", keys.len());

    for key in keys.iter().take(15) {
        println!("  {}", key);
    }

    assert!(!keys.is_empty(), "No words found with prefix 'app'");
}

#[test]
fn test_mdx_suggest() {
    let mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    // Test with a misspelled word
    let suggestions = mdx.suggest("helo", 2);
    println!("=== Suggest for 'helo' (max distance 2) ===");
    println!("Found {} suggestions", suggestions.len());

    for suggestion in suggestions.iter().take(10) {
        println!("  {}", suggestion);
    }
}

#[test]
fn test_mdx_contains() {
    let mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    assert!(mdx.contains("hello"), "'hello' should exist");
    assert!(mdx.contains("world"), "'world' should exist");
    assert!(mdx.contains("apple"), "'apple' should exist");
    assert!(
        !mdx.contains("xyznonexistent123"),
        "Random word should not exist"
    );

    println!("=== Contains test passed ===");
}

#[test]
fn test_mdx_keywords_sample() {
    let mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    let keywords = mdx.keywords();
    println!("=== Sample keywords ===");
    println!("Total: {}", keywords.len());

    // Show first 20 keywords
    println!("\nFirst 20 keywords:");
    for key in keywords.iter().take(20) {
        println!("  {}", key);
    }

    // Show last 10 keywords
    println!("\nLast 10 keywords:");
    for key in keywords.iter().rev().take(10) {
        println!("  {}", key);
    }
}

#[test]
fn test_mdd_load() {
    let mdd = Mdd::new(MDD_PATH);
    assert!(mdd.is_ok(), "Failed to load MDD file: {:?}", mdd.err());

    let mdd = mdd.unwrap();
    println!("MDD loaded successfully!");
    println!("  Version: {}", mdd.meta().version);
    println!("  Encoding: {:?}", mdd.meta().encoding);
    println!("  Resources: {}", mdd.resource_count());
}

#[test]
fn test_mdd_info() {
    let mdd = Mdd::new(MDD_PATH).expect("Failed to load MDD");

    println!("=== MDD Resource File Info ===");
    println!("File: {}", mdd.filepath());
    println!("Version: {}", mdd.meta().version);
    println!("Encoding: {:?}", mdd.meta().encoding);
    println!("Encryption: {:?}", mdd.meta().encrypt);
    println!("Total resources: {}", mdd.resource_count());

    println!("\nHeader attributes:");
    for (key, value) in mdd.header() {
        // Truncate long values safely (handle multi-byte chars)
        let display_value = if value.chars().count() > 100 {
            let truncated: String = value.chars().take(100).collect();
            format!("{}...", truncated)
        } else {
            value.clone()
        };
        println!("  {}: {}", key, display_value);
    }
}

#[test]
fn test_mdd_resource_keys_sample() {
    let mdd = Mdd::new(MDD_PATH).expect("Failed to load MDD");

    let keys = mdd.resource_keys();
    println!("=== Sample resource keys ===");
    println!("Total: {}", keys.len());

    // Show first 30 resource keys
    println!("\nFirst 30 resource keys:");
    for key in keys.iter().take(30) {
        println!("  {}", key);
    }
}

#[test]
fn test_mdd_prefix_search() {
    let mdd = Mdd::new(MDD_PATH).expect("Failed to load MDD");

    // Search for resources starting with backslash
    let keys = mdd.prefix_keys("\\");
    println!("=== MDD prefix search '\\' ===");
    println!("Found {} resources", keys.len());

    for key in keys.iter().take(20) {
        println!("  {}", key);
    }
}

#[test]
fn test_mdd_locate_resource() {
    let mut mdd = Mdd::new(MDD_PATH).expect("Failed to load MDD");

    // Get first available resource key - clone to avoid borrow issues
    let keys: Vec<String> = mdd.resource_keys().iter().map(|s| s.to_string()).collect();
    if keys.is_empty() {
        println!("No resources found in MDD");
        return;
    }

    let first_key = &keys[0];
    println!("=== Locating resource: {} ===", first_key);

    let result = mdd.locate(first_key);
    if let Some(res) = result {
        println!("Key: {}", res.key_text);
        println!("Data length (base64): {} chars", res.definition.len());
        println!("Estimated raw size: {} bytes", res.definition.len() * 3 / 4);

        // Show first 100 chars of base64
        if res.definition.len() > 100 {
            println!("Data preview: {}...", &res.definition[..100]);
        }
    } else {
        println!("Resource not found");
    }
}

#[test]
fn test_mdd_locate_raw() {
    let mut mdd = Mdd::new(MDD_PATH).expect("Failed to load MDD");

    // Get first available resource key - clone to avoid borrow issues
    let keys: Vec<String> = mdd.resource_keys().iter().map(|s| s.to_string()).collect();
    if keys.is_empty() {
        println!("No resources found in MDD");
        return;
    }

    let first_key = &keys[0];
    println!("=== Locating raw resource: {} ===", first_key);

    let result = mdd.locate_raw(first_key);
    if let Some(data) = result {
        println!("Raw data size: {} bytes", data.len());

        // Show first 32 bytes as hex
        let hex_preview: String = data
            .iter()
            .take(32)
            .map(|b| format!("{:02x}", b))
            .collect::<Vec<_>>()
            .join(" ");
        println!("Hex preview: {}", hex_preview);
    } else {
        println!("Resource not found");
    }
}

#[test]
fn test_mdd_resource_info() {
    let mdd = Mdd::new(MDD_PATH).expect("Failed to load MDD");

    let keys = mdd.resource_keys();

    println!("=== Resource info samples ===");
    for key in keys.iter().take(10) {
        if let Some(info) = mdd.get_resource_info(key) {
            println!(
                "  {} -> ext: {}, mime: {}",
                info.key, info.extension, info.mime_type
            );
        }
    }
}

#[test]
fn test_mdd_contains() {
    let mdd = Mdd::new(MDD_PATH).expect("Failed to load MDD");

    let keys = mdd.resource_keys();
    if !keys.is_empty() {
        let first_key = keys[0];
        assert!(mdd.contains(first_key), "First resource key should exist");
    }

    assert!(
        !mdd.contains("\\nonexistent_resource_xyz.abc"),
        "Random resource should not exist"
    );

    println!("=== MDD contains test passed ===");
}

// ============ Additional tests for associate, fetch, lookup_keyword ============

#[test]
fn test_mdx_associate() {
    let mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    // Get associated keywords for "apple"
    let associated = mdx.associate("apple");

    println!("=== Associate 'apple' ===");
    println!(
        "Found {} associated words in the same key block",
        associated.len()
    );

    // Show first 20 associated words
    for item in associated.iter().take(20) {
        println!(
            "  {} (block: {}, offset: {})",
            item.key_text, item.key_block_idx, item.record_start_offset
        );
    }

    assert!(!associated.is_empty(), "Should find associated words");

    // All associated words should be in the same key block
    if !associated.is_empty() {
        let block_idx = associated[0].key_block_idx;
        assert!(
            associated
                .iter()
                .all(|item| item.key_block_idx == block_idx),
            "All associated words should be in the same key block"
        );
    }
}

#[test]
fn test_mdx_lookup_keyword() {
    let mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    // Lookup keyword item for "hello"
    let keyword = mdx.lookup_keyword("hello");

    println!("=== Lookup keyword 'hello' ===");

    assert!(keyword.is_some(), "Keyword 'hello' should exist");

    let keyword = keyword.unwrap();
    println!("Key text: {}", keyword.key_text);
    println!("Key block index: {}", keyword.key_block_idx);
    println!("Record start offset: {}", keyword.record_start_offset);
    println!("Record end offset: {}", keyword.record_end_offset);

    assert_eq!(keyword.key_text.to_lowercase(), "hello");
}

#[test]
fn test_mdx_fetch() {
    let mut mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    // First lookup the keyword
    let keyword = mdx.lookup_keyword("world").cloned();
    assert!(keyword.is_some(), "Keyword 'world' should exist");

    let keyword = keyword.unwrap();

    // Then fetch the definition using the keyword item
    let result = mdx.fetch(&keyword);

    println!("=== Fetch definition for 'world' ===");

    assert!(result.is_some(), "Should be able to fetch definition");

    let result = result.unwrap();
    println!("Key text: {}", result.key_text);
    println!("Definition length: {} chars", result.definition.len());

    assert!(
        !result.definition.is_empty(),
        "Definition should not be empty"
    );
}

#[test]
fn test_mdx_keyword_list() {
    let mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    let keyword_list = mdx.keyword_list();

    println!("=== Keyword list ===");
    println!("Total keywords: {}", keyword_list.len());

    // Show first 10 keyword items with details
    println!("\nFirst 10 keyword items:");
    for item in keyword_list.iter().take(10) {
        println!(
            "  {} (block: {}, start: {}, end: {})",
            item.key_text, item.key_block_idx, item.record_start_offset, item.record_end_offset
        );
    }

    assert!(!keyword_list.is_empty(), "Keyword list should not be empty");
}

#[test]
fn test_mdx_fuzzy_search() {
    let mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    // Fuzzy search for "aple" (misspelled "apple")
    let fuzzy_results = mdx.fuzzy_search("aple", 5, 2);

    println!("=== Fuzzy search 'aple' (max 5 results, max distance 2) ===");
    println!("Found {} fuzzy matches", fuzzy_results.len());

    for fw in fuzzy_results.iter() {
        println!(
            "  {} (edit distance: {})",
            fw.item.key_text, fw.edit_distance
        );
    }

    // Should find "apple" with edit distance 1
    assert!(
        fuzzy_results
            .iter()
            .any(|fw| fw.item.key_text.to_lowercase() == "apple"),
        "Should find 'apple' as a fuzzy match for 'aple'"
    );
}

#[test]
fn test_mdx_get_definition() {
    let mut mdx = Mdx::new(MDX_PATH).expect("Failed to load MDX");

    // Get keyword list and pick one
    let keyword = mdx.keyword_list().first().cloned();
    assert!(keyword.is_some(), "Should have at least one keyword");

    let keyword = keyword.unwrap();

    // Get definition for this keyword
    let definition = mdx.get_definition(&keyword);

    println!("=== Get definition for first keyword ===");
    println!("Keyword: {}", keyword.key_text);

    assert!(definition.is_some(), "Should be able to get definition");

    let def = definition.unwrap();
    println!("Definition length: {} chars", def.len());
}

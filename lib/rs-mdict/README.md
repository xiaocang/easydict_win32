# rust-mdict

A Rust implementation of MDX/MDD dictionary parser, inspired by [js-mdict](https://github.com/terasum/js-mdict).

## Features

- **MDX Support**: Parse and query MDX dictionary files
  - Lookup words and get definitions
  - Prefix search
  - Fuzzy search with edit distance
  - Suggest similar words

- **MDD Support**: Parse and locate resources in MDD files
  - Locate resources by key
  - Get raw binary data or base64 encoded data
  - Resource info (MIME type, extension)

- **Compression Support**:
  - No compression
  - LZO compression
  - Zlib compression

- **Encoding Support**:
  - UTF-8
  - UTF-16LE
  - GB18030 (GBK/GB2312)
  - Big5

- **Encryption Support**:
  - Record block encryption
  - Key info block encryption

## Installation

Add to your `Cargo.toml`:

```toml
[dependencies]
rs-mdict = "0.1.0"
```

## Usage

### MDX Dictionary

```rust
use rust_mdict::Mdx;

fn main() {
    // Load dictionary
    let mut mdx = Mdx::new("dictionary.mdx").unwrap();
    
    // Lookup a word
    if let Some(result) = mdx.lookup("hello") {
        println!("Definition: {}", result.definition);
    }
    
    // Prefix search
    let words = mdx.prefix_keys("hel");
    for word in words {
        println!("{}", word);
    }
    
    // Suggest similar words
    let suggestions = mdx.suggest("helo", 2);
    for suggestion in suggestions {
        println!("{}", suggestion);
    }
}
```

### MDD Resource File

```rust
use rust_mdict::Mdd;

fn main() {
    // Load resource file
    let mut mdd = Mdd::new("dictionary.mdd").unwrap();
    
    // Locate a resource (returns base64)
    if let Some(result) = mdd.locate("\\Logo.jpg") {
        println!("Base64 data: {}", result.definition);
    }
    
    // Get raw bytes
    if let Some(data) = mdd.locate_raw("\\Logo.jpg") {
        // Use raw bytes directly
        std::fs::write("logo.jpg", data).unwrap();
    }
    
    // Get resource info
    if let Some(info) = mdd.get_resource_info("\\Logo.jpg") {
        println!("MIME type: {}", info.mime_type);
    }
}
```

### CLI Tool

```bash
# MDX operations
mdict-cli dictionary.mdx lookup hello
mdict-cli dictionary.mdx prefix hel
mdict-cli dictionary.mdx suggest helo
mdict-cli dictionary.mdx info

# MDD operations
mdict-cli dictionary.mdd locate "\\Logo.jpg"
mdict-cli dictionary.mdd prefix "\\"
mdict-cli dictionary.mdd info
```

## API Reference

### Mdx

| Method | Description |
|--------|-------------|
| `new(path)` | Create a new MDX parser |
| `lookup(word)` | Look up a word and get its definition |
| `prefix(prefix)` | Find words with prefix and their definitions |
| `prefix_keys(prefix)` | Find words with prefix (keys only) |
| `suggest(word, max_distance)` | Suggest similar words |
| `fuzzy_search(word, max_results, max_distance)` | Fuzzy search with edit distance |
| `contains(word)` | Check if a word exists |
| `keywords()` | Get all keywords |
| `keyword_count()` | Get total keyword count |
| `header()` | Get dictionary header attributes |
| `meta()` | Get dictionary metadata |

### Mdd

| Method | Description |
|--------|-------------|
| `new(path)` | Create a new MDD parser |
| `locate(key)` | Locate a resource (returns base64) |
| `locate_raw(key)` | Locate a resource (returns raw bytes) |
| `prefix(prefix)` | Find resources with prefix |
| `prefix_keys(prefix)` | Find resource keys with prefix |
| `contains(key)` | Check if a resource exists |
| `get_resource_info(key)` | Get resource info (MIME type, extension) |
| `resource_keys()` | Get all resource keys |
| `resource_count()` | Get total resource count |
| `header()` | Get file header attributes |
| `meta()` | Get file metadata |

## License

MIT License

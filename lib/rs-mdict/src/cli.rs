//! Command-line interface for rust-mdict
//!
//! Usage:
//!   mdict-cli <file> lookup <word>
//!   mdict-cli <file> prefix <prefix>
//!   mdict-cli <file> info

use std::env;
use std::process;

use rust_mdict::{Mdd, Mdx};

fn print_usage() {
    eprintln!("Usage:");
    eprintln!("  mdict-cli <file.mdx> lookup <word>    - Look up a word in MDX dictionary");
    eprintln!("  mdict-cli <file.mdx> prefix <prefix>  - Find words with prefix in MDX");
    eprintln!("  mdict-cli <file.mdx> suggest <word>   - Suggest similar words in MDX");
    eprintln!("  mdict-cli <file.mdx> info             - Show MDX dictionary info");
    eprintln!("  mdict-cli <file.mdd> locate <key>     - Locate resource in MDD file");
    eprintln!("  mdict-cli <file.mdd> info             - Show MDD file info");
}

fn main() {
    let args: Vec<String> = env::args().collect();

    if args.len() < 3 {
        print_usage();
        process::exit(1);
    }

    let filepath = &args[1];
    let command = &args[2];

    // Determine file type
    let is_mdd = filepath.to_lowercase().ends_with(".mdd");

    if is_mdd {
        handle_mdd(filepath, command, &args[3..]);
    } else {
        handle_mdx(filepath, command, &args[3..]);
    }
}

fn handle_mdx(filepath: &str, command: &str, args: &[String]) {
    let mut mdx = match Mdx::new(filepath) {
        Ok(m) => m,
        Err(e) => {
            eprintln!("Error opening MDX file: {}", e);
            process::exit(1);
        }
    };

    match command {
        "lookup" => {
            if args.is_empty() {
                eprintln!("Error: missing word argument");
                process::exit(1);
            }
            let word = &args[0];
            match mdx.lookup(word) {
                Some(result) => {
                    println!("Word: {}", result.key_text);
                    println!("Definition:");
                    println!("{}", result.definition);
                }
                None => {
                    println!("Word '{}' not found", word);
                }
            }
        }
        "prefix" => {
            if args.is_empty() {
                eprintln!("Error: missing prefix argument");
                process::exit(1);
            }
            let prefix = &args[0];
            let keys = mdx.prefix_keys(prefix);
            println!("Found {} words with prefix '{}':", keys.len(), prefix);
            for key in keys.iter().take(20) {
                println!("  {}", key);
            }
            if keys.len() > 20 {
                println!("  ... and {} more", keys.len() - 20);
            }
        }
        "suggest" => {
            if args.is_empty() {
                eprintln!("Error: missing word argument");
                process::exit(1);
            }
            let word = &args[0];
            let suggestions = mdx.suggest(word, 3);
            println!("Suggestions for '{}':", word);
            for suggestion in suggestions.iter().take(10) {
                println!("  {}", suggestion);
            }
        }
        "info" => {
            println!("MDX Dictionary Info:");
            println!("  File: {}", mdx.filepath());
            println!("  Version: {}", mdx.meta().version);
            println!("  Encoding: {:?}", mdx.meta().encoding);
            println!("  Encryption: {:?}", mdx.meta().encrypt);
            println!("  Keywords: {}", mdx.keyword_count());
            println!();
            println!("Header attributes:");
            for (key, value) in mdx.header() {
                println!("  {}: {}", key, value);
            }
        }
        _ => {
            eprintln!("Unknown command: {}", command);
            print_usage();
            process::exit(1);
        }
    }
}

fn handle_mdd(filepath: &str, command: &str, args: &[String]) {
    let mut mdd = match Mdd::new(filepath) {
        Ok(m) => m,
        Err(e) => {
            eprintln!("Error opening MDD file: {}", e);
            process::exit(1);
        }
    };

    match command {
        "locate" => {
            if args.is_empty() {
                eprintln!("Error: missing resource key argument");
                process::exit(1);
            }
            let key = &args[0];
            match mdd.locate(key) {
                Some(result) => {
                    println!("Resource: {}", result.key_text);
                    // Show truncated base64 for large resources
                    if result.definition.len() > 100 {
                        println!("Data (base64, truncated): {}...", &result.definition[..100]);
                        println!("Total size: {} bytes", result.definition.len() * 3 / 4);
                    } else {
                        println!("Data (base64): {}", result.definition);
                    }
                }
                None => {
                    println!("Resource '{}' not found", key);
                }
            }
        }
        "prefix" => {
            if args.is_empty() {
                eprintln!("Error: missing prefix argument");
                process::exit(1);
            }
            let prefix = &args[0];
            let keys = mdd.prefix_keys(prefix);
            println!("Found {} resources with prefix '{}':", keys.len(), prefix);
            for key in keys.iter().take(20) {
                println!("  {}", key);
            }
            if keys.len() > 20 {
                println!("  ... and {} more", keys.len() - 20);
            }
        }
        "info" => {
            println!("MDD Resource File Info:");
            println!("  File: {}", mdd.filepath());
            println!("  Version: {}", mdd.meta().version);
            println!("  Encoding: {:?}", mdd.meta().encoding);
            println!("  Encryption: {:?}", mdd.meta().encrypt);
            println!("  Resources: {}", mdd.resource_count());
            println!();
            println!("Header attributes:");
            for (key, value) in mdd.header() {
                println!("  {}: {}", key, value);
            }
        }
        _ => {
            eprintln!("Unknown command: {}", command);
            print_usage();
            process::exit(1);
        }
    }
}

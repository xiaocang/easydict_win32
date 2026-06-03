//! Rust implementation of MDX/MDD dictionary parser
//!
//! This library provides functionality to parse and query MDict dictionary files.
//!
//! # Example
//! ```no_run
//! use rust_mdict::{Mdx, Mdd};
//!
//! // Load and query MDX dictionary
//! let mut mdx = Mdx::new("dictionary.mdx").unwrap();
//! if let Some(result) = mdx.lookup("hello") {
//!     println!("Definition: {}", result.definition);
//! }
//!
//! // Load and query MDD resource file
//! let mut mdd = Mdd::new("dictionary.mdd").unwrap();
//! if let Some(result) = mdd.locate("\\Logo.jpg") {
//!     println!("Resource data (base64): {}", result.definition);
//! }
//! ```

mod error;
mod lzo;
mod mdd;
mod mdict_base;
mod mdx;
mod ripemd128;
mod types;
mod utils;

pub use error::{MdictError, Result};
pub use mdd::Mdd;
pub use mdx::Mdx;
pub use types::*;

//! XML front-end for Direct XAML: lexing, concrete syntax tree, spans and diagnostics.
//!
//! Nothing in this crate knows what a control or a property is — it produces an untyped tree with
//! accurate source positions, which `dxaml-ast` then interprets as XAML.

pub mod cst;
pub mod diagnostic;
pub mod lexer;
pub mod span;

pub use cst::{Attribute, Element, ElementId, QName, SyntaxTree};
pub use diagnostic::{codes, Diagnostic, DiagnosticBag, Severity};
pub use lexer::parse;
pub use span::{LineIndex, Span};

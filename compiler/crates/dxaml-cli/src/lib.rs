//! Compiler driver: source text in, IR plus rendered diagnostics out.
//!
//! Kept separate from `main.rs` so tests can drive a compile without spawning a process.

use dxaml_ir::IrDocument;
use dxaml_syntax::{codes, Diagnostic, LineIndex, Span};

pub const COMPILER_VERSION: &str = env!("CARGO_PKG_VERSION");

pub struct CompileResult {
    /// `None` when compilation failed; a partial document is never returned.
    pub document: Option<IrDocument>,
    /// Diagnostics already rendered in MSBuild's format, in source order.
    pub diagnostics: Vec<String>,
    pub failed: bool,
}

/// Compiles one document. `display_path` appears in diagnostics and in the IR header.
pub fn compile_source(source: &str, display_path: &str) -> CompileResult {
    compile_source_with_paths(source, display_path, display_path)
}

/// Compiles one document while keeping diagnostic and reproducible IR paths separate.
pub fn compile_source_with_paths(
    source: &str,
    diagnostic_path: &str,
    source_path: &str,
) -> CompileResult {
    let index = LineIndex::new(source);
    let (hir, bag) = dxaml_hir::analyze(source);

    let mut diagnostics: Vec<String> = bag
        .sorted()
        .iter()
        .map(|diagnostic| diagnostic.render(diagnostic_path, &index))
        .collect();

    let hir = match hir {
        Some(hir) if !bag.has_errors() => hir,
        _ => {
            // A document that produced no diagnostic but also no HIR would be a silent failure,
            // which the contract forbids.
            if !bag.has_errors() {
                diagnostics.push(
                    Diagnostic::error(
                        codes::IR_VALIDATION,
                        "compilation produced no document and no diagnostic; this is a compiler bug",
                        Span::empty(0),
                    )
                    .render(diagnostic_path, &index),
                );
            }
            return CompileResult {
                document: None,
                diagnostics,
                failed: true,
            };
        }
    };

    let document = dxaml_lower::lower(&hir, source, source_path, COMPILER_VERSION);

    let problems = dxaml_ir::validate(&document);
    if !problems.is_empty() {
        for problem in problems {
            diagnostics.push(
                Diagnostic::error(codes::IR_VALIDATION, problem, Span::empty(0))
                    .render(diagnostic_path, &index),
            );
        }
        return CompileResult {
            document: None,
            diagnostics,
            failed: true,
        };
    }

    // Reaching here implies the bag held no errors, so anything left is advisory.
    CompileResult {
        document: Some(document),
        diagnostics,
        failed: false,
    }
}

/// The output file name for a given input stem: `Foo.xaml` becomes `Foo.dxir.json`.
pub fn output_file_name(stem: &str) -> String {
    format!("{stem}.dxir.json")
}

/// The generated binding source name for a given input stem.
pub fn bindings_output_file_name(stem: &str) -> String {
    format!("{stem}.bindings.g.cs")
}

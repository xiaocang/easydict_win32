use crate::span::{LineIndex, Span};

/// Diagnostic codes. Ranges are defined in `spec/direct-xaml-v0.md`:
/// `DX1xxx` syntax, `DX2xxx` resolution, `DX3xxx` outside the subset, `DX4xxx` lowering.
pub mod codes {
    pub const XML_PARSE: &str = "DX1001";
    pub const NO_ROOT: &str = "DX1002";
    pub const MULTIPLE_ROOTS: &str = "DX1003";

    pub const ROOT_MUST_BE_USERCONTROL: &str = "DX2001";
    pub const MISSING_X_CLASS: &str = "DX2002";
    pub const UNKNOWN_NAMESPACE: &str = "DX2003";
    pub const PROPERTY_NOT_VALID_HERE: &str = "DX2004";
    pub const BAD_VALUE: &str = "DX2005";
    pub const DUPLICATE_NAME: &str = "DX2006";
    pub const INVALID_IDENTIFIER: &str = "DX2007";
    pub const TEXT_AND_TEXT_ATTRIBUTE: &str = "DX2008";
    pub const WRONG_CHILD_COUNT: &str = "DX2009";
    pub const ELEMENT_NOT_VALID_HERE: &str = "DX2010";

    pub const UNSUPPORTED_CONTROL: &str = "DX3001";
    pub const UNSUPPORTED_ATTACHED_PROPERTY: &str = "DX3002";
    pub const UNSUPPORTED_PROPERTY_ELEMENT: &str = "DX3003";
    pub const UNSUPPORTED_MARKUP_EXTENSION: &str = "DX3004";
    pub const UNSUPPORTED_DIRECTIVE: &str = "DX3005";

    pub const IR_VALIDATION: &str = "DX4001";
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Severity {
    Error,
    Warning,
}

impl Severity {
    fn label(self) -> &'static str {
        match self {
            Severity::Error => "error",
            Severity::Warning => "warning",
        }
    }
}

#[derive(Debug, Clone)]
pub struct Diagnostic {
    pub code: &'static str,
    pub severity: Severity,
    pub message: String,
    pub span: Span,
}

impl Diagnostic {
    pub fn error(code: &'static str, message: impl Into<String>, span: Span) -> Self {
        Self {
            code,
            severity: Severity::Error,
            message: message.into(),
            span,
        }
    }

    pub fn warning(code: &'static str, message: impl Into<String>, span: Span) -> Self {
        Self {
            code,
            severity: Severity::Warning,
            message: message.into(),
            span,
        }
    }

    /// Renders in MSBuild's canonical error format so a future `<Exec>` integration surfaces
    /// the diagnostic in the IDE with no extra parsing.
    pub fn render(&self, path: &str, index: &LineIndex) -> String {
        let (line, column) = index.location(self.span.start);
        format!(
            "{}({},{}): {} {}: {}",
            path,
            line,
            column,
            self.severity.label(),
            self.code,
            self.message
        )
    }
}

/// Collects diagnostics produced across compilation phases.
#[derive(Debug, Default, Clone)]
pub struct DiagnosticBag {
    diagnostics: Vec<Diagnostic>,
}

impl DiagnosticBag {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn push(&mut self, diagnostic: Diagnostic) {
        self.diagnostics.push(diagnostic);
    }

    pub fn error(&mut self, code: &'static str, message: impl Into<String>, span: Span) {
        self.push(Diagnostic::error(code, message, span));
    }

    pub fn extend(&mut self, other: impl IntoIterator<Item = Diagnostic>) {
        self.diagnostics.extend(other);
    }

    pub fn has_errors(&self) -> bool {
        self.diagnostics
            .iter()
            .any(|d| d.severity == Severity::Error)
    }

    pub fn is_empty(&self) -> bool {
        self.diagnostics.is_empty()
    }

    pub fn len(&self) -> usize {
        self.diagnostics.len()
    }

    pub fn iter(&self) -> impl Iterator<Item = &Diagnostic> {
        self.diagnostics.iter()
    }

    /// Sorts by source position so output is stable regardless of the order phases ran in.
    pub fn sorted(&self) -> Vec<Diagnostic> {
        let mut sorted = self.diagnostics.clone();
        sorted.sort_by_key(|d| (d.span.start, d.code));
        sorted
    }

    pub fn into_vec(self) -> Vec<Diagnostic> {
        self.diagnostics
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn renders_msbuild_format() {
        let source = "line one\nline two\n";
        let index = LineIndex::new(source);
        let diagnostic = Diagnostic::error(
            codes::UNSUPPORTED_CONTROL,
            "control 'X' unsupported",
            Span::new(9, 13),
        );
        assert_eq!(
            diagnostic.render("Foo.xaml", &index),
            "Foo.xaml(2,1): error DX3001: control 'X' unsupported"
        );
    }

    #[test]
    fn sorting_is_positional() {
        let mut bag = DiagnosticBag::new();
        bag.error(codes::BAD_VALUE, "second", Span::new(40, 41));
        bag.error(codes::BAD_VALUE, "first", Span::new(10, 11));
        let sorted = bag.sorted();
        assert_eq!(sorted[0].message, "first");
        assert_eq!(sorted[1].message, "second");
    }
}

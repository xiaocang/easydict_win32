use crate::span::Span;

pub type ElementId = usize;

/// A possibly-prefixed XML name, kept exactly as written.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QName {
    pub prefix: Option<String>,
    pub local: String,
}

impl QName {
    pub fn parse(raw: &str) -> Self {
        match raw.split_once(':') {
            Some((prefix, local)) => Self {
                prefix: Some(prefix.to_string()),
                local: local.to_string(),
            },
            None => Self {
                prefix: None,
                local: raw.to_string(),
            },
        }
    }

    pub fn prefix_str(&self) -> &str {
        self.prefix.as_deref().unwrap_or("")
    }

    /// The name as written, including any prefix. Used in diagnostics.
    pub fn as_written(&self) -> String {
        match &self.prefix {
            Some(prefix) => format!("{prefix}:{}", self.local),
            None => self.local.clone(),
        }
    }
}

#[derive(Debug, Clone)]
pub struct Attribute {
    pub name: QName,
    /// Entity-decoded value.
    pub value: String,
    pub span: Span,
    pub name_span: Span,
    pub value_span: Span,
}

#[derive(Debug, Clone)]
pub struct Element {
    pub name: QName,
    /// Covers the open tag through the end tag once the element is closed.
    pub span: Span,
    pub name_span: Span,
    pub attributes: Vec<Attribute>,
    pub children: Vec<ElementId>,
    /// Concatenated significant text; whitespace-only runs are dropped.
    pub text: String,
    pub text_span: Option<Span>,
}

impl Element {
    pub fn attribute(&self, local: &str) -> Option<&Attribute> {
        self.attributes.iter().find(|a| a.name.local == local)
    }
}

/// Flat arena of elements. `root` is the single document element, if the document had one.
#[derive(Debug, Clone, Default)]
pub struct SyntaxTree {
    pub elements: Vec<Element>,
    pub root: Option<ElementId>,
}

impl SyntaxTree {
    pub fn get(&self, id: ElementId) -> &Element {
        &self.elements[id]
    }

    pub fn len(&self) -> usize {
        self.elements.len()
    }

    pub fn is_empty(&self) -> bool {
        self.elements.is_empty()
    }
}

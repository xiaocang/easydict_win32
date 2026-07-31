//! The only module in the workspace that depends on `quick-xml`.
//!
//! quick-xml changes API across minor versions, so the surface used here is deliberately narrow:
//! `Reader::from_str`, `read_event`, `buffer_position`, and the `Start`/`End`/`Empty`/`Text`/`Eof`
//! events. Every `match` on `Event` carries a catch-all arm so that variants added by future
//! versions (`GeneralRef` in 0.38, for example) do not break the build.
//!
//! Spans are computed here rather than taken from quick-xml, which does not expose per-attribute
//! positions. Because events are contiguous and cover the whole document, the byte range between
//! two consecutive `buffer_position` readings is exactly the current event's source text.

use quick_xml::events::{BytesStart, Event};
use quick_xml::Reader;

use crate::cst::{Attribute, Element, ElementId, QName, SyntaxTree};
use crate::diagnostic::{codes, DiagnosticBag};
use crate::span::Span;

/// `buffer_position` returns `u64` in recent quick-xml and `usize` in older releases; the cast
/// keeps both compiling.
#[allow(clippy::unnecessary_cast)]
fn buffer_pos(reader: &Reader<&[u8]>) -> usize {
    reader.buffer_position() as usize
}

/// Name span plus one `(name, value, whole)` triple per attribute.
type TagSpans = (Span, Vec<(Span, Span, Span)>);

/// Locates the element name and every attribute inside a raw tag such as `<Border Padding="4">`.
fn scan_tag(tag: &str, base: usize) -> TagSpans {
    let bytes = tag.as_bytes();
    let mut i = 0usize;

    if i < bytes.len() && bytes[i] == b'<' {
        i += 1;
    }
    let name_start = i;
    while i < bytes.len() && !bytes[i].is_ascii_whitespace() && bytes[i] != b'>' && bytes[i] != b'/'
    {
        i += 1;
    }
    let name_span = Span::new(base + name_start, base + i);

    let mut attributes = Vec::new();
    while i < bytes.len() {
        while i < bytes.len() && bytes[i].is_ascii_whitespace() {
            i += 1;
        }
        if i >= bytes.len() || bytes[i] == b'>' || bytes[i] == b'/' {
            break;
        }

        let attr_start = i;
        while i < bytes.len()
            && !bytes[i].is_ascii_whitespace()
            && bytes[i] != b'='
            && bytes[i] != b'>'
            && bytes[i] != b'/'
        {
            i += 1;
        }
        if i == attr_start {
            // Not a name character and not a terminator: skip it rather than spin.
            i += 1;
            continue;
        }
        let attr_name_span = Span::new(base + attr_start, base + i);
        let attr_name_end = i;

        while i < bytes.len() && bytes[i].is_ascii_whitespace() {
            i += 1;
        }
        if i >= bytes.len() || bytes[i] != b'=' {
            attributes.push((
                attr_name_span,
                Span::empty(base + attr_name_end),
                attr_name_span,
            ));
            continue;
        }
        i += 1; // '='

        while i < bytes.len() && bytes[i].is_ascii_whitespace() {
            i += 1;
        }
        if i >= bytes.len() {
            break;
        }
        let quote = bytes[i];
        if quote != b'"' && quote != b'\'' {
            attributes.push((attr_name_span, Span::empty(base + i), attr_name_span));
            continue;
        }
        i += 1;

        let value_start = i;
        while i < bytes.len() && bytes[i] != quote {
            i += 1;
        }
        let value_span = Span::new(base + value_start, base + i);
        if i < bytes.len() {
            i += 1; // closing quote
        }
        attributes.push((
            attr_name_span,
            value_span,
            Span::new(base + attr_start, base + i),
        ));
    }

    (name_span, attributes)
}

fn build_element(
    source: &str,
    start: usize,
    end: usize,
    bytes: &BytesStart<'_>,
    diagnostics: &mut DiagnosticBag,
) -> Element {
    let tag_span = Span::new(start, end);
    let tag_source = source.get(start..end).unwrap_or("");
    let (name_span, attribute_spans) = scan_tag(tag_source, start);

    let raw_name = String::from_utf8_lossy(bytes.name().as_ref()).into_owned();

    let mut attributes = Vec::new();
    for (index, attribute) in bytes.attributes().enumerate() {
        let attribute = match attribute {
            Ok(attribute) => attribute,
            Err(error) => {
                diagnostics.error(
                    codes::XML_PARSE,
                    format!("malformed attribute: {error}"),
                    tag_span,
                );
                continue;
            }
        };

        let raw_key = String::from_utf8_lossy(attribute.key.as_ref()).into_owned();
        let value = match attribute.unescape_value() {
            Ok(value) => value.into_owned(),
            Err(error) => {
                diagnostics.error(
                    codes::XML_PARSE,
                    format!("cannot decode value of '{raw_key}': {error}"),
                    tag_span,
                );
                String::from_utf8_lossy(&attribute.value).into_owned()
            }
        };

        let (attr_name_span, value_span, whole_span) = attribute_spans
            .get(index)
            .copied()
            .unwrap_or((tag_span, tag_span, tag_span));

        attributes.push(Attribute {
            name: QName::parse(&raw_key),
            value,
            span: whole_span,
            name_span: attr_name_span,
            value_span,
        });
    }

    Element {
        name: QName::parse(&raw_name),
        span: tag_span,
        name_span,
        attributes,
        children: Vec::new(),
        text: String::new(),
        text_span: None,
    }
}

/// Parses XML into a concrete syntax tree, recovering where it can so that a single malformed
/// construct does not hide every later problem.
pub fn parse(source: &str) -> (SyntaxTree, DiagnosticBag) {
    let mut tree = SyntaxTree::default();
    let mut diagnostics = DiagnosticBag::new();
    let mut reader = Reader::from_str(source);
    let mut stack: Vec<ElementId> = Vec::new();
    let mut cursor = 0usize;

    loop {
        let start = cursor;
        let event = reader.read_event();
        let end = buffer_pos(&reader);
        cursor = end;

        let event = match event {
            Ok(event) => event,
            Err(error) => {
                diagnostics.error(
                    codes::XML_PARSE,
                    format!("XML parse error: {error}"),
                    Span::new(start, end),
                );
                break;
            }
        };

        match event {
            Event::Eof => break,

            Event::Start(bytes) => {
                let element = build_element(source, start, end, &bytes, &mut diagnostics);
                let id = attach(&mut tree, &mut diagnostics, &stack, element);
                stack.push(id);
            }

            Event::Empty(bytes) => {
                let element = build_element(source, start, end, &bytes, &mut diagnostics);
                attach(&mut tree, &mut diagnostics, &stack, element);
            }

            Event::End(_) => match stack.pop() {
                Some(id) => tree.elements[id].span.extend_to(end),
                None => diagnostics.error(
                    codes::XML_PARSE,
                    "closing tag without a matching opening tag",
                    Span::new(start, end),
                ),
            },

            Event::Text(bytes) => {
                let text = match bytes.unescape() {
                    Ok(text) => text.into_owned(),
                    Err(error) => {
                        diagnostics.error(
                            codes::XML_PARSE,
                            format!("cannot decode text content: {error}"),
                            Span::new(start, end),
                        );
                        continue;
                    }
                };
                let trimmed = text.trim();
                if trimmed.is_empty() {
                    continue;
                }
                if let Some(&parent) = stack.last() {
                    let element = &mut tree.elements[parent];
                    if !element.text.is_empty() {
                        element.text.push(' ');
                    }
                    element.text.push_str(trimmed);
                    match &mut element.text_span {
                        Some(span) => span.extend_to(end),
                        None => element.text_span = Some(Span::new(start, end)),
                    }
                }
            }

            // Declarations, comments, CDATA, processing instructions, doctypes, and any variant
            // introduced by a future quick-xml release carry no meaning in Direct XAML.
            _ => {}
        }
    }

    for unclosed in stack {
        let span = tree.elements[unclosed].span;
        let name = tree.elements[unclosed].name.as_written();
        diagnostics.error(
            codes::XML_PARSE,
            format!("element '{name}' is never closed"),
            span,
        );
    }

    if tree.root.is_none() && !diagnostics.has_errors() {
        diagnostics.error(
            codes::NO_ROOT,
            "document contains no root element",
            Span::empty(0),
        );
    }

    (tree, diagnostics)
}

fn attach(
    tree: &mut SyntaxTree,
    diagnostics: &mut DiagnosticBag,
    stack: &[ElementId],
    element: Element,
) -> ElementId {
    let span = element.span;
    let id = tree.elements.len();
    tree.elements.push(element);

    match stack.last() {
        Some(&parent) => tree.elements[parent].children.push(id),
        None => {
            if tree.root.is_none() {
                tree.root = Some(id);
            } else {
                diagnostics.error(
                    codes::MULTIPLE_ROOTS,
                    "document has more than one root element",
                    span,
                );
            }
        }
    }

    id
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_nested_elements_and_attributes() {
        let source = r#"<Root a="1"><Child b="two"/></Root>"#;
        let (tree, diagnostics) = parse(source);
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());

        let root = tree.get(tree.root.expect("root"));
        assert_eq!(root.name.local, "Root");
        assert_eq!(root.attributes.len(), 1);
        assert_eq!(root.attributes[0].value, "1");
        assert_eq!(root.children.len(), 1);

        let child = tree.get(root.children[0]);
        assert_eq!(child.name.local, "Child");
        assert_eq!(child.attributes[0].value, "two");
    }

    #[test]
    fn attribute_spans_point_at_the_value() {
        let source = r#"<Root Padding="12"/>"#;
        let (tree, _) = parse(source);
        let root = tree.get(tree.root.expect("root"));
        let padding = &root.attributes[0];
        assert_eq!(&source[padding.name_span.start..padding.name_span.end], "Padding");
        assert_eq!(&source[padding.value_span.start..padding.value_span.end], "12");
    }

    #[test]
    fn element_span_covers_the_end_tag() {
        let source = "<Root>\n  <Child/>\n</Root>";
        let (tree, _) = parse(source);
        let root = tree.get(tree.root.expect("root"));
        assert_eq!(root.span.start, 0);
        assert_eq!(root.span.end, source.len());
    }

    #[test]
    fn keeps_significant_text_and_drops_whitespace() {
        let source = "<Root>\n   \n  <T>  hello  </T>\n</Root>";
        let (tree, _) = parse(source);
        let root = tree.get(tree.root.expect("root"));
        assert_eq!(root.text, "");
        let text_node = tree.get(root.children[0]);
        assert_eq!(text_node.text, "hello");
        assert!(text_node.text_span.is_some());
    }

    #[test]
    fn decodes_entities() {
        let source = r#"<Root t="a &amp; b">x &lt; y</Root>"#;
        let (tree, _) = parse(source);
        let root = tree.get(tree.root.expect("root"));
        assert_eq!(root.attributes[0].value, "a & b");
        assert_eq!(root.text, "x < y");
    }

    #[test]
    fn reports_unclosed_elements() {
        let (_, diagnostics) = parse("<Root><Child></Root>");
        assert!(diagnostics.has_errors());
    }

    #[test]
    fn reports_empty_documents() {
        let (_, diagnostics) = parse("   ");
        assert!(diagnostics
            .iter()
            .any(|d| d.code == codes::NO_ROOT));
    }

    #[test]
    fn skips_declaration_and_comments() {
        let source = "<?xml version=\"1.0\"?>\n<!-- note -->\n<Root/>";
        let (tree, diagnostics) = parse(source);
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());
        assert_eq!(tree.get(tree.root.expect("root")).name.local, "Root");
    }
}

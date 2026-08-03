//! Turns the untyped CST into a XAML abstract syntax tree.
//!
//! This layer resolves XML namespaces and classifies each attribute as a directive, a property,
//! an attached property or a namespace declaration. It performs no schema validation — it does
//! not know whether `Border` exists or whether `Padding` is legal on it. That is `dxaml-hir`'s
//! job. What this layer guarantees is that every surviving node is in the presentation namespace
//! and that ignorable markup has been dropped.

pub mod markup;

use std::collections::{HashMap, HashSet};

use dxaml_schema as schema;
use dxaml_syntax::{codes, DiagnosticBag, ElementId, Span, SyntaxTree};

pub use markup::{AttributeValue, MarkupExtension};

#[derive(Debug, Clone)]
pub struct XamlDocument {
    pub root: Option<XamlElement>,
    /// Value of the root's `x:Class` directive, if present.
    pub class_name: Option<String>,
}

#[derive(Debug, Clone)]
pub struct XamlElement {
    /// Local type name; the namespace has already been checked.
    pub name: String,
    pub span: Span,
    pub name_span: Span,
    pub directives: Vec<XamlDirective>,
    pub properties: Vec<XamlProperty>,
    pub children: Vec<XamlChild>,
    pub text: String,
    pub text_span: Option<Span>,
    /// Namespace aliases in scope on this element. `x:DataType` uses these to turn
    /// `prefix:Type` into a C# type name without making the XML parser understand CLR types.
    pub namespace_aliases: HashMap<String, String>,
}

impl XamlElement {
    pub fn directive(&self, name: &str) -> Option<&XamlDirective> {
        self.directives.iter().find(|d| d.name == name)
    }

    /// Child elements, skipping property elements.
    pub fn element_children(&self) -> impl Iterator<Item = &XamlElement> {
        self.children.iter().filter_map(|child| match child {
            XamlChild::Element(element) => Some(element),
            XamlChild::PropertyElement(_) => None,
        })
    }
}

/// An `x:`-prefixed attribute, such as `x:Class` or `x:Name`.
#[derive(Debug, Clone)]
pub struct XamlDirective {
    pub name: String,
    pub value: String,
    pub span: Span,
    pub name_span: Span,
    pub value_span: Span,
}

#[derive(Debug, Clone)]
pub struct XamlProperty {
    /// `Some("Grid")` for an attached property such as `Grid.Row`.
    pub owner: Option<String>,
    pub name: String,
    pub value: AttributeValue,
    pub span: Span,
    pub name_span: Span,
    pub value_span: Span,
}

impl XamlProperty {
    /// The name as written, for diagnostics.
    pub fn as_written(&self) -> String {
        match &self.owner {
            Some(owner) => format!("{owner}.{}", self.name),
            None => self.name.clone(),
        }
    }
}

#[derive(Debug, Clone)]
pub enum XamlChild {
    Element(XamlElement),
    PropertyElement(XamlPropertyElement),
}

/// An `Owner.Property` child element, such as `<Grid.RowDefinitions>`.
#[derive(Debug, Clone)]
pub struct XamlPropertyElement {
    pub owner: String,
    pub name: String,
    pub span: Span,
    pub name_span: Span,
    pub children: Vec<XamlElement>,
}

#[derive(Debug, Clone, Default)]
struct Namespaces {
    default: Option<String>,
    by_prefix: HashMap<String, String>,
    ignorable: HashSet<String>,
}

impl Namespaces {
    fn uri_for(&self, prefix: &str) -> Option<&str> {
        if prefix.is_empty() {
            self.default.as_deref()
        } else {
            self.by_prefix.get(prefix).map(String::as_str)
        }
    }

    fn is_ignorable_uri(uri: &str) -> bool {
        uri == schema::NS_BLEND || uri == schema::NS_MARKUP_COMPAT
    }
}

pub fn build(tree: &SyntaxTree, diagnostics: &mut DiagnosticBag) -> XamlDocument {
    let root_id = match tree.root {
        Some(root_id) => root_id,
        None => {
            return XamlDocument {
                root: None,
                class_name: None,
            }
        }
    };

    let root = build_element(tree, root_id, &Namespaces::default(), diagnostics);
    let class_name = root
        .as_ref()
        .and_then(|element| element.directive("Class"))
        .map(|directive| directive.value.clone());

    XamlDocument { root, class_name }
}

/// Returns `None` when the element belongs to an ignorable namespace and should be dropped.
fn build_element(
    tree: &SyntaxTree,
    id: ElementId,
    inherited: &Namespaces,
    diagnostics: &mut DiagnosticBag,
) -> Option<XamlElement> {
    let source = tree.get(id);
    let namespaces = extend_namespaces(tree, id, inherited);

    let prefix = source.name.prefix_str();
    if namespaces.ignorable.contains(prefix) {
        return None;
    }
    match namespaces.uri_for(prefix) {
        Some(uri) if uri == schema::NS_PRESENTATION => {}
        Some(uri) if Namespaces::is_ignorable_uri(uri) => return None,
        Some(uri) => {
            diagnostics.error(
                codes::UNKNOWN_NAMESPACE,
                format!(
                    "element '{}' is in namespace '{uri}'; Direct XAML v0 only accepts the presentation namespace",
                    source.name.as_written()
                ),
                source.name_span,
            );
            return None;
        }
        None => {
            diagnostics.error(
                codes::UNKNOWN_NAMESPACE,
                format!("undeclared namespace prefix '{prefix}'"),
                source.name_span,
            );
            return None;
        }
    }

    let mut directives = Vec::new();
    let mut properties = Vec::new();

    for attribute in &source.attributes {
        let prefix = attribute.name.prefix_str();
        let local = attribute.name.local.as_str();

        // Namespace declarations were consumed by `extend_namespaces`.
        if prefix == "xmlns" || (prefix.is_empty() && local == "xmlns") {
            continue;
        }

        if !prefix.is_empty() {
            if namespaces.ignorable.contains(prefix) {
                continue;
            }
            match namespaces.uri_for(prefix) {
                Some(uri) if uri == schema::NS_DIRECTIVES => {
                    directives.push(XamlDirective {
                        name: local.to_string(),
                        value: attribute.value.clone(),
                        span: attribute.span,
                        name_span: attribute.name_span,
                        value_span: attribute.value_span,
                    });
                }
                Some(uri) if Namespaces::is_ignorable_uri(uri) => {}
                Some(uri) => diagnostics.error(
                    codes::UNKNOWN_NAMESPACE,
                    format!(
                        "attribute '{}' is in namespace '{uri}', which Direct XAML v0 does not understand",
                        attribute.name.as_written()
                    ),
                    attribute.name_span,
                ),
                None => diagnostics.error(
                    codes::UNKNOWN_NAMESPACE,
                    format!("undeclared namespace prefix '{prefix}'"),
                    attribute.name_span,
                ),
            }
            continue;
        }

        let (owner, name) = match local.split_once('.') {
            Some((owner, name)) => (Some(owner.to_string()), name.to_string()),
            None => (None, local.to_string()),
        };

        properties.push(XamlProperty {
            owner,
            name,
            value: AttributeValue::classify(&attribute.value),
            span: attribute.span,
            name_span: attribute.name_span,
            value_span: attribute.value_span,
        });
    }

    let mut children = Vec::new();
    for &child_id in &source.children {
        let child_source = tree.get(child_id);
        if child_source.name.local.contains('.') {
            if let Some(property_element) =
                build_property_element(tree, child_id, &namespaces, diagnostics)
            {
                children.push(XamlChild::PropertyElement(property_element));
            }
        } else if let Some(element) = build_element(tree, child_id, &namespaces, diagnostics) {
            children.push(XamlChild::Element(element));
        }
    }

    Some(XamlElement {
        name: source.name.local.clone(),
        span: source.span,
        name_span: source.name_span,
        directives,
        properties,
        children,
        text: source.text.clone(),
        text_span: source.text_span,
        namespace_aliases: namespaces.by_prefix.clone(),
    })
}

fn build_property_element(
    tree: &SyntaxTree,
    id: ElementId,
    inherited: &Namespaces,
    diagnostics: &mut DiagnosticBag,
) -> Option<XamlPropertyElement> {
    let source = tree.get(id);
    let namespaces = extend_namespaces(tree, id, inherited);

    let prefix = source.name.prefix_str();
    if namespaces.ignorable.contains(prefix) {
        return None;
    }
    if let Some(uri) = namespaces.uri_for(prefix) {
        if Namespaces::is_ignorable_uri(uri) {
            return None;
        }
    }

    let (owner, name) = source.name.local.split_once('.')?;

    let mut children = Vec::new();
    for &child_id in &source.children {
        if let Some(element) = build_element(tree, child_id, &namespaces, diagnostics) {
            children.push(element);
        }
    }

    Some(XamlPropertyElement {
        owner: owner.to_string(),
        name: name.to_string(),
        span: source.span,
        name_span: source.name_span,
        children,
    })
}

fn extend_namespaces(tree: &SyntaxTree, id: ElementId, inherited: &Namespaces) -> Namespaces {
    let source = tree.get(id);
    let mut namespaces = inherited.clone();

    for attribute in &source.attributes {
        let prefix = attribute.name.prefix_str();
        let local = attribute.name.local.as_str();

        if prefix == "xmlns" {
            namespaces
                .by_prefix
                .insert(local.to_string(), attribute.value.clone());
        } else if prefix.is_empty() && local == "xmlns" {
            namespaces.default = Some(attribute.value.clone());
        }
    }

    // `mc:Ignorable` can only be read once its own prefix is bound, hence the second pass.
    for attribute in &source.attributes {
        if attribute.name.local != "Ignorable" {
            continue;
        }
        let prefix = attribute.name.prefix_str();
        if namespaces.uri_for(prefix) == Some(schema::NS_MARKUP_COMPAT) {
            for ignorable in attribute.value.split_whitespace() {
                namespaces.ignorable.insert(ignorable.to_string());
            }
        }
    }

    namespaces
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(source: &str) -> (XamlDocument, DiagnosticBag) {
        let (tree, mut diagnostics) = dxaml_syntax::parse(source);
        let document = build(&tree, &mut diagnostics);
        (document, diagnostics)
    }

    const HEADER: &str = concat!(
        r#"xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" "#,
        r#"xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" "#,
        r#"xmlns:d="http://schemas.microsoft.com/expression/blend/2008" "#,
        r#"xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" "#,
        r#"mc:Ignorable="d""#
    );

    #[test]
    fn separates_directives_from_properties() {
        let source = format!(
            r#"<UserControl {HEADER} x:Class="A.B"><Border x:Name="Root" Padding="12"/></UserControl>"#
        );
        let (document, diagnostics) = parse(&source);
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());

        let root = document.root.expect("root");
        assert_eq!(root.name, "UserControl");
        assert_eq!(document.class_name.as_deref(), Some("A.B"));

        let border = root.element_children().next().expect("border");
        assert_eq!(
            border.directive("Name").map(|d| d.value.as_str()),
            Some("Root")
        );
        assert_eq!(border.properties.len(), 1);
        assert_eq!(border.properties[0].name, "Padding");
    }

    #[test]
    fn splits_attached_properties() {
        let source = format!(r#"<UserControl {HEADER}><Border Grid.Row="1"/></UserControl>"#);
        let (document, diagnostics) = parse(&source);
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());

        let border = document
            .root
            .expect("root")
            .element_children()
            .next()
            .cloned()
            .expect("border");
        let property = &border.properties[0];
        assert_eq!(property.owner.as_deref(), Some("Grid"));
        assert_eq!(property.name, "Row");
        assert_eq!(property.as_written(), "Grid.Row");
    }

    #[test]
    fn recognises_property_elements() {
        let source = format!(
            r#"<UserControl {HEADER}><Grid><Grid.RowDefinitions><RowDefinition Height="Auto"/></Grid.RowDefinitions></Grid></UserControl>"#
        );
        let (document, diagnostics) = parse(&source);
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());

        let grid = document
            .root
            .expect("root")
            .element_children()
            .next()
            .cloned()
            .expect("grid");
        match &grid.children[0] {
            XamlChild::PropertyElement(property_element) => {
                assert_eq!(property_element.owner, "Grid");
                assert_eq!(property_element.name, "RowDefinitions");
                assert_eq!(property_element.children.len(), 1);
                assert_eq!(property_element.children[0].name, "RowDefinition");
            }
            other => panic!("expected a property element, got {other:?}"),
        }
    }

    #[test]
    fn drops_ignorable_markup() {
        let source = format!(
            r#"<UserControl {HEADER} d:DesignHeight="400"><d:Placeholder/><Border/></UserControl>"#
        );
        let (document, diagnostics) = parse(&source);
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());

        let root = document.root.expect("root");
        assert!(root.properties.is_empty(), "d: attributes must be dropped");
        assert_eq!(root.children.len(), 1, "d: elements must be dropped");
    }

    #[test]
    fn rejects_undeclared_prefixes() {
        let source = format!(r#"<UserControl {HEADER}><local:Widget/></UserControl>"#);
        let (_, diagnostics) = parse(&source);
        assert!(diagnostics
            .iter()
            .any(|d| d.code == codes::UNKNOWN_NAMESPACE));
    }

    #[test]
    fn keeps_text_content() {
        let source = format!(r#"<UserControl {HEADER}><TextBlock>hello</TextBlock></UserControl>"#);
        let (document, _) = parse(&source);
        let text = document
            .root
            .expect("root")
            .element_children()
            .next()
            .cloned()
            .expect("text");
        assert_eq!(text.text, "hello");
    }
}

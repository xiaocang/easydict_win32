//! Lowers the XAML AST into a typed, schema-checked HIR.
//!
//! This is where the v0 subset is enforced. Every construct is either recognised by
//! `dxaml-schema` or reported — nothing is dropped silently.

use std::collections::HashSet;

use dxaml_ast::{AttributeValue, XamlChild, XamlDocument, XamlElement, XamlProperty, XamlPropertyElement};
use dxaml_schema::{self as schema, ContentKind, ControlKind, Invalidation, ValueType};
use dxaml_syntax::{codes, DiagnosticBag, Span};

use crate::value::{
    parse_bool, parse_color, parse_corner_radius, parse_double, parse_grid_length, parse_int,
    parse_length, parse_thickness, HirValue, LiteralValue, ResourceKind, ResourceRef,
};

pub type NodeId = usize;

#[derive(Debug, Clone)]
pub struct HirDocument {
    pub class_name: String,
    pub root: NodeId,
    pub nodes: Vec<HirNode>,
}

#[derive(Debug, Clone)]
pub struct HirNode {
    pub kind: ControlKind,
    pub span: Span,
    pub parent: Option<NodeId>,
    pub children: Vec<NodeId>,
    /// The `x:Name`, if the element declared one.
    pub name: Option<String>,
    pub properties: Vec<HirProperty>,
    pub events: Vec<HirEvent>,
    pub text: Option<String>,
}

#[derive(Debug, Clone)]
pub struct HirProperty {
    /// `Padding`, or `Grid.Row` for an attached property.
    pub name: String,
    pub value: HirValue,
    pub invalidation: Invalidation,
    pub span: Span,
}

#[derive(Debug, Clone)]
pub struct HirEvent {
    pub ir_event: &'static str,
    pub handler: String,
    pub span: Span,
}

pub fn build(document: &XamlDocument, diagnostics: &mut DiagnosticBag) -> Option<HirDocument> {
    let root_element = document.root.as_ref()?;

    if root_element.name != ControlKind::UserControl.name() {
        diagnostics.error(
            codes::ROOT_MUST_BE_USERCONTROL,
            format!(
                "the root element must be UserControl, found '{}'",
                root_element.name
            ),
            root_element.name_span,
        );
        return None;
    }

    let class_name = match document.class_name.as_deref() {
        Some(name) if !name.trim().is_empty() => name.trim().to_string(),
        _ => {
            diagnostics.error(
                codes::MISSING_X_CLASS,
                "the root element needs an x:Class directive; the generated accessor type is derived from it",
                root_element.name_span,
            );
            return None;
        }
    };

    let mut builder = Builder {
        nodes: Vec::new(),
        diagnostics,
        names: HashSet::new(),
    };

    let root = builder.element(root_element, None, None)?;
    let nodes = builder.nodes;

    Some(HirDocument {
        class_name,
        root,
        nodes,
    })
}

struct Builder<'a> {
    nodes: Vec<HirNode>,
    diagnostics: &'a mut DiagnosticBag,
    names: HashSet<String>,
}

impl Builder<'_> {
    fn push_node(&mut self, kind: ControlKind, span: Span, parent: Option<NodeId>) -> NodeId {
        let id = self.nodes.len();
        self.nodes.push(HirNode {
            kind,
            span,
            parent,
            children: Vec::new(),
            name: None,
            properties: Vec::new(),
            events: Vec::new(),
            text: None,
        });
        id
    }

    fn element(
        &mut self,
        element: &XamlElement,
        parent: Option<NodeId>,
        parent_kind: Option<ControlKind>,
    ) -> Option<NodeId> {
        let kind = match ControlKind::from_name(&element.name) {
            Some(kind) => kind,
            None => {
                self.diagnostics.error(
                    codes::UNSUPPORTED_CONTROL,
                    format!(
                        "control '{}' is not in the Direct XAML v0 subset",
                        element.name
                    ),
                    element.name_span,
                );
                return None;
            }
        };

        if !kind.is_visual() {
            self.diagnostics.error(
                codes::ELEMENT_NOT_VALID_HERE,
                format!(
                    "'{}' may only appear inside a Grid.{}s property element",
                    kind.name(),
                    kind.name()
                ),
                element.name_span,
            );
            return None;
        }

        if kind == ControlKind::UserControl && parent.is_some() {
            self.diagnostics.error(
                codes::ELEMENT_NOT_VALID_HERE,
                "UserControl may only be the root element",
                element.name_span,
            );
            return None;
        }

        let id = self.push_node(kind, element.span, parent);
        self.directives(id, element, parent.is_none());

        for property in &element.properties {
            self.property(id, kind, parent_kind, property);
        }

        self.text(id, kind, element);

        let mut attempted_visual = 0usize;
        for child in &element.children {
            match child {
                XamlChild::Element(child_element) => {
                    attempted_visual += 1;
                    if let Some(child_id) = self.element(child_element, Some(id), Some(kind)) {
                        self.nodes[id].children.push(child_id);
                    }
                }
                XamlChild::PropertyElement(property_element) => {
                    self.property_element(id, kind, property_element);
                }
            }
        }

        self.check_content(kind, element, attempted_visual);
        Some(id)
    }

    /// Builds a `RowDefinition` or `ColumnDefinition` inside a property element.
    fn definition(
        &mut self,
        element: &XamlElement,
        expected: ControlKind,
        parent: NodeId,
    ) -> Option<NodeId> {
        let kind = match ControlKind::from_name(&element.name) {
            Some(kind) => kind,
            None => {
                self.diagnostics.error(
                    codes::UNSUPPORTED_CONTROL,
                    format!(
                        "control '{}' is not in the Direct XAML v0 subset",
                        element.name
                    ),
                    element.name_span,
                );
                return None;
            }
        };

        if kind != expected {
            self.diagnostics.error(
                codes::ELEMENT_NOT_VALID_HERE,
                format!(
                    "expected '{}' here, found '{}'",
                    expected.name(),
                    kind.name()
                ),
                element.name_span,
            );
            return None;
        }

        let id = self.push_node(kind, element.span, Some(parent));
        self.directives(id, element, false);

        for property in &element.properties {
            self.property(id, kind, None, property);
        }

        if !element.children.is_empty() {
            self.diagnostics.error(
                codes::WRONG_CHILD_COUNT,
                format!("'{}' cannot contain child elements", kind.name()),
                element.span,
            );
        }

        Some(id)
    }

    fn directives(&mut self, id: NodeId, element: &XamlElement, is_root: bool) {
        for directive in &element.directives {
            match directive.name.as_str() {
                "Class" => {
                    if !is_root {
                        self.diagnostics.error(
                            codes::UNSUPPORTED_DIRECTIVE,
                            "x:Class is only valid on the root element",
                            directive.name_span,
                        );
                    }
                }
                "Name" => {
                    let name = directive.value.trim();
                    if !is_identifier(name) {
                        self.diagnostics.error(
                            codes::INVALID_IDENTIFIER,
                            format!(
                                "x:Name '{name}' is not a valid identifier; names become members of the generated accessor type"
                            ),
                            directive.value_span,
                        );
                        continue;
                    }
                    if self.names.contains(name) {
                        self.diagnostics.error(
                            codes::DUPLICATE_NAME,
                            format!("x:Name '{name}' is already used in this document"),
                            directive.value_span,
                        );
                        continue;
                    }
                    self.names.insert(name.to_string());
                    self.nodes[id].name = Some(name.to_string());
                }
                other => self.diagnostics.error(
                    codes::UNSUPPORTED_DIRECTIVE,
                    format!("directive 'x:{other}' is not in the Direct XAML v0 subset"),
                    directive.name_span,
                ),
            }
        }
    }

    fn property(
        &mut self,
        id: NodeId,
        kind: ControlKind,
        parent_kind: Option<ControlKind>,
        property: &XamlProperty,
    ) {
        if let Some(owner) = property.owner.as_deref() {
            self.attached_property(id, owner, parent_kind, property);
            return;
        }

        if let Some(ir_event) = schema::lookup_event(&property.name) {
            self.event(id, ir_event, property);
            return;
        }

        let definition = match schema::lookup_property(kind, &property.name) {
            Some(definition) => definition,
            None => {
                let message = if schema::property_exists(&property.name) {
                    format!(
                        "property '{}' is not valid on '{}'",
                        property.name,
                        kind.name()
                    )
                } else {
                    format!(
                        "property '{}' is not in the Direct XAML v0 subset",
                        property.name
                    )
                };
                self.diagnostics
                    .error(codes::PROPERTY_NOT_VALID_HERE, message, property.name_span);
                return;
            }
        };

        if let Some(value) = self.value(&property.value, definition.value_type, property) {
            self.nodes[id].properties.push(HirProperty {
                name: property.name.clone(),
                value,
                invalidation: definition.invalidation,
                span: property.span,
            });
        }
    }

    fn attached_property(
        &mut self,
        id: NodeId,
        owner: &str,
        parent_kind: Option<ControlKind>,
        property: &XamlProperty,
    ) {
        let definition = match schema::lookup_attached(owner, &property.name) {
            Some(definition) => definition,
            None => {
                self.diagnostics.error(
                    codes::UNSUPPORTED_ATTACHED_PROPERTY,
                    format!(
                        "attached property '{}' is not in the Direct XAML v0 subset",
                        property.as_written()
                    ),
                    property.name_span,
                );
                return;
            }
        };

        if parent_kind != Some(definition.parent) {
            self.diagnostics.error(
                codes::PROPERTY_NOT_VALID_HERE,
                format!(
                    "'{}' only has an effect on a direct child of a {}",
                    property.as_written(),
                    definition.parent.name()
                ),
                property.name_span,
            );
            return;
        }

        if let Some(value) = self.value(&property.value, definition.value_type, property) {
            self.nodes[id].properties.push(HirProperty {
                name: property.as_written(),
                value,
                invalidation: definition.invalidation,
                span: property.span,
            });
        }
    }

    fn event(&mut self, id: NodeId, ir_event: &'static str, property: &XamlProperty) {
        let handler = match property.value.as_literal() {
            Some(handler) => handler.trim(),
            None => {
                self.diagnostics.error(
                    codes::BAD_VALUE,
                    format!(
                        "the '{}' handler must be a method name, not a markup extension",
                        property.name
                    ),
                    property.value_span,
                );
                return;
            }
        };

        if !is_identifier(handler) {
            self.diagnostics.error(
                codes::INVALID_IDENTIFIER,
                format!("'{handler}' is not a valid method name"),
                property.value_span,
            );
            return;
        }

        self.nodes[id].events.push(HirEvent {
            ir_event,
            handler: handler.to_string(),
            span: property.span,
        });
    }

    fn value(
        &mut self,
        value: &AttributeValue,
        value_type: ValueType,
        property: &XamlProperty,
    ) -> Option<HirValue> {
        let raw = match value {
            AttributeValue::Markup(extension) => {
                if !schema::is_supported_markup_extension(&extension.name) {
                    self.diagnostics.error(
                        codes::UNSUPPORTED_MARKUP_EXTENSION,
                        format!(
                            "markup extension '{{{}}}' is not in the Direct XAML v0 subset; v0 accepts {{ThemeResource}} and {{StaticResource}} only",
                            extension.name
                        ),
                        property.value_span,
                    );
                    return None;
                }

                let key = match extension.arguments.as_slice() {
                    [key] => key.clone(),
                    _ => {
                        self.diagnostics.error(
                            codes::BAD_VALUE,
                            format!(
                                "{{{}}} takes exactly one resource key",
                                extension.name
                            ),
                            property.value_span,
                        );
                        return None;
                    }
                };

                let kind = if extension.name == "ThemeResource" {
                    ResourceKind::Theme
                } else {
                    ResourceKind::Static
                };
                return Some(HirValue::Resource(ResourceRef { kind, key }));
            }
            AttributeValue::Literal(raw) => raw.as_str(),
        };

        let parsed = match value_type {
            ValueType::Double => parse_double(raw).map(LiteralValue::Double),
            ValueType::Length => parse_length(raw).map(LiteralValue::Length),
            ValueType::GridLength => parse_grid_length(raw).map(LiteralValue::GridLength),
            ValueType::Thickness => parse_thickness(raw).map(LiteralValue::Thickness),
            ValueType::CornerRadius => parse_corner_radius(raw).map(LiteralValue::CornerRadius),
            ValueType::Brush => parse_color(raw).map(LiteralValue::Color),
            ValueType::Str => Ok(LiteralValue::Str(raw.to_string())),
            ValueType::Bool => parse_bool(raw).map(LiteralValue::Bool),
            ValueType::Int => parse_int(raw).map(LiteralValue::Int),
            ValueType::Enumeration(enum_kind) => match enum_kind.resolve(raw.trim()) {
                Some(variant) => Ok(LiteralValue::Enumeration {
                    enum_name: enum_kind.name(),
                    variant,
                }),
                None => Err(format!(
                    "'{}' is not a {}; expected one of {}",
                    raw.trim(),
                    enum_kind.name(),
                    enum_kind.variants().join(", ")
                )),
            },
        };

        match parsed {
            Ok(literal) => Some(HirValue::Literal(literal)),
            Err(message) => {
                self.diagnostics.error(
                    codes::BAD_VALUE,
                    format!("{}: {message}", property.as_written()),
                    property.value_span,
                );
                None
            }
        }
    }

    fn text(&mut self, id: NodeId, kind: ControlKind, element: &XamlElement) {
        if element.text.is_empty() {
            return;
        }

        let span = element.text_span.unwrap_or(element.span);

        if kind != ControlKind::TextBlock {
            self.diagnostics.error(
                codes::ELEMENT_NOT_VALID_HERE,
                format!("'{}' cannot contain text content", kind.name()),
                span,
            );
            return;
        }

        if element
            .properties
            .iter()
            .any(|property| property.owner.is_none() && property.name == "Text")
        {
            self.diagnostics.error(
                codes::TEXT_AND_TEXT_ATTRIBUTE,
                "TextBlock has both a Text attribute and text content; use one or the other",
                span,
            );
            return;
        }

        self.nodes[id].text = Some(element.text.clone());
    }

    fn property_element(
        &mut self,
        id: NodeId,
        kind: ControlKind,
        property_element: &XamlPropertyElement,
    ) {
        if property_element.owner != kind.name() {
            self.diagnostics.error(
                codes::UNSUPPORTED_PROPERTY_ELEMENT,
                format!(
                    "property element '{}.{}' does not belong to '{}'",
                    property_element.owner,
                    property_element.name,
                    kind.name()
                ),
                property_element.name_span,
            );
            return;
        }

        let element_kind = match schema::lookup_property_element(kind, &property_element.name) {
            Some(element_kind) => element_kind,
            None => {
                self.diagnostics.error(
                    codes::UNSUPPORTED_PROPERTY_ELEMENT,
                    format!(
                        "property element '{}.{}' is not in the Direct XAML v0 subset",
                        property_element.owner, property_element.name
                    ),
                    property_element.name_span,
                );
                return;
            }
        };

        let expected = element_kind.child_kind();
        for child in &property_element.children {
            if let Some(child_id) = self.definition(child, expected, id) {
                self.nodes[id].children.push(child_id);
            }
        }
    }

    fn check_content(&mut self, kind: ControlKind, element: &XamlElement, visual_children: usize) {
        match kind.content() {
            ContentKind::None => {
                if visual_children > 0 {
                    self.diagnostics.error(
                        codes::WRONG_CHILD_COUNT,
                        format!("'{}' cannot contain child elements", kind.name()),
                        element.span,
                    );
                }
            }
            ContentKind::Single => {
                if visual_children != 1 {
                    self.diagnostics.error(
                        codes::WRONG_CHILD_COUNT,
                        format!(
                            "'{}' takes exactly one child element, found {visual_children}",
                            kind.name()
                        ),
                        element.span,
                    );
                }
            }
            ContentKind::Many => {}
            ContentKind::Text => {
                if visual_children > 0 {
                    self.diagnostics.error(
                        codes::WRONG_CHILD_COUNT,
                        "TextBlock cannot contain child elements; inline runs are not in the Direct XAML v0 subset",
                        element.span,
                    );
                }
            }
        }
    }
}

fn is_identifier(value: &str) -> bool {
    let mut chars = value.chars();
    match chars.next() {
        Some(first) if first.is_ascii_alphabetic() || first == '_' => {}
        _ => return false,
    }
    chars.all(|c| c.is_ascii_alphanumeric() || c == '_')
}

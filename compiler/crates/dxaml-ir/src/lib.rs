//! The compiled representation a renderer consumes.
//!
//! The IR is deliberately backend-neutral: it carries structure, typed property values, runtime
//! resource slots, named slots and actions, but **no geometry and no resolved colours**. Both
//! depend on runtime state — window size, DPI, active theme — so folding them in at compile time
//! would defeat the point.
//!
//! `../../schemas/dxir-v0.schema.json` is the normative schema for this format.

use std::collections::HashSet;

use serde::{Deserialize, Serialize};

pub const IR_VERSION: &str = "0.2.0";

/// Capability names a runtime must understand before it may load the document.
pub mod features {
    pub const NAMED_SLOTS: &str = "named-slots";
    pub const BINDINGS: &str = "bindings";
    pub const THEME_RESOURCES: &str = "theme-resources";
    pub const ACTIONS: &str = "actions";
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IrDocument {
    pub ir_version: String,
    pub compiler_version: String,
    pub source: IrSource,
    pub class_name: String,
    /// C# type resolved from root `x:DataType`; required when `bindings` is non-empty.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub binding_context_type: Option<String>,
    pub features: Vec<String>,
    pub nodes: Vec<IrNode>,
    pub properties: Vec<IrProperty>,
    pub named_slots: Vec<IrNamedSlot>,
    pub bindings: Vec<IrBinding>,
    pub resources: Vec<IrResource>,
    pub actions: Vec<IrAction>,
    pub semantics: Vec<IrSemantics>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IrSource {
    pub path: String,
    /// Non-cryptographic content hash for build-cache invalidation only.
    pub hash: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IrNode {
    pub id: usize,
    pub kind: String,
    pub parent: Option<usize>,
    /// For a `grid`, this includes its `rowDefinition` and `columnDefinition` nodes as well as
    /// its visual children. Consumers separate them by `kind`.
    pub children: Vec<usize>,
    pub text: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IrProperty {
    pub node: usize,
    pub name: String,
    pub value: IrValue,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum IrValue {
    /// Resolved at runtime through the host's resource lookup, for any property type.
    Resource {
        resource: usize,
    },
    Double {
        value: f64,
    },
    Length {
        value: IrLength,
    },
    GridLength {
        value: IrGridLength,
    },
    /// left, top, right, bottom
    Thickness {
        value: [f64; 4],
    },
    /// topLeft, topRight, bottomRight, bottomLeft
    CornerRadius {
        value: [f64; 4],
    },
    Color {
        argb: String,
    },
    #[serde(rename = "string")]
    Str {
        value: String,
    },
    #[serde(rename = "bool")]
    Boolean {
        value: bool,
    },
    #[serde(rename = "enum")]
    Enumeration {
        #[serde(rename = "enum")]
        enum_name: String,
        value: String,
    },
    Int {
        value: i64,
    },
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum IrLength {
    Auto,
    Dip { value: f64 },
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum IrGridLength {
    Auto,
    Dip { value: f64 },
    Star { value: f64 },
}

/// An `x:Name` target. The generated C# accessor type is derived from this table.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IrNamedSlot {
    pub name: String,
    pub node: usize,
    pub mutable: Vec<IrMutableProperty>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IrMutableProperty {
    pub property: String,
    pub invalidation: Vec<String>,
}

/// A compile-time binding. v0 emits an empty table for views that use named-slot code-behind.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IrBinding {
    pub target_node: usize,
    pub target_property: String,
    pub source_path: Vec<String>,
    pub mode: IrBindingMode,
    pub invalidation: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum IrBindingMode {
    OneTime,
    OneWay,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IrResource {
    pub id: usize,
    pub kind: String,
    pub key: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IrAction {
    pub node: usize,
    pub event: String,
    pub handler: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IrSemantics {
    pub node: usize,
    pub role: Option<String>,
    pub name: Option<String>,
    pub focusable: bool,
}

impl IrDocument {
    /// Pretty JSON with a trailing newline, so the file is diff-friendly and golden tests are
    /// stable across platforms.
    pub fn to_json(&self) -> Result<String, serde_json::Error> {
        let mut json = serde_json::to_string_pretty(self)?;
        json.push('\n');
        Ok(json)
    }

    pub fn from_json(raw: &str) -> Result<Self, serde_json::Error> {
        serde_json::from_str(raw)
    }
}

/// Structural checks that must hold for any IR the compiler emits. A failure here is a compiler
/// bug, not a user error, which is why it has its own diagnostic class (`DX4001`).
pub fn validate(document: &IrDocument) -> Vec<String> {
    let mut problems = Vec::new();

    if document.ir_version != IR_VERSION {
        problems.push(format!(
            "ir_version is '{}', expected '{IR_VERSION}'",
            document.ir_version
        ));
    }

    if document.nodes.is_empty() {
        problems.push("document has no nodes".to_string());
    }

    let mut root_count = 0;
    let mut root_node = None;
    for (index, node) in document.nodes.iter().enumerate() {
        if node.id != index {
            problems.push(format!(
                "node at index {index} declares id {}; ids must match their position",
                node.id
            ));
        }
        if let Some(parent) = node.parent {
            if parent >= document.nodes.len() {
                problems.push(format!("node {} has out-of-range parent {parent}", node.id));
            } else if !document.nodes[parent].children.contains(&node.id) {
                problems.push(format!(
                    "node {} claims parent {parent}, which does not list it as a child",
                    node.id
                ));
            }
        } else {
            root_count += 1;
            root_node = Some(index);
        }
        for (child_index, &child) in node.children.iter().enumerate() {
            if node.children[..child_index].contains(&child) {
                problems.push(format!(
                    "node {} lists child {child} more than once",
                    node.id
                ));
            }

            match document.nodes.get(child) {
                None => problems.push(format!("node {} has out-of-range child {child}", node.id)),
                Some(child_node) if child_node.parent != Some(node.id) => problems.push(format!(
                    "node {} lists child {child}, which does not point back at it",
                    node.id
                )),
                Some(_) => {}
            }
        }
    }

    if !document.nodes.is_empty() && root_count != 1 {
        problems.push(format!(
            "expected exactly one root node, found {root_count}"
        ));
    } else if let Some(root) = root_node {
        validate_connected_tree(document, root, &mut problems);
    }

    for property in &document.properties {
        if property.node >= document.nodes.len() {
            problems.push(format!(
                "property '{}' references unknown node {}",
                property.name, property.node
            ));
        }
        if let IrValue::Resource { resource } = &property.value {
            if *resource >= document.resources.len() {
                problems.push(format!(
                    "property '{}' references unknown resource {resource}",
                    property.name
                ));
            }
        }
    }

    let has_binding_context_type = matches!(
        document.binding_context_type.as_deref(),
        Some(value) if !value.is_empty()
    );
    if !document.bindings.is_empty() && !has_binding_context_type {
        problems.push("bindings require a non-empty binding_context_type".to_string());
    }
    if !document.bindings.is_empty()
        && !document
            .features
            .iter()
            .any(|feature| feature == features::BINDINGS)
    {
        problems.push("bindings require the bindings feature".to_string());
    }

    let mut seen_binding_targets = HashSet::new();
    for binding in &document.bindings {
        if binding.target_node >= document.nodes.len() {
            problems.push(format!(
                "binding for '{}' references unknown node {}",
                binding.target_property, binding.target_node
            ));
        }
        if binding.source_path.len() != 1
            || !binding.source_path.iter().all(|part| is_identifier(part))
        {
            problems.push(format!(
                "binding for '{}.{}' has an invalid source path",
                binding.target_node, binding.target_property
            ));
        }
        if binding.invalidation.is_empty() {
            problems.push(format!(
                "binding for '{}.{}' declares no invalidation",
                binding.target_node, binding.target_property
            ));
        }
        if !seen_binding_targets.insert((binding.target_node, binding.target_property.clone())) {
            problems.push(format!(
                "binding target '{}.{}' is declared more than once",
                binding.target_node, binding.target_property
            ));
        }
    }

    for (index, resource) in document.resources.iter().enumerate() {
        if resource.id != index {
            problems.push(format!(
                "resource at index {index} declares id {}; ids must match their position",
                resource.id
            ));
        }
    }

    let mut seen_names = Vec::new();
    for slot in &document.named_slots {
        if slot.node >= document.nodes.len() {
            problems.push(format!(
                "named slot '{}' references unknown node {}",
                slot.name, slot.node
            ));
        }
        if seen_names.contains(&slot.name) {
            problems.push(format!("named slot '{}' is declared twice", slot.name));
        }
        seen_names.push(slot.name.clone());
    }

    for action in &document.actions {
        if action.node >= document.nodes.len() {
            problems.push(format!(
                "action '{}' references unknown node {}",
                action.event, action.node
            ));
        }
    }

    for entry in &document.semantics {
        if entry.node >= document.nodes.len() {
            problems.push(format!(
                "semantics entry references unknown node {}",
                entry.node
            ));
        }
    }

    problems
}

fn validate_connected_tree(document: &IrDocument, root_node: usize, problems: &mut Vec<String>) {
    let mut visited = vec![false; document.nodes.len()];
    let mut pending = vec![root_node];

    while let Some(node) = pending.pop() {
        if visited[node] {
            problems.push(format!(
                "node {node} is reachable from root node {root_node} more than once"
            ));
            continue;
        }

        visited[node] = true;
        for &child in &document.nodes[node].children {
            if child < document.nodes.len() {
                pending.push(child);
            }
        }
    }

    if let Some(unreachable_node) = visited.iter().position(|seen| !seen) {
        problems.push(format!(
            "node {unreachable_node} is not reachable from root node {root_node}"
        ));
    }
}

fn is_identifier(value: &str) -> bool {
    let mut chars = value.chars();
    matches!(chars.next(), Some('A'..='Z' | 'a'..='z' | '_'))
        && chars.all(|ch| ch.is_ascii_alphanumeric() || ch == '_')
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> IrDocument {
        IrDocument {
            ir_version: IR_VERSION.to_string(),
            compiler_version: "0.1.0".to_string(),
            source: IrSource {
                path: "Sample.xaml".to_string(),
                hash: "fnv1a64:0123456789abcdef".to_string(),
            },
            class_name: "A.B".to_string(),
            binding_context_type: None,
            features: vec![features::NAMED_SLOTS.to_string()],
            nodes: vec![
                IrNode {
                    id: 0,
                    kind: "userControl".into(),
                    parent: None,
                    children: vec![1],
                    text: None,
                },
                IrNode {
                    id: 1,
                    kind: "textBlock".into(),

                    parent: Some(0),
                    children: vec![],
                    text: Some("hi".into()),
                },
            ],
            properties: vec![IrProperty {
                node: 1,
                name: "Foreground".to_string(),
                value: IrValue::Resource { resource: 0 },
            }],
            named_slots: vec![IrNamedSlot {
                name: "ResultText".to_string(),
                node: 1,
                mutable: vec![IrMutableProperty {
                    property: "Text".to_string(),
                    invalidation: vec!["measure".to_string(), "paint".to_string()],
                }],
            }],
            bindings: vec![],
            resources: vec![IrResource {
                id: 0,
                kind: "themeResource".to_string(),
                key: "QueryTextBrush".to_string(),
            }],
            actions: vec![],
            semantics: vec![],
        }
    }

    #[test]
    fn valid_documents_have_no_problems() {
        assert!(validate(&sample()).is_empty());
    }

    #[test]
    fn round_trips_through_json() {
        let document = sample();
        let json = document.to_json().expect("serialize");
        let parsed = IrDocument::from_json(&json).expect("deserialize");
        assert_eq!(document, parsed);
    }

    #[test]
    fn values_use_the_documented_shapes() {
        let cases = vec![
            (
                IrValue::Resource { resource: 3 },
                r#"{"type":"resource","resource":3}"#,
            ),
            (
                IrValue::Double { value: 12.0 },
                r#"{"type":"double","value":12.0}"#,
            ),
            (
                IrValue::Length {
                    value: IrLength::Auto,
                },
                r#"{"type":"length","value":{"kind":"auto"}}"#,
            ),
            (
                IrValue::GridLength {
                    value: IrGridLength::Star { value: 2.0 },
                },
                r#"{"type":"gridLength","value":{"kind":"star","value":2.0}}"#,
            ),
            (
                IrValue::Thickness {
                    value: [0.0, 0.0, 0.0, 2.0],
                },
                r#"{"type":"thickness","value":[0.0,0.0,0.0,2.0]}"#,
            ),
            // Doubled hashes: the expected JSON contains `"#`, which would otherwise terminate an
            // `r#"..."#` literal early.
            (
                IrValue::Color {
                    argb: "#FF102030".into(),
                },
                r##"{"type":"color","argb":"#FF102030"}"##,
            ),
            (
                IrValue::Str { value: "hi".into() },
                r#"{"type":"string","value":"hi"}"#,
            ),
            (
                IrValue::Boolean { value: true },
                r#"{"type":"bool","value":true}"#,
            ),
            (
                IrValue::Enumeration {
                    enum_name: "Visibility".into(),
                    value: "Collapsed".into(),
                },
                r#"{"type":"enum","enum":"Visibility","value":"Collapsed"}"#,
            ),
            (IrValue::Int { value: 1 }, r#"{"type":"int","value":1}"#),
        ];

        for (value, expected) in cases {
            let json = serde_json::to_string(&value).expect("serialize");
            assert_eq!(json, expected);
        }
    }

    #[test]
    fn detects_broken_parent_links() {
        let mut document = sample();
        document.nodes[1].parent = Some(0);
        document.nodes[0].children.clear();
        let problems = validate(&document);
        assert!(problems
            .iter()
            .any(|p| p.contains("does not list it as a child")));
    }

    #[test]
    fn detects_dangling_resource_references() {
        let mut document = sample();
        document.resources.clear();
        let problems = validate(&document);
        assert!(problems.iter().any(|p| p.contains("unknown resource")));
    }

    #[test]
    fn detects_dangling_binding_targets() {
        let mut document = sample();
        document.bindings.push(IrBinding {
            target_node: 99,
            target_property: "Text".into(),
            source_path: vec!["ResultText".into()],
            mode: IrBindingMode::OneWay,
            invalidation: vec!["measure".into(), "paint".into()],
        });
        let problems = validate(&document);
        assert!(problems
            .iter()
            .any(|problem| problem.contains("binding for 'Text' references unknown node 99")));
    }

    #[test]
    fn detects_multiple_roots() {
        let mut document = sample();
        document.nodes[1].parent = None;
        document.nodes[0].children.clear();
        let problems = validate(&document);
        assert!(problems.iter().any(|p| p.contains("exactly one root")));
    }

    #[test]
    fn detects_duplicate_children() {
        let mut document = sample();
        document.nodes[0].children.push(1);

        let problems = validate(&document);

        assert!(problems
            .iter()
            .any(|problem| problem.contains("lists child 1 more than once")));
    }

    #[test]
    fn detects_disconnected_cycles() {
        let mut document = sample();
        document.nodes[0].children.clear();
        document.nodes[1].parent = Some(1);
        document.nodes[1].children = vec![1];

        let problems = validate(&document);

        assert!(problems
            .iter()
            .any(|problem| problem.contains("not reachable from root node 0")));
    }
}

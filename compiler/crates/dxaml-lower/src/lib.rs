//! Lowers the typed HIR into the backend-neutral UI IR.
//!
//! Lowering is deterministic: the same HIR always produces byte-identical IR. Resource slots are
//! interned in first-encounter order, and every table is emitted in node order.

use dxaml_hir::{HirBindingMode, HirDocument, HirValue, LiteralValue, ResourceRef};
use dxaml_ir::{
    features, IrAction, IrBinding, IrBindingMode, IrDocument, IrGridLength, IrLength,
    IrMutableProperty, IrNamedSlot, IrNode, IrProperty, IrResource, IrSource, IrValue, IR_VERSION,
};
use dxaml_schema as schema;

pub fn lower(
    hir: &HirDocument,
    source: &str,
    source_path: &str,
    compiler_version: &str,
) -> IrDocument {
    let mut resources = ResourceTable::default();
    let mut nodes = Vec::with_capacity(hir.nodes.len());
    let mut properties = Vec::new();
    let mut named_slots = Vec::new();
    let mut actions = Vec::new();
    let bindings = hir
        .bindings
        .iter()
        .map(|binding| IrBinding {
            target_node: binding.target_node,
            target_property: binding.target_property.clone(),
            source_path: binding.source_path.clone(),
            mode: match binding.mode {
                HirBindingMode::OneTime => IrBindingMode::OneTime,
                HirBindingMode::OneWay => IrBindingMode::OneWay,
            },
            invalidation: binding
                .invalidation
                .names()
                .into_iter()
                .map(str::to_string)
                .collect(),
        })
        .collect::<Vec<_>>();

    for (id, node) in hir.nodes.iter().enumerate() {
        nodes.push(IrNode {
            id,
            kind: node.kind.ir_name().to_string(),
            parent: node.parent,
            children: node.children.clone(),
            text: node.text.clone(),
        });

        for property in &node.properties {
            properties.push(IrProperty {
                node: id,
                name: property.name.clone(),
                value: lower_value(&property.value, &mut resources),
            });
        }

        for event in &node.events {
            actions.push(IrAction {
                node: id,
                event: event.ir_event.to_string(),
                handler: event.handler.clone(),
            });
        }

        if let Some(name) = &node.name {
            named_slots.push(IrNamedSlot {
                name: name.clone(),
                node: id,
                mutable: schema::mutable_properties(node.kind)
                    .into_iter()
                    .map(|definition| IrMutableProperty {
                        property: definition.name.to_string(),
                        invalidation: definition
                            .invalidation
                            .names()
                            .into_iter()
                            .map(str::to_string)
                            .collect(),
                    })
                    .collect(),
            });
        }
    }

    let resources = resources.into_entries();

    let mut feature_list = Vec::new();
    if !named_slots.is_empty() {
        feature_list.push(features::NAMED_SLOTS.to_string());
    }
    if !bindings.is_empty() {
        feature_list.push(features::BINDINGS.to_string());
    }
    if !resources.is_empty() {
        feature_list.push(features::THEME_RESOURCES.to_string());
    }
    if !actions.is_empty() {
        feature_list.push(features::ACTIONS.to_string());
    }

    IrDocument {
        ir_version: IR_VERSION.to_string(),
        compiler_version: compiler_version.to_string(),
        source: IrSource {
            path: source_path.to_string(),
            hash: format!("fnv1a64:{:016x}", fnv1a64(source.as_bytes())),
        },
        class_name: hir.class_name.clone(),
        binding_context_type: hir.binding_context_type.clone(),
        features: feature_list,
        nodes,
        properties,
        named_slots,
        bindings,
        resources,
        actions,
        // Automation metadata is supplied at runtime by ServiceResultViewHost in v0; the table
        // exists so a later version can carry a virtual automation tree without an IR break.
        semantics: Vec::new(),
    }
}

#[derive(Default)]
struct ResourceTable {
    entries: Vec<IrResource>,
}

impl ResourceTable {
    fn intern(&mut self, resource: &ResourceRef) -> usize {
        let kind = resource.kind.ir_name();
        if let Some(existing) = self
            .entries
            .iter()
            .find(|entry| entry.kind == kind && entry.key == resource.key)
        {
            return existing.id;
        }

        let id = self.entries.len();
        self.entries.push(IrResource {
            id,
            kind: kind.to_string(),
            key: resource.key.clone(),
        });
        id
    }

    fn into_entries(self) -> Vec<IrResource> {
        self.entries
    }
}

fn lower_value(value: &HirValue, resources: &mut ResourceTable) -> IrValue {
    match value {
        HirValue::Resource(resource) => IrValue::Resource {
            resource: resources.intern(resource),
        },
        HirValue::Literal(literal) => lower_literal(literal),
    }
}

fn lower_literal(literal: &LiteralValue) -> IrValue {
    match literal {
        LiteralValue::Double(value) => IrValue::Double { value: *value },
        LiteralValue::Length(length) => IrValue::Length {
            value: match length {
                dxaml_hir::Length::Auto => IrLength::Auto,
                dxaml_hir::Length::Dip(value) => IrLength::Dip { value: *value },
            },
        },
        LiteralValue::GridLength(length) => IrValue::GridLength {
            value: match length {
                dxaml_hir::GridLength::Auto => IrGridLength::Auto,
                dxaml_hir::GridLength::Dip(value) => IrGridLength::Dip { value: *value },
                dxaml_hir::GridLength::Star(value) => IrGridLength::Star { value: *value },
            },
        },
        LiteralValue::Thickness(thickness) => IrValue::Thickness {
            value: [
                thickness.left,
                thickness.top,
                thickness.right,
                thickness.bottom,
            ],
        },
        LiteralValue::CornerRadius(radius) => IrValue::CornerRadius {
            value: [
                radius.top_left,
                radius.top_right,
                radius.bottom_right,
                radius.bottom_left,
            ],
        },
        LiteralValue::Color(color) => IrValue::Color {
            argb: color.to_argb_hex(),
        },
        LiteralValue::Str(value) => IrValue::Str {
            value: value.clone(),
        },
        LiteralValue::Bool(value) => IrValue::Boolean { value: *value },
        LiteralValue::Int(value) => IrValue::Int { value: *value },
        LiteralValue::Enumeration { enum_name, variant } => IrValue::Enumeration {
            enum_name: (*enum_name).to_string(),
            value: (*variant).to_string(),
        },
    }
}

/// FNV-1a, 64-bit. Used only for build-cache invalidation, never for integrity.
fn fnv1a64(data: &[u8]) -> u64 {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for &byte in data {
        hash ^= u64::from(byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}

#[cfg(test)]
mod tests {
    use super::*;

    const HEADER: &str = concat!(
        r#"xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" "#,
        r#"xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" "#,
        r#"x:Class="Easydict.Sample""#
    );

    fn lower_body(body: &str) -> IrDocument {
        let source = format!("<UserControl {HEADER}>{body}</UserControl>");
        let (hir, diagnostics) = dxaml_hir::analyze(&source);
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());
        let hir = hir.expect("hir");
        lower(&hir, &source, "Sample.xaml", "0.1.0")
    }

    #[test]
    fn produces_valid_ir() {
        let document = lower_body(
            r#"<Border x:Name="Root" Background="{ThemeResource CardBrush}"><TextBlock x:Name="Body" Text="hi"/></Border>"#,
        );
        assert!(dxaml_ir::validate(&document).is_empty());
        assert_eq!(document.ir_version, IR_VERSION);
        assert_eq!(document.class_name, "Easydict.Sample");
    }

    #[test]
    fn interns_repeated_resources_once() {
        let document = lower_body(
            r#"<Border BorderBrush="{ThemeResource Stroke}" Background="{ThemeResource Stroke}"><TextBlock Foreground="{ThemeResource Stroke}"/></Border>"#,
        );
        assert_eq!(document.resources.len(), 1);
        assert_eq!(document.resources[0].key, "Stroke");
        for property in &document.properties {
            assert_eq!(property.value, IrValue::Resource { resource: 0 });
        }
    }

    #[test]
    fn distinguishes_theme_from_static_resources() {
        let document = lower_body(
            r#"<Border BorderBrush="{ThemeResource Same}" Background="{StaticResource Same}"><TextBlock/></Border>"#,
        );
        assert_eq!(document.resources.len(), 2);
        assert_eq!(document.resources[0].kind, "themeResource");
        assert_eq!(document.resources[1].kind, "staticResource");
    }

    #[test]
    fn named_slots_carry_their_mutable_set() {
        let document = lower_body(r#"<TextBlock x:Name="ResultText"/>"#);
        let slot = &document.named_slots[0];
        assert_eq!(slot.name, "ResultText");

        let text = slot
            .mutable
            .iter()
            .find(|entry| entry.property == "Text")
            .expect("Text is mutable");
        assert_eq!(text.invalidation, vec!["measure", "paint"]);

        let foreground = slot
            .mutable
            .iter()
            .find(|entry| entry.property == "Foreground")
            .expect("Foreground is mutable");
        assert_eq!(foreground.invalidation, vec!["paint"]);
    }

    #[test]
    fn records_features_actually_used() {
        let document = lower_body(r#"<Border PointerPressed="OnPressed"><TextBlock/></Border>"#);
        assert!(document.features.contains(&features::ACTIONS.to_string()));
        assert!(!document
            .features
            .contains(&features::NAMED_SLOTS.to_string()));
    }

    #[test]
    fn lowers_typed_bindings_with_context_and_invalidation() {
        let source = format!(
            "<UserControl {HEADER} x:DataType=\"Easydict.SampleContext\">\
                <TextBlock Text=\"{{x:Bind ResultText, Mode=OneWay}}\"/>\
            </UserControl>"
        );
        let (hir, diagnostics) = dxaml_hir::analyze(&source);
        assert!(!diagnostics.has_errors(), "{:?}", diagnostics.sorted());
        let hir = hir.expect("hir");

        let document = lower(&hir, &source, "Sample.xaml", "0.1.0");

        assert!(document.features.contains(&features::BINDINGS.to_string()));
        assert_eq!(
            document.binding_context_type.as_deref(),
            Some("Easydict.SampleContext")
        );
        assert_eq!(document.bindings.len(), 1);
        let binding = &document.bindings[0];
        assert_eq!(binding.target_node, 1);
        assert_eq!(binding.target_property, "Text");
        assert_eq!(binding.source_path, vec!["ResultText"]);
        assert_eq!(binding.mode, IrBindingMode::OneWay);
        assert_eq!(binding.invalidation, vec!["measure", "paint"]);
    }

    #[test]
    fn lowering_is_deterministic() {
        let body = r#"<Border x:Name="Root" CornerRadius="4"><TextBlock Text="hi"/></Border>"#;
        let first = lower_body(body).to_json().expect("serialize");
        let second = lower_body(body).to_json().expect("serialize");
        assert_eq!(first, second);
    }

    #[test]
    fn hashes_differ_for_different_sources() {
        let a = lower_body(r#"<TextBlock Text="a"/>"#);
        let b = lower_body(r#"<TextBlock Text="b"/>"#);
        assert_ne!(a.source.hash, b.source.hash);
        assert!(a.source.hash.starts_with("fnv1a64:"));
        assert_eq!(a.source.hash.len(), "fnv1a64:".len() + 16);
    }
}

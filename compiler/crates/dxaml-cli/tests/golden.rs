//! End-to-end compiler tests driven by real markup from the app.
//!
//! `MinimalServiceResultItem.xaml` is a verbatim copy of
//! `dotnet/src/Easydict.WinUI/Views/Controls/MinimalServiceResultItem.xaml`. Keeping a copy rather
//! than reaching across the tree means the compiler's test suite stays runnable on its own, and a
//! change to the shipping card shows up here as a deliberate fixture update.

use std::collections::BTreeSet;
use std::path::PathBuf;

use dxaml_cli::compile_source;
use dxaml_ir::{IrDocument, IrValue};

fn fixtures_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
}

/// Line endings are normalised so a Windows checkout with `core.autocrlf=true` produces the same
/// content hash, and therefore the same IR, as a Linux one.
fn read_fixture(name: &str) -> String {
    let path = fixtures_dir().join(name);
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", path.display()));
    raw.replace("\r\n", "\n")
}

fn compile_fixture(name: &str) -> (Option<IrDocument>, Vec<String>) {
    let source = read_fixture(name);
    // The display path is the bare file name so diagnostics and the IR header do not embed an
    // absolute, machine-specific path.
    let result = compile_source(&source, name);
    (result.document, result.diagnostics)
}

fn minimal_card() -> IrDocument {
    let (document, diagnostics) = compile_fixture("MinimalServiceResultItem.xaml");
    assert!(
        diagnostics.is_empty(),
        "the shipping minimal card must compile cleanly, got:\n{}",
        diagnostics.join("\n")
    );
    document.expect("minimal card produced no IR")
}

#[test]
fn compiles_the_shipping_minimal_card() {
    let document = minimal_card();

    assert!(
        dxaml_ir::validate(&document).is_empty(),
        "{:?}",
        dxaml_ir::validate(&document)
    );
    assert_eq!(
        document.class_name,
        "Easydict.WinUI.Views.Controls.MinimalServiceResultItem"
    );

    let count_of = |kind: &str| document.nodes.iter().filter(|n| n.kind == kind).count();
    assert_eq!(count_of("userControl"), 1);
    assert_eq!(count_of("border"), 3);
    assert_eq!(count_of("grid"), 2);
    assert_eq!(count_of("rowDefinition"), 2);
    assert_eq!(count_of("columnDefinition"), 2);
    assert_eq!(count_of("stackPanel"), 1);
    assert_eq!(count_of("textBlock"), 5);
    assert_eq!(count_of("button"), 1);
    assert_eq!(document.nodes.len(), 17);
}

#[test]
fn exposes_every_named_element_as_a_slot() {
    let document = minimal_card();

    let names: Vec<&str> = document
        .named_slots
        .iter()
        .map(|slot| slot.name.as_str())
        .collect();

    // Document order, which is what the generated accessor type will follow.
    assert_eq!(
        names,
        vec![
            "RootBorder",
            "HeaderBar",
            "ServiceNameText",
            "StatusText",
            "ContentArea",
            "PendingQueryText",
            "ResultText",
            "ErrorText",
            "CopyButton",
        ]
    );
}

/// The compatibility claim in `spec/compatibility.md` is that `MinimalServiceResultItem.UpdateUI()`
/// ports onto the direct backend without being rewritten. That only holds if every property the
/// method writes is exposed as a mutable slot property. This test pins that.
#[test]
fn covers_everything_update_ui_writes() {
    let document = minimal_card();

    let writes: &[(&str, &str)] = &[
        ("RootBorder", "Opacity"),
        ("ServiceNameText", "Text"),
        ("StatusText", "Text"),
        ("StatusText", "Visibility"),
        ("PendingQueryText", "Visibility"),
        ("ResultText", "Text"),
        ("ResultText", "Foreground"),
        ("ResultText", "Visibility"),
        ("ErrorText", "Text"),
        ("ErrorText", "Visibility"),
        ("ContentArea", "Visibility"),
        ("CopyButton", "Content"),
        ("CopyButton", "Visibility"),
        // Written by ApplyAppearance.
        ("ServiceNameText", "FontSize"),
        ("StatusText", "FontSize"),
        ("ResultText", "FontSize"),
    ];

    for (slot_name, property) in writes {
        let slot = document
            .named_slots
            .iter()
            .find(|slot| slot.name == *slot_name)
            .unwrap_or_else(|| panic!("no slot named '{slot_name}'"));

        assert!(
            slot.mutable.iter().any(|entry| entry.property == *property),
            "slot '{slot_name}' cannot write '{property}', which UpdateUI does"
        );
    }
}

#[test]
fn classifies_invalidation_per_property() {
    let document = minimal_card();
    let slot = document
        .named_slots
        .iter()
        .find(|slot| slot.name == "ResultText")
        .expect("ResultText");

    let invalidation = |property: &str| -> Vec<String> {
        slot.mutable
            .iter()
            .find(|entry| entry.property == property)
            .unwrap_or_else(|| panic!("{property} is not mutable"))
            .invalidation
            .clone()
    };

    // Text changes reflow; a colour change only repaints; visibility also moves automation.
    assert_eq!(invalidation("Text"), vec!["measure", "paint"]);
    assert_eq!(invalidation("Foreground"), vec!["paint"]);
    assert_eq!(
        invalidation("Visibility"),
        vec!["measure", "paint", "semantics"]
    );
}

#[test]
fn interns_theme_resources_without_folding_them() {
    let document = minimal_card();

    let keys: BTreeSet<&str> = document
        .resources
        .iter()
        .map(|resource| resource.key.as_str())
        .collect();

    let expected: BTreeSet<&str> = [
        "ResultViewBackgroundBrush",
        "CardStrokeColorDefaultBrush",
        "EasydictCardBorderThickness",
        "EasydictCardCornerRadius",
        "ServiceResultHeaderBackgroundBrush",
        "ServiceResultHeaderForegroundBrush",
        "ServiceResultHeaderSecondaryForegroundBrush",
        "TextFillColorTertiaryBrush",
        "QueryTextBrush",
        "SystemFillColorCriticalBrush",
        "ControlFillColorDefaultBrush",
        "ControlStrokeColorDefaultBrush",
    ]
    .into_iter()
    .collect();
    assert_eq!(keys, expected);

    // CardStrokeColorDefaultBrush is referenced twice but must be interned once.
    assert_eq!(document.resources.len(), 12);

    for resource in &document.resources {
        assert_eq!(resource.kind, "themeResource");
    }

    // Nothing may have been folded into a literal colour: theme switching depends on it.
    assert!(
        !document
            .properties
            .iter()
            .any(|property| matches!(property.value, IrValue::Color { .. })),
        "theme resources must stay runtime slots"
    );
}

/// `BorderThickness` and `CornerRadius` are written as theme resources on the real card, which is
/// why resource references have to be legal for every property type, not only brushes.
#[test]
fn allows_resources_for_non_brush_properties() {
    let document = minimal_card();

    for name in ["BorderThickness", "CornerRadius"] {
        let values: Vec<&IrValue> = document
            .properties
            .iter()
            .filter(|property| property.name == name)
            .map(|property| &property.value)
            .collect();

        assert!(!values.is_empty(), "{name} is missing from the IR");
        assert!(
            values
                .iter()
                .any(|value| matches!(value, IrValue::Resource { .. })),
            "{name} should be a resource reference on at least one node, got {values:?}"
        );
    }
}

#[test]
fn records_header_and_copy_actions() {
    let document = minimal_card();
    assert_eq!(document.actions.len(), 2);

    let header_action = document
        .actions
        .iter()
        .find(|action| action.handler == "OnHeaderPointerPressed")
        .expect("header action");
    assert_eq!(header_action.event, "pointerPressed");
    let header = document
        .named_slots
        .iter()
        .find(|slot| slot.name == "HeaderBar")
        .expect("HeaderBar");
    assert_eq!(header_action.node, header.node);

    let copy_action = document
        .actions
        .iter()
        .find(|action| action.handler == "CopyCommand")
        .expect("copy command");
    assert_eq!(copy_action.event, "click");
    let copy = document
        .named_slots
        .iter()
        .find(|slot| slot.name == "CopyButton")
        .expect("CopyButton");
    assert_eq!(copy_action.node, copy.node);
    assert!(document.properties.iter().any(|property| {
        property.node == copy.node
            && property.name == "Content"
            && matches!(&property.value, IrValue::Str { value } if value == "Copy")
    }));
}

#[test]
fn rejects_every_unsupported_construct() {
    let (document, diagnostics) = compile_fixture("UnsupportedConstructs.xaml");

    assert!(
        document.is_none(),
        "a document outside the subset must not produce IR"
    );
    let joined = diagnostics.join("\n");

    for (code, needle) in [
        ("DX3001", "ProgressRing"),
        ("DX3001", "FontIcon"),
        ("DX3001", "Image"),
        ("DX3001", "HyperlinkButton"),
        ("DX3001", "ScrollViewer"),
        ("DX3004", "Binding"),
        ("DX3002", "ToolTipService.ToolTip"),
        ("DX3002", "AutomationProperties.AutomationId"),
        ("DX3005", "x:Uid"),
        ("DX2005", "Hidden"),
        ("DX2004", "TextWrapping"),
    ] {
        assert!(
            diagnostics
                .iter()
                .any(|line| line.contains(code) && line.contains(needle)),
            "expected a {code} diagnostic mentioning '{needle}', got:\n{joined}"
        );
    }
}

#[test]
fn diagnostics_use_the_msbuild_format() {
    let (_, diagnostics) = compile_fixture("UnsupportedConstructs.xaml");
    let first = diagnostics.first().expect("at least one diagnostic");

    // e.g. UnsupportedConstructs.xaml(14,10): error DX3001: ...
    assert!(first.starts_with("UnsupportedConstructs.xaml("), "{first}");
    assert!(first.contains("): error DX"), "{first}");
}

#[test]
fn diagnostics_are_ordered_by_position() {
    let (_, diagnostics) = compile_fixture("UnsupportedConstructs.xaml");

    fn line_of(entry: &str) -> usize {
        let open = entry.find('(').expect("open paren");
        let comma = entry.find(',').expect("comma");
        entry[open + 1..comma].parse().expect("line number")
    }

    let mut lines = Vec::new();
    for entry in &diagnostics {
        lines.push(line_of(entry));
    }

    let mut sorted = lines.clone();
    sorted.sort_unstable();
    assert_eq!(lines, sorted, "diagnostics must come out in source order");
}

#[test]
fn compilation_is_deterministic() {
    let first = minimal_card().to_json().expect("serialize");
    let second = minimal_card().to_json().expect("serialize");
    assert_eq!(first, second);
}

#[test]
fn ir_round_trips_through_json() {
    let document = minimal_card();
    let json = document.to_json().expect("serialize");
    let parsed = IrDocument::from_json(&json).expect("deserialize");
    assert_eq!(document, parsed);
}

/// Byte-exact regression golden.
///
/// The file is created on first run — review it, then commit it. After that any change to the
/// emitted IR shows up as a diff. Re-run with `UPDATE_GOLDEN=1` to accept an intended change.
#[test]
fn golden_ir_is_stable() {
    let mut document = minimal_card();
    // The compiler version would otherwise churn the golden on every release.
    document.compiler_version = "<version>".to_string();
    let actual = document.to_json().expect("serialize");

    let path = fixtures_dir().join("MinimalServiceResultItem.dxir.json");
    let updating = std::env::var_os("UPDATE_GOLDEN").is_some();

    if !path.exists() || updating {
        std::fs::write(&path, &actual)
            .unwrap_or_else(|error| panic!("cannot write {}: {error}", path.display()));
        if !updating {
            eprintln!(
                "note: created golden {} — review it and commit it",
                path.display()
            );
        }
        return;
    }

    let expected = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", path.display()))
        .replace("\r\n", "\n");

    assert_eq!(
        expected,
        actual,
        "emitted IR differs from {}; re-run with UPDATE_GOLDEN=1 to accept",
        path.display()
    );
}

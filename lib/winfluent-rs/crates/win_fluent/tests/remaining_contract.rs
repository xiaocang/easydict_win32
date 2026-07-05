#![allow(deprecated)]

use win_fluent::prelude::*;
use win_fluent::{
    diff_views, fluent_icon_glyph, icon, resolve_accessibility_tree, view_schema, IconToken,
    STANDARD_ICON_NAMES,
};

#[derive(Clone, Debug, PartialEq)]
enum Msg {
    Copy,
    Pick(String),
}

fn accepts_legacy_card_alias(token: &ServiceResultCardToken<Msg>) -> &ResultCardToken<Msg> {
    token
}

fn accepts_legacy_list_alias(token: &ServiceResultListToken<Msg>) -> &ResultListToken<Msg> {
    token
}

#[test]
fn deprecated_service_result_aliases_still_migrate_to_result_tokens() {
    let card = service_result_card(ResultItem::new("a", "Provider A", "translated")).into_view();
    let ViewToken::ResultCard(card_token) = card.token() else {
        panic!("expected ResultCard");
    };

    let list = service_result_list([ResultItem::new("a", "Provider A", "translated")]).into_view();
    let ViewToken::ResultList(list_token) = list.token() else {
        panic!("expected ResultList");
    };

    assert_eq!(accepts_legacy_card_alias(card_token).item.id, "a");
    assert_eq!(accepts_legacy_list_alias(list_token).items.len(), 1);

    let _builder_card: ServiceResultCardBuilder<Msg> =
        service_result_card(ResultItem::new("b", "Provider B", "ready"));
    let _builder_list: ServiceResultListBuilder<Msg> =
        service_result_list([ResultItem::new("b", "Provider B", "ready")]);
}

#[test]
fn standard_icons_have_public_fluent_glyph_contract() {
    for name in STANDARD_ICON_NAMES {
        assert!(
            fluent_icon_glyph(name).is_some(),
            "{name} should map to a Fluent glyph"
        );
        assert_eq!(
            IconToken::named(name).resolved_glyph(),
            fluent_icon_glyph(name)
        );
    }

    assert_eq!(icon::settings().resolved_glyph(), Some('\u{E713}'));
    assert_eq!(
        IconToken::with_glyph("custom", 'x').resolved_glyph(),
        Some('x')
    );
    assert_eq!(IconToken::named("unknown").resolved_glyph(), None);
}

#[test]
fn status_badge_exposes_text_count_and_dot_semantics() {
    let count = status_badge::<Msg>("ignored", ValidationSeverity::Info)
        .count(12)
        .into_view();
    let ViewToken::StatusBadge(count_token) = count.token() else {
        panic!("expected StatusBadge");
    };

    assert_eq!(count_token.kind, StatusBadgeKind::Count);
    assert_eq!(count_token.count, Some(12));
    assert_eq!(count_token.label, "12");
    assert_eq!(count_token.automation_name(), "12 Info notifications");

    let dot = status_badge::<Msg>("", ValidationSeverity::Error)
        .dot()
        .into_view();
    let ViewToken::StatusBadge(dot_token) = dot.token() else {
        panic!("expected StatusBadge");
    };

    assert_eq!(dot_token.kind, StatusBadgeKind::Dot);
    assert_eq!(dot_token.count, None);
    assert_eq!(
        resolve_accessibility_tree(&dot).name.as_deref(),
        Some("Error status")
    );

    let snapshot = view_schema(&count).snapshot();
    assert!(snapshot.contains("kind=count"));
    assert!(snapshot.contains("count=12"));
}

#[test]
fn settings_row_schema_and_a11y_include_content_and_trailing_contract() {
    let row = settings_row("OCR language")
        .description("Used by screenshot translation")
        .content(combo_box([ComboBoxItem::new("auto", "Auto")]).selected("auto"))
        .trailing([button("Reset").on_press(Msg::Copy)])
        .into_view();

    let schema = view_schema(&row).snapshot();
    assert!(schema.contains("SettingsRow"));
    assert!(schema.contains("has_content=true"));
    assert!(schema.contains("trailing=1"));
    assert!(schema.contains("ComboBox"));
    assert!(schema.contains("Button"));

    let a11y = resolve_accessibility_tree(&row);
    assert_eq!(a11y.name.as_deref(), Some("OCR language"));
    assert_eq!(
        a11y.description.as_deref(),
        Some("Used by screenshot translation")
    );
    assert_eq!(
        a11y.help_text.as_deref(),
        Some("SettingsRow:content=true,trailing=1")
    );
    assert_eq!(a11y.children.len(), 2);
}

#[test]
fn result_list_contract_is_distinct_from_generic_list_view() {
    let results: View<Msg> =
        result_list([ResultItem::new("a", "Provider A", "translated").expanded(false)])
            .virtualized(false)
            .collapse_transition_ms(120)
            .into_view();
    let generic = list_view([ListViewItem::new("a", text("History item"))])
        .selected("a")
        .on_select(Msg::Pick);

    let ViewToken::ResultList(result_token) = results.token() else {
        panic!("expected ResultList");
    };
    let ViewToken::ListView(generic_token) = generic.token() else {
        panic!("expected ListView");
    };

    assert_eq!(
        result_token.list_contract_kind(),
        ListContractKind::TranslationResultList
    );
    assert_eq!(
        generic_token.list_contract_kind(),
        ListContractKind::GenericListView
    );
    assert_eq!(
        resolve_accessibility_tree(&results).help_text.as_deref(),
        Some("ListContract:translation-result-list,virtualized=false,collapse_transition_ms=120")
    );

    let result_schema = view_schema(&results).snapshot();
    let generic_schema = view_schema(&generic).snapshot();
    assert!(result_schema.contains("list_contract=translation-result-list"));
    assert!(result_schema.contains("collapse_transition_ms=120"));
    assert!(generic_schema.contains("list_contract=generic-list-view"));
}

#[test]
fn control_template_is_a_public_custom_control_contract() {
    let template = control_template(
        "Button",
        "AccentRoundButtonTemplate",
        [text("Accent"), icon_button()],
    );

    let ViewToken::Custom(token) = template.token() else {
        panic!("expected Custom token");
    };

    assert_eq!(token.kind, CustomControlKind::ControlTemplate);
    assert_eq!(token.control, "AccentRoundButtonTemplate");
    assert_eq!(token.target_type.as_deref(), Some("Button"));
    assert_eq!(token.children.len(), 2);

    let schema = view_schema(&template).snapshot();
    assert!(schema.contains("kind=control-template"));
    assert!(schema.contains("target_type=\"Button\""));

    let a11y = resolve_accessibility_tree(&template);
    assert_eq!(
        a11y.help_text.as_deref(),
        Some("CustomControlKind:control-template,target_type=Button")
    );
}

#[test]
fn custom_control_kind_participates_in_virtual_tree_diff() {
    let before = custom_control::<Msg, _>("HostControl", [text("A")]);
    let after = control_template::<Msg, _>("HostControl", "HostTemplate", [text("A")]);

    let changes = diff_views(&before, &after);

    assert_eq!(changes.len(), 1);
    assert!(matches!(
        changes[0].kind,
        win_fluent::ViewChangeKind::Updated { .. }
    ));
}

#[test]
fn visual_state_snapshot_and_theme_coverage_are_public_contracts() {
    let visual_state = ControlState::default()
        .hovered(true)
        .focused(true)
        .selected(true)
        .visual_state_snapshot();
    assert_eq!(visual_state.common, CommonVisualState::PointerOver);
    assert_eq!(visual_state.focus, FocusVisualState::Focused);
    assert_eq!(visual_state.selection, SelectionVisualState::Selected);
    assert!(visual_state
        .automation_key()
        .contains("CommonStates:PointerOver"));

    let report = ThemeTokens::fluent_light().coverage_report();
    assert_eq!(report.color_tokens, 38);
    assert_eq!(report.control_metric_tokens, 13);
    assert!(report.summary().contains("categories=13"));
}

fn icon_button() -> View<Msg> {
    button("").icon(icon::settings()).on_press(Msg::Copy)
}

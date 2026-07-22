use win_fluent::a11y::A11yRole;
use win_fluent::prelude::*;
use win_fluent::resolve_accessibility_tree;
use win_fluent::view::TextWrapping;
use win_fluent::IconToken;

#[derive(Clone, Debug, PartialEq)]
enum Msg {
    Pressed(&'static str),
    Bool(bool),
    Text(String),
    Number(f32),
    Selected(String),
}

#[test]
fn command_accelerators_flow_through_page_dialog_and_command_bar_contracts() {
    let view = page("Command Contract")
        .command(
            command("Refresh")
                .id("refresh")
                .placement(CommandPlacement::Primary)
                .keyboard(KeyboardAccelerator::new("R").modifier("Ctrl"))
                .on_invoke(Msg::Pressed("refresh"))
                .build(),
        )
        .content(column(vec![
            command_bar(vec![
                primary_button("Save")
                    .id("save")
                    .on_press(Msg::Pressed("save")),
                button("Cancel")
                    .id("cancel")
                    .tooltip("Cancel changes")
                    .on_press(Msg::Pressed("cancel")),
            ])
            .id("editor-command-bar")
            .compact(true)
            .space_between()
            .into_view(),
            dialog("Apply changes?")
                .id("confirm-dialog")
                .primary(
                    command("Apply")
                        .id("apply")
                        .keyboard(KeyboardAccelerator::new("Enter").modifier("Ctrl"))
                        .on_invoke(Msg::Pressed("apply"))
                        .build(),
                )
                .secondary(
                    command("Dismiss")
                        .id("dismiss")
                        .placement(CommandPlacement::Secondary)
                        .enabled(false)
                        .build(),
                )
                .content(text(
                    "The command contract keeps automation names and accelerators.",
                ))
                .into_view(),
        ]))
        .into_view();

    let schema = view_schema(&view).snapshot();
    assert!(schema.contains("command:refresh=label=\"Refresh\",placement=Primary"));
    assert!(schema.contains("keyboard=Ctrl+R"));
    assert!(schema.contains("CommandBar"));
    assert!(schema.contains("compact=true"));
    assert!(schema.contains("primary=label=\"Apply\",placement=Primary"));
    assert!(schema.contains("keyboard=Ctrl+Enter"));
    assert!(schema.contains("secondary=label=\"Dismiss\",placement=Secondary"));
    assert!(schema.contains("enabled=false"));

    let a11y = resolve_accessibility_tree(&view);
    let nodes = collect_a11y_nodes(&a11y);
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::Button
            && node.name.as_deref() == Some("Refresh")
            && node.help_text.as_deref() == Some("placement=Primary, keyboard=Ctrl+R")
    }));
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::Button
            && node.name.as_deref() == Some("Apply")
            && node.help_text.as_deref() == Some("placement=Primary, keyboard=Ctrl+Enter")
    }));
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::Button
            && node.name.as_deref() == Some("Dismiss")
            && node.help_text.as_deref() == Some("placement=Secondary, disabled")
            && !node.focusable
    }));
}

#[test]
fn progress_text_editor_slider_combo_expander_and_busy_overlay_expose_fluent_state_contracts() {
    let invalid_combo = combo_box([ComboBoxItem::new("en", "English")])
        .id("invalid-selection")
        .selected("missing")
        .into_view();
    let ViewToken::ComboBox(invalid_combo_token) = invalid_combo.token() else {
        panic!("expected ComboBox");
    };
    assert_eq!(invalid_combo_token.selected, None);
    assert_eq!(invalid_combo_token.wrapping, TextWrapping::None);

    let default_flyout = flyout_button::<Msg>("Default").into_view();
    let ViewToken::FlyoutButton(default_flyout_token) = default_flyout.token() else {
        panic!("expected FlyoutButton");
    };
    assert_eq!(default_flyout_token.placement, FlyoutPlacement::Bottom);

    let title_flyout = flyout_button("DemoApp")
        .id("mode-title")
        .text_style(TextStyle::Subtitle)
        .font_size(22)
        .min_width(0)
        .min_height(0)
        .placement(FlyoutPlacement::Right)
        .items([FlyoutMenuItem::radio("quick", "Quick Translation", true)])
        .on_select(Msg::Selected);
    let ViewToken::FlyoutButton(title_flyout_token) = title_flyout.token() else {
        panic!("expected FlyoutButton");
    };
    assert_eq!(title_flyout_token.label, "DemoApp");
    assert_eq!(title_flyout_token.text_style, Some(TextStyle::Subtitle));
    assert_eq!(title_flyout_token.font_size, Some(22));
    assert_eq!(title_flyout_token.placement, FlyoutPlacement::Right);

    let view = column(vec![
        progress_bar::<Msg>()
            .id("download-progress")
            .label("Download progress")
            .value(150.0)
            .into_view(),
        progress_ring::<Msg>()
            .id("sync-ring")
            .active(false)
            .label("Background sync")
            .into_view(),
        busy_overlay(text("Document body"))
            .id("busy")
            .active(true)
            .blocks_input(true)
            .label("Syncing")
            .into_view(),
        text_editor("secret")
            .id("api-key")
            .placeholder("API key")
            .password()
            .read_only(true)
            .on_key(
                TextEditorKey::Enter,
                TextEditorKeyModifiers::control(),
                Msg::Text("submit".into()),
            )
            .into_view(),
        slider(42.0)
            .id("volume")
            .range(0.0, 100.0)
            .step(5.0)
            .focused(true)
            .on_change(Msg::Number),
        combo_box([
            ComboBoxItem::new("light", "Light"),
            ComboBoxItem::new("dark", "Dark"),
        ])
        .id("theme")
        .label("Theme")
        .selected("dark")
        .wrapping(TextWrapping::Word)
        .on_change(Msg::Selected),
        invalid_combo,
        expander("Advanced")
            .id("advanced")
            .expanded(false)
            .on_toggle(Msg::Bool)
            .content(text("Advanced options"))
            .into_view(),
    ])
    .into_view();

    let schema = view_schema(&view).snapshot();
    assert!(schema.contains("ProgressBar"));
    assert!(schema.contains("value=100.00"));
    assert!(schema.contains("ProgressRing"));
    assert!(schema.contains("BusyOverlay"));
    assert!(schema.contains("blocks_input=true"));
    assert!(schema.contains("TextEditor"));
    assert!(schema.contains("secure=true"));
    assert!(schema.contains("read_only=true"));
    assert!(schema.contains("key_bindings=Ctrl+Enter"));
    assert!(schema.contains("Slider"));
    assert!(schema.contains("preview_active=true"));
    assert!(schema.contains("ComboBox"));
    assert!(schema.contains("selected=\"dark\""));
    assert!(schema.contains("selected_label=\"Dark\""));
    assert!(schema.contains("wrapping=Word"));
    assert!(schema.contains("selected=none"));
    assert!(schema.contains("Expander"));
    assert!(schema.contains("motion=expand-collapse-reveal"));
    assert!(schema.contains("expanded=false"));

    let a11y = resolve_accessibility_tree(&view);
    let nodes = collect_a11y_nodes(&a11y);
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::ProgressBar
            && node.name.as_deref() == Some("Download progress")
            && node
                .help_text
                .as_deref()
                .is_some_and(|text| text.contains("value=100.00"))
    }));
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::ProgressBar
            && node.name.as_deref() == Some("Background sync")
            && node.help_text.as_deref() == Some("indeterminate, inactive")
    }));
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::Pane
            && node.name.as_deref() == Some("Syncing")
            && node.help_text.as_deref() == Some("active=true, blocks_input=true")
    }));
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::TextInput
            && node.name.as_deref() == Some("API key")
            && node.focusable
            && node.help_text.as_deref() == Some("secure, read-only")
    }));
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::Slider
            && node.name.as_deref() == Some("volume")
            && node
                .help_text
                .as_deref()
                .is_some_and(|text| text.contains("preview_active=true"))
    }));
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::ComboBox
            && node.name.as_deref() == Some("Theme")
            && node.help_text.as_deref() == Some("selected=Dark")
    }));
    assert!(nodes.iter().any(|node| {
        node.role == A11yRole::Group
            && node.name.as_deref() == Some("Advanced")
            && node.help_text.as_deref() == Some("collapsed")
    }));
}

#[test]
fn checkbox_layout_defaults_and_explicit_values_are_public() {
    let defaults = checkbox::<Msg>("Context", false).into_view();
    let ViewToken::CheckBox(default_token) = defaults.token() else {
        panic!("expected CheckBox");
    };
    assert_eq!(default_token.width, Length::Shrink);
    assert_eq!(default_token.wrapping, TextWrapping::Word);

    let explicit = checkbox::<Msg>("Context", false)
        .width(Length::Fill)
        .wrapping(TextWrapping::Word)
        .on_toggle(Msg::Bool);
    let ViewToken::CheckBox(explicit_token) = explicit.token() else {
        panic!("expected CheckBox");
    };
    assert_eq!(explicit_token.width, Length::Fill);
    assert_eq!(explicit_token.wrapping, TextWrapping::Word);
    let snapshot = view_schema(&explicit).snapshot();
    assert!(snapshot.contains("CheckBox"));
    assert!(snapshot.contains("width=Fill"));
    assert!(snapshot.contains("wrapping=Word"));
}

#[test]
fn button_exposes_trailing_icon_and_selected_accessibility_state() {
    let view = button("Application")
        .id("mode-menu")
        .trailing_icon(IconToken::with_glyph("chevron-down", '\u{E70D}'))
        .selected(true)
        .on_press(Msg::Pressed("mode-menu"));

    let ViewToken::Button(token) = view.token() else {
        panic!("expected Button");
    };
    assert_eq!(
        token.trailing_icon.as_ref().map(|icon| icon.name),
        Some("chevron-down")
    );
    assert!(token.state.selected);

    let schema = view_schema(&view).snapshot();
    assert!(schema.contains("trailing_icon=chevron-down"));

    let a11y = resolve_accessibility_tree(&view);
    assert_eq!(a11y.name.as_deref(), Some("Application"));
    assert_eq!(a11y.help_text.as_deref(), Some("selected"));
}

fn collect_a11y_nodes(root: &win_fluent::A11yNode) -> Vec<&win_fluent::A11yNode> {
    fn visit<'a>(node: &'a win_fluent::A11yNode, nodes: &mut Vec<&'a win_fluent::A11yNode>) {
        nodes.push(node);
        for child in &node.children {
            visit(child, nodes);
        }
    }

    let mut nodes = Vec::new();
    visit(root, &mut nodes);
    nodes
}

#![forbid(unsafe_code)]

use std::fmt::Write;

use win_fluent::a11y::{resolve_accessibility_tree, A11yNode};
use win_fluent::action::ActionKind;
use win_fluent::schema::{view_schema, ViewSchema};
use win_fluent::theme::ThemeTokens;
use win_fluent::view::{LayoutKind, View, ViewToken};

pub fn view_schema_tree<Message>(view: &View<Message>) -> ViewSchema {
    view_schema(view)
}

pub fn view_snapshot<Message>(view: &View<Message>) -> String {
    view_schema(view).snapshot()
}

pub fn layout_snapshot<Message>(view: &View<Message>) -> String {
    let mut output = String::new();
    write_layout(&mut output, view, 0);
    output
}

pub fn accessibility_tree<Message>(view: &View<Message>) -> A11yNode {
    resolve_accessibility_tree(view)
}

pub fn accessibility_snapshot<Message>(view: &View<Message>) -> String {
    let tree = accessibility_tree(view);
    let mut output = String::new();
    write_a11y(&mut output, &tree, 0);
    output
}

pub fn theme_snapshot(theme: &ThemeTokens) -> String {
    format!(
        "ResolvedTheme mode={:?} background=#{:02x}{:02x}{:02x} surface=#{:02x}{:02x}{:02x} surface_alt=#{:02x}{:02x}{:02x} text_primary=#{:02x}{:02x}{:02x} text_secondary=#{:02x}{:02x}{:02x} border=#{:02x}{:02x}{:02x} focus=#{:02x}{:02x}{:02x} accent=#{:02x}{:02x}{:02x} radius_control={} spacing_md={} density={:?}",
        theme.mode,
        theme.background.r,
        theme.background.g,
        theme.background.b,
        theme.surface.r,
        theme.surface.g,
        theme.surface.b,
        theme.surface_alt.r,
        theme.surface_alt.g,
        theme.surface_alt.b,
        theme.text_primary.r,
        theme.text_primary.g,
        theme.text_primary.b,
        theme.text_secondary.r,
        theme.text_secondary.g,
        theme.text_secondary.b,
        theme.border.r,
        theme.border.g,
        theme.border.b,
        theme.focus.r,
        theme.focus.g,
        theme.focus.b,
        theme.accent.base.r,
        theme.accent.base.g,
        theme.accent.base.b,
        theme.radius.control,
        theme.spacing.md,
        theme.density
    )
}

fn write_layout<Message>(output: &mut String, view: &View<Message>, indent: usize) {
    let pad = " ".repeat(indent);
    match view.token() {
        ViewToken::Layout(token) => {
            let label = match token.kind {
                LayoutKind::Column => "Column",
                LayoutKind::Row => "Row",
            };
            let _ = writeln!(
                output,
                "{pad}{label} id={:?} children={} padding={} spacing={} width={:?} height={:?} align={:?}",
                token.id,
                token.children.len(),
                token.padding,
                token.spacing,
                token.width,
                token.height,
                token.align
            );
            for child in &token.children {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::Page(token) => {
            if let Some(content) = &token.content {
                write_layout(output, content, indent);
            }
        }
        ViewToken::CommandBar(token) => {
            let _ = writeln!(
                output,
                "{pad}CommandBar id={:?} items={} compact={}",
                token.id,
                token.items.len(),
                token.compact
            );
            for child in &token.items {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::NavigationView(token) => {
            if let Some(content) = &token.content {
                write_layout(output, content, indent);
            }
        }
        ViewToken::Dialog(token) => {
            if let Some(content) = &token.content {
                write_layout(output, content, indent);
            }
        }
        ViewToken::Lazy(token) => write_layout(output, &token.content, indent),
        ViewToken::ScrollView(token) => {
            let _ = writeln!(
                output,
                "{pad}ScrollView id={:?} horizontal={:?} vertical={:?}",
                token.id, token.horizontal, token.vertical
            );
            if let Some(content) = &token.content {
                write_layout(output, content, indent + 2);
            }
        }
        ViewToken::SettingsRow(token) => {
            let _ = writeln!(
                output,
                "{pad}SettingsRow id={:?} trailing={}",
                token.id,
                token.trailing.len()
            );
            if let Some(content) = &token.content {
                write_layout(output, content, indent + 2);
            }
            for child in &token.trailing {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::Custom(token) => {
            let _ = writeln!(
                output,
                "{pad}Custom id={:?} control={:?} children={}",
                token.id,
                token.control,
                token.children.len()
            );
            for child in &token.children {
                write_layout(output, child, indent + 2);
            }
        }
        ViewToken::Text(_)
        | ViewToken::Button(_)
        | ViewToken::TextEditor(_)
        | ViewToken::ToggleSwitch(_)
        | ViewToken::ComboBox(_)
        | ViewToken::ServiceResultCard(_)
        | ViewToken::ServiceResultList(_) => {}
    }
}

fn write_a11y(output: &mut String, node: &A11yNode, indent: usize) {
    let pad = " ".repeat(indent);
    let _ = writeln!(
        output,
        "{pad}{:?} name={:?} focusable={}",
        node.role, node.name, node.focusable
    );
    for child in &node.children {
        write_a11y(output, child, indent + 2);
    }
}

pub fn assert_action_kind<Message>(action: &win_fluent::Action<Message>, expected: ActionKind) {
    assert_eq!(action.kind(), expected);
}

#[macro_export]
macro_rules! assert_view_snapshot {
    ($name:literal, $view:expr) => {{
        let snapshot = $crate::view_snapshot(&$view);
        assert!(
            !snapshot.trim().is_empty(),
            "view snapshot `{}` was empty",
            $name
        );
        snapshot
    }};
}

#[cfg(test)]
mod tests {
    use win_fluent::prelude::*;

    #[allow(dead_code)]
    #[derive(Clone)]
    enum Msg {
        Save,
        Changed(String),
    }

    #[test]
    fn snapshots_token_tree_with_schema_version() {
        let view = page("Settings")
            .content(column((
                settings_row("Mode")
                    .trailing((toggle_switch("Enabled", true).on_toggle(|_| Msg::Save),)),
                text_editor("value")
                    .focused(true)
                    .validation(ValidationState::error("Invalid value"))
                    .on_input(Msg::Changed),
            )))
            .into_view();

        let snapshot = crate::view_snapshot(&view);

        assert!(snapshot.contains("ViewSchema version=1"));
        assert!(snapshot.contains("Page title=\"Settings\""));
        assert!(snapshot.contains("ToggleSwitch"));
        assert!(snapshot.contains("TextEditor"));
        assert!(snapshot.contains("validation=Error"));
    }

    #[test]
    fn snapshots_layout_tokens_separately() {
        let view: View<Msg> = page("Layout")
            .content(column((text("A"), text("B"))).padding(24).spacing(12))
            .into_view();

        let snapshot = crate::layout_snapshot(&view);

        assert!(snapshot.contains("Column"));
        assert!(snapshot.contains("padding=24"));
        assert!(snapshot.contains("spacing=12"));
    }
}

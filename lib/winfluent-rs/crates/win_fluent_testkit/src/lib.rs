#![forbid(unsafe_code)]

use std::fmt::Write;

use win_fluent::a11y::{resolve_accessibility_tree, A11yNode};
use win_fluent::action::ActionKind;
use win_fluent::theme::ThemeTokens;
use win_fluent::view::{
    LayoutKind, ResultStatus, ServiceResultCardToken, ServiceResultListToken, View, ViewToken,
};

pub fn view_snapshot<Message>(view: &View<Message>) -> String {
    let mut output = String::new();
    write_view(&mut output, view, 0);
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
        "Theme(mode={:?}, background=#{:02x}{:02x}{:02x}, surface=#{:02x}{:02x}{:02x}, text=#{:02x}{:02x}{:02x}, accent=#{:02x}{:02x}{:02x})",
        theme.mode,
        theme.background.r,
        theme.background.g,
        theme.background.b,
        theme.surface.r,
        theme.surface.g,
        theme.surface.b,
        theme.text_primary.r,
        theme.text_primary.g,
        theme.text_primary.b,
        theme.accent.base.r,
        theme.accent.base.g,
        theme.accent.base.b
    )
}

fn write_view<Message>(output: &mut String, view: &View<Message>, indent: usize) {
    let pad = " ".repeat(indent);
    match view.token() {
        ViewToken::Page(token) => {
            let _ = writeln!(
                output,
                "{pad}Page title={:?} id={:?}",
                token.title, token.id
            );
            if let Some(content) = &token.content {
                write_view(output, content, indent + 2);
            }
        }
        ViewToken::Text(token) => {
            let _ = writeln!(
                output,
                "{pad}Text value={:?} style={:?} id={:?}",
                token.value, token.style, token.id
            );
        }
        ViewToken::Button(token) => {
            let _ = writeln!(
                output,
                "{pad}Button label={:?} kind={:?} icon={:?} enabled={} action={:?} id={:?}",
                token.label,
                token.kind,
                token.icon.as_ref().map(|icon| icon.name),
                token.enabled,
                token.action.kind(),
                token.id
            );
        }
        ViewToken::TextEditor(token) => {
            let _ = writeln!(
                output,
                "{pad}TextEditor id={:?} placeholder={:?} min_height={:?} read_only={} action={:?}",
                token.id,
                token.placeholder,
                token.min_height,
                token.read_only,
                token.action.kind()
            );
        }
        ViewToken::ToggleSwitch(token) => {
            let _ = writeln!(
                output,
                "{pad}ToggleSwitch label={:?} checked={} action={:?} id={:?}",
                token.label,
                token.checked,
                token.action.kind(),
                token.id
            );
        }
        ViewToken::ComboBox(token) => {
            let _ = writeln!(
                output,
                "{pad}ComboBox label={:?} selected={:?} items={} action={:?} id={:?}",
                token.label,
                token.selected,
                token.items.len(),
                token.action.kind(),
                token.id
            );
        }
        ViewToken::CommandBar(token) => {
            let _ = writeln!(
                output,
                "{pad}CommandBar compact={} items={} id={:?}",
                token.compact,
                token.items.len(),
                token.id
            );
            for child in &token.items {
                write_view(output, child, indent + 2);
            }
        }
        ViewToken::NavigationView(token) => {
            let _ = writeln!(
                output,
                "{pad}NavigationView selected={:?} items={} action={:?} id={:?}",
                token.selected,
                token.items.len(),
                token.action.kind(),
                token.id
            );
            if let Some(content) = &token.content {
                write_view(output, content, indent + 2);
            }
        }
        ViewToken::Dialog(token) => {
            let _ = writeln!(
                output,
                "{pad}Dialog title={:?} kind={:?} id={:?}",
                token.title, token.kind, token.id
            );
            if let Some(content) = &token.content {
                write_view(output, content, indent + 2);
            }
        }
        ViewToken::Layout(token) => {
            let label = match token.kind {
                LayoutKind::Column => "Column",
                LayoutKind::Row => "Row",
            };
            let _ = writeln!(
                output,
                "{pad}{label} children={} padding={} spacing={} width={:?} height={:?} align={:?} id={:?}",
                token.children.len(),
                token.padding,
                token.spacing,
                token.width,
                token.height,
                token.align,
                token.id
            );
            for child in &token.children {
                write_view(output, child, indent + 2);
            }
        }
        ViewToken::Lazy(token) => {
            let _ = writeln!(output, "{pad}Lazy key={:?} id={:?}", token.key, token.id);
            write_view(output, &token.content, indent + 2);
        }
        ViewToken::ScrollView(token) => {
            let _ = writeln!(
                output,
                "{pad}ScrollView horizontal={:?} vertical={:?} id={:?}",
                token.horizontal, token.vertical, token.id
            );
            if let Some(content) = &token.content {
                write_view(output, content, indent + 2);
            }
        }
        ViewToken::SettingsRow(token) => {
            let _ = writeln!(
                output,
                "{pad}SettingsRow title={:?} description={:?} kind={:?} trailing={} id={:?}",
                token.title,
                token.description,
                token.kind,
                token.trailing.len(),
                token.id
            );
            if let Some(content) = &token.content {
                write_view(output, content, indent + 2);
            }
            for child in &token.trailing {
                write_view(output, child, indent + 2);
            }
        }
        ViewToken::ServiceResultCard(token) => write_result_card(output, token, indent),
        ViewToken::ServiceResultList(token) => write_result_list(output, token, indent),
        ViewToken::Custom(token) => {
            let _ = writeln!(
                output,
                "{pad}Custom control={:?} children={} id={:?}",
                token.control,
                token.children.len(),
                token.id
            );
            for child in &token.children {
                write_view(output, child, indent + 2);
            }
        }
    }
}

fn write_result_card<Message>(
    output: &mut String,
    token: &ServiceResultCardToken<Message>,
    indent: usize,
) {
    let pad = " ".repeat(indent);
    let _ = writeln!(
        output,
        "{pad}ServiceResultCard id={:?} item={} status={} copy={:?} speak={:?}",
        token.id,
        token.item.id,
        status_label(token.item.status),
        token.copy_action.kind(),
        token.speak_action.kind()
    );
}

fn write_result_list<Message>(
    output: &mut String,
    token: &ServiceResultListToken<Message>,
    indent: usize,
) {
    let pad = " ".repeat(indent);
    let _ = writeln!(
        output,
        "{pad}ServiceResultList id={:?} items={} virtualized={} copy={:?} speak={:?}",
        token.id,
        token.items.len(),
        token.virtualized,
        token.copy_action.kind(),
        token.speak_action.kind()
    );
    for item in &token.items {
        let _ = writeln!(
            output,
            "{pad}  ResultItem id={} title={:?} status={}",
            item.id,
            item.title,
            status_label(item.status)
        );
    }
}

fn status_label(status: ResultStatus) -> &'static str {
    match status {
        ResultStatus::Loading => "loading",
        ResultStatus::Streaming => "streaming",
        ResultStatus::Ready => "ready",
        ResultStatus::Error => "error",
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
    fn snapshots_token_tree() {
        let view = page("Settings")
            .content(column((
                settings_row("Mode")
                    .trailing((toggle_switch("Enabled", true).on_toggle(|_| Msg::Save),)),
                text_editor("value").on_input(Msg::Changed),
            )))
            .into_view();

        let snapshot = crate::view_snapshot(&view);

        assert!(snapshot.contains("Page title=\"Settings\""));
        assert!(snapshot.contains("ToggleSwitch"));
        assert!(snapshot.contains("TextEditor"));
    }
}

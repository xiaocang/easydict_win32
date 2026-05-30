use std::fmt::Write;

use crate::command::CommandToken;
use crate::view::{LayoutKind, ResultCardToken, ResultItem, ResultListToken, View, ViewToken};

pub const VIEW_SCHEMA_VERSION: u16 = 1;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ViewSchema {
    pub version: u16,
    pub root: SchemaNode,
}

impl ViewSchema {
    pub fn snapshot(&self) -> String {
        let mut output = String::new();
        let _ = writeln!(output, "ViewSchema version={}", self.version);
        write_node(&mut output, &self.root, 0);
        output
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SchemaNode {
    pub kind: &'static str,
    pub id: Option<String>,
    pub properties: Vec<SchemaProperty>,
    pub children: Vec<SchemaNode>,
}

impl SchemaNode {
    fn new(kind: &'static str, id: Option<String>) -> Self {
        Self {
            kind,
            id,
            properties: Vec::new(),
            children: Vec::new(),
        }
    }

    fn property(mut self, name: impl Into<String>, value: impl Into<String>) -> Self {
        self.properties.push(SchemaProperty {
            name: name.into(),
            value: value.into(),
        });
        self
    }

    fn child(mut self, child: SchemaNode) -> Self {
        self.children.push(child);
        self
    }

    fn children(mut self, children: impl IntoIterator<Item = SchemaNode>) -> Self {
        self.children.extend(children);
        self
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SchemaProperty {
    pub name: String,
    pub value: String,
}

pub fn view_schema<Message>(view: &View<Message>) -> ViewSchema {
    ViewSchema {
        version: VIEW_SCHEMA_VERSION,
        root: schema_node(view),
    }
}

fn schema_node<Message>(view: &View<Message>) -> SchemaNode {
    match view.token() {
        ViewToken::Page(token) => {
            let mut node = SchemaNode::new("Page", token.id.clone())
                .property("title", quoted(&token.title))
                .property("commands", token.commands.len().to_string());
            for command in &token.commands {
                node.properties.push(SchemaProperty::command(
                    command_label(command),
                    command_summary(command),
                ));
            }
            if let Some(content) = &token.content {
                node = node.child(schema_node(content));
            }
            node
        }
        ViewToken::TitleBar(token) => SchemaNode::new("TitleBar", token.id.clone())
            .property("title", quoted(&token.title))
            .property("subtitle", optional_string(token.subtitle.as_deref()))
            .property("icon", optional_icon(token.icon.as_ref()))
            .property("commands", token.commands.len().to_string())
            .property("caption_controls", token.show_caption_controls.to_string())
            .property("minimize", format!("{:?}", token.minimize_action.kind()))
            .property(
                "toggle_maximize",
                format!("{:?}", token.toggle_maximize_action.kind()),
            )
            .property("close", format!("{:?}", token.close_action.kind()))
            .children(token.commands.iter().map(schema_node)),
        ViewToken::Text(token) => SchemaNode::new("Text", token.id.clone())
            .property("value", quoted(&token.value))
            .property("style", format!("{:?}", token.style))
            .property("selectable", token.selectable.to_string()),
        ViewToken::Button(token) => SchemaNode::new("Button", token.id.clone())
            .property("label", quoted(&token.label))
            .property("kind", format!("{:?}", token.kind))
            .property("icon", optional_icon(token.icon.as_ref()))
            .property("tooltip", optional_string(token.tooltip.as_deref()))
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::StatusBadge(token) => SchemaNode::new("StatusBadge", token.id.clone())
            .property("label", quoted(&token.label))
            .property("severity", format!("{:?}", token.severity))
            .property("icon", optional_icon(token.icon.as_ref())),
        ViewToken::Card(token) => {
            let mut node = SchemaNode::new("Card", token.id.clone())
                .property("title", quoted(&token.title))
                .property("description", optional_string(token.description.as_deref()))
                .property("icon", optional_icon(token.icon.as_ref()))
                .property("kind", format!("{:?}", token.kind))
                .property("trailing", token.trailing.len().to_string());
            if let Some(content) = &token.content {
                node = node.child(schema_node(content));
            }
            node.children.extend(token.trailing.iter().map(schema_node));
            node
        }
        ViewToken::Spacer(token) => SchemaNode::new("Spacer", token.id.clone())
            .property("width", format!("{:?}", token.width))
            .property("height", format!("{:?}", token.height)),
        ViewToken::TextEditor(token) => SchemaNode::new("TextEditor", token.id.clone())
            .property("text_len", token.text.chars().count().to_string())
            .property("placeholder", optional_string(token.placeholder.as_deref()))
            .property("min_height", optional_u16(token.min_height))
            .property("max_height", optional_u16(token.max_height))
            .property("text_style", format!("{:?}", token.text_style))
            .property("chrome", format!("{:?}", token.chrome))
            .property("read_only", token.read_only.to_string())
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::ToggleSwitch(token) => SchemaNode::new("ToggleSwitch", token.id.clone())
            .property("label", quoted(&token.label))
            .property("checked", token.checked.to_string())
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::ComboBox(token) => SchemaNode::new("ComboBox", token.id.clone())
            .property("label", optional_string(token.label.as_deref()))
            .property("selected", optional_string(token.selected.as_deref()))
            .property("items", combo_items(&token.items))
            .property("width", format!("{:?}", token.width))
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::CommandBar(token) => SchemaNode::new("CommandBar", token.id.clone())
            .property("compact", token.compact.to_string())
            .property("width", format!("{:?}", token.width))
            .property("align", format!("{:?}", token.align))
            .property("distribution", format!("{:?}", token.distribution))
            .children(token.items.iter().map(schema_node)),
        ViewToken::NavigationView(token) => SchemaNode::new("NavigationView", token.id.clone())
            .property("selected", optional_string(token.selected.as_deref()))
            .property("items", navigation_items(&token.items))
            .property("action", format!("{:?}", token.action.kind()))
            .children(token.content.iter().map(|content| schema_node(content))),
        ViewToken::Dialog(token) => SchemaNode::new("Dialog", token.id.clone())
            .property("title", quoted(&token.title))
            .property("kind", format!("{:?}", token.kind))
            .property("primary", optional_command(token.primary.as_ref()))
            .property("secondary", optional_command(token.secondary.as_ref()))
            .children(token.content.iter().map(|content| schema_node(content))),
        ViewToken::Layout(token) => {
            let kind = match token.kind {
                LayoutKind::Column => "Column",
                LayoutKind::Row => "Row",
            };
            SchemaNode::new(kind, token.id.clone())
                .property("children", token.children.len().to_string())
                .property("padding", token.padding.to_string())
                .property("spacing", token.spacing.to_string())
                .property("width", format!("{:?}", token.width))
                .property("height", format!("{:?}", token.height))
                .property("align", format!("{:?}", token.align))
                .property("distribution", format!("{:?}", token.distribution))
                .property("style", quoted(&token.style.summary()))
                .children(token.children.iter().map(schema_node))
        }
        ViewToken::Lazy(token) => SchemaNode::new("Lazy", token.id.clone())
            .property("key", quoted(&token.key))
            .child(schema_node(&token.content)),
        ViewToken::ScrollView(token) => SchemaNode::new("ScrollView", token.id.clone())
            .property("horizontal", format!("{:?}", token.horizontal))
            .property("vertical", format!("{:?}", token.vertical))
            .children(token.content.iter().map(|content| schema_node(content))),
        ViewToken::SettingsRow(token) => {
            let mut node = SchemaNode::new("SettingsRow", token.id.clone())
                .property("title", quoted(&token.title))
                .property("description", optional_string(token.description.as_deref()))
                .property("icon", optional_icon(token.icon.as_ref()))
                .property("kind", format!("{:?}", token.kind))
                .property("trailing", token.trailing.len().to_string());
            if let Some(content) = &token.content {
                node = node.child(schema_node(content));
            }
            node.children.extend(token.trailing.iter().map(schema_node));
            node
        }
        ViewToken::ResultCard(token) => result_card_schema(token),
        ViewToken::ResultList(token) => result_list_schema(token),
        ViewToken::Custom(token) => SchemaNode::new("Custom", token.id.clone())
            .property("control", quoted(&token.control))
            .property("children", token.children.len().to_string())
            .children(token.children.iter().map(schema_node)),
    }
}

impl SchemaProperty {
    fn command(name: String, value: String) -> Self {
        Self { name, value }
    }
}

fn write_node(output: &mut String, node: &SchemaNode, indent: usize) {
    let pad = " ".repeat(indent);
    let _ = write!(output, "{pad}{}", node.kind);
    for property in &node.properties {
        let _ = write!(output, " {}={}", property.name, property.value);
    }
    let _ = writeln!(output, " id={}", optional_string(node.id.as_deref()));
    for child in &node.children {
        write_node(output, child, indent + 2);
    }
}

fn result_card_schema<Message>(token: &ResultCardToken<Message>) -> SchemaNode {
    SchemaNode::new("ResultCard", token.id.clone())
        .property("item", result_item_summary(&token.item))
        .property("copy", format!("{:?}", token.copy_action.kind()))
        .property("speak", format!("{:?}", token.speak_action.kind()))
        .property("toggle", format!("{:?}", token.toggle_action.kind()))
        .property(
            "collapse_transition_ms",
            token.collapse_transition.duration_ms.to_string(),
        )
}

fn result_list_schema<Message>(token: &ResultListToken<Message>) -> SchemaNode {
    SchemaNode::new("ResultList", token.id.clone())
        .property("items", token.items.len().to_string())
        .property("virtualized", token.virtualized.to_string())
        .property("copy", format!("{:?}", token.copy_action.kind()))
        .property("speak", format!("{:?}", token.speak_action.kind()))
        .property("toggle", format!("{:?}", token.toggle_action.kind()))
        .property(
            "collapse_transition_ms",
            token.collapse_transition.duration_ms.to_string(),
        )
        .children(token.items.iter().map(|item| {
            SchemaNode::new("ResultItem", Some(item.id.clone()))
                .property("title", quoted(&item.title))
                .property("body_len", item.body.chars().count().to_string())
                .property("icon", optional_icon(item.icon.as_ref()))
                .property("metadata", optional_string(item.metadata.as_deref()))
                .property(
                    "pending_hint",
                    optional_string(item.pending_hint.as_deref()),
                )
                .property("expanded", item.expanded.to_string())
                .property("toggleable", item.toggleable.to_string())
                .property("dimmed", item.dimmed.to_string())
                .property("status", format!("{:?}", item.status))
        }))
}

fn result_item_summary(item: &ResultItem) -> String {
    format!(
        "id={},title={},status={:?},icon={},metadata={},pending_hint={},expanded={},toggleable={},dimmed={},body_len={}",
        item.id,
        quoted(&item.title),
        item.status,
        optional_icon(item.icon.as_ref()),
        optional_string(item.metadata.as_deref()),
        optional_string(item.pending_hint.as_deref()),
        item.expanded,
        item.toggleable,
        item.dimmed,
        item.body.chars().count()
    )
}

fn command_summary<Message>(command: &CommandToken<Message>) -> String {
    format!(
        "label={},placement={:?},icon={},keyboard={},enabled={},action={:?}",
        quoted(&command.label),
        command.placement,
        optional_icon(command.icon.as_ref()),
        optional_keyboard(command.keyboard.as_ref()),
        command.enabled,
        command.action.kind()
    )
}

fn command_label<Message>(command: &CommandToken<Message>) -> String {
    match &command.id {
        Some(id) => format!("command:{id}"),
        None => format!("command:{}", command.label),
    }
}

fn optional_command<Message>(command: Option<&CommandToken<Message>>) -> String {
    command
        .map(command_summary)
        .unwrap_or_else(|| "none".to_string())
}

fn optional_keyboard(keyboard: Option<&crate::command::KeyboardAccelerator>) -> String {
    match keyboard {
        Some(keyboard) if keyboard.modifiers.is_empty() => keyboard.key.clone(),
        Some(keyboard) => format!("{}+{}", keyboard.modifiers.join("+"), keyboard.key),
        None => "none".to_string(),
    }
}

fn combo_items(items: &[crate::view::ComboBoxItem]) -> String {
    items
        .iter()
        .map(|item| format!("{}:{}", item.id, quoted(&item.label)))
        .collect::<Vec<_>>()
        .join(",")
}

fn navigation_items(items: &[crate::view::NavigationItem]) -> String {
    items
        .iter()
        .map(|item| format!("{}:{}", item.id, quoted(&item.label)))
        .collect::<Vec<_>>()
        .join(",")
}

fn optional_icon(icon: Option<&crate::icon::IconToken>) -> String {
    icon.map(|icon| icon.name.to_string())
        .unwrap_or_else(|| "none".to_string())
}

fn optional_u16(value: Option<u16>) -> String {
    value
        .map(|value| value.to_string())
        .unwrap_or_else(|| "none".to_string())
}

fn optional_string(value: Option<&str>) -> String {
    value.map(quoted).unwrap_or_else(|| "none".to_string())
}

fn quoted(value: &str) -> String {
    format!("{value:?}")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::view::{button, column, page, text_editor, IntoView};

    #[allow(dead_code)]
    #[derive(Clone)]
    enum Msg {
        Input(String),
        Run,
    }

    #[test]
    fn emits_versioned_schema_snapshot() {
        let view = page("Demo")
            .content(column((
                text_editor("")
                    .id("input")
                    .validation(crate::state::ValidationState::warning("Required"))
                    .on_input(Msg::Input),
                button("Run").focused(true).on_press(Msg::Run),
            )))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.starts_with("ViewSchema version=1"));
        assert!(snapshot.contains("TextEditor"));
        assert!(snapshot.contains("validation=Warning"));
        assert!(snapshot.contains("focused=true"));
    }
}

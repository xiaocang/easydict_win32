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
        ViewToken::Text(token) => {
            let mut node = SchemaNode::new("Text", token.id.clone())
                .property("value", quoted(&token.value))
                .property("style", format!("{:?}", token.style))
                .property("selectable", token.selectable.to_string());
            if let Some(width) = token.width {
                node = node.property("width", optional_length(Some(width)));
            }
            if let Some(height) = token.height {
                node = node.property("height", optional_length(Some(height)));
            }
            node
        }
        ViewToken::Button(token) => SchemaNode::new("Button", token.id.clone())
            .property("label", quoted(&token.label))
            .property("kind", format!("{:?}", token.kind))
            .property("icon", optional_icon(token.icon.as_ref()))
            .property("tooltip", optional_string(token.tooltip.as_deref()))
            .property("width", optional_length(token.width))
            .property("height", optional_length(token.height))
            .property("text_style", optional_text_style(token.text_style))
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::FlyoutButton(token) => SchemaNode::new("FlyoutButton", token.id.clone())
            .property("label", quoted(&token.label))
            .property("icon", optional_icon(token.icon.as_ref()))
            .property("tooltip", optional_string(token.tooltip.as_deref()))
            .property("selected", optional_string(token.selected.as_deref()))
            .property("items", flyout_items(&token.items))
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::StatusBadge(token) => SchemaNode::new("StatusBadge", token.id.clone())
            .property("label", quoted(&token.label))
            .property("severity", format!("{:?}", token.severity))
            .property("icon", optional_icon(token.icon.as_ref())),
        ViewToken::ProgressRing(token) => SchemaNode::new("ProgressRing", token.id.clone())
            .property("active", token.active.to_string())
            .property("size", token.size.to_string())
            .property("label", optional_string(token.label.as_deref())),
        ViewToken::BusyOverlay(token) => SchemaNode::new("BusyOverlay", token.id.clone())
            .property("active", token.active.to_string())
            .property("opacity", format!("{:.2}", token.opacity))
            .property("fade_transition_ms", token.fade_transition_ms.to_string())
            .property("blocks_input", token.blocks_input.to_string())
            .property("label", optional_string(token.label.as_deref()))
            .child(schema_node(&token.content)),
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
            .property("action", format!("{:?}", token.action.kind()))
            .property(
                "key_bindings",
                text_editor_key_bindings(&token.key_bindings),
            ),
        ViewToken::ToggleSwitch(token) => SchemaNode::new("ToggleSwitch", token.id.clone())
            .property("label", quoted(&token.label))
            .property("checked", token.checked.to_string())
            .property("header", optional_string(token.header.as_deref()))
            .property(
                "width",
                token
                    .width
                    .map(|width| format!("{width:?}"))
                    .unwrap_or_else(|| {
                        toggle_switch_evidence_width(token.header.as_deref(), &token.label)
                    }),
            )
            .property(
                "height",
                token
                    .height
                    .map(|height| format!("{height:?}"))
                    .unwrap_or_else(|| "Fixed(32)".to_string()),
            )
            .property(
                "labeled_height",
                toggle_switch_labeled_evidence_height(token.header.as_deref()),
            )
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::CheckBox(token) => SchemaNode::new("CheckBox", token.id.clone())
            .property("label", quoted(&token.label))
            .property("checked", token.checked.to_string())
            .property("label_italic", token.label_italic.to_string())
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::Slider(token) => SchemaNode::new("Slider", token.id.clone())
            .property("value", format!("{:.2}", token.value))
            .property("min", format!("{:.2}", token.min))
            .property("max", format!("{:.2}", token.max))
            .property("step", format!("{:.2}", token.step))
            .property("width", format!("{:?}", token.width))
            .property("height", "Fixed(32)")
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::ComboBox(token) => SchemaNode::new("ComboBox", token.id.clone())
            .property("label", optional_string(token.label.as_deref()))
            .property("selected", optional_string(token.selected.as_deref()))
            .property("items", combo_items(&token.items))
            .property("width", format!("{:?}", token.width))
            .property(
                "labeled_width",
                combo_box_labeled_evidence_width(token.label.as_deref(), token.width),
            )
            .property("height", "Fixed(32)")
            .property(
                "labeled_height",
                combo_box_labeled_evidence_height(token.label.as_deref()),
            )
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
                .property("max_width", optional_u16(token.max_width))
                .property("center_x", token.center_x.to_string())
                .property("margin", format!("{:?}", token.margin))
                .property("align", format!("{:?}", token.align))
                .property("distribution", format!("{:?}", token.distribution))
                .property("style", quoted(&token.style.summary()))
                .children(token.children.iter().map(schema_node))
        }
        ViewToken::Wrap(token) => SchemaNode::new("Wrap", token.id.clone())
            .property("children", token.children.len().to_string())
            .property("max_columns", token.max_columns.to_string())
            .property("spacing", token.spacing.to_string())
            .property("run_spacing", token.run_spacing.to_string())
            .children(token.children.iter().map(schema_node)),
        ViewToken::Overlay(token) => {
            let layers = token
                .layers
                .iter()
                .map(|layer| {
                    format!(
                        "{:?}/{:?}/scrim={:?}/block={}",
                        layer.align_x, layer.align_y, layer.scrim, layer.blocks_input
                    )
                })
                .collect::<Vec<_>>()
                .join(",");
            let children = std::iter::once(token.base.as_ref())
                .chain(token.layers.iter().map(|layer| layer.content.as_ref()));
            SchemaNode::new("Overlay", token.id.clone())
                .property("layers", token.layers.len().to_string())
                .property("layout", quoted(&layers))
                .children(children.map(schema_node))
        }
        ViewToken::AdaptiveSwitch(token) => SchemaNode::new("AdaptiveSwitch", token.id.clone())
            .property("breakpoint_width", token.breakpoint_width.to_string())
            .child(schema_node(&token.wide))
            .child(schema_node(&token.narrow)),
        ViewToken::Lazy(token) => SchemaNode::new("Lazy", token.id.clone())
            .property("key", quoted(&token.key))
            .child(schema_node(&token.content)),
        ViewToken::ScrollView(token) => SchemaNode::new("ScrollView", token.id.clone())
            .property("horizontal", format!("{:?}", token.horizontal))
            .property("vertical", format!("{:?}", token.vertical))
            .property("scrollbars_visible", token.scrollbars_visible.to_string())
            .children(token.content.iter().map(|content| schema_node(content))),
        ViewToken::Expander(token) => {
            let mut node = SchemaNode::new("Expander", token.id.clone())
                .property("title", quoted(&token.title))
                .property("description", optional_string(token.description.as_deref()))
                .property("icon", optional_icon(token.icon.as_ref()))
                .property("expanded", token.expanded.to_string())
                .property("action", format!("{:?}", token.action.kind()))
                .property("trailing", token.trailing.len().to_string());
            if token.expanded {
                if let Some(content) = &token.content {
                    node = node.child(schema_node(content));
                }
            }
            node.children.extend(token.trailing.iter().map(schema_node));
            node
        }
        ViewToken::SettingsRow(token) => {
            let mut node = SchemaNode::new("SettingsRow", token.id.clone())
                .property("title", quoted(&token.title))
                .property("description", optional_string(token.description.as_deref()))
                .property("icon", optional_icon(token.icon.as_ref()))
                .property("kind", format!("{:?}", token.kind))
                .property("trailing", token.trailing.len().to_string());
            if let Some(id) = &token.title_id {
                node = node.child(settings_row_text_node(id, &token.title, "Subtitle"));
            }
            if let (Some(id), Some(description)) = (&token.description_id, &token.description) {
                node = node.child(settings_row_text_node(id, description, "Caption"));
            }
            if let Some(content) = &token.content {
                node = node.child(schema_node(content));
            }
            node.children.extend(token.trailing.iter().map(schema_node));
            node
        }
        ViewToken::ResultCard(token) => result_card_schema(token),
        ViewToken::ResultList(token) => result_list_schema(token),
        ViewToken::PointerRegion(token) => SchemaNode::new("PointerRegion", token.id.clone())
            .property("width", format!("{:?}", token.width))
            .property("height", format!("{:?}", token.height))
            .property("move", format!("{:?}", token.move_action.kind()))
            .property("left_down", format!("{:?}", token.left_down_action.kind()))
            .property("left_up", format!("{:?}", token.left_up_action.kind()))
            .property(
                "double_click",
                format!("{:?}", token.double_click_action.kind()),
            )
            .property(
                "right_down",
                format!("{:?}", token.right_down_action.kind()),
            )
            .property("wheel", format!("{:?}", token.wheel_action.kind()))
            .property("escape", format!("{:?}", token.escape_action.kind()))
            .child(schema_node(&token.content)),
        ViewToken::CaptureOverlay(token) => SchemaNode::new("CaptureOverlay", token.id.clone())
            .property("phase", quoted(token.phase.as_str()))
            .property("detection_depth", token.detection_depth.to_string())
            .property("dragging", token.dragging.to_string())
            .property("detected_rect", optional_capture_rect(token.detected_rect))
            .property(
                "selection_rect",
                optional_capture_rect(token.selection_rect),
            )
            .property("handles_visible", token.handles_visible.to_string())
            .property("magnifier_visible", token.magnifier_visible.to_string()),
        ViewToken::Image(token) => SchemaNode::new("Image", token.id.clone())
            .property("bgra_path", quoted(&token.bgra_path))
            .property("pixel_width", token.pixel_width.to_string())
            .property("pixel_height", token.pixel_height.to_string())
            .property("width", format!("{:?}", token.width))
            .property("height", format!("{:?}", token.height)),
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
        .property("replace", format!("{:?}", token.replace_action.kind()))
        .property("retry", format!("{:?}", token.retry_action.kind()))
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
        .property("replace", format!("{:?}", token.replace_action.kind()))
        .property("retry", format!("{:?}", token.retry_action.kind()))
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
                .property("header_state", item.header_state.to_string())
                .property("actions_visible", item.actions_visible.to_string())
        }))
}

fn result_item_summary(item: &ResultItem) -> String {
    format!(
        "id={},title={},status={:?},icon={},metadata={},pending_hint={},expanded={},toggleable={},dimmed={},header_state={},actions_visible={},body_len={}",
        item.id,
        quoted(&item.title),
        item.status,
        optional_icon(item.icon.as_ref()),
        optional_string(item.metadata.as_deref()),
        optional_string(item.pending_hint.as_deref()),
        item.expanded,
        item.toggleable,
        item.dimmed,
        item.header_state,
        item.actions_visible,
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

fn flyout_items(items: &[crate::view::FlyoutMenuItem]) -> String {
    items
        .iter()
        .map(|item| {
            format!(
                "{}:{}:{:?}:checked={}:enabled={}",
                item.id,
                quoted(&item.label),
                item.kind,
                item.checked,
                item.enabled
            )
        })
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

fn text_editor_key_bindings<Message>(
    bindings: &[crate::view::TextEditorKeyBinding<Message>],
) -> String {
    if bindings.is_empty() {
        return "none".to_string();
    }

    bindings
        .iter()
        .map(|binding| {
            let mut parts = Vec::new();
            if binding.modifiers.control {
                parts.push("Ctrl");
            }
            if binding.modifiers.alt {
                parts.push("Alt");
            }
            if binding.modifiers.shift {
                parts.push("Shift");
            }
            if binding.modifiers.logo {
                parts.push("Logo");
            }
            parts.push(match binding.key {
                crate::view::TextEditorKey::Enter => "Enter",
                crate::view::TextEditorKey::Tab => "Tab",
                crate::view::TextEditorKey::Escape => "Escape",
                crate::view::TextEditorKey::ArrowUp => "ArrowUp",
                crate::view::TextEditorKey::ArrowDown => "ArrowDown",
            });
            parts.join("+")
        })
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

fn optional_length(value: Option<crate::view::Length>) -> String {
    value
        .map(|value| format!("{value:?}"))
        .unwrap_or_else(|| "none".to_string())
}

fn optional_text_style(value: Option<crate::view::TextStyle>) -> String {
    value
        .map(|value| format!("{value:?}"))
        .unwrap_or_else(|| "none".to_string())
}

fn combo_box_labeled_evidence_height(label: Option<&str>) -> &'static str {
    if label.is_some_and(|value| !value.trim().is_empty()) {
        "Fixed(64)"
    } else {
        "none"
    }
}

fn combo_box_labeled_evidence_width(label: Option<&str>, width: crate::view::Length) -> String {
    if !label.is_some_and(|value| !value.trim().is_empty()) {
        return "none".to_string();
    }

    match width {
        crate::view::Length::Fixed(value) => format!("Fixed({})", value.saturating_add(8)),
        _ => "none".to_string(),
    }
}

fn toggle_switch_labeled_evidence_height(header: Option<&str>) -> &'static str {
    if header.is_some_and(|value| !value.trim().is_empty()) {
        "Fixed(63)"
    } else {
        "none"
    }
}

fn toggle_switch_evidence_width(header: Option<&str>, label: &str) -> String {
    let header_width = header
        .map(|value| estimated_text_width_dips(value, 14.0))
        .unwrap_or(0.0);
    let content_width = 50.0 + estimated_text_width_dips(label, 14.0);
    format!("Fixed({:.0})", header_width.max(content_width).ceil())
}

fn estimated_text_width_dips(value: &str, font_size: f32) -> f32 {
    value
        .chars()
        .map(|ch| {
            if ch.is_whitespace() {
                font_size * 0.32
            } else if is_wide_text_char(ch) {
                font_size
            } else if ch.is_ascii_punctuation() {
                font_size * 0.36
            } else {
                font_size * 0.52
            }
        })
        .sum()
}

fn is_wide_text_char(ch: char) -> bool {
    matches!(
        ch as u32,
        0x1100..=0x11FF
            | 0x2E80..=0xA4CF
            | 0xAC00..=0xD7AF
            | 0xF900..=0xFAFF
            | 0xFE10..=0xFE1F
            | 0xFE30..=0xFE6F
            | 0xFF00..=0xFFEF
            | 0x20000..=0x3FFFD
    )
}

fn optional_capture_rect(value: Option<crate::view::CaptureOverlayRect>) -> String {
    value
        .map(|rect| {
            format!(
                "({},{} {}x{})",
                rect.left, rect.top, rect.width, rect.height
            )
        })
        .unwrap_or_else(|| "none".to_string())
}

fn optional_string(value: Option<&str>) -> String {
    value.map(quoted).unwrap_or_else(|| "none".to_string())
}

fn settings_row_text_node(id: &str, value: &str, style: &str) -> SchemaNode {
    SchemaNode::new("Text", Some(id.to_string()))
        .property("value", quoted(value))
        .property("style", style.to_string())
        .property("selectable", false.to_string())
}

fn quoted(value: &str) -> String {
    format!("{value:?}")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::view::{button, column, page, settings_row, text_editor, IntoView};

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

    #[test]
    fn settings_row_title_and_description_ids_are_schema_text_nodes() {
        let view = page::<Msg>("Settings")
            .content(column((settings_row("TTS Reading Speed (0.5x - 3.0x)")
                .id("settings.tts_speed")
                .title_id("TtsSpeedLabelText")
                .description("Adjust speech rate")
                .description_id("TtsSpeedDescriptionText"),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("SettingsRow title=\"TTS Reading Speed (0.5x - 3.0x)\""));
        assert!(snapshot.contains(
            "Text value=\"TTS Reading Speed (0.5x - 3.0x)\" style=Subtitle selectable=false id=\"TtsSpeedLabelText\""
        ));
        assert!(snapshot.contains(
            "Text value=\"Adjust speech rate\" style=Caption selectable=false id=\"TtsSpeedDescriptionText\""
        ));
    }
}

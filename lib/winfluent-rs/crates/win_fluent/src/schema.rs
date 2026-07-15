use std::fmt::Write;

use crate::command::CommandToken;
use crate::view::{
    Alignment, LayoutKind, ResultCardToken, ResultItem, ResultListToken, View, ViewToken,
};

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

#[cfg(feature = "parity-diagnostics")]
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DiagnosticViewSchema {
    pub version: u16,
    pub root: DiagnosticNode,
    pub warnings: Vec<String>,
}
#[cfg(feature = "parity-diagnostics")]
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DiagnosticNode {
    pub path: crate::diff::ViewPath,
    pub kind: String,
    pub id: Option<String>,
    pub properties: Vec<SchemaProperty>,
    pub style_classes: Vec<String>,
    pub provenance: crate::provenance::ViewProvenance,
    pub children: Vec<DiagnosticNode>,
}

#[cfg(feature = "parity-diagnostics")]
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DiagnosticViewDiff {
    pub changes: Vec<DiagnosticChange>,
}

#[cfg(feature = "parity-diagnostics")]
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DiagnosticChange {
    pub path: crate::diff::ViewPath,
    pub property: Option<String>,
    pub before: Option<String>,
    pub after: Option<String>,
    pub before_source: Option<crate::provenance::SourceLocation>,
    pub after_source: Option<crate::provenance::SourceLocation>,
}

#[cfg(feature = "parity-diagnostics")]
pub fn diagnostic_view_schema<Message>(view: &View<Message>) -> DiagnosticViewSchema {
    let root = diagnostic_node(view, crate::diff::ViewPath::root());
    let mut warnings = Vec::new();
    collect_duplicate_id_warnings(&root, &mut warnings);
    DiagnosticViewSchema {
        version: VIEW_SCHEMA_VERSION,
        root,
        warnings,
    }
}

#[cfg(feature = "parity-diagnostics")]
pub fn diagnostic_diff_views<Message>(
    before: &View<Message>,
    after: &View<Message>,
) -> DiagnosticViewDiff {
    let before_schema = diagnostic_view_schema(before);
    let after_schema = diagnostic_view_schema(after);
    let mut changes = Vec::new();
    diff_diagnostic_nodes(&mut changes, &before_schema.root, &after_schema.root);
    DiagnosticViewDiff { changes }
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
    let mut node = schema_node_inner(view);
    if let Some(tooltip) = view.tooltip_text() {
        node = node.property("tooltip", quoted(tooltip));
        node = node.property(
            "tooltip_placement",
            format!("{:?}", view.tooltip_placement()),
        );
    }
    node
}

fn schema_node_inner<Message>(view: &View<Message>) -> SchemaNode {
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
            .property("drag", format!("{:?}", token.drag_action.kind()))
            .children(token.commands.iter().map(schema_node)),
        ViewToken::Text(token) => {
            let mut node = SchemaNode::new("Text", token.id.clone())
                .property("value", quoted(&token.value))
                .property("style", format!("{:?}", token.style))
                .property("font_size", optional_u16(token.font_size))
                .property("wrapping", format!("{:?}", token.wrapping))
                .property("selectable", token.selectable.to_string());
            if let Some(width) = token.width {
                node = node.property("width", optional_length(Some(width)));
            }
            if let Some(height) = token.height {
                node = node.property("height", optional_length(Some(height)));
            }
            if !token.margin.is_zero() {
                node = node.property("margin", format!("{:?}", token.margin));
            }
            if token.align_x != Alignment::Start {
                node = node.property("align_x", format!("{:?}", token.align_x));
            }
            if token.align_y != Alignment::Start {
                node = node.property("align_y", format!("{:?}", token.align_y));
            }
            node
        }
        ViewToken::RichText(token) => {
            let runs = token
                .runs
                .iter()
                .map(|run| match run.kind {
                    crate::view::TextRunKind::Link => {
                        format!("link({}->{})", run.text, run.href.as_deref().unwrap_or(""))
                    }
                    crate::view::TextRunKind::Bold => format!("bold({})", run.text),
                    crate::view::TextRunKind::Italic => format!("italic({})", run.text),
                    crate::view::TextRunKind::Plain => run.text.clone(),
                })
                .collect::<Vec<_>>()
                .join("|");
            SchemaNode::new("RichText", token.id.clone())
                .property("style", format!("{:?}", token.style))
                .property("wrapping", format!("{:?}", token.wrapping))
                .property("runs", quoted(&runs))
                .property("on_link", format!("{:?}", token.link_action.kind()))
        }
        ViewToken::Button(token) => {
            let mut node = SchemaNode::new("Button", token.id.clone())
                .property("label", quoted(&token.label))
                .property("kind", format!("{:?}", token.kind))
                .property("icon", optional_icon(token.icon.as_ref()))
                .property("tooltip", optional_string(token.tooltip.as_deref()))
                .property("width", optional_length(token.width))
                .property("height", optional_length(token.height))
                .property("padding", optional_edges(token.padding))
                .property("text_style", optional_text_style(token.text_style))
                .property("font_size", optional_u16(token.font_size))
                .property("state", token.state.to_string())
                .property("action", format!("{:?}", token.action.kind()));
            if !token.margin.is_zero() {
                node = node.property("margin", format!("{:?}", token.margin));
            }
            node
        }
        ViewToken::ToggleButton(token) => SchemaNode::new("ToggleButton", token.id.clone())
            .property("label", quoted(&token.label))
            .property("icon", optional_icon(token.icon.as_ref()))
            .property("pressed", token.pressed.to_string())
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::SplitButton(token) => SchemaNode::new("SplitButton", token.id.clone())
            .property("label", quoted(&token.label))
            .property("icon", optional_icon(token.icon.as_ref()))
            .property("items", flyout_items(&token.items))
            .property("open", token.open.to_string())
            .property("state", token.state.to_string())
            .property("primary", format!("{:?}", token.primary_action.kind()))
            .property("select", format!("{:?}", token.select_action.kind())),
        ViewToken::FlyoutButton(token) => SchemaNode::new("FlyoutButton", token.id.clone())
            .property("label", quoted(&token.label))
            .property("icon", optional_icon(token.icon.as_ref()))
            .property("tooltip", optional_string(token.tooltip.as_deref()))
            .property("selected", optional_string(token.selected.as_deref()))
            .property("items", flyout_items(&token.items))
            .property("min_width", optional_u16(token.min_width))
            .property("min_height", optional_u16(token.min_height))
            .property("padding", optional_edges(token.padding))
            .property("border_width", optional_u16(token.border_width))
            .property("radius", optional_u16(token.radius))
            .property("align_y", format!("{:?}", token.align_y))
            .property("text_style", optional_text_style(token.text_style))
            .property("font_size", optional_u16(token.font_size))
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::StatusBadge(token) => SchemaNode::new("StatusBadge", token.id.clone())
            .property("label", quoted(&token.label))
            .property("kind", token.kind.as_str())
            .property(
                "count",
                token
                    .count
                    .map(|value| value.to_string())
                    .unwrap_or_else(|| "none".to_string()),
            )
            .property("severity", format!("{:?}", token.severity))
            .property("icon", optional_icon(token.icon.as_ref())),
        ViewToken::InfoBar(token) => SchemaNode::new("InfoBar", token.id.clone())
            .property("title", quoted(&token.title))
            .property("message", quoted(&token.message))
            .property("severity", format!("{:?}", token.severity))
            .property("icon", optional_icon(token.icon.as_ref())),
        ViewToken::ProgressRing(token) => SchemaNode::new("ProgressRing", token.id.clone())
            .property("active", token.active.to_string())
            .property("size", token.size.to_string())
            .property("label", optional_string(token.label.as_deref())),
        ViewToken::ProgressBar(token) => SchemaNode::new("ProgressBar", token.id.clone())
            .property("active", token.active.to_string())
            .property(
                "value",
                token
                    .normalized_value()
                    .map(|value| format!("{value:.2}"))
                    .unwrap_or_else(|| "indeterminate".to_string()),
            )
            .property("width", format!("{:?}", token.width))
            .property("height", format!("Fixed({})", token.height))
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
                .property("padding", optional_edges(token.padding))
                .property("content_spacing", token.content_spacing.to_string())
                .property("margin", format!("{:?}", token.margin))
                .property("max_height", optional_u16(token.max_height))
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
        ViewToken::TextEditor(token) => {
            let mut node = SchemaNode::new("TextEditor", token.id.clone())
                .property("text_len", token.text.chars().count().to_string())
                .property("placeholder", optional_string(token.placeholder.as_deref()))
                .property(
                    "width",
                    token
                        .width
                        .map(|width| format!("{width:?}"))
                        .unwrap_or_else(|| "auto".to_string()),
                )
                .property("height", text_editor_evidence_height(token))
                .property("min_height", optional_u16(token.min_height))
                .property("max_height", optional_u16(token.max_height))
                .property("padding", optional_edges(token.padding))
                .property("text_style", format!("{:?}", token.text_style))
                .property("chrome", format!("{:?}", token.chrome))
                .property("secure", token.secure.to_string())
                .property("read_only", token.read_only.to_string())
                .property("state", token.state.to_string())
                .property("action", format!("{:?}", token.action.kind()))
                .property(
                    "key_bindings",
                    text_editor_key_bindings(&token.key_bindings),
                );
            if let Some(icon) = &token.trailing_icon {
                node = node.child(
                    SchemaNode::new("Button", Some(icon.id.clone()))
                        .property("label", quoted(""))
                        .property("kind", "Icon")
                        .property("icon", optional_icon(Some(&icon.icon)))
                        .property("tooltip", optional_string(Some(&icon.label)))
                        .property("width", format!("Fixed({})", icon.width))
                        .property("height", format!("Fixed({})", icon.height))
                        .property("text_style", "none")
                        .property("state", "enabled=true,hovered=false,pressed=false,focused=false,selected=false,validation=none")
                        .property("action", "none"),
                );
            }
            node
        }
        ViewToken::ToggleSwitch(token) => {
            let mut node = SchemaNode::new("ToggleSwitch", token.id.clone())
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
                .property("action", format!("{:?}", token.action.kind()));
            if !token.margin.is_zero() {
                node = node.property("margin", format!("{:?}", token.margin));
            }
            if token.align_y != Alignment::Start {
                node = node.property("align_y", format!("{:?}", token.align_y));
            }
            node
        }
        ViewToken::CheckBox(token) => SchemaNode::new("CheckBox", token.id.clone())
            .property("label", quoted(&token.label))
            .property("checked", token.checked.to_string())
            .property("indeterminate", token.indeterminate.to_string())
            .property("label_italic", token.label_italic.to_string())
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::RadioGroup(token) => {
            let options = token
                .options
                .iter()
                .map(|option| {
                    let mark = if token.selected.as_deref() == Some(option.id.as_str()) {
                        "*"
                    } else {
                        ""
                    };
                    format!("{}{}:{}", mark, option.id, option.label)
                })
                .collect::<Vec<_>>()
                .join(",");
            SchemaNode::new("RadioGroup", token.id.clone())
                .property("header", optional_string(token.header.as_deref()))
                .property("selected", optional_string(token.selected.as_deref()))
                .property("orientation", format!("{:?}", token.orientation))
                .property("spacing", token.spacing.to_string())
                .property("options", quoted(&options))
                .property("state", token.state.to_string())
                .property("action", format!("{:?}", token.action.kind()))
        }
        ViewToken::Slider(token) => SchemaNode::new("Slider", token.id.clone())
            .property("value", format!("{:.2}", token.value))
            .property("min", format!("{:.2}", token.min))
            .property("max", format!("{:.2}", token.max))
            .property("step", format!("{:.2}", token.step))
            .property("preview_active", token.preview_active().to_string())
            .property("width", format!("{:?}", token.width))
            .property("height", "Fixed(32)")
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::NumberBox(token) => SchemaNode::new("NumberBox", token.id.clone())
            .property("value", format!("{:.2}", token.value))
            .property(
                "min",
                token
                    .min
                    .map(|v| format!("{v:.2}"))
                    .unwrap_or_else(|| "none".to_string()),
            )
            .property(
                "max",
                token
                    .max
                    .map(|v| format!("{v:.2}"))
                    .unwrap_or_else(|| "none".to_string()),
            )
            .property("step", format!("{:.2}", token.step))
            .property("header", optional_string(token.header.as_deref()))
            .property("placeholder", optional_string(token.placeholder.as_deref()))
            .property("spin_buttons", token.spin_buttons.to_string())
            .property("state", token.state.to_string())
            .property("action", format!("{:?}", token.action.kind())),
        ViewToken::AutoSuggestBox(token) => SchemaNode::new("AutoSuggestBox", token.id.clone())
            .property("text", quoted(&token.text))
            .property("placeholder", optional_string(token.placeholder.as_deref()))
            .property("header", optional_string(token.header.as_deref()))
            .property("open", token.open.to_string())
            .property("highlighted_index", optional_usize(token.highlighted_index))
            .property("suggestions", quoted(&token.suggestions.join(",")))
            .property("width", format!("{:?}", token.width))
            .property("state", token.state.to_string())
            .property("on_change", format!("{:?}", token.change_action.kind()))
            .property("on_submit", format!("{:?}", token.submit_action.kind())),
        ViewToken::ComboBox(token) => SchemaNode::new("ComboBox", token.id.clone())
            .property("label", optional_string(token.label.as_deref()))
            .property("placeholder", optional_string(token.placeholder.as_deref()))
            .property("selected", optional_string(token.selected.as_deref()))
            .property("selected_label", optional_string(token.selected_label()))
            .property("items", combo_items(&token.items))
            .property("width", format!("{:?}", token.width))
            .property(
                "labeled_width",
                combo_box_labeled_evidence_width(token.label.as_deref(), token.width),
            )
            .property("height", format!("{:?}", token.height))
            .property(
                "labeled_height",
                combo_box_labeled_evidence_height(token.label.as_deref(), token.height),
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
            .property("footer_items", navigation_items(&token.footer_items))
            .property(
                "pane_display_mode",
                format!("{:?}", token.pane_display_mode),
            )
            .property("header", optional_string(token.header.as_deref()))
            .property("settings_visible", token.settings_visible.to_string())
            .property("back_button", token.back_button_visible.to_string())
            .property("action", format!("{:?}", token.action.kind()))
            .property("back_action", format!("{:?}", token.back_action.kind()))
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
                .property(
                    "padding",
                    layout_padding(token.padding, token.padding_edges),
                )
                .property("spacing", token.spacing.to_string())
                .property("width", format!("{:?}", token.width))
                .property("height", format!("{:?}", token.height))
                .property("max_width", optional_u16(token.max_width))
                .property("max_height", optional_u16(token.max_height))
                .property("center_x", token.center_x.to_string())
                .property("margin", format!("{:?}", token.margin))
                .property("align", format!("{:?}", token.align))
                .property("distribution", format!("{:?}", token.distribution))
                .property("style", quoted(&token.style.summary()))
                .children(token.children.iter().map(schema_node))
        }
        ViewToken::Grid(token) => {
            let mut node = SchemaNode::new("Grid", token.id.clone())
                .property("rows", lengths(&token.rows))
                .property("columns", lengths(&token.columns))
                .property("row_spacing", token.row_spacing.to_string())
                .property("column_spacing", token.column_spacing.to_string())
                .property(
                    "padding",
                    layout_padding(token.padding, token.padding_edges),
                )
                .property("width", format!("{:?}", token.width))
                .property("height", format!("{:?}", token.height))
                .property("align", format!("{:?}", token.align))
                .property("children", token.children.len().to_string());
            for child in &token.children {
                let cell = SchemaNode::new("GridCell", None)
                    .property("row", child.row.to_string())
                    .property("column", child.column.to_string())
                    .property("row_span", child.row_span.to_string())
                    .property("column_span", child.column_span.to_string())
                    .child(schema_node(&child.view));
                node = node.child(cell);
            }
            node
        }
        ViewToken::Border(token) => SchemaNode::new("Border", token.id.clone())
            .property("corner_radius", token.corner_radius.to_string())
            .property("stroke_width", token.stroke_width.to_string())
            .property("filled", token.filled.to_string())
            .property("padding", format!("{:?}", token.padding))
            .property("width", format!("{:?}", token.width))
            .property("height", format!("{:?}", token.height))
            .child(schema_node(&token.content)),
        ViewToken::Viewbox(token) => SchemaNode::new("Viewbox", token.id.clone())
            .property("stretch", format!("{:?}", token.stretch))
            .property("width", format!("{:?}", token.width))
            .property("height", format!("{:?}", token.height))
            .child(schema_node(&token.content)),
        ViewToken::TabView(token) => {
            let mut node = SchemaNode::new("TabView", token.id.clone())
                .property("tabs", token.tabs.len().to_string())
                .property("selected", optional_string(token.selected.as_deref()))
                .property("action", format!("{:?}", token.action.kind()))
                .property("close_action", format!("{:?}", token.close_action.kind()));
            for tab in &token.tabs {
                let mut tab_node = SchemaNode::new("Tab", Some(tab.id.clone()))
                    .property("header", quoted(&tab.header))
                    .property("closable", tab.closable.to_string())
                    .property("close_a11y_name", tab_close_a11y_name(tab))
                    .property(
                        "selected",
                        (token.selected.as_deref() == Some(tab.id.as_str())).to_string(),
                    );
                // Only the selected tab's content is rendered in the backend, but
                // the schema records every tab's subtree for snapshot coverage.
                tab_node = tab_node.child(schema_node(&tab.content));
                node = node.child(tab_node);
            }
            node
        }
        ViewToken::TreeView(token) => {
            let mut node = SchemaNode::new("TreeView", token.id.clone())
                .property("roots", token.roots.len().to_string())
                .property("selected", optional_string(token.selected.as_deref()))
                .property("action", format!("{:?}", token.action.kind()))
                .property("toggle_action", format!("{:?}", token.toggle_action.kind()));
            for root in &token.roots {
                node = node.child(tree_node_schema(root, token.selected.as_deref()));
            }
            node
        }
        ViewToken::Wrap(token) => SchemaNode::new("Wrap", token.id.clone())
            .property("children", token.children.len().to_string())
            .property("max_columns", token.max_columns.to_string())
            .property("spacing", token.spacing.to_string())
            .property("run_spacing", token.run_spacing.to_string())
            .children(token.children.iter().map(schema_node)),
        ViewToken::Flyout(token) => SchemaNode::new("Flyout", token.id.clone())
            .property("open", token.open.to_string())
            .property("placement", format!("{:?}", token.placement))
            .property("light_dismiss", format!("{:?}", token.light_dismiss))
            .property("focus_behavior", format!("{:?}", token.focus_behavior))
            .child(schema_node(&token.anchor))
            .child(schema_node(&token.content)),
        ViewToken::Overlay(token) => {
            let layers = token
                .layers
                .iter()
                .map(|layer| {
                    format!(
                        "{:?}/{:?}/scrim={}/block={}",
                        layer.align_x,
                        layer.align_y,
                        optional_f32(layer.scrim),
                        layer.blocks_input
                    )
                })
                .collect::<Vec<_>>()
                .join(",");
            let children = std::iter::once(token.base.as_ref())
                .chain(token.layers.iter().map(|layer| layer.content.as_ref()));
            SchemaNode::new("Overlay", token.id.clone())
                .property("layers", token.layers.len().to_string())
                .property("blocking_layers", token.blocking_layer_count().to_string())
                .property("scrim_layers", token.scrim_layer_count().to_string())
                .property("layout", quoted(&layers))
                .children(children.map(schema_node))
        }
        ViewToken::AdaptiveSwitch(token) => {
            let node = SchemaNode::new("AdaptiveSwitch", token.id.clone())
                .property("breakpoint_width", token.breakpoint_width.to_string())
                .property("resolved_width", optional_f32(token.resolved_width))
                .property("resolved_branch", token.resolved_branch_name());
            match token.resolved_branch() {
                Some(branch) => node.child(schema_node(branch)),
                None => node
                    .child(schema_node(&token.wide))
                    .child(schema_node(&token.narrow)),
            }
        }
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
                .property("title_id", optional_string(token.title_id.as_deref()))
                .property("description", optional_string(token.description.as_deref()))
                .property("icon", optional_icon(token.icon.as_ref()))
                .property("expanded", token.expanded.to_string())
                .property("motion", "expand-collapse-reveal")
                .property("state", format!("{}", token.header_state))
                .property("header_style", token.header_style.summary())
                .property("content_style", token.content_style.summary())
                .property("action", format!("{:?}", token.action.kind()))
                .property("trailing", token.trailing.len().to_string());
            if let Some(id) = &token.title_id {
                node = node.child(settings_row_text_node(id, &token.title, "BodyStrong"));
            }
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
                .property("has_content", token.content.is_some().to_string())
                .property("trailing", token.trailing.len().to_string());
            if !token.margin.is_zero() {
                node = node.property("margin", format!("{:?}", token.margin));
            }
            if token.align_x != Alignment::Start {
                node = node.property("align_x", format!("{:?}", token.align_x));
            }
            if token.content_align_x != Alignment::Start {
                node = node.property("content_align_x", format!("{:?}", token.content_align_x));
            }
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
        ViewToken::ListView(token) => {
            let mut node = SchemaNode::new("ListView", token.id.clone())
                .property("list_contract", token.list_contract_kind().as_str())
                .property("items", token.items.len().to_string())
                .property("selected", optional_string(token.selected.as_deref()))
                .property("spacing", token.spacing.to_string())
                .property("max_height", optional_u16(token.max_height))
                .property("virtualized", token.virtualized.to_string())
                .property("action", format!("{:?}", token.action.kind()));
            for item in &token.items {
                let row = SchemaNode::new("ListViewItem", Some(item.id.clone()))
                    .property(
                        "selected",
                        (token.selected.as_deref() == Some(item.id.as_str())).to_string(),
                    )
                    .child(schema_node(&item.view));
                node = node.child(row);
            }
            node
        }
        ViewToken::TrayMenu(token) => {
            let mut node = SchemaNode::new("TrayMenu", token.id.clone())
                .property("items", token.items.len().to_string())
                .property("min_width", token.min_width.to_string())
                .property("max_height", optional_u16(token.style.presenter_max_height))
                .property(
                    "corner_radius",
                    token.style.presenter_corner_radius.to_string(),
                )
                .property(
                    "shadow_margin",
                    token.style.presenter_shadow_margin.to_string(),
                )
                .property("animation_offset_y", token.animation_offset_y.to_string())
                .property("item_min_height", token.style.item_min_height.to_string())
                .property("item_font_size", token.style.item_font_size.to_string())
                .property(
                    "item_corner_radius",
                    token.style.item_corner_radius.to_string(),
                )
                .property(
                    "item_vertical_padding",
                    token.style.item_vertical_padding.to_string(),
                )
                .property(
                    "item_horizontal_padding",
                    token.style.item_horizontal_padding.to_string(),
                )
                .property(
                    "submenu_arrow_column_width",
                    token.style.submenu_arrow_column_width.to_string(),
                )
                .property("hover_inset_x", token.style.hover_inset_x.to_string())
                .property("hover_inset_y", token.style.hover_inset_y.to_string())
                .property("separator_height", token.style.separator_height.to_string())
                .property(
                    "separator_line_thickness",
                    token.style.separator_line_thickness.to_string(),
                )
                .property(
                    "separator_horizontal_inset",
                    token.style.separator_horizontal_inset.to_string(),
                )
                .property("light_surface", tray_menu_color(token.style.light_surface))
                .property(
                    "light_foreground",
                    tray_menu_color(token.style.light_foreground),
                )
                .property(
                    "light_separator",
                    tray_menu_color(token.style.light_separator),
                )
                .property("dark_surface", tray_menu_color(token.style.dark_surface))
                .property(
                    "dark_foreground",
                    tray_menu_color(token.style.dark_foreground),
                )
                .property(
                    "dark_separator",
                    tray_menu_color(token.style.dark_separator),
                )
                .property(
                    "hover_foreground_mix_percent",
                    token.style.hover_foreground_mix_percent.to_string(),
                );
            for item in &token.items {
                node = node.child(tray_menu_item_schema(item));
            }
            node
        }
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
            .property("magnifier_visible", token.magnifier_visible.to_string())
            .property("has_background", token.background.is_some().to_string())
            .property("background_pixels", optional_capture_background(token))
            .property("cursor", optional_capture_point(token.cursor)),
        ViewToken::Image(token) => SchemaNode::new("Image", token.id.clone())
            .property("source_kind", format!("{:?}", token.source_kind()))
            .property("fallback", token.fallback_behavior())
            .property("bgra_path", quoted(&token.bgra_path))
            .property("pixel_width", token.pixel_width.to_string())
            .property("pixel_height", token.pixel_height.to_string())
            .property("raster_path", optional_string(token.raster_path.as_deref()))
            .property("stretch", format!("{:?}", token.stretch))
            .property("width", format!("{:?}", token.width))
            .property("height", format!("{:?}", token.height)),
        ViewToken::WebView(token) => {
            let (source_kind, source_value) = match &token.source {
                crate::view::WebViewSource::Html(html) => ("html", html.clone()),
                crate::view::WebViewSource::Url(url) => ("url", url.clone()),
            };
            SchemaNode::new("WebView", token.id.clone())
                .property("source_kind", source_kind)
                .property("source", quoted(&source_value))
                .property("width", format!("{:?}", token.width))
                .property("height", format!("{:?}", token.height))
        }
        ViewToken::Custom(token) => SchemaNode::new("Custom", token.id.clone())
            .property("kind", token.kind.as_str())
            .property("control", quoted(&token.control))
            .property("target_type", optional_string(token.target_type.as_deref()))
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
        .property("list_contract", token.list_contract_kind().as_str())
        .property("items", token.items.len().to_string())
        .property("virtualized", token.virtualized.to_string())
        .property(
            "height",
            token
                .height
                .map(|value| format!("{value:?}"))
                .unwrap_or_else(|| "none".to_string()),
        )
        .property("max_height", optional_u16(token.max_height))
        .property("spacing", optional_u16(token.spacing))
        .property("padding", optional_edges(token.padding))
        .property("border_width", optional_u16(token.border_width))
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

fn optional_usize(value: Option<usize>) -> String {
    value
        .map(|value| value.to_string())
        .unwrap_or_else(|| "none".to_string())
}

fn optional_f32(value: Option<f32>) -> String {
    value
        .map(|value| format!("{value:.2}"))
        .unwrap_or_else(|| "none".to_string())
}

fn tab_close_a11y_name<Message>(tab: &crate::view::TabItem<Message>) -> String {
    if !tab.closable {
        return "none".to_string();
    }

    let name = tab
        .close_a11y_name
        .clone()
        .unwrap_or_else(|| format!("Close {}", tab.header));
    quoted(&name)
}

fn optional_length(value: Option<crate::view::Length>) -> String {
    value
        .map(|value| format!("{value:?}"))
        .unwrap_or_else(|| "none".to_string())
}

fn tree_node_schema(node: &crate::view::TreeNode, selected: Option<&str>) -> SchemaNode {
    let mut schema = SchemaNode::new("TreeNode", Some(node.id.clone()))
        .property("label", quoted(&node.label))
        .property("expanded", node.expanded.to_string())
        .property("selected", (selected == Some(node.id.as_str())).to_string())
        .property("children", node.children.len().to_string());
    for child in &node.children {
        schema = schema.child(tree_node_schema(child, selected));
    }
    schema
}

fn tray_menu_color(color: crate::platform::TrayMenuColor) -> String {
    match color {
        crate::platform::TrayMenuColor::SystemMenu => "SystemMenu".to_string(),
        crate::platform::TrayMenuColor::Rgb(red, green, blue) => {
            format!("#{red:02X}{green:02X}{blue:02X}")
        }
    }
}

fn tray_menu_item_schema<Message>(item: &crate::platform::TrayMenuItem<Message>) -> SchemaNode {
    if item.is_separator() {
        return SchemaNode::new("TrayMenuSeparator", None);
    }

    let mut row = SchemaNode::new("TrayMenuItem", Some(item.id.clone()))
        .property("label", quoted(&item.label))
        .property("tooltip", optional_string(item.tooltip.as_deref()))
        .property("enabled", item.enabled.to_string())
        .property("submenu", item.is_submenu().to_string())
        .property("children", item.children.len().to_string())
        .property("action", format!("{:?}", item.action.kind()));
    for child in &item.children {
        row = row.child(tray_menu_item_schema(child));
    }
    row
}

fn lengths(values: &[crate::view::Length]) -> String {
    if values.is_empty() {
        return "[]".to_string();
    }
    let joined = values
        .iter()
        .map(|value| format!("{value:?}"))
        .collect::<Vec<_>>()
        .join(",");
    format!("[{joined}]")
}

fn optional_text_style(value: Option<crate::view::TextStyle>) -> String {
    value
        .map(|value| format!("{value:?}"))
        .unwrap_or_else(|| "none".to_string())
}

fn optional_edges(value: Option<crate::view::Edges>) -> String {
    value
        .map(|value| format!("{value:?}"))
        .unwrap_or_else(|| "none".to_string())
}

fn layout_padding(uniform: u16, edges: Option<crate::view::Edges>) -> String {
    edges
        .map(|value| format!("{value:?}"))
        .unwrap_or_else(|| uniform.to_string())
}

fn combo_box_labeled_evidence_height(label: Option<&str>, height: crate::view::Length) -> String {
    if !label.is_some_and(|value| !value.trim().is_empty()) {
        return "none".to_string();
    }

    match height {
        crate::view::Length::Fixed(value) => format!("Fixed({})", value.saturating_add(32)),
        _ => "none".to_string(),
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

fn text_editor_evidence_height<Message>(token: &crate::view::TextEditorToken<Message>) -> String {
    token
        .min_height
        .or(token.max_height)
        .map(|height| format!("Fixed({height})"))
        .unwrap_or_else(|| "auto".to_string())
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

fn optional_capture_point(value: Option<crate::view::CaptureOverlayPoint>) -> String {
    value
        .map(|point| format!("({},{})", point.x, point.y))
        .unwrap_or_else(|| "none".to_string())
}

fn optional_capture_background(token: &crate::view::CaptureOverlayToken) -> String {
    token
        .background_pixel_size()
        .map(|(width, height)| format!("{width}x{height}"))
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
    use crate::view::{button, checkbox, column, page, settings_row, text, text_editor, IntoView};

    #[allow(dead_code)]
    #[derive(Clone)]
    enum Msg {
        Input(String),
        Run,
        Toggle(bool),
    }

    #[test]
    fn checkbox_emits_indeterminate_state() {
        let view = page::<Msg>("Demo")
            .content(column((checkbox("Mixed", false)
                .indeterminate(true)
                .on_toggle(Msg::Toggle),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("CheckBox label=\"Mixed\""));
        assert!(snapshot.contains("indeterminate=true"));
    }

    #[test]
    fn grid_emits_tracks_and_placed_cells() {
        use crate::view::{grid, Length};
        let view = page::<Msg>("Demo")
            .content(column((grid()
                .columns([Length::Shrink, Length::Fill])
                .rows([Length::Shrink, Length::Shrink])
                .cell(0, 0, text("Name"))
                .cell(0, 1, text_editor("").on_input(Msg::Input))
                .cell_span(1, 0, 1, 2, button("Save").on_press(Msg::Run)),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("Grid"));
        assert!(snapshot.contains("columns=[Shrink,Fill]"));
        assert!(snapshot.contains("rows=[Shrink,Shrink]"));
        assert!(snapshot.contains("GridCell"));
        assert!(snapshot.contains("column_span=2"));
    }

    #[test]
    fn list_view_emits_items_and_selection() {
        use crate::view::{list_view, ListViewItem};
        let view = page::<Msg>("Demo")
            .content(column((list_view([
                ListViewItem::new("en", text("English")),
                ListViewItem::new("zh", text("中文")),
            ])
            .selected("zh")
            .on_select(Msg::Input),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("ListView"));
        assert!(snapshot.contains("selected=\"zh\""));
        assert!(snapshot.contains("action=selection_input"));
        assert!(snapshot.contains("ListViewItem"));
        assert!(snapshot.contains("id=\"en\""));
    }

    #[test]
    fn radio_group_emits_options_and_selection() {
        use crate::view::radio_group;
        let view = page::<Msg>("Demo")
            .content(column((radio_group()
                .header("Theme")
                .option("system", "System")
                .option("light", "Light")
                .option("dark", "Dark")
                .selected("light")
                .on_select(Msg::Input),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("RadioGroup"));
        assert!(snapshot.contains("header=\"Theme\""));
        assert!(snapshot.contains("selected=\"light\""));
        assert!(snapshot.contains("*light:Light"));
        assert!(snapshot.contains("action=selection_input"));
    }

    #[test]
    fn generic_image_emits_raster_source_and_stretch() {
        use crate::view::{image, ImageStretch, Length};
        let view = page::<Msg>("Demo")
            .content(column((image("assets/flags/zh.png")
                .stretch(ImageStretch::Uniform)
                .width(Length::Fixed(24)),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("raster_path=\"assets/flags/zh.png\""));
        assert!(snapshot.contains("stretch=Uniform"));
    }

    #[test]
    fn number_box_emits_range_and_spin() {
        use crate::view::number_box;
        let view = page::<Msg>("Demo")
            .content(column((number_box(1.0)
                .range(0.5, 3.0)
                .step(0.1)
                .header("Speed")
                .on_change(|_| Msg::Run),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("NumberBox"));
        assert!(snapshot.contains("min=0.50"));
        assert!(snapshot.contains("max=3.00"));
        assert!(snapshot.contains("spin_buttons=true"));
        assert!(snapshot.contains("action=number_input"));
    }

    #[test]
    fn auto_suggest_box_emits_suggestions() {
        use crate::view::auto_suggest_box;
        let view = page::<Msg>("Demo")
            .content(column((auto_suggest_box("en")
                .placeholder("Search")
                .suggestions(["English", "Spanish"])
                .on_change(Msg::Input)
                .on_submit(Msg::Input),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("AutoSuggestBox"));
        assert!(snapshot.contains("open=true"));
        assert!(snapshot.contains("suggestions=\"English,Spanish\""));
        assert!(snapshot.contains("on_change=text_input"));
        assert!(snapshot.contains("on_submit=selection_input"));
    }

    #[test]
    fn navigation_view_emits_pane_mode_header_footer_settings() {
        use crate::view::{navigation_view, NavigationItem, PaneDisplayMode};
        let view = navigation_view::<Msg>([NavigationItem::new("home", "Home")])
            .header("DemoApp")
            .pane_display_mode(PaneDisplayMode::LeftCompact)
            .footer_items([NavigationItem::new("about", "About")])
            .settings_visible(true)
            .back_button(Msg::Run)
            .on_select(Msg::Input)
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("pane_display_mode=LeftCompact"));
        assert!(snapshot.contains("header=\"DemoApp\""));
        assert!(snapshot.contains("settings_visible=true"));
        assert!(snapshot.contains("back_button=true"));
        assert!(snapshot.contains("back_action=message"));
    }

    #[test]
    fn generic_flyout_emits_anchor_and_content() {
        use crate::view::{flyout, FlyoutPlacement};
        let view = page::<Msg>("Demo")
            .content(column((flyout(
                button("Open").on_press(Msg::Run),
                text("Panel"),
            )
            .open(true)
            .placement(FlyoutPlacement::Right),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("Flyout open=true placement=Right"));
        assert!(snapshot.contains("value=\"Panel\""));
    }

    #[test]
    fn rich_text_emits_styled_runs_and_links() {
        use crate::view::{text_runs, TextRun};
        let view = page::<Msg>("Demo")
            .content(column((text_runs([
                TextRun::plain("see "),
                TextRun::link("hello", "word:hello"),
                TextRun::italic(" (interj.)"),
            ])
            .on_link(Msg::Input),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("RichText"));
        assert!(snapshot.contains("link(hello->word:hello)"));
        assert!(snapshot.contains("italic( (interj.))"));
        assert!(snapshot.contains("on_link=selection_input"));
    }

    #[test]
    fn web_view_emits_source() {
        use crate::view::web_view_url;
        let view = page::<Msg>("Demo")
            .content(column((web_view_url("https://example.com/entry"),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("WebView"));
        assert!(snapshot.contains("source_kind=url"));
        assert!(snapshot.contains("source=\"https://example.com/entry\""));
    }

    #[test]
    fn toggle_and_split_buttons_emit_state() {
        use crate::view::{split_button, toggle_button, FlyoutMenuItem};
        let view = page::<Msg>("Demo")
            .content(column((
                toggle_button("Bold", true).on_toggle(Msg::Toggle),
                split_button("Save")
                    .items([FlyoutMenuItem::command("save_as", "Save As…")])
                    .on_press(Msg::Run)
                    .on_select(Msg::Input),
            )))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("ToggleButton"));
        assert!(snapshot.contains("pressed=true"));
        assert!(snapshot.contains("SplitButton"));
        assert!(snapshot.contains("primary=message"));
        assert!(snapshot.contains("select=selection_input"));
    }

    #[test]
    fn flyout_button_emits_title_trigger_styling() {
        use crate::view::{flyout_button, FlyoutMenuItem, TextStyle};
        let view = page::<Msg>("Demo")
            .content(column((flyout_button("DemoApp")
                .text_style(TextStyle::Subtitle)
                .font_size(22)
                .min_width(0)
                .min_height(0)
                .items([FlyoutMenuItem::radio("quick", "Quick Translation", true)])
                .on_select(Msg::Input),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("FlyoutButton"));
        assert!(snapshot.contains("label=\"DemoApp\""));
        assert!(snapshot.contains("text_style=Subtitle"));
        assert!(snapshot.contains("font_size=22"));
        assert!(snapshot.contains("action=selection_input"));
    }

    #[test]
    fn tab_view_and_tree_view_emit_structure() {
        use crate::view::{tab_view, tree_view, TabItem, TreeNode};
        let view = page::<Msg>("Demo")
            .content(column((
                tab_view([
                    TabItem::new("a", "Tab A", text("A body")),
                    TabItem::new("b", "Tab B", text("B body")).closable(true),
                ])
                .selected("b")
                .on_select(Msg::Input),
                tree_view([TreeNode::branch(
                    "root",
                    "Root",
                    [TreeNode::leaf("child", "Child")],
                )])
                .selected("child")
                .on_select(Msg::Input),
            )))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("TabView"));
        assert!(snapshot.contains("Tab header=\"Tab B\""));
        assert!(snapshot.contains("closable=true"));
        assert!(snapshot.contains("TreeView"));
        assert!(snapshot.contains("TreeNode"));
        assert!(snapshot.contains("label=\"Child\""));
    }

    #[test]
    fn border_and_viewbox_wrap_content() {
        use crate::view::{border, viewbox, ImageStretch};
        let view = page::<Msg>("Demo")
            .content(column((
                border(text("Boxed")).corner_radius(8).filled(true),
                viewbox(text("Scaled")).stretch(ImageStretch::Uniform),
            )))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("Border"));
        assert!(snapshot.contains("corner_radius=8"));
        assert!(snapshot.contains("filled=true"));
        assert!(snapshot.contains("Viewbox"));
        assert!(snapshot.contains("value=\"Boxed\""));
    }

    #[test]
    fn generic_tooltip_is_emitted_for_any_element() {
        let view = page::<Msg>("Demo")
            .content(column((text("Hover me").tooltip("Helpful hint"),)))
            .into_view();

        let snapshot = view_schema(&view).snapshot();

        assert!(snapshot.contains("tooltip=\"Helpful hint\""));
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

#[cfg(feature = "parity-diagnostics")]
fn diagnostic_node<Message>(
    view: &View<Message>,
    path: crate::diff::ViewPath,
) -> DiagnosticNode {
    let declarative = schema_node(view);
    let children = diagnostic_children(view.token())
        .into_iter()
        .enumerate()
        .map(|(index, child)| {
            let id = schema_node(child).id;
            let segment = id
                .map(|id| format!("{index}:{id}"))
                .unwrap_or_else(|| format!("{index}:{}", schema_node(child).kind));
            diagnostic_node(child, path.child(segment))
        })
        .collect();
    DiagnosticNode {
        path,
        kind: declarative.kind.to_string(),
        id: declarative.id,
        properties: declarative.properties,
        style_classes: diagnostic_style_classes(view.token()),
        provenance: view.provenance().clone(),
        children,
    }
}

#[cfg(feature = "parity-diagnostics")]
fn diagnostic_style_classes<Message>(token: &ViewToken<Message>) -> Vec<String> {
    match token {
        ViewToken::Layout(token) => token.style.classes().to_vec(),
        ViewToken::Expander(token) => token
            .header_style
            .classes()
            .iter()
            .chain(token.content_style.classes().iter())
            .cloned()
            .collect(),
        _ => Vec::new(),
    }
}

#[cfg(feature = "parity-diagnostics")]
fn diagnostic_children<Message>(token: &ViewToken<Message>) -> Vec<&View<Message>> {
    match token {
        ViewToken::Page(token) => token.content.iter().map(Box::as_ref).collect(),
        ViewToken::TitleBar(token) => token.commands.iter().collect(),
        ViewToken::BusyOverlay(token) => vec![token.content.as_ref()],
        ViewToken::CommandBar(token) => token.items.iter().collect(),
        ViewToken::NavigationView(token) => token.content.iter().map(Box::as_ref).collect(),
        ViewToken::Dialog(token) => token.content.iter().map(Box::as_ref).collect(),
        ViewToken::Layout(token) => token.children.iter().collect(),
        ViewToken::Grid(token) => token.children.iter().map(|child| &child.view).collect(),
        ViewToken::Border(token) => vec![token.content.as_ref()],
        ViewToken::Viewbox(token) => vec![token.content.as_ref()],
        ViewToken::TabView(token) => token.tabs.iter().map(|tab| &tab.content).collect(),
        ViewToken::ListView(token) => token.items.iter().map(|item| &item.view).collect(),
        ViewToken::Wrap(token) => token.children.iter().collect(),
        ViewToken::Flyout(token) => vec![token.anchor.as_ref(), token.content.as_ref()],
        ViewToken::Overlay(token) => {
            let mut children: Vec<&View<Message>> = vec![token.base.as_ref()];
            children.extend(token.layers.iter().map(|layer| layer.content.as_ref()));
            children
        }
        ViewToken::AdaptiveSwitch(token) => match token.resolved_branch() {
            Some(branch) => vec![branch],
            None => vec![token.wide.as_ref(), token.narrow.as_ref()],
        },
        ViewToken::Lazy(token) => vec![token.content.as_ref()],
        ViewToken::ScrollView(token) => token.content.iter().map(Box::as_ref).collect(),
        ViewToken::Card(token) => {
            let mut children: Vec<&View<Message>> =
                token.content.iter().map(Box::as_ref).collect();
            children.extend(token.trailing.iter());
            children
        }
        ViewToken::Expander(token) => {
            let mut children: Vec<&View<Message>> = if token.expanded {
                token.content.iter().map(Box::as_ref).collect()
            } else {
                Vec::new()
            };
            children.extend(token.trailing.iter());
            children
        }
        ViewToken::SettingsRow(token) => {
            let mut children: Vec<&View<Message>> =
                token.content.iter().map(Box::as_ref).collect();
            children.extend(token.trailing.iter());
            children
        }
        ViewToken::PointerRegion(token) => vec![token.content.as_ref()],
        ViewToken::Custom(token) => token.children.iter().collect(),
        ViewToken::Text(_)
        | ViewToken::RichText(_)
        | ViewToken::Button(_)
        | ViewToken::ToggleButton(_)
        | ViewToken::SplitButton(_)
        | ViewToken::TreeView(_)
        | ViewToken::FlyoutButton(_)
        | ViewToken::StatusBadge(_)
        | ViewToken::InfoBar(_)
        | ViewToken::ProgressRing(_)
        | ViewToken::ProgressBar(_)
        | ViewToken::Spacer(_)
        | ViewToken::TextEditor(_)
        | ViewToken::CheckBox(_)
        | ViewToken::RadioGroup(_)
        | ViewToken::ToggleSwitch(_)
        | ViewToken::Slider(_)
        | ViewToken::NumberBox(_)
        | ViewToken::AutoSuggestBox(_)
        | ViewToken::ComboBox(_)
        | ViewToken::ResultCard(_)
        | ViewToken::ResultList(_)
        | ViewToken::TrayMenu(_)
        | ViewToken::CaptureOverlay(_)
        | ViewToken::Image(_)
        | ViewToken::WebView(_) => Vec::new(),
    }
}

#[cfg(feature = "parity-diagnostics")]
fn collect_duplicate_id_warnings(node: &DiagnosticNode, warnings: &mut Vec<String>) {
    let mut seen: Vec<(&str, &crate::diff::ViewPath)> = Vec::new();
    for child in &node.children {
        if let Some(id) = child.id.as_deref() {
            if let Some((_, first_path)) = seen.iter().find(|(seen_id, _)| *seen_id == id) {
                warnings.push(format!(
                    "duplicate-id:{id}:{}:{}",
                    first_path, child.path
                ));
            } else {
                seen.push((id, &child.path));
            }
        }
    }
    for child in &node.children {
        collect_duplicate_id_warnings(child, warnings);
    }
}


#[cfg(feature = "parity-diagnostics")]
fn diff_diagnostic_nodes(
    changes: &mut Vec<DiagnosticChange>,
    before: &DiagnosticNode,
    after: &DiagnosticNode,
) {
    if before.kind != after.kind || before.id != after.id {
        changes.push(DiagnosticChange {
            path: after.path.clone(),
            property: None,
            before: Some(before.kind.clone()),
            after: Some(after.kind.clone()),
            before_source: Some(before.provenance.constructor),
            after_source: Some(after.provenance.constructor),
        });
        return;
    }
    for property in before
        .properties
        .iter()
        .filter_map(|property| Some(property.name.as_str()))
        .chain(after.properties.iter().map(|property| property.name.as_str()))
    {
        if changes.iter().any(|change: &DiagnosticChange| {
            change.path == after.path && change.property.as_deref() == Some(property)
        }) {
            continue;
        }
        let left = before.properties.iter().find(|item| item.name == property);
        let right = after.properties.iter().find(|item| item.name == property);
        if left.map(|item| &item.value) != right.map(|item| &item.value) {
            changes.push(DiagnosticChange {
                path: after.path.clone(),
                property: Some(property.to_string()),
                before: left.map(|item| item.value.clone()),
                after: right.map(|item| item.value.clone()),
                before_source: left.and_then(|_| before.provenance.source_for(property)),
                after_source: right.and_then(|_| after.provenance.source_for(property)),
            });
        }
    }
    let max = before.children.len().max(after.children.len());
    for index in 0..max {
        match (before.children.get(index), after.children.get(index)) {
            (Some(left), Some(right)) => diff_diagnostic_nodes(changes, left, right),
            (Some(left), None) => changes.push(DiagnosticChange {
                path: left.path.clone(),
                property: None,
                before: Some(left.kind.clone()),
                after: None,
                before_source: Some(left.provenance.constructor),
                after_source: None,
            }),
            (None, Some(right)) => changes.push(DiagnosticChange {
                path: right.path.clone(),
                property: None,
                before: None,
                after: Some(right.kind.clone()),
                before_source: None,
                after_source: Some(right.provenance.constructor),
            }),
            (None, None) => {}
        }
    }
}

#[cfg(all(test, feature = "parity-diagnostics"))]
mod diagnostic_tests {
    use super::*;
    use crate::view::{button, text, IntoView, Length};

    #[test]
    fn explicit_width_records_setter_source_and_default_constructor() {
        let default_view = text::<()>("default");
        let explicit_view = button::<()>("run")
            .width(Length::Fixed(120))
            .into_view();
        let diagnostic = diagnostic_view_schema(&explicit_view);
        let width = diagnostic
            .root
            .properties
            .iter()
            .find(|property| property.name == "width")
            .expect("button width property");
        let source = diagnostic
            .root
            .provenance
            .source_for(&width.name)
            .expect("width source");
        assert_ne!(source.file, "<unavailable>");
        assert!(source.line > 0);
        assert_ne!(
            default_view.provenance().constructor,
            crate::provenance::SourceLocation {
                file: "<unavailable>",
                line: 0,
                column: 0
            }
        );
    }

    #[test]
    fn duplicate_ids_keep_structural_paths() {
        let view = crate::view::column((
            button::<()>("one").id("dup").into_view(),
            button::<()>("two").id("dup").into_view(),
        ))
        .id("root")
        .into_view();
        let diagnostic = diagnostic_view_schema(&view);
        let paths = diagnostic
            .root
            .children
            .iter()
            .map(|node| node.path.to_string())
            .collect::<Vec<_>>();
        assert_eq!(paths, vec!["root/0:dup", "root/1:dup"]);
        assert_eq!(diagnostic.warnings.len(), 1);
    }
}

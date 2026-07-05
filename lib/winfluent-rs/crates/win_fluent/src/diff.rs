use std::fmt;

use crate::view::{LayoutKind, ResultItem, View, ViewToken};

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ViewPath(Vec<String>);

impl ViewPath {
    pub fn root() -> Self {
        Self(vec!["root".to_string()])
    }

    pub fn child(&self, segment: impl Into<String>) -> Self {
        let mut values = self.0.clone();
        values.push(segment.into());
        Self(values)
    }

    pub fn segments(&self) -> &[String] {
        &self.0
    }
}

impl fmt::Display for ViewPath {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0.join("/"))
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ViewChangeKind {
    Added {
        kind: &'static str,
    },
    Removed {
        kind: &'static str,
    },
    Replaced {
        before: &'static str,
        after: &'static str,
    },
    Updated {
        kind: &'static str,
    },
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ViewChange {
    pub path: ViewPath,
    pub kind: ViewChangeKind,
}

pub fn diff_views<Message>(before: &View<Message>, after: &View<Message>) -> Vec<ViewChange> {
    let mut changes = Vec::new();
    diff_at(&mut changes, ViewPath::root(), before, after);
    changes
}

fn diff_at<Message>(
    changes: &mut Vec<ViewChange>,
    path: ViewPath,
    before: &View<Message>,
    after: &View<Message>,
) {
    let before_kind = token_kind(before.token());
    let after_kind = token_kind(after.token());

    if before_kind != after_kind {
        changes.push(ViewChange {
            path,
            kind: ViewChangeKind::Replaced {
                before: before_kind,
                after: after_kind,
            },
        });
        return;
    }

    if token_summary(before.token()) != token_summary(after.token()) {
        changes.push(ViewChange {
            path: path.clone(),
            kind: ViewChangeKind::Updated { kind: before_kind },
        });
    }

    let before_children = token_children(before.token());
    let after_children = token_children(after.token());
    let max = before_children.len().max(after_children.len());

    for index in 0..max {
        match (before_children.get(index), after_children.get(index)) {
            (Some(left), Some(right)) => {
                let segment = child_segment(index, right.token());
                diff_at(changes, path.child(segment), left, right);
            }
            (Some(left), None) => changes.push(ViewChange {
                path: path.child(child_segment(index, left.token())),
                kind: ViewChangeKind::Removed {
                    kind: token_kind(left.token()),
                },
            }),
            (None, Some(right)) => changes.push(ViewChange {
                path: path.child(child_segment(index, right.token())),
                kind: ViewChangeKind::Added {
                    kind: token_kind(right.token()),
                },
            }),
            (None, None) => {}
        }
    }
}

fn child_segment<Message>(index: usize, token: &ViewToken<Message>) -> String {
    match token_id(token) {
        Some(id) => format!("{index}:{id}"),
        None => format!("{index}:{}", token_kind(token)),
    }
}

fn token_children<Message>(token: &ViewToken<Message>) -> Vec<&View<Message>> {
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
        ViewToken::TrayMenu(_) => Vec::new(),
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
            let mut children: Vec<&View<Message>> = token.content.iter().map(Box::as_ref).collect();
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
            let mut children: Vec<&View<Message>> = token.content.iter().map(Box::as_ref).collect();
            children.extend(token.trailing.iter());
            children
        }
        ViewToken::PointerRegion(token) => vec![token.content.as_ref()],
        ViewToken::CaptureOverlay(_) => Vec::new(),
        ViewToken::Image(_) => Vec::new(),
        ViewToken::WebView(_) => Vec::new(),
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
        | ViewToken::ResultList(_) => Vec::new(),
    }
}

fn token_kind<Message>(token: &ViewToken<Message>) -> &'static str {
    match token {
        ViewToken::Page(_) => "Page",
        ViewToken::TitleBar(_) => "TitleBar",
        ViewToken::Text(_) => "Text",
        ViewToken::RichText(_) => "RichText",
        ViewToken::ToggleButton(_) => "ToggleButton",
        ViewToken::SplitButton(_) => "SplitButton",
        ViewToken::Border(_) => "Border",
        ViewToken::Viewbox(_) => "Viewbox",
        ViewToken::TabView(_) => "TabView",
        ViewToken::TreeView(_) => "TreeView",
        ViewToken::Button(_) => "Button",
        ViewToken::FlyoutButton(_) => "FlyoutButton",
        ViewToken::StatusBadge(_) => "StatusBadge",
        ViewToken::InfoBar(_) => "InfoBar",
        ViewToken::ProgressRing(_) => "ProgressRing",
        ViewToken::ProgressBar(_) => "ProgressBar",
        ViewToken::BusyOverlay(_) => "BusyOverlay",
        ViewToken::Card(_) => "Card",
        ViewToken::Spacer(_) => "Spacer",
        ViewToken::TextEditor(_) => "TextEditor",
        ViewToken::CheckBox(_) => "CheckBox",
        ViewToken::ToggleSwitch(_) => "ToggleSwitch",
        ViewToken::RadioGroup(_) => "RadioGroup",
        ViewToken::Slider(_) => "Slider",
        ViewToken::NumberBox(_) => "NumberBox",
        ViewToken::AutoSuggestBox(_) => "AutoSuggestBox",
        ViewToken::ComboBox(_) => "ComboBox",
        ViewToken::CommandBar(_) => "CommandBar",
        ViewToken::NavigationView(_) => "NavigationView",
        ViewToken::Dialog(_) => "Dialog",
        ViewToken::Layout(token) => match token.kind {
            LayoutKind::Column => "Column",
            LayoutKind::Row => "Row",
        },
        ViewToken::Grid(_) => "Grid",
        ViewToken::Wrap(_) => "Wrap",
        ViewToken::Flyout(_) => "Flyout",
        ViewToken::Overlay(_) => "Overlay",
        ViewToken::AdaptiveSwitch(_) => "AdaptiveSwitch",
        ViewToken::Lazy(_) => "Lazy",
        ViewToken::ScrollView(_) => "ScrollView",
        ViewToken::Expander(_) => "Expander",
        ViewToken::SettingsRow(_) => "SettingsRow",
        ViewToken::ResultCard(_) => "ResultCard",
        ViewToken::ResultList(_) => "ResultList",
        ViewToken::ListView(_) => "ListView",
        ViewToken::TrayMenu(_) => "TrayMenu",
        ViewToken::PointerRegion(_) => "PointerRegion",
        ViewToken::CaptureOverlay(_) => "CaptureOverlay",
        ViewToken::Image(_) => "Image",
        ViewToken::WebView(_) => "WebView",
        ViewToken::Custom(_) => "Custom",
    }
}

fn token_id<Message>(token: &ViewToken<Message>) -> Option<&str> {
    match token {
        ViewToken::Page(token) => token.id.as_deref(),
        ViewToken::TitleBar(token) => token.id.as_deref(),
        ViewToken::Text(token) => token.id.as_deref(),
        ViewToken::RichText(token) => token.id.as_deref(),
        ViewToken::ToggleButton(token) => token.id.as_deref(),
        ViewToken::SplitButton(token) => token.id.as_deref(),
        ViewToken::Border(token) => token.id.as_deref(),
        ViewToken::Viewbox(token) => token.id.as_deref(),
        ViewToken::TabView(token) => token.id.as_deref(),
        ViewToken::TreeView(token) => token.id.as_deref(),
        ViewToken::Button(token) => token.id.as_deref(),
        ViewToken::FlyoutButton(token) => token.id.as_deref(),
        ViewToken::StatusBadge(token) => token.id.as_deref(),
        ViewToken::InfoBar(token) => token.id.as_deref(),
        ViewToken::ProgressRing(token) => token.id.as_deref(),
        ViewToken::ProgressBar(token) => token.id.as_deref(),
        ViewToken::BusyOverlay(token) => token.id.as_deref(),
        ViewToken::Card(token) => token.id.as_deref(),
        ViewToken::Spacer(token) => token.id.as_deref(),
        ViewToken::TextEditor(token) => token.id.as_deref(),
        ViewToken::CheckBox(token) => token.id.as_deref(),
        ViewToken::ToggleSwitch(token) => token.id.as_deref(),
        ViewToken::RadioGroup(token) => token.id.as_deref(),
        ViewToken::Slider(token) => token.id.as_deref(),
        ViewToken::NumberBox(token) => token.id.as_deref(),
        ViewToken::AutoSuggestBox(token) => token.id.as_deref(),
        ViewToken::ComboBox(token) => token.id.as_deref(),
        ViewToken::CommandBar(token) => token.id.as_deref(),
        ViewToken::NavigationView(token) => token.id.as_deref(),
        ViewToken::Dialog(token) => token.id.as_deref(),
        ViewToken::Layout(token) => token.id.as_deref(),
        ViewToken::Grid(token) => token.id.as_deref(),
        ViewToken::Wrap(token) => token.id.as_deref(),
        ViewToken::Flyout(token) => token.id.as_deref(),
        ViewToken::Overlay(token) => token.id.as_deref(),
        ViewToken::AdaptiveSwitch(token) => token.id.as_deref(),
        ViewToken::Lazy(token) => token.id.as_deref().or(Some(token.key.as_str())),
        ViewToken::ScrollView(token) => token.id.as_deref(),
        ViewToken::Expander(token) => token.id.as_deref(),
        ViewToken::SettingsRow(token) => token.id.as_deref(),
        ViewToken::ResultCard(token) => token.id.as_deref(),
        ViewToken::ResultList(token) => token.id.as_deref(),
        ViewToken::ListView(token) => token.id.as_deref(),
        ViewToken::TrayMenu(token) => token.id.as_deref(),
        ViewToken::PointerRegion(token) => token.id.as_deref(),
        ViewToken::CaptureOverlay(token) => token.id.as_deref(),
        ViewToken::Image(token) => token.id.as_deref(),
        ViewToken::WebView(token) => token.id.as_deref(),
        ViewToken::Custom(token) => token.id.as_deref(),
    }
}

fn token_summary<Message>(token: &ViewToken<Message>) -> String {
    match token {
        ViewToken::Page(token) => format!("{:?}|commands={}", token.title, token.commands.len()),
        ViewToken::TitleBar(token) => format!(
            "{:?}|{:?}|{:?}|commands={}|caption={}|minimize={:?}|toggle_maximize={:?}|close={:?}|drag={:?}",
            token.title,
            token.subtitle,
            token.icon.as_ref().map(|icon| icon.name),
            token.commands.len(),
            token.show_caption_controls,
            token.minimize_action.kind(),
            token.toggle_maximize_action.kind(),
            token.close_action.kind(),
            token.drag_action.kind()
        ),
        ViewToken::Text(token) => format!(
            "{:?}|{:?}|font_size={:?}|{:?}|{}|margin={:?}|align_x={:?}|align_y={:?}",
            token.value,
            token.style,
            token.font_size,
            token.wrapping,
            token.selectable,
            token.margin,
            token.align_x,
            token.align_y
        ),
        ViewToken::RichText(token) => format!(
            "{:?}|{:?}|runs=[{}]|{:?}",
            token.style,
            token.wrapping,
            token
                .runs
                .iter()
                .map(|run| format!("{:?}:{}:{:?}", run.kind, run.text, run.href))
                .collect::<Vec<_>>()
                .join(","),
            token.link_action.kind()
        ),
        ViewToken::ToggleButton(token) => format!(
            "{:?}|icon={}|pressed={}|{}|{:?}",
            token.label,
            token.icon.is_some(),
            token.pressed,
            token.state,
            token.action.kind()
        ),
        ViewToken::SplitButton(token) => format!(
            "{:?}|icon={}|items=[{}]|open={}|{}|primary={:?}|select={:?}",
            token.label,
            token.icon.is_some(),
            token
                .items
                .iter()
                .map(|item| format!("{}:{}", item.id, item.label))
                .collect::<Vec<_>>()
                .join(","),
            token.open,
            token.state,
            token.primary_action.kind(),
            token.select_action.kind()
        ),
        ViewToken::Border(token) => format!(
            "radius={}|stroke={}|filled={}|padding={:?}|{:?}|{:?}",
            token.corner_radius,
            token.stroke_width,
            token.filled,
            token.padding,
            token.width,
            token.height
        ),
        ViewToken::Viewbox(token) => {
            format!("{:?}|{:?}|{:?}", token.stretch, token.width, token.height)
        }
        ViewToken::TabView(token) => format!(
            "selected={:?}|tabs=[{}]|{:?}|{:?}",
            token.selected,
            token
                .tabs
                .iter()
                .map(|tab| format!("{}:{}:{}", tab.id, tab.header, tab.closable))
                .collect::<Vec<_>>()
                .join(","),
            token.action.kind(),
            token.close_action.kind()
        ),
        ViewToken::TreeView(token) => format!(
            "selected={:?}|roots={}|{:?}|{:?}",
            token.selected,
            token.roots.len(),
            token.action.kind(),
            token.toggle_action.kind()
        ),
        ViewToken::Button(token) => format!(
            "{:?}|{:?}|{:?}|{:?}|{:?}|padding={:?}|text_style={:?}|font_size={:?}|margin={:?}|{}|{:?}",
            token.label,
            token.kind,
            token.icon.as_ref().map(|icon| icon.name),
            token.width,
            token.height,
            token.padding,
            token.text_style,
            token.font_size,
            token.margin,
            token.state,
            token.action.kind()
        ),
        ViewToken::FlyoutButton(token) => format!(
            "{:?}|selected={:?}|items={}|{}|{:?}",
            token.label,
            token.selected,
            token.items.len(),
            token.state,
            token.action.kind()
        ),
        ViewToken::StatusBadge(token) => format!(
            "{:?}|{:?}|{:?}|{:?}|{:?}",
            token.label,
            token.kind,
            token.count,
            token.severity,
            token.icon.as_ref().map(|icon| icon.name)
        ),
        ViewToken::InfoBar(token) => format!(
            "{:?}|{:?}|{:?}|{:?}",
            token.title,
            token.message,
            token.severity,
            token.icon.as_ref().map(|icon| icon.name)
        ),
        ViewToken::ProgressRing(token) => {
            format!(
                "active={}|size={}|{:?}",
                token.active, token.size, token.label
            )
        }
        ViewToken::ProgressBar(token) => format!(
            "active={}|value={:?}|{:?}|height={}|{:?}",
            token.active, token.value, token.width, token.height, token.label
        ),
        ViewToken::BusyOverlay(token) => format!(
            "active={}|opacity={:.2}|fade_transition_ms={}|blocks_input={}|{:?}",
            token.active,
            token.opacity,
            token.fade_transition_ms,
            token.blocks_input,
            token.label
        ),
        ViewToken::Card(token) => format!(
            "{:?}|{:?}|{:?}|{:?}|margin={:?}|max_height={:?}|trailing={}",
            token.title,
            token.description,
            token.kind,
            token.icon.as_ref().map(|icon| icon.name),
            token.margin,
            token.max_height,
            token.trailing.len()
        ),
        ViewToken::Spacer(token) => format!("{:?}|{:?}", token.width, token.height),
        ViewToken::TextEditor(token) => format!(
            "{:?}|{:?}|{:?}|{:?}|padding={:?}|{:?}|{:?}|secure={}|{}|{}|{:?}|keys={}",
            token.placeholder,
            token.width,
            token.min_height,
            token.max_height,
            token.padding,
            token.text_style,
            token.chrome,
            token.secure,
            token.read_only,
            token.state,
            token.action.kind(),
            token.key_bindings.len()
        ),
        ViewToken::ToggleSwitch(token) => format!(
            "{:?}|{}|margin={:?}|align_y={:?}|{}|{:?}",
            token.label,
            token.checked,
            token.margin,
            token.align_y,
            token.state,
            token.action.kind()
        ),
        ViewToken::CheckBox(token) => format!(
            "{:?}|{}|indeterminate={}|italic={}|{}|{:?}",
            token.label,
            token.checked,
            token.indeterminate,
            token.label_italic,
            token.state,
            token.action.kind()
        ),
        ViewToken::RadioGroup(token) => format!(
            "header={:?}|selected={:?}|{:?}|spacing={}|options=[{}]|{}|{:?}",
            token.header,
            token.selected,
            token.orientation,
            token.spacing,
            token
                .options
                .iter()
                .map(|option| format!("{}:{}:{}", option.id, option.label, option.enabled))
                .collect::<Vec<_>>()
                .join(","),
            token.state,
            token.action.kind()
        ),
        ViewToken::Slider(token) => format!(
            "{:.2}|{:.2}..={:.2}|step={:.2}|{:?}|{}|{:?}",
            token.value,
            token.min,
            token.max,
            token.step,
            token.width,
            token.state,
            token.action.kind()
        ),
        ViewToken::NumberBox(token) => format!(
            "{:.2}|min={:?}|max={:?}|step={:.2}|header={:?}|placeholder={:?}|spin={}|{}|{:?}",
            token.value,
            token.min,
            token.max,
            token.step,
            token.header,
            token.placeholder,
            token.spin_buttons,
            token.state,
            token.action.kind()
        ),
        ViewToken::AutoSuggestBox(token) => format!(
            "{:?}|placeholder={:?}|header={:?}|open={}|suggestions=[{}]|{:?}|{}|{:?}|{:?}",
            token.text,
            token.placeholder,
            token.header,
            token.open,
            token.suggestions.join(","),
            token.width,
            token.state,
            token.change_action.kind(),
            token.submit_action.kind()
        ),
        ViewToken::ComboBox(token) => format!(
            "{:?}|{:?}|{:?}|items={}|{:?}|{:?}|{}|{:?}",
            token.label,
            token.placeholder,
            token.selected,
            token.items.len(),
            token.width,
            token.height,
            token.state,
            token.action.kind()
        ),
        ViewToken::CommandBar(token) => format!(
            "compact={}|{:?}|{:?}|{:?}",
            token.compact, token.width, token.align, token.distribution
        ),
        ViewToken::NavigationView(token) => {
            format!(
                "{:?}|items={}|footer={}|{:?}|header={:?}|settings={}|back={}|{:?}|{:?}",
                token.selected,
                token.items.len(),
                token.footer_items.len(),
                token.pane_display_mode,
                token.header,
                token.settings_visible,
                token.back_button_visible,
                token.action.kind(),
                token.back_action.kind()
            )
        }
        ViewToken::Dialog(token) => format!(
            "{:?}|{:?}|primary={}|secondary={}",
            token.title,
            token.kind,
            token.primary.is_some(),
            token.secondary.is_some()
        ),
        ViewToken::Layout(token) => format!(
            "padding={}|padding_edges={:?}|spacing={}|{:?}|{:?}|max_width={:?}|max_height={:?}|center_x={}|margin={:?}|{:?}|{:?}|style={:?}",
            token.padding,
            token.padding_edges,
            token.spacing,
            token.width,
            token.height,
            token.max_width,
            token.max_height,
            token.center_x,
            token.margin,
            token.align,
            token.distribution,
            token.style.summary()
        ),
        ViewToken::Grid(token) => format!(
            "rows={:?}|columns={:?}|row_spacing={}|column_spacing={}|padding={}|padding_edges={:?}|{:?}|{:?}|{:?}|cells={}",
            token.rows,
            token.columns,
            token.row_spacing,
            token.column_spacing,
            token.padding,
            token.padding_edges,
            token.width,
            token.height,
            token.align,
            token.children.len()
        ),
        ViewToken::Wrap(token) => format!(
            "max_columns={}|spacing={}|run_spacing={}",
            token.max_columns, token.spacing, token.run_spacing
        ),
        ViewToken::Flyout(token) => {
            format!("open={}|{:?}", token.open, token.placement)
        }
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
            format!(
                "layers=[{layers}]|blocking_layers={}|scrim_layers={}",
                token.blocking_layer_count(),
                token.scrim_layer_count()
            )
        }
        ViewToken::AdaptiveSwitch(token) => {
            format!(
                "breakpoint_width={}|resolved_width={:?}|resolved_branch={}",
                token.breakpoint_width,
                token.resolved_width,
                token.resolved_branch_name()
            )
        }
        ViewToken::Lazy(token) => token.key.clone(),
        ViewToken::ScrollView(token) => format!(
            "{:?}|{:?}|scrollbars_visible={}",
            token.horizontal, token.vertical, token.scrollbars_visible
        ),
        ViewToken::Expander(token) => format!(
            "{:?}|title_id={:?}|{:?}|expanded={}|header_style={}|content_style={}|action={:?}|{:?}",
            token.title,
            token.title_id,
            token.description,
            token.expanded,
            token.header_style.summary(),
            token.content_style.summary(),
            token.action.kind(),
            token.icon.as_ref().map(|icon| icon.name)
        ),
        ViewToken::SettingsRow(token) => format!(
            "{:?}|{:?}|{:?}|{:?}|{:?}|margin={:?}|align_x={:?}|content_align_x={:?}|{:?}",
            token.title,
            token.title_id,
            token.description,
            token.description_id,
            token.kind,
            token.margin,
            token.align_x,
            token.content_align_x,
            token.icon.as_ref().map(|icon| icon.name)
        ),
        ViewToken::ResultCard(token) => format!(
            "{}|copy={:?}|speak={:?}|replace={:?}|retry={:?}|toggle={:?}|collapse_transition_ms={}",
            result_item_summary(&token.item),
            token.copy_action.kind(),
            token.speak_action.kind(),
            token.replace_action.kind(),
            token.retry_action.kind(),
            token.toggle_action.kind(),
            token.collapse_transition.duration_ms
        ),
        ViewToken::ResultList(token) => format!(
            "contract={}|items={}|virtualized={}|max_height={:?}|spacing={:?}|padding={:?}|border_width={:?}|collapse_transition_ms={}|{}|{:?}|{:?}|{:?}|{:?}|{:?}",
            token.list_contract_kind().as_str(),
            token.items.len(),
            token.virtualized,
            token.max_height,
            token.spacing,
            token.padding,
            token.border_width,
            token.collapse_transition.duration_ms,
            token
                .items
                .iter()
                .map(result_item_summary)
                .collect::<Vec<_>>()
                .join(","),
            token.copy_action.kind(),
            token.speak_action.kind(),
            token.replace_action.kind(),
            token.retry_action.kind(),
            token.toggle_action.kind()
        ),
        ViewToken::ListView(token) => format!(
            "items={}|selected={:?}|spacing={}|max_height={:?}|virtualized={}|action={:?}|ids={}",
            token.items.len(),
            token.selected,
            token.spacing,
            token.max_height,
            token.virtualized,
            token.action.kind(),
            token
                .items
                .iter()
                .map(|item| item.id.clone())
                .collect::<Vec<_>>()
                .join(",")
        ),
        ViewToken::TrayMenu(token) => format!(
            "items={}|min_width={}|max_height={:?}|radius={}|shadow_margin={}|animation_offset_y={}|item_height={}|ids={}",
            token.items.len(),
            token.min_width,
            token.style.presenter_max_height,
            token.style.presenter_corner_radius,
            token.style.presenter_shadow_margin,
            token.animation_offset_y,
            token.style.item_min_height,
            token
                .items
                .iter()
                .filter(|item| !item.is_separator())
                .map(|item| item.id.clone())
                .collect::<Vec<_>>()
                .join(",")
        ),
        ViewToken::PointerRegion(token) => format!(
            "{:?}|{:?}|move={:?}|left_down={:?}|left_up={:?}|double_click={:?}|right_down={:?}|wheel={:?}|escape={:?}",
            token.width,
            token.height,
            token.move_action.kind(),
            token.left_down_action.kind(),
            token.left_up_action.kind(),
            token.double_click_action.kind(),
            token.right_down_action.kind(),
            token.wheel_action.kind(),
            token.escape_action.kind()
        ),
        ViewToken::CaptureOverlay(token) => format!(
            "{}|depth={}|dragging={}|detected={:?}|selection={:?}|handles={}|magnifier={}|background={:?}|cursor={:?}",
            token.phase,
            token.detection_depth,
            token.dragging,
            token.detected_rect,
            token.selection_rect,
            token.handles_visible,
            token.magnifier_visible,
            token.background_pixel_size(),
            token.cursor
        ),
        ViewToken::Image(token) => format!(
            "bgra_path={:?}|{}x{} px|raster={:?}|{:?}|{:?}|{:?}",
            token.bgra_path,
            token.pixel_width,
            token.pixel_height,
            token.raster_path,
            token.stretch,
            token.width,
            token.height
        ),
        ViewToken::WebView(token) => {
            format!("{:?}|{:?}|{:?}", token.source, token.width, token.height)
        }
        ViewToken::Custom(token) => format!(
            "{}|{}|{:?}",
            token.kind.as_str(),
            token.control,
            token.target_type
        ),
    }
}

fn result_item_summary(item: &ResultItem) -> String {
    format!(
        "{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}",
        item.id,
        item.title,
        item.body,
        item.icon.as_ref().map(|icon| icon.name),
        item.metadata,
        item.pending_hint,
        item.expanded,
        item.toggleable,
        item.dimmed,
        item.status,
        item.header_state,
        item.actions_visible
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::view::{button, column, page, text, IntoView};

    #[derive(Clone)]
    enum Msg {
        Pressed,
    }

    #[test]
    fn detects_structural_updates() {
        let before = page("Home")
            .content(column((text("One"), button("Run").on_press(Msg::Pressed))))
            .into_view();
        let after = page("Home")
            .content(column((text("Two"), button("Run").on_press(Msg::Pressed))))
            .into_view();

        let changes = diff_views(&before, &after);

        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].kind, ViewChangeKind::Updated { kind: "Text" });
    }
}

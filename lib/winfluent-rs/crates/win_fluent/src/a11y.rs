use crate::view::{LayoutKind, View, ViewToken};

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum A11yRole {
    Application,
    Button,
    CheckBox,
    ComboBox,
    Dialog,
    Document,
    Group,
    Hyperlink,
    Image,
    List,
    ListItem,
    MenuItem,
    Navigation,
    Pane,
    ProgressBar,
    RadioButton,
    ScrollView,
    Slider,
    StaticText,
    Tab,
    TabItem,
    TextInput,
    Tooltip,
    Tree,
    TreeItem,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct A11yHint {
    pub name: Option<String>,
    pub description: Option<String>,
    pub role: Option<A11yRole>,
    pub focusable: bool,
    /// Free-form automation status string (maps to WinUI `AutomationProperties.
    /// HelpText`). Used as a UIA test hook, e.g. `SelectedSettingsTab:{Id}`.
    pub help_text: Option<String>,
}

impl A11yHint {
    pub fn named(name: impl Into<String>) -> Self {
        Self {
            name: Some(name.into()),
            ..Self::default()
        }
    }

    pub fn role(mut self, role: A11yRole) -> Self {
        self.role = Some(role);
        self
    }

    pub fn description(mut self, description: impl Into<String>) -> Self {
        self.description = Some(description.into());
        self
    }

    pub fn focusable(mut self, focusable: bool) -> Self {
        self.focusable = focusable;
        self
    }

    pub fn help_text(mut self, help_text: impl Into<String>) -> Self {
        self.help_text = Some(help_text.into());
        self
    }
}

impl Default for A11yHint {
    fn default() -> Self {
        Self {
            name: None,
            description: None,
            role: None,
            focusable: false,
            help_text: None,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct A11yNode {
    pub role: A11yRole,
    pub name: Option<String>,
    pub description: Option<String>,
    pub focusable: bool,
    pub help_text: Option<String>,
    pub children: Vec<A11yNode>,
}

impl A11yNode {
    pub fn new(role: A11yRole) -> Self {
        Self {
            role,
            name: None,
            description: None,
            focusable: false,
            help_text: None,
            children: Vec::new(),
        }
    }

    fn with_hint(mut self, hint: &A11yHint) -> Self {
        if let Some(role) = &hint.role {
            self.role = role.clone();
        }
        if hint.name.is_some() {
            self.name = hint.name.clone();
        }
        if hint.description.is_some() {
            self.description = hint.description.clone();
        }
        if hint.help_text.is_some() {
            self.help_text = hint.help_text.clone();
        }
        self.focusable = self.focusable || hint.focusable;
        self
    }
}

fn tree_node_a11y(node: &crate::view::TreeNode, selected: Option<&str>) -> A11yNode {
    let mut states = Vec::new();
    if !node.children.is_empty() {
        states.push(if node.expanded {
            "expanded"
        } else {
            "collapsed"
        });
    }
    if selected == Some(node.id.as_str()) {
        states.push("selected");
    }

    A11yNode {
        role: A11yRole::TreeItem,
        name: Some(node.label.clone()),
        description: None,
        focusable: true,
        help_text: (!states.is_empty()).then(|| states.join(", ")),
        children: node
            .children
            .iter()
            .map(|child| tree_node_a11y(child, selected))
            .collect(),
    }
}

fn tab_close_a11y_name<Message>(tab: &crate::view::TabItem<Message>) -> String {
    tab.close_a11y_name
        .clone()
        .unwrap_or_else(|| format!("Close {}", tab.header))
}

fn command_a11y_node<Message>(command: &crate::command::CommandToken<Message>) -> A11yNode {
    let mut help_parts = vec![format!("placement={:?}", command.placement)];
    if let Some(keyboard) = &command.keyboard {
        let accelerator = if keyboard.modifiers.is_empty() {
            keyboard.key.clone()
        } else {
            format!("{}+{}", keyboard.modifiers.join("+"), keyboard.key)
        };
        help_parts.push(format!("keyboard={accelerator}"));
    }
    if !command.enabled {
        help_parts.push("disabled".to_string());
    }

    A11yNode {
        role: A11yRole::Button,
        name: Some(command.label.clone()),
        description: None,
        focusable: command.enabled,
        help_text: Some(help_parts.join(", ")),
        children: Vec::new(),
    }
}

fn set_default_help_text(node: &mut A11yNode, value: impl Into<String>) {
    if node.help_text.is_none() {
        node.help_text = Some(value.into());
    }
}

pub fn resolve_accessibility_tree<Message>(view: &View<Message>) -> A11yNode {
    match view.token() {
        ViewToken::Page(token) => {
            let mut node = A11yNode::new(A11yRole::Application).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.title.clone()));
            if let Some(content) = &token.content {
                node.children.push(resolve_accessibility_tree(content));
            }
            node.children
                .extend(token.commands.iter().map(command_a11y_node));
            node
        }
        ViewToken::Text(token) => {
            let mut node = A11yNode::new(A11yRole::StaticText).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.value.clone()));
            node
        }
        ViewToken::RichText(token) => {
            let mut node = A11yNode::new(A11yRole::StaticText).with_hint(&token.a11y);
            let combined = token
                .runs
                .iter()
                .map(|run| run.text.as_str())
                .collect::<String>();
            node.name = token.a11y.name.clone().or(Some(combined));
            node.children = token
                .runs
                .iter()
                .filter(|run| matches!(run.kind, crate::view::TextRunKind::Link))
                .map(|run| A11yNode {
                    role: A11yRole::Hyperlink,
                    name: Some(run.text.clone()),
                    description: run.href.clone(),
                    focusable: true,
                    help_text: None,
                    children: Vec::new(),
                })
                .collect();
            node
        }
        ViewToken::TitleBar(token) => {
            let mut node = A11yNode::new(A11yRole::Pane).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.title.clone()));
            node.children = token
                .commands
                .iter()
                .map(resolve_accessibility_tree)
                .collect();
            node
        }
        ViewToken::Button(token) => {
            let default_role = if matches!(token.kind, crate::view::ButtonKind::Link) {
                A11yRole::Hyperlink
            } else {
                A11yRole::Button
            };
            let mut node = A11yNode::new(default_role).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.label.clone()));
            node.focusable = token.state.is_focusable();
            node
        }
        ViewToken::ToggleButton(token) => {
            let mut node = A11yNode::new(A11yRole::Button).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.label.clone()));
            node.focusable = token.state.is_focusable();
            if token.pressed {
                node.help_text = node.help_text.or_else(|| Some("pressed".to_string()));
            }
            node
        }
        ViewToken::SplitButton(token) => {
            let mut node = A11yNode::new(A11yRole::Button).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.label.clone()));
            node.focusable = token.state.is_focusable();
            node.children
                .extend(token.items.iter().map(|item| A11yNode {
                    role: A11yRole::MenuItem,
                    name: Some(item.label.clone()),
                    description: None,
                    focusable: item.enabled,
                    help_text: None,
                    children: Vec::new(),
                }));
            node
        }
        ViewToken::FlyoutButton(token) => {
            let mut node = A11yNode::new(A11yRole::Button).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.label.clone()));
            node.focusable = token.state.is_focusable();
            node.children
                .extend(token.items.iter().map(|item| A11yNode {
                    role: A11yRole::ListItem,
                    name: Some(item.label.clone()),
                    description: None,
                    focusable: item.enabled,
                    help_text: None,
                    children: Vec::new(),
                }));
            node
        }
        ViewToken::StatusBadge(token) => {
            let mut node = A11yNode::new(A11yRole::StaticText).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.automation_name()));
            node
        }
        ViewToken::InfoBar(token) => {
            let mut node = A11yNode::new(A11yRole::StaticText).with_hint(&token.a11y);
            node.name = token.a11y.name.clone().or_else(|| {
                let combined = if token.message.is_empty() {
                    token.title.clone()
                } else {
                    format!("{}. {}", token.title, token.message)
                };
                Some(combined)
            });
            node
        }
        ViewToken::ProgressRing(token) => {
            let mut node = A11yNode::new(A11yRole::ProgressBar).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| token.label.clone())
                .or_else(|| Some("Loading".to_string()));
            set_default_help_text(
                &mut node,
                if token.active {
                    "indeterminate, active"
                } else {
                    "indeterminate, inactive"
                },
            );
            node
        }
        ViewToken::ProgressBar(token) => {
            let mut node = A11yNode::new(A11yRole::ProgressBar).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| token.label.clone())
                .or_else(|| Some("Progress".to_string()));
            let value = token
                .value
                .map(|value| format!("{value:.2}"))
                .unwrap_or_else(|| "indeterminate".to_string());
            set_default_help_text(
                &mut node,
                format!(
                    "value={value}, min=0.00, max=100.00, active={}",
                    token.active
                ),
            );
            node
        }
        ViewToken::BusyOverlay(token) => {
            let mut node = A11yNode::new(A11yRole::Pane).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| token.label.clone())
                .or_else(|| token.active.then(|| "Loading".to_string()));
            set_default_help_text(
                &mut node,
                format!(
                    "active={}, blocks_input={}",
                    token.active, token.blocks_input
                ),
            );
            node.children
                .push(resolve_accessibility_tree(&token.content));
            if token.active {
                node.children.push(A11yNode {
                    role: A11yRole::StaticText,
                    name: token.label.clone().or_else(|| Some("Loading".to_string())),
                    description: None,
                    focusable: false,
                    help_text: None,
                    children: Vec::new(),
                });
            }
            node
        }
        ViewToken::Card(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.title.clone()));
            if node.description.is_none() {
                node.description = token.description.clone();
            }
            if let Some(content) = &token.content {
                node.children.push(resolve_accessibility_tree(content));
            }
            node.children
                .extend(token.trailing.iter().map(resolve_accessibility_tree));
            node
        }
        ViewToken::Spacer(_) => A11yNode::new(A11yRole::StaticText),
        ViewToken::TextEditor(token) => {
            let mut node = A11yNode::new(A11yRole::TextInput).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| token.placeholder.clone())
                .or_else(|| token.id.clone());
            node.focusable = token.state.is_focusable();
            let mut state = Vec::new();
            if token.secure {
                state.push("secure");
            }
            if token.read_only {
                state.push("read-only");
            }
            if !state.is_empty() {
                set_default_help_text(&mut node, state.join(", "));
            }
            node
        }
        ViewToken::ToggleSwitch(token) => {
            let mut node = A11yNode::new(A11yRole::CheckBox).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.label.clone()));
            node.focusable = token.state.is_focusable();
            node
        }
        ViewToken::CheckBox(token) => {
            let mut node = A11yNode::new(A11yRole::CheckBox).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.label.clone()));
            node.focusable = token.state.is_focusable();
            node
        }
        ViewToken::RadioGroup(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.name = token.a11y.name.clone().or_else(|| token.header.clone());
            node.children = token
                .options
                .iter()
                .map(|option| A11yNode {
                    role: A11yRole::RadioButton,
                    name: Some(option.label.clone()),
                    description: None,
                    focusable: option.enabled && token.state.is_focusable(),
                    help_text: (token.selected.as_deref() == Some(option.id.as_str()))
                        .then(|| "selected".to_string()),
                    children: Vec::new(),
                })
                .collect();
            node
        }
        ViewToken::Slider(token) => {
            let mut node = A11yNode::new(A11yRole::Slider).with_hint(&token.a11y);
            node.name = token.a11y.name.clone().or_else(|| token.id.clone());
            node.focusable = token.state.is_focusable();
            set_default_help_text(
                &mut node,
                format!(
                    "value={:.2}, min={:.2}, max={:.2}, step={:.2}, preview_active={}",
                    token.value,
                    token.min,
                    token.max,
                    token.step,
                    token.preview_active()
                ),
            );
            node
        }
        ViewToken::NumberBox(token) => {
            let mut node = A11yNode::new(A11yRole::TextInput).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| token.header.clone())
                .or_else(|| token.placeholder.clone())
                .or_else(|| token.id.clone());
            node.focusable = token.state.is_focusable();
            node
        }
        ViewToken::AutoSuggestBox(token) => {
            let mut node = A11yNode::new(A11yRole::ComboBox).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| token.header.clone())
                .or_else(|| token.placeholder.clone())
                .or_else(|| token.id.clone());
            node.focusable = token.state.is_focusable();
            node.children = token
                .suggestions
                .iter()
                .map(|suggestion| A11yNode {
                    role: A11yRole::ListItem,
                    name: Some(suggestion.clone()),
                    description: None,
                    focusable: true,
                    help_text: None,
                    children: Vec::new(),
                })
                .collect();
            node
        }
        ViewToken::ComboBox(token) => {
            let mut node = A11yNode::new(A11yRole::ComboBox).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| token.label.clone())
                .or_else(|| token.id.clone());
            node.focusable = token.state.is_focusable();
            if let Some(selected) = token.selected_item() {
                set_default_help_text(&mut node, format!("selected={}", selected.label));
            }
            node.children = token
                .items
                .iter()
                .map(|item| A11yNode {
                    role: A11yRole::ListItem,
                    name: Some(item.label.clone()),
                    description: None,
                    focusable: token.state.is_focusable(),
                    help_text: (token.selected.as_deref() == Some(item.id.as_str()))
                        .then(|| "selected".to_string()),
                    children: Vec::new(),
                })
                .collect();
            node
        }
        ViewToken::CommandBar(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.children = token.items.iter().map(resolve_accessibility_tree).collect();
            node
        }
        ViewToken::NavigationView(token) => {
            let mut node = A11yNode::new(A11yRole::Navigation).with_hint(&token.a11y);
            let nav_item = |item: &crate::view::NavigationItem| A11yNode {
                role: A11yRole::ListItem,
                name: Some(item.label.clone()),
                description: None,
                focusable: true,
                help_text: None,
                children: Vec::new(),
            };
            node.children.extend(token.items.iter().map(nav_item));
            node.children
                .extend(token.footer_items.iter().map(nav_item));
            if token.settings_visible {
                node.children.push(A11yNode {
                    role: A11yRole::ListItem,
                    name: Some("Settings".to_string()),
                    description: None,
                    focusable: true,
                    help_text: None,
                    children: Vec::new(),
                });
            }
            if let Some(content) = &token.content {
                node.children.push(resolve_accessibility_tree(content));
            }
            node
        }
        ViewToken::Dialog(token) => {
            let mut node = A11yNode::new(A11yRole::Dialog).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.title.clone()));
            if let Some(content) = &token.content {
                node.children.push(resolve_accessibility_tree(content));
            }
            if let Some(primary) = &token.primary {
                node.children.push(command_a11y_node(primary));
            }
            if let Some(secondary) = &token.secondary {
                node.children.push(command_a11y_node(secondary));
            }
            node
        }
        ViewToken::Layout(token) => {
            let role = match token.kind {
                LayoutKind::Column | LayoutKind::Row => A11yRole::Group,
            };
            let mut node = A11yNode::new(role).with_hint(&token.a11y);
            node.children = token
                .children
                .iter()
                .map(resolve_accessibility_tree)
                .collect();
            node
        }
        ViewToken::Grid(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.children = token
                .children
                .iter()
                .map(|child| resolve_accessibility_tree(&child.view))
                .collect();
            node
        }
        ViewToken::Wrap(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.children = token
                .children
                .iter()
                .map(resolve_accessibility_tree)
                .collect();
            node
        }
        ViewToken::Border(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.children
                .push(resolve_accessibility_tree(&token.content));
            node
        }
        ViewToken::Viewbox(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.children
                .push(resolve_accessibility_tree(&token.content));
            node
        }
        ViewToken::TabView(token) => {
            let mut node = A11yNode::new(A11yRole::Tab).with_hint(&token.a11y);
            for tab in &token.tabs {
                let mut tab_node = A11yNode {
                    role: A11yRole::TabItem,
                    name: Some(tab.header.clone()),
                    description: None,
                    focusable: true,
                    help_text: (token.selected.as_deref() == Some(tab.id.as_str()))
                        .then(|| "selected".to_string()),
                    children: Vec::new(),
                };
                if tab.closable {
                    tab_node.children.push(A11yNode {
                        role: A11yRole::Button,
                        name: Some(tab_close_a11y_name(tab)),
                        description: None,
                        focusable: true,
                        help_text: Some("close tab".to_string()),
                        children: Vec::new(),
                    });
                }
                node.children.push(tab_node);
            }
            if let Some(selected) = &token.selected {
                if let Some(tab) = token.tabs.iter().find(|tab| &tab.id == selected) {
                    node.children.push(resolve_accessibility_tree(&tab.content));
                }
            } else if let Some(tab) = token.tabs.first() {
                node.children.push(resolve_accessibility_tree(&tab.content));
            }
            node
        }
        ViewToken::TreeView(token) => {
            let mut node = A11yNode::new(A11yRole::Tree).with_hint(&token.a11y);
            node.children = token
                .roots
                .iter()
                .map(|root| tree_node_a11y(root, token.selected.as_deref()))
                .collect();
            node
        }
        ViewToken::Flyout(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.help_text = Some(format!(
                "light-dismiss={:?}; focus={:?}",
                token.light_dismiss, token.focus_behavior
            ));
            node.children
                .push(resolve_accessibility_tree(&token.anchor));
            if token.open {
                node.children
                    .push(resolve_accessibility_tree(&token.content));
            }
            node
        }
        ViewToken::Overlay(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            set_default_help_text(
                &mut node,
                format!(
                    "layers={}, blocking_layers={}, scrim_layers={}",
                    token.layers.len(),
                    token.blocking_layer_count(),
                    token.scrim_layer_count()
                ),
            );
            node.children.push(resolve_accessibility_tree(&token.base));
            node.children.extend(
                token
                    .layers
                    .iter()
                    .map(|layer| resolve_accessibility_tree(&layer.content)),
            );
            node
        }
        ViewToken::AdaptiveSwitch(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            set_default_help_text(
                &mut node,
                format!(
                    "breakpoint_width={}, resolved_width={:?}, resolved_branch={}",
                    token.breakpoint_width,
                    token.resolved_width,
                    token.resolved_branch_name()
                ),
            );
            match token.resolved_branch() {
                Some(branch) => node.children.push(resolve_accessibility_tree(branch)),
                None => {
                    node.children.push(resolve_accessibility_tree(&token.wide));
                    node.children
                        .push(resolve_accessibility_tree(&token.narrow));
                }
            }
            node
        }
        ViewToken::Lazy(token) => {
            let mut node = resolve_accessibility_tree(&token.content);
            if token.a11y.role.is_some()
                || token.a11y.name.is_some()
                || token.a11y.description.is_some()
                || token.a11y.focusable
            {
                node = node.with_hint(&token.a11y);
            }
            node
        }
        ViewToken::ScrollView(token) => {
            let mut node = A11yNode::new(A11yRole::ScrollView).with_hint(&token.a11y);
            if let Some(content) = &token.content {
                node.children.push(resolve_accessibility_tree(content));
            }
            node
        }
        ViewToken::Expander(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.title.clone()));
            set_default_help_text(
                &mut node,
                if token.expanded {
                    "expanded"
                } else {
                    "collapsed"
                },
            );
            if token.expanded {
                if let Some(content) = &token.content {
                    node.children.push(resolve_accessibility_tree(content));
                }
            }
            node.children
                .extend(token.trailing.iter().map(resolve_accessibility_tree));
            node
        }
        ViewToken::SettingsRow(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.title.clone()));
            if node.description.is_none() {
                node.description = token.description.clone();
            }
            if node.help_text.is_none() {
                node.help_text = Some(format!(
                    "SettingsRow:content={},trailing={}",
                    token.content.is_some(),
                    token.trailing.len()
                ));
            }
            if let Some(content) = &token.content {
                node.children.push(resolve_accessibility_tree(content));
            }
            node.children
                .extend(token.trailing.iter().map(resolve_accessibility_tree));
            node
        }
        ViewToken::ResultCard(token) => {
            let mut node = A11yNode::new(A11yRole::ListItem).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.item.title.clone()));
            node
        }
        ViewToken::ResultList(token) => {
            let mut node = A11yNode::new(A11yRole::List).with_hint(&token.a11y);
            if node.help_text.is_none() {
                node.help_text = Some(format!(
                    "ListContract:{},virtualized={},collapse_transition_ms={}",
                    token.list_contract_kind().as_str(),
                    token.virtualized,
                    token.collapse_transition.duration_ms
                ));
            }
            node.children
                .extend(token.items.iter().map(|item| A11yNode {
                    role: A11yRole::ListItem,
                    name: Some(item.title.clone()),
                    description: Some(item.body.clone()),
                    focusable: false,
                    help_text: None,
                    children: Vec::new(),
                }));
            node
        }
        ViewToken::ListView(token) => {
            let mut node = A11yNode::new(A11yRole::List).with_hint(&token.a11y);
            node.children = token
                .items
                .iter()
                .map(|item| {
                    let mut child = resolve_accessibility_tree(&item.view);
                    if child.role == A11yRole::StaticText || child.role == A11yRole::Group {
                        child.role = A11yRole::ListItem;
                    }
                    child
                })
                .collect();
            node
        }
        ViewToken::TrayMenu(token) => {
            let mut node = A11yNode::new(A11yRole::List).with_hint(&token.a11y);
            node.children = token
                .items
                .iter()
                .filter(|item| !item.is_separator())
                .map(|item| A11yNode {
                    role: A11yRole::MenuItem,
                    name: Some(item.label.clone()),
                    description: item.tooltip.clone(),
                    focusable: item.enabled,
                    help_text: item.is_submenu().then(|| "submenu".to_string()),
                    children: Vec::new(),
                })
                .collect();
            node
        }
        ViewToken::PointerRegion(token) => {
            let mut node = A11yNode::new(A11yRole::Pane).with_hint(&token.a11y);
            node.name = token.a11y.name.clone().or_else(|| token.id.clone());
            node.children
                .push(resolve_accessibility_tree(&token.content));
            node
        }
        ViewToken::CaptureOverlay(token) => {
            let mut node = A11yNode::new(A11yRole::Pane).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some("OCR capture overlay".to_string()));
            node.description = Some(format!(
                "phase={}, depth={}, selection={:?}, detected={:?}, handles={}, magnifier={}, background={:?}, cursor={:?}",
                token.phase,
                token.detection_depth,
                token.selection_rect,
                token.detected_rect,
                token.handles_visible,
                token.magnifier_visible,
                token.background_pixel_size(),
                token.cursor
            ));
            node
        }
        ViewToken::Image(token) => {
            let mut node = A11yNode::new(A11yRole::Image).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some("Image".to_string()));
            node
        }
        ViewToken::WebView(token) => {
            let mut node = A11yNode::new(A11yRole::Document).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some("Web content".to_string()));
            node
        }
        ViewToken::Custom(token) => {
            let mut node = A11yNode::new(A11yRole::Pane).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.control.clone()));
            if node.help_text.is_none() {
                node.help_text = Some(format!(
                    "CustomControlKind:{},target_type={}",
                    token.kind.as_str(),
                    token.target_type.as_deref().unwrap_or("none")
                ));
            }
            node.children = token
                .children
                .iter()
                .map(resolve_accessibility_tree)
                .collect();
            node
        }
    }
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
    fn resolves_accessibility_roles_from_token_tree() {
        let view = page("Window")
            .content(column((text("Label"), button("Go").on_press(Msg::Pressed))))
            .into_view();

        let tree = resolve_accessibility_tree(&view);

        assert_eq!(tree.role, A11yRole::Application);
        assert_eq!(tree.children[0].role, A11yRole::Group);
        assert_eq!(tree.children[0].children[1].role, A11yRole::Button);
    }

    #[test]
    fn link_buttons_map_to_hyperlink_role() {
        let view = button::<Msg>("Open docs")
            .link()
            .on_press(Msg::Pressed)
            .into_view();

        let tree = resolve_accessibility_tree(&view);

        assert_eq!(tree.role, A11yRole::Hyperlink);
    }

    #[test]
    fn progress_ring_maps_to_progress_bar_role() {
        let view = crate::view::progress_ring::<Msg>().active(true).into_view();

        let tree = resolve_accessibility_tree(&view);

        assert_eq!(tree.role, A11yRole::ProgressBar);
    }
}

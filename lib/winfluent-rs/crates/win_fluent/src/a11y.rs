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
    A11yNode {
        role: A11yRole::TreeItem,
        name: Some(node.label.clone()),
        description: None,
        focusable: true,
        help_text: (selected == Some(node.id.as_str())).then(|| "selected".to_string()),
        children: node
            .children
            .iter()
            .map(|child| tree_node_a11y(child, selected))
            .collect(),
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
            node.children.extend(token.items.iter().map(|item| A11yNode {
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
                .or_else(|| Some(token.label.clone()));
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
            node
        }
        ViewToken::BusyOverlay(token) => {
            let mut node = A11yNode::new(A11yRole::Pane).with_hint(&token.a11y);
            node.name = token.a11y.name.clone().or_else(|| token.label.clone());
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
            node.focusable = token.state.is_focusable() && !token.read_only;
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
            node.children.extend(token.footer_items.iter().map(nav_item));
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
            node.children.push(resolve_accessibility_tree(&token.content));
            node
        }
        ViewToken::Viewbox(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
            node.children.push(resolve_accessibility_tree(&token.content));
            node
        }
        ViewToken::TabView(token) => {
            let mut node = A11yNode::new(A11yRole::Tab).with_hint(&token.a11y);
            for tab in &token.tabs {
                node.children.push(A11yNode {
                    role: A11yRole::TabItem,
                    name: Some(tab.header.clone()),
                    description: None,
                    focusable: true,
                    help_text: (token.selected.as_deref() == Some(tab.id.as_str()))
                        .then(|| "selected".to_string()),
                    children: Vec::new(),
                });
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
            node.children.push(resolve_accessibility_tree(&token.anchor));
            if token.open {
                node.children.push(resolve_accessibility_tree(&token.content));
            }
            node
        }
        ViewToken::Overlay(token) => {
            let mut node = A11yNode::new(A11yRole::Group).with_hint(&token.a11y);
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
                "phase={}, depth={}, selection={:?}, detected={:?}",
                token.phase, token.detection_depth, token.selection_rect, token.detected_rect
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
            node.name = token.a11y.name.clone().or_else(|| Some("Web content".to_string()));
            node
        }
        ViewToken::Custom(token) => {
            let mut node = A11yNode::new(A11yRole::Pane).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.control.clone()));
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

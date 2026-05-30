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
    List,
    ListItem,
    Navigation,
    Pane,
    ScrollView,
    StaticText,
    TextInput,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct A11yHint {
    pub name: Option<String>,
    pub description: Option<String>,
    pub role: Option<A11yRole>,
    pub focusable: bool,
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
}

impl Default for A11yHint {
    fn default() -> Self {
        Self {
            name: None,
            description: None,
            role: None,
            focusable: false,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct A11yNode {
    pub role: A11yRole,
    pub name: Option<String>,
    pub description: Option<String>,
    pub focusable: bool,
    pub children: Vec<A11yNode>,
}

impl A11yNode {
    pub fn new(role: A11yRole) -> Self {
        Self {
            role,
            name: None,
            description: None,
            focusable: false,
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
        self.focusable = self.focusable || hint.focusable;
        self
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
            let mut node = A11yNode::new(A11yRole::Button).with_hint(&token.a11y);
            node.name = token
                .a11y
                .name
                .clone()
                .or_else(|| Some(token.label.clone()));
            node.focusable = token.state.is_focusable();
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
            node.children
                .extend(token.items.iter().map(|item| A11yNode {
                    role: A11yRole::ListItem,
                    name: Some(item.label.clone()),
                    description: None,
                    focusable: true,
                    children: Vec::new(),
                }));
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
                    children: Vec::new(),
                }));
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
}

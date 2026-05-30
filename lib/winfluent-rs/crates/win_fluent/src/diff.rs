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
        ViewToken::CommandBar(token) => token.items.iter().collect(),
        ViewToken::NavigationView(token) => token.content.iter().map(Box::as_ref).collect(),
        ViewToken::Dialog(token) => token.content.iter().map(Box::as_ref).collect(),
        ViewToken::Layout(token) => token.children.iter().collect(),
        ViewToken::Lazy(token) => vec![token.content.as_ref()],
        ViewToken::ScrollView(token) => token.content.iter().map(Box::as_ref).collect(),
        ViewToken::SettingsRow(token) => {
            let mut children: Vec<&View<Message>> = token.content.iter().map(Box::as_ref).collect();
            children.extend(token.trailing.iter());
            children
        }
        ViewToken::Custom(token) => token.children.iter().collect(),
        ViewToken::Text(_)
        | ViewToken::Button(_)
        | ViewToken::TextEditor(_)
        | ViewToken::ToggleSwitch(_)
        | ViewToken::ComboBox(_)
        | ViewToken::ServiceResultCard(_)
        | ViewToken::ServiceResultList(_) => Vec::new(),
    }
}

fn token_kind<Message>(token: &ViewToken<Message>) -> &'static str {
    match token {
        ViewToken::Page(_) => "Page",
        ViewToken::Text(_) => "Text",
        ViewToken::Button(_) => "Button",
        ViewToken::TextEditor(_) => "TextEditor",
        ViewToken::ToggleSwitch(_) => "ToggleSwitch",
        ViewToken::ComboBox(_) => "ComboBox",
        ViewToken::CommandBar(_) => "CommandBar",
        ViewToken::NavigationView(_) => "NavigationView",
        ViewToken::Dialog(_) => "Dialog",
        ViewToken::Layout(token) => match token.kind {
            LayoutKind::Column => "Column",
            LayoutKind::Row => "Row",
        },
        ViewToken::Lazy(_) => "Lazy",
        ViewToken::ScrollView(_) => "ScrollView",
        ViewToken::SettingsRow(_) => "SettingsRow",
        ViewToken::ServiceResultCard(_) => "ServiceResultCard",
        ViewToken::ServiceResultList(_) => "ServiceResultList",
        ViewToken::Custom(_) => "Custom",
    }
}

fn token_id<Message>(token: &ViewToken<Message>) -> Option<&str> {
    match token {
        ViewToken::Page(token) => token.id.as_deref(),
        ViewToken::Text(token) => token.id.as_deref(),
        ViewToken::Button(token) => token.id.as_deref(),
        ViewToken::TextEditor(token) => token.id.as_deref(),
        ViewToken::ToggleSwitch(token) => token.id.as_deref(),
        ViewToken::ComboBox(token) => token.id.as_deref(),
        ViewToken::CommandBar(token) => token.id.as_deref(),
        ViewToken::NavigationView(token) => token.id.as_deref(),
        ViewToken::Dialog(token) => token.id.as_deref(),
        ViewToken::Layout(token) => token.id.as_deref(),
        ViewToken::Lazy(token) => token.id.as_deref().or(Some(token.key.as_str())),
        ViewToken::ScrollView(token) => token.id.as_deref(),
        ViewToken::SettingsRow(token) => token.id.as_deref(),
        ViewToken::ServiceResultCard(token) => token.id.as_deref(),
        ViewToken::ServiceResultList(token) => token.id.as_deref(),
        ViewToken::Custom(token) => token.id.as_deref(),
    }
}

fn token_summary<Message>(token: &ViewToken<Message>) -> String {
    match token {
        ViewToken::Page(token) => format!("{:?}|commands={}", token.title, token.commands.len()),
        ViewToken::Text(token) => {
            format!("{:?}|{:?}|{}", token.value, token.style, token.selectable)
        }
        ViewToken::Button(token) => format!(
            "{:?}|{:?}|{:?}|{}|{:?}",
            token.label,
            token.kind,
            token.icon.as_ref().map(|icon| icon.name),
            token.state,
            token.action.kind()
        ),
        ViewToken::TextEditor(token) => format!(
            "{:?}|{:?}|{:?}|{}|{}|{:?}",
            token.placeholder,
            token.min_height,
            token.max_height,
            token.read_only,
            token.state,
            token.action.kind()
        ),
        ViewToken::ToggleSwitch(token) => format!(
            "{:?}|{}|{}|{:?}",
            token.label,
            token.checked,
            token.state,
            token.action.kind()
        ),
        ViewToken::ComboBox(token) => format!(
            "{:?}|{:?}|items={}|{}|{:?}",
            token.label,
            token.selected,
            token.items.len(),
            token.state,
            token.action.kind()
        ),
        ViewToken::CommandBar(token) => format!("compact={}", token.compact),
        ViewToken::NavigationView(token) => {
            format!(
                "{:?}|items={}|{:?}",
                token.selected,
                token.items.len(),
                token.action.kind()
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
            "padding={}|spacing={}|{:?}|{:?}|{:?}",
            token.padding, token.spacing, token.width, token.height, token.align
        ),
        ViewToken::Lazy(token) => token.key.clone(),
        ViewToken::ScrollView(token) => format!("{:?}|{:?}", token.horizontal, token.vertical),
        ViewToken::SettingsRow(token) => format!(
            "{:?}|{:?}|{:?}|{:?}",
            token.title,
            token.description,
            token.kind,
            token.icon.as_ref().map(|icon| icon.name)
        ),
        ViewToken::ServiceResultCard(token) => result_item_summary(&token.item),
        ViewToken::ServiceResultList(token) => format!(
            "items={}|virtualized={}|{}|{:?}|{:?}",
            token.items.len(),
            token.virtualized,
            token
                .items
                .iter()
                .map(result_item_summary)
                .collect::<Vec<_>>()
                .join(","),
            token.copy_action.kind(),
            token.speak_action.kind()
        ),
        ViewToken::Custom(token) => token.control.clone(),
    }
}

fn result_item_summary(item: &ResultItem) -> String {
    format!(
        "{:?}|{:?}|{:?}|{:?}",
        item.id, item.title, item.body, item.status
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

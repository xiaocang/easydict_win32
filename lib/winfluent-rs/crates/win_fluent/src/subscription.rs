use std::sync::Arc;

use crate::platform::Hotkey;
use crate::theme::ThemeMode;
use crate::window::WindowId;

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SubscriptionKind {
    Hotkey(Hotkey),
    Clipboard,
    NamedEvent { name: String, auto_reset: bool },
    Theme,
    Tray,
    Window(WindowId),
    Custom(String),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum WindowEvent {
    Opened(WindowId),
    CloseRequested(WindowId),
    Closed(WindowId),
    Focused(WindowId),
    DpiChanged(WindowId),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PlatformEvent {
    HotkeyPressed(String),
    ClipboardChanged,
    NamedEventSignaled(String),
    ThemeChanged(ThemeMode),
    TrayCommand(String),
    Window(WindowEvent),
    Custom { kind: String, value: String },
}

#[derive(Clone)]
pub enum Subscription<Message> {
    None,
    Event {
        kind: SubscriptionKind,
        map: Arc<dyn Fn(PlatformEvent) -> Option<Message> + Send + Sync + 'static>,
    },
    Batch(Vec<Subscription<Message>>),
}

impl<Message> Subscription<Message> {
    pub const fn none() -> Self {
        Self::None
    }

    pub fn event(
        kind: SubscriptionKind,
        map: impl Fn(PlatformEvent) -> Option<Message> + Send + Sync + 'static,
    ) -> Self {
        Self::Event {
            kind,
            map: Arc::new(map),
        }
    }

    pub fn hotkey(hotkey: Hotkey, map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        Self::event(SubscriptionKind::Hotkey(hotkey), move |event| match event {
            PlatformEvent::HotkeyPressed(id) => Some(map(id)),
            _ => None,
        })
    }

    pub fn clipboard(map: impl Fn() -> Message + Send + Sync + 'static) -> Self {
        Self::event(SubscriptionKind::Clipboard, move |event| match event {
            PlatformEvent::ClipboardChanged => Some(map()),
            _ => None,
        })
    }

    pub fn named_event(
        name: impl Into<String>,
        auto_reset: bool,
        map: impl Fn(String) -> Message + Send + Sync + 'static,
    ) -> Self {
        Self::event(
            SubscriptionKind::NamedEvent {
                name: name.into(),
                auto_reset,
            },
            move |event| match event {
                PlatformEvent::NamedEventSignaled(name) => Some(map(name)),
                _ => None,
            },
        )
    }

    pub fn theme(map: impl Fn(ThemeMode) -> Message + Send + Sync + 'static) -> Self {
        Self::event(SubscriptionKind::Theme, move |event| match event {
            PlatformEvent::ThemeChanged(mode) => Some(map(mode)),
            _ => None,
        })
    }

    pub fn tray(map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        Self::event(SubscriptionKind::Tray, move |event| match event {
            PlatformEvent::TrayCommand(id) => Some(map(id)),
            _ => None,
        })
    }

    pub fn batch(values: impl IntoIterator<Item = Subscription<Message>>) -> Self {
        let mut subscriptions = Vec::new();
        for value in values {
            match value {
                Subscription::None => {}
                Subscription::Batch(inner) => subscriptions.extend(inner),
                other => subscriptions.push(other),
            }
        }

        match subscriptions.len() {
            0 => Subscription::None,
            1 => subscriptions.pop().expect("length checked"),
            _ => Subscription::Batch(subscriptions),
        }
    }

    pub fn is_none(&self) -> bool {
        matches!(self, Self::None)
    }
}

impl<Message> Default for Subscription<Message> {
    fn default() -> Self {
        Self::None
    }
}

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

impl WindowEvent {
    pub fn window_id(&self) -> &WindowId {
        match self {
            Self::Opened(id)
            | Self::CloseRequested(id)
            | Self::Closed(id)
            | Self::Focused(id)
            | Self::DpiChanged(id) => id,
        }
    }
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

    pub fn window(
        id: impl Into<WindowId>,
        map: impl Fn(WindowEvent) -> Message + Send + Sync + 'static,
    ) -> Self {
        let id = id.into();
        Self::event(
            SubscriptionKind::Window(id.clone()),
            move |event| match event {
                PlatformEvent::Window(window_event) if window_event.window_id() == &id => {
                    Some(map(window_event))
                }
                _ => None,
            },
        )
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

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Clone, Debug, Eq, PartialEq)]
    enum Msg {
        Window(WindowEvent),
        Clipboard,
        Named(String),
    }

    #[test]
    fn window_subscription_maps_window_events() {
        let subscription = Subscription::window("main", Msg::Window);

        let Subscription::Event { kind, map } = subscription else {
            panic!("expected window event subscription");
        };

        assert_eq!(kind, SubscriptionKind::Window(WindowId::new("main")));
        assert_eq!(
            map(PlatformEvent::Window(WindowEvent::CloseRequested(
                WindowId::new("main"),
            ))),
            Some(Msg::Window(WindowEvent::CloseRequested(WindowId::new(
                "main"
            ))))
        );
        assert_eq!(
            map(PlatformEvent::Window(WindowEvent::CloseRequested(
                WindowId::new("mini"),
            ))),
            None
        );
        assert_eq!(map(PlatformEvent::ClipboardChanged), None);
    }

    #[test]
    fn batch_flattens_nested_subscriptions_and_discards_none() {
        let subscription = Subscription::batch([
            Subscription::none(),
            Subscription::batch([Subscription::clipboard(|| Msg::Clipboard)]),
            Subscription::named_event("Local\\Test", true, Msg::Named),
        ]);

        let Subscription::Batch(values) = subscription else {
            panic!("expected batch");
        };

        assert_eq!(values.len(), 2);
    }

    #[test]
    fn batch_returns_single_event_without_extra_wrapper() {
        let subscription = Subscription::batch([
            Subscription::none(),
            Subscription::clipboard(|| Msg::Clipboard),
        ]);

        let Subscription::Event { kind, map } = subscription else {
            panic!("expected single event");
        };

        assert_eq!(kind, SubscriptionKind::Clipboard);
        assert_eq!(map(PlatformEvent::ClipboardChanged), Some(Msg::Clipboard));
    }

    #[test]
    fn named_event_subscription_maps_only_matching_event_kind() {
        let subscription = Subscription::named_event("Local\\Wake", true, Msg::Named);

        let Subscription::Event { kind, map } = subscription else {
            panic!("expected named event subscription");
        };

        assert_eq!(
            kind,
            SubscriptionKind::NamedEvent {
                name: "Local\\Wake".to_string(),
                auto_reset: true
            }
        );
        assert_eq!(
            map(PlatformEvent::NamedEventSignaled("Local\\Wake".to_string())),
            Some(Msg::Named("Local\\Wake".to_string()))
        );
        assert_eq!(map(PlatformEvent::ClipboardChanged), None);
    }
}

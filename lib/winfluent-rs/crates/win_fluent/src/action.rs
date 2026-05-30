use std::fmt;
use std::sync::Arc;

#[derive(Clone)]
pub enum Action<Message> {
    None,
    Message(Message),
    TextInput(Arc<dyn Fn(String) -> Message + Send + Sync + 'static>),
    BoolInput(Arc<dyn Fn(bool) -> Message + Send + Sync + 'static>),
    SelectionInput(Arc<dyn Fn(String) -> Message + Send + Sync + 'static>),
}

impl<Message> Action<Message> {
    pub const fn none() -> Self {
        Self::None
    }

    pub fn message(message: Message) -> Self {
        Self::Message(message)
    }

    pub fn text_input(map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        Self::TextInput(Arc::new(map))
    }

    pub fn bool_input(map: impl Fn(bool) -> Message + Send + Sync + 'static) -> Self {
        Self::BoolInput(Arc::new(map))
    }

    pub fn selection_input(map: impl Fn(String) -> Message + Send + Sync + 'static) -> Self {
        Self::SelectionInput(Arc::new(map))
    }

    pub const fn kind(&self) -> ActionKind {
        match self {
            Self::None => ActionKind::None,
            Self::Message(_) => ActionKind::Message,
            Self::TextInput(_) => ActionKind::TextInput,
            Self::BoolInput(_) => ActionKind::BoolInput,
            Self::SelectionInput(_) => ActionKind::SelectionInput,
        }
    }

    pub fn press(&self) -> Option<Message>
    where
        Message: Clone,
    {
        match self {
            Self::Message(message) => Some(message.clone()),
            _ => None,
        }
    }

    pub fn input_text(&self, value: impl Into<String>) -> Option<Message> {
        match self {
            Self::TextInput(map) => Some(map(value.into())),
            Self::SelectionInput(map) => Some(map(value.into())),
            _ => None,
        }
    }

    pub fn input_bool(&self, value: bool) -> Option<Message> {
        match self {
            Self::BoolInput(map) => Some(map(value)),
            _ => None,
        }
    }
}

#[derive(Clone, Copy, Eq, PartialEq)]
pub enum ActionKind {
    None,
    Message,
    TextInput,
    BoolInput,
    SelectionInput,
}

impl fmt::Debug for ActionKind {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::None => "none",
            Self::Message => "message",
            Self::TextInput => "text_input",
            Self::BoolInput => "bool_input",
            Self::SelectionInput => "selection_input",
        })
    }
}

impl<Message: fmt::Debug> fmt::Debug for Action<Message> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::None => formatter.write_str("Action::None"),
            Self::Message(message) => formatter
                .debug_tuple("Action::Message")
                .field(message)
                .finish(),
            Self::TextInput(_) => formatter.write_str("Action::TextInput(<handler>)"),
            Self::BoolInput(_) => formatter.write_str("Action::BoolInput(<handler>)"),
            Self::SelectionInput(_) => formatter.write_str("Action::SelectionInput(<handler>)"),
        }
    }
}

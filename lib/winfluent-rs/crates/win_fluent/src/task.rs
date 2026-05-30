use std::future::Future;
use std::pin::Pin;

use crate::window::WindowCommand;

pub enum Task<Message> {
    None,
    Message(Message),
    Batch(Vec<Task<Message>>),
    Future(Pin<Box<dyn Future<Output = Message> + Send + 'static>>),
    Window(WindowCommand<Message>),
}

impl<Message> Task<Message> {
    pub const fn none() -> Self {
        Self::None
    }

    pub fn message(message: Message) -> Self {
        Self::Message(message)
    }

    pub fn batch(tasks: impl IntoIterator<Item = Task<Message>>) -> Self {
        let mut values = Vec::new();
        for task in tasks {
            match task {
                Task::None => {}
                Task::Batch(inner) => values.extend(inner),
                other => values.push(other),
            }
        }

        match values.len() {
            0 => Task::None,
            1 => values.pop().expect("length checked"),
            _ => Task::Batch(values),
        }
    }

    pub fn perform<T, Fut, Map>(future: Fut, map: Map) -> Self
    where
        Fut: Future<Output = T> + Send + 'static,
        Map: FnOnce(T) -> Message + Send + 'static,
        Message: Send + 'static,
    {
        Self::Future(Box::pin(async move { map(future.await) }))
    }

    pub fn window(command: WindowCommand<Message>) -> Self {
        Self::Window(command)
    }

    pub fn is_none(&self) -> bool {
        matches!(self, Self::None)
    }
}

impl<Message> Default for Task<Message> {
    fn default() -> Self {
        Self::None
    }
}

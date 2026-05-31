use std::future::Future;
use std::pin::Pin;

use crate::platform::{
    FileDialogOptions, PlatformCommand, ProtocolRegistration, ScreenCaptureRequest,
    ScreenCaptureResult, ShellVerb,
};
use crate::window::WindowCommand;
use futures_core::Stream;

pub enum Task<Message> {
    None,
    Message(Message),
    Batch(Vec<Task<Message>>),
    Future(Pin<Box<dyn Future<Output = Message> + Send + 'static>>),
    Stream(Pin<Box<dyn Stream<Item = Message> + Send + 'static>>),
    Window(WindowCommand<Message>),
    Platform(PlatformCommand),
    ReadClipboardText(Box<dyn Fn(Option<String>) -> Message + Send + 'static>),
    CaptureScreenRegion {
        request: ScreenCaptureRequest,
        map: Box<dyn Fn(Option<ScreenCaptureResult>) -> Message + Send + 'static>,
    },
    OpenFileDialog {
        options: FileDialogOptions,
        map: Box<dyn Fn(Option<String>) -> Message + Send + 'static>,
    },
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

    pub fn stream(stream: impl Stream<Item = Message> + Send + 'static) -> Self
    where
        Message: Send + 'static,
    {
        Self::Stream(Box::pin(stream))
    }

    pub fn window(command: WindowCommand<Message>) -> Self {
        Self::Window(command)
    }

    pub fn clipboard_text(text: impl Into<String>) -> Self {
        Self::Platform(PlatformCommand::WriteClipboardText(text.into()))
    }

    pub fn read_clipboard_text(map: impl Fn(Option<String>) -> Message + Send + 'static) -> Self {
        Self::ReadClipboardText(Box::new(map))
    }

    pub fn capture_screen_region(
        map: impl Fn(Option<ScreenCaptureResult>) -> Message + Send + 'static,
    ) -> Self {
        Self::capture_screen_region_with_request(ScreenCaptureRequest::virtual_desktop(), map)
    }

    pub fn capture_screen_region_with_request(
        request: ScreenCaptureRequest,
        map: impl Fn(Option<ScreenCaptureResult>) -> Message + Send + 'static,
    ) -> Self {
        Self::CaptureScreenRegion {
            request,
            map: Box::new(map),
        }
    }

    pub fn open_file_dialog(
        options: FileDialogOptions,
        map: impl Fn(Option<String>) -> Message + Send + 'static,
    ) -> Self {
        Self::OpenFileDialog {
            options,
            map: Box::new(map),
        }
    }

    pub fn capture_text_insertion_target() -> Self {
        Self::Platform(PlatformCommand::CaptureTextInsertionTarget)
    }

    pub fn insert_text(text: impl Into<String>) -> Self {
        Self::Platform(PlatformCommand::InsertText(text.into()))
    }

    pub fn open_url(url: impl Into<String>) -> Self {
        Self::Platform(PlatformCommand::OpenUrl(url.into()))
    }

    pub fn register_shell_verb(verb: ShellVerb) -> Self {
        Self::Platform(PlatformCommand::RegisterShellVerb(verb))
    }

    pub fn unregister_shell_verb(verb: ShellVerb) -> Self {
        Self::Platform(PlatformCommand::UnregisterShellVerb(verb))
    }

    pub fn register_protocol(protocol: ProtocolRegistration) -> Self {
        Self::Platform(PlatformCommand::RegisterProtocol(protocol))
    }

    pub fn unregister_protocol(protocol: ProtocolRegistration) -> Self {
        Self::Platform(PlatformCommand::UnregisterProtocol(protocol))
    }

    pub fn run_bundled_executable(
        executable_name: impl Into<String>,
        arguments: impl IntoIterator<Item = impl Into<String>>,
    ) -> Self {
        Self::Platform(PlatformCommand::RunBundledExecutable {
            executable_name: executable_name.into(),
            arguments: arguments.into_iter().map(Into::into).collect(),
        })
    }

    pub fn speak_text(text: impl Into<String>, language: Option<String>) -> Self {
        Self::Platform(PlatformCommand::SpeakText {
            text: text.into(),
            language,
        })
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::platform::{ScreenCaptureRequest, ScreenRect};

    #[derive(Debug, Eq, PartialEq)]
    enum TestMessage {
        Captured(Option<ScreenCaptureResult>),
    }

    #[test]
    fn capture_screen_region_uses_virtual_desktop_request_by_default() {
        let task = Task::capture_screen_region(TestMessage::Captured);

        let Task::CaptureScreenRegion { request, .. } = task else {
            panic!("expected capture task");
        };

        assert_eq!(request, ScreenCaptureRequest::virtual_desktop());
    }

    #[test]
    fn capture_screen_region_with_request_preserves_region_and_mapper() {
        let region = ScreenRect::new(-10, 20, 300, 200);
        let task = Task::capture_screen_region_with_request(
            ScreenCaptureRequest::region(region),
            TestMessage::Captured,
        );

        let Task::CaptureScreenRegion { request, map } = task else {
            panic!("expected capture task");
        };

        assert_eq!(request, ScreenCaptureRequest::region(region));
        assert_eq!(map(None), TestMessage::Captured(None));
    }
}

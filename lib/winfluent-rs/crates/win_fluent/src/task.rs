use std::future::Future;
use std::pin::Pin;

use crate::platform::{
    FileDialogOptions, FolderDialogOptions, PlatformCommand, ProtocolRegistration,
    ScreenCaptureRequest, ScreenCaptureResult, ScreenWindow, ScreenWindowSnapshotRequest,
    ShellVerb,
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
    /// Snap the scroll view with the given id back to the top (offset 0).
    ScrollToTop(String),
    /// Snap the scroll view with the given id to a relative offset.
    ScrollTo {
        id: String,
        x: f32,
        y: f32,
    },
    ReadClipboardText(Box<dyn Fn(Option<String>) -> Message + Send + 'static>),
    CaptureScreenRegion {
        request: ScreenCaptureRequest,
        map: Box<dyn Fn(Option<ScreenCaptureResult>) -> Message + Send + 'static>,
    },
    CaptureScreenWindows {
        request: ScreenWindowSnapshotRequest,
        map: Box<dyn Fn(Vec<ScreenWindow>) -> Message + Send + 'static>,
    },
    OpenFileDialog {
        options: FileDialogOptions,
        map: Box<dyn Fn(Option<String>) -> Message + Send + 'static>,
    },
    OpenFolderDialog {
        options: FolderDialogOptions,
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

    /// Snaps the scroll view with the given id back to the top.
    pub fn scroll_to_top(id: impl Into<String>) -> Self {
        Self::ScrollToTop(id.into())
    }

    /// Snaps the scroll view with the given id to a relative offset.
    pub fn scroll_to(id: impl Into<String>, x: f32, y: f32) -> Self {
        Self::ScrollTo {
            id: id.into(),
            x: x.clamp(0.0, 1.0),
            y: y.clamp(0.0, 1.0),
        }
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

    pub fn capture_screen_windows(
        map: impl Fn(Vec<ScreenWindow>) -> Message + Send + 'static,
    ) -> Self {
        Self::capture_screen_windows_with_request(ScreenWindowSnapshotRequest::new(), map)
    }

    pub fn capture_screen_windows_with_request(
        request: ScreenWindowSnapshotRequest,
        map: impl Fn(Vec<ScreenWindow>) -> Message + Send + 'static,
    ) -> Self {
        Self::CaptureScreenWindows {
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

    pub fn open_folder_dialog(
        options: FolderDialogOptions,
        map: impl Fn(Option<String>) -> Message + Send + 'static,
    ) -> Self {
        Self::OpenFolderDialog {
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
    use crate::platform::{
        ScreenCaptureRequest, ScreenRect, ScreenWindow, ScreenWindowSnapshotRequest,
    };

    #[derive(Debug, Eq, PartialEq)]
    enum TestMessage {
        Captured(Option<ScreenCaptureResult>),
        Windows(Vec<ScreenWindow>),
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
    fn scroll_to_clamps_relative_offsets() {
        let task: Task<TestMessage> = Task::scroll_to("MainScrollViewer", -0.25, 1.25);

        let Task::ScrollTo { id, x, y } = task else {
            panic!("expected scroll task");
        };

        assert_eq!(id, "MainScrollViewer");
        assert_eq!(x, 0.0);
        assert_eq!(y, 1.0);
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

    #[test]
    fn capture_screen_windows_with_request_preserves_excluded_titles_and_mapper() {
        let expected_request = ScreenWindowSnapshotRequest::new().exclude_title("Capture Overlay");
        let task = Task::capture_screen_windows_with_request(
            expected_request.clone(),
            TestMessage::Windows,
        );

        let Task::CaptureScreenWindows { request, map } = task else {
            panic!("expected window snapshot task");
        };

        assert_eq!(request, expected_request);
        let windows = vec![ScreenWindow::new(7, None, ScreenRect::new(1, 2, 30, 40))];
        assert_eq!(map(windows.clone()), TestMessage::Windows(windows));
    }
}

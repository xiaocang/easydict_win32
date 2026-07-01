use std::future::Future;
use std::pin::Pin;
use std::sync::Arc;

use crate::platform::{
    FileDialogOptions, FolderDialogOptions, PlatformCommand, ProtocolRegistration,
    ScreenCaptureRequest, ScreenCaptureResult, ScreenWindow, ScreenWindowSnapshotRequest,
    ShellVerb,
};
use crate::window::WindowCommand;
use futures_core::Stream;
use futures_util::StreamExt;

pub enum Task<Message> {
    None,
    Message(Message),
    Batch(Vec<Task<Message>>),
    Future(Pin<Box<dyn Future<Output = Message> + Send + 'static>>),
    Stream(Pin<Box<dyn Stream<Item = Message> + Send + 'static>>),
    Window(WindowCommand<Message>),
    Platform(PlatformCommand),
    Cancel(String),
    Exit,
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

    /// Map messages produced directly by this task while preserving window and
    /// platform side effects. This is intentionally same-message mapping: view
    /// trees inside `WindowCommand` keep their existing message type.
    pub fn map(self, map: impl Fn(Message) -> Message + Send + Sync + 'static) -> Self
    where
        Message: Send + 'static,
    {
        self.map_with_arc(Arc::new(map))
    }

    fn map_with_arc(self, map: Arc<dyn Fn(Message) -> Message + Send + Sync + 'static>) -> Self
    where
        Message: Send + 'static,
    {
        match self {
            Task::None => Task::None,
            Task::Message(message) => Task::Message(map(message)),
            Task::Batch(tasks) => Task::batch(
                tasks
                    .into_iter()
                    .map(|task| task.map_with_arc(Arc::clone(&map))),
            ),
            Task::Future(future) => Task::Future(Box::pin(async move { map(future.await) })),
            Task::Stream(stream) => Task::Stream(Box::pin(stream.map(move |message| map(message)))),
            Task::ReadClipboardText(inner) => {
                Task::ReadClipboardText(Box::new(move |value| map(inner(value))))
            }
            Task::CaptureScreenRegion {
                request,
                map: inner,
            } => Task::CaptureScreenRegion {
                request,
                map: Box::new(move |value| map(inner(value))),
            },
            Task::CaptureScreenWindows {
                request,
                map: inner,
            } => Task::CaptureScreenWindows {
                request,
                map: Box::new(move |value| map(inner(value))),
            },
            Task::OpenFileDialog {
                options,
                map: inner,
            } => Task::OpenFileDialog {
                options,
                map: Box::new(move |value| map(inner(value))),
            },
            Task::OpenFolderDialog {
                options,
                map: inner,
            } => Task::OpenFolderDialog {
                options,
                map: Box::new(move |value| map(inner(value))),
            },
            Task::Window(command) => Task::Window(command),
            Task::Platform(command) => Task::Platform(command),
            Task::Cancel(id) => Task::Cancel(id),
            Task::Exit => Task::Exit,
            Task::ScrollToTop(id) => Task::ScrollToTop(id),
            Task::ScrollTo { id, x, y } => Task::ScrollTo { id, x, y },
        }
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

    pub fn cancel(id: impl Into<String>) -> Self {
        Self::Cancel(id.into())
    }

    pub const fn exit() -> Self {
        Self::Exit
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
        FileDialogFilter, ScreenCaptureRequest, ScreenRect, ScreenWindow,
        ScreenWindowSnapshotRequest,
    };

    #[derive(Debug, Eq, PartialEq)]
    enum TestMessage {
        Captured(Option<ScreenCaptureResult>),
        Windows(Vec<ScreenWindow>),
        Text(Option<String>),
        Tagged(&'static str),
        Path(Option<String>),
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
    fn batch_flattens_nested_tasks_and_discards_none() {
        let task = Task::batch([
            Task::none(),
            Task::batch([Task::message(TestMessage::Tagged("a"))]),
            Task::message(TestMessage::Tagged("b")),
        ]);

        let Task::Batch(values) = task else {
            panic!("expected batch");
        };

        assert_eq!(values.len(), 2);
    }

    #[test]
    fn map_transforms_immediate_and_callback_tasks() {
        let task = Task::message(TestMessage::Tagged("raw")).map(|message| match message {
            TestMessage::Tagged(_) => TestMessage::Tagged("mapped"),
            other => other,
        });
        assert!(matches!(task, Task::Message(TestMessage::Tagged("mapped"))));

        let task = Task::read_clipboard_text(TestMessage::Text).map(|message| match message {
            TestMessage::Text(_) => TestMessage::Tagged("clipboard"),
            other => other,
        });
        let Task::ReadClipboardText(map) = task else {
            panic!("expected clipboard task");
        };
        assert_eq!(
            map(Some("value".to_string())),
            TestMessage::Tagged("clipboard")
        );
    }

    #[test]
    fn cancel_task_preserves_cancellation_identifier() {
        let task: Task<TestMessage> = Task::cancel("download:42");

        let Task::Cancel(id) = task else {
            panic!("expected cancel task");
        };

        assert_eq!(id, "download:42");
    }

    #[test]
    fn clipboard_and_dialog_tasks_preserve_options_and_mappers() {
        let clipboard = Task::<TestMessage>::clipboard_text("hello");
        assert!(matches!(
            clipboard,
            Task::Platform(PlatformCommand::WriteClipboardText(text)) if text == "hello"
        ));

        let file_options = FileDialogOptions::new("Open document")
            .initial_directory(r"C:\Users")
            .filter(FileDialogFilter::new("Text", ["*.txt", "*.md"]));
        let file_task = Task::open_file_dialog(file_options.clone(), TestMessage::Path);
        let Task::OpenFileDialog { options, map } = file_task else {
            panic!("expected file dialog task");
        };
        assert_eq!(options, file_options);
        assert_eq!(
            map(Some(r"C:\Users\notes.txt".to_string())),
            TestMessage::Path(Some(r"C:\Users\notes.txt".to_string()))
        );

        let folder_options =
            FolderDialogOptions::new("Choose output").initial_directory(r"C:\Temp");
        let folder_task = Task::open_folder_dialog(folder_options.clone(), TestMessage::Path);
        let Task::OpenFolderDialog { options, map } = folder_task else {
            panic!("expected folder dialog task");
        };
        assert_eq!(options, folder_options);
        assert_eq!(map(None), TestMessage::Path(None));
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

use std::fmt;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::task::{Context, Poll};
use std::time::Duration;

use futures_channel::mpsc::{unbounded, UnboundedReceiver, UnboundedSender};
use futures_core::Stream;

pub const CLIPBOARD_MONITOR_POLL_INTERVAL: Duration = Duration::from_millis(500);

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ClipboardError {
    UnsupportedPlatform,
    Backend(String),
}

impl fmt::Display for ClipboardError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform => {
                write!(formatter, "clipboard is only available on Windows")
            }
            Self::Backend(message) => write!(formatter, "{message}"),
        }
    }
}

impl std::error::Error for ClipboardError {}

pub fn read_clipboard_text() -> Result<Option<String>, ClipboardError> {
    easydict_windows_text_selection::clipboard_text_snapshot()
        .map(|snapshot| snapshot.text)
        .map_err(ClipboardError::from)
}

pub fn write_clipboard_text(text: impl AsRef<str>) -> Result<(), ClipboardError> {
    let text = text.as_ref();
    easydict_windows_text_selection::set_clipboard_text(text).map_err(ClipboardError::from)?;
    remember_self_written_clipboard_text(text);
    Ok(())
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ClipboardMonitorSnapshot {
    pub sequence_number: u32,
    pub text: Option<String>,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ClipboardMonitorState {
    last_sequence_number: Option<u32>,
    last_text: Option<String>,
}

impl ClipboardMonitorState {
    pub fn seed(&mut self, snapshot: ClipboardMonitorSnapshot) {
        self.last_sequence_number = Some(snapshot.sequence_number);
        self.last_text = normalize_clipboard_monitor_text(snapshot.text);
    }

    pub fn observe(&mut self, snapshot: ClipboardMonitorSnapshot) -> Option<String> {
        if self.last_sequence_number == Some(snapshot.sequence_number) {
            return None;
        }

        self.last_sequence_number = Some(snapshot.sequence_number);
        let text = normalize_clipboard_monitor_text(snapshot.text)?;
        if self.last_text.as_deref() == Some(text.as_str()) {
            return None;
        }

        self.last_text = Some(text.clone());
        if consume_self_written_clipboard_text(&text) {
            return None;
        }

        Some(text)
    }
}

pub struct ClipboardMonitorStream<Message> {
    running: Arc<AtomicBool>,
    poll_interval: Duration,
    map: Option<Box<dyn Fn(String) -> Message + Send + 'static>>,
    receiver: Option<UnboundedReceiver<Message>>,
}

impl<Message> Unpin for ClipboardMonitorStream<Message> {}

impl<Message> Drop for ClipboardMonitorStream<Message> {
    fn drop(&mut self) {
        self.running.store(false, Ordering::SeqCst);
        clear_clipboard_monitor_if_current(&self.running);
    }
}

impl<Message> Stream for ClipboardMonitorStream<Message>
where
    Message: Send + 'static,
{
    type Item = Message;

    fn poll_next(self: Pin<&mut Self>, context: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        let this = self.get_mut();
        if this.receiver.is_none() {
            this.start();
        }

        let Some(receiver) = this.receiver.as_mut() else {
            return Poll::Ready(None);
        };
        Pin::new(receiver).poll_next(context)
    }
}

impl<Message> ClipboardMonitorStream<Message>
where
    Message: Send + 'static,
{
    fn start(&mut self) {
        let Some(map) = self.map.take() else {
            return;
        };

        let running = self.running.clone();
        let poll_interval = self.poll_interval;
        let (sender, receiver) = unbounded();
        self.receiver = Some(receiver);
        std::thread::spawn(move || {
            run_clipboard_monitor_loop(running, poll_interval, sender, map);
        });
    }
}

pub fn clipboard_monitor_stream<Message>(
    map: impl Fn(String) -> Message + Send + 'static,
) -> Option<ClipboardMonitorStream<Message>>
where
    Message: Send + 'static,
{
    clipboard_monitor_stream_with_interval(CLIPBOARD_MONITOR_POLL_INTERVAL, map)
}

pub fn clipboard_monitor_stream_with_interval<Message>(
    poll_interval: Duration,
    map: impl Fn(String) -> Message + Send + 'static,
) -> Option<ClipboardMonitorStream<Message>>
where
    Message: Send + 'static,
{
    let mut slot = clipboard_monitor_slot()
        .lock()
        .expect("clipboard monitor mutex poisoned");
    if slot
        .as_ref()
        .is_some_and(|running| running.load(Ordering::SeqCst))
    {
        return None;
    }

    let running = Arc::new(AtomicBool::new(true));
    *slot = Some(running.clone());
    Some(ClipboardMonitorStream {
        running,
        poll_interval,
        map: Some(Box::new(map)),
        receiver: None,
    })
}

pub fn stop_clipboard_monitor() {
    let mut slot = clipboard_monitor_slot()
        .lock()
        .expect("clipboard monitor mutex poisoned");
    if let Some(running) = slot.take() {
        running.store(false, Ordering::SeqCst);
    }
}

pub fn clipboard_monitor_is_running() -> bool {
    clipboard_monitor_slot()
        .lock()
        .expect("clipboard monitor mutex poisoned")
        .as_ref()
        .is_some_and(|running| running.load(Ordering::SeqCst))
}

impl From<easydict_windows_text_selection::WindowsTextSelectionError> for ClipboardError {
    fn from(error: easydict_windows_text_selection::WindowsTextSelectionError) -> Self {
        match error {
            easydict_windows_text_selection::WindowsTextSelectionError::UnsupportedPlatform => {
                Self::UnsupportedPlatform
            }
            other => Self::Backend(other.to_string()),
        }
    }
}

fn run_clipboard_monitor_loop<Message>(
    running: Arc<AtomicBool>,
    poll_interval: Duration,
    sender: UnboundedSender<Message>,
    map: Box<dyn Fn(String) -> Message + Send + 'static>,
) where
    Message: Send + 'static,
{
    let mut state = ClipboardMonitorState::default();
    if let Ok(snapshot) = current_clipboard_monitor_snapshot() {
        state.seed(snapshot);
    }

    while running.load(Ordering::SeqCst) {
        std::thread::sleep(poll_interval);
        if !running.load(Ordering::SeqCst) {
            break;
        }

        let snapshot = match current_clipboard_monitor_snapshot() {
            Ok(snapshot) => snapshot,
            Err(ClipboardError::UnsupportedPlatform) => break,
            Err(_) => continue,
        };

        let Some(text) = state.observe(snapshot) else {
            continue;
        };
        if !running.load(Ordering::SeqCst) {
            break;
        }
        if sender.unbounded_send(map(text)).is_err() {
            break;
        }
    }

    running.store(false, Ordering::SeqCst);
    clear_clipboard_monitor_if_current(&running);
}

fn current_clipboard_monitor_snapshot() -> Result<ClipboardMonitorSnapshot, ClipboardError> {
    easydict_windows_text_selection::clipboard_text_snapshot()
        .map(|snapshot| ClipboardMonitorSnapshot {
            sequence_number: snapshot.sequence_number,
            text: snapshot.text,
        })
        .map_err(ClipboardError::from)
}

fn normalize_clipboard_monitor_text(text: Option<String>) -> Option<String> {
    text.map(|text| text.trim().to_string())
        .filter(|text| !text.is_empty())
}

fn remember_self_written_clipboard_text(text: &str) {
    let Some(text) = normalize_clipboard_monitor_text(Some(text.to_string())) else {
        return;
    };
    *self_written_clipboard_text_slot()
        .lock()
        .expect("self-written clipboard mutex poisoned") = Some(text);
}

fn consume_self_written_clipboard_text(text: &str) -> bool {
    let mut slot = self_written_clipboard_text_slot()
        .lock()
        .expect("self-written clipboard mutex poisoned");
    if slot.as_deref() == Some(text) {
        *slot = None;
        true
    } else {
        false
    }
}

fn clipboard_monitor_slot() -> &'static Mutex<Option<Arc<AtomicBool>>> {
    static SLOT: OnceLock<Mutex<Option<Arc<AtomicBool>>>> = OnceLock::new();
    SLOT.get_or_init(|| Mutex::new(None))
}

fn self_written_clipboard_text_slot() -> &'static Mutex<Option<String>> {
    static SLOT: OnceLock<Mutex<Option<String>>> = OnceLock::new();
    SLOT.get_or_init(|| Mutex::new(None))
}

fn clear_clipboard_monitor_if_current(running: &Arc<AtomicBool>) {
    let mut slot = clipboard_monitor_slot()
        .lock()
        .expect("clipboard monitor mutex poisoned");
    if slot
        .as_ref()
        .is_some_and(|current| Arc::ptr_eq(current, running))
    {
        *slot = None;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(windows)]
    static REAL_CLIPBOARD_SMOKE_LOCK: Mutex<()> = Mutex::new(());

    #[test]
    fn maps_unsupported_platform_error_without_winfluent_dependency() {
        let error = ClipboardError::from(
            easydict_windows_text_selection::WindowsTextSelectionError::UnsupportedPlatform,
        );

        assert_eq!(error, ClipboardError::UnsupportedPlatform);
        assert_eq!(error.to_string(), "clipboard is only available on Windows");
    }

    #[test]
    fn monitor_state_emits_only_changed_non_empty_text() {
        let mut state = ClipboardMonitorState::default();
        state.seed(ClipboardMonitorSnapshot {
            sequence_number: 10,
            text: Some("seed".to_string()),
        });

        assert_eq!(
            state.observe(ClipboardMonitorSnapshot {
                sequence_number: 10,
                text: Some("ignored".to_string()),
            }),
            None
        );
        assert_eq!(
            state.observe(ClipboardMonitorSnapshot {
                sequence_number: 11,
                text: Some("   ".to_string()),
            }),
            None
        );
        assert_eq!(
            state.observe(ClipboardMonitorSnapshot {
                sequence_number: 12,
                text: Some(" next ".to_string()),
            }),
            Some("next".to_string())
        );
        assert_eq!(
            state.observe(ClipboardMonitorSnapshot {
                sequence_number: 13,
                text: Some("next".to_string()),
            }),
            None
        );
    }

    #[test]
    fn monitor_state_ignores_self_written_clipboard_text_once() {
        *self_written_clipboard_text_slot()
            .lock()
            .expect("self-written clipboard mutex poisoned") = None;
        remember_self_written_clipboard_text(" copied result ");

        let mut state = ClipboardMonitorState::default();
        assert_eq!(
            state.observe(ClipboardMonitorSnapshot {
                sequence_number: 1,
                text: Some("copied result".to_string()),
            }),
            None
        );
        assert_eq!(
            state.observe(ClipboardMonitorSnapshot {
                sequence_number: 2,
                text: Some("copied result again".to_string()),
            }),
            Some("copied result again".to_string())
        );
    }

    #[test]
    fn monitor_stream_registration_is_singleton_and_stoppable() {
        stop_clipboard_monitor();

        let stream = clipboard_monitor_stream_with_interval(Duration::from_millis(1), |text| text)
            .expect("first monitor stream should register");
        assert!(clipboard_monitor_is_running());
        assert!(
            clipboard_monitor_stream_with_interval(Duration::from_millis(1), |text| text).is_none()
        );

        drop(stream);
        assert!(!clipboard_monitor_is_running());
    }

    #[cfg(windows)]
    #[test]
    fn real_clipboard_monitor_smoke_when_enabled() {
        if !windows_clipboard_monitor_smoke_enabled() {
            return;
        }

        let _guard = REAL_CLIPBOARD_SMOKE_LOCK
            .lock()
            .expect("real clipboard smoke lock");
        let original = easydict_windows_text_selection::clipboard_text_snapshot().ok();
        stop_clipboard_monitor();
        *self_written_clipboard_text_slot()
            .lock()
            .expect("self-written clipboard mutex poisoned") = None;

        let seed = format!("easydict-clipboard-monitor-seed-{}", std::process::id());
        easydict_windows_text_selection::set_clipboard_text(&seed).expect("seed clipboard");

        let mut stream =
            clipboard_monitor_stream_with_interval(Duration::from_millis(25), |text| text)
                .expect("real clipboard monitor should start");
        assert!(clipboard_monitor_is_running());
        if let Some(startup_message) =
            poll_stream_until_message(&mut stream, Duration::from_millis(150))
        {
            assert_eq!(
                startup_message, seed,
                "startup monitor race should only be allowed to emit the seed clipboard text"
            );
        }

        let external = format!("easydict-clipboard-monitor-external-{}", std::process::id());
        easydict_windows_text_selection::set_clipboard_text(&external)
            .expect("write external clipboard text");
        assert_eq!(
            poll_stream_until_message(&mut stream, Duration::from_secs(3)).as_deref(),
            Some(external.as_str())
        );

        let self_written = format!("easydict-clipboard-monitor-self-{}", std::process::id());
        write_clipboard_text(&self_written).expect("write self clipboard text");
        assert_eq!(
            poll_stream_until_message(&mut stream, Duration::from_millis(250)),
            None,
            "monitor should suppress app-written clipboard text once"
        );

        drop(stream);
        assert!(!clipboard_monitor_is_running());
        restore_clipboard_after_smoke(original.as_ref());
    }

    #[cfg(windows)]
    fn windows_clipboard_monitor_smoke_enabled() -> bool {
        std::env::var("EASYDICT_WINDOWS_CLIPBOARD_MONITOR_SMOKE")
            .map(|value| {
                matches!(
                    value.trim().to_ascii_lowercase().as_str(),
                    "1" | "true" | "yes" | "on"
                )
            })
            .unwrap_or(false)
    }

    #[cfg(windows)]
    fn poll_stream_until_message(
        stream: &mut ClipboardMonitorStream<String>,
        timeout: Duration,
    ) -> Option<String> {
        let deadline = std::time::Instant::now() + timeout;
        let waker = std::task::Waker::noop();
        let mut context = Context::from_waker(waker);
        while std::time::Instant::now() < deadline {
            match Pin::new(&mut *stream).poll_next(&mut context) {
                Poll::Ready(Some(message)) => return Some(message),
                Poll::Ready(None) => return None,
                Poll::Pending => std::thread::sleep(Duration::from_millis(10)),
            }
        }
        None
    }

    #[cfg(windows)]
    fn restore_clipboard_after_smoke(
        original: Option<&easydict_windows_text_selection::ClipboardTextSnapshot>,
    ) {
        match original.and_then(|snapshot| snapshot.text.as_deref()) {
            Some(text) => {
                let _ = easydict_windows_text_selection::set_clipboard_text(text);
            }
            None => {
                let _ = easydict_windows_text_selection::clear_clipboard();
            }
        }
    }
}

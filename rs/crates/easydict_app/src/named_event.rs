use futures_channel::mpsc::{unbounded, UnboundedReceiver, UnboundedSender};
use futures_core::Stream;
use std::pin::Pin;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};
use std::task::{Context, Poll};
use std::time::Duration;

const WAIT_INTERVAL: Duration = Duration::from_millis(100);

pub struct NamedEventStream<Message> {
    name: String,
    auto_reset: bool,
    message: Option<Message>,
    running: Arc<AtomicBool>,
    receiver: Option<UnboundedReceiver<Message>>,
}

impl<Message> Unpin for NamedEventStream<Message> {}

impl<Message> Drop for NamedEventStream<Message> {
    fn drop(&mut self) {
        self.running.store(false, Ordering::SeqCst);
    }
}

impl<Message> Stream for NamedEventStream<Message>
where
    Message: Clone + Send + 'static,
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

impl<Message> NamedEventStream<Message>
where
    Message: Clone + Send + 'static,
{
    fn start(&mut self) {
        let Some(message) = self.message.take() else {
            return;
        };

        let name = self.name.clone();
        let auto_reset = self.auto_reset;
        let running = self.running.clone();
        let (sender, receiver) = unbounded();
        self.receiver = Some(receiver);
        std::thread::spawn(move || {
            run_named_event_loop(name, auto_reset, running, sender, message);
        });
    }
}

pub fn named_event_stream<Message>(
    name: impl Into<String>,
    auto_reset: bool,
    message: Message,
) -> NamedEventStream<Message>
where
    Message: Clone + Send + 'static,
{
    NamedEventStream {
        name: name.into(),
        auto_reset,
        message: Some(message),
        running: Arc::new(AtomicBool::new(true)),
        receiver: None,
    }
}

fn run_named_event_loop<Message>(
    name: String,
    auto_reset: bool,
    running: Arc<AtomicBool>,
    sender: UnboundedSender<Message>,
    message: Message,
) where
    Message: Clone + Send + 'static,
{
    let listener = match easydict_windows_ipc::NamedEventListener::create(&name, auto_reset) {
        Ok(listener) => listener,
        Err(_) => {
            running.store(false, Ordering::SeqCst);
            return;
        }
    };

    while running.load(Ordering::SeqCst) {
        match listener.wait(WAIT_INTERVAL) {
            Ok(true) => {
                if sender.unbounded_send(message.clone()).is_err() {
                    break;
                }
            }
            Ok(false) => {}
            Err(_) => break,
        }
    }

    running.store(false, Ordering::SeqCst);
}

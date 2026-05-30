use crate::subscription::Subscription;
use crate::task::Task;
use crate::theme::ThemeMode;
use crate::view::View;
use crate::window::WindowId;

pub trait Application: Sized + 'static {
    type Message: Clone + Send + 'static;
    type Flags;

    fn new(flags: Self::Flags) -> (Self, Task<Self::Message>);

    fn title(&self, window: &WindowId) -> String;

    fn view(&self, window: &WindowId) -> View<Self::Message>;

    fn update(&mut self, message: Self::Message) -> Task<Self::Message>;

    fn subscription(&self) -> Subscription<Self::Message> {
        Subscription::none()
    }

    fn theme(&self) -> ThemeMode {
        ThemeMode::System
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RuntimeError {
    BackendUnavailable,
    WindowUnavailable(String),
    PlatformUnavailable(String),
}

pub struct RuntimePlan<App: Application> {
    pub app: App,
    pub initial_task: Task<App::Message>,
}

impl<App: Application> RuntimePlan<App> {
    pub fn new(flags: App::Flags) -> Self {
        let (app, initial_task) = App::new(flags);
        Self { app, initial_task }
    }
}

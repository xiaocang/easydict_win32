use crate::platform::{NamedEventRegistration, ProtocolRegistration, ShellVerb, TrayMenu};
use crate::subscription::Subscription;
use crate::task::Task;
use crate::theme::{ThemeMode, ThemeTokens};
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

    fn tray_menu(&self) -> Option<TrayMenu<Self::Message>> {
        None
    }

    fn named_events(&self) -> Vec<NamedEventRegistration<Self::Message>> {
        Vec::new()
    }

    fn shell_verbs(&self) -> Vec<ShellVerb> {
        Vec::new()
    }

    fn protocol_registrations(&self) -> Vec<ProtocolRegistration> {
        Vec::new()
    }

    fn theme(&self) -> ThemeMode {
        ThemeMode::System
    }

    fn theme_tokens(&self) -> ThemeTokens {
        ThemeTokens::resolve(self.theme())
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
    pub desktop_integration: DesktopIntegrationPlan<App::Message>,
}

#[derive(Clone)]
pub struct DesktopIntegrationPlan<Message> {
    pub tray_menu: Option<TrayMenu<Message>>,
    pub named_events: Vec<NamedEventRegistration<Message>>,
    pub shell_verbs: Vec<ShellVerb>,
    pub protocol_registrations: Vec<ProtocolRegistration>,
}

impl<Message> DesktopIntegrationPlan<Message> {
    pub fn has_entries(&self) -> bool {
        self.tray_menu
            .as_ref()
            .is_some_and(|menu| !menu.items.is_empty())
            || !self.named_events.is_empty()
            || !self.shell_verbs.is_empty()
            || !self.protocol_registrations.is_empty()
    }

    pub fn entry_count(&self) -> usize {
        self.tray_menu
            .as_ref()
            .map_or(0, |menu| usize::from(!menu.items.is_empty()))
            + self.named_events.len()
            + self.shell_verbs.len()
            + self.protocol_registrations.len()
    }
}

impl<App: Application> RuntimePlan<App> {
    pub fn new(flags: App::Flags) -> Self {
        let (app, initial_task) = App::new(flags);
        let desktop_integration = DesktopIntegrationPlan {
            tray_menu: app.tray_menu(),
            named_events: app.named_events(),
            shell_verbs: app.shell_verbs(),
            protocol_registrations: app.protocol_registrations(),
        };

        Self {
            app,
            initial_task,
            desktop_integration,
        }
    }
}

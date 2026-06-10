use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::time::{Duration, Instant};

use win_fluent::a11y::{A11yNode, A11yRole};
use win_fluent::action::ActionKind;
use win_fluent::platform::{
    ClipboardFormat, Hotkey, HotkeyKey, HotkeyModifier, NamedEventRegistration,
    ProtocolRegistration, ScreenCaptureRequest, ScreenCaptureResult, ScreenWindow,
    ScreenWindowSnapshotRequest, ShellVerb, TrayMenu,
};
use win_fluent::runtime::DesktopIntegrationPlan;
use win_fluent::subscription::{Subscription, SubscriptionKind};
use win_fluent::window::{
    WindowFrame, WindowLevel, WindowOptions, WindowPlacement, WindowResizeMode,
    WindowScreenConstraint,
};

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum WindowsRegistration {
    Clipboard,
    Hotkey(WindowsHotkey),
    NamedEvent(WindowsNamedEventPlan),
    Protocol(WindowsProtocolRegistrationPlan),
    ShellVerb(WindowsShellVerbPlan),
    Theme,
    Tray(WindowsTrayPlan),
    Window(String),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsHotkey {
    pub id: String,
    pub native_id: i32,
    pub modifiers: u32,
    pub virtual_key: u32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsHotkeyEvent {
    pub id: String,
    pub native_id: i32,
    pub modifiers: u32,
    pub virtual_key: u32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsNamedEvent {
    pub name: String,
}

#[derive(Clone, Debug, PartialEq)]
pub struct WindowsWindowPlan {
    pub id: String,
    pub title: String,
    pub style: u32,
    pub ex_style: u32,
    pub width: i32,
    pub height: i32,
    pub min_width: Option<i32>,
    pub min_height: Option<i32>,
    pub visible_on_start: bool,
    pub skip_taskbar: bool,
    pub uses_acrylic: bool,
    pub placement: Option<ResolvedWindowPlacement>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct WindowsPoint {
    pub x: i32,
    pub y: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct WindowsRect {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}

impl WindowsRect {
    pub fn width(self) -> i32 {
        self.right - self.left
    }

    pub fn height(self) -> i32 {
        self.bottom - self.top
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ResolvedWindowPlacement {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
    pub dpi: u32,
    pub work_area: WindowsRect,
    pub physical_work_area: WindowsRect,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct WindowsMonitorMetrics {
    pub physical_work_area: WindowsRect,
    pub physical_monitor_area: WindowsRect,
    pub dpi: u32,
}

impl WindowsMonitorMetrics {
    pub fn new(physical_work_area: WindowsRect, dpi: u32) -> Self {
        Self {
            physical_work_area,
            physical_monitor_area: physical_work_area,
            dpi: dpi.max(1),
        }
    }

    pub fn with_monitor_area(
        physical_work_area: WindowsRect,
        physical_monitor_area: WindowsRect,
        dpi: u32,
    ) -> Self {
        Self {
            physical_work_area,
            physical_monitor_area,
            dpi: dpi.max(1),
        }
    }

    pub fn scale_factor(self) -> f32 {
        self.dpi as f32 / 96.0
    }

    pub fn work_area_dips(self) -> WindowsRect {
        physical_rect_to_dips(self.physical_work_area, self.scale_factor())
    }

    pub fn monitor_area_dips(self) -> WindowsRect {
        physical_rect_to_dips(self.physical_monitor_area, self.scale_factor())
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct WindowsProcessMemory {
    pub private_bytes: usize,
    pub working_set_bytes: usize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowsDataProtectionScope {
    CurrentUser,
    LocalMachine,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsClipboardTextSnapshot {
    pub unicode_text: Option<String>,
    pub formats: Vec<WindowsClipboardFormatSnapshot>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsClipboardFormatSnapshot {
    pub format: u32,
    pub bytes: Vec<u8>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsTrayPlan {
    pub tooltip: String,
    pub callback_message: u32,
    pub item_count: usize,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsNamedEventPlan {
    pub name: String,
    pub auto_reset: bool,
    pub action_kind: ActionKind,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsShellVerbPlan {
    pub id: String,
    pub label: String,
    pub accepts_files: bool,
    pub accepts_directory_background: bool,
    pub registry_key_paths: Vec<String>,
    pub command_key_paths: Vec<String>,
    pub command_arguments: Vec<String>,
}

impl WindowsShellVerbPlan {
    pub fn command_line(&self, executable_path: &str) -> String {
        windows_command_line(executable_path, &self.command_arguments, false)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsProtocolRegistrationPlan {
    pub scheme: String,
    pub description: String,
    pub url_protocol_marker: bool,
    pub registry_key_path: String,
    pub command_key_path: String,
    pub command_arguments: Vec<String>,
}

impl WindowsProtocolRegistrationPlan {
    pub fn command_line(&self, executable_path: &str) -> String {
        windows_command_line(executable_path, &self.command_arguments, true)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsDesktopIntegrationPlan {
    pub tray: Option<WindowsTrayPlan>,
    pub named_events: Vec<WindowsNamedEventPlan>,
    pub shell_verbs: Vec<WindowsShellVerbPlan>,
    pub protocol_registrations: Vec<WindowsProtocolRegistrationPlan>,
}

impl WindowsDesktopIntegrationPlan {
    pub fn has_entries(&self) -> bool {
        self.tray.is_some()
            || !self.named_events.is_empty()
            || !self.shell_verbs.is_empty()
            || !self.protocol_registrations.is_empty()
    }

    pub fn entry_count(&self) -> usize {
        usize::from(self.tray.is_some())
            + self.named_events.len()
            + self.shell_verbs.len()
            + self.protocol_registrations.len()
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowsUiaControlType {
    Button,
    CheckBox,
    ComboBox,
    Document,
    Edit,
    Group,
    List,
    ListItem,
    Pane,
    Slider,
    Text,
    Window,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsUiaNodePlan {
    pub control_type: WindowsUiaControlType,
    pub name: Option<String>,
    pub description: Option<String>,
    pub help_text: Option<String>,
    pub focusable: bool,
    pub children: Vec<WindowsUiaNodePlan>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsUiaTreePlan {
    pub root: WindowsUiaNodePlan,
}

#[derive(Debug, Eq, PartialEq)]
pub enum WindowsPlatformError {
    UnsupportedPlatform,
    UnsupportedHotkeyKey(String),
    InvalidFunctionKey(u8),
    TextInsertionTargetUnavailable,
    NativeCallFailed { operation: &'static str, code: u32 },
}

pub struct WindowsPlatformAdapter;

impl WindowsPlatformAdapter {
    pub fn plan_hotkeys(hotkeys: &[Hotkey]) -> Result<Vec<WindowsHotkey>, WindowsPlatformError> {
        hotkeys.iter().map(plan_hotkey).collect()
    }

    pub fn plan_window(options: &WindowOptions) -> WindowsWindowPlan {
        WindowsWindowPlan {
            id: options.id.as_str().to_string(),
            title: options.title.clone(),
            style: window_style(options),
            ex_style: window_ex_style(options),
            width: options.width.round() as i32,
            height: options.height.round() as i32,
            min_width: options.min_width.map(|value| value.round() as i32),
            min_height: options.min_height.map(|value| value.round() as i32),
            visible_on_start: options.visible_on_start,
            skip_taskbar: options.skip_taskbar,
            uses_acrylic: options.frame == WindowFrame::Acrylic,
            placement: None,
        }
    }

    pub fn apply_window_options_to_hwnd(
        hwnd: isize,
        options: &WindowOptions,
    ) -> Result<(), WindowsPlatformError> {
        native::apply_window_ex_style(hwnd, window_ex_style(options))
    }

    pub fn resolve_window_placement(
        options: &WindowOptions,
    ) -> Result<ResolvedWindowPlacement, WindowsPlatformError> {
        let cursor = native::cursor_position()?;
        let monitor = native::monitor_metrics_for_point(cursor)?;

        Ok(Self::resolve_window_placement_for_monitor(
            options, cursor, monitor,
        ))
    }

    pub fn resolve_window_placement_for(
        options: &WindowOptions,
        cursor: WindowsPoint,
        work_area: WindowsRect,
    ) -> ResolvedWindowPlacement {
        Self::resolve_window_placement_for_monitor(
            options,
            cursor,
            WindowsMonitorMetrics::new(work_area, 96),
        )
    }

    pub fn resolve_window_placement_for_monitor(
        options: &WindowOptions,
        cursor: WindowsPoint,
        monitor: WindowsMonitorMetrics,
    ) -> ResolvedWindowPlacement {
        resolve_window_placement_with(options, cursor, monitor)
    }

    pub fn resolve_window_placement_for_work_areas(
        options: &WindowOptions,
        cursor: WindowsPoint,
        work_areas: &[WindowsRect],
    ) -> Option<ResolvedWindowPlacement> {
        let work_area = select_work_area_for_point(cursor, work_areas)?;
        Some(Self::resolve_window_placement_for(
            options, cursor, work_area,
        ))
    }

    pub fn plan_window_with_resolved_placement(
        options: &WindowOptions,
    ) -> Result<WindowsWindowPlan, WindowsPlatformError> {
        let mut plan = Self::plan_window(options);
        let placement = Self::resolve_window_placement(options)?;
        plan.width = placement.width;
        plan.height = placement.height;
        plan.placement = Some(placement);
        Ok(plan)
    }

    pub fn plan_tray<Message>(tray: &TrayMenu<Message>) -> Option<WindowsTrayPlan> {
        if tray.items.is_empty() {
            None
        } else {
            Some(WindowsTrayPlan {
                tooltip: tray.tooltip.clone(),
                callback_message: native::wm_user() + 1,
                item_count: tray.items.len(),
            })
        }
    }

    pub fn plan_named_events<Message>(
        events: &[NamedEventRegistration<Message>],
    ) -> Vec<WindowsNamedEventPlan> {
        events
            .iter()
            .map(|event| WindowsNamedEventPlan {
                name: event.name.clone(),
                auto_reset: event.auto_reset,
                action_kind: event.action.kind(),
            })
            .collect()
    }

    pub fn plan_named_event_subscription(name: &str, auto_reset: bool) -> WindowsNamedEventPlan {
        WindowsNamedEventPlan {
            name: name.to_string(),
            auto_reset,
            action_kind: ActionKind::Message,
        }
    }

    pub fn plan_shell_verbs(verbs: &[ShellVerb]) -> Vec<WindowsShellVerbPlan> {
        verbs.iter().map(plan_shell_verb).collect()
    }

    pub fn plan_protocol_registrations(
        protocols: &[ProtocolRegistration],
    ) -> Vec<WindowsProtocolRegistrationPlan> {
        protocols.iter().map(plan_protocol_registration).collect()
    }

    pub fn plan_desktop_integration<Message>(
        desktop: &DesktopIntegrationPlan<Message>,
    ) -> WindowsDesktopIntegrationPlan {
        WindowsDesktopIntegrationPlan {
            tray: desktop.tray_menu.as_ref().and_then(Self::plan_tray),
            named_events: Self::plan_named_events(&desktop.named_events),
            shell_verbs: Self::plan_shell_verbs(&desktop.shell_verbs),
            protocol_registrations: Self::plan_protocol_registrations(
                &desktop.protocol_registrations,
            ),
        }
    }

    pub fn register_shell_verb(
        plan: &WindowsShellVerbPlan,
        executable_path: &str,
    ) -> Result<(), WindowsPlatformError> {
        for (registry_key_path, command_key_path) in plan
            .registry_key_paths
            .iter()
            .zip(plan.command_key_paths.iter())
        {
            native::write_current_user_registry_value_string(registry_key_path, None, &plan.label)?;
            native::write_current_user_registry_value_string(
                registry_key_path,
                Some("Icon"),
                executable_path,
            )?;
            native::write_current_user_registry_value_string(
                command_key_path,
                None,
                &plan.command_line(executable_path),
            )?;
        }

        Ok(())
    }

    pub fn unregister_shell_verb(plan: &WindowsShellVerbPlan) -> Result<(), WindowsPlatformError> {
        for registry_key_path in &plan.registry_key_paths {
            native::delete_current_user_registry_tree(registry_key_path)?;
        }

        Ok(())
    }

    pub fn register_protocol_registration(
        plan: &WindowsProtocolRegistrationPlan,
        executable_path: &str,
    ) -> Result<(), WindowsPlatformError> {
        native::write_current_user_registry_value_string(
            &plan.registry_key_path,
            None,
            &plan.description,
        )?;
        if plan.url_protocol_marker {
            native::write_current_user_registry_value_string(
                &plan.registry_key_path,
                Some("URL Protocol"),
                "",
            )?;
        }
        native::write_current_user_registry_value_string(
            &plan.command_key_path,
            None,
            &plan.command_line(executable_path),
        )?;

        Ok(())
    }

    pub fn unregister_protocol_registration(
        plan: &WindowsProtocolRegistrationPlan,
    ) -> Result<(), WindowsPlatformError> {
        native::delete_current_user_registry_tree(&plan.registry_key_path)
    }

    pub fn register_desktop_registry_entries(
        plan: &WindowsDesktopIntegrationPlan,
        executable_path: &str,
    ) -> Result<(), WindowsPlatformError> {
        for shell_verb in &plan.shell_verbs {
            Self::register_shell_verb(shell_verb, executable_path)?;
        }
        for protocol in &plan.protocol_registrations {
            Self::register_protocol_registration(protocol, executable_path)?;
        }

        Ok(())
    }

    pub fn unregister_desktop_registry_entries(
        plan: &WindowsDesktopIntegrationPlan,
    ) -> Result<(), WindowsPlatformError> {
        for shell_verb in &plan.shell_verbs {
            Self::unregister_shell_verb(shell_verb)?;
        }
        for protocol in &plan.protocol_registrations {
            Self::unregister_protocol_registration(protocol)?;
        }

        Ok(())
    }

    pub fn plan_uia_tree(root: &A11yNode) -> WindowsUiaTreePlan {
        WindowsUiaTreePlan {
            root: plan_uia_node(root),
        }
    }

    pub fn plan_subscription<Message>(
        subscription: &Subscription<Message>,
    ) -> Result<Vec<WindowsRegistration>, WindowsPlatformError> {
        let mut registrations = Vec::new();
        collect_subscription(subscription, &mut registrations)?;
        Ok(registrations)
    }

    pub fn native_clipboard_format(format: ClipboardFormat) -> Option<u32> {
        native::clipboard_format(format)
    }

    pub fn is_clipboard_format_available(
        format: ClipboardFormat,
    ) -> Result<bool, WindowsPlatformError> {
        native::is_clipboard_format_available(format)
    }

    pub fn register_global_hotkey(
        hotkey: &Hotkey,
    ) -> Result<WindowsHotkeyHandle, WindowsPlatformError> {
        let native_hotkey = plan_hotkey(hotkey)?;
        native::register_global_hotkey(native_hotkey)
    }

    pub fn wait_for_hotkey_event(
        handles: &[&WindowsHotkeyHandle],
        timeout: Duration,
    ) -> Result<Option<WindowsHotkeyEvent>, WindowsPlatformError> {
        let start = Instant::now();

        loop {
            if let Some(message) = native::poll_hotkey_message()? {
                if let Some(event) = map_hotkey_message(handles, message) {
                    return Ok(Some(event));
                }
            }

            if start.elapsed() >= timeout {
                return Ok(None);
            }

            std::thread::sleep(Duration::from_millis(10));
        }
    }

    pub fn create_named_event(
        name: &str,
        auto_reset: bool,
    ) -> Result<WindowsNamedEventHandle, WindowsPlatformError> {
        native::create_named_event(name, auto_reset)
    }

    pub fn signal_named_event(name: &str) -> Result<bool, WindowsPlatformError> {
        native::signal_named_event(name)
    }

    pub fn wait_for_named_event(
        handle: &WindowsNamedEventHandle,
        timeout: Duration,
    ) -> Result<Option<WindowsNamedEvent>, WindowsPlatformError> {
        native::wait_for_named_event(handle, timeout)
    }

    pub fn send_hotkey_input_for_probe(hotkey: &Hotkey) -> Result<(), WindowsPlatformError> {
        let native_hotkey = plan_hotkey(hotkey)?;
        native::send_hotkey_input_for_probe(&native_hotkey)
    }

    pub fn send_unicode_text_input(text: &str) -> Result<(), WindowsPlatformError> {
        native::send_unicode_text_input_for_probe(text)
    }

    pub fn send_unicode_text_input_for_probe(text: &str) -> Result<(), WindowsPlatformError> {
        Self::send_unicode_text_input(text)
    }

    pub fn send_clipboard_text_paste(text: &str) -> Result<(), WindowsPlatformError> {
        native::send_clipboard_text_paste_for_probe(text)
    }

    pub fn send_clipboard_text_paste_for_probe(text: &str) -> Result<(), WindowsPlatformError> {
        Self::send_clipboard_text_paste(text)
    }

    pub fn capture_text_insertion_target() -> Result<(), WindowsPlatformError> {
        native::capture_text_insertion_target()
    }

    pub fn capture_screen_region() -> Result<ScreenCaptureResult, WindowsPlatformError> {
        Self::capture_screen_region_with_request(ScreenCaptureRequest::virtual_desktop())
    }

    pub fn capture_screen_region_with_request(
        request: ScreenCaptureRequest,
    ) -> Result<ScreenCaptureResult, WindowsPlatformError> {
        native::capture_screen_region(request)
    }

    pub fn capture_screen_windows() -> Result<Vec<ScreenWindow>, WindowsPlatformError> {
        Self::capture_screen_windows_with_request(ScreenWindowSnapshotRequest::new())
    }

    pub fn capture_screen_windows_with_request(
        request: ScreenWindowSnapshotRequest,
    ) -> Result<Vec<ScreenWindow>, WindowsPlatformError> {
        native::capture_screen_windows(request)
    }

    pub fn has_text_insertion_target() -> Result<bool, WindowsPlatformError> {
        native::has_text_insertion_target()
    }

    pub fn insert_text(text: &str) -> Result<(), WindowsPlatformError> {
        native::insert_text(text)
    }

    pub fn open_url(url: &str) -> Result<(), WindowsPlatformError> {
        native::open_url(url)
    }

    pub fn speak_text(text: &str, language: Option<&str>) -> Result<(), WindowsPlatformError> {
        native::speak_text(text, language)
    }

    pub fn clipboard_text_snapshot_for_probe(
    ) -> Result<WindowsClipboardTextSnapshot, WindowsPlatformError> {
        native::clipboard_text_snapshot_for_probe()
    }

    pub fn restore_clipboard_text_for_probe(
        snapshot: &WindowsClipboardTextSnapshot,
    ) -> Result<(), WindowsPlatformError> {
        native::restore_clipboard_text_for_probe(snapshot)
    }

    pub fn current_process_memory() -> Result<WindowsProcessMemory, WindowsPlatformError> {
        native::current_process_memory()
    }

    pub fn protect_data(
        plaintext: &[u8],
        optional_entropy: &[u8],
        scope: WindowsDataProtectionScope,
    ) -> Result<Vec<u8>, WindowsPlatformError> {
        native::protect_data(plaintext, optional_entropy, scope)
    }

    pub fn unprotect_data(
        protected_bytes: &[u8],
        optional_entropy: &[u8],
        scope: WindowsDataProtectionScope,
    ) -> Result<Vec<u8>, WindowsPlatformError> {
        native::unprotect_data(protected_bytes, optional_entropy, scope)
    }

    pub fn write_current_user_registry_string(
        key_path: &str,
        value: &str,
    ) -> Result<(), WindowsPlatformError> {
        native::write_current_user_registry_value_string(key_path, None, value)
    }

    pub fn write_current_user_registry_value_string(
        key_path: &str,
        value_name: Option<&str>,
        value: &str,
    ) -> Result<(), WindowsPlatformError> {
        native::write_current_user_registry_value_string(key_path, value_name, value)
    }

    pub fn read_current_user_registry_string(
        key_path: &str,
    ) -> Result<Option<String>, WindowsPlatformError> {
        native::read_current_user_registry_value_string(key_path, None)
    }

    pub fn read_current_user_registry_value_string(
        key_path: &str,
        value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsPlatformError> {
        native::read_current_user_registry_value_string(key_path, value_name)
    }

    pub fn read_local_machine_registry_value_string(
        key_path: &str,
        value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsPlatformError> {
        native::read_local_machine_registry_value_string(key_path, value_name)
    }

    pub fn delete_current_user_registry_key(key_path: &str) -> Result<(), WindowsPlatformError> {
        native::delete_current_user_registry_key(key_path)
    }

    pub fn delete_current_user_registry_tree(key_path: &str) -> Result<(), WindowsPlatformError> {
        native::delete_current_user_registry_tree(key_path)
    }
}

#[derive(Debug)]
pub struct WindowsHotkeyHandle {
    native_hotkey: WindowsHotkey,
    #[cfg(windows)]
    hwnd: windows_sys::Win32::Foundation::HWND,
}

impl WindowsHotkeyHandle {
    pub fn hotkey(&self) -> &WindowsHotkey {
        &self.native_hotkey
    }
}

impl Drop for WindowsHotkeyHandle {
    fn drop(&mut self) {
        native::unregister_global_hotkey(self);
    }
}

#[derive(Debug)]
pub struct WindowsNamedEventHandle {
    name: String,
    native: native::NamedEventHandle,
}

impl WindowsNamedEventHandle {
    pub fn name(&self) -> &str {
        &self.name
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct NativeHotkeyMessage {
    native_id: i32,
    modifiers: u32,
    virtual_key: u32,
}

fn map_hotkey_message(
    handles: &[&WindowsHotkeyHandle],
    message: NativeHotkeyMessage,
) -> Option<WindowsHotkeyEvent> {
    handles
        .iter()
        .map(|handle| handle.hotkey())
        .find(|hotkey| hotkey.native_id == message.native_id)
        .map(|hotkey| WindowsHotkeyEvent {
            id: hotkey.id.clone(),
            native_id: message.native_id,
            modifiers: message.modifiers,
            virtual_key: message.virtual_key,
        })
}

fn collect_subscription<Message>(
    subscription: &Subscription<Message>,
    registrations: &mut Vec<WindowsRegistration>,
) -> Result<(), WindowsPlatformError> {
    match subscription {
        Subscription::None => {}
        Subscription::Event { kind, .. } => match kind {
            SubscriptionKind::Hotkey(hotkey) => {
                registrations.push(WindowsRegistration::Hotkey(plan_hotkey(hotkey)?));
            }
            SubscriptionKind::Clipboard => registrations.push(WindowsRegistration::Clipboard),
            SubscriptionKind::NamedEvent { name, auto_reset } => {
                registrations.push(WindowsRegistration::NamedEvent(
                    WindowsPlatformAdapter::plan_named_event_subscription(name, *auto_reset),
                ));
            }
            SubscriptionKind::Theme => registrations.push(WindowsRegistration::Theme),
            SubscriptionKind::Tray => {
                registrations.push(WindowsRegistration::Tray(WindowsTrayPlan {
                    tooltip: String::new(),
                    callback_message: native::wm_user() + 1,
                    item_count: 0,
                }))
            }
            SubscriptionKind::Window(id) => {
                registrations.push(WindowsRegistration::Window(id.as_str().to_string()));
            }
            SubscriptionKind::Custom(kind) => registrations.push(WindowsRegistration::ShellVerb(
                plan_shell_verb(&ShellVerb::new(kind.clone(), kind.clone())),
            )),
        },
        Subscription::Batch(values) => {
            for value in values {
                collect_subscription(value, registrations)?;
            }
        }
    }

    Ok(())
}

fn plan_shell_verb(verb: &ShellVerb) -> WindowsShellVerbPlan {
    let registry_key_paths = shell_verb_registry_key_paths(verb);
    let command_key_paths = registry_key_paths
        .iter()
        .map(|path| format!(r"{path}\command"))
        .collect();

    WindowsShellVerbPlan {
        id: verb.id.clone(),
        label: verb.label.clone(),
        accepts_files: verb.accepts_files,
        accepts_directory_background: verb.accepts_directory_background,
        registry_key_paths,
        command_key_paths,
        command_arguments: verb.arguments.clone(),
    }
}

fn shell_verb_registry_key_paths(verb: &ShellVerb) -> Vec<String> {
    let mut paths = Vec::new();

    if verb.accepts_files {
        paths.push(format!(r"Software\Classes\*\shell\{}", verb.id));
    }

    if verb.accepts_directory_background {
        paths.push(format!(
            r"Software\Classes\Directory\Background\shell\{}",
            verb.id
        ));
    }

    paths
}

fn plan_protocol_registration(protocol: &ProtocolRegistration) -> WindowsProtocolRegistrationPlan {
    let registry_key_path = format!(r"Software\Classes\{}", protocol.scheme);
    let command_key_path = format!(r"{registry_key_path}\shell\open\command");

    WindowsProtocolRegistrationPlan {
        scheme: protocol.scheme.clone(),
        description: protocol.description.clone(),
        url_protocol_marker: true,
        registry_key_path,
        command_key_path,
        command_arguments: protocol.arguments.clone(),
    }
}

fn windows_command_line(
    executable_path: &str,
    arguments: &[String],
    quote_all_arguments: bool,
) -> String {
    let mut parts = vec![quote_windows_argument(executable_path, true)];
    parts.extend(
        arguments
            .iter()
            .map(|argument| quote_windows_argument(argument, quote_all_arguments)),
    );
    parts.join(" ")
}

fn quote_windows_argument(value: &str, force: bool) -> String {
    let needs_quotes = force
        || value.is_empty()
        || value
            .chars()
            .any(|character| character.is_whitespace() || character == '"');

    if !needs_quotes {
        return value.to_string();
    }

    let mut quoted = String::from("\"");
    let mut backslash_count = 0usize;

    for character in value.chars() {
        match character {
            '\\' => backslash_count += 1,
            '"' => {
                quoted.extend(std::iter::repeat('\\').take(backslash_count * 2 + 1));
                quoted.push('"');
                backslash_count = 0;
            }
            _ => {
                quoted.extend(std::iter::repeat('\\').take(backslash_count));
                backslash_count = 0;
                quoted.push(character);
            }
        }
    }

    quoted.extend(std::iter::repeat('\\').take(backslash_count * 2));
    quoted.push('"');
    quoted
}

fn plan_hotkey(hotkey: &Hotkey) -> Result<WindowsHotkey, WindowsPlatformError> {
    Ok(WindowsHotkey {
        id: hotkey.id.clone(),
        native_id: native_hotkey_id(&hotkey.id),
        modifiers: hotkey_modifiers(&hotkey.modifiers),
        virtual_key: hotkey_virtual_key(&hotkey.key)?,
    })
}

fn plan_uia_node(node: &A11yNode) -> WindowsUiaNodePlan {
    WindowsUiaNodePlan {
        control_type: uia_control_type(&node.role),
        name: node.name.clone(),
        description: node.description.clone(),
        help_text: node.help_text.clone(),
        focusable: node.focusable,
        children: node.children.iter().map(plan_uia_node).collect(),
    }
}

fn uia_control_type(role: &A11yRole) -> WindowsUiaControlType {
    match role {
        A11yRole::Application | A11yRole::Dialog => WindowsUiaControlType::Window,
        A11yRole::Button => WindowsUiaControlType::Button,
        A11yRole::CheckBox => WindowsUiaControlType::CheckBox,
        A11yRole::ComboBox => WindowsUiaControlType::ComboBox,
        A11yRole::Document => WindowsUiaControlType::Document,
        A11yRole::Group | A11yRole::Navigation => WindowsUiaControlType::Group,
        A11yRole::List => WindowsUiaControlType::List,
        A11yRole::ListItem => WindowsUiaControlType::ListItem,
        A11yRole::Pane | A11yRole::ScrollView => WindowsUiaControlType::Pane,
        A11yRole::Slider => WindowsUiaControlType::Slider,
        A11yRole::StaticText => WindowsUiaControlType::Text,
        A11yRole::TextInput => WindowsUiaControlType::Edit,
    }
}

fn native_hotkey_id(id: &str) -> i32 {
    let mut hasher = DefaultHasher::new();
    id.hash(&mut hasher);
    ((hasher.finish() % 0x7fff) + 1) as i32
}

fn hotkey_modifiers(modifiers: &[HotkeyModifier]) -> u32 {
    modifiers.iter().fold(0, |acc, modifier| {
        acc | match modifier {
            HotkeyModifier::Control => native::mod_control(),
            HotkeyModifier::Alt => native::mod_alt(),
            HotkeyModifier::Shift => native::mod_shift(),
            HotkeyModifier::Logo => native::mod_win(),
        }
    })
}

fn hotkey_virtual_key(key: &HotkeyKey) -> Result<u32, WindowsPlatformError> {
    match key {
        HotkeyKey::Character(ch) if ch.is_ascii_alphanumeric() => {
            Ok(ch.to_ascii_uppercase() as u32)
        }
        HotkeyKey::Character(ch) => Err(WindowsPlatformError::UnsupportedHotkeyKey(ch.to_string())),
        HotkeyKey::Function(value) if (1..=24).contains(value) => {
            Ok(native::vk_f1() + u32::from(*value) - 1)
        }
        HotkeyKey::Function(value) => Err(WindowsPlatformError::InvalidFunctionKey(*value)),
        HotkeyKey::Named(value) => native_named_key(value)
            .ok_or_else(|| WindowsPlatformError::UnsupportedHotkeyKey(value.clone())),
    }
}

fn native_named_key(value: &str) -> Option<u32> {
    match value.to_ascii_lowercase().as_str() {
        "backspace" => Some(native::vk_back()),
        "delete" => Some(native::vk_delete()),
        "down" | "arrowdown" => Some(native::vk_down()),
        "end" => Some(native::vk_end()),
        "enter" | "return" => Some(native::vk_return()),
        "escape" | "esc" => Some(native::vk_escape()),
        "home" => Some(native::vk_home()),
        "left" | "arrowleft" => Some(native::vk_left()),
        "right" | "arrowright" => Some(native::vk_right()),
        "space" => Some(native::vk_space()),
        "tab" => Some(native::vk_tab()),
        "up" | "arrowup" => Some(native::vk_up()),
        _ => None,
    }
}

fn window_style(options: &WindowOptions) -> u32 {
    let mut style = match options.frame {
        WindowFrame::Standard => native::ws_overlapped_window(),
        WindowFrame::Borderless | WindowFrame::Acrylic => native::ws_popup(),
    };

    match options.resize_mode {
        WindowResizeMode::CanResize => {}
        WindowResizeMode::CanMinimize => {
            style &= !native::ws_thickframe();
            style |= native::ws_minimize_box();
        }
        WindowResizeMode::Fixed => {
            style &= !native::ws_thickframe();
            style &= !native::ws_minimize_box();
        }
    }

    style
}

fn window_ex_style(options: &WindowOptions) -> u32 {
    let mut ex_style = 0;

    match options.level {
        WindowLevel::Normal => {}
        WindowLevel::TopMost => ex_style |= native::ws_ex_topmost(),
        WindowLevel::ToolWindow => {
            ex_style |= native::ws_ex_toolwindow();
            ex_style |= native::ws_ex_topmost();
        }
    }

    if options.skip_taskbar {
        ex_style |= native::ws_ex_toolwindow();
    }

    if options.no_activate {
        ex_style |= native::ws_ex_noactivate();
    }

    ex_style
}

fn resolve_window_placement_with(
    options: &WindowOptions,
    physical_cursor: WindowsPoint,
    monitor: WindowsMonitorMetrics,
) -> ResolvedWindowPlacement {
    let work_area = monitor.work_area_dips();
    let monitor_area = monitor.monitor_area_dips();
    let cursor = physical_point_to_dips(physical_cursor, monitor.scale_factor());
    let requested_width = match options.placement {
        WindowPlacement::Monitor => monitor_area.width(),
        WindowPlacement::WorkArea => work_area.width(),
        _ => options.width.round().max(1.0) as i32,
    };
    let requested_height = match options.placement {
        WindowPlacement::Monitor => monitor_area.height(),
        WindowPlacement::WorkArea => work_area.height(),
        _ => options.height.round().max(1.0) as i32,
    };
    let constraint_area = match options.placement {
        WindowPlacement::Monitor => monitor_area,
        _ => work_area,
    };
    let width = constrained_size(
        requested_width,
        options.min_width,
        constraint_area.width(),
        options.screen_constraint,
    );
    let height = constrained_size(
        requested_height,
        options.min_height,
        constraint_area.height(),
        options.screen_constraint,
    );
    // A borderless window sized exactly to the monitor triggers DWM's
    // fullscreen optimization, which can leave the swapchain unpresented (the
    // window shows as blank white). Oversize by one DIP after constraining so
    // the overlay stays visually full-screen while opting out of that path;
    // the extra row hangs below the monitor edge.
    let height = if options.placement == WindowPlacement::Monitor
        && options.frame == WindowFrame::Borderless
    {
        height + 1
    } else {
        height
    };

    let (x, y) = match options.placement {
        WindowPlacement::Center => (
            work_area.left + (work_area.width() - width) / 2,
            work_area.top + (work_area.height() - height) / 2,
        ),
        WindowPlacement::Monitor => (monitor_area.left, monitor_area.top),
        WindowPlacement::WorkArea => (work_area.left, work_area.top),
        WindowPlacement::CursorOffset { x, y } => {
            (cursor.x + x.round() as i32, cursor.y + y.round() as i32)
        }
        WindowPlacement::TopRight { margin_x, margin_y } => (
            work_area.right - width - margin_x.round() as i32,
            work_area.top + margin_y.round() as i32,
        ),
        WindowPlacement::Explicit { x, y } => (x.round() as i32, y.round() as i32),
    };

    let (x, y) = if clamps_position(options.screen_constraint) {
        (
            clamp_axis(x, width, constraint_area.left, constraint_area.right),
            clamp_axis(y, height, constraint_area.top, constraint_area.bottom),
        )
    } else {
        (x, y)
    };

    ResolvedWindowPlacement {
        x,
        y,
        width,
        height,
        dpi: monitor.dpi,
        work_area,
        physical_work_area: monitor.physical_work_area,
    }
}

fn physical_rect_to_dips(rect: WindowsRect, scale_factor: f32) -> WindowsRect {
    WindowsRect {
        left: physical_axis_to_dips(rect.left, scale_factor),
        top: physical_axis_to_dips(rect.top, scale_factor),
        right: physical_axis_to_dips(rect.right, scale_factor),
        bottom: physical_axis_to_dips(rect.bottom, scale_factor),
    }
}

fn physical_point_to_dips(point: WindowsPoint, scale_factor: f32) -> WindowsPoint {
    WindowsPoint {
        x: physical_axis_to_dips(point.x, scale_factor),
        y: physical_axis_to_dips(point.y, scale_factor),
    }
}

fn physical_axis_to_dips(value: i32, scale_factor: f32) -> i32 {
    if scale_factor <= f32::EPSILON {
        value
    } else {
        (value as f32 / scale_factor).round() as i32
    }
}

fn constrained_size(
    requested: i32,
    _min_size: Option<f32>,
    available: i32,
    constraint: WindowScreenConstraint,
) -> i32 {
    if constraint != WindowScreenConstraint::SizeAndPosition {
        return requested.max(1);
    }

    let available = available.max(1);
    requested.min(available).max(1)
}

fn clamps_position(constraint: WindowScreenConstraint) -> bool {
    matches!(
        constraint,
        WindowScreenConstraint::Position | WindowScreenConstraint::SizeAndPosition
    )
}

fn clamp_axis(value: i32, size: i32, min: i32, max: i32) -> i32 {
    if max - min <= size {
        return min;
    }

    value.clamp(min, max - size)
}

fn select_work_area_for_point(
    point: WindowsPoint,
    work_areas: &[WindowsRect],
) -> Option<WindowsRect> {
    work_areas
        .iter()
        .copied()
        .find(|area| contains_point(*area, point))
        .or_else(|| {
            work_areas
                .iter()
                .copied()
                .min_by_key(|area| squared_distance_to_rect(point, *area))
        })
}

fn contains_point(area: WindowsRect, point: WindowsPoint) -> bool {
    point.x >= area.left && point.x < area.right && point.y >= area.top && point.y < area.bottom
}

fn squared_distance_to_rect(point: WindowsPoint, area: WindowsRect) -> i64 {
    let dx = if point.x < area.left {
        area.left - point.x
    } else if point.x >= area.right {
        point.x - area.right + 1
    } else {
        0
    } as i64;
    let dy = if point.y < area.top {
        area.top - point.y
    } else if point.y >= area.bottom {
        point.y - area.bottom + 1
    } else {
        0
    } as i64;

    dx * dx + dy * dy
}

#[cfg(windows)]
mod native {
    use std::io::Write;
    use std::process::Command;
    use std::ptr::{null, null_mut};
    use std::sync::Mutex;
    use std::time::{Duration, SystemTime, UNIX_EPOCH};

    use super::{
        ClipboardFormat, NativeHotkeyMessage, WindowsClipboardFormatSnapshot,
        WindowsClipboardTextSnapshot, WindowsDataProtectionScope, WindowsHotkey,
        WindowsHotkeyHandle, WindowsMonitorMetrics, WindowsNamedEvent, WindowsNamedEventHandle,
        WindowsPlatformError, WindowsPoint, WindowsProcessMemory, WindowsRect,
    };
    use win_fluent::platform::{
        ScreenCaptureRequest, ScreenCaptureResult, ScreenRect, ScreenWindow,
        ScreenWindowSnapshotRequest,
    };
    use windows_sys::Win32::Foundation::{
        CloseHandle, GetLastError, GlobalFree, SetLastError, ERROR_FILE_NOT_FOUND, ERROR_SUCCESS,
        HANDLE, HWND, LPARAM, POINT, RECT, WAIT_OBJECT_0, WAIT_TIMEOUT,
    };
    use windows_sys::Win32::Graphics::Gdi::{
        BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, GetDC,
        GetDIBits, GetMonitorInfoW, MonitorFromPoint, ReleaseDC, SelectObject, BITMAPINFO,
        BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS, HBITMAP, HDC, HGDIOBJ, MONITORINFO,
        MONITOR_DEFAULTTONEAREST, SRCCOPY,
    };
    use windows_sys::Win32::Security::Cryptography::{
        CryptProtectData, CryptUnprotectData, CRYPTPROTECT_LOCAL_MACHINE,
        CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
    };
    use windows_sys::Win32::System::DataExchange::{
        CloseClipboard, EmptyClipboard, EnumClipboardFormats, GetClipboardData,
        IsClipboardFormatAvailable, OpenClipboard, SetClipboardData,
    };
    use windows_sys::Win32::System::Memory::{
        GlobalAlloc, GlobalLock, GlobalSize, GlobalUnlock, GMEM_MOVEABLE,
    };
    use windows_sys::Win32::System::Ole::CF_UNICODETEXT;
    use windows_sys::Win32::System::ProcessStatus::{
        K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS, PROCESS_MEMORY_COUNTERS_EX,
    };
    use windows_sys::Win32::System::Registry::{
        RegCloseKey, RegCreateKeyExW, RegDeleteKeyW, RegDeleteTreeW, RegOpenKeyExW,
        RegQueryValueExW, RegSetValueExW, HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ,
        KEY_SET_VALUE, REG_SZ,
    };
    use windows_sys::Win32::System::Threading::{
        AttachThreadInput, CreateEventExW, GetCurrentProcess, GetCurrentThreadId, OpenEventW,
        SetEvent, WaitForSingleObject, CREATE_EVENT_MANUAL_RESET, EVENT_MODIFY_STATE,
        SYNCHRONIZATION_SYNCHRONIZE,
    };
    use windows_sys::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI};
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        RegisterHotKey, SendInput, UnregisterHotKey, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT,
        KEYEVENTF_KEYUP, KEYEVENTF_UNICODE, MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN, VK_BACK,
        VK_CONTROL, VK_DELETE, VK_DOWN, VK_END, VK_ESCAPE, VK_F1, VK_HOME, VK_LEFT, VK_LWIN,
        VK_MENU, VK_RETURN, VK_RIGHT, VK_SHIFT, VK_SPACE, VK_TAB, VK_UP,
    };
    use windows_sys::Win32::UI::Shell::ShellExecuteW;
    #[cfg(test)]
    use windows_sys::Win32::UI::WindowsAndMessaging::PostThreadMessageW;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        EnumChildWindows, EnumWindows, GetClassNameW, GetCursorPos, GetForegroundWindow, GetParent,
        GetSystemMetrics, GetWindowLongPtrW, GetWindowRect, GetWindowTextLengthW, GetWindowTextW,
        GetWindowThreadProcessId, IsWindow, IsWindowVisible, PeekMessageW, SetForegroundWindow,
        SetWindowLongPtrW, SetWindowPos, GWL_EXSTYLE, HWND_NOTOPMOST, HWND_TOPMOST, MSG, PM_REMOVE,
        SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
        SWP_FRAMECHANGED, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SWP_NOZORDER, SW_SHOWNORMAL,
        WM_HOTKEY, WM_USER, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_MINIMIZEBOX,
        WS_OVERLAPPEDWINDOW, WS_POPUP, WS_THICKFRAME,
    };

    static TEXT_INSERTION_TARGET: Mutex<isize> = Mutex::new(0);

    #[derive(Debug)]
    pub struct NamedEventHandle {
        handle: HANDLE,
    }

    impl Drop for NamedEventHandle {
        fn drop(&mut self) {
            // Safety: handle is owned by this wrapper and was returned by CreateEventW/OpenEventW.
            let _ = unsafe { CloseHandle(self.handle) };
        }
    }

    struct RegistryKey(HKEY);

    impl Drop for RegistryKey {
        fn drop(&mut self) {
            // Safety: HKEY is owned by this guard and was returned by RegCreateKeyExW/RegOpenKeyExW.
            let _ = unsafe { RegCloseKey(self.0) };
        }
    }

    struct ScreenDc(HDC);

    impl Drop for ScreenDc {
        fn drop(&mut self) {
            // Safety: HDC was acquired with GetDC(NULL) and is released against the same desktop window.
            let _ = unsafe { ReleaseDC(null_mut(), self.0) };
        }
    }

    struct CompatibleDc(HDC);

    impl Drop for CompatibleDc {
        fn drop(&mut self) {
            // Safety: HDC was created by CreateCompatibleDC and is owned by this guard.
            let _ = unsafe { DeleteDC(self.0) };
        }
    }

    struct BitmapHandle(HBITMAP);

    impl Drop for BitmapHandle {
        fn drop(&mut self) {
            // Safety: HBITMAP was created by CreateCompatibleBitmap and is owned by this guard.
            let _ = unsafe { DeleteObject(self.0 as HGDIOBJ) };
        }
    }

    struct SelectedObject {
        dc: HDC,
        previous: HGDIOBJ,
    }

    impl Drop for SelectedObject {
        fn drop(&mut self) {
            // Safety: previous was returned by SelectObject for this DC, restoring the original object.
            let _ = unsafe { SelectObject(self.dc, self.previous) };
        }
    }

    pub fn register_global_hotkey(
        native_hotkey: WindowsHotkey,
    ) -> Result<WindowsHotkeyHandle, WindowsPlatformError> {
        let hwnd: HWND = null_mut();
        // Safety: RegisterHotKey is called with a null HWND to bind the hotkey to the
        // current thread message queue. The handle object unregisters the same id on drop.
        let ok = unsafe {
            RegisterHotKey(
                hwnd,
                native_hotkey.native_id,
                native_hotkey.modifiers,
                native_hotkey.virtual_key,
            )
        };

        if ok == 0 {
            return Err(last_error("RegisterHotKey"));
        }

        Ok(WindowsHotkeyHandle {
            native_hotkey,
            hwnd,
        })
    }

    pub fn unregister_global_hotkey(handle: &WindowsHotkeyHandle) {
        // Safety: The id and HWND pair matches the successful RegisterHotKey call owned by handle.
        let _ = unsafe { UnregisterHotKey(handle.hwnd, handle.native_hotkey.native_id) };
    }

    pub fn poll_hotkey_message() -> Result<Option<NativeHotkeyMessage>, WindowsPlatformError> {
        let mut message = MSG::default();
        // Safety: PeekMessageW writes to a valid MSG pointer and is constrained to WM_HOTKEY.
        let has_message =
            unsafe { PeekMessageW(&mut message, null_mut(), WM_HOTKEY, WM_HOTKEY, PM_REMOVE) };

        if has_message == 0 {
            return Ok(None);
        }

        let lparam = message.lParam as u32;
        Ok(Some(NativeHotkeyMessage {
            native_id: message.wParam as i32,
            modifiers: lparam & 0xffff,
            virtual_key: (lparam >> 16) & 0xffff,
        }))
    }

    pub fn create_named_event(
        name: &str,
        auto_reset: bool,
    ) -> Result<WindowsNamedEventHandle, WindowsPlatformError> {
        let wide_name = wide_null(name);
        let flags = if auto_reset {
            0
        } else {
            CREATE_EVENT_MANUAL_RESET
        };
        // Safety: security attributes are null, the UTF-16 event name is null-terminated, and
        // the returned handle is owned by WindowsNamedEventHandle.
        let handle = unsafe {
            CreateEventExW(
                null(),
                wide_name.as_ptr(),
                flags,
                EVENT_MODIFY_STATE | SYNCHRONIZATION_SYNCHRONIZE,
            )
        };

        if handle.is_null() {
            return Err(last_error("CreateEventExW"));
        }

        Ok(WindowsNamedEventHandle {
            name: name.to_string(),
            native: NamedEventHandle { handle },
        })
    }

    pub fn signal_named_event(name: &str) -> Result<bool, WindowsPlatformError> {
        let wide_name = wide_null(name);
        // Safety: access mask requests only signal permission; name is null-terminated.
        let handle = unsafe { OpenEventW(EVENT_MODIFY_STATE, 0, wide_name.as_ptr()) };
        if handle.is_null() {
            let error = unsafe { GetLastError() };
            if error == 2 {
                return Ok(false);
            }

            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "OpenEventW",
                code: error,
            });
        }

        let _guard = NamedEventHandle { handle };
        // Safety: handle is a valid event handle opened with EVENT_MODIFY_STATE.
        if unsafe { SetEvent(handle) } == 0 {
            return Err(last_error("SetEvent"));
        }

        Ok(true)
    }

    pub fn wait_for_named_event(
        handle: &WindowsNamedEventHandle,
        timeout: Duration,
    ) -> Result<Option<WindowsNamedEvent>, WindowsPlatformError> {
        // Safety: handle.native.handle is a valid event handle owned by WindowsNamedEventHandle.
        match unsafe { WaitForSingleObject(handle.native.handle, timeout.as_millis() as u32) } {
            WAIT_OBJECT_0 => Ok(Some(WindowsNamedEvent {
                name: handle.name.clone(),
            })),
            WAIT_TIMEOUT => Ok(None),
            _ => Err(last_error("WaitForSingleObject")),
        }
    }

    pub fn send_hotkey_input_for_probe(hotkey: &WindowsHotkey) -> Result<(), WindowsPlatformError> {
        let mut inputs = Vec::new();
        let modifier_keys = modifier_virtual_keys(hotkey.modifiers);

        for key in &modifier_keys {
            inputs.push(keyboard_input(*key, 0));
        }
        inputs.push(keyboard_input(hotkey.virtual_key as u16, 0));
        inputs.push(keyboard_input(hotkey.virtual_key as u16, KEYEVENTF_KEYUP));
        for key in modifier_keys.iter().rev() {
            inputs.push(keyboard_input(*key, KEYEVENTF_KEYUP));
        }

        // Safety: inputs points to a contiguous array of INPUT values with the correct element size.
        let sent = unsafe {
            SendInput(
                inputs.len() as u32,
                inputs.as_ptr(),
                std::mem::size_of::<INPUT>() as i32,
            )
        };
        if sent != inputs.len() as u32 {
            return Err(last_error("SendInput"));
        }

        Ok(())
    }

    pub fn send_unicode_text_input_for_probe(text: &str) -> Result<(), WindowsPlatformError> {
        let mut inputs = Vec::new();
        for unit in text.encode_utf16() {
            inputs.push(unicode_keyboard_input(unit, 0));
            inputs.push(unicode_keyboard_input(unit, KEYEVENTF_KEYUP));
        }

        if inputs.is_empty() {
            return Ok(());
        }

        // Safety: inputs points to a contiguous array of INPUT values with the correct element size.
        let sent = unsafe {
            SendInput(
                inputs.len() as u32,
                inputs.as_ptr(),
                std::mem::size_of::<INPUT>() as i32,
            )
        };
        if sent != inputs.len() as u32 {
            return Err(last_error("SendInput"));
        }

        Ok(())
    }

    pub fn send_clipboard_text_paste_for_probe(text: &str) -> Result<(), WindowsPlatformError> {
        set_clipboard_text_for_probe(text)?;
        send_hotkey_input_for_probe(&WindowsHotkey {
            id: "clipboard-paste".to_string(),
            native_id: 0,
            modifiers: MOD_CONTROL,
            virtual_key: b'V' as u32,
        })
    }

    pub fn capture_text_insertion_target() -> Result<(), WindowsPlatformError> {
        // Safety: GetForegroundWindow reads the current desktop foreground window handle.
        let hwnd = unsafe { GetForegroundWindow() };
        let mut target = TEXT_INSERTION_TARGET
            .lock()
            .map_err(|_| WindowsPlatformError::TextInsertionTargetUnavailable)?;
        *target = hwnd as isize;
        Ok(())
    }

    pub fn apply_window_ex_style(hwnd: isize, ex_style: u32) -> Result<(), WindowsPlatformError> {
        let hwnd = hwnd as HWND;
        if !is_valid_window(hwnd) {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "IsWindow",
                code: 0,
            });
        }

        // Safety: hwnd was validated with IsWindow and GWL_EXSTYLE targets the window's extended style.
        let current = unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) } as u32;
        let managed_bits = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
        let next = (current & !managed_bits) | ex_style;
        if next != current {
            // Safety: SetLastError resets the current thread's Win32 error code before SetWindowLongPtrW.
            unsafe { SetLastError(ERROR_SUCCESS) };
            // Safety: hwnd was validated and next preserves unmanaged style bits while applying requested win_fluent bits.
            let previous = unsafe { SetWindowLongPtrW(hwnd, GWL_EXSTYLE, next as isize) };
            // Safety: GetLastError reads the current thread's Win32 error code.
            let error = unsafe { GetLastError() };
            if previous == 0 && error != ERROR_SUCCESS {
                return Err(WindowsPlatformError::NativeCallFailed {
                    operation: "SetWindowLongPtrW",
                    code: error,
                });
            }
        }

        let mut flags = SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED | SWP_NOACTIVATE;
        let was_topmost = current & WS_EX_TOPMOST != 0;
        let wants_topmost = ex_style & WS_EX_TOPMOST != 0;
        let insert_after = if wants_topmost {
            HWND_TOPMOST
        } else if was_topmost {
            HWND_NOTOPMOST
        } else {
            flags |= SWP_NOZORDER;
            null_mut()
        };

        // Safety: hwnd was validated and SetWindowPos only refreshes frame/z-order without moving or sizing.
        if unsafe { SetWindowPos(hwnd, insert_after, 0, 0, 0, 0, flags) } == 0 {
            return Err(last_error("SetWindowPos"));
        }

        Ok(())
    }

    pub fn capture_screen_region(
        request: ScreenCaptureRequest,
    ) -> Result<ScreenCaptureResult, WindowsPlatformError> {
        let (x, y, width, height) = screen_capture_rect(request)?;

        let width_usize = width as usize;
        let height_usize = height as usize;
        let buffer_len = width_usize
            .checked_mul(height_usize)
            .and_then(|pixels| pixels.checked_mul(4))
            .ok_or(WindowsPlatformError::NativeCallFailed {
                operation: "capture_screen_region_size",
                code: 0,
            })?;

        // Safety: GetDC(NULL) obtains the desktop screen device context for read-only capture.
        let screen_dc = unsafe { GetDC(null_mut()) };
        if screen_dc.is_null() {
            return Err(last_error("GetDC"));
        }
        let screen_dc = ScreenDc(screen_dc);

        // Safety: CreateCompatibleDC creates a memory DC compatible with the screen DC.
        let mem_dc = unsafe { CreateCompatibleDC(screen_dc.0) };
        if mem_dc.is_null() {
            return Err(last_error("CreateCompatibleDC"));
        }
        let mem_dc = CompatibleDc(mem_dc);

        // Safety: bitmap dimensions were validated as positive above.
        let bitmap = unsafe { CreateCompatibleBitmap(screen_dc.0, width, height) };
        if bitmap.is_null() {
            return Err(last_error("CreateCompatibleBitmap"));
        }
        let bitmap = BitmapHandle(bitmap);

        // Safety: bitmap and memory DC are valid. The selected object is restored by guard.
        let previous = unsafe { SelectObject(mem_dc.0, bitmap.0 as HGDIOBJ) };
        if previous.is_null() {
            return Err(last_error("SelectObject"));
        }
        let _selected = SelectedObject {
            dc: mem_dc.0,
            previous,
        };

        // Safety: all DCs are valid and the destination bitmap has the requested dimensions.
        let copied = unsafe { BitBlt(mem_dc.0, 0, 0, width, height, screen_dc.0, x, y, SRCCOPY) };
        if copied == 0 {
            return Err(last_error("BitBlt"));
        }

        let mut pixels = vec![0u8; buffer_len];
        // Safety: BITMAPINFO is immediately initialized below before use.
        let mut bitmap_info: BITMAPINFO = unsafe { std::mem::zeroed() };
        bitmap_info.bmiHeader = BITMAPINFOHEADER {
            biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB,
            biSizeImage: buffer_len as u32,
            biXPelsPerMeter: 0,
            biYPelsPerMeter: 0,
            biClrUsed: 0,
            biClrImportant: 0,
        };

        // Safety: pixels is large enough for a 32bpp top-down DIB of the validated dimensions.
        let rows = unsafe {
            GetDIBits(
                mem_dc.0,
                bitmap.0,
                0,
                height as u32,
                pixels.as_mut_ptr() as *mut _,
                &mut bitmap_info,
                DIB_RGB_COLORS,
            )
        };
        if rows == 0 {
            return Err(last_error("GetDIBits"));
        }

        let path = screen_capture_temp_path()?;
        let mut file =
            std::fs::File::create(&path).map_err(|_| WindowsPlatformError::NativeCallFailed {
                operation: "CreateCaptureFile",
                code: 0,
            })?;
        file.write_all(&pixels)
            .map_err(|_| WindowsPlatformError::NativeCallFailed {
                operation: "WriteCaptureFile",
                code: 0,
            })?;

        Ok(ScreenCaptureResult {
            pixel_data_path: path.to_string_lossy().into_owned(),
            pixel_width: width as u32,
            pixel_height: height as u32,
            screen_rect: ScreenRect::new(x, y, width as u32, height as u32),
        })
    }

    pub fn capture_screen_windows(
        request: ScreenWindowSnapshotRequest,
    ) -> Result<Vec<ScreenWindow>, WindowsPlatformError> {
        let mut windows = Vec::new();
        let mut context = WindowSnapshotContext {
            request: &request,
            windows: &mut windows,
        };

        // Safety: The callback only borrows `context` for the duration of EnumWindows.
        let ok = unsafe {
            EnumWindows(
                Some(enum_top_level_window_proc),
                &mut context as *mut WindowSnapshotContext<'_> as LPARAM,
            )
        };
        if ok == 0 {
            return Err(last_error("EnumWindows"));
        }

        Ok(windows)
    }

    struct WindowSnapshotContext<'a> {
        request: &'a ScreenWindowSnapshotRequest,
        windows: &'a mut Vec<ScreenWindow>,
    }

    struct ChildWindowSnapshotContext<'a> {
        request: &'a ScreenWindowSnapshotRequest,
        windows: &'a mut Vec<ScreenWindow>,
        parent_hwnd: HWND,
        parent_id: isize,
    }

    unsafe extern "system" fn enum_top_level_window_proc(hwnd: HWND, lparam: LPARAM) -> i32 {
        let context = unsafe { &mut *(lparam as *mut WindowSnapshotContext<'_>) };
        let Some(window) = screen_window_from_hwnd(hwnd, None, context.request, true) else {
            return 1;
        };

        context.windows.push(window);
        collect_direct_child_windows(hwnd, context.request, context.windows);
        1
    }

    unsafe extern "system" fn enum_child_window_proc(hwnd: HWND, lparam: LPARAM) -> i32 {
        let context = unsafe { &mut *(lparam as *mut ChildWindowSnapshotContext<'_>) };
        let parent = unsafe { GetParent(hwnd) };
        if parent != context.parent_hwnd {
            return 1;
        }

        let Some(window) =
            screen_window_from_hwnd(hwnd, Some(context.parent_id), context.request, false)
        else {
            return 1;
        };

        context.windows.push(window);
        collect_direct_child_windows(hwnd, context.request, context.windows);
        1
    }

    fn collect_direct_child_windows(
        parent_hwnd: HWND,
        request: &ScreenWindowSnapshotRequest,
        windows: &mut Vec<ScreenWindow>,
    ) {
        let mut context = ChildWindowSnapshotContext {
            request,
            windows,
            parent_hwnd,
            parent_id: parent_hwnd as isize,
        };

        // Safety: The callback only borrows `context` for the duration of EnumChildWindows.
        unsafe {
            EnumChildWindows(
                parent_hwnd,
                Some(enum_child_window_proc),
                &mut context as *mut ChildWindowSnapshotContext<'_> as LPARAM,
            );
        }
    }

    fn screen_window_from_hwnd(
        hwnd: HWND,
        parent_id: Option<isize>,
        request: &ScreenWindowSnapshotRequest,
        apply_top_level_filters: bool,
    ) -> Option<ScreenWindow> {
        if hwnd.is_null() {
            return None;
        }

        // Safety: IsWindowVisible reads the borrowed HWND state without taking ownership.
        if unsafe { IsWindowVisible(hwnd) } == 0 {
            return None;
        }

        let class_name = window_class_name(hwnd);
        if apply_top_level_filters && matches!(class_name.as_str(), "Progman" | "WorkerW") {
            return None;
        }

        if apply_top_level_filters {
            let title = window_title(hwnd);
            if request
                .excluded_titles
                .iter()
                .any(|excluded| title == *excluded)
            {
                return None;
            }
        }

        let mut rect = RECT {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
        // Safety: rect points to writable memory and hwnd is a borrowed window handle.
        if unsafe { GetWindowRect(hwnd, &mut rect) } == 0 {
            return None;
        }

        let width = rect.right - rect.left;
        let height = rect.bottom - rect.top;
        if width <= 0 || height <= 0 {
            return None;
        }

        Some(
            ScreenWindow::new(
                hwnd as isize,
                parent_id,
                ScreenRect::new(rect.left, rect.top, width as u32, height as u32),
            )
            .class_name(class_name),
        )
    }

    fn window_class_name(hwnd: HWND) -> String {
        let mut buffer = [0u16; 256];
        // Safety: buffer is valid for 256 UTF-16 code units and hwnd is borrowed.
        let len = unsafe { GetClassNameW(hwnd, buffer.as_mut_ptr(), buffer.len() as i32) };
        if len <= 0 {
            return String::new();
        }

        String::from_utf16_lossy(&buffer[..len as usize])
    }

    fn window_title(hwnd: HWND) -> String {
        // Safety: GetWindowTextLengthW reads the title length for a borrowed HWND.
        let len = unsafe { GetWindowTextLengthW(hwnd) };
        if len <= 0 {
            return String::new();
        }

        let mut buffer = vec![0u16; len as usize + 1];
        // Safety: buffer is writable and includes space for the trailing null terminator.
        let copied = unsafe { GetWindowTextW(hwnd, buffer.as_mut_ptr(), buffer.len() as i32) };
        if copied <= 0 {
            return String::new();
        }

        String::from_utf16_lossy(&buffer[..copied as usize])
    }

    fn screen_capture_rect(
        request: ScreenCaptureRequest,
    ) -> Result<(i32, i32, i32, i32), WindowsPlatformError> {
        match request.region {
            Some(region) => {
                if region.is_empty() {
                    return Err(invalid_screen_capture_request("empty region"));
                }

                let width = i32::try_from(region.width)
                    .map_err(|_| invalid_screen_capture_request("region width overflow"))?;
                let height = i32::try_from(region.height)
                    .map_err(|_| invalid_screen_capture_request("region height overflow"))?;
                Ok((region.x, region.y, width, height))
            }
            None => {
                let x = unsafe { GetSystemMetrics(SM_XVIRTUALSCREEN) };
                let y = unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) };
                let width = unsafe { GetSystemMetrics(SM_CXVIRTUALSCREEN) };
                let height = unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) };

                if width <= 0 || height <= 0 {
                    return Err(win32_error("GetSystemMetrics", 0));
                }

                Ok((x, y, width, height))
            }
        }
    }

    fn invalid_screen_capture_request(_reason: &'static str) -> WindowsPlatformError {
        WindowsPlatformError::NativeCallFailed {
            operation: "capture_screen_region_request",
            code: 0,
        }
    }

    pub fn has_text_insertion_target() -> Result<bool, WindowsPlatformError> {
        let target = captured_text_insertion_target()?;
        Ok(is_valid_window(target))
    }

    pub fn insert_text(text: &str) -> Result<(), WindowsPlatformError> {
        if text.is_empty() {
            return Ok(());
        }

        let target = captured_text_insertion_target()?;
        if !is_valid_window(target) {
            return Err(WindowsPlatformError::TextInsertionTargetUnavailable);
        }

        set_clipboard_text_for_probe(text)?;
        focus_window_and_send_paste(target)
    }

    pub fn open_url(url: &str) -> Result<(), WindowsPlatformError> {
        if url.trim().is_empty() {
            return Ok(());
        }

        let operation = wide_null("open");
        let file = wide_null(url);
        let result = unsafe {
            ShellExecuteW(
                null_mut(),
                operation.as_ptr(),
                file.as_ptr(),
                null(),
                null(),
                SW_SHOWNORMAL,
            )
        };

        if (result as isize) <= 32 {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "ShellExecuteW",
                code: result as u32,
            });
        }

        Ok(())
    }

    pub fn speak_text(text: &str, language: Option<&str>) -> Result<(), WindowsPlatformError> {
        if text.trim().is_empty() {
            return Ok(());
        }

        let script = r#"
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
try {
    $language = $env:WIN_FLUENT_TTS_LANG
    if (-not [string]::IsNullOrWhiteSpace($language) -and $language -ne 'auto') {
        try {
            $culture = [System.Globalization.CultureInfo]::GetCultureInfo($language)
            $synth.SelectVoiceByHints(
                [System.Speech.Synthesis.VoiceGender]::NotSet,
                [System.Speech.Synthesis.VoiceAge]::NotSet,
                0,
                $culture)
        } catch {
        }
    }

    $synth.Speak($env:WIN_FLUENT_TTS_TEXT)
} finally {
    $synth.Dispose()
}
"#;

        let mut command = Command::new("powershell.exe");
        command
            .arg("-NoProfile")
            .arg("-WindowStyle")
            .arg("Hidden")
            .arg("-ExecutionPolicy")
            .arg("Bypass")
            .arg("-Command")
            .arg(script)
            .env("WIN_FLUENT_TTS_TEXT", text);

        if let Some(language) = language {
            command.env("WIN_FLUENT_TTS_LANG", language);
        }

        command
            .spawn()
            .map(|_| ())
            .map_err(|_| WindowsPlatformError::NativeCallFailed {
                operation: "powershell.exe",
                code: 0,
            })
    }

    fn captured_text_insertion_target() -> Result<HWND, WindowsPlatformError> {
        let target = TEXT_INSERTION_TARGET
            .lock()
            .map_err(|_| WindowsPlatformError::TextInsertionTargetUnavailable)?;

        if *target == 0 {
            return Err(WindowsPlatformError::TextInsertionTargetUnavailable);
        }

        Ok(*target as HWND)
    }

    fn is_valid_window(hwnd: HWND) -> bool {
        // Safety: IsWindow only validates the borrowed HWND value; it does not take ownership.
        !hwnd.is_null() && unsafe { IsWindow(hwnd) } != 0
    }

    fn focus_window_and_send_paste(hwnd: HWND) -> Result<(), WindowsPlatformError> {
        let mut process_id = 0u32;
        // Safety: hwnd was validated by IsWindow before this call; process_id points to valid storage.
        let target_thread_id = unsafe { GetWindowThreadProcessId(hwnd, &mut process_id) };
        // Safety: GetCurrentThreadId has no preconditions.
        let current_thread_id = unsafe { GetCurrentThreadId() };
        let mut attached = false;

        if target_thread_id != 0 && target_thread_id != current_thread_id {
            // Safety: Both thread ids come from Win32 APIs; detach is attempted below when attach succeeds.
            attached = unsafe { AttachThreadInput(current_thread_id, target_thread_id, 1) } != 0;
        }

        let result = (|| {
            // Safety: hwnd remains a borrowed window handle owned by the OS.
            if unsafe { SetForegroundWindow(hwnd) } == 0 {
                return Err(last_error("SetForegroundWindow"));
            }

            std::thread::sleep(Duration::from_millis(100));

            // Safety: GetForegroundWindow reads the current foreground HWND without ownership transfer.
            if unsafe { GetForegroundWindow() } != hwnd {
                return Err(WindowsPlatformError::TextInsertionTargetUnavailable);
            }

            send_hotkey_input_for_probe(&WindowsHotkey {
                id: "text-insertion-paste".to_string(),
                native_id: 0,
                modifiers: MOD_CONTROL,
                virtual_key: b'V' as u32,
            })
        })();

        if attached {
            // Safety: This reverses the successful AttachThreadInput call above.
            let _ = unsafe { AttachThreadInput(current_thread_id, target_thread_id, 0) };
        }

        result
    }

    pub fn clipboard_text_snapshot_for_probe(
    ) -> Result<WindowsClipboardTextSnapshot, WindowsPlatformError> {
        // Safety: Passing null owner opens the process clipboard for inspection only.
        if unsafe { OpenClipboard(null_mut()) } == 0 {
            return Err(last_error("OpenClipboard"));
        }

        let _guard = ClipboardGuard;
        let mut formats = Vec::new();
        let mut current = 0u32;

        loop {
            // Safety: Clipboard is open. SetLastError lets us distinguish end-of-enum from errors.
            unsafe { SetLastError(0) };
            // Safety: Clipboard is open for enumeration.
            let format = unsafe { EnumClipboardFormats(current) };
            if format == 0 {
                let code = unsafe { GetLastError() };
                if code != 0 {
                    return Err(WindowsPlatformError::NativeCallFailed {
                        operation: "EnumClipboardFormats",
                        code,
                    });
                }
                break;
            }

            formats.push(read_clipboard_format(format)?);
            current = format;
        }

        let unicode_text = formats
            .iter()
            .find(|format| format.format == u32::from(CF_UNICODETEXT))
            .and_then(|format| decode_clipboard_unicode_text(&format.bytes));

        Ok(WindowsClipboardTextSnapshot {
            unicode_text,
            formats,
        })
    }

    pub fn restore_clipboard_text_for_probe(
        snapshot: &WindowsClipboardTextSnapshot,
    ) -> Result<(), WindowsPlatformError> {
        // Safety: Passing null owner opens the process clipboard for mutation.
        if unsafe { OpenClipboard(null_mut()) } == 0 {
            return Err(last_error("OpenClipboard"));
        }

        let _guard = ClipboardGuard;
        // Safety: Clipboard is open for the current process.
        if unsafe { EmptyClipboard() } == 0 {
            return Err(last_error("EmptyClipboard"));
        }

        for format in &snapshot.formats {
            let handle = global_alloc_from_bytes(&format.bytes)?;

            // Safety: Clipboard is open; handle contains a movable memory block and ownership
            // transfers to the system on success.
            if unsafe { SetClipboardData(format.format, handle) }.is_null() {
                // Safety: ownership has not been transferred to the clipboard.
                let _ = unsafe { GlobalFree(handle) };
                return Err(last_error("SetClipboardData"));
            }
        }

        Ok(())
    }

    fn modifier_virtual_keys(modifiers: u32) -> Vec<u16> {
        let mut keys = Vec::new();
        if modifiers & MOD_CONTROL != 0 {
            keys.push(VK_CONTROL);
        }
        if modifiers & MOD_ALT != 0 {
            keys.push(VK_MENU);
        }
        if modifiers & MOD_SHIFT != 0 {
            keys.push(VK_SHIFT);
        }
        if modifiers & MOD_WIN != 0 {
            keys.push(VK_LWIN);
        }
        keys
    }

    fn keyboard_input(virtual_key: u16, flags: u32) -> INPUT {
        INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT {
                    wVk: virtual_key,
                    wScan: 0,
                    dwFlags: flags,
                    time: 0,
                    dwExtraInfo: 0,
                },
            },
        }
    }

    fn unicode_keyboard_input(unit: u16, flags: u32) -> INPUT {
        INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT {
                    wVk: 0,
                    wScan: unit,
                    dwFlags: KEYEVENTF_UNICODE | flags,
                    time: 0,
                    dwExtraInfo: 0,
                },
            },
        }
    }

    fn read_clipboard_format(
        format: u32,
    ) -> Result<WindowsClipboardFormatSnapshot, WindowsPlatformError> {
        // Safety: Clipboard is open for the current process.
        let handle = unsafe { GetClipboardData(format) };
        if handle.is_null() {
            return Err(last_error("GetClipboardData"));
        }

        // Safety: handle is owned by the clipboard and valid while the clipboard remains open.
        let size = unsafe { GlobalSize(handle) };
        if size == 0 {
            return Ok(WindowsClipboardFormatSnapshot {
                format,
                bytes: Vec::new(),
            });
        }

        // Safety: handle is owned by the clipboard and valid while the clipboard remains open.
        let locked = unsafe { GlobalLock(handle) } as *const u8;
        if locked.is_null() {
            return Err(last_error("GlobalLock"));
        }

        // Safety: locked points to size initialized bytes while the clipboard remains open.
        let bytes = unsafe { std::slice::from_raw_parts(locked, size) }.to_vec();
        // Safety: Balances a successful GlobalLock on the clipboard handle.
        let _ = unsafe { GlobalUnlock(handle) };

        Ok(WindowsClipboardFormatSnapshot { format, bytes })
    }

    fn decode_clipboard_unicode_text(bytes: &[u8]) -> Option<String> {
        let units = bytes
            .chunks_exact(2)
            .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]))
            .take_while(|unit| *unit != 0)
            .collect::<Vec<_>>();

        (!units.is_empty()).then(|| String::from_utf16_lossy(&units))
    }

    pub fn current_process_memory() -> Result<WindowsProcessMemory, WindowsPlatformError> {
        let mut counters = PROCESS_MEMORY_COUNTERS_EX {
            cb: std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
            ..unsafe { std::mem::zeroed() }
        };

        // Safety: GetCurrentProcess returns a valid pseudo handle for the current process.
        // counters points to initialized storage large enough for PROCESS_MEMORY_COUNTERS_EX.
        let ok = unsafe {
            K32GetProcessMemoryInfo(
                GetCurrentProcess(),
                &mut counters as *mut PROCESS_MEMORY_COUNTERS_EX as *mut PROCESS_MEMORY_COUNTERS,
                std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
            )
        };
        if ok == 0 {
            return Err(last_error("K32GetProcessMemoryInfo"));
        }

        Ok(WindowsProcessMemory {
            private_bytes: counters.PrivateUsage,
            working_set_bytes: counters.WorkingSetSize,
        })
    }

    pub fn protect_data(
        plaintext: &[u8],
        optional_entropy: &[u8],
        scope: WindowsDataProtectionScope,
    ) -> Result<Vec<u8>, WindowsPlatformError> {
        execute_data_protection(plaintext, optional_entropy, scope, true)
    }

    pub fn unprotect_data(
        protected_bytes: &[u8],
        optional_entropy: &[u8],
        scope: WindowsDataProtectionScope,
    ) -> Result<Vec<u8>, WindowsPlatformError> {
        execute_data_protection(protected_bytes, optional_entropy, scope, false)
    }

    fn execute_data_protection(
        input: &[u8],
        optional_entropy: &[u8],
        scope: WindowsDataProtectionScope,
        protect: bool,
    ) -> Result<Vec<u8>, WindowsPlatformError> {
        let input_blob = blob_from_slice(input);
        let entropy_blob = blob_from_slice(optional_entropy);
        let mut output_blob = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: null_mut(),
        };
        let flags = data_protection_flags(scope, protect);

        let ok = if protect {
            // Safety: all blob pointers either point to valid immutable byte slices for the
            // duration of the call or are null for empty slices. output_blob is initialized for
            // CryptProtectData to fill and is freed with LocalFree below.
            unsafe {
                CryptProtectData(
                    &input_blob,
                    null(),
                    &entropy_blob,
                    null(),
                    null(),
                    flags,
                    &mut output_blob,
                )
            }
        } else {
            // Safety: same pointer guarantees as above. The optional description output is not
            // requested, and output_blob is freed with LocalFree below.
            unsafe {
                CryptUnprotectData(
                    &input_blob,
                    null_mut(),
                    &entropy_blob,
                    null(),
                    null(),
                    flags,
                    &mut output_blob,
                )
            }
        };

        if ok == 0 {
            return Err(last_error(if protect {
                "CryptProtectData"
            } else {
                "CryptUnprotectData"
            }));
        }

        let output = bytes_from_blob(&output_blob);
        // Safety: output_blob.pbData is allocated by CryptProtectData/CryptUnprotectData on
        // success and must be released with LocalFree.
        let _ = unsafe {
            windows_sys::Win32::Foundation::LocalFree(output_blob.pbData as *mut core::ffi::c_void)
        };
        Ok(output)
    }

    fn data_protection_flags(scope: WindowsDataProtectionScope, protect: bool) -> u32 {
        let mut flags = CRYPTPROTECT_UI_FORBIDDEN;
        if protect && scope == WindowsDataProtectionScope::LocalMachine {
            flags |= CRYPTPROTECT_LOCAL_MACHINE;
        }
        flags
    }

    fn blob_from_slice(bytes: &[u8]) -> CRYPT_INTEGER_BLOB {
        if bytes.is_empty() {
            return CRYPT_INTEGER_BLOB {
                cbData: 0,
                pbData: null_mut(),
            };
        }

        CRYPT_INTEGER_BLOB {
            cbData: bytes.len() as u32,
            pbData: bytes.as_ptr() as *mut u8,
        }
    }

    fn bytes_from_blob(blob: &CRYPT_INTEGER_BLOB) -> Vec<u8> {
        if blob.cbData == 0 || blob.pbData.is_null() {
            return Vec::new();
        }

        // Safety: blob was returned by DPAPI with cbData initialized bytes at pbData.
        unsafe { std::slice::from_raw_parts(blob.pbData, blob.cbData as usize) }.to_vec()
    }

    pub fn write_current_user_registry_value_string(
        key_path: &str,
        value_name: Option<&str>,
        value: &str,
    ) -> Result<(), WindowsPlatformError> {
        let key = create_current_user_key(key_path)?;
        let wide_value_name = value_name.map(wide_null);
        let value_name_ptr = wide_value_name
            .as_ref()
            .map_or(null(), |value| value.as_ptr());
        let wide_value = wide_null(value);
        let bytes = wide_value
            .iter()
            .flat_map(|unit| unit.to_le_bytes())
            .collect::<Vec<_>>();

        // Safety: key is valid, value_name_ptr is either null for the default value or points to a
        // null-terminated UTF-16 value name, and bytes contains a null-terminated UTF-16 REG_SZ.
        let result = unsafe {
            RegSetValueExW(
                key.0,
                value_name_ptr,
                0,
                REG_SZ,
                bytes.as_ptr(),
                bytes.len() as u32,
            )
        };
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegSetValueExW", result));
        }

        Ok(())
    }

    pub fn read_current_user_registry_value_string(
        key_path: &str,
        value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsPlatformError> {
        read_registry_value_string(HKEY_CURRENT_USER, key_path, value_name)
    }

    pub fn read_local_machine_registry_value_string(
        key_path: &str,
        value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsPlatformError> {
        read_registry_value_string(HKEY_LOCAL_MACHINE, key_path, value_name)
    }

    fn read_registry_value_string(
        root: HKEY,
        key_path: &str,
        value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsPlatformError> {
        let Some(key) = open_registry_key(root, key_path)? else {
            return Ok(None);
        };

        let wide_value_name = value_name.map(wide_null);
        let value_name_ptr = wide_value_name
            .as_ref()
            .map_or(null(), |value| value.as_ptr());
        let mut value_type = 0u32;
        let mut byte_count = 0u32;
        // Safety: Querying with null data obtains the byte count for the chosen value.
        let result = unsafe {
            RegQueryValueExW(
                key.0,
                value_name_ptr,
                null(),
                &mut value_type,
                null_mut(),
                &mut byte_count,
            )
        };
        if result == ERROR_FILE_NOT_FOUND {
            return Ok(None);
        }
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegQueryValueExW", result));
        }
        if value_type != REG_SZ || byte_count == 0 {
            return Ok(None);
        }

        let mut bytes = vec![0u8; byte_count as usize];
        // Safety: bytes has the size returned by the first RegQueryValueExW call.
        let result = unsafe {
            RegQueryValueExW(
                key.0,
                value_name_ptr,
                null(),
                &mut value_type,
                bytes.as_mut_ptr(),
                &mut byte_count,
            )
        };
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegQueryValueExW", result));
        }

        let mut units = bytes
            .chunks_exact(2)
            .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]))
            .collect::<Vec<_>>();
        while units.last() == Some(&0) {
            units.pop();
        }

        Ok(Some(String::from_utf16_lossy(&units)))
    }

    pub fn delete_current_user_registry_key(key_path: &str) -> Result<(), WindowsPlatformError> {
        let wide_path = wide_null(key_path);
        // Safety: HKEY_CURRENT_USER is predefined and key_path is null-terminated.
        let result = unsafe { RegDeleteKeyW(HKEY_CURRENT_USER, wide_path.as_ptr()) };
        if result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND {
            return Ok(());
        }

        Err(win32_error("RegDeleteKeyW", result))
    }

    pub fn delete_current_user_registry_tree(key_path: &str) -> Result<(), WindowsPlatformError> {
        let wide_path = wide_null(key_path);
        // Safety: HKEY_CURRENT_USER is predefined and key_path is null-terminated.
        let result = unsafe { RegDeleteTreeW(HKEY_CURRENT_USER, wide_path.as_ptr()) };
        if result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND {
            return Ok(());
        }

        Err(win32_error("RegDeleteTreeW", result))
    }

    fn global_alloc_from_bytes(
        bytes: &[u8],
    ) -> Result<*mut std::ffi::c_void, WindowsPlatformError> {
        let byte_len = bytes.len().max(1);

        // Safety: GlobalAlloc returns a movable memory handle owned by this function until a
        // successful SetClipboardData call transfers ownership to the system clipboard.
        let handle = unsafe { GlobalAlloc(GMEM_MOVEABLE, byte_len) };
        if handle.is_null() {
            return Err(last_error("GlobalAlloc"));
        }

        // Safety: handle is a valid movable memory handle from GlobalAlloc.
        let locked = unsafe { GlobalLock(handle) } as *mut u8;
        if locked.is_null() {
            // Safety: ownership has not been transferred to the clipboard.
            let _ = unsafe { GlobalFree(handle) };
            return Err(last_error("GlobalLock"));
        }

        if bytes.is_empty() {
            // Safety: locked points to at least one byte because byte_len is at least 1.
            unsafe { *locked = 0 };
        } else {
            // Safety: locked points to byte_len bytes and bytes.len() <= byte_len.
            unsafe {
                std::ptr::copy_nonoverlapping(bytes.as_ptr(), locked, bytes.len());
            }
        }

        // Safety: Balances a successful GlobalLock on the allocated handle.
        let _ = unsafe { GlobalUnlock(handle) };
        Ok(handle)
    }

    fn set_clipboard_text_for_probe(text: &str) -> Result<(), WindowsPlatformError> {
        let wide = text
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect::<Vec<_>>();
        let bytes = wide
            .iter()
            .flat_map(|unit| unit.to_le_bytes())
            .collect::<Vec<_>>();
        let handle = global_alloc_from_bytes(&bytes)?;

        // Safety: Passing null owner opens the process clipboard for mutation.
        if unsafe { OpenClipboard(null_mut()) } == 0 {
            // Safety: ownership has not been transferred to the clipboard.
            let _ = unsafe { GlobalFree(handle) };
            return Err(last_error("OpenClipboard"));
        }

        let _guard = ClipboardGuard;
        // Safety: Clipboard is open for the current process.
        if unsafe { EmptyClipboard() } == 0 {
            // Safety: ownership has not been transferred to the clipboard.
            let _ = unsafe { GlobalFree(handle) };
            return Err(last_error("EmptyClipboard"));
        }

        // Safety: handle contains a null-terminated UTF-16 buffer for CF_UNICODETEXT.
        if unsafe { SetClipboardData(u32::from(CF_UNICODETEXT), handle) }.is_null() {
            // Safety: ownership has not been transferred to the clipboard.
            let _ = unsafe { GlobalFree(handle) };
            return Err(last_error("SetClipboardData"));
        }

        Ok(())
    }

    #[cfg(test)]
    pub fn post_hotkey_message_for_current_thread_for_test(
        hotkey: &WindowsHotkey,
    ) -> Result<(), WindowsPlatformError> {
        let lparam = ((hotkey.virtual_key as isize) << 16) | hotkey.modifiers as isize;
        // Safety: GetCurrentThreadId has no preconditions; PostThreadMessageW posts to this
        // thread's queue, which is created before this call by poll_hotkey_message in tests.
        let ok = unsafe {
            PostThreadMessageW(
                GetCurrentThreadId(),
                WM_HOTKEY,
                hotkey.native_id as usize,
                lparam,
            )
        };

        if ok == 0 {
            return Err(last_error("PostThreadMessageW"));
        }

        Ok(())
    }

    pub fn is_clipboard_format_available(
        format: ClipboardFormat,
    ) -> Result<bool, WindowsPlatformError> {
        let Some(native_format) = clipboard_format(format) else {
            return Ok(false);
        };

        // Safety: Passing null owner opens the process clipboard for inspection only.
        if unsafe { OpenClipboard(null_mut()) } == 0 {
            return Err(last_error("OpenClipboard"));
        }

        let _guard = ClipboardGuard;
        // Safety: Clipboard is open for the current process and native_format is a Win32 format id.
        Ok(unsafe { IsClipboardFormatAvailable(native_format) } != 0)
    }

    pub fn cursor_position() -> Result<WindowsPoint, WindowsPlatformError> {
        let mut point = POINT::default();
        // Safety: GetCursorPos writes to a valid POINT pointer.
        if unsafe { GetCursorPos(&mut point) } == 0 {
            return Err(last_error("GetCursorPos"));
        }

        Ok(WindowsPoint {
            x: point.x,
            y: point.y,
        })
    }

    pub fn monitor_metrics_for_point(
        point: WindowsPoint,
    ) -> Result<WindowsMonitorMetrics, WindowsPlatformError> {
        let native_point = POINT {
            x: point.x,
            y: point.y,
        };
        // Safety: MonitorFromPoint reads the POINT by value and returns a monitor handle or null.
        let monitor = unsafe { MonitorFromPoint(native_point, MONITOR_DEFAULTTONEAREST) };
        if monitor.is_null() {
            return Err(last_error("MonitorFromPoint"));
        }

        let mut info = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..MONITORINFO::default()
        };
        // Safety: info points to a valid MONITORINFO with cbSize initialized as required.
        if unsafe { GetMonitorInfoW(monitor, &mut info) } == 0 {
            return Err(last_error("GetMonitorInfoW"));
        }

        let physical_work_area = WindowsRect {
            left: info.rcWork.left,
            top: info.rcWork.top,
            right: info.rcWork.right,
            bottom: info.rcWork.bottom,
        };
        let physical_monitor_area = WindowsRect {
            left: info.rcMonitor.left,
            top: info.rcMonitor.top,
            right: info.rcMonitor.right,
            bottom: info.rcMonitor.bottom,
        };

        let mut dpi_x = 96u32;
        let mut dpi_y = 96u32;
        // Safety: monitor is a valid HMONITOR and dpi pointers are valid for writes.
        let hr = unsafe { GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut dpi_x, &mut dpi_y) };
        let dpi = if hr >= 0 && dpi_x > 0 { dpi_x } else { 96 };

        Ok(WindowsMonitorMetrics::with_monitor_area(
            physical_work_area,
            physical_monitor_area,
            dpi,
        ))
    }

    struct ClipboardGuard;

    impl Drop for ClipboardGuard {
        fn drop(&mut self) {
            // Safety: Balanced with a successful OpenClipboard call in this module.
            let _ = unsafe { CloseClipboard() };
        }
    }

    fn last_error(operation: &'static str) -> WindowsPlatformError {
        // Safety: GetLastError has no preconditions and reads thread-local Win32 error state.
        WindowsPlatformError::NativeCallFailed {
            operation,
            code: unsafe { GetLastError() },
        }
    }

    fn screen_capture_temp_path() -> Result<std::path::PathBuf, WindowsPlatformError> {
        let mut directory = std::env::temp_dir();
        directory.push("WinFluent");
        directory.push("screen-capture");
        std::fs::create_dir_all(&directory).map_err(|_| {
            WindowsPlatformError::NativeCallFailed {
                operation: "CreateCaptureDirectory",
                code: 0,
            }
        })?;

        let timestamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or_default();
        directory.push(format!("capture-{}-{timestamp}.bgra", std::process::id()));
        Ok(directory)
    }

    fn win32_error(operation: &'static str, code: u32) -> WindowsPlatformError {
        WindowsPlatformError::NativeCallFailed { operation, code }
    }

    fn create_current_user_key(key_path: &str) -> Result<RegistryKey, WindowsPlatformError> {
        let wide_path = wide_null(key_path);
        let mut key = null_mut();
        // Safety: HKEY_CURRENT_USER is predefined, key_path is null-terminated, and key is writable.
        let result = unsafe {
            RegCreateKeyExW(
                HKEY_CURRENT_USER,
                wide_path.as_ptr(),
                0,
                null(),
                0,
                KEY_SET_VALUE,
                null(),
                &mut key,
                null_mut(),
            )
        };
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegCreateKeyExW", result));
        }

        Ok(RegistryKey(key))
    }

    fn open_registry_key(
        root: HKEY,
        key_path: &str,
    ) -> Result<Option<RegistryKey>, WindowsPlatformError> {
        let wide_path = wide_null(key_path);
        let mut key = null_mut();
        // Safety: root is a predefined registry hive, key_path is null-terminated, and key is writable.
        let result = unsafe { RegOpenKeyExW(root, wide_path.as_ptr(), 0, KEY_READ, &mut key) };
        if result == ERROR_FILE_NOT_FOUND {
            return Ok(None);
        }
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegOpenKeyExW", result));
        }

        Ok(Some(RegistryKey(key)))
    }

    fn wide_null(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }

    pub fn clipboard_format(format: ClipboardFormat) -> Option<u32> {
        match format {
            ClipboardFormat::Text => Some(u32::from(CF_UNICODETEXT)),
            ClipboardFormat::Image => Some(2),
            ClipboardFormat::Files => Some(15),
            ClipboardFormat::Custom(_) => None,
        }
    }

    pub const fn mod_control() -> u32 {
        MOD_CONTROL
    }

    pub const fn mod_alt() -> u32 {
        MOD_ALT
    }

    pub const fn mod_shift() -> u32 {
        MOD_SHIFT
    }

    pub const fn mod_win() -> u32 {
        MOD_WIN
    }

    pub const fn vk_f1() -> u32 {
        VK_F1 as u32
    }

    pub const fn vk_back() -> u32 {
        VK_BACK as u32
    }

    pub const fn vk_delete() -> u32 {
        VK_DELETE as u32
    }

    pub const fn vk_down() -> u32 {
        VK_DOWN as u32
    }

    pub const fn vk_end() -> u32 {
        VK_END as u32
    }

    pub const fn vk_escape() -> u32 {
        VK_ESCAPE as u32
    }

    pub const fn vk_home() -> u32 {
        VK_HOME as u32
    }

    pub const fn vk_left() -> u32 {
        VK_LEFT as u32
    }

    pub const fn vk_return() -> u32 {
        VK_RETURN as u32
    }

    pub const fn vk_right() -> u32 {
        VK_RIGHT as u32
    }

    pub const fn vk_space() -> u32 {
        VK_SPACE as u32
    }

    pub const fn vk_tab() -> u32 {
        VK_TAB as u32
    }

    pub const fn vk_up() -> u32 {
        VK_UP as u32
    }

    pub const fn wm_user() -> u32 {
        WM_USER
    }

    pub const fn ws_overlapped_window() -> u32 {
        WS_OVERLAPPEDWINDOW
    }

    pub const fn ws_popup() -> u32 {
        WS_POPUP
    }

    pub const fn ws_thickframe() -> u32 {
        WS_THICKFRAME
    }

    pub const fn ws_minimize_box() -> u32 {
        WS_MINIMIZEBOX
    }

    pub const fn ws_ex_toolwindow() -> u32 {
        WS_EX_TOOLWINDOW
    }

    pub const fn ws_ex_topmost() -> u32 {
        WS_EX_TOPMOST
    }

    pub const fn ws_ex_noactivate() -> u32 {
        WS_EX_NOACTIVATE
    }
}

#[cfg(not(windows))]
mod native {
    use super::{
        ClipboardFormat, NativeHotkeyMessage, WindowsClipboardTextSnapshot,
        WindowsDataProtectionScope, WindowsHotkey, WindowsHotkeyHandle, WindowsMonitorMetrics,
        WindowsNamedEvent, WindowsNamedEventHandle, WindowsPlatformError, WindowsPoint,
        WindowsProcessMemory, WindowsRect,
    };
    use win_fluent::platform::{
        ScreenCaptureRequest, ScreenCaptureResult, ScreenWindow, ScreenWindowSnapshotRequest,
    };

    #[derive(Debug)]
    pub struct NamedEventHandle;

    pub fn register_global_hotkey(
        native_hotkey: WindowsHotkey,
    ) -> Result<WindowsHotkeyHandle, WindowsPlatformError> {
        let _ = native_hotkey;
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn unregister_global_hotkey(_handle: &WindowsHotkeyHandle) {}

    pub fn poll_hotkey_message() -> Result<Option<NativeHotkeyMessage>, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn create_named_event(
        name: &str,
        _auto_reset: bool,
    ) -> Result<WindowsNamedEventHandle, WindowsPlatformError> {
        let _ = name;
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn signal_named_event(_name: &str) -> Result<bool, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn wait_for_named_event(
        _handle: &WindowsNamedEventHandle,
        _timeout: std::time::Duration,
    ) -> Result<Option<WindowsNamedEvent>, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn send_hotkey_input_for_probe(
        _hotkey: &WindowsHotkey,
    ) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn send_unicode_text_input_for_probe(_text: &str) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn send_clipboard_text_paste_for_probe(_text: &str) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn capture_text_insertion_target() -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn apply_window_ex_style(_hwnd: isize, _ex_style: u32) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn capture_screen_region(
        _request: ScreenCaptureRequest,
    ) -> Result<ScreenCaptureResult, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn capture_screen_windows(
        _request: ScreenWindowSnapshotRequest,
    ) -> Result<Vec<ScreenWindow>, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn has_text_insertion_target() -> Result<bool, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn insert_text(_text: &str) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn open_url(_url: &str) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn speak_text(_text: &str, _language: Option<&str>) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn clipboard_text_snapshot_for_probe(
    ) -> Result<WindowsClipboardTextSnapshot, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn restore_clipboard_text_for_probe(
        _snapshot: &WindowsClipboardTextSnapshot,
    ) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn current_process_memory() -> Result<WindowsProcessMemory, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn protect_data(
        _plaintext: &[u8],
        _optional_entropy: &[u8],
        _scope: WindowsDataProtectionScope,
    ) -> Result<Vec<u8>, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn unprotect_data(
        _protected_bytes: &[u8],
        _optional_entropy: &[u8],
        _scope: WindowsDataProtectionScope,
    ) -> Result<Vec<u8>, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn write_current_user_registry_value_string(
        _key_path: &str,
        _value_name: Option<&str>,
        _value: &str,
    ) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn read_current_user_registry_value_string(
        _key_path: &str,
        _value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn read_local_machine_registry_value_string(
        _key_path: &str,
        _value_name: Option<&str>,
    ) -> Result<Option<String>, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn delete_current_user_registry_key(_key_path: &str) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn delete_current_user_registry_tree(_key_path: &str) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn is_clipboard_format_available(
        _format: ClipboardFormat,
    ) -> Result<bool, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn cursor_position() -> Result<WindowsPoint, WindowsPlatformError> {
        Ok(WindowsPoint { x: 0, y: 0 })
    }

    pub fn monitor_metrics_for_point(
        _point: WindowsPoint,
    ) -> Result<WindowsMonitorMetrics, WindowsPlatformError> {
        Ok(WindowsMonitorMetrics::new(
            WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
            96,
        ))
    }

    pub fn clipboard_format(format: ClipboardFormat) -> Option<u32> {
        match format {
            ClipboardFormat::Text => Some(13),
            ClipboardFormat::Image => Some(2),
            ClipboardFormat::Files => Some(15),
            ClipboardFormat::Custom(_) => None,
        }
    }

    pub const fn mod_control() -> u32 {
        0x0002
    }

    pub const fn mod_alt() -> u32 {
        0x0001
    }

    pub const fn mod_shift() -> u32 {
        0x0004
    }

    pub const fn mod_win() -> u32 {
        0x0008
    }

    pub const fn vk_f1() -> u32 {
        112
    }

    pub const fn vk_back() -> u32 {
        8
    }

    pub const fn vk_delete() -> u32 {
        46
    }

    pub const fn vk_down() -> u32 {
        40
    }

    pub const fn vk_end() -> u32 {
        35
    }

    pub const fn vk_escape() -> u32 {
        27
    }

    pub const fn vk_home() -> u32 {
        36
    }

    pub const fn vk_left() -> u32 {
        37
    }

    pub const fn vk_return() -> u32 {
        13
    }

    pub const fn vk_right() -> u32 {
        39
    }

    pub const fn vk_space() -> u32 {
        32
    }

    pub const fn vk_tab() -> u32 {
        9
    }

    pub const fn vk_up() -> u32 {
        38
    }

    pub const fn wm_user() -> u32 {
        1024
    }

    pub const fn ws_overlapped_window() -> u32 {
        0x00cf0000
    }

    pub const fn ws_popup() -> u32 {
        0x80000000
    }

    pub const fn ws_thickframe() -> u32 {
        0x00040000
    }

    pub const fn ws_minimize_box() -> u32 {
        0x00020000
    }

    pub const fn ws_ex_toolwindow() -> u32 {
        0x00000080
    }

    pub const fn ws_ex_topmost() -> u32 {
        0x00000008
    }

    pub const fn ws_ex_noactivate() -> u32 {
        0x08000000
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use win_fluent::a11y::{resolve_accessibility_tree, A11yNode, A11yRole};
    use win_fluent::platform::{HotkeyKey, HotkeyModifier, TrayMenu, TrayMenuItem};
    use win_fluent::prelude::{button, column, page, text_editor, IntoView};
    use win_fluent::runtime::DesktopIntegrationPlan;
    use win_fluent::subscription::Subscription;
    use win_fluent::window::{WindowFrame, WindowLevel, WindowPlacement};

    #[allow(dead_code)]
    #[derive(Clone)]
    enum Msg {
        Open,
        Changed(String),
    }

    #[test]
    fn maps_hotkey_token_to_native_values() {
        let hotkey = Hotkey::new("mini", HotkeyKey::Character('m'))
            .modifier(HotkeyModifier::Control)
            .modifier(HotkeyModifier::Alt);

        let plan = WindowsPlatformAdapter::plan_hotkeys(&[hotkey]).expect("valid hotkey");

        assert_eq!(plan.len(), 1);
        assert_eq!(plan[0].id, "mini");
        assert_eq!(plan[0].virtual_key, b'M' as u32);
        assert_ne!(plan[0].native_id, 0);
    }

    #[test]
    fn maps_native_hotkey_message_back_to_token_id() {
        let hotkey = Hotkey::new("mini", HotkeyKey::Character('m'))
            .modifier(HotkeyModifier::Control)
            .modifier(HotkeyModifier::Alt);
        let handle = WindowsHotkeyHandle {
            native_hotkey: plan_hotkey(&hotkey).expect("valid hotkey"),
            #[cfg(windows)]
            hwnd: std::ptr::null_mut(),
        };
        let native = handle.hotkey().clone();

        let event = map_hotkey_message(
            &[&handle],
            NativeHotkeyMessage {
                native_id: native.native_id,
                modifiers: native.modifiers,
                virtual_key: native.virtual_key,
            },
        )
        .expect("mapped hotkey event");

        assert_eq!(event.id, "mini");
        assert_eq!(event.native_id, native.native_id);
        assert_eq!(event.modifiers, native.modifiers);
        assert_eq!(event.virtual_key, native.virtual_key);
    }

    #[cfg(windows)]
    #[test]
    fn waits_for_registered_hotkey_message() {
        let hotkey = Hotkey::new("probe", HotkeyKey::Function(24))
            .modifier(HotkeyModifier::Control)
            .modifier(HotkeyModifier::Alt)
            .modifier(HotkeyModifier::Shift);
        let Ok(handle) = WindowsPlatformAdapter::register_global_hotkey(&hotkey) else {
            return;
        };

        native::post_hotkey_message_for_current_thread_for_test(handle.hotkey())
            .expect("post WM_HOTKEY");

        let event =
            WindowsPlatformAdapter::wait_for_hotkey_event(&[&handle], Duration::from_millis(500))
                .expect("wait for hotkey event")
                .expect("hotkey event");

        assert_eq!(event.id, "probe");
        assert_eq!(event.native_id, handle.hotkey().native_id);
        assert_eq!(event.modifiers, handle.hotkey().modifiers);
        assert_eq!(event.virtual_key, handle.hotkey().virtual_key);
    }

    #[test]
    fn rejects_invalid_function_key() {
        let hotkey = Hotkey::new("bad", HotkeyKey::Function(25));

        let err = WindowsPlatformAdapter::plan_hotkeys(&[hotkey]).unwrap_err();

        assert_eq!(err, WindowsPlatformError::InvalidFunctionKey(25));
    }

    #[test]
    fn maps_mini_window_options_to_native_window_plan() {
        let options = WindowOptions::new("mini", "Mini")
            .size(420.0, 360.0)
            .level(WindowLevel::TopMost)
            .frame(WindowFrame::Acrylic)
            .placement(WindowPlacement::CursorOffset { x: 12.0, y: 12.0 })
            .skip_taskbar(true)
            .no_activate(true);

        let plan = WindowsPlatformAdapter::plan_window(&options);

        assert_eq!(plan.id, "mini");
        assert_eq!(plan.style, native::ws_popup());
        assert!(plan.ex_style & native::ws_ex_topmost() != 0);
        assert!(plan.ex_style & native::ws_ex_toolwindow() != 0);
        assert!(plan.ex_style & native::ws_ex_noactivate() != 0);
        assert!(plan.uses_acrylic);
    }

    #[test]
    fn maps_tool_window_options_to_topmost_no_activate_native_plan() {
        let options = WindowOptions::new("pop-button", "Selection")
            .size(30.0, 30.0)
            .min_size(30.0, 30.0)
            .level(WindowLevel::ToolWindow)
            .frame(WindowFrame::Borderless)
            .resize_mode(WindowResizeMode::Fixed)
            .skip_taskbar(true)
            .no_activate(true);

        let plan = WindowsPlatformAdapter::plan_window(&options);

        assert_eq!(plan.id, "pop-button");
        assert_eq!(plan.width, 30);
        assert_eq!(plan.height, 30);
        assert_eq!(plan.min_width, Some(30));
        assert_eq!(plan.min_height, Some(30));
        assert_eq!(plan.style, native::ws_popup());
        assert!(plan.ex_style & native::ws_ex_toolwindow() != 0);
        assert!(plan.ex_style & native::ws_ex_topmost() != 0);
        assert!(plan.ex_style & native::ws_ex_noactivate() != 0);
    }

    #[test]
    fn pop_button_show_at_clamps_to_work_area_near_edges() {
        let options = WindowOptions::new("pop-button", "Selection")
            .size(30.0, 30.0)
            .min_size(30.0, 30.0)
            .level(WindowLevel::ToolWindow)
            .frame(WindowFrame::Borderless)
            .resize_mode(WindowResizeMode::Fixed)
            .placement(WindowPlacement::Explicit {
                x: 1910.0,
                y: 1070.0,
            })
            .skip_taskbar(true)
            .no_activate(true);

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 1910, y: 1070 },
            WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
        );
        let plan = WindowsPlatformAdapter::plan_window(&options);

        assert_eq!(placement.x, 1890);
        assert_eq!(placement.y, 1050);
        assert_eq!(placement.width, 30);
        assert_eq!(placement.height, 30);
        assert!(placement.x >= placement.work_area.left);
        assert!(placement.y >= placement.work_area.top);
        assert!(placement.x + placement.width <= placement.work_area.right);
        assert!(placement.y + placement.height <= placement.work_area.bottom);
        assert!(plan.ex_style & native::ws_ex_toolwindow() != 0);
        assert!(plan.ex_style & native::ws_ex_topmost() != 0);
        assert!(plan.ex_style & native::ws_ex_noactivate() != 0);
    }

    #[test]
    fn resolves_cursor_offset_inside_monitor_work_area() {
        let options = WindowOptions::new("mini", "Mini")
            .size(420.0, 360.0)
            .placement(WindowPlacement::CursorOffset { x: 12.0, y: 12.0 });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 1900, y: 1000 },
            WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
        );

        assert_eq!(placement.x, 1500);
        assert_eq!(placement.y, 720);
        assert_eq!(placement.width, 420);
        assert_eq!(placement.height, 360);
    }

    #[test]
    fn resolves_top_right_inside_monitor_work_area() {
        let options = WindowOptions::new("mini", "Mini")
            .size(420.0, 360.0)
            .placement(WindowPlacement::TopRight {
                margin_x: 16.0,
                margin_y: 24.0,
            });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 400, y: 300 },
            WindowsRect {
                left: -1280,
                top: 0,
                right: 0,
                bottom: 720,
            },
        );

        assert_eq!(placement.x, -436);
        assert_eq!(placement.y, 24);
        assert_eq!(placement.width, 420);
        assert_eq!(placement.height, 360);
    }

    #[test]
    fn clamps_explicit_window_placement_by_default() {
        let options = WindowOptions::new("mini", "Mini")
            .size(420.0, 360.0)
            .placement(WindowPlacement::Explicit {
                x: 2200.0,
                y: -500.0,
            });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 0, y: 0 },
            WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
        );

        assert_eq!(placement.x, 1500);
        assert_eq!(placement.y, 0);
    }

    #[test]
    fn allows_opt_in_offscreen_window_placement() {
        let options = WindowOptions::new("mini", "Mini")
            .size(420.0, 360.0)
            .placement(WindowPlacement::Explicit {
                x: 2200.0,
                y: -500.0,
            })
            .allow_offscreen();

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 0, y: 0 },
            WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
        );

        assert_eq!(placement.x, 2200);
        assert_eq!(placement.y, -500);
        assert_eq!(placement.width, 420);
        assert_eq!(placement.height, 360);
    }

    #[test]
    fn constrains_window_size_to_monitor_work_area() {
        let options = WindowOptions::new("main", "Main")
            .size(2400.0, 1400.0)
            .min_size(640.0, 480.0)
            .placement(WindowPlacement::Center);

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 0, y: 0 },
            WindowsRect {
                left: 0,
                top: 0,
                right: 1366,
                bottom: 768,
            },
        );

        assert_eq!(placement.x, 0);
        assert_eq!(placement.y, 0);
        assert_eq!(placement.width, 1366);
        assert_eq!(placement.height, 768);
    }

    #[test]
    fn constrains_window_size_to_monitor_work_area_dips_on_high_dpi() {
        let options = WindowOptions::new("main", "Main")
            .size(940.0, 1220.0)
            .placement(WindowPlacement::Explicit { x: 40.0, y: 20.0 });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for_monitor(
            &options,
            WindowsPoint { x: 300, y: 200 },
            WindowsMonitorMetrics::new(
                WindowsRect {
                    left: 0,
                    top: 0,
                    right: 1440,
                    bottom: 900,
                },
                144,
            ),
        );

        assert_eq!(placement.dpi, 144);
        assert_eq!(placement.physical_work_area.width(), 1440);
        assert_eq!(placement.work_area.width(), 960);
        assert_eq!(placement.x, 20);
        assert_eq!(placement.y, 0);
        assert_eq!(placement.width, 940);
        assert_eq!(placement.height, 600);
    }

    #[test]
    fn resolves_work_area_window_placement_to_monitor_bounds_in_dips() {
        let options = WindowOptions::new("capture-overlay", "Capture")
            .size(1920.0, 1080.0)
            .min_size(1.0, 1.0)
            .placement(WindowPlacement::WorkArea);

        let placement = WindowsPlatformAdapter::resolve_window_placement_for_monitor(
            &options,
            WindowsPoint { x: 300, y: 200 },
            WindowsMonitorMetrics::new(
                WindowsRect {
                    left: -1920,
                    top: 0,
                    right: 0,
                    bottom: 1200,
                },
                120,
            ),
        );

        assert_eq!(placement.dpi, 120);
        assert_eq!(placement.work_area.left, -1536);
        assert_eq!(placement.work_area.top, 0);
        assert_eq!(placement.x, -1536);
        assert_eq!(placement.y, 0);
        assert_eq!(placement.width, 1536);
        assert_eq!(placement.height, 960);
    }

    #[test]
    fn resolves_monitor_window_placement_to_full_monitor_bounds_in_dips() {
        let options = WindowOptions::new("capture-overlay", "Capture")
            .size(1920.0, 1080.0)
            .min_size(1.0, 1.0)
            .placement(WindowPlacement::Monitor);

        let placement = WindowsPlatformAdapter::resolve_window_placement_for_monitor(
            &options,
            WindowsPoint { x: 300, y: 200 },
            WindowsMonitorMetrics::with_monitor_area(
                WindowsRect {
                    left: 0,
                    top: 0,
                    right: 2880,
                    bottom: 1704,
                },
                WindowsRect {
                    left: 0,
                    top: 0,
                    right: 2880,
                    bottom: 1800,
                },
                192,
            ),
        );

        assert_eq!(placement.dpi, 192);
        assert_eq!(placement.work_area.height(), 852);
        assert_eq!(placement.x, 0);
        assert_eq!(placement.y, 0);
        assert_eq!(placement.width, 1440);
        assert_eq!(placement.height, 900);
    }

    #[test]
    fn borderless_monitor_placement_oversizes_height_to_avoid_fullscreen_present_bug() {
        let options = WindowOptions::new("capture-overlay", "Capture")
            .size(1920.0, 1080.0)
            .min_size(1.0, 1.0)
            .frame(WindowFrame::Borderless)
            .placement(WindowPlacement::Monitor);

        let placement = WindowsPlatformAdapter::resolve_window_placement_for_monitor(
            &options,
            WindowsPoint { x: 300, y: 200 },
            WindowsMonitorMetrics::with_monitor_area(
                WindowsRect {
                    left: 0,
                    top: 0,
                    right: 2880,
                    bottom: 1704,
                },
                WindowsRect {
                    left: 0,
                    top: 0,
                    right: 2880,
                    bottom: 1800,
                },
                192,
            ),
        );

        // One DIP taller than the monitor so DWM does not treat the borderless
        // overlay as an exclusive fullscreen surface.
        assert_eq!(placement.width, 1440);
        assert_eq!(placement.height, 901);
        assert_eq!(placement.x, 0);
        assert_eq!(placement.y, 0);
    }

    #[test]
    fn selects_work_area_for_multi_monitor_cursor() {
        let options = WindowOptions::new("mini", "Mini")
            .size(420.0, 360.0)
            .placement(WindowPlacement::CursorOffset { x: 12.0, y: 12.0 });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for_work_areas(
            &options,
            WindowsPoint { x: -50, y: 700 },
            &[
                WindowsRect {
                    left: 0,
                    top: 0,
                    right: 1920,
                    bottom: 1080,
                },
                WindowsRect {
                    left: -1280,
                    top: 0,
                    right: 0,
                    bottom: 720,
                },
            ],
        )
        .expect("work area");

        assert_eq!(placement.work_area.left, -1280);
        assert_eq!(placement.work_area.right, 0);
        assert_eq!(placement.x, -420);
        assert_eq!(placement.y, 360);
    }

    #[test]
    fn resolves_cursor_offset_from_physical_cursor_into_dips() {
        let options = WindowOptions::new("mini", "Mini")
            .size(420.0, 360.0)
            .placement(WindowPlacement::CursorOffset { x: 12.0, y: 12.0 });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for_monitor(
            &options,
            WindowsPoint { x: 1350, y: 780 },
            WindowsMonitorMetrics::new(
                WindowsRect {
                    left: 0,
                    top: 0,
                    right: 1440,
                    bottom: 900,
                },
                144,
            ),
        );

        assert_eq!(placement.work_area.width(), 960);
        assert_eq!(placement.work_area.height(), 600);
        assert_eq!(placement.x, 540);
        assert_eq!(placement.y, 240);
    }

    #[cfg(windows)]
    #[test]
    fn resolves_current_cursor_monitor_work_area() {
        let options = WindowOptions::new("mini", "Mini")
            .size(420.0, 360.0)
            .placement(WindowPlacement::CursorOffset { x: 12.0, y: 12.0 });

        let placement =
            WindowsPlatformAdapter::resolve_window_placement(&options).expect("runtime placement");

        assert_eq!(placement.width, 420);
        assert_eq!(placement.height, 360);
        assert!(placement.x >= placement.work_area.left);
        assert!(placement.y >= placement.work_area.top);
        assert!(placement.x + placement.width <= placement.work_area.right);
        assert!(placement.y + placement.height <= placement.work_area.bottom);
    }

    #[test]
    fn maps_tray_and_clipboard_tokens_to_native_plan() {
        let tray = TrayMenu::new("win fluent").item(TrayMenuItem::new("open", "Open"));
        let tray_plan = WindowsPlatformAdapter::plan_tray::<Msg>(&tray).expect("tray plan");

        assert_eq!(tray_plan.callback_message, native::wm_user() + 1);
        assert_eq!(tray_plan.item_count, 1);
        assert_eq!(
            WindowsPlatformAdapter::native_clipboard_format(ClipboardFormat::Text),
            Some(13)
        );
    }

    #[test]
    fn maps_hotkey_subscription_to_native_registration() {
        let hotkey = Hotkey::new("translate", HotkeyKey::Character('d'))
            .modifier(HotkeyModifier::Control)
            .modifier(HotkeyModifier::Alt);
        let subscription = Subscription::hotkey(hotkey, Msg::Changed);

        let registrations =
            WindowsPlatformAdapter::plan_subscription(&subscription).expect("subscription plan");

        assert_eq!(registrations.len(), 1);
        match &registrations[0] {
            WindowsRegistration::Hotkey(plan) => {
                assert_eq!(plan.id, "translate");
                assert_eq!(plan.modifiers, native::mod_control() | native::mod_alt());
                assert_eq!(plan.virtual_key, b'D' as u32);
            }
            other => panic!("expected hotkey registration, got {other:?}"),
        }
    }

    #[test]
    fn maps_named_event_subscription_to_native_registration() {
        let subscription = Subscription::named_event(r"Local\Demo-Action", true, Msg::Changed);

        let registrations =
            WindowsPlatformAdapter::plan_subscription(&subscription).expect("subscription plan");

        assert_eq!(registrations.len(), 1);
        match &registrations[0] {
            WindowsRegistration::NamedEvent(plan) => {
                assert_eq!(plan.name, r"Local\Demo-Action");
                assert!(plan.auto_reset);
                assert_eq!(plan.action_kind, ActionKind::Message);
            }
            other => panic!("expected named event registration, got {other:?}"),
        }
    }

    #[cfg(windows)]
    #[test]
    fn signals_and_waits_for_named_event() {
        let name = format!(r"Local\WinFluent-NamedEventTest-{}", std::process::id());
        assert!(
            !WindowsPlatformAdapter::signal_named_event(&format!("{name}-missing"))
                .expect("missing event signal")
        );

        let handle =
            WindowsPlatformAdapter::create_named_event(&name, true).expect("named event handle");

        assert!(
            !WindowsPlatformAdapter::wait_for_named_event(&handle, Duration::from_millis(0))
                .expect("initial wait")
                .is_some()
        );

        assert!(WindowsPlatformAdapter::signal_named_event(&name).expect("signal event"));

        let event = WindowsPlatformAdapter::wait_for_named_event(&handle, Duration::from_secs(1))
            .expect("wait event")
            .expect("event should be signaled");
        assert_eq!(event.name, name);

        assert!(
            WindowsPlatformAdapter::wait_for_named_event(&handle, Duration::from_millis(0))
                .expect("auto reset wait")
                .is_none()
        );
    }

    #[cfg(windows)]
    #[test]
    fn roundtrips_current_user_registry_string() {
        let key_path = format!(
            r"Software\WinFluent\Tests\RegistryString-{}",
            std::process::id()
        );

        WindowsPlatformAdapter::delete_current_user_registry_key(&key_path)
            .expect("cleanup before test");
        assert_eq!(
            WindowsPlatformAdapter::read_current_user_registry_string(&key_path)
                .expect("missing value"),
            None
        );

        WindowsPlatformAdapter::write_current_user_registry_string(
            &key_path,
            r"C:\Demo\manifest.json",
        )
        .expect("write registry");
        assert_eq!(
            WindowsPlatformAdapter::read_current_user_registry_string(&key_path)
                .expect("read value"),
            Some(r"C:\Demo\manifest.json".to_string())
        );

        WindowsPlatformAdapter::delete_current_user_registry_key(&key_path).expect("delete key");
        assert_eq!(
            WindowsPlatformAdapter::read_current_user_registry_string(&key_path)
                .expect("missing after delete"),
            None
        );
    }

    #[cfg(windows)]
    #[test]
    fn registers_and_unregisters_shell_verb_registry_entries() {
        let id = unique_registry_test_id("ShellVerb");
        let plan = WindowsPlatformAdapter::plan_shell_verbs(&[ShellVerb::new(&id, "Inspect")
            .directory_background(true)
            .argument("--inspect")])
        .remove(0);
        let executable_path = r"C:\Program Files\Demo\demo.exe";

        for key_path in &plan.registry_key_paths {
            WindowsPlatformAdapter::delete_current_user_registry_tree(key_path)
                .expect("cleanup before test");
        }

        WindowsPlatformAdapter::register_shell_verb(&plan, executable_path)
            .expect("register shell verb");

        for (registry_key_path, command_key_path) in plan
            .registry_key_paths
            .iter()
            .zip(plan.command_key_paths.iter())
        {
            assert_eq!(
                WindowsPlatformAdapter::read_current_user_registry_string(registry_key_path)
                    .expect("read shell label"),
                Some("Inspect".to_string())
            );
            assert_eq!(
                WindowsPlatformAdapter::read_current_user_registry_value_string(
                    registry_key_path,
                    Some("Icon")
                )
                .expect("read shell icon"),
                Some(executable_path.to_string())
            );
            assert_eq!(
                WindowsPlatformAdapter::read_current_user_registry_string(command_key_path)
                    .expect("read shell command"),
                Some(format!(r#""{executable_path}" --inspect"#))
            );
        }

        WindowsPlatformAdapter::unregister_shell_verb(&plan).expect("unregister shell verb");
        for registry_key_path in &plan.registry_key_paths {
            assert_eq!(
                WindowsPlatformAdapter::read_current_user_registry_string(registry_key_path)
                    .expect("shell key removed"),
                None
            );
        }
    }

    #[cfg(windows)]
    #[test]
    fn registers_and_unregisters_protocol_registry_entries() {
        let scheme = unique_registry_test_id("protocol").to_ascii_lowercase();
        let plan =
            WindowsPlatformAdapter::plan_protocol_registrations(&[ProtocolRegistration::new(
                &scheme,
                "URL:Demo Protocol",
            )
            .argument("%1")])
            .remove(0);
        let executable_path = r"C:\Program Files\Demo\demo.exe";

        WindowsPlatformAdapter::delete_current_user_registry_tree(&plan.registry_key_path)
            .expect("cleanup before test");

        WindowsPlatformAdapter::register_protocol_registration(&plan, executable_path)
            .expect("register protocol");

        assert_eq!(
            WindowsPlatformAdapter::read_current_user_registry_string(&plan.registry_key_path)
                .expect("read protocol description"),
            Some("URL:Demo Protocol".to_string())
        );
        assert_eq!(
            WindowsPlatformAdapter::read_current_user_registry_value_string(
                &plan.registry_key_path,
                Some("URL Protocol")
            )
            .expect("read protocol marker"),
            Some(String::new())
        );
        assert_eq!(
            WindowsPlatformAdapter::read_current_user_registry_string(&plan.command_key_path)
                .expect("read protocol command"),
            Some(format!(r#""{executable_path}" "%1""#))
        );

        WindowsPlatformAdapter::unregister_protocol_registration(&plan)
            .expect("unregister protocol");
        assert_eq!(
            WindowsPlatformAdapter::read_current_user_registry_string(&plan.registry_key_path)
                .expect("protocol key removed"),
            None
        );
    }

    #[test]
    fn maps_shell_verbs_without_touching_registry() {
        let verbs = WindowsPlatformAdapter::plan_shell_verbs(&[ShellVerb::new("open", "Open")
            .directory_background(true)
            .argument("--open")]);

        assert_eq!(verbs[0].id, "open");
        assert!(verbs[0].accepts_files);
        assert!(verbs[0].accepts_directory_background);
        assert_eq!(
            verbs[0].registry_key_paths,
            vec![
                r"Software\Classes\*\shell\open",
                r"Software\Classes\Directory\Background\shell\open"
            ]
        );
        assert_eq!(
            verbs[0].command_key_paths,
            vec![
                r"Software\Classes\*\shell\open\command",
                r"Software\Classes\Directory\Background\shell\open\command"
            ]
        );
        assert_eq!(verbs[0].command_arguments, vec!["--open"]);
        assert_eq!(
            verbs[0].command_line(r"C:\Program Files\Demo\demo.exe"),
            r#""C:\Program Files\Demo\demo.exe" --open"#
        );
    }

    #[test]
    fn maps_protocol_registration_without_touching_registry() {
        let protocols =
            WindowsPlatformAdapter::plan_protocol_registrations(&[ProtocolRegistration::new(
                "demo",
                "URL:Demo Protocol",
            )
            .argument("%1")]);

        assert_eq!(protocols[0].scheme, "demo");
        assert_eq!(protocols[0].description, "URL:Demo Protocol");
        assert!(protocols[0].url_protocol_marker);
        assert_eq!(protocols[0].registry_key_path, r"Software\Classes\demo");
        assert_eq!(
            protocols[0].command_key_path,
            r"Software\Classes\demo\shell\open\command"
        );
        assert_eq!(protocols[0].command_arguments, vec!["%1"]);
        assert_eq!(
            protocols[0].command_line(r"C:\Program Files\Demo\demo.exe"),
            r#""C:\Program Files\Demo\demo.exe" "%1""#
        );
    }

    #[test]
    fn maps_named_event_plan_without_touching_os_handles() {
        let events = WindowsPlatformAdapter::plan_named_events(&[NamedEventRegistration::new(
            r"Local\Demo-Action",
        )
        .on_signal(Msg::Changed)]);

        assert_eq!(events.len(), 1);
        assert_eq!(events[0].name, r"Local\Demo-Action");
        assert!(events[0].auto_reset);
        assert_eq!(events[0].action_kind, ActionKind::Message);
    }

    #[test]
    fn maps_desktop_integration_plan_without_touching_registry() {
        let desktop = DesktopIntegrationPlan {
            tray_menu: Some(TrayMenu::<Msg>::new("demo").item(TrayMenuItem::new("open", "Open"))),
            named_events: vec![NamedEventRegistration::new(r"Local\Demo-Action")
                .on_signal(Msg::Changed("event".to_string()))],
            shell_verbs: vec![ShellVerb::new("inspect", "Inspect")
                .directory_background(true)
                .argument("--inspect")],
            protocol_registrations: vec![
                ProtocolRegistration::new("demo", "URL:Demo Protocol").argument("%1")
            ],
        };

        let plan = WindowsPlatformAdapter::plan_desktop_integration(&desktop);

        assert!(plan.has_entries());
        assert_eq!(plan.entry_count(), 4);
        assert_eq!(plan.tray.expect("tray").item_count, 1);
        assert_eq!(plan.named_events[0].name, r"Local\Demo-Action");
        assert_eq!(plan.named_events[0].action_kind, ActionKind::Message);
        assert_eq!(plan.shell_verbs[0].command_arguments, vec!["--inspect"]);
        assert_eq!(
            plan.shell_verbs[0].registry_key_paths,
            vec![
                r"Software\Classes\*\shell\inspect",
                r"Software\Classes\Directory\Background\shell\inspect"
            ]
        );
        assert_eq!(plan.protocol_registrations[0].scheme, "demo");
        assert_eq!(
            plan.protocol_registrations[0].command_key_path,
            r"Software\Classes\demo\shell\open\command"
        );
    }

    #[test]
    fn maps_accessibility_tree_to_uia_control_types() {
        let mut root = A11yNode::new(A11yRole::Application);
        root.name = Some("Win Fluent".to_string());

        let mut group = A11yNode::new(A11yRole::Group);
        let mut button = A11yNode::new(A11yRole::Button);
        button.name = Some("Translate".to_string());
        button.help_text = Some("Runs the selected service".to_string());
        button.focusable = true;
        group.children.push(button);
        root.children.push(group);

        let plan = WindowsPlatformAdapter::plan_uia_tree(&root);

        assert_eq!(plan.root.control_type, WindowsUiaControlType::Window);
        assert_eq!(plan.root.name.as_deref(), Some("Win Fluent"));
        assert_eq!(
            plan.root.children[0].control_type,
            WindowsUiaControlType::Group
        );
        assert_eq!(
            plan.root.children[0].children[0].control_type,
            WindowsUiaControlType::Button
        );
        assert_eq!(
            plan.root.children[0].children[0].help_text.as_deref(),
            Some("Runs the selected service")
        );
        assert!(plan.root.children[0].children[0].focusable);
    }

    #[test]
    fn maps_view_accessibility_tree_to_uia_plan() {
        let view = page("Main")
            .content(column((
                button("Open").on_press(Msg::Open),
                text_editor("").placeholder("Query").on_input(Msg::Changed),
            )))
            .into_view();
        let tree = resolve_accessibility_tree(&view);

        let plan = WindowsPlatformAdapter::plan_uia_tree(&tree);

        assert_eq!(plan.root.control_type, WindowsUiaControlType::Window);
        assert_eq!(plan.root.name.as_deref(), Some("Main"));
        assert_eq!(
            plan.root.children[0].children[0].control_type,
            WindowsUiaControlType::Button
        );
        assert_eq!(
            plan.root.children[0].children[1].control_type,
            WindowsUiaControlType::Edit
        );
        assert_eq!(
            plan.root.children[0].children[1].name.as_deref(),
            Some("Query")
        );
    }

    #[cfg(windows)]
    fn unique_registry_test_id(prefix: &str) -> String {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("system time")
            .as_nanos();
        format!("{prefix}-{}-{nanos}", std::process::id())
    }
}

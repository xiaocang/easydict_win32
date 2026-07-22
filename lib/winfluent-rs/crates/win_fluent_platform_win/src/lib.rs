use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::time::{Duration, Instant};

use win_fluent::a11y::{A11yNode, A11yRole};
use win_fluent::action::ActionKind;
use win_fluent::platform::{
    ClipboardFormat, Hotkey, HotkeyKey, HotkeyModifier, NamedEventRegistration,
    ProtocolRegistration, ScreenCaptureRequest, ScreenCaptureResult, ScreenWindow,
    ScreenWindowSnapshotRequest, ShellVerb, TrayMenu, TrayMenuItem, TrayMenuPresenterKind,
    TrayMenuPresenterStyle,
};
use win_fluent::runtime::DesktopIntegrationPlan;
use win_fluent::subscription::{Subscription, SubscriptionKind};
use win_fluent::theme::ThemeMode;
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

/// Per-process GPU (video) memory usage for the local memory segment, as tracked
/// by the OS video memory manager (DXGI `QueryVideoMemoryInfo`). This is the
/// "rendering" share of the in-process iced/wgpu renderer — the counterpart to
/// the CPU-side [`WindowsProcessMemory`].
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct WindowsGpuMemory {
    /// Current video memory used by this process, in bytes.
    pub current_usage_bytes: u64,
    /// OS-provided video memory budget for this process, in bytes.
    pub budget_bytes: u64,
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
    pub icon_path: Option<String>,
    pub presenter_kind: TrayMenuPresenterKind,
    pub presenter_min_width: Option<u16>,
    pub presenter_style: TrayMenuPresenterStyle,
    pub callback_message: u32,
    pub item_count: usize,
    pub default_command_id: Option<u32>,
    pub items: Vec<WindowsTrayItemPlan>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsTrayItemPlan {
    pub id: String,
    pub label: String,
    pub tooltip: Option<String>,
    pub enabled: bool,
    pub command_id: u32,
    pub action_kind: ActionKind,
    pub kind: WindowsTrayItemKind,
    pub children: Vec<WindowsTrayItemPlan>,
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum WindowsTrayItemKind {
    Command,
    Separator,
    Submenu,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum WindowsTrayEvent {
    Command { id: String, command_id: u32 },
    OpenMenu { x: i32, y: i32 },
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
    Hyperlink,
    Image,
    List,
    ListItem,
    MenuItem,
    Pane,
    ProgressBar,
    RadioButton,
    Slider,
    Tab,
    TabItem,
    Text,
    ToolTip,
    Tree,
    TreeItem,
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

    /// Returns the Windows application color preference. Missing or malformed
    /// personalization data follows the Windows default and resolves to light.
    pub fn system_theme_mode() -> Result<ThemeMode, WindowsPlatformError> {
        native::system_uses_dark_theme().map(|uses_dark_theme| {
            if uses_dark_theme {
                ThemeMode::Dark
            } else {
                ThemeMode::Light
            }
        })
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
        native::apply_window_style(hwnd, window_style(options))?;
        native::configure_resize_hit_test(
            hwnd,
            options.resize_mode == WindowResizeMode::CanResize
                && matches!(
                    options.frame,
                    WindowFrame::Borderless | WindowFrame::Acrylic
                ),
        )?;
        native::apply_window_ex_style(hwnd, window_ex_style(options))?;
        native::apply_window_corner_preference(
            hwnd,
            options.level == WindowLevel::ToolWindow
                && options.resize_mode == WindowResizeMode::Fixed,
        );
        native::apply_window_native_border(hwnd, options.native_border)?;
        Ok(())
    }
    pub fn show_window(hwnd: isize, activate: bool) -> Result<(), WindowsPlatformError> {
        native::show_window(hwnd, activate)
    }

    /// Pre-fills the window's client surface with a solid color so the first
    /// `show_window` composites the theme background instead of the default
    /// white surface while the renderer's first frame is still pending.
    pub fn paint_window_background(
        hwnd: isize,
        red: u8,
        green: u8,
        blue: u8,
    ) -> Result<(), WindowsPlatformError> {
        native::paint_window_background(hwnd, red, green, blue)
    }

    /// Pins the native non-client frame to the application's effective
    /// light/dark palette. This prevents Windows from repainting a transient
    /// system-colored caption while owned popup windows close.
    pub fn set_window_dark_mode(hwnd: isize, enabled: bool) -> Result<(), WindowsPlatformError> {
        native::set_window_dark_mode(hwnd, enabled)
    }

    /// Cloaks/uncloaks a window at the DWM level. A cloaked window is fully
    /// managed (shown, focusable, presents frames) but composites nothing, so
    /// the OS pre-rendered legacy frame never reaches the screen before the
    /// renderer's first present.
    pub fn set_window_cloaked(hwnd: isize, cloaked: bool) -> Result<(), WindowsPlatformError> {
        native::set_window_cloaked(hwnd, cloaked)
    }

    pub fn set_window_maximized(hwnd: isize, maximized: bool) -> Result<(), WindowsPlatformError> {
        native::set_window_maximized(hwnd, maximized)
    }

    pub fn toggle_window_maximized(hwnd: isize) -> Result<(), WindowsPlatformError> {
        native::toggle_window_maximized(hwnd)
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
    pub fn apply_window_placement_to_hwnd(
        hwnd: isize,
        options: &WindowOptions,
    ) -> Result<(), WindowsPlatformError> {
        let cursor = native::cursor_position()?;
        let monitor = native::monitor_metrics_for_point(cursor)?;
        let placement = Self::resolve_window_placement_for_monitor(options, cursor, monitor);
        let (x, y, width, height) = physical_window_geometry(options, placement, monitor);
        native::set_window_geometry(hwnd, x, y, width, height)
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
            let mut next_command_id = 1000;
            let items = tray
                .items
                .iter()
                .map(|item| plan_tray_item(item, &mut next_command_id))
                .collect::<Vec<_>>();
            Some(WindowsTrayPlan {
                tooltip: tray.tooltip.clone(),
                icon_path: tray.icon_path.clone(),
                presenter_kind: tray.presenter_kind,
                presenter_min_width: tray.presenter_min_width,
                presenter_style: tray.presenter_style,
                callback_message: native::wm_user() + 1,
                item_count: (next_command_id - 1000) as usize,
                default_command_id: tray.default_item_id.as_deref().and_then(|id| {
                    find_enabled_tray_command_by_id(&items, id).map(|item| item.command_id)
                }),
                items,
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

    pub fn create_tray_icon(
        plan: &WindowsTrayPlan,
    ) -> Result<WindowsTrayHandle, WindowsPlatformError> {
        native::create_tray_icon(plan)
    }

    pub fn wait_for_tray_event(
        handle: &WindowsTrayHandle,
        timeout: Duration,
    ) -> Result<Option<WindowsTrayEvent>, WindowsPlatformError> {
        let start = Instant::now();

        loop {
            if let Some(message) = native::poll_tray_message(handle)? {
                match message {
                    NativeTrayMessage::Command { command_id } => {
                        if let Some(item) =
                            find_tray_item_by_command(&handle.plan.items, command_id)
                        {
                            return Ok(Some(WindowsTrayEvent::Command {
                                id: item.id.clone(),
                                command_id: item.command_id,
                            }));
                        }
                    }
                    NativeTrayMessage::OpenMenu { x, y } => {
                        return Ok(Some(WindowsTrayEvent::OpenMenu { x, y }));
                    }
                }
            }

            if start.elapsed() >= timeout {
                return Ok(None);
            }

            std::thread::sleep(Duration::from_millis(10));
        }
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

    /// Queries this process's current GPU/video memory usage (debug builds only).
    /// Returns `None` on non-debug builds, non-Windows, or if DXGI is unavailable.
    pub fn current_gpu_memory() -> Option<WindowsGpuMemory> {
        #[cfg(all(windows, debug_assertions))]
        {
            native::current_gpu_memory().ok()
        }
        #[cfg(not(all(windows, debug_assertions)))]
        {
            None
        }
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

#[derive(Debug)]
pub struct WindowsTrayHandle {
    plan: WindowsTrayPlan,
    native: native::TrayHandle,
}

impl WindowsTrayHandle {
    pub fn plan(&self) -> &WindowsTrayPlan {
        &self.plan
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct NativeHotkeyMessage {
    native_id: i32,
    modifiers: u32,
    virtual_key: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum NativeTrayMessage {
    Command { command_id: u32 },
    OpenMenu { x: i32, y: i32 },
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
                    icon_path: None,
                    presenter_kind: TrayMenuPresenterKind::default(),
                    presenter_min_width: None,
                    presenter_style: TrayMenuPresenterStyle::default(),
                    callback_message: native::wm_user() + 1,
                    item_count: 0,
                    default_command_id: None,
                    items: Vec::new(),
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

fn find_tray_item_by_command(
    items: &[WindowsTrayItemPlan],
    command_id: u32,
) -> Option<&WindowsTrayItemPlan> {
    items.iter().find_map(|item| {
        if item.kind == WindowsTrayItemKind::Command && item.command_id == command_id {
            Some(item)
        } else {
            find_tray_item_by_command(&item.children, command_id)
        }
    })
}

fn find_enabled_tray_command_by_id<'a>(
    items: &'a [WindowsTrayItemPlan],
    id: &str,
) -> Option<&'a WindowsTrayItemPlan> {
    items.iter().find_map(|item| {
        if item.kind == WindowsTrayItemKind::Command && item.enabled && item.id == id {
            Some(item)
        } else if item.enabled {
            find_enabled_tray_command_by_id(&item.children, id)
        } else {
            None
        }
    })
}

fn plan_tray_item<Message>(
    item: &TrayMenuItem<Message>,
    next_command_id: &mut u32,
) -> WindowsTrayItemPlan {
    if item.is_separator() {
        return WindowsTrayItemPlan {
            id: item.id.clone(),
            label: item.label.clone(),
            tooltip: item.tooltip.clone(),
            enabled: false,
            command_id: 0,
            action_kind: ActionKind::None,
            kind: WindowsTrayItemKind::Separator,
            children: Vec::new(),
        };
    }

    if item.is_submenu() {
        return WindowsTrayItemPlan {
            id: item.id.clone(),
            label: item.label.clone(),
            tooltip: item.tooltip.clone(),
            enabled: item.enabled,
            command_id: 0,
            action_kind: ActionKind::None,
            kind: WindowsTrayItemKind::Submenu,
            children: item
                .children
                .iter()
                .map(|child| plan_tray_item(child, next_command_id))
                .collect(),
        };
    }

    let command_id = *next_command_id;
    *next_command_id += 1;
    WindowsTrayItemPlan {
        id: item.id.clone(),
        label: item.label.clone(),
        tooltip: item.tooltip.clone(),
        enabled: item.enabled,
        command_id,
        action_kind: item.action.kind(),
        kind: WindowsTrayItemKind::Command,
        children: Vec::new(),
    }
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
        A11yRole::Hyperlink => WindowsUiaControlType::Hyperlink,
        A11yRole::Image => WindowsUiaControlType::Image,
        A11yRole::List => WindowsUiaControlType::List,
        A11yRole::ListItem => WindowsUiaControlType::ListItem,
        A11yRole::MenuItem => WindowsUiaControlType::MenuItem,
        A11yRole::Pane | A11yRole::ScrollView => WindowsUiaControlType::Pane,
        A11yRole::ProgressBar => WindowsUiaControlType::ProgressBar,
        A11yRole::RadioButton => WindowsUiaControlType::RadioButton,
        A11yRole::Slider => WindowsUiaControlType::Slider,
        A11yRole::StaticText => WindowsUiaControlType::Text,
        A11yRole::Tab => WindowsUiaControlType::Tab,
        A11yRole::TabItem => WindowsUiaControlType::TabItem,
        A11yRole::TextInput => WindowsUiaControlType::Edit,
        A11yRole::Tooltip => WindowsUiaControlType::ToolTip,
        A11yRole::Tree => WindowsUiaControlType::Tree,
        A11yRole::TreeItem => WindowsUiaControlType::TreeItem,
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
        WindowResizeMode::CanResize => {
            if matches!(
                options.frame,
                WindowFrame::Borderless | WindowFrame::Acrylic
            ) {
                style |= native::ws_thickframe();
                style |= native::ws_maximize_box() | native::ws_minimize_box();
            }
        }
        WindowResizeMode::CanMinimize => {
            style &= !native::ws_thickframe();
            style &= !native::ws_maximize_box();
            style |= native::ws_minimize_box();
        }
        WindowResizeMode::Fixed => {
            style &= !native::ws_thickframe();
            style &= !native::ws_maximize_box();
            style &= !native::ws_minimize_box();
        }
    }

    style
}

fn merge_window_style(current: u32, desired: u32) -> u32 {
    let managed_bits = native::ws_popup() | native::ws_overlapped_window();
    (current & !managed_bits) | (desired & managed_bits)
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
        WindowPlacement::ContextMenu { x, y } => context_menu_position(
            x.round() as i32,
            y.round() as i32,
            width,
            height,
            constraint_area,
            0,
            0,
        ),
        WindowPlacement::ContextMenuInset {
            x,
            y,
            inset_x,
            inset_y,
        } => context_menu_position(
            x.round() as i32,
            y.round() as i32,
            width,
            height,
            constraint_area,
            inset_x.round() as i32,
            inset_y.round() as i32,
        ),
        WindowPlacement::ContextMenuAtCursor { inset_x, inset_y } => context_menu_position_signed(
            cursor.x,
            cursor.y,
            width,
            height,
            constraint_area,
            inset_x.round() as i32,
            inset_y.round() as i32,
        ),
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
fn physical_window_geometry(
    options: &WindowOptions,
    placement: ResolvedWindowPlacement,
    monitor: WindowsMonitorMetrics,
) -> (i32, i32, i32, i32) {
    let scale = monitor.scale_factor();
    let (logical_area, physical_area) = if options.placement == WindowPlacement::Monitor {
        (monitor.monitor_area_dips(), monitor.physical_monitor_area)
    } else {
        (monitor.work_area_dips(), monitor.physical_work_area)
    };
    let x = physical_area.left + ((placement.x - logical_area.left) as f32 * scale).round() as i32;
    let y = physical_area.top + ((placement.y - logical_area.top) as f32 * scale).round() as i32;
    let width = (placement.width as f32 * scale).round().max(1.0) as i32;
    let height = (placement.height as f32 * scale).round().max(1.0) as i32;
    (x, y, width, height)
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

fn context_menu_position(
    anchor_x: i32,
    anchor_y: i32,
    width: i32,
    height: i32,
    area: WindowsRect,
    inset_x: i32,
    inset_y: i32,
) -> (i32, i32) {
    let inset_x = inset_x.clamp(0, width.saturating_sub(1) / 2);
    let inset_y = inset_y.clamp(0, height.saturating_sub(1) / 2);
    let right_up_x = anchor_x - inset_x;
    let left_up_x = anchor_x - width + inset_x;
    let x = if right_up_x + width <= area.right {
        right_up_x
    } else if left_up_x >= area.left {
        left_up_x
    } else {
        clamp_axis(right_up_x, width, area.left, area.right)
    };

    let upper_y = anchor_y - height + inset_y;
    let lower_y = anchor_y - inset_y;
    let y = if upper_y >= area.top {
        upper_y
    } else if lower_y + height <= area.bottom {
        lower_y
    } else {
        clamp_axis(upper_y, height, area.top, area.bottom)
    };

    (x, y)
}

fn context_menu_position_signed(
    anchor_x: i32,
    anchor_y: i32,
    width: i32,
    height: i32,
    area: WindowsRect,
    inset_x: i32,
    inset_y: i32,
) -> (i32, i32) {
    let right_x = anchor_x - inset_x;
    let left_x = anchor_x - width + inset_x;
    let x = if right_x + width <= area.right {
        right_x
    } else if left_x >= area.left {
        left_x
    } else {
        clamp_axis(right_x, width, area.left, area.right)
    };

    let upper_y = anchor_y - height + inset_y;
    let lower_y = anchor_y - inset_y;
    let y = if upper_y >= area.top {
        upper_y
    } else if lower_y + height <= area.bottom {
        lower_y
    } else {
        clamp_axis(upper_y, height, area.top, area.bottom)
    };

    (x, y)
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
    use std::collections::{BTreeMap, VecDeque};
    use std::io::Write;
    #[cfg(feature = "legacy-powershell-tts")]
    use std::process::Command;
    use std::ptr::{null, null_mut};
    use std::sync::{Mutex, OnceLock};
    use std::time::{Duration, SystemTime, UNIX_EPOCH};

    use super::{
        ClipboardFormat, NativeHotkeyMessage, NativeTrayMessage, WindowsClipboardFormatSnapshot,
        WindowsClipboardTextSnapshot, WindowsDataProtectionScope, WindowsHotkey,
        WindowsHotkeyHandle, WindowsMonitorMetrics, WindowsNamedEvent, WindowsNamedEventHandle,
        WindowsPlatformError, WindowsPoint, WindowsProcessMemory, WindowsRect, WindowsTrayHandle,
        WindowsTrayPlan,
    };
    use win_fluent::platform::{
        ScreenCaptureRequest, ScreenCaptureResult, ScreenRect, ScreenWindow,
        ScreenWindowSnapshotRequest, TrayMenuColor, TrayMenuPopupAnimation, TrayMenuPresenterKind,
        TrayMenuPresenterStyle,
    };
    use windows_sys::Win32::Foundation::{
        CloseHandle, GetLastError, GlobalFree, SetLastError, ERROR_FILE_NOT_FOUND, ERROR_SUCCESS,
        HANDLE, HWND, LPARAM, LRESULT, POINT, RECT, WAIT_OBJECT_0, WAIT_TIMEOUT, WPARAM,
    };
    use windows_sys::Win32::Graphics::Dwm::{
        DwmSetWindowAttribute, DWMWA_CLOAK, DWMWA_USE_IMMERSIVE_DARK_MODE,
        DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND, DWMWCP_ROUND, DWMWCP_ROUNDSMALL,
    };
    const DWMWA_BORDER_COLOR: u32 = 34;
    const DWMWA_COLOR_DEFAULT: i32 = -1;
    const DWMWA_COLOR_NONE: i32 = -2;
    use windows_sys::Win32::Graphics::Gdi::{
        BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, GetDC,
        GetDIBits, GetMonitorInfoW, GetWindowDC, MonitorFromPoint, MonitorFromWindow, ReleaseDC,
        SelectObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS, HBITMAP, HDC, HGDIOBJ,
        MONITORINFO, MONITOR_DEFAULTTONEAREST, SRCCOPY,
    };
    // Owner-drawn tray menu (fluent WinUI-style item rendering).
    use windows_sys::Win32::Graphics::Gdi::{
        CreateFontIndirectW, CreateRoundRectRgn, CreateSolidBrush, DrawTextW, FillRect, FillRgn,
        GetDeviceCaps, GetSysColor, GetTextExtentPoint32W, SetBkMode, SetTextColor, COLOR_GRAYTEXT,
        COLOR_MENU, DT_LEFT, DT_SINGLELINE, DT_VCENTER, HBRUSH, HFONT, LOGFONTW, LOGPIXELSX,
        TRANSPARENT,
    };
    use windows_sys::Win32::Security::Cryptography::{
        CryptProtectData, CryptUnprotectData, CRYPTPROTECT_LOCAL_MACHINE,
        CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
    };
    use windows_sys::Win32::System::DataExchange::{
        CloseClipboard, EmptyClipboard, EnumClipboardFormats, GetClipboardData,
        IsClipboardFormatAvailable, OpenClipboard, SetClipboardData,
    };
    use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
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
        KEY_SET_VALUE, REG_DWORD, REG_SZ,
    };
    use windows_sys::Win32::System::Threading::{
        AttachThreadInput, CreateEventExW, GetCurrentProcess, GetCurrentThreadId, OpenEventW,
        SetEvent, WaitForSingleObject, CREATE_EVENT_MANUAL_RESET, EVENT_MODIFY_STATE,
        SYNCHRONIZATION_SYNCHRONIZE,
    };
    use windows_sys::Win32::UI::HiDpi::{GetDpiForMonitor, GetDpiForWindow, MDT_EFFECTIVE_DPI};
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        RegisterHotKey, SendInput, UnregisterHotKey, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT,
        KEYEVENTF_KEYUP, KEYEVENTF_UNICODE, MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN, VK_BACK,
        VK_CONTROL, VK_DELETE, VK_DOWN, VK_END, VK_ESCAPE, VK_F1, VK_HOME, VK_LEFT, VK_LWIN,
        VK_MENU, VK_RETURN, VK_RIGHT, VK_SHIFT, VK_SPACE, VK_TAB, VK_UP,
    };
    use windows_sys::Win32::UI::Shell::{
        DefSubclassProc, RemoveWindowSubclass, SetWindowSubclass, ShellExecuteW, Shell_NotifyIconW,
        NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NIM_SETVERSION, NIN_SELECT,
        NOTIFYICONDATAW, NOTIFYICON_VERSION_4,
    };
    #[cfg(test)]
    use windows_sys::Win32::UI::WindowsAndMessaging::PostThreadMessageW;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CreatePopupMenu, CreateWindowExW, DefWindowProcW, DestroyIcon, DestroyMenu, DestroyWindow,
        DispatchMessageW, EnumChildWindows, EnumWindows, GetClassNameW, GetCursorPos,
        GetForegroundWindow, GetParent, GetSystemMetrics, GetWindowLongPtrW, GetWindowRect,
        GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsWindow, IsWindowVisible,
        IsZoomed, LoadIconW, LoadImageW, PeekMessageW, PostMessageW, RegisterClassW, SetCursorPos,
        SetForegroundWindow, SetWindowLongPtrW, SetWindowPos, SetWindowTextW, ShowWindow,
        TrackPopupMenuEx, TranslateMessage, CS_HREDRAW, CS_VREDRAW, CW_USEDEFAULT, GWL_EXSTYLE,
        GWL_STYLE, HICON, HTBOTTOM, HTBOTTOMLEFT, HTBOTTOMRIGHT, HTCLIENT, HTLEFT, HTRIGHT, HTTOP,
        HTTOPLEFT, HTTOPRIGHT, HWND_NOTOPMOST, HWND_TOPMOST, IDI_APPLICATION, IMAGE_ICON,
        LR_DEFAULTSIZE, LR_LOADFROMFILE, MF_POPUP, MF_SEPARATOR, MF_SYSMENU, MINMAXINFO, MSG,
        PM_REMOVE, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
        SWP_FRAMECHANGED, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SWP_NOZORDER, SWP_SHOWWINDOW,
        SW_HIDE, SW_MAXIMIZE, SW_RESTORE, SW_SHOWNORMAL, TPM_LEFTALIGN, TPM_NOANIMATION,
        TPM_RETURNCMD, TPM_RIGHTBUTTON, TPM_VERNEGANIMATION, TPM_VERPOSANIMATION, WM_CONTEXTMENU,
        WM_GETMINMAXINFO, WM_HOTKEY, WM_LBUTTONDBLCLK, WM_LBUTTONUP, WM_MENUSELECT, WM_NCDESTROY,
        WM_NCHITTEST, WM_NULL, WM_RBUTTONUP, WM_USER, WNDCLASSW, WS_BORDER, WS_EX_NOACTIVATE,
        WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_MAXIMIZEBOX, WS_MINIMIZEBOX, WS_OVERLAPPEDWINDOW,
        WS_POPUP, WS_THICKFRAME,
    };
    // Owner-drawn tray menu support.
    use windows_sys::Win32::Foundation::SIZE;
    use windows_sys::Win32::UI::Controls::{
        DRAWITEMSTRUCT, MEASUREITEMSTRUCT, ODS_DISABLED, ODS_GRAYED, ODS_SELECTED,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        InsertMenuItemW, SetMenuInfo, SystemParametersInfoW, MENUINFO, MENUITEMINFOW, MFS_ENABLED,
        MFS_GRAYED, MFT_OWNERDRAW, MFT_SEPARATOR, MIIM_DATA, MIIM_FTYPE, MIIM_ID, MIIM_STATE,
        MIIM_SUBMENU, MIM_APPLYTOSUBMENUS, MIM_BACKGROUND, MIM_MAXHEIGHT, MIM_STYLE,
        MNS_AUTODISMISS, MNS_NOCHECK, NONCLIENTMETRICSW, SPI_GETNONCLIENTMETRICS, WM_DRAWITEM,
        WM_MEASUREITEM,
    };

    static TEXT_INSERTION_TARGET: Mutex<isize> = Mutex::new(0);
    static TRAY_MENU_TOOLTIP_STATES: OnceLock<Mutex<BTreeMap<isize, TrayMenuTooltipState>>> =
        OnceLock::new();
    const UIA_TRAY_CONTEXT_MENU_POINT_ENV: &str = "EASYDICT_UIA_TRAY_CONTEXT_MENU_POINT";
    const UIA_TRAY_CONTEXT_MENU_DELAY_ENV: &str = "EASYDICT_UIA_TRAY_CONTEXT_MENU_DELAY_MS";
    pub(super) const NIN_KEYSELECT: u32 = WM_USER + 1;

    /// Per-window queue of resolved tray-callback events, keyed by HWND.
    ///
    /// The Windows shell delivers `Shell_NotifyIcon` callbacks via `SendMessage`,
    /// so they reach the window procedure (not `PeekMessage`'s return value). The
    /// procedure records the canonical activation/context event here, and the
    /// poll loop drains it on its own (poll) thread. The window procedure and the
    /// poll loop run on the same thread, so the lock is always uncontended and is
    /// never held across a blocking call (e.g. `TrackPopupMenu`).
    static TRAY_CALLBACK_QUEUES: OnceLock<Mutex<BTreeMap<isize, TrayCallbackQueue>>> =
        OnceLock::new();

    struct TrayCallbackQueue {
        callback_message: u32,
        events: VecDeque<u32>,
    }

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

    #[derive(Debug)]
    pub struct TrayHandle {
        hwnd: HWND,
        icon_id: u32,
        callback_message: u32,
        owned_icon: Option<HICON>,
        presenter_kind: TrayMenuPresenterKind,
        presenter_min_width: Option<u16>,
        presenter_style: TrayMenuPresenterStyle,
        default_command_id: Option<u32>,
        menu_items: Vec<super::WindowsTrayItemPlan>,
    }

    #[derive(Debug, Default)]
    pub(super) struct TrayMenuTooltipState {
        pub(super) command_tooltips: BTreeMap<u32, String>,
        pub(super) submenu_tooltips: BTreeMap<(isize, u32), String>,
        tooltip_hwnd: isize,
        visible_text: Option<String>,
    }

    impl Drop for TrayHandle {
        fn drop(&mut self) {
            unregister_tray_callback_queue(self.hwnd);
            clear_tray_menu_tooltip_state(self.hwnd);
            let mut data = tray_icon_data(self.hwnd, self.icon_id, self.callback_message);
            // Safety: data identifies the icon added by create_tray_icon for this hidden HWND.
            let _ = unsafe { Shell_NotifyIconW(NIM_DELETE, &mut data as _) };
            destroy_owned_icon(self.owned_icon);
            if !self.hwnd.is_null() {
                // Safety: hwnd was created by CreateWindowExW and is owned by this handle.
                let _ = unsafe { DestroyWindow(self.hwnd) };
            }
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

    fn load_tray_icon(plan: &WindowsTrayPlan) -> (HICON, Option<HICON>) {
        if let Some(path) = plan
            .icon_path
            .as_deref()
            .map(str::trim)
            .filter(|path| !path.is_empty())
        {
            let wide_path = wide_null(path);
            // Safety: LoadImageW reads a null-terminated path buffer for this call.
            // LR_LOADFROMFILE returns a private icon handle that this module owns.
            let icon = unsafe {
                LoadImageW(
                    null_mut(),
                    wide_path.as_ptr(),
                    IMAGE_ICON,
                    0,
                    0,
                    LR_LOADFROMFILE | LR_DEFAULTSIZE,
                ) as HICON
            };
            if !icon.is_null() {
                return (icon, Some(icon));
            }
        }

        // Safety: Loading the predefined application icon returns a shared icon handle.
        (unsafe { LoadIconW(null_mut(), IDI_APPLICATION) }, None)
    }

    fn destroy_owned_icon(icon: Option<HICON>) {
        if let Some(icon) = icon {
            if !icon.is_null() {
                // Safety: icon is only populated for LR_LOADFROMFILE, which returns
                // a private HICON owned by this tray handle or error path.
                let _ = unsafe { DestroyIcon(icon) };
            }
        }
    }

    pub fn create_tray_icon(
        plan: &WindowsTrayPlan,
    ) -> Result<WindowsTrayHandle, WindowsPlatformError> {
        if plan.items.is_empty() {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "CreateTrayIcon",
                code: 0,
            });
        }

        let hwnd = create_tray_window()?;
        let icon_id = 1;
        let mut data = tray_icon_data(hwnd, icon_id, plan.callback_message);
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        let (icon, owned_icon) = load_tray_icon(plan);
        data.hIcon = icon;
        copy_wide_truncated(&mut data.szTip, &plan.tooltip);

        // Safety: data contains a valid hidden HWND owned by this thread.
        if unsafe { Shell_NotifyIconW(NIM_ADD, &mut data as _) } == 0 {
            destroy_owned_icon(owned_icon);
            // Safety: hwnd was created above and has not escaped on this error path.
            let _ = unsafe { DestroyWindow(hwnd) };
            return Err(last_error("Shell_NotifyIconW(NIM_ADD)"));
        }

        data.Anonymous.uVersion = NOTIFYICON_VERSION_4;
        // Safety: data identifies the newly-added icon and sets its shell notification version.
        if unsafe { Shell_NotifyIconW(NIM_SETVERSION, &mut data as _) } == 0 {
            let mut delete_data = tray_icon_data(hwnd, icon_id, plan.callback_message);
            // Safety: best-effort cleanup for the icon added above.
            let _ = unsafe { Shell_NotifyIconW(NIM_DELETE, &mut delete_data as _) };
            destroy_owned_icon(owned_icon);
            // Safety: hwnd was created above and has not escaped on this error path.
            let _ = unsafe { DestroyWindow(hwnd) };
            return Err(last_error("Shell_NotifyIconW(NIM_SETVERSION)"));
        }

        // Ready to receive callbacks: register the queue the window procedure
        // pushes resolved tray events into for the poll loop to drain.
        register_tray_callback_queue(hwnd, plan.callback_message);
        schedule_uia_tray_context_menu(hwnd, plan.callback_message);

        Ok(WindowsTrayHandle {
            plan: plan.clone(),
            native: TrayHandle {
                hwnd,
                icon_id,
                callback_message: plan.callback_message,
                owned_icon,
                presenter_kind: plan.presenter_kind,
                presenter_min_width: plan.presenter_min_width,
                presenter_style: plan.presenter_style,
                default_command_id: plan.default_command_id,
                menu_items: plan.items.clone(),
            },
        })
    }

    pub fn poll_tray_message(
        handle: &WindowsTrayHandle,
    ) -> Result<Option<NativeTrayMessage>, WindowsPlatformError> {
        // Pump the tray window's message queue so its window procedure runs.
        // `Shell_NotifyIcon` delivers its callback via `SendMessage`, which is
        // dispatched to the window procedure as a side effect of `PeekMessage`
        // (it is never *returned* by it); any posted messages are dispatched
        // explicitly. The procedure records the resolved activation/context event
        // into this window's callback queue via `record_tray_callback`.
        let mut message = MSG::default();
        // Safety: PeekMessageW writes to a valid MSG pointer scoped to the tray HWND;
        // the dispatched messages target a window owned by this thread.
        while unsafe { PeekMessageW(&mut message, handle.native.hwnd, 0, 0, PM_REMOVE) } != 0 {
            unsafe { TranslateMessage(&message) };
            unsafe { DispatchMessageW(&message) };
        }

        let Some(event) = take_tray_callback_event(handle.native.hwnd) else {
            return Ok(None);
        };

        if is_tray_default_activation_message(event) {
            let item = handle
                .native
                .default_command_id
                .and_then(|command_id| {
                    super::find_tray_item_by_command(&handle.native.menu_items, command_id)
                })
                .or_else(|| first_enabled_tray_command(&handle.native.menu_items));

            return Ok(item.map(|item| NativeTrayMessage::Command {
                command_id: item.command_id,
            }));
        }

        if is_tray_context_menu_message(event) {
            if handle.native.presenter_kind == TrayMenuPresenterKind::Fluent {
                let cursor = cursor_position()?;
                let monitor = monitor_metrics_for_point(cursor)?;
                let anchor = super::physical_point_to_dips(cursor, monitor.scale_factor());
                return Ok(Some(NativeTrayMessage::OpenMenu {
                    x: anchor.x,
                    y: anchor.y,
                }));
            }
            return show_tray_menu(&handle.native);
        }

        Ok(None)
    }

    /// Extracts the shell notification event from a tray callback's `lParam`.
    ///
    /// The icon is registered with `NOTIFYICON_VERSION_4`, which packs the
    /// notification event (e.g. `WM_LBUTTONUP`, `NIN_SELECT`, `WM_CONTEXTMENU`)
    /// into the **low word** of `lParam` and the icon id into the high word.
    /// Comparing the raw `lParam` against the event constants therefore never
    /// matches (the high word holds the icon id), so the event must be masked
    /// out with `LOWORD` before dispatching — otherwise every tray click is
    /// silently dropped.
    pub(super) const fn tray_notification_event(lparam: isize) -> u32 {
        (lparam as u32) & 0xFFFF
    }

    pub(super) const fn is_tray_default_activation_message(message: u32) -> bool {
        matches!(
            message,
            WM_LBUTTONUP | WM_LBUTTONDBLCLK | NIN_SELECT | NIN_KEYSELECT
        )
    }

    pub(super) const fn is_tray_context_menu_message(message: u32) -> bool {
        matches!(message, WM_RBUTTONUP | WM_CONTEXTMENU)
    }

    fn tray_callback_queues() -> &'static Mutex<BTreeMap<isize, TrayCallbackQueue>> {
        TRAY_CALLBACK_QUEUES.get_or_init(|| Mutex::new(BTreeMap::new()))
    }

    pub(super) fn register_tray_callback_queue(hwnd: HWND, callback_message: u32) {
        let mut queues = tray_callback_queues()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        queues.insert(
            hwnd as isize,
            TrayCallbackQueue {
                callback_message,
                events: VecDeque::new(),
            },
        );
    }

    pub(super) fn unregister_tray_callback_queue(hwnd: HWND) {
        let mut queues = tray_callback_queues()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        queues.remove(&(hwnd as isize));
    }

    /// Records a `Shell_NotifyIcon` callback delivered to the window procedure.
    /// Returns `true` if `message` is this window's tray callback (so the window
    /// procedure can stop processing it).
    ///
    /// `NOTIFYICON_VERSION_4` emits *both* the legacy mouse-up (`WM_LBUTTONUP` /
    /// `WM_RBUTTONUP`) and the modern notification (`NIN_SELECT` /
    /// `WM_CONTEXTMENU`) for a single gesture. Only the modern notification is
    /// recorded, so the bound action fires exactly once per click.
    pub(super) fn record_tray_callback(hwnd: HWND, message: u32, lparam: LPARAM) -> bool {
        let mut queues = tray_callback_queues()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let Some(queue) = queues.get_mut(&(hwnd as isize)) else {
            return false;
        };
        if message != queue.callback_message {
            return false;
        }

        let event = tray_notification_event(lparam);
        if matches!(event, NIN_SELECT | NIN_KEYSELECT | WM_CONTEXTMENU) {
            queue.events.push_back(event);
        }
        true
    }

    pub(super) fn take_tray_callback_event(hwnd: HWND) -> Option<u32> {
        let mut queues = tray_callback_queues()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        queues
            .get_mut(&(hwnd as isize))
            .and_then(|queue| queue.events.pop_front())
    }

    fn schedule_uia_tray_context_menu(hwnd: HWND, callback_message: u32) {
        let Some((x, y)) = uia_tray_context_menu_point() else {
            return;
        };

        let delay_ms = uia_tray_context_menu_delay_ms();
        let hwnd_value = hwnd as isize;
        std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_millis(delay_ms));
            let hwnd = hwnd_value as HWND;
            // Safety: SetCursorPos uses screen coordinates supplied by the UI automation test.
            let _ = unsafe { SetCursorPos(x, y) };
            // NOTIFYICON_VERSION_4 stores the event in LOWORD(lParam) and the icon id in HIWORD.
            let lparam = ((1u32 << 16) | (WM_CONTEXTMENU & 0xffff)) as isize;
            // Safety: hwnd is the hidden tray host window created by this process.
            let _ = unsafe { PostMessageW(hwnd, callback_message, 0, lparam) };
        });
    }

    fn uia_tray_context_menu_point() -> Option<(i32, i32)> {
        let value = std::env::var(UIA_TRAY_CONTEXT_MENU_POINT_ENV).ok()?;
        let (x, y) = value.split_once(',').or_else(|| value.split_once(';'))?;
        let x = x.trim().parse::<i32>().ok()?;
        let y = y.trim().parse::<i32>().ok()?;
        Some((x, y))
    }

    fn uia_tray_context_menu_delay_ms() -> u64 {
        std::env::var(UIA_TRAY_CONTEXT_MENU_DELAY_ENV)
            .ok()
            .and_then(|value| value.trim().parse::<u64>().ok())
            .map(|delay| delay.clamp(100, 10_000))
            .unwrap_or(900)
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

    const RESIZE_HIT_TEST_SUBCLASS_ID: usize = 0x5746_5253;

    unsafe extern "system" fn resize_hit_test_subclass_proc(
        hwnd: HWND,
        message: u32,
        wparam: WPARAM,
        lparam: LPARAM,
        _subclass_id: usize,
        _reference_data: usize,
    ) -> LRESULT {
        if message == WM_GETMINMAXINFO && lparam != 0 {
            // Let winit populate constraints such as ptMinTrackSize before overriding only
            // the monitor work-area maximize geometry.
            let default_result = unsafe { DefSubclassProc(hwnd, message, wparam, lparam) };
            let monitor = unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) };
            let mut monitor_info = MONITORINFO {
                cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            if !monitor.is_null() && unsafe { GetMonitorInfoW(monitor, &mut monitor_info) } != 0 {
                let maximize = unsafe { &mut *(lparam as *mut MINMAXINFO) };
                maximize.ptMaxPosition.x = monitor_info.rcWork.left - monitor_info.rcMonitor.left;
                maximize.ptMaxPosition.y = monitor_info.rcWork.top - monitor_info.rcMonitor.top;
                maximize.ptMaxSize.x = monitor_info.rcWork.right - monitor_info.rcWork.left;
                maximize.ptMaxSize.y = monitor_info.rcWork.bottom - monitor_info.rcWork.top;
            }
            return default_result;
        }

        if message == WM_NCHITTEST {
            // Safety: the subclass contract permits forwarding to the next procedure.
            let default_result = unsafe { DefSubclassProc(hwnd, message, wparam, lparam) };
            if default_result != HTCLIENT as LRESULT {
                return default_result;
            }

            let mut rect = RECT::default();
            // Safety: hwnd is the live window invoking this subclass callback.
            if unsafe { GetWindowRect(hwnd, &mut rect) } != 0 {
                // Safety: hwnd is live for the duration of this callback.
                let dpi = unsafe { GetDpiForWindow(hwnd) }.max(96);
                let border = ((8 * dpi + 95) / 96).max(1) as i32;
                let raw = lparam as u32;
                let x = (raw as u16 as i16) as i32;
                let y = ((raw >> 16) as u16 as i16) as i32;
                let left = x < rect.left + border;
                let right = x >= rect.right - border;
                let top = y < rect.top + border;
                let bottom = y >= rect.bottom - border;

                return match (left, right, top, bottom) {
                    (true, _, true, _) => HTTOPLEFT as LRESULT,
                    (_, true, true, _) => HTTOPRIGHT as LRESULT,
                    (true, _, _, true) => HTBOTTOMLEFT as LRESULT,
                    (_, true, _, true) => HTBOTTOMRIGHT as LRESULT,
                    (true, _, _, _) => HTLEFT as LRESULT,
                    (_, true, _, _) => HTRIGHT as LRESULT,
                    (_, _, true, _) => HTTOP as LRESULT,
                    (_, _, _, true) => HTBOTTOM as LRESULT,
                    _ => default_result,
                };
            }

            return default_result;
        }

        if message == WM_NCDESTROY {
            // Safety: hwnd is live during WM_NCDESTROY and this removes only our subclass id.
            let _ = unsafe {
                RemoveWindowSubclass(
                    hwnd,
                    Some(resize_hit_test_subclass_proc),
                    RESIZE_HIT_TEST_SUBCLASS_ID,
                )
            };
        }

        // Safety: all messages not handled above continue through the subclass chain.
        unsafe { DefSubclassProc(hwnd, message, wparam, lparam) }
    }

    pub fn apply_window_corner_preference(hwnd: isize, rounded: bool) {
        if !rounded {
            return;
        }

        let hwnd = hwnd as HWND;
        if !is_valid_window(hwnd) {
            return;
        }

        // Best effort: this Windows 11 attribute is unavailable on older systems.
        let preference = DWMWCP_ROUND;
        let _ = unsafe {
            DwmSetWindowAttribute(
                hwnd,
                DWMWA_WINDOW_CORNER_PREFERENCE as u32,
                (&preference as *const i32).cast(),
                std::mem::size_of_val(&preference) as u32,
            )
        };
    }

    pub fn apply_window_native_border(
        hwnd: isize,
        enabled: bool,
    ) -> Result<(), WindowsPlatformError> {
        let value = if enabled { DWMWA_COLOR_DEFAULT } else { DWMWA_COLOR_NONE };
        let result = unsafe {
            DwmSetWindowAttribute(
                hwnd as HWND,
                DWMWA_BORDER_COLOR,
                (&value as *const i32).cast(),
                std::mem::size_of::<i32>() as u32,
            )
        };
        if result == 0 || result == -2147024809 {
            return Ok(());
        }
        Err(WindowsPlatformError::NativeCallFailed {
            operation: "DwmSetWindowAttribute(DWMWA_BORDER_COLOR)",
            code: result as u32,
        })
    }

    pub fn configure_resize_hit_test(
        hwnd: isize,
        enabled: bool,
    ) -> Result<(), WindowsPlatformError> {
        let hwnd = hwnd as HWND;
        if !is_valid_window(hwnd) {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "IsWindow",
                code: 0,
            });
        }

        if enabled {
            // Safety: hwnd is valid; the callback stores no borrowed state and removes itself on destroy.
            if unsafe {
                SetWindowSubclass(
                    hwnd,
                    Some(resize_hit_test_subclass_proc),
                    RESIZE_HIT_TEST_SUBCLASS_ID,
                    0,
                )
            } == 0
            {
                return Err(last_error("SetWindowSubclass"));
            }
        } else {
            // Safety: removing an absent subclass is harmless; hwnd was validated above.
            let _ = unsafe {
                RemoveWindowSubclass(
                    hwnd,
                    Some(resize_hit_test_subclass_proc),
                    RESIZE_HIT_TEST_SUBCLASS_ID,
                )
            };
        }

        Ok(())
    }

    pub fn system_uses_dark_theme() -> Result<bool, WindowsPlatformError> {
        const PERSONALIZE_KEY: &str =
            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const APPS_USE_LIGHT_THEME: &str = "AppsUseLightTheme";

        let Some(key) = open_registry_key(HKEY_CURRENT_USER, PERSONALIZE_KEY)? else {
            return Ok(false);
        };
        let value_name = wide_null(APPS_USE_LIGHT_THEME);
        let mut value_type = 0_u32;
        let mut byte_count = 0_u32;
        // Query metadata first so malformed or wrong-type values are handled
        // as the Windows default instead of surfacing ERROR_MORE_DATA.
        let result = unsafe {
            RegQueryValueExW(
                key.0,
                value_name.as_ptr(),
                null(),
                &mut value_type,
                null_mut(),
                &mut byte_count,
            )
        };
        if result == ERROR_FILE_NOT_FOUND {
            return Ok(false);
        }
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegQueryValueExW", result));
        }
        if value_type != REG_DWORD || byte_count != std::mem::size_of::<u32>() as u32 {
            return Ok(false);
        }

        let mut value = 1_u32;
        let result = unsafe {
            RegQueryValueExW(
                key.0,
                value_name.as_ptr(),
                null(),
                &mut value_type,
                (&mut value as *mut u32).cast(),
                &mut byte_count,
            )
        };
        if result != ERROR_SUCCESS {
            return Err(win32_error("RegQueryValueExW", result));
        }

        Ok(value == 0)
    }

    pub fn set_window_dark_mode(hwnd: isize, enabled: bool) -> Result<(), WindowsPlatformError> {
        let hwnd = hwnd as HWND;
        if !is_valid_window(hwnd) {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "IsWindow",
                code: 0,
            });
        }

        let value: i32 = i32::from(enabled);
        // Safety: hwnd was validated and DWM reads one BOOL-sized value.
        let result = unsafe {
            DwmSetWindowAttribute(
                hwnd,
                DWMWA_USE_IMMERSIVE_DARK_MODE as u32,
                (&value as *const i32).cast(),
                std::mem::size_of::<i32>() as u32,
            )
        };
        if result != 0 {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)",
                code: result as u32,
            });
        }
        Ok(())
    }

    pub fn set_window_cloaked(hwnd: isize, cloaked: bool) -> Result<(), WindowsPlatformError> {
        let hwnd = hwnd as HWND;
        if !is_valid_window(hwnd) {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "IsWindow",
                code: 0,
            });
        }

        let value: i32 = if cloaked { 1 } else { 0 };
        // Safety: hwnd was validated; DWMWA_CLOAK reads the BOOL-sized value.
        let result = unsafe {
            DwmSetWindowAttribute(
                hwnd,
                DWMWA_CLOAK as u32,
                (&value as *const i32).cast(),
                std::mem::size_of::<i32>() as u32,
            )
        };
        if result != 0 {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "DwmSetWindowAttribute",
                code: result as u32,
            });
        }
        Ok(())
    }

    pub fn show_window(hwnd: isize, activate: bool) -> Result<(), WindowsPlatformError> {
        let hwnd = hwnd as HWND;
        if !is_valid_window(hwnd) {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "IsWindow",
                code: 0,
            });
        }

        if activate {
            // Safety: hwnd was validated; preserve a hidden maximized window's show state.
            let show_command = if unsafe { IsZoomed(hwnd) } != 0 {
                SW_MAXIMIZE
            } else {
                SW_SHOWNORMAL
            };
            // Safety: hwnd was validated and ShowWindow owns no borrowed state.
            unsafe { ShowWindow(hwnd, show_command) };
        } else {
            let flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW;
            // Safety: hwnd was validated; flags expose it without activation or geometry changes.
            if unsafe { SetWindowPos(hwnd, null_mut(), 0, 0, 0, 0, flags) } == 0 {
                return Err(last_error("SetWindowPos"));
            }
        }
        Ok(())
    }

    /// Fills the whole window band (including any legacy non-client strip) on
    /// the GDI redirection surface. Used right after the FIRST `ShowWindow` so
    /// DWM composites the theme background instead of the default white
    /// surface during the gap until the renderer presents its first frame.
    pub fn paint_window_background(
        hwnd: isize,
        red: u8,
        green: u8,
        blue: u8,
    ) -> Result<(), WindowsPlatformError> {
        let hwnd = hwnd as HWND;
        if !is_valid_window(hwnd) {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "IsWindow",
                code: 0,
            });
        }

        let mut window_rect = RECT {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
        // Safety: hwnd was validated and GetWindowRect writes only into window_rect.
        if unsafe { GetWindowRect(hwnd, &mut window_rect) } == 0 {
            return Err(last_error("GetWindowRect"));
        }
        let rect = RECT {
            left: 0,
            top: 0,
            right: window_rect.right - window_rect.left,
            bottom: window_rect.bottom - window_rect.top,
        };

        // Safety: hwnd was validated; GetWindowDC covers the full window band.
        let hdc = unsafe { GetWindowDC(hwnd) };
        if hdc.is_null() {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "GetWindowDC",
                code: 0,
            });
        }

        let color = u32::from(red) | (u32::from(green) << 8) | (u32::from(blue) << 16);
        // Safety: CreateSolidBrush allocates a GDI brush released below.
        let brush = unsafe { CreateSolidBrush(color) };
        let result = if brush.is_null() {
            Err(WindowsPlatformError::NativeCallFailed {
                operation: "CreateSolidBrush",
                code: 0,
            })
        } else {
            // Safety: hdc, rect, and brush are valid for the duration of the call.
            let filled = unsafe { FillRect(hdc, &rect, brush) };
            // Safety: brush was created above and is no longer used.
            unsafe { DeleteObject(brush as HGDIOBJ) };
            if filled == 0 {
                Err(WindowsPlatformError::NativeCallFailed {
                    operation: "FillRect",
                    code: 0,
                })
            } else {
                Ok(())
            }
        };
        // Safety: hdc was acquired from GetDC for this hwnd.
        unsafe { ReleaseDC(hwnd, hdc) };
        result
    }

    pub fn set_window_maximized(hwnd: isize, maximized: bool) -> Result<(), WindowsPlatformError> {
        let hwnd = hwnd as HWND;
        if !is_valid_window(hwnd) {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "IsWindow",
                code: 0,
            });
        }

        // Safety: hwnd was validated and ShowWindow is the native Windows maximize/restore path.
        unsafe { ShowWindow(hwnd, if maximized { SW_MAXIMIZE } else { SW_RESTORE }) };
        Ok(())
    }

    pub fn toggle_window_maximized(hwnd: isize) -> Result<(), WindowsPlatformError> {
        let native_hwnd = hwnd as HWND;
        if !is_valid_window(native_hwnd) {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "IsWindow",
                code: 0,
            });
        }

        // Safety: native_hwnd was validated and IsZoomed only reads its show state.
        set_window_maximized(hwnd, unsafe { IsZoomed(native_hwnd) } == 0)
    }
    pub fn set_window_geometry(
        hwnd: isize,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
    ) -> Result<(), WindowsPlatformError> {
        let hwnd = hwnd as HWND;
        if !is_valid_window(hwnd) {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "IsWindow",
                code: 0,
            });
        }

        let flags = SWP_NOACTIVATE | SWP_NOZORDER;
        // Safety: hwnd was validated and the geometry is expressed in Win32 physical pixels.
        if unsafe { SetWindowPos(hwnd, null_mut(), x, y, width.max(1), height.max(1), flags) } == 0
        {
            return Err(last_error("SetWindowPos"));
        }
        Ok(())
    }

    pub fn apply_window_style(hwnd: isize, desired_style: u32) -> Result<(), WindowsPlatformError> {
        let hwnd = hwnd as HWND;
        if !is_valid_window(hwnd) {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "IsWindow",
                code: 0,
            });
        }

        // Safety: hwnd was validated with IsWindow and GWL_STYLE targets the window's normal style.
        let current = unsafe { GetWindowLongPtrW(hwnd, GWL_STYLE) } as u32;
        let next = super::merge_window_style(current, desired_style);
        if next == current {
            return Ok(());
        }

        // Safety: SetLastError resets the current thread's Win32 error code before SetWindowLongPtrW.
        unsafe { SetLastError(ERROR_SUCCESS) };
        // Safety: hwnd was validated and next preserves every style bit not managed by win_fluent.
        let previous = unsafe { SetWindowLongPtrW(hwnd, GWL_STYLE, next as isize) };
        // Safety: GetLastError reads the current thread's Win32 error code.
        let error = unsafe { GetLastError() };
        if previous == 0 && error != ERROR_SUCCESS {
            return Err(WindowsPlatformError::NativeCallFailed {
                operation: "SetWindowLongPtrW",
                code: error,
            });
        }

        // Notify Windows only when the managed style actually changed. Reapplying
        // SWP_FRAMECHANGED after the window is visible briefly repaints legacy
        // non-client chrome on undecorated windows.
        let flags = SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_NOZORDER;
        if unsafe { SetWindowPos(hwnd, null_mut(), 0, 0, 0, 0, flags) } == 0 {
            return Err(last_error("SetWindowPos"));
        }

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
        if next == current {
            return Ok(());
        }

        {
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

    #[cfg(feature = "legacy-powershell-tts")]
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

    #[cfg(not(feature = "legacy-powershell-tts"))]
    pub fn speak_text(_text: &str, _language: Option<&str>) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
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

    #[cfg(debug_assertions)]
    pub fn current_gpu_memory() -> Result<super::WindowsGpuMemory, WindowsPlatformError> {
        use super::WindowsGpuMemory;
        use windows::core::Interface;
        use windows::Win32::Graphics::Dxgi::{
            CreateDXGIFactory1, IDXGIAdapter3, IDXGIFactory4, DXGI_MEMORY_SEGMENT_GROUP_LOCAL,
            DXGI_QUERY_VIDEO_MEMORY_INFO,
        };

        fn dxgi_err(operation: &'static str, error: windows::core::Error) -> WindowsPlatformError {
            WindowsPlatformError::NativeCallFailed {
                operation,
                code: error.code().0 as u32,
            }
        }

        // Safety: DXGI factory/adapter creation and QueryVideoMemoryInfo only read
        // adapter state for the current process; no handles outlive this scope.
        unsafe {
            let factory: IDXGIFactory4 =
                CreateDXGIFactory1().map_err(|error| dxgi_err("CreateDXGIFactory1", error))?;
            // Adapter 0 is the primary adapter the swapchain renders on.
            let adapter = factory
                .EnumAdapters(0)
                .map_err(|error| dxgi_err("IDXGIFactory::EnumAdapters", error))?;
            let adapter3: IDXGIAdapter3 = adapter
                .cast()
                .map_err(|error| dxgi_err("IDXGIAdapter3::cast", error))?;
            let mut info = DXGI_QUERY_VIDEO_MEMORY_INFO::default();
            adapter3
                .QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &mut info)
                .map_err(|error| dxgi_err("IDXGIAdapter3::QueryVideoMemoryInfo", error))?;
            Ok(WindowsGpuMemory {
                current_usage_bytes: info.CurrentUsage,
                budget_bytes: info.Budget,
            })
        }
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

    unsafe extern "system" fn tray_window_proc(
        hwnd: HWND,
        message: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        // `Shell_NotifyIcon` delivers its callback to this procedure via
        // `SendMessage`, so it must be handled here (it never surfaces through the
        // poll loop's `PeekMessage` return). Record it for the poll thread to act
        // on; the message id is otherwise meaningless to `DefWindowProc`.
        if record_tray_callback(hwnd, message, lparam) {
            return 0;
        }

        // Owner-drawn tray menu: size and paint each item for a fluent WinUI look.
        if message == WM_MEASUREITEM {
            // Safety: for an owner-drawn menu, lParam is a valid MEASUREITEMSTRUCT and
            // itemData is the TrayOwnerDrawItem pointer set in insert_owner_draw_item.
            let measure = unsafe { &mut *(lparam as *mut MEASUREITEMSTRUCT) };
            if measure.itemData != 0 {
                let item = unsafe { &*(measure.itemData as *const TrayOwnerDrawItem) };
                let (width, height) = measure_tray_owner_draw_item(item);
                measure.itemWidth = width;
                measure.itemHeight = height;
                return 1;
            }
        }

        if message == WM_DRAWITEM {
            // Safety: for an owner-drawn menu, lParam is a valid DRAWITEMSTRUCT and
            // itemData is the TrayOwnerDrawItem pointer set in insert_owner_draw_item.
            let draw = unsafe { &*(lparam as *const DRAWITEMSTRUCT) };
            if draw.itemData != 0 {
                let item = unsafe { &*(draw.itemData as *const TrayOwnerDrawItem) };
                draw_tray_owner_draw_item(draw, item);
                return 1;
            }
        }

        if message == WM_MENUSELECT {
            handle_tray_menu_select(hwnd, wparam, lparam);
            return 0;
        }

        // Safety: This hidden window exists only to receive shell callback messages. All
        // unhandled messages use the default Win32 procedure.
        unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
    }

    fn create_tray_window() -> Result<HWND, WindowsPlatformError> {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or_default();
        let class_name = wide_null(&format!(
            "WinFluentTrayHost-{}-{:?}-{nonce}",
            std::process::id(),
            std::thread::current().id()
        ));
        // Safety: null module name asks for the current module handle.
        let hinstance = unsafe { GetModuleHandleW(null()) };
        if hinstance.is_null() {
            return Err(last_error("GetModuleHandleW"));
        }

        let window_class = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(tray_window_proc),
            hInstance: hinstance,
            lpszClassName: class_name.as_ptr(),
            ..Default::default()
        };

        // Safety: WNDCLASSW points to a valid, null-terminated class name.
        if unsafe { RegisterClassW(&window_class) } == 0 {
            return Err(last_error("RegisterClassW"));
        }

        // A *normal* (top-level, never-shown) window — NOT a message-only
        // (`HWND_MESSAGE`) window. The Windows notification area does not reliably
        // deliver `Shell_NotifyIcon` mouse callbacks (`WM_LBUTTONUP`,
        // `WM_CONTEXTMENU`, `NIN_SELECT`, …) to message-only windows: the icon
        // appears but every click is dropped, so neither left-click activation nor
        // the right-click menu fires. A hidden top-level window with
        // `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` is invisible (we never call
        // `ShowWindow`), stays out of the taskbar / Alt-Tab, and receives the shell
        // callbacks correctly — the same pattern H.NotifyIcon (the .NET build) and
        // every other production tray host use.
        //
        // Safety: class_name is registered above; a null parent makes this a
        // top-level window owned by this thread.
        let hwnd = unsafe {
            CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                class_name.as_ptr(),
                class_name.as_ptr(),
                WS_POPUP,
                CW_USEDEFAULT,
                0,
                CW_USEDEFAULT,
                0,
                null_mut(),
                null_mut(),
                hinstance,
                null_mut(),
            )
        };

        if hwnd.is_null() {
            return Err(last_error("CreateWindowExW"));
        }

        Ok(hwnd)
    }

    fn tray_icon_data(hwnd: HWND, icon_id: u32, callback_message: u32) -> NOTIFYICONDATAW {
        NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: hwnd,
            uID: icon_id,
            uCallbackMessage: callback_message,
            ..Default::default()
        }
    }

    fn copy_wide_truncated(destination: &mut [u16], value: &str) {
        if destination.is_empty() {
            return;
        }

        for (index, unit) in value.encode_utf16().take(destination.len() - 1).enumerate() {
            destination[index] = unit;
        }
    }

    fn tray_menu_tooltip_states() -> &'static Mutex<BTreeMap<isize, TrayMenuTooltipState>> {
        TRAY_MENU_TOOLTIP_STATES.get_or_init(|| Mutex::new(BTreeMap::new()))
    }

    fn install_tray_menu_tooltip_state(hwnd: HWND, state: TrayMenuTooltipState) {
        if state.command_tooltips.is_empty() && state.submenu_tooltips.is_empty() {
            return;
        }

        let mut states = tray_menu_tooltip_states()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if let Some(mut old_state) = states.insert(hwnd as isize, state) {
            destroy_tray_menu_tooltip(&mut old_state);
        }
    }

    fn clear_tray_menu_tooltip_state(hwnd: HWND) {
        let mut state = {
            let mut states = tray_menu_tooltip_states()
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            states.remove(&(hwnd as isize))
        };
        if let Some(state) = state.as_mut() {
            destroy_tray_menu_tooltip(state);
        }
    }

    fn handle_tray_menu_select(hwnd: HWND, wparam: WPARAM, lparam: LPARAM) {
        let selected_item = low_word(wparam);
        let flags = high_word(wparam);
        let menu = lparam;
        let mut states = tray_menu_tooltip_states()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let Some(state) = states.get_mut(&(hwnd as isize)) else {
            return;
        };

        if tray_menu_select_is_closed(flags, menu) {
            hide_tray_menu_tooltip(state);
            return;
        }

        let tooltip =
            tray_menu_selected_tooltip(state, selected_item, flags, menu).map(str::to_string);
        if let Some(tooltip) = tooltip {
            show_tray_menu_tooltip(state, &tooltip);
        } else {
            hide_tray_menu_tooltip(state);
        }
    }

    fn low_word(value: WPARAM) -> u16 {
        (value & 0xffff) as u16
    }

    fn high_word(value: WPARAM) -> u16 {
        ((value >> 16) & 0xffff) as u16
    }

    pub(super) fn tray_menu_select_is_closed(flags: u16, menu: isize) -> bool {
        flags == 0xffff && menu == 0
    }

    pub(super) fn tray_menu_selected_tooltip(
        state: &TrayMenuTooltipState,
        selected_item: u16,
        flags: u16,
        menu: isize,
    ) -> Option<&str> {
        if tray_menu_select_is_closed(flags, menu)
            || menu_select_has_flag(flags, MF_SEPARATOR)
            || menu_select_has_flag(flags, MF_SYSMENU)
        {
            return None;
        }

        if menu_select_has_flag(flags, MF_POPUP) {
            return state
                .submenu_tooltips
                .get(&(menu, u32::from(selected_item)))
                .map(String::as_str);
        }

        state
            .command_tooltips
            .get(&u32::from(selected_item))
            .map(String::as_str)
    }

    fn menu_select_has_flag(flags: u16, flag: u32) -> bool {
        u32::from(flags) & flag != 0
    }

    fn tray_item_tooltip_text(item: &super::WindowsTrayItemPlan) -> Option<String> {
        item.tooltip
            .as_deref()
            .map(str::trim)
            .filter(|tooltip| !tooltip.is_empty())
            .map(ToOwned::to_owned)
    }

    fn show_tray_menu_tooltip(state: &mut TrayMenuTooltipState, text: &str) {
        let hwnd = match tray_menu_tooltip_window(state) {
            Some(hwnd) => hwnd,
            None => return,
        };
        let wide_text = wide_null(text);
        // Safety: hwnd is the tooltip window owned by this state and wide_text is null-terminated.
        let _ = unsafe { SetWindowTextW(hwnd, wide_text.as_ptr()) };

        let mut cursor = POINT { x: 0, y: 0 };
        // Safety: cursor is a valid out pointer.
        if unsafe { GetCursorPos(&mut cursor) } == 0 {
            return;
        }

        let (width, height) = tray_menu_tooltip_size(text);
        let x = clamp_to_virtual_screen(
            cursor.x + 18,
            width,
            // Safety: GetSystemMetrics has no pointer preconditions.
            unsafe { GetSystemMetrics(SM_XVIRTUALSCREEN) },
            unsafe { GetSystemMetrics(SM_CXVIRTUALSCREEN) },
        );
        let y = clamp_to_virtual_screen(
            cursor.y + 22,
            height,
            unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) },
            unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) },
        );

        // Safety: hwnd is a valid top-level static window; SetWindowPos shows it without activation.
        let _ = unsafe {
            SetWindowPos(
                hwnd,
                HWND_TOPMOST,
                x,
                y,
                width,
                height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW,
            )
        };
        state.visible_text = Some(text.to_string());
    }

    fn hide_tray_menu_tooltip(state: &mut TrayMenuTooltipState) {
        state.visible_text = None;
        let hwnd = state.tooltip_hwnd as HWND;
        if !hwnd.is_null() {
            // Safety: hwnd is the tooltip window owned by this state.
            let _ = unsafe { ShowWindow(hwnd, SW_HIDE) };
        }
    }

    fn destroy_tray_menu_tooltip(state: &mut TrayMenuTooltipState) {
        let hwnd = state.tooltip_hwnd as HWND;
        if !hwnd.is_null() {
            // Safety: hwnd is the tooltip window owned by this state.
            let _ = unsafe { DestroyWindow(hwnd) };
        }
        state.tooltip_hwnd = 0;
        state.visible_text = None;
    }

    fn tray_menu_tooltip_window(state: &mut TrayMenuTooltipState) -> Option<HWND> {
        let existing = state.tooltip_hwnd as HWND;
        if !existing.is_null() {
            return Some(existing);
        }

        let class_name = wide_null("STATIC");
        let title = wide_null("");
        // Safety: STATIC is a predefined window class. The popup is no-activate and owned by
        // this transient menu tooltip state, then destroyed when TrackPopupMenu returns.
        let hwnd = unsafe {
            CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                class_name.as_ptr(),
                title.as_ptr(),
                WS_POPUP | WS_BORDER,
                CW_USEDEFAULT,
                0,
                CW_USEDEFAULT,
                0,
                null_mut(),
                null_mut(),
                null_mut(),
                null_mut(),
            )
        };
        if hwnd.is_null() {
            None
        } else {
            state.tooltip_hwnd = hwnd as isize;
            Some(hwnd)
        }
    }

    fn tray_menu_tooltip_size(text: &str) -> (i32, i32) {
        let line_count = text.lines().count().max(1);
        let max_columns = text
            .lines()
            .map(|line| line.chars().count())
            .max()
            .unwrap_or_else(|| text.chars().count())
            .max(1);
        let width = ((max_columns as i32 * 7) + 28).clamp(96, 420);
        let height = ((line_count as i32 * 18) + 12).clamp(30, 160);
        (width, height)
    }

    fn clamp_to_virtual_screen(
        origin: i32,
        size: i32,
        screen_start: i32,
        screen_extent: i32,
    ) -> i32 {
        let max = screen_start + screen_extent - size;
        if max < screen_start {
            screen_start
        } else {
            origin.clamp(screen_start, max)
        }
    }

    fn show_tray_menu(
        handle: &TrayHandle,
    ) -> Result<Option<NativeTrayMessage>, WindowsPlatformError> {
        // Safety: CreatePopupMenu has no preconditions and returns an owned HMENU on success.
        let menu = unsafe { CreatePopupMenu() };
        if menu.is_null() {
            return Err(last_error("CreatePopupMenu"));
        }

        let mut tooltip_state = TrayMenuTooltipState::default();
        // Backing store for owner-drawn item state. Each menu item's `dwItemData`
        // points into this arena; it must outlive `TrackPopupMenu` (the window
        // procedure reads it during WM_MEASUREITEM/WM_DRAWITEM), so it is dropped
        // only after the menu is torn down at the end of this function.
        let mut owner_draw_items: Vec<Box<TrayOwnerDrawItem>> = Vec::new();
        if let Err(error) = append_tray_menu_items(
            menu,
            &handle.menu_items,
            handle.presenter_min_width,
            handle.presenter_style,
            &mut tooltip_state,
            &mut owner_draw_items,
        ) {
            // Safety: menu is owned by this function.
            let _ = unsafe { DestroyMenu(menu) };
            return Err(error);
        }
        let menu_background = tray_menu_background_color(handle.presenter_style);
        let menu_background_brush = unsafe { CreateSolidBrush(menu_background) };
        if menu_background_brush.is_null() {
            let _ = unsafe { DestroyMenu(menu) };
            return Err(last_error("CreateSolidBrush"));
        }
        if let Err(error) =
            apply_tray_menu_presenter_info(menu, handle.presenter_style, menu_background_brush)
        {
            unsafe { DeleteObject(menu_background_brush as HGDIOBJ) };
            let _ = unsafe { DestroyMenu(menu) };
            return Err(error);
        }

        let mut cursor = POINT { x: 0, y: 0 };
        // Safety: cursor is a valid out pointer.
        if unsafe { GetCursorPos(&mut cursor) } == 0 {
            unsafe { DeleteObject(menu_background_brush as HGDIOBJ) };
            // Safety: menu is owned by this function.
            let _ = unsafe { DestroyMenu(menu) };
            return Err(last_error("GetCursorPos"));
        }

        // Safety: hidden HWND is valid while handle is alive. Foregrounding ensures the menu
        // dismisses when the user clicks elsewhere, per notification area menu guidance.
        let _ = unsafe { SetForegroundWindow(handle.hwnd) };
        schedule_tray_presenter_corner_radius(cursor, handle.presenter_style);
        install_tray_menu_tooltip_state(handle.hwnd, tooltip_state);
        // Safety: menu and HWND are valid; TPM_RETURNCMD returns the chosen command id.
        let popup_flags = tray_menu_popup_flags(handle.presenter_style.popup_animation, cursor);
        let command = unsafe {
            TrackPopupMenuEx(
                menu,
                TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | popup_flags,
                cursor.x,
                cursor.y,
                handle.hwnd,
                null(),
            )
        };
        // Safety: benign message recommended by Shell docs after notification-area popup menus.
        let _ = unsafe { PostMessageW(handle.hwnd, WM_NULL, 0, 0) };
        clear_tray_menu_tooltip_state(handle.hwnd);
        // Safety: menu is owned by this function.
        let _ = unsafe { DestroyMenu(menu) };
        unsafe { DeleteObject(menu_background_brush as HGDIOBJ) };

        if command == 0 {
            Ok(None)
        } else {
            Ok(Some(NativeTrayMessage::Command {
                command_id: command as u32,
            }))
        }
    }

    /// Backing state for an owner-drawn tray menu item. A boxed instance is
    /// referenced by the item's `dwItemData` and read back during
    /// WM_MEASUREITEM / WM_DRAWITEM. Owned by the arena in `show_tray_menu`,
    /// which outlives `TrackPopupMenu`.
    struct TrayOwnerDrawItem {
        /// NUL-terminated UTF-16 label.
        text: Vec<u16>,
        /// Label length in UTF-16 code units, excluding the trailing NUL.
        text_len: i32,
        /// Draw a trailing chevron for submenu items.
        is_submenu: bool,
        /// Draw a WinUI-style separator instead of text.
        is_separator: bool,
        /// Minimum item width in DIP/pixels (the menu's `presenter_min_width`),
        /// applied as a floor so the whole menu reaches the requested width.
        min_width: i32,
        style: TrayMenuPresenterStyle,
    }

    fn append_tray_menu_items(
        menu: windows_sys::Win32::UI::WindowsAndMessaging::HMENU,
        items: &[super::WindowsTrayItemPlan],
        presenter_min_width: Option<u16>,
        presenter_style: TrayMenuPresenterStyle,
        tooltip_state: &mut TrayMenuTooltipState,
        arena: &mut Vec<Box<TrayOwnerDrawItem>>,
    ) -> Result<(), WindowsPlatformError> {
        for (position, item) in items.iter().enumerate() {
            match item.kind {
                super::WindowsTrayItemKind::Separator => {
                    insert_owner_draw_separator(
                        menu,
                        position as u32,
                        presenter_min_width,
                        presenter_style,
                        arena,
                    )?;
                }
                super::WindowsTrayItemKind::Submenu => {
                    // Safety: CreatePopupMenu has no preconditions and returns an owned HMENU.
                    let submenu = unsafe { CreatePopupMenu() };
                    if submenu.is_null() {
                        return Err(last_error("CreatePopupMenu"));
                    }
                    if let Err(error) = append_tray_menu_items(
                        submenu,
                        &item.children,
                        presenter_min_width,
                        presenter_style,
                        tooltip_state,
                        arena,
                    ) {
                        // Safety: submenu is owned by this branch until InsertMenuItemW succeeds.
                        let _ = unsafe { DestroyMenu(submenu) };
                        return Err(error);
                    }
                    if let Err(error) = insert_owner_draw_item(
                        menu,
                        position as u32,
                        item,
                        presenter_min_width,
                        presenter_style,
                        Some(submenu),
                        arena,
                    ) {
                        // Safety: submenu has not been transferred on failure.
                        let _ = unsafe { DestroyMenu(submenu) };
                        return Err(error);
                    }
                    if let Some(tooltip) = tray_item_tooltip_text(item) {
                        tooltip_state
                            .submenu_tooltips
                            .insert((menu as isize, position as u32), tooltip);
                    }
                }
                super::WindowsTrayItemKind::Command => {
                    insert_owner_draw_item(
                        menu,
                        position as u32,
                        item,
                        presenter_min_width,
                        presenter_style,
                        None,
                        arena,
                    )?;
                    if let Some(tooltip) = tray_item_tooltip_text(item) {
                        tooltip_state
                            .command_tooltips
                            .insert(item.command_id, tooltip);
                    }
                }
            }
        }

        Ok(())
    }

    /// Inserts a single owner-drawn command or submenu item, allocating its
    /// [`TrayOwnerDrawItem`] backing state in `arena`.
    fn insert_owner_draw_item(
        menu: windows_sys::Win32::UI::WindowsAndMessaging::HMENU,
        position: u32,
        item: &super::WindowsTrayItemPlan,
        presenter_min_width: Option<u16>,
        presenter_style: TrayMenuPresenterStyle,
        submenu: Option<windows_sys::Win32::UI::WindowsAndMessaging::HMENU>,
        arena: &mut Vec<Box<TrayOwnerDrawItem>>,
    ) -> Result<(), WindowsPlatformError> {
        let text = wide_null(&item.label);
        let text_len = text.len().saturating_sub(1) as i32;
        let draw = Box::new(TrayOwnerDrawItem {
            text,
            text_len,
            is_submenu: submenu.is_some(),
            is_separator: false,
            min_width: presenter_min_width.map(i32::from).unwrap_or(0),
            style: presenter_style,
        });
        // The boxed state lives on the heap; moving the Box into the arena does
        // not move it, so this pointer stays valid for the menu's lifetime.
        let data_ptr = draw.as_ref() as *const TrayOwnerDrawItem as usize;
        arena.push(draw);

        // Safety: a zeroed MENUITEMINFOW with cbSize set is the documented way to
        // populate the struct; only the masked fields are read.
        let mut info: MENUITEMINFOW = unsafe { std::mem::zeroed() };
        info.cbSize = std::mem::size_of::<MENUITEMINFOW>() as u32;
        info.fMask = MIIM_FTYPE
            | MIIM_STATE
            | MIIM_DATA
            | if submenu.is_some() {
                MIIM_SUBMENU
            } else {
                MIIM_ID
            };
        info.fType = MFT_OWNERDRAW;
        info.fState = if item.enabled {
            MFS_ENABLED
        } else {
            MFS_GRAYED
        };
        if let Some(submenu) = submenu {
            info.hSubMenu = submenu;
        } else {
            info.wID = item.command_id;
        }
        info.dwItemData = data_ptr;

        // Safety: info is fully initialized; the boxed state outlives the menu.
        if unsafe { InsertMenuItemW(menu, position, 1, &info) } == 0 {
            return Err(last_error("InsertMenuItemW"));
        }
        Ok(())
    }

    fn insert_owner_draw_separator(
        menu: windows_sys::Win32::UI::WindowsAndMessaging::HMENU,
        position: u32,
        presenter_min_width: Option<u16>,
        presenter_style: TrayMenuPresenterStyle,
        arena: &mut Vec<Box<TrayOwnerDrawItem>>,
    ) -> Result<(), WindowsPlatformError> {
        let draw = Box::new(TrayOwnerDrawItem {
            text: wide_null(""),
            text_len: 0,
            is_submenu: false,
            is_separator: true,
            min_width: presenter_min_width.map(i32::from).unwrap_or(0),
            style: presenter_style,
        });
        let data_ptr = draw.as_ref() as *const TrayOwnerDrawItem as usize;
        arena.push(draw);

        let mut info: MENUITEMINFOW = unsafe { std::mem::zeroed() };
        info.cbSize = std::mem::size_of::<MENUITEMINFOW>() as u32;
        info.fMask = MIIM_FTYPE | MIIM_STATE | MIIM_DATA;
        info.fType = MFT_OWNERDRAW | MFT_SEPARATOR;
        info.fState = MFS_GRAYED;
        info.dwItemData = data_ptr;

        if unsafe { InsertMenuItemW(menu, position, 1, &info) } == 0 {
            return Err(last_error("InsertMenuItemW"));
        }
        Ok(())
    }

    fn apply_tray_menu_presenter_info(
        menu: windows_sys::Win32::UI::WindowsAndMessaging::HMENU,
        style: TrayMenuPresenterStyle,
        background: HBRUSH,
    ) -> Result<(), WindowsPlatformError> {
        let mut info: MENUINFO = unsafe { std::mem::zeroed() };
        info.cbSize = std::mem::size_of::<MENUINFO>() as u32;
        info.fMask = MIM_STYLE | MIM_BACKGROUND | MIM_APPLYTOSUBMENUS;
        info.dwStyle = MNS_AUTODISMISS | MNS_NOCHECK;
        info.hbrBack = background;

        if let Some(max_height) = style.presenter_max_height {
            info.fMask |= MIM_MAXHEIGHT;
            let height = tray_menu_dip(max_height, tray_menu_screen_scale());
            info.cyMax = height.max(tray_menu_dip(style.item_min_height, 1.0)) as u32;
        }

        if unsafe { SetMenuInfo(menu, &info) } == 0 {
            return Err(last_error("SetMenuInfo"));
        }

        Ok(())
    }

    /// Creates an `HFONT` for the current system menu font, or null on failure
    /// (callers skip font selection when null). The caller owns and deletes it.
    fn tray_menu_font(style: TrayMenuPresenterStyle, scale: f32) -> HFONT {
        let mut metrics: NONCLIENTMETRICSW = unsafe { std::mem::zeroed() };
        metrics.cbSize = std::mem::size_of::<NONCLIENTMETRICSW>() as u32;
        // Safety: pvParam points to a NONCLIENTMETRICSW whose cbSize is set.
        let ok = unsafe {
            SystemParametersInfoW(
                SPI_GETNONCLIENTMETRICS,
                metrics.cbSize,
                (&mut metrics as *mut NONCLIENTMETRICSW).cast(),
                0,
            )
        };
        if ok == 0 {
            return null_mut();
        }
        if style.item_font_size > 0 {
            metrics.lfMenuFont.lfHeight = -tray_menu_dip(style.item_font_size, scale);
            metrics.lfMenuFont.lfWidth = 0;
        }
        // Safety: lfMenuFont is a valid LOGFONTW populated by the call above.
        unsafe { CreateFontIndirectW(&metrics.lfMenuFont as *const LOGFONTW) }
    }

    /// Blends two `COLORREF`s (`0x00BBGGRR`) by `t` in `0.0..=1.0`.
    pub(super) fn blend_color(from: u32, to: u32, t: f32) -> u32 {
        let channel = |shift: u32| {
            let a = ((from >> shift) & 0xFF) as f32;
            let b = ((to >> shift) & 0xFF) as f32;
            (a + (b - a) * t).round().clamp(0.0, 255.0) as u32
        };
        channel(0) | (channel(8) << 8) | (channel(16) << 16)
    }

    fn measure_tray_owner_draw_item(item: &TrayOwnerDrawItem) -> (u32, u32) {
        // Safety: GetDC(null) returns a screen DC; released below.
        let hdc = unsafe { GetDC(null_mut()) };
        let scale = tray_menu_scale_from_hdc(hdc);

        if item.is_separator {
            // Safety: release the screen DC obtained above.
            unsafe { ReleaseDC(null_mut(), hdc) };
            let min_width = (item.min_width as f32 * scale).round() as i32;
            return (
                min_width.max(1) as u32,
                tray_menu_dip(item.style.separator_height, scale).max(1) as u32,
            );
        }

        let font = tray_menu_font(item.style, scale);
        let old_font = if font.is_null() {
            null_mut()
        } else {
            // Safety: hdc and font are valid; previous object restored below.
            unsafe { SelectObject(hdc, font as HGDIOBJ) }
        };
        let mut size = SIZE { cx: 0, cy: 0 };
        // Safety: text is a valid UTF-16 buffer of text_len units.
        unsafe { GetTextExtentPoint32W(hdc, item.text.as_ptr(), item.text_len, &mut size) };
        if !font.is_null() {
            // Safety: restore and delete the font we created.
            unsafe { SelectObject(hdc, old_font) };
            unsafe { DeleteObject(font as HGDIOBJ) };
        }
        // Safety: release the screen DC obtained above.
        unsafe { ReleaseDC(null_mut(), hdc) };

        let left_padding = tray_menu_dip(item.style.item_horizontal_padding, scale);
        let right_padding = tray_menu_dip(item.style.item_horizontal_padding, scale);
        let chevron = if item.is_submenu {
            tray_menu_dip(item.style.submenu_arrow_column_width, scale)
        } else {
            0
        };
        let min_width = (item.min_width as f32 * scale).round() as i32;
        let width = (size.cx + left_padding + right_padding + chevron)
            .max(min_width)
            .max(1) as u32;
        let height = (size.cy + tray_menu_dip(item.style.item_vertical_padding, scale))
            .max(tray_menu_dip(item.style.item_min_height, scale))
            .max(1) as u32;
        (width, height)
    }

    fn draw_tray_owner_draw_item(draw: &DRAWITEMSTRUCT, item: &TrayOwnerDrawItem) {
        let hdc = draw.hDC;
        let rect = draw.rcItem;
        let scale = tray_menu_scale_from_hdc(hdc);
        let selected = (draw.itemState & ODS_SELECTED) != 0;
        let disabled = (draw.itemState & (ODS_GRAYED | ODS_DISABLED)) != 0;

        let menu_bg = tray_menu_background_color(item.style);
        let text_color = if disabled {
            unsafe { GetSysColor(COLOR_GRAYTEXT) }
        } else {
            tray_menu_text_color(menu_bg, item.style)
        };

        // Base background.
        // Safety: brush and rect are valid; brush deleted right after.
        let bg_brush = unsafe { CreateSolidBrush(menu_bg) };
        unsafe { FillRect(hdc, &rect, bg_brush) };
        unsafe { DeleteObject(bg_brush as HGDIOBJ) };

        if item.is_separator {
            let thickness = tray_menu_dip(item.style.separator_line_thickness, scale);
            let line_y = rect.top + (((rect.bottom - rect.top) - thickness) / 2).max(0);
            let inset = tray_menu_dip(item.style.separator_horizontal_inset, scale);
            let line_rect = RECT {
                left: rect.left + inset,
                top: line_y,
                right: rect.right - inset,
                bottom: line_y + thickness,
            };
            let line_brush =
                unsafe { CreateSolidBrush(tray_menu_separator_color(menu_bg, item.style)) };
            unsafe { FillRect(hdc, &line_rect, line_brush) };
            unsafe { DeleteObject(line_brush as HGDIOBJ) };
            return;
        }

        // Fluent hover: subtle, inset, rounded highlight (theme-aware tint).
        if selected && !disabled {
            let hover = blend_color(
                menu_bg,
                text_color,
                f32::from(item.style.hover_foreground_mix_percent) / 100.0,
            );
            let inset_x = tray_menu_dip(item.style.hover_inset_x, scale);
            let inset_y = tray_menu_dip(item.style.hover_inset_y, scale);
            let radius = tray_menu_dip(item.style.item_corner_radius, scale);
            // Safety: brush/region are valid and freed below.
            let brush = unsafe { CreateSolidBrush(hover) };
            let region = unsafe {
                CreateRoundRectRgn(
                    rect.left + inset_x,
                    rect.top + inset_y,
                    rect.right - inset_x,
                    rect.bottom - inset_y,
                    radius,
                    radius,
                )
            };
            if !region.is_null() {
                unsafe { FillRgn(hdc, region, brush) };
                unsafe { DeleteObject(region as HGDIOBJ) };
            }
            unsafe { DeleteObject(brush as HGDIOBJ) };
        }

        let font = tray_menu_font(item.style, scale);
        let old_font = if font.is_null() {
            null_mut()
        } else {
            // Safety: hdc and font are valid; restored below.
            unsafe { SelectObject(hdc, font as HGDIOBJ) }
        };
        // Safety: standard text-rendering setup on a valid DC.
        unsafe { SetBkMode(hdc, TRANSPARENT as i32) };
        unsafe { SetTextColor(hdc, text_color) };

        let mut text_rect = rect;
        text_rect.left += tray_menu_dip(item.style.item_horizontal_padding, scale);
        text_rect.right -= if item.is_submenu {
            tray_menu_dip(
                item.style.item_horizontal_padding + item.style.submenu_arrow_column_width,
                scale,
            )
        } else {
            tray_menu_dip(item.style.item_horizontal_padding, scale)
        };
        // Safety: text buffer and rect are valid for the duration of the call.
        unsafe {
            DrawTextW(
                hdc,
                item.text.as_ptr(),
                item.text_len,
                &mut text_rect,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE,
            )
        };

        if !font.is_null() {
            // Safety: restore and delete the font created above.
            unsafe { SelectObject(hdc, old_font) };
            unsafe { DeleteObject(font as HGDIOBJ) };
        }
    }

    fn tray_menu_background_color(style: TrayMenuPresenterStyle) -> u32 {
        let system_bg = unsafe { GetSysColor(COLOR_MENU) };
        if tray_color_luma(system_bg) < 128 {
            tray_menu_color(style.dark_surface)
        } else {
            tray_menu_color(style.light_surface)
        }
    }

    fn tray_menu_text_color(background: u32, style: TrayMenuPresenterStyle) -> u32 {
        if tray_color_luma(background) < 128 {
            tray_menu_color(style.dark_foreground)
        } else {
            tray_menu_color(style.light_foreground)
        }
    }

    fn tray_menu_separator_color(background: u32, style: TrayMenuPresenterStyle) -> u32 {
        if tray_color_luma(background) < 128 {
            tray_menu_color(style.dark_separator)
        } else {
            tray_menu_color(style.light_separator)
        }
    }

    fn tray_menu_color(color: TrayMenuColor) -> u32 {
        match color {
            TrayMenuColor::SystemMenu => unsafe { GetSysColor(COLOR_MENU) },
            TrayMenuColor::Rgb(red, green, blue) => {
                tray_rgb(u32::from(red), u32::from(green), u32::from(blue))
            }
        }
    }

    fn tray_color_luma(color: u32) -> u32 {
        let red = color & 0xff;
        let green = (color >> 8) & 0xff;
        let blue = (color >> 16) & 0xff;
        ((red * 299) + (green * 587) + (blue * 114)) / 1000
    }

    fn tray_rgb(red: u32, green: u32, blue: u32) -> u32 {
        (red & 0xff) | ((green & 0xff) << 8) | ((blue & 0xff) << 16)
    }

    fn tray_menu_scale_from_hdc(hdc: HDC) -> f32 {
        let dpi = if hdc.is_null() {
            96
        } else {
            unsafe { GetDeviceCaps(hdc, LOGPIXELSX as i32) }
        };
        if dpi <= 0 {
            1.0
        } else {
            dpi as f32 / 96.0
        }
    }

    fn tray_menu_screen_scale() -> f32 {
        let hdc = unsafe { GetDC(null_mut()) };
        let scale = tray_menu_scale_from_hdc(hdc);
        if !hdc.is_null() {
            unsafe { ReleaseDC(null_mut(), hdc) };
        }
        scale
    }

    fn tray_menu_dip(value: u16, scale: f32) -> i32 {
        ((value as f32) * scale).round().max(1.0) as i32
    }

    pub(super) fn tray_menu_popup_animation_flags(
        animation: TrayMenuPopupAnimation,
        opens_upward: bool,
    ) -> u32 {
        match animation {
            TrayMenuPopupAnimation::System => 0,
            TrayMenuPopupAnimation::None => TPM_NOANIMATION,
            TrayMenuPopupAnimation::Vertical if opens_upward => TPM_VERNEGANIMATION,
            TrayMenuPopupAnimation::Vertical => TPM_VERPOSANIMATION,
        }
    }

    fn tray_menu_popup_flags(animation: TrayMenuPopupAnimation, cursor: POINT) -> u32 {
        let screen_y = unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) };
        let screen_height = unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) };
        let opens_upward = screen_height > 0 && cursor.y > screen_y + (screen_height / 2);
        tray_menu_popup_animation_flags(animation, opens_upward)
    }

    fn schedule_tray_presenter_corner_radius(cursor: POINT, style: TrayMenuPresenterStyle) {
        std::thread::spawn(move || {
            for _ in 0..30 {
                let hwnd = find_tray_menu_popup_window(cursor);
                if !hwnd.is_null() {
                    apply_tray_presenter_corner_radius(hwnd, style);
                    return;
                }
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
        });
    }

    fn find_tray_menu_popup_window(cursor: POINT) -> HWND {
        let mut search = TrayPopupCornerSearch {
            cursor,
            hwnd: null_mut(),
        };
        let _ = unsafe {
            EnumWindows(
                Some(enum_tray_menu_popup_corner_proc),
                &mut search as *mut TrayPopupCornerSearch as LPARAM,
            )
        };
        search.hwnd
    }

    struct TrayPopupCornerSearch {
        cursor: POINT,
        hwnd: HWND,
    }

    unsafe extern "system" fn enum_tray_menu_popup_corner_proc(hwnd: HWND, lparam: LPARAM) -> i32 {
        if unsafe { IsWindowVisible(hwnd) } == 0 {
            return 1;
        }

        let mut class_name = [0u16; 32];
        let len = unsafe { GetClassNameW(hwnd, class_name.as_mut_ptr(), class_name.len() as i32) };
        if len <= 0 {
            return 1;
        }
        let class_name = String::from_utf16_lossy(&class_name[..len as usize]);
        if class_name != "#32768" {
            return 1;
        }

        let mut rect = RECT::default();
        if unsafe { GetWindowRect(hwnd, &mut rect) } == 0 {
            return 1;
        }

        let search = unsafe { &mut *(lparam as *mut TrayPopupCornerSearch) };
        if !point_is_near_rect(search.cursor, rect, 12) {
            return 1;
        }

        search.hwnd = hwnd;
        0
    }

    fn point_is_near_rect(point: POINT, rect: RECT, margin: i32) -> bool {
        point.x >= rect.left - margin
            && point.x <= rect.right + margin
            && point.y >= rect.top - margin
            && point.y <= rect.bottom + margin
    }

    fn apply_tray_presenter_corner_radius(hwnd: HWND, style: TrayMenuPresenterStyle) {
        let preference = tray_corner_preference_for_radius(style.presenter_corner_radius);
        let _ = unsafe {
            DwmSetWindowAttribute(
                hwnd,
                DWMWA_WINDOW_CORNER_PREFERENCE as u32,
                (&preference as *const i32).cast(),
                std::mem::size_of_val(&preference) as u32,
            )
        };
    }

    pub(super) fn tray_corner_preference_for_radius(radius: u16) -> i32 {
        if radius == 0 {
            DWMWCP_DONOTROUND
        } else if radius <= 4 {
            DWMWCP_ROUNDSMALL
        } else {
            DWMWCP_ROUND
        }
    }

    fn first_enabled_tray_command(
        items: &[super::WindowsTrayItemPlan],
    ) -> Option<&super::WindowsTrayItemPlan> {
        items.iter().find_map(|item| match item.kind {
            super::WindowsTrayItemKind::Command if item.enabled => Some(item),
            super::WindowsTrayItemKind::Submenu if item.enabled => {
                first_enabled_tray_command(&item.children)
            }
            _ => None,
        })
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

    pub const fn ws_maximize_box() -> u32 {
        WS_MAXIMIZEBOX
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
        ClipboardFormat, NativeHotkeyMessage, NativeTrayMessage, WindowsClipboardTextSnapshot,
        WindowsDataProtectionScope, WindowsHotkey, WindowsHotkeyHandle, WindowsMonitorMetrics,
        WindowsNamedEvent, WindowsNamedEventHandle, WindowsPlatformError, WindowsPoint,
        WindowsProcessMemory, WindowsRect, WindowsTrayHandle, WindowsTrayPlan,
    };
    use win_fluent::platform::{
        ScreenCaptureRequest, ScreenCaptureResult, ScreenWindow, ScreenWindowSnapshotRequest,
    };

    #[derive(Debug)]
    pub struct NamedEventHandle;

    #[derive(Debug)]
    pub struct TrayHandle;

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

    pub fn create_tray_icon(
        _plan: &WindowsTrayPlan,
    ) -> Result<WindowsTrayHandle, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn poll_tray_message(
        _handle: &WindowsTrayHandle,
    ) -> Result<Option<NativeTrayMessage>, WindowsPlatformError> {
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

    pub fn apply_window_native_border(
        _hwnd: isize,
        _enabled: bool,
    ) -> Result<(), WindowsPlatformError> {
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

    pub fn apply_window_corner_preference(_hwnd: isize, _rounded: bool) {}
    pub fn set_window_dark_mode(_hwnd: isize, _enabled: bool) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }
    pub fn system_uses_dark_theme() -> Result<bool, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }
    pub fn show_window(_hwnd: isize, _activate: bool) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn set_window_maximized(
        _hwnd: isize,
        _maximized: bool,
    ) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn toggle_window_maximized(_hwnd: isize) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }
    pub fn set_window_geometry(
        _hwnd: isize,
        _x: i32,
        _y: i32,
        _width: i32,
        _height: i32,
    ) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn configure_resize_hit_test(
        _hwnd: isize,
        _enabled: bool,
    ) -> Result<(), WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn apply_window_style(_hwnd: isize, _style: u32) -> Result<(), WindowsPlatformError> {
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

    pub const fn ws_maximize_box() -> u32 {
        0x00010000
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
    use win_fluent::platform::{
        HotkeyKey, HotkeyModifier, TrayMenu, TrayMenuItem, TrayMenuPopupAnimation,
        TrayMenuPresenterStyle,
    };
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

    #[cfg(windows)]
    #[test]
    fn system_theme_mode_resolves_to_a_concrete_palette() {
        assert!(matches!(
            WindowsPlatformAdapter::system_theme_mode(),
            Ok(ThemeMode::Light | ThemeMode::Dark)
        ));
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
    fn mode_menu_cursor_geometry_clears_centered_trigger_above_and_below() {
        let area = WindowsRect {
            left: 0,
            top: 0,
            right: 1000,
            bottom: 800,
        };

        let above = context_menu_position_signed(500, 500, 220, 80, area, 0, -34);
        assert_eq!(above, (500, 386));
        assert!(
            above.1 + 80 <= 500 - 16,
            "the popup must clear a 32-DIP trigger centered on the cursor"
        );

        let below = context_menu_position_signed(500, 20, 220, 80, area, 0, -34);
        assert_eq!(below, (500, 54));
        assert!(
            below.1 >= 20 + 16,
            "the top-edge fallback must clear the trigger below"
        );
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
    fn hotkey_planning_deduplicates_repeated_modifiers_as_bitflags() {
        let hotkey = Hotkey::new("dedupe", HotkeyKey::Character('d'))
            .modifier(HotkeyModifier::Control)
            .modifier(HotkeyModifier::Control)
            .modifier(HotkeyModifier::Alt);

        let plan = WindowsPlatformAdapter::plan_hotkeys(&[hotkey]).expect("valid hotkey");

        assert_eq!(plan[0].modifiers, native::mod_control() | native::mod_alt());
    }

    #[test]
    fn rejects_unsupported_named_hotkey() {
        let hotkey = Hotkey::new("bad", HotkeyKey::Named("browser-refresh".to_string()));

        let err = WindowsPlatformAdapter::plan_hotkeys(&[hotkey]).unwrap_err();

        assert_eq!(
            err,
            WindowsPlatformError::UnsupportedHotkeyKey("browser-refresh".to_string())
        );
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
        assert_eq!(
            plan.style,
            native::ws_popup()
                | native::ws_thickframe()
                | native::ws_maximize_box()
                | native::ws_minimize_box()
        );
        assert!(plan.ex_style & native::ws_ex_topmost() != 0);
        assert!(plan.ex_style & native::ws_ex_toolwindow() != 0);
        assert!(plan.ex_style & native::ws_ex_noactivate() != 0);
        assert!(plan.uses_acrylic);
    }

    #[test]
    fn resize_mode_controls_thickframe_for_each_window_frame() {
        for frame in [WindowFrame::Borderless, WindowFrame::Acrylic] {
            let resizable = WindowOptions::new("resizable", "Resizable")
                .frame(frame)
                .resize_mode(WindowResizeMode::CanResize);
            assert!(window_style(&resizable) & native::ws_thickframe() != 0);
            assert!(window_style(&resizable) & native::ws_maximize_box() != 0);
            assert!(window_style(&resizable) & native::ws_minimize_box() != 0);

            let fixed = WindowOptions::new("fixed", "Fixed")
                .frame(frame)
                .resize_mode(WindowResizeMode::Fixed);
            assert_eq!(window_style(&fixed) & native::ws_thickframe(), 0);
            assert_eq!(window_style(&fixed) & native::ws_maximize_box(), 0);

            let minimizable = WindowOptions::new("min", "Min")
                .frame(frame)
                .resize_mode(WindowResizeMode::CanMinimize);
            assert_eq!(window_style(&minimizable) & native::ws_thickframe(), 0);
            assert_eq!(window_style(&minimizable) & native::ws_maximize_box(), 0);
        }

        let standard = WindowOptions::new("standard", "Standard")
            .frame(WindowFrame::Standard)
            .resize_mode(WindowResizeMode::CanResize);
        assert_eq!(window_style(&standard), native::ws_overlapped_window());
    }

    #[test]
    fn native_style_merge_preserves_unmanaged_runtime_bits() {
        const WS_VISIBLE_FOR_TEST: u32 = 0x1000_0000;
        let current = WS_VISIBLE_FOR_TEST | native::ws_popup();
        let desired = native::ws_overlapped_window();

        let merged = merge_window_style(current, desired);

        assert!(merged & WS_VISIBLE_FOR_TEST != 0);
        assert_eq!(
            merged & (native::ws_popup() | native::ws_overlapped_window()),
            desired
        );
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
    fn resolves_context_menu_to_upper_right_of_anchor_by_default() {
        let options = WindowOptions::new("tray-menu", "Tray Menu")
            .size(200.0, 120.0)
            .placement(WindowPlacement::ContextMenu { x: 320.0, y: 500.0 });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 320, y: 500 },
            WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
        );

        assert_eq!(placement.x, 320);
        assert_eq!(placement.y, 380);
        assert_eq!(placement.width, 200);
        assert_eq!(placement.height, 120);
    }

    #[test]
    fn resolves_context_menu_to_upper_left_near_right_edge() {
        let options = WindowOptions::new("tray-menu", "Tray Menu")
            .size(200.0, 120.0)
            .placement(WindowPlacement::ContextMenu {
                x: 1880.0,
                y: 500.0,
            });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 1880, y: 500 },
            WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
        );

        assert_eq!(placement.x, 1680);
        assert_eq!(placement.y, 380);
        assert_eq!(placement.width, 200);
        assert_eq!(placement.height, 120);
    }

    #[test]
    fn resolves_context_menu_inset_to_visible_upper_right_of_anchor() {
        let options = WindowOptions::new("tray-menu", "Tray Menu")
            .size(200.0, 120.0)
            .placement(WindowPlacement::ContextMenuInset {
                x: 320.0,
                y: 500.0,
                inset_x: 12.0,
                inset_y: 12.0,
            });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 320, y: 500 },
            WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
        );

        assert_eq!(placement.x, 308);
        assert_eq!(placement.y, 392);
        assert_eq!(placement.x + 12, 320);
        assert_eq!(placement.y + placement.height - 12, 500);
    }

    #[test]
    fn resolves_context_menu_inset_to_visible_upper_left_near_right_edge() {
        let options = WindowOptions::new("tray-menu", "Tray Menu")
            .size(200.0, 120.0)
            .placement(WindowPlacement::ContextMenuInset {
                x: 1880.0,
                y: 500.0,
                inset_x: 12.0,
                inset_y: 12.0,
            });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 1880, y: 500 },
            WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
        );

        assert_eq!(placement.x, 1692);
        assert_eq!(placement.y, 392);
        assert_eq!(placement.x + placement.width - 12, 1880);
        assert_eq!(placement.y + placement.height - 12, 500);
    }

    #[test]
    fn resolves_context_menu_inset_below_anchor_when_top_space_is_insufficient() {
        let options = WindowOptions::new("tray-menu", "Tray Menu")
            .size(200.0, 120.0)
            .placement(WindowPlacement::ContextMenuInset {
                x: 320.0,
                y: 20.0,
                inset_x: 12.0,
                inset_y: 12.0,
            });

        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            WindowsPoint { x: 320, y: 20 },
            WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
        );

        assert_eq!(placement.x, 308);
        assert_eq!(placement.y, 8);
        assert_eq!(placement.x + 12, 320);
        assert_eq!(placement.y + 12, 20);
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
    fn converts_cursor_placement_back_to_target_monitor_physical_pixels() {
        let options = WindowOptions::new("mini", "Mini")
            .size(320.0, 200.0)
            .placement(WindowPlacement::CursorOffset { x: 12.0, y: 12.0 });
        let monitor = WindowsMonitorMetrics::new(
            WindowsRect {
                left: 1920,
                top: 0,
                right: 3200,
                bottom: 960,
            },
            144,
        );
        let placement = WindowsPlatformAdapter::resolve_window_placement_for_monitor(
            &options,
            WindowsPoint { x: 2100, y: 300 },
            monitor,
        );

        assert_eq!(
            physical_window_geometry(&options, placement, monitor),
            (2118, 318, 480, 300)
        );
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
        let style = TrayMenuPresenterStyle::winui()
            .presenter_corner_radius(9)
            .item_corner_radius(7)
            .item_font_size(13)
            .item_min_height(35)
            .separator_line_thickness(2)
            .presenter_max_height(Some(444))
            .popup_animation(TrayMenuPopupAnimation::None);
        let tray = TrayMenu::new("win fluent")
            .icon_path("C:\\Easydict\\AppIcon.ico")
            .presenter_min_width(300)
            .presenter_style(style)
            .item(TrayMenuItem::new("open", "Open").tooltip("Open"));
        let tray_plan = WindowsPlatformAdapter::plan_tray::<Msg>(&tray).expect("tray plan");

        assert_eq!(
            tray_plan.icon_path.as_deref(),
            Some("C:\\Easydict\\AppIcon.ico")
        );
        assert_eq!(tray_plan.presenter_min_width, Some(300));
        assert_eq!(tray_plan.presenter_style, style);
        assert_eq!(tray_plan.callback_message, native::wm_user() + 1);
        assert_eq!(tray_plan.item_count, 1);
        assert_eq!(tray_plan.default_command_id, None);
        assert_eq!(tray_plan.items.len(), 1);
        assert_eq!(tray_plan.items[0].id, "open");
        assert_eq!(tray_plan.items[0].label, "Open");
        assert_eq!(tray_plan.items[0].tooltip.as_deref(), Some("Open"));
        assert!(tray_plan.items[0].enabled);
        assert_eq!(tray_plan.items[0].command_id, 1000);
        assert_eq!(tray_plan.items[0].action_kind, ActionKind::None);
        assert_eq!(tray_plan.items[0].kind, WindowsTrayItemKind::Command);
        assert!(tray_plan.items[0].children.is_empty());
        assert_eq!(
            WindowsPlatformAdapter::native_clipboard_format(ClipboardFormat::Text),
            Some(13)
        );
    }

    #[test]
    fn maps_structured_tray_menu_to_native_plan() {
        let tray = TrayMenu::new("win fluent")
            .default_item("open")
            .item(TrayMenuItem::new("open", "Open").on_invoke(Msg::Changed))
            .separator()
            .item(
                TrayMenuItem::submenu("browser", "Browser Support")
                    .tooltip("Browser Support")
                    .item(TrayMenuItem::new("install", "Install").on_invoke(Msg::Changed))
                    .item(TrayMenuItem::new("uninstall", "Uninstall").enabled(false)),
            )
            .item(TrayMenuItem::new("exit", "Exit").on_invoke(Msg::Changed));
        let tray_plan = WindowsPlatformAdapter::plan_tray(&tray).expect("tray plan");

        assert_eq!(tray_plan.item_count, 4);
        assert_eq!(tray_plan.default_command_id, Some(1000));
        assert_eq!(tray_plan.items[0].kind, WindowsTrayItemKind::Command);
        assert_eq!(tray_plan.items[0].command_id, 1000);
        assert_eq!(tray_plan.items[1].kind, WindowsTrayItemKind::Separator);
        assert_eq!(tray_plan.items[1].command_id, 0);
        assert_eq!(tray_plan.items[2].kind, WindowsTrayItemKind::Submenu);
        assert_eq!(tray_plan.items[2].label, "Browser Support");
        assert_eq!(
            tray_plan.items[2].tooltip.as_deref(),
            Some("Browser Support")
        );
        assert_eq!(tray_plan.items[2].children[0].id, "install");
        assert_eq!(tray_plan.items[2].children[0].command_id, 1001);
        assert!(tray_plan.items[2].children[0].enabled);
        assert_eq!(tray_plan.items[2].children[1].id, "uninstall");
        assert_eq!(tray_plan.items[2].children[1].command_id, 1002);
        assert!(!tray_plan.items[2].children[1].enabled);
        assert_eq!(tray_plan.items[3].id, "exit");
        assert_eq!(tray_plan.items[3].command_id, 1003);
    }

    #[test]
    fn tray_default_item_ignores_disabled_or_missing_commands() {
        let disabled_default = TrayMenu::new("win fluent")
            .default_item("open")
            .item(TrayMenuItem::new("open", "Open").enabled(false))
            .item(TrayMenuItem::new("exit", "Exit").on_invoke(Msg::Open));
        let disabled_plan =
            WindowsPlatformAdapter::plan_tray::<Msg>(&disabled_default).expect("tray plan");
        assert_eq!(disabled_plan.default_command_id, None);

        let missing_default = TrayMenu::new("win fluent")
            .default_item("missing")
            .item(TrayMenuItem::new("open", "Open").on_invoke(Msg::Open));
        let missing_plan =
            WindowsPlatformAdapter::plan_tray::<Msg>(&missing_default).expect("tray plan");
        assert_eq!(missing_plan.default_command_id, None);

        let disabled_parent = TrayMenu::new("win fluent").default_item("install").item(
            TrayMenuItem::submenu("browser", "Browser")
                .enabled(false)
                .item(TrayMenuItem::new("install", "Install").on_invoke(Msg::Open)),
        );
        let disabled_parent_plan =
            WindowsPlatformAdapter::plan_tray::<Msg>(&disabled_parent).expect("tray plan");
        assert_eq!(disabled_parent_plan.default_command_id, None);
    }

    #[cfg(windows)]
    #[test]
    fn tray_default_activation_accepts_mouse_and_keyboard_invocation() {
        use windows_sys::Win32::UI::Shell::NIN_SELECT;
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            WM_CONTEXTMENU, WM_LBUTTONDBLCLK, WM_LBUTTONUP, WM_RBUTTONUP,
        };

        assert!(native::is_tray_default_activation_message(WM_LBUTTONUP));
        assert!(native::is_tray_default_activation_message(WM_LBUTTONDBLCLK));
        assert!(native::is_tray_default_activation_message(NIN_SELECT));
        assert!(native::is_tray_default_activation_message(
            native::NIN_KEYSELECT
        ));
        assert!(!native::is_tray_default_activation_message(WM_RBUTTONUP));
        assert!(!native::is_tray_default_activation_message(WM_CONTEXTMENU));
    }

    #[cfg(windows)]
    #[test]
    fn tray_context_menu_accepts_legacy_and_v4_shell_messages() {
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            WM_CONTEXTMENU, WM_LBUTTONUP, WM_RBUTTONUP,
        };

        assert!(native::is_tray_context_menu_message(WM_RBUTTONUP));
        assert!(native::is_tray_context_menu_message(WM_CONTEXTMENU));
        assert!(!native::is_tray_context_menu_message(WM_LBUTTONUP));
    }

    #[cfg(windows)]
    #[test]
    fn tray_presenter_radius_maps_to_dwm_corner_preference() {
        use windows_sys::Win32::Graphics::Dwm::{
            DWMWCP_DONOTROUND, DWMWCP_ROUND, DWMWCP_ROUNDSMALL,
        };

        assert_eq!(
            native::tray_corner_preference_for_radius(0),
            DWMWCP_DONOTROUND
        );
        assert_eq!(
            native::tray_corner_preference_for_radius(4),
            DWMWCP_ROUNDSMALL
        );
        assert_eq!(native::tray_corner_preference_for_radius(8), DWMWCP_ROUND);
    }

    #[cfg(windows)]
    #[test]
    fn tray_popup_animation_maps_to_native_track_popup_flags() {
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            TPM_NOANIMATION, TPM_VERNEGANIMATION, TPM_VERPOSANIMATION,
        };

        assert_eq!(
            native::tray_menu_popup_animation_flags(TrayMenuPopupAnimation::System, false),
            0
        );
        assert_eq!(
            native::tray_menu_popup_animation_flags(TrayMenuPopupAnimation::None, false),
            TPM_NOANIMATION
        );
        assert_eq!(
            native::tray_menu_popup_animation_flags(TrayMenuPopupAnimation::Vertical, false),
            TPM_VERPOSANIMATION
        );
        assert_eq!(
            native::tray_menu_popup_animation_flags(TrayMenuPopupAnimation::Vertical, true),
            TPM_VERNEGANIMATION
        );
    }

    #[cfg(windows)]
    #[test]
    fn tray_callback_queue_records_canonical_version4_events_once() {
        use windows_sys::Win32::Foundation::HWND;
        use windows_sys::Win32::UI::Shell::NIN_SELECT;
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            WM_CONTEXTMENU, WM_LBUTTONUP, WM_RBUTTONUP,
        };

        // The HWND is only used as a map key, never dereferenced.
        let hwnd = 0x7777_usize as HWND;
        let callback = native::wm_user() + 1;
        native::register_tray_callback_queue(hwnd, callback);

        // NOTIFYICON_VERSION_4 packs the icon id into the high word.
        let pack = |event: u32| ((1_isize) << 16) | (event as isize);

        // A different message id is not this window's tray callback.
        assert!(!native::record_tray_callback(
            hwnd,
            callback + 7,
            pack(NIN_SELECT)
        ));
        // A single left click also emits the legacy WM_LBUTTONUP; it must be
        // dropped so the action does not fire twice. Same for right click's
        // WM_RBUTTONUP vs WM_CONTEXTMENU.
        assert!(native::record_tray_callback(
            hwnd,
            callback,
            pack(WM_LBUTTONUP)
        ));
        assert!(native::record_tray_callback(
            hwnd,
            callback,
            pack(WM_RBUTTONUP)
        ));
        // The canonical v4 notifications are the ones queued.
        assert!(native::record_tray_callback(
            hwnd,
            callback,
            pack(NIN_SELECT)
        ));
        assert!(native::record_tray_callback(
            hwnd,
            callback,
            pack(WM_CONTEXTMENU)
        ));

        assert_eq!(native::take_tray_callback_event(hwnd), Some(NIN_SELECT));
        assert_eq!(native::take_tray_callback_event(hwnd), Some(WM_CONTEXTMENU));
        assert_eq!(native::take_tray_callback_event(hwnd), None);

        // After unregistering, the window has no queue at all.
        native::unregister_tray_callback_queue(hwnd);
        assert_eq!(native::take_tray_callback_event(hwnd), None);
        assert!(!native::record_tray_callback(
            hwnd,
            callback,
            pack(NIN_SELECT)
        ));
    }

    #[cfg(windows)]
    #[test]
    fn tray_notification_event_unpacks_version4_lparam() {
        use windows_sys::Win32::UI::WindowsAndMessaging::{WM_LBUTTONUP, WM_RBUTTONUP};

        // NOTIFYICON_VERSION_4 packs the icon id (1) into the high word and the
        // event into the low word. The raw lParam must NOT be matched directly;
        // only the low word is the event.
        let icon_id: isize = 1;
        let left = (icon_id << 16) | (WM_LBUTTONUP as isize);
        let right = (icon_id << 16) | (WM_RBUTTONUP as isize);

        assert_ne!(left as u32, WM_LBUTTONUP, "raw lParam carries the icon id");
        assert_eq!(native::tray_notification_event(left), WM_LBUTTONUP);
        assert_eq!(native::tray_notification_event(right), WM_RBUTTONUP);
        assert!(native::is_tray_default_activation_message(
            native::tray_notification_event(left)
        ));
        assert!(native::is_tray_context_menu_message(
            native::tray_notification_event(right)
        ));
    }

    #[cfg(windows)]
    #[test]
    fn tray_owner_draw_blend_color_interpolates_channels() {
        // COLORREF is 0x00BBGGRR.
        let white = 0x00FF_FFFF;
        let black = 0x0000_0000;
        assert_eq!(native::blend_color(white, black, 0.0), white);
        assert_eq!(native::blend_color(white, black, 1.0), black);
        // Halfway between white and black is mid-gray on every channel
        // (255 * 0.5 = 127.5, rounded to 128 = 0x80).
        assert_eq!(native::blend_color(white, black, 0.5), 0x0080_8080);

        // Per-channel: red toward blue blends the R and B bytes independently.
        let mid = native::blend_color(0x0000_00FF, 0x00FF_0000, 0.5);
        assert_eq!(mid & 0xFF, 0x80); // red halved
        assert_eq!((mid >> 16) & 0xFF, 0x80); // blue half-raised
        assert_eq!((mid >> 8) & 0xFF, 0x00); // green untouched
    }

    #[cfg(windows)]
    #[test]
    fn tray_menu_hover_tooltips_map_win32_selection_messages() {
        use windows_sys::Win32::UI::WindowsAndMessaging::{MF_POPUP, MF_SEPARATOR};

        let mut state = native::TrayMenuTooltipState::default();
        state.command_tooltips.insert(1000, "Show Easydict".into());
        state
            .submenu_tooltips
            .insert((42, 2), "Browser Support".into());

        assert_eq!(
            native::tray_menu_selected_tooltip(&state, 1000, 0, 42),
            Some("Show Easydict")
        );
        assert_eq!(
            native::tray_menu_selected_tooltip(&state, 2, MF_POPUP as u16, 42),
            Some("Browser Support")
        );
        assert_eq!(
            native::tray_menu_selected_tooltip(&state, 2, MF_SEPARATOR as u16, 42),
            None
        );
        assert!(native::tray_menu_select_is_closed(0xffff, 0));
        assert_eq!(
            native::tray_menu_selected_tooltip(&state, 1000, 0xffff, 0),
            None
        );
    }

    #[cfg(windows)]
    #[test]
    fn creates_and_drops_tray_icon_when_notification_area_is_available() {
        let tray = TrayMenu::new("win fluent").item(TrayMenuItem::new("open", "Open"));
        let plan = WindowsPlatformAdapter::plan_tray::<Msg>(&tray).expect("tray plan");

        match WindowsPlatformAdapter::create_tray_icon(&plan) {
            Ok(handle) => {
                assert_eq!(handle.plan().tooltip, "win fluent");
                assert_eq!(handle.plan().items[0].id, "open");
                drop(handle);
            }
            Err(WindowsPlatformError::NativeCallFailed { operation, .. })
                if operation.starts_with("Shell_NotifyIconW") =>
            {
                // Headless CI and service sessions may not expose Explorer's notification area.
            }
            Err(error) => {
                panic!("tray icon should create or gracefully skip shell absence: {error:?}")
            }
        }
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

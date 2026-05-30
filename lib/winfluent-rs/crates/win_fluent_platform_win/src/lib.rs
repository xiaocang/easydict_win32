use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::time::{Duration, Instant};

use win_fluent::a11y::{A11yNode, A11yRole};
use win_fluent::platform::{
    ClipboardFormat, Hotkey, HotkeyKey, HotkeyModifier, ShellVerb, TrayMenu,
};
use win_fluent::subscription::{Subscription, SubscriptionKind};
use win_fluent::window::{
    WindowFrame, WindowLevel, WindowOptions, WindowPlacement, WindowResizeMode,
};

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum WindowsRegistration {
    Clipboard,
    Hotkey(WindowsHotkey),
    HotkeySubscription(String),
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
    pub work_area: WindowsRect,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct WindowsProcessMemory {
    pub private_bytes: usize,
    pub working_set_bytes: usize,
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
pub struct WindowsShellVerbPlan {
    pub id: String,
    pub label: String,
    pub accepts_files: bool,
    pub accepts_directory_background: bool,
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
    Text,
    Window,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowsUiaNodePlan {
    pub control_type: WindowsUiaControlType,
    pub name: Option<String>,
    pub description: Option<String>,
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

    pub fn resolve_window_placement(
        options: &WindowOptions,
    ) -> Result<ResolvedWindowPlacement, WindowsPlatformError> {
        let cursor = native::cursor_position()?;
        let work_area = native::monitor_work_area_for_point(cursor)?;

        Ok(Self::resolve_window_placement_for(
            options, cursor, work_area,
        ))
    }

    pub fn resolve_window_placement_for(
        options: &WindowOptions,
        cursor: WindowsPoint,
        work_area: WindowsRect,
    ) -> ResolvedWindowPlacement {
        resolve_window_placement_with(options, cursor, work_area)
    }

    pub fn plan_window_with_resolved_placement(
        options: &WindowOptions,
    ) -> Result<WindowsWindowPlan, WindowsPlatformError> {
        let mut plan = Self::plan_window(options);
        plan.placement = Some(Self::resolve_window_placement(options)?);
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

    pub fn plan_shell_verbs(verbs: &[ShellVerb]) -> Vec<WindowsShellVerbPlan> {
        verbs
            .iter()
            .map(|verb| WindowsShellVerbPlan {
                id: verb.id.clone(),
                label: verb.label.clone(),
                accepts_files: verb.accepts_files,
                accepts_directory_background: verb.accepts_directory_background,
            })
            .collect()
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

    pub fn send_hotkey_input_for_probe(hotkey: &Hotkey) -> Result<(), WindowsPlatformError> {
        let native_hotkey = plan_hotkey(hotkey)?;
        native::send_hotkey_input_for_probe(&native_hotkey)
    }

    pub fn send_unicode_text_input_for_probe(text: &str) -> Result<(), WindowsPlatformError> {
        native::send_unicode_text_input_for_probe(text)
    }

    pub fn send_clipboard_text_paste_for_probe(text: &str) -> Result<(), WindowsPlatformError> {
        native::send_clipboard_text_paste_for_probe(text)
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
        Subscription::Event { kind, .. } => registrations.push(match kind {
            SubscriptionKind::Hotkey(id) => WindowsRegistration::HotkeySubscription(id.clone()),
            SubscriptionKind::Clipboard => WindowsRegistration::Clipboard,
            SubscriptionKind::Theme => WindowsRegistration::Theme,
            SubscriptionKind::Tray => WindowsRegistration::Tray(WindowsTrayPlan {
                tooltip: String::new(),
                callback_message: native::wm_user() + 1,
                item_count: 0,
            }),
            SubscriptionKind::Window(id) => WindowsRegistration::Window(id.as_str().to_string()),
            SubscriptionKind::Custom(kind) => {
                WindowsRegistration::ShellVerb(WindowsShellVerbPlan {
                    id: kind.clone(),
                    label: kind.clone(),
                    accepts_files: true,
                    accepts_directory_background: false,
                })
            }
        }),
        Subscription::Batch(values) => {
            for value in values {
                collect_subscription(value, registrations)?;
            }
        }
    }

    Ok(())
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
        WindowLevel::ToolWindow => ex_style |= native::ws_ex_toolwindow(),
    }

    if options.skip_taskbar {
        ex_style |= native::ws_ex_toolwindow();
    }

    ex_style
}

fn resolve_window_placement_with(
    options: &WindowOptions,
    cursor: WindowsPoint,
    work_area: WindowsRect,
) -> ResolvedWindowPlacement {
    let width = options.width.round() as i32;
    let height = options.height.round() as i32;

    let (x, y) = match options.placement {
        WindowPlacement::Center => (
            work_area.left + (work_area.width() - width) / 2,
            work_area.top + (work_area.height() - height) / 2,
        ),
        WindowPlacement::CursorOffset { x, y } => {
            (cursor.x + x.round() as i32, cursor.y + y.round() as i32)
        }
        WindowPlacement::TopRight { margin_x, margin_y } => (
            work_area.right - width - margin_x.round() as i32,
            work_area.top + margin_y.round() as i32,
        ),
        WindowPlacement::Explicit { x, y } => (x.round() as i32, y.round() as i32),
    };

    let (x, y) = match options.placement {
        WindowPlacement::Explicit { .. } => (x, y),
        _ => (
            clamp_axis(x, width, work_area.left, work_area.right),
            clamp_axis(y, height, work_area.top, work_area.bottom),
        ),
    };

    ResolvedWindowPlacement {
        x,
        y,
        width,
        height,
        work_area,
    }
}

fn clamp_axis(value: i32, size: i32, min: i32, max: i32) -> i32 {
    if max - min <= size {
        return min;
    }

    value.clamp(min, max - size)
}

#[cfg(windows)]
mod native {
    use std::ptr::null_mut;

    use super::{
        ClipboardFormat, NativeHotkeyMessage, WindowsClipboardFormatSnapshot,
        WindowsClipboardTextSnapshot, WindowsHotkey, WindowsHotkeyHandle, WindowsPlatformError,
        WindowsPoint, WindowsProcessMemory, WindowsRect,
    };
    use windows_sys::Win32::Foundation::{GetLastError, GlobalFree, SetLastError, HWND, POINT};
    use windows_sys::Win32::Graphics::Gdi::{
        GetMonitorInfoW, MonitorFromPoint, MONITORINFO, MONITOR_DEFAULTTONEAREST,
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
    use windows_sys::Win32::System::Threading::GetCurrentProcess;
    #[cfg(test)]
    use windows_sys::Win32::System::Threading::GetCurrentThreadId;
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        RegisterHotKey, SendInput, UnregisterHotKey, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT,
        KEYEVENTF_KEYUP, KEYEVENTF_UNICODE, MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN, VK_BACK,
        VK_CONTROL, VK_DELETE, VK_DOWN, VK_END, VK_ESCAPE, VK_F1, VK_HOME, VK_LEFT, VK_LWIN,
        VK_MENU, VK_RETURN, VK_RIGHT, VK_SHIFT, VK_SPACE, VK_TAB, VK_UP,
    };
    #[cfg(test)]
    use windows_sys::Win32::UI::WindowsAndMessaging::PostThreadMessageW;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        GetCursorPos, PeekMessageW, MSG, PM_REMOVE, WM_HOTKEY, WM_USER, WS_EX_TOOLWINDOW,
        WS_EX_TOPMOST, WS_MINIMIZEBOX, WS_OVERLAPPEDWINDOW, WS_POPUP, WS_THICKFRAME,
    };

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

    pub fn monitor_work_area_for_point(
        point: WindowsPoint,
    ) -> Result<WindowsRect, WindowsPlatformError> {
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

        Ok(WindowsRect {
            left: info.rcWork.left,
            top: info.rcWork.top,
            right: info.rcWork.right,
            bottom: info.rcWork.bottom,
        })
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
}

#[cfg(not(windows))]
mod native {
    use super::{
        ClipboardFormat, NativeHotkeyMessage, WindowsClipboardTextSnapshot, WindowsHotkey,
        WindowsHotkeyHandle, WindowsPlatformError, WindowsPoint, WindowsProcessMemory, WindowsRect,
    };

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

    pub fn is_clipboard_format_available(
        _format: ClipboardFormat,
    ) -> Result<bool, WindowsPlatformError> {
        Err(WindowsPlatformError::UnsupportedPlatform)
    }

    pub fn cursor_position() -> Result<WindowsPoint, WindowsPlatformError> {
        Ok(WindowsPoint { x: 0, y: 0 })
    }

    pub fn monitor_work_area_for_point(
        _point: WindowsPoint,
    ) -> Result<WindowsRect, WindowsPlatformError> {
        Ok(WindowsRect {
            left: 0,
            top: 0,
            right: 1920,
            bottom: 1080,
        })
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
}

#[cfg(test)]
mod tests {
    use super::*;
    use win_fluent::a11y::{resolve_accessibility_tree, A11yNode, A11yRole};
    use win_fluent::platform::{HotkeyKey, HotkeyModifier, TrayMenu, TrayMenuItem};
    use win_fluent::prelude::{button, column, page, text_editor, IntoView};
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
            .skip_taskbar(true);

        let plan = WindowsPlatformAdapter::plan_window(&options);

        assert_eq!(plan.id, "mini");
        assert_eq!(plan.style, native::ws_popup());
        assert!(plan.ex_style & native::ws_ex_topmost() != 0);
        assert!(plan.ex_style & native::ws_ex_toolwindow() != 0);
        assert!(plan.uses_acrylic);
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
    fn keeps_explicit_window_placement_unclamped() {
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

        assert_eq!(placement.x, 2200);
        assert_eq!(placement.y, -500);
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
    fn maps_shell_verbs_without_touching_registry() {
        let verbs =
            WindowsPlatformAdapter::plan_shell_verbs(&[
                ShellVerb::new("ocr", "OCR Translate").directory_background(true)
            ]);

        assert_eq!(verbs[0].id, "ocr");
        assert!(verbs[0].accepts_files);
        assert!(verbs[0].accepts_directory_background);
    }

    #[test]
    fn maps_accessibility_tree_to_uia_control_types() {
        let mut root = A11yNode::new(A11yRole::Application);
        root.name = Some("Win Fluent".to_string());

        let mut group = A11yNode::new(A11yRole::Group);
        let mut button = A11yNode::new(A11yRole::Button);
        button.name = Some("Translate".to_string());
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
}

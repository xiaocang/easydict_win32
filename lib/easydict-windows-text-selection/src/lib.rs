#![cfg_attr(not(windows), forbid(unsafe_code))]

use std::fmt;

pub const DEFAULT_UIA_SEMAPHORE_TIMEOUT_MS: u64 = 200;
pub const DEFAULT_UIA_EXECUTION_TIMEOUT_MS: u64 = 800;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ForegroundTextSelectionTarget {
    pub hwnd: isize,
    pub process_id: u32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TextInsertionTarget {
    pub hwnd: isize,
    pub process_id: u32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ClipboardTextSnapshot {
    pub text: Option<String>,
    pub available_format_count: u32,
    pub sequence_number: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LowLevelMouseHookEvent {
    pub message: u32,
    pub x: i32,
    pub y: i32,
    pub mouse_data: u32,
    pub flags: u32,
    pub event_time_ms: u32,
    pub extra_info: isize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LowLevelKeyboardHookEvent {
    pub message: u32,
    pub virtual_key: u32,
    pub scan_code: u32,
    pub flags: u32,
    pub event_time_ms: u32,
    pub extra_info: isize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LowLevelInputHookEvent {
    Mouse(LowLevelMouseHookEvent),
    Keyboard(LowLevelKeyboardHookEvent),
}

#[derive(Debug)]
pub enum WindowsTextSelectionError {
    UnsupportedPlatform,
    NativeCallFailed {
        operation: &'static str,
        code: i32,
    },
    UiaFailed {
        operation: &'static str,
        message: String,
    },
    InvalidWindow,
    ClipboardUnavailable,
    ClipboardDataUnavailable,
    UiaBusy {
        timeout_ms: u64,
    },
    UiaTimedOut {
        timeout_ms: u64,
    },
    LowLevelHookAlreadyInstalled,
    LowLevelHookThreadUnavailable,
}

impl fmt::Display for WindowsTextSelectionError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform => {
                write!(f, "Windows text selection is only available on Windows")
            }
            Self::NativeCallFailed { operation, code } => {
                write!(f, "{operation} failed with Win32 error {code}")
            }
            Self::UiaFailed { operation, message } => {
                write!(f, "{operation} failed: {message}")
            }
            Self::InvalidWindow => write!(f, "foreground window is not available"),
            Self::ClipboardUnavailable => write!(f, "clipboard is not available"),
            Self::ClipboardDataUnavailable => write!(f, "clipboard text data is not available"),
            Self::UiaBusy { timeout_ms } => {
                write!(
                    f,
                    "UI Automation selection is busy after waiting {timeout_ms}ms"
                )
            }
            Self::UiaTimedOut { timeout_ms } => {
                write!(f, "UI Automation selection timed out after {timeout_ms}ms")
            }
            Self::LowLevelHookAlreadyInstalled => {
                write!(f, "Windows low-level input hook is already installed")
            }
            Self::LowLevelHookThreadUnavailable => {
                write!(f, "Windows low-level input hook thread is unavailable")
            }
        }
    }
}

impl std::error::Error for WindowsTextSelectionError {}

fn process_name_from_image_path(path: &str) -> Option<String> {
    let file_name = path
        .rsplit(['\\', '/'])
        .next()
        .map(str::trim)
        .filter(|value| !value.is_empty())?;
    let stem = file_name
        .rsplit_once('.')
        .and_then(|(stem, extension)| extension.eq_ignore_ascii_case("exe").then_some(stem))
        .unwrap_or(file_name);
    Some(stem.to_string())
}

#[cfg(windows)]
mod platform {
    use super::{
        ClipboardTextSnapshot, ForegroundTextSelectionTarget, LowLevelInputHookEvent,
        LowLevelKeyboardHookEvent, LowLevelMouseHookEvent, TextInsertionTarget,
        WindowsTextSelectionError, DEFAULT_UIA_SEMAPHORE_TIMEOUT_MS,
    };
    use std::ffi::c_void;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::mpsc::{self, Receiver, Sender, TryRecvError};
    use std::sync::{Mutex, OnceLock};
    use std::thread;
    use std::time::{Duration, Instant};
    use windows::core::PWSTR;
    use windows::Win32::Foundation::{
        CloseHandle, GetLastError, GlobalFree, SetLastError, HANDLE, HGLOBAL, HWND, LPARAM,
        LRESULT, POINT, RPC_E_CHANGED_MODE, WIN32_ERROR,
    };
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CoUninitialize, CLSCTX_INPROC_SERVER,
        COINIT_MULTITHREADED,
    };
    use windows::Win32::System::DataExchange::{
        CloseClipboard, EmptyClipboard, EnumClipboardFormats, GetClipboardData,
        GetClipboardSequenceNumber, IsClipboardFormatAvailable, OpenClipboard, SetClipboardData,
    };
    use windows::Win32::System::Memory::{
        GlobalAlloc, GlobalLock, GlobalSize, GlobalUnlock, GMEM_MOVEABLE,
    };
    use windows::Win32::System::Ole::CF_UNICODETEXT;
    use windows::Win32::System::SystemInformation::GetTickCount64;
    use windows::Win32::System::Threading::{
        AttachThreadInput, GetCurrentThreadId, OpenProcess, QueryFullProcessImageNameW,
        PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
    };
    use windows::Win32::UI::Accessibility::{
        CUIAutomation, IUIAutomation, IUIAutomationElement, IUIAutomationTextPattern,
        IUIAutomationTextPattern2, IUIAutomationTextRange, UIA_TextPattern2Id, UIA_TextPatternId,
    };
    use windows::Win32::UI::Input::KeyboardAndMouse::{
        GetDoubleClickTime, SendInput, SetFocus, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT,
        KEYEVENTF_KEYUP, VIRTUAL_KEY, VK_C, VK_CONTROL, VK_LWIN, VK_MENU, VK_RWIN, VK_SHIFT, VK_V,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        CallNextHookEx, GetAncestor, GetForegroundWindow, GetMessageW, GetWindowThreadProcessId,
        IsWindow, PeekMessageW, PostThreadMessageW, SetForegroundWindow, SetWindowsHookExW,
        UnhookWindowsHookEx, WindowFromPoint, GA_ROOT, HC_ACTION, HHOOK, KBDLLHOOKSTRUCT, MSG,
        MSLLHOOKSTRUCT, PM_NOREMOVE, WH_KEYBOARD_LL, WH_MOUSE_LL, WM_QUIT,
    };

    static LOW_LEVEL_HOOK_SENDER: OnceLock<Mutex<Option<Sender<LowLevelInputHookEvent>>>> =
        OnceLock::new();
    static UIA_SELECTION_IN_PROGRESS: AtomicBool = AtomicBool::new(false);

    struct ClipboardGuard;

    impl Drop for ClipboardGuard {
        fn drop(&mut self) {
            // Safety: balanced with a successful OpenClipboard call in this module.
            let _ = unsafe { CloseClipboard() };
        }
    }

    struct HandleGuard(HANDLE);

    impl Drop for HandleGuard {
        fn drop(&mut self) {
            // Safety: HandleGuard only wraps handles returned by successful Win32 calls.
            let _ = unsafe { CloseHandle(self.0) };
        }
    }

    struct HookGuard(HHOOK);

    impl Drop for HookGuard {
        fn drop(&mut self) {
            // Safety: HookGuard only wraps HHOOK values returned by successful hook installs.
            let _ = unsafe { UnhookWindowsHookEx(self.0) };
        }
    }

    struct HookSenderGuard;

    impl Drop for HookSenderGuard {
        fn drop(&mut self) {
            if let Some(sender) = LOW_LEVEL_HOOK_SENDER.get() {
                if let Ok(mut sender) = sender.lock() {
                    *sender = None;
                }
            }
        }
    }

    struct UiaSelectionGuard;

    impl Drop for UiaSelectionGuard {
        fn drop(&mut self) {
            UIA_SELECTION_IN_PROGRESS.store(false, Ordering::Release);
        }
    }

    struct ComApartment {
        should_uninitialize: bool,
    }

    impl ComApartment {
        fn initialize() -> Result<Self, WindowsTextSelectionError> {
            let result = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
            if result.is_ok() {
                Ok(Self {
                    should_uninitialize: true,
                })
            } else if result == RPC_E_CHANGED_MODE {
                Ok(Self {
                    should_uninitialize: false,
                })
            } else {
                Err(uia_error("CoInitializeEx", result))
            }
        }
    }

    impl Drop for ComApartment {
        fn drop(&mut self) {
            if self.should_uninitialize {
                // Safety: paired with a successful CoInitializeEx on this thread.
                unsafe { CoUninitialize() };
            }
        }
    }

    pub struct LowLevelInputHook {
        events: Receiver<LowLevelInputHookEvent>,
        thread_id: u32,
        thread: Option<thread::JoinHandle<()>>,
    }

    impl LowLevelInputHook {
        pub fn events(&self) -> &Receiver<LowLevelInputHookEvent> {
            &self.events
        }

        pub fn try_recv(&self) -> Result<LowLevelInputHookEvent, TryRecvError> {
            self.events.try_recv()
        }
    }

    impl Drop for LowLevelInputHook {
        fn drop(&mut self) {
            let posted = unsafe {
                PostThreadMessageW(
                    self.thread_id,
                    WM_QUIT,
                    Default::default(),
                    Default::default(),
                )
            }
            .is_ok();

            if posted {
                if let Some(thread) = self.thread.take() {
                    let _ = thread.join();
                }
            }
        }
    }

    pub fn start_low_level_input_hook() -> Result<LowLevelInputHook, WindowsTextSelectionError> {
        let (event_tx, event_rx) = mpsc::channel();
        let (ready_tx, ready_rx) = mpsc::channel();

        let thread = thread::spawn(move || {
            let _ = run_low_level_input_hook_thread(event_tx, ready_tx);
        });

        let thread_id = match ready_rx.recv() {
            Ok(Ok(thread_id)) => thread_id,
            Ok(Err(error)) => {
                let _ = thread.join();
                return Err(error);
            }
            Err(_) => {
                let _ = thread.join();
                return Err(WindowsTextSelectionError::LowLevelHookThreadUnavailable);
            }
        };

        Ok(LowLevelInputHook {
            events: event_rx,
            thread_id,
            thread: Some(thread),
        })
    }

    pub fn system_double_click_time_ms() -> u64 {
        u64::from(unsafe { GetDoubleClickTime() })
    }

    pub fn current_tick_count_ms() -> i64 {
        let ticks = unsafe { GetTickCount64() };
        i64::try_from(ticks).unwrap_or(i64::MAX)
    }

    pub fn point_targets_window_root(
        x: i32,
        y: i32,
        hwnd: isize,
    ) -> Result<bool, WindowsTextSelectionError> {
        let target = HWND(hwnd as *mut c_void);
        if !is_valid_window(target) {
            return Err(WindowsTextSelectionError::InvalidWindow);
        }

        let point = POINT { x, y };
        let window = unsafe { WindowFromPoint(point) };
        if window.0.is_null() {
            return Ok(false);
        }

        if window == target {
            return Ok(true);
        }

        let root = unsafe { GetAncestor(window, GA_ROOT) };
        Ok(root == target)
    }

    pub fn foreground_text_selection_target(
    ) -> Result<ForegroundTextSelectionTarget, WindowsTextSelectionError> {
        // Safety: reads the current foreground HWND without taking ownership.
        let hwnd = unsafe { GetForegroundWindow() };
        if !is_valid_window(hwnd) {
            return Err(WindowsTextSelectionError::InvalidWindow);
        }

        let mut process_id = 0u32;
        // Safety: hwnd is a valid borrowed window handle; process_id points to valid storage.
        unsafe { GetWindowThreadProcessId(hwnd, Some(&mut process_id)) };

        Ok(ForegroundTextSelectionTarget {
            hwnd: hwnd.0 as isize,
            process_id,
        })
    }

    pub fn foreground_text_insertion_target(
    ) -> Result<TextInsertionTarget, WindowsTextSelectionError> {
        // Safety: reads the current foreground HWND without taking ownership.
        let hwnd = unsafe { GetForegroundWindow() };
        if !is_valid_window(hwnd) {
            return Err(WindowsTextSelectionError::InvalidWindow);
        }

        let mut process_id = 0u32;
        // Safety: hwnd is a valid borrowed window handle; process_id points to valid storage.
        unsafe { GetWindowThreadProcessId(hwnd, Some(&mut process_id)) };

        Ok(TextInsertionTarget {
            hwnd: hwnd.0 as isize,
            process_id,
        })
    }

    pub fn text_insertion_target_is_valid(target: &TextInsertionTarget) -> bool {
        is_valid_window(HWND(target.hwnd as *mut c_void))
    }

    pub fn process_name_for_id(
        process_id: u32,
    ) -> Result<Option<String>, WindowsTextSelectionError> {
        if process_id == 0 {
            return Ok(None);
        }

        // Safety: process_id is owned by the OS; the returned handle is closed by HandleGuard.
        let handle = unsafe {
            OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process_id)
                .map_err(|error| windows_error("OpenProcess", error))?
        };
        let _guard = HandleGuard(handle);

        let mut buffer = vec![0u16; 32_768];
        let mut length = buffer.len() as u32;
        // Safety: buffer is writable for length UTF-16 code units and length points to valid storage.
        unsafe {
            QueryFullProcessImageNameW(
                handle,
                PROCESS_NAME_WIN32,
                PWSTR(buffer.as_mut_ptr()),
                &mut length,
            )
            .map_err(|error| windows_error("QueryFullProcessImageNameW", error))?;
        }

        let image_path = String::from_utf16_lossy(&buffer[..length as usize]);
        Ok(super::process_name_from_image_path(&image_path))
    }

    pub fn selected_text_via_uia() -> Result<Option<String>, WindowsTextSelectionError> {
        let _com = ComApartment::initialize()?;
        // Safety: COM is initialized on this thread and CUIAutomation is the documented UIA class.
        let automation: IUIAutomation = unsafe {
            CoCreateInstance(&CUIAutomation, None, CLSCTX_INPROC_SERVER)
                .map_err(|error| uia_error("CoCreateInstance(CUIAutomation)", error.code()))?
        };

        // Safety: UI Automation owns the returned focused element COM interface.
        let element = unsafe {
            automation
                .GetFocusedElement()
                .map_err(|error| uia_error("IUIAutomation::GetFocusedElement", error.code()))?
        };

        selected_text_from_element(&element)
    }

    pub fn selected_text_via_uia_with_timeout(
        timeout_ms: u64,
    ) -> Result<Option<String>, WindowsTextSelectionError> {
        let guard = acquire_uia_selection_guard(DEFAULT_UIA_SEMAPHORE_TIMEOUT_MS)?;
        let (tx, rx) = mpsc::channel();

        thread::spawn(move || {
            let _guard = guard;
            let result = selected_text_via_uia();
            let _ = tx.send(result);
        });

        match rx.recv_timeout(Duration::from_millis(timeout_ms)) {
            Ok(result) => result,
            Err(mpsc::RecvTimeoutError::Timeout) => {
                Err(WindowsTextSelectionError::UiaTimedOut { timeout_ms })
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                Err(WindowsTextSelectionError::UiaFailed {
                    operation: "selected_text_via_uia_with_timeout",
                    message: "worker thread exited before returning a result".to_string(),
                })
            }
        }
    }

    fn acquire_uia_selection_guard(
        timeout_ms: u64,
    ) -> Result<UiaSelectionGuard, WindowsTextSelectionError> {
        let deadline = Instant::now() + Duration::from_millis(timeout_ms);
        loop {
            if UIA_SELECTION_IN_PROGRESS
                .compare_exchange(false, true, Ordering::Acquire, Ordering::Relaxed)
                .is_ok()
            {
                return Ok(UiaSelectionGuard);
            }

            if Instant::now() >= deadline {
                return Err(WindowsTextSelectionError::UiaBusy { timeout_ms });
            }

            thread::sleep(Duration::from_millis(10));
        }
    }

    fn selected_text_from_element(
        element: &IUIAutomationElement,
    ) -> Result<Option<String>, WindowsTextSelectionError> {
        if let Ok(pattern) =
            unsafe { element.GetCurrentPatternAs::<IUIAutomationTextPattern>(UIA_TextPatternId) }
        {
            if let Ok(Some(text)) = selected_text_from_text_pattern(&pattern) {
                return Ok(Some(text));
            }
        }

        if let Ok(pattern2) =
            unsafe { element.GetCurrentPatternAs::<IUIAutomationTextPattern2>(UIA_TextPattern2Id) }
        {
            if let Ok(Some(text)) = selected_text_from_text_pattern(&pattern2) {
                return Ok(Some(text));
            }
        }

        Ok(None)
    }

    fn selected_text_from_text_pattern(
        pattern: &IUIAutomationTextPattern,
    ) -> Result<Option<String>, WindowsTextSelectionError> {
        // Safety: pattern is a live UIA text pattern COM interface.
        let ranges = unsafe {
            pattern.GetSelection().map_err(|error| {
                uia_error("IUIAutomationTextPattern::GetSelection", error.code())
            })?
        };
        // Safety: ranges is a live UIA text range array.
        let length = unsafe {
            ranges
                .Length()
                .map_err(|error| uia_error("IUIAutomationTextRangeArray::Length", error.code()))?
        };

        let mut chunks = Vec::new();
        for index in 0..length {
            // Safety: index is within the range returned by Length.
            let range = unsafe {
                ranges.GetElement(index).map_err(|error| {
                    uia_error("IUIAutomationTextRangeArray::GetElement", error.code())
                })?
            };
            if let Some(text) = text_from_range(&range)? {
                chunks.push(text);
            }
        }

        let text = chunks.join("\n").trim().to_string();
        Ok((!text.is_empty()).then_some(text))
    }

    fn text_from_range(
        range: &IUIAutomationTextRange,
    ) -> Result<Option<String>, WindowsTextSelectionError> {
        // Safety: range is a live UIA text range; -1 asks UIA for the entire range text.
        let text = unsafe {
            range
                .GetText(-1)
                .map_err(|error| uia_error("IUIAutomationTextRange::GetText", error.code()))?
        };
        let text = text.to_string();
        let text = text.trim();
        Ok((!text.is_empty()).then(|| text.to_string()))
    }

    pub fn clipboard_sequence_number() -> u32 {
        // Safety: GetClipboardSequenceNumber has no preconditions.
        unsafe { GetClipboardSequenceNumber() }
    }

    pub fn clipboard_text_snapshot() -> Result<ClipboardTextSnapshot, WindowsTextSelectionError> {
        // Safety: passing a null owner opens the current process clipboard for inspection.
        if unsafe { OpenClipboard(None) }.is_err() {
            return Err(WindowsTextSelectionError::ClipboardUnavailable);
        }

        let _guard = ClipboardGuard;
        let available_format_count = clipboard_format_count()?;
        let text = if unsafe { IsClipboardFormatAvailable(CF_UNICODETEXT.0 as u32) }.is_ok() {
            Some(read_clipboard_unicode_text()?)
        } else {
            None
        };

        Ok(ClipboardTextSnapshot {
            text,
            available_format_count,
            sequence_number: clipboard_sequence_number(),
        })
    }

    pub fn set_clipboard_text(text: &str) -> Result<(), WindowsTextSelectionError> {
        // Safety: passing a null owner opens the current process clipboard for mutation.
        if unsafe { OpenClipboard(None) }.is_err() {
            return Err(WindowsTextSelectionError::ClipboardUnavailable);
        }

        let _guard = ClipboardGuard;
        // Safety: clipboard is open for the current process.
        if unsafe { EmptyClipboard() }.is_err() {
            return Err(last_error("EmptyClipboard"));
        }

        let handle = global_alloc_utf16_text(text)?;
        // Safety: clipboard is open and handle contains a movable memory block; ownership
        // transfers to the system on success.
        if unsafe { SetClipboardData(CF_UNICODETEXT.0 as u32, Some(HANDLE(handle.0))) }.is_err() {
            // Safety: ownership was not transferred to the clipboard.
            let _ = unsafe { GlobalFree(Some(handle)) };
            return Err(last_error("SetClipboardData"));
        }

        Ok(())
    }

    pub fn clear_clipboard() -> Result<(), WindowsTextSelectionError> {
        // Safety: passing a null owner opens the current process clipboard for mutation.
        if unsafe { OpenClipboard(None) }.is_err() {
            return Err(WindowsTextSelectionError::ClipboardUnavailable);
        }

        let _guard = ClipboardGuard;
        // Safety: clipboard is open for the current process.
        if unsafe { EmptyClipboard() }.is_err() {
            return Err(last_error("EmptyClipboard"));
        }

        Ok(())
    }

    pub fn focus_window_and_send_ctrl_c(
        hwnd: isize,
        extra_info: isize,
    ) -> Result<(), WindowsTextSelectionError> {
        focus_window_and_send_hotkey(hwnd, VK_C, extra_info)
    }

    pub fn focus_window_and_send_ctrl_v(
        hwnd: isize,
        extra_info: isize,
    ) -> Result<(), WindowsTextSelectionError> {
        focus_window_and_send_hotkey(hwnd, VK_V, extra_info)
    }

    pub fn insert_text_into_target(
        target: &TextInsertionTarget,
        text: &str,
        extra_info: isize,
    ) -> Result<(), WindowsTextSelectionError> {
        if text.is_empty() {
            return Ok(());
        }
        if !text_insertion_target_is_valid(target) {
            return Err(WindowsTextSelectionError::InvalidWindow);
        }

        set_clipboard_text(text)?;
        focus_window_and_send_ctrl_v(target.hwnd, extra_info)
    }

    fn focus_window_and_send_hotkey(
        hwnd: isize,
        key: VIRTUAL_KEY,
        extra_info: isize,
    ) -> Result<(), WindowsTextSelectionError> {
        let hwnd = HWND(hwnd as *mut c_void);
        if !is_valid_window(hwnd) {
            return Err(WindowsTextSelectionError::InvalidWindow);
        }

        let mut process_id = 0u32;
        // Safety: hwnd was validated above; process_id points to valid storage.
        let target_thread_id = unsafe { GetWindowThreadProcessId(hwnd, Some(&mut process_id)) };
        // Safety: GetCurrentThreadId has no preconditions.
        let current_thread_id = unsafe { GetCurrentThreadId() };
        // Safety: reads foreground window without taking ownership.
        let foreground_hwnd = unsafe { GetForegroundWindow() };
        let foreground_thread_id = if is_valid_window(foreground_hwnd) {
            // Safety: foreground_hwnd was validated above; process id is not needed here.
            unsafe { GetWindowThreadProcessId(foreground_hwnd, None) }
        } else {
            0
        };
        let mut attached_target = false;
        let mut attached_foreground = false;

        if target_thread_id != 0 && target_thread_id != current_thread_id {
            // Safety: thread ids come from Win32 APIs; detach is attempted before returning.
            attached_target =
                unsafe { AttachThreadInput(current_thread_id, target_thread_id, true) }.as_bool();
        }
        if foreground_thread_id != 0
            && foreground_thread_id != current_thread_id
            && foreground_thread_id != target_thread_id
        {
            // Safety: thread ids come from Win32 APIs; detach is attempted before returning.
            attached_foreground =
                unsafe { AttachThreadInput(current_thread_id, foreground_thread_id, true) }
                    .as_bool();
        }

        let result = (|| {
            // Safety: hwnd is a valid borrowed OS window handle.
            if !unsafe { SetForegroundWindow(hwnd) }.as_bool() {
                return Err(last_error("SetForegroundWindow"));
            }

            thread::sleep(Duration::from_millis(100));
            // Safety: reads foreground window without taking ownership.
            if unsafe { GetForegroundWindow() } != hwnd {
                return Err(WindowsTextSelectionError::InvalidWindow);
            }
            if target_thread_id == current_thread_id {
                // Same-thread smoke/helper windows need an explicit focus restore after
                // foreground activation; external app windows keep their own focused child.
                let _ = unsafe { SetFocus(Some(hwnd)) };
            }

            send_ctrl_hotkey(key, extra_info)
        })();

        if attached_foreground {
            // Safety: reverses a successful AttachThreadInput call above.
            let _ = unsafe { AttachThreadInput(current_thread_id, foreground_thread_id, false) };
        }
        if attached_target {
            // Safety: reverses a successful AttachThreadInput call above.
            let _ = unsafe { AttachThreadInput(current_thread_id, target_thread_id, false) };
        }

        result
    }

    pub fn send_ctrl_c(extra_info: isize) -> Result<(), WindowsTextSelectionError> {
        send_ctrl_hotkey(VK_C, extra_info)
    }

    pub fn send_ctrl_v(extra_info: isize) -> Result<(), WindowsTextSelectionError> {
        send_ctrl_hotkey(VK_V, extra_info)
    }

    fn send_ctrl_hotkey(
        key: VIRTUAL_KEY,
        extra_info: isize,
    ) -> Result<(), WindowsTextSelectionError> {
        let mut inputs = Vec::new();
        for key in [VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN] {
            inputs.push(keyboard_input(key, true, extra_info));
        }
        inputs.push(keyboard_input(VK_CONTROL, false, extra_info));
        inputs.push(keyboard_input(key, false, extra_info));
        inputs.push(keyboard_input(key, true, extra_info));
        inputs.push(keyboard_input(VK_CONTROL, true, extra_info));

        // Safety: inputs is a contiguous array of INPUT values with the correct element size.
        let sent = unsafe { SendInput(&inputs, std::mem::size_of::<INPUT>() as i32) };
        if sent != inputs.len() as u32 {
            return Err(last_error("SendInput"));
        }

        Ok(())
    }

    fn clipboard_format_count() -> Result<u32, WindowsTextSelectionError> {
        let mut count = 0u32;
        let mut current = 0u32;
        loop {
            // Safety: clipboard is open. SetLastError lets us distinguish end-of-enum from errors.
            unsafe { SetLastError(WIN32_ERROR(0)) };
            // Safety: clipboard is open for enumeration.
            let format = unsafe { EnumClipboardFormats(current) };
            if format == 0 {
                let code = unsafe { GetLastError() };
                if code.0 != 0 {
                    return Err(WindowsTextSelectionError::NativeCallFailed {
                        operation: "EnumClipboardFormats",
                        code: code.0 as i32,
                    });
                }
                return Ok(count);
            }

            count = count.saturating_add(1);
            current = format;
        }
    }

    fn read_clipboard_unicode_text() -> Result<String, WindowsTextSelectionError> {
        // Safety: clipboard is open and format availability has already been checked.
        let handle = unsafe { GetClipboardData(CF_UNICODETEXT.0 as u32) }
            .map_err(|_| WindowsTextSelectionError::ClipboardDataUnavailable)?;
        if handle.is_invalid() {
            return Err(WindowsTextSelectionError::ClipboardDataUnavailable);
        }
        let handle = HGLOBAL(handle.0);

        // Safety: handle belongs to the clipboard and remains valid while clipboard is open.
        let locked = unsafe { GlobalLock(handle) };
        if locked.is_null() {
            return Err(last_error("GlobalLock"));
        }

        // Safety: handle is valid and locked.
        let byte_len = unsafe { GlobalSize(handle) };
        let unit_len = byte_len / 2;
        // Safety: locked points to a CF_UNICODETEXT buffer of at least byte_len bytes.
        let units = unsafe { std::slice::from_raw_parts(locked as *const u16, unit_len) };
        let end = units.iter().position(|unit| *unit == 0).unwrap_or(unit_len);
        let text = String::from_utf16_lossy(&units[..end]);

        global_unlock(handle)?;

        Ok(text)
    }

    fn global_alloc_utf16_text(text: &str) -> Result<HGLOBAL, WindowsTextSelectionError> {
        let mut units = text.encode_utf16().collect::<Vec<_>>();
        units.push(0);
        let byte_len = units.len() * 2;

        // Safety: allocates a movable global memory block for SetClipboardData ownership transfer.
        let handle = unsafe { GlobalAlloc(GMEM_MOVEABLE, byte_len) }
            .map_err(|error| windows_error("GlobalAlloc", error))?;

        // Safety: handle is a valid movable global memory block.
        let locked = unsafe { GlobalLock(handle) };
        if locked.is_null() {
            // Safety: ownership has not been transferred.
            let _ = unsafe { GlobalFree(Some(handle)) };
            return Err(last_error("GlobalLock"));
        }

        // Safety: locked points to byte_len writable bytes.
        unsafe {
            std::ptr::copy_nonoverlapping(units.as_ptr(), locked as *mut u16, units.len());
        }

        if let Err(error) = global_unlock(handle) {
            // Safety: ownership has not been transferred.
            let _ = unsafe { GlobalFree(Some(handle)) };
            return Err(error);
        }

        Ok(handle)
    }

    fn global_unlock(handle: HGLOBAL) -> Result<(), WindowsTextSelectionError> {
        // GlobalUnlock returns zero both on failure and when the lock count reaches zero.
        // GetLastError distinguishes those cases; NO_ERROR means the unlock succeeded.
        unsafe { SetLastError(WIN32_ERROR(0)) };
        if unsafe { GlobalUnlock(handle) }.is_err() {
            let code = unsafe { GetLastError() };
            if code.0 != 0 {
                return Err(WindowsTextSelectionError::NativeCallFailed {
                    operation: "GlobalUnlock",
                    code: code.0 as i32,
                });
            }
        }

        Ok(())
    }

    fn keyboard_input(key: VIRTUAL_KEY, key_up: bool, extra_info: isize) -> INPUT {
        INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT {
                    wVk: key,
                    wScan: 0,
                    dwFlags: if key_up {
                        KEYEVENTF_KEYUP
                    } else {
                        Default::default()
                    },
                    time: 0,
                    dwExtraInfo: extra_info as usize,
                },
            },
        }
    }

    fn is_valid_window(hwnd: HWND) -> bool {
        // Safety: IsWindow only validates a borrowed HWND value.
        !hwnd.0.is_null() && unsafe { IsWindow(Some(hwnd)) }.as_bool()
    }

    fn run_low_level_input_hook_thread(
        event_tx: Sender<LowLevelInputHookEvent>,
        ready_tx: Sender<Result<u32, WindowsTextSelectionError>>,
    ) -> Result<(), WindowsTextSelectionError> {
        {
            let sender = LOW_LEVEL_HOOK_SENDER.get_or_init(|| Mutex::new(None));
            let mut sender = sender
                .lock()
                .map_err(|_| WindowsTextSelectionError::LowLevelHookThreadUnavailable)?;
            if sender.is_some() {
                let _ = ready_tx.send(Err(WindowsTextSelectionError::LowLevelHookAlreadyInstalled));
                return Ok(());
            }
            *sender = Some(event_tx);
        }
        let _sender_guard = HookSenderGuard;

        let mouse_hook = unsafe { SetWindowsHookExW(WH_MOUSE_LL, Some(mouse_hook_proc), None, 0) }
            .map_err(|error| windows_error("SetWindowsHookExW(WH_MOUSE_LL)", error))?;
        let _mouse_hook = HookGuard(mouse_hook);

        let keyboard_hook =
            unsafe { SetWindowsHookExW(WH_KEYBOARD_LL, Some(keyboard_hook_proc), None, 0) }
                .map_err(|error| windows_error("SetWindowsHookExW(WH_KEYBOARD_LL)", error))?;
        let _keyboard_hook = HookGuard(keyboard_hook);

        let mut message = MSG::default();
        let _ = unsafe { PeekMessageW(&mut message, None, 0, 0, PM_NOREMOVE) };

        let thread_id = unsafe { GetCurrentThreadId() };
        if ready_tx.send(Ok(thread_id)).is_err() {
            return Ok(());
        }

        loop {
            let result = unsafe { GetMessageW(&mut message, None, 0, 0) };
            match result.0 {
                -1 => return Err(last_error("GetMessageW")),
                0 => return Ok(()),
                _ => {}
            }
        }
    }

    unsafe extern "system" fn mouse_hook_proc(
        code: i32,
        wparam: windows::Win32::Foundation::WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        if code == HC_ACTION as i32 && lparam.0 != 0 {
            let hook = unsafe { &*(lparam.0 as *const MSLLHOOKSTRUCT) };
            send_low_level_input_event(LowLevelInputHookEvent::Mouse(LowLevelMouseHookEvent {
                message: wparam.0 as u32,
                x: hook.pt.x,
                y: hook.pt.y,
                mouse_data: hook.mouseData,
                flags: hook.flags,
                event_time_ms: hook.time,
                extra_info: hook.dwExtraInfo as isize,
            }));
        }

        unsafe { CallNextHookEx(None, code, wparam, lparam) }
    }

    unsafe extern "system" fn keyboard_hook_proc(
        code: i32,
        wparam: windows::Win32::Foundation::WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        if code == HC_ACTION as i32 && lparam.0 != 0 {
            let hook = unsafe { &*(lparam.0 as *const KBDLLHOOKSTRUCT) };
            send_low_level_input_event(LowLevelInputHookEvent::Keyboard(
                LowLevelKeyboardHookEvent {
                    message: wparam.0 as u32,
                    virtual_key: hook.vkCode,
                    scan_code: hook.scanCode,
                    flags: hook.flags.0,
                    event_time_ms: hook.time,
                    extra_info: hook.dwExtraInfo as isize,
                },
            ));
        }

        unsafe { CallNextHookEx(None, code, wparam, lparam) }
    }

    fn send_low_level_input_event(event: LowLevelInputHookEvent) {
        let Some(sender) = LOW_LEVEL_HOOK_SENDER.get() else {
            return;
        };
        let Ok(sender) = sender.lock() else {
            return;
        };
        if let Some(sender) = sender.as_ref() {
            let _ = sender.send(event);
        }
    }

    fn uia_error(
        operation: &'static str,
        code: windows::core::HRESULT,
    ) -> WindowsTextSelectionError {
        WindowsTextSelectionError::UiaFailed {
            operation,
            message: code.message(),
        }
    }

    fn windows_error(
        operation: &'static str,
        error: windows::core::Error,
    ) -> WindowsTextSelectionError {
        WindowsTextSelectionError::NativeCallFailed {
            operation,
            code: error.code().0,
        }
    }

    fn last_error(operation: &'static str) -> WindowsTextSelectionError {
        let code = unsafe { GetLastError() };
        WindowsTextSelectionError::NativeCallFailed {
            operation,
            code: code.0 as i32,
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use super::{
        ClipboardTextSnapshot, ForegroundTextSelectionTarget, LowLevelInputHookEvent,
        TextInsertionTarget, WindowsTextSelectionError,
    };
    use std::sync::mpsc::{self, Receiver, TryRecvError};

    pub struct LowLevelInputHook {
        events: Receiver<LowLevelInputHookEvent>,
    }

    impl LowLevelInputHook {
        pub fn events(&self) -> &Receiver<LowLevelInputHookEvent> {
            &self.events
        }

        pub fn try_recv(&self) -> Result<LowLevelInputHookEvent, TryRecvError> {
            self.events.try_recv()
        }
    }

    pub fn start_low_level_input_hook() -> Result<LowLevelInputHook, WindowsTextSelectionError> {
        let (_tx, _events) = mpsc::channel::<LowLevelInputHookEvent>();
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn system_double_click_time_ms() -> u64 {
        500
    }

    pub fn current_tick_count_ms() -> i64 {
        0
    }

    pub fn point_targets_window_root(
        _x: i32,
        _y: i32,
        _hwnd: isize,
    ) -> Result<bool, WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn foreground_text_selection_target(
    ) -> Result<ForegroundTextSelectionTarget, WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn foreground_text_insertion_target(
    ) -> Result<TextInsertionTarget, WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn text_insertion_target_is_valid(_target: &TextInsertionTarget) -> bool {
        false
    }

    pub fn selected_text_via_uia() -> Result<Option<String>, WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn selected_text_via_uia_with_timeout(
        _timeout_ms: u64,
    ) -> Result<Option<String>, WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn process_name_for_id(
        _process_id: u32,
    ) -> Result<Option<String>, WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn clipboard_sequence_number() -> u32 {
        0
    }

    pub fn clipboard_text_snapshot() -> Result<ClipboardTextSnapshot, WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn set_clipboard_text(_text: &str) -> Result<(), WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn clear_clipboard() -> Result<(), WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn focus_window_and_send_ctrl_c(
        _hwnd: isize,
        _extra_info: isize,
    ) -> Result<(), WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn focus_window_and_send_ctrl_v(
        _hwnd: isize,
        _extra_info: isize,
    ) -> Result<(), WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn send_ctrl_c(_extra_info: isize) -> Result<(), WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn send_ctrl_v(_extra_info: isize) -> Result<(), WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }

    pub fn insert_text_into_target(
        _target: &TextInsertionTarget,
        _text: &str,
        _extra_info: isize,
    ) -> Result<(), WindowsTextSelectionError> {
        Err(WindowsTextSelectionError::UnsupportedPlatform)
    }
}

pub use platform::{
    clear_clipboard, clipboard_sequence_number, clipboard_text_snapshot, current_tick_count_ms,
    focus_window_and_send_ctrl_c, focus_window_and_send_ctrl_v, foreground_text_insertion_target,
    foreground_text_selection_target, insert_text_into_target, point_targets_window_root,
    process_name_for_id, selected_text_via_uia, selected_text_via_uia_with_timeout, send_ctrl_c,
    send_ctrl_v, set_clipboard_text, start_low_level_input_hook, system_double_click_time_ms,
    text_insertion_target_is_valid, LowLevelInputHook,
};

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unsupported_platform_error_is_descriptive() {
        if cfg!(windows) {
            return;
        }

        assert_eq!(
            WindowsTextSelectionError::UnsupportedPlatform.to_string(),
            "Windows text selection is only available on Windows"
        );
    }

    #[test]
    fn uia_busy_and_timeout_errors_are_descriptive() {
        assert_eq!(
            WindowsTextSelectionError::UiaBusy { timeout_ms: 200 }.to_string(),
            "UI Automation selection is busy after waiting 200ms"
        );
        assert_eq!(
            WindowsTextSelectionError::UiaTimedOut { timeout_ms: 800 }.to_string(),
            "UI Automation selection timed out after 800ms"
        );
    }

    #[test]
    fn process_name_from_image_path_strips_directory_and_exe_suffix() {
        assert_eq!(
            process_name_from_image_path(r"C:\Program Files\PowerShell\7\pwsh.exe").as_deref(),
            Some("pwsh")
        );
        assert_eq!(
            process_name_from_image_path(r"C:\Tools\MobaXterm_Personal_26.2.ExE").as_deref(),
            Some("MobaXterm_Personal_26.2")
        );
        assert_eq!(
            process_name_from_image_path("notepad").as_deref(),
            Some("notepad")
        );
        assert_eq!(process_name_from_image_path("   "), None);
    }

    #[test]
    fn low_level_input_event_shapes_preserve_raw_hook_fields() {
        let mouse = LowLevelInputHookEvent::Mouse(LowLevelMouseHookEvent {
            message: 0x0201,
            x: 10,
            y: 20,
            mouse_data: 30,
            flags: 40,
            event_time_ms: 50,
            extra_info: 60,
        });
        assert_eq!(
            mouse,
            LowLevelInputHookEvent::Mouse(LowLevelMouseHookEvent {
                message: 0x0201,
                x: 10,
                y: 20,
                mouse_data: 30,
                flags: 40,
                event_time_ms: 50,
                extra_info: 60,
            })
        );

        let keyboard = LowLevelInputHookEvent::Keyboard(LowLevelKeyboardHookEvent {
            message: 0x0100,
            virtual_key: 0x43,
            scan_code: 46,
            flags: 16,
            event_time_ms: 70,
            extra_info: 80,
        });
        assert_eq!(
            keyboard,
            LowLevelInputHookEvent::Keyboard(LowLevelKeyboardHookEvent {
                message: 0x0100,
                virtual_key: 0x43,
                scan_code: 46,
                flags: 16,
                event_time_ms: 70,
                extra_info: 80,
            })
        );
    }

    #[cfg(not(windows))]
    #[test]
    fn low_level_input_hook_is_unsupported_off_windows() {
        let error = match start_low_level_input_hook() {
            Err(error) => error,
            Ok(_) => panic!("hook should be Windows-only"),
        };
        assert_eq!(
            error.to_string(),
            "Windows text selection is only available on Windows"
        );
        assert_eq!(system_double_click_time_ms(), 500);
        assert_eq!(current_tick_count_ms(), 0);
        assert!(point_targets_window_root(0, 0, 0).is_err());
    }

    #[cfg(windows)]
    #[test]
    fn real_notepad_uia_smoke_reads_selection_when_enabled() {
        if !windows_uia_text_selection_smoke_enabled() {
            return;
        }

        let _guard = WINDOWS_SMOKE_LOCK.lock().expect("smoke lock");
        let mut notepad = NotepadFileSmokeProcess::start("easydict-uia-smoke")
            .expect("notepad UIA smoke should start");

        let hwnd = notepad
            .wait_for_window(std::time::Duration::from_secs(5))
            .expect("notepad UIA smoke should find its document window");
        focus_smoke_window(hwnd);
        send_ctrl_a_to_smoke_window(hwnd);
        std::thread::sleep(std::time::Duration::from_millis(200));
        let selected = selected_text_via_uia_with_timeout(DEFAULT_UIA_EXECUTION_TIMEOUT_MS)
            .expect("UIA should read the Notepad selection");
        assert_eq!(selected.as_deref(), Some("easydict-uia-smoke"));

        notepad.close_without_prompt(hwnd);
    }

    #[cfg(windows)]
    #[test]
    fn real_low_level_input_hook_smoke_when_enabled() {
        if !windows_mouse_selection_hook_smoke_enabled() {
            return;
        }

        let _guard = WINDOWS_SMOKE_LOCK.lock().expect("smoke lock");
        let hook = start_low_level_input_hook().expect("low-level input hook should install");
        let mouse_extra_info = unique_smoke_extra_info(0x01);
        let keyboard_extra_info = unique_smoke_extra_info(0x02);

        send_smoke_mouse_move(mouse_extra_info);
        send_smoke_f24_key(keyboard_extra_info);

        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(3);
        let mut saw_mouse = false;
        let mut saw_keyboard = false;
        while !(saw_mouse && saw_keyboard) {
            let now = std::time::Instant::now();
            if now >= deadline {
                break;
            }

            let remaining = deadline.saturating_duration_since(now);
            match hook.events().recv_timeout(remaining) {
                Ok(LowLevelInputHookEvent::Mouse(event)) => {
                    if event.extra_info == mouse_extra_info {
                        saw_mouse =
                            event.message == windows::Win32::UI::WindowsAndMessaging::WM_MOUSEMOVE;
                    }
                }
                Ok(LowLevelInputHookEvent::Keyboard(event)) => {
                    if event.extra_info == keyboard_extra_info {
                        saw_keyboard = event.virtual_key == 0x87
                            && matches!(
                                event.message,
                                windows::Win32::UI::WindowsAndMessaging::WM_KEYDOWN
                                    | windows::Win32::UI::WindowsAndMessaging::WM_KEYUP
                            );
                    }
                }
                Err(std::sync::mpsc::RecvTimeoutError::Timeout) => break,
                Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => {
                    panic!("low-level input hook channel disconnected")
                }
            }
        }

        assert!(saw_mouse, "low-level mouse hook should receive SendInput");
        assert!(
            saw_keyboard,
            "low-level keyboard hook should receive SendInput"
        );
    }

    #[cfg(windows)]
    #[test]
    fn real_edit_control_smoke_copies_selection_and_inserts_text_when_enabled() {
        if !windows_text_selection_smoke_enabled() {
            return;
        }

        let _guard = WINDOWS_SMOKE_LOCK.lock().expect("smoke lock");
        let _clipboard_guard = ClipboardRestoreGuard::capture();
        let window = TestEditWindow::new("alpha beta gamma");
        window.focus();
        window.select_range(6, 10);

        focus_window_and_send_ctrl_c(window.hwnd.0 as isize, 0).expect("copy selected smoke text");
        pump_test_window_messages(std::time::Duration::from_millis(250));
        std::thread::sleep(std::time::Duration::from_millis(150));
        let selected = clipboard_text_snapshot().expect("clipboard should contain smoke text");
        assert_eq!(selected.text.as_deref(), Some("beta"));

        let target =
            foreground_text_insertion_target().expect("smoke edit control should be foreground");
        insert_text_into_target(&target, "delta", 0).expect("insert into smoke edit control");

        pump_test_window_messages(std::time::Duration::from_millis(250));
        std::thread::sleep(std::time::Duration::from_millis(150));
        assert_eq!(window.text(), "alpha delta gamma");
    }

    #[cfg(windows)]
    #[test]
    fn real_notepad_smoke_inserts_text_when_enabled() {
        if !windows_notepad_text_insertion_smoke_enabled() {
            return;
        }

        let _guard = WINDOWS_SMOKE_LOCK.lock().expect("smoke lock");
        let _clipboard_guard = ClipboardRestoreGuard::capture();
        let mut notepad = NotepadSmokeProcess::start().expect("notepad smoke should start Notepad");

        let hwnd = notepad
            .wait_for_window(std::time::Duration::from_secs(5))
            .expect("notepad smoke should find Notepad window");
        focus_smoke_window(hwnd);
        let target =
            foreground_text_insertion_target().expect("notepad should be insertion foreground");

        insert_text_into_target(&target, "easydict-notepad-smoke", 0).expect("insert into Notepad");
        std::thread::sleep(std::time::Duration::from_millis(200));

        send_ctrl_a_to_smoke_window(hwnd);
        focus_window_and_send_ctrl_c(hwnd.0 as isize, 0).expect("copy Notepad text");
        std::thread::sleep(std::time::Duration::from_millis(200));
        let copied = clipboard_text_snapshot().expect("clipboard should contain Notepad text");
        assert_eq!(copied.text.as_deref(), Some("easydict-notepad-smoke"));

        notepad.close_without_prompt(hwnd);
    }

    #[cfg(windows)]
    #[test]
    fn real_vscode_smoke_inserts_text_when_enabled() {
        if !windows_vscode_text_insertion_smoke_enabled() {
            return;
        }

        let _guard = WINDOWS_SMOKE_LOCK.lock().expect("smoke lock");
        let _clipboard_guard = ClipboardRestoreGuard::capture();
        let mut vscode =
            VsCodeSmokeWorkspace::start().expect("VS Code insertion smoke should start");

        let hwnd = vscode
            .wait_for_window(std::time::Duration::from_secs(20))
            .expect("VS Code insertion smoke should find its document window");
        std::thread::sleep(std::time::Duration::from_secs(3));
        focus_smoke_window(hwnd);
        send_ctrl_key_to_smoke_window(hwnd, 0x31);
        click_vscode_editor_first_line(hwnd);
        send_ctrl_key_to_smoke_window(hwnd, 0x31);
        let target = foreground_text_insertion_target()
            .expect("VS Code document window should be insertion foreground");

        insert_text_into_target(&target, "easydict-vscode-smoke", 0).expect("insert into VS Code");
        std::thread::sleep(std::time::Duration::from_millis(300));
        set_clipboard_text("easydict-vscode-sentinel").expect("set VS Code smoke sentinel");
        send_ctrl_a_to_smoke_window(hwnd);
        focus_window_and_send_ctrl_c(hwnd.0 as isize, 0).expect("copy VS Code editor text");
        std::thread::sleep(std::time::Duration::from_millis(300));
        let copied = clipboard_text_snapshot().expect("VS Code editor text should copy");
        assert!(
            copied
                .text
                .as_deref()
                .is_some_and(|text| text.contains("easydict-vscode-smoke")),
            "VS Code editor should contain inserted text, copied {:?}",
            copied.text
        );

        send_ctrl_key_to_foreground(0x53);
        std::thread::sleep(std::time::Duration::from_millis(300));

        vscode.close_without_prompt(hwnd);
    }

    #[cfg(windows)]
    static WINDOWS_SMOKE_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

    #[cfg(windows)]
    struct ClipboardRestoreGuard(Option<ClipboardTextSnapshot>);

    #[cfg(windows)]
    impl ClipboardRestoreGuard {
        fn capture() -> Self {
            Self(clipboard_text_snapshot().ok())
        }
    }

    #[cfg(windows)]
    impl Drop for ClipboardRestoreGuard {
        fn drop(&mut self) {
            match self
                .0
                .as_ref()
                .and_then(|snapshot| snapshot.text.as_deref())
            {
                Some(text) => {
                    let _ = set_clipboard_text(text);
                }
                None => {
                    let _ = clear_clipboard();
                }
            }
        }
    }

    #[cfg(windows)]
    fn windows_text_selection_smoke_enabled() -> bool {
        std::env::var("EASYDICT_WINDOWS_TEXT_SELECTION_SMOKE")
            .map(|value| {
                matches!(
                    value.trim().to_ascii_lowercase().as_str(),
                    "1" | "true" | "yes" | "on"
                )
            })
            .unwrap_or(false)
    }

    #[cfg(windows)]
    fn windows_uia_text_selection_smoke_enabled() -> bool {
        std::env::var("EASYDICT_WINDOWS_UIA_TEXT_SELECTION_SMOKE")
            .map(|value| {
                matches!(
                    value.trim().to_ascii_lowercase().as_str(),
                    "1" | "true" | "yes" | "on"
                )
            })
            .unwrap_or(false)
    }

    #[cfg(windows)]
    fn windows_notepad_text_insertion_smoke_enabled() -> bool {
        std::env::var("EASYDICT_WINDOWS_TEXT_INSERTION_NOTEPAD_SMOKE")
            .map(|value| {
                matches!(
                    value.trim().to_ascii_lowercase().as_str(),
                    "1" | "true" | "yes" | "on"
                )
            })
            .unwrap_or(false)
    }

    #[cfg(windows)]
    fn windows_vscode_text_insertion_smoke_enabled() -> bool {
        std::env::var("EASYDICT_WINDOWS_TEXT_INSERTION_VSCODE_SMOKE")
            .map(|value| {
                matches!(
                    value.trim().to_ascii_lowercase().as_str(),
                    "1" | "true" | "yes" | "on"
                )
            })
            .unwrap_or(false)
    }

    #[cfg(windows)]
    fn windows_mouse_selection_hook_smoke_enabled() -> bool {
        std::env::var("EASYDICT_WINDOWS_MOUSE_SELECTION_HOOK_SMOKE")
            .map(|value| {
                matches!(
                    value.trim().to_ascii_lowercase().as_str(),
                    "1" | "true" | "yes" | "on"
                )
            })
            .unwrap_or(false)
    }

    #[cfg(windows)]
    fn unique_smoke_extra_info(tag: isize) -> isize {
        let pid = isize::try_from(std::process::id() & 0x0fff).unwrap_or_default();
        0x0455_0000isize | (pid << 4) | tag
    }

    #[cfg(windows)]
    fn send_smoke_mouse_move(extra_info: isize) {
        use windows::Win32::UI::Input::KeyboardAndMouse::{
            SendInput, INPUT, INPUT_0, INPUT_MOUSE, MOUSEEVENTF_MOVE, MOUSEINPUT,
        };

        let input = INPUT {
            r#type: INPUT_MOUSE,
            Anonymous: INPUT_0 {
                mi: MOUSEINPUT {
                    dx: 1,
                    dy: 0,
                    mouseData: 0,
                    dwFlags: MOUSEEVENTF_MOVE,
                    time: 0,
                    dwExtraInfo: extra_info as usize,
                },
            },
        };

        let sent = unsafe { SendInput(&[input], std::mem::size_of::<INPUT>() as i32) };
        assert_eq!(sent, 1, "SendInput should inject a smoke mouse move");
    }

    #[cfg(windows)]
    fn send_smoke_f24_key(extra_info: isize) {
        use windows::Win32::UI::Input::KeyboardAndMouse::{
            SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, VIRTUAL_KEY,
        };

        let virtual_key_f24 = VIRTUAL_KEY(0x87);
        let inputs = [
            INPUT {
                r#type: INPUT_KEYBOARD,
                Anonymous: INPUT_0 {
                    ki: KEYBDINPUT {
                        wVk: virtual_key_f24,
                        wScan: 0,
                        dwFlags: Default::default(),
                        time: 0,
                        dwExtraInfo: extra_info as usize,
                    },
                },
            },
            INPUT {
                r#type: INPUT_KEYBOARD,
                Anonymous: INPUT_0 {
                    ki: KEYBDINPUT {
                        wVk: virtual_key_f24,
                        wScan: 0,
                        dwFlags: KEYEVENTF_KEYUP,
                        time: 0,
                        dwExtraInfo: extra_info as usize,
                    },
                },
            },
        ];

        let sent = unsafe { SendInput(&inputs, std::mem::size_of::<INPUT>() as i32) };
        assert_eq!(
            sent,
            inputs.len() as u32,
            "SendInput should inject a smoke keyboard event"
        );
    }

    #[cfg(windows)]
    fn pump_test_window_messages(duration: std::time::Duration) {
        use windows::Win32::UI::WindowsAndMessaging::{
            DispatchMessageW, PeekMessageW, TranslateMessage, MSG, PM_REMOVE,
        };

        let deadline = std::time::Instant::now() + duration;
        while std::time::Instant::now() < deadline {
            let mut dispatched = false;
            loop {
                let mut message = MSG::default();
                let has_message =
                    unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool();
                if !has_message {
                    break;
                }

                dispatched = true;
                unsafe {
                    let _ = TranslateMessage(&message);
                    let _ = DispatchMessageW(&message);
                }
            }

            if !dispatched {
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
        }
    }

    #[cfg(windows)]
    struct TestEditWindow {
        hwnd: windows::Win32::Foundation::HWND,
    }

    #[cfg(windows)]
    impl TestEditWindow {
        fn new(text: &str) -> Self {
            use windows::core::PCWSTR;
            use windows::Win32::UI::WindowsAndMessaging::{
                CreateWindowExW, ShowWindow, ES_LEFT, SW_SHOW, WINDOW_EX_STYLE, WINDOW_STYLE,
                WS_OVERLAPPEDWINDOW, WS_VISIBLE,
            };

            let class_name = wide_null("EDIT");
            let title = wide_null(text);
            let hwnd = unsafe {
                CreateWindowExW(
                    WINDOW_EX_STYLE::default(),
                    PCWSTR(class_name.as_ptr()),
                    PCWSTR(title.as_ptr()),
                    WS_OVERLAPPEDWINDOW | WS_VISIBLE | WINDOW_STYLE(ES_LEFT as u32),
                    64,
                    64,
                    480,
                    120,
                    None,
                    None,
                    None,
                    None,
                )
            }
            .expect("create smoke edit control");
            unsafe {
                let _ = ShowWindow(hwnd, SW_SHOW);
            }

            Self { hwnd }
        }

        fn focus(&self) {
            use windows::Win32::UI::Input::KeyboardAndMouse::{SetActiveWindow, SetFocus};
            use windows::Win32::UI::WindowsAndMessaging::{BringWindowToTop, SetForegroundWindow};

            unsafe {
                let _ = BringWindowToTop(self.hwnd);
                let _ = SetForegroundWindow(self.hwnd);
                let _ = SetActiveWindow(self.hwnd);
                let _ = SetFocus(Some(self.hwnd));
            }
            std::thread::sleep(std::time::Duration::from_millis(150));
        }

        fn select_range(&self, start: usize, end: usize) {
            use windows::Win32::Foundation::{LPARAM, WPARAM};
            use windows::Win32::UI::WindowsAndMessaging::SendMessageW;

            const EM_SETSEL: u32 = 0x00B1;

            unsafe {
                let _ = SendMessageW(
                    self.hwnd,
                    EM_SETSEL,
                    Some(WPARAM(start)),
                    Some(LPARAM(end as isize)),
                );
            }
            std::thread::sleep(std::time::Duration::from_millis(100));
        }

        fn text(&self) -> String {
            use windows::Win32::UI::WindowsAndMessaging::{GetWindowTextLengthW, GetWindowTextW};

            let length = unsafe { GetWindowTextLengthW(self.hwnd) };
            let mut buffer = vec![0u16; length as usize + 1];
            let copied = unsafe { GetWindowTextW(self.hwnd, &mut buffer) };
            String::from_utf16_lossy(&buffer[..copied as usize])
        }
    }

    #[cfg(windows)]
    impl Drop for TestEditWindow {
        fn drop(&mut self) {
            unsafe {
                let _ = windows::Win32::UI::WindowsAndMessaging::DestroyWindow(self.hwnd);
            }
        }
    }

    #[cfg(windows)]
    fn wide_null(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }

    #[cfg(windows)]
    struct NotepadFileSmokeProcess {
        child: std::process::Child,
        file_path: std::path::PathBuf,
        title_fragment: String,
        closed: bool,
    }

    #[cfg(windows)]
    impl NotepadFileSmokeProcess {
        fn start(text: &str) -> std::io::Result<Self> {
            let file_path = std::env::temp_dir().join(format!(
                "easydict-uia-smoke-{}-{:?}.txt",
                std::process::id(),
                std::thread::current().id()
            ));
            std::fs::write(&file_path, text)?;
            let title_fragment = file_path
                .file_name()
                .and_then(|name| name.to_str())
                .unwrap_or("easydict-uia-smoke")
                .to_string();
            let child = std::process::Command::new("notepad.exe")
                .arg(&file_path)
                .spawn()?;
            Ok(Self {
                child,
                file_path,
                title_fragment,
                closed: false,
            })
        }

        fn wait_for_window(
            &mut self,
            timeout: std::time::Duration,
        ) -> Option<windows::Win32::Foundation::HWND> {
            let deadline = std::time::Instant::now() + timeout;
            while std::time::Instant::now() < deadline {
                if let Some(hwnd) = find_top_level_window_by_title_fragment(&self.title_fragment) {
                    return Some(hwnd);
                }
                std::thread::sleep(std::time::Duration::from_millis(100));
            }
            None
        }

        fn close_without_prompt(&mut self, hwnd: windows::Win32::Foundation::HWND) {
            close_smoke_window(hwnd);
            self.closed = true;
            let _ = self.child.try_wait();
            let _ = std::fs::remove_file(&self.file_path);
        }
    }

    #[cfg(windows)]
    impl Drop for NotepadFileSmokeProcess {
        fn drop(&mut self) {
            if !self.closed {
                if let Some(hwnd) = find_top_level_window_by_title_fragment(&self.title_fragment) {
                    close_smoke_window(hwnd);
                }
                let _ = self.child.kill();
                let _ = self.child.wait();
                let _ = std::fs::remove_file(&self.file_path);
            }
        }
    }

    #[cfg(windows)]
    struct VsCodeSmokeWorkspace {
        root_dir: std::path::PathBuf,
        title_fragment: String,
        closed: bool,
    }

    #[cfg(windows)]
    impl VsCodeSmokeWorkspace {
        fn start() -> std::io::Result<Self> {
            let code_command = vscode_command_path()?;
            let root_dir = std::env::temp_dir().join(format!(
                "easydict-vscode-smoke-{}-{:?}",
                std::process::id(),
                std::thread::current().id()
            ));
            std::fs::create_dir_all(&root_dir)?;
            let file_path = root_dir.join("easydict-vscode-smoke.txt");
            std::fs::write(&file_path, "")?;
            let title_fragment = file_path
                .file_name()
                .and_then(|name| name.to_str())
                .unwrap_or("easydict-vscode-smoke.txt")
                .to_string();

            let mut command = std::process::Command::new(code_command);
            command
                .arg("--new-window")
                .arg("--disable-extensions")
                .arg("--goto")
                .arg(format!("{}:1:1", file_path.display()));
            let _ = command.spawn()?;

            Ok(Self {
                root_dir,
                title_fragment,
                closed: false,
            })
        }

        fn wait_for_window(
            &mut self,
            timeout: std::time::Duration,
        ) -> Option<windows::Win32::Foundation::HWND> {
            let deadline = std::time::Instant::now() + timeout;
            while std::time::Instant::now() < deadline {
                if let Some(hwnd) = find_top_level_window_by_title_fragment(&self.title_fragment) {
                    return Some(hwnd);
                }
                std::thread::sleep(std::time::Duration::from_millis(250));
            }
            None
        }

        fn close_without_prompt(&mut self, hwnd: windows::Win32::Foundation::HWND) {
            close_smoke_window(hwnd);
            self.closed = true;
            let _ = std::fs::remove_dir_all(&self.root_dir);
        }
    }

    #[cfg(windows)]
    impl Drop for VsCodeSmokeWorkspace {
        fn drop(&mut self) {
            if !self.closed {
                if let Some(hwnd) = find_top_level_window_by_title_fragment(&self.title_fragment) {
                    close_smoke_window(hwnd);
                }
                let _ = std::fs::remove_dir_all(&self.root_dir);
            }
        }
    }

    #[cfg(windows)]
    fn vscode_command_path() -> std::io::Result<std::path::PathBuf> {
        if let Some(path) = std::env::var_os("EASYDICT_WINDOWS_TEXT_INSERTION_VSCODE_CODE") {
            return Ok(std::path::PathBuf::from(path));
        }

        let path = std::process::Command::new("where.exe")
            .arg("code.cmd")
            .output()
            .and_then(|output| {
                if output.status.success() {
                    Ok(output)
                } else {
                    Err(std::io::Error::new(
                        std::io::ErrorKind::NotFound,
                        "code.cmd was not found on PATH",
                    ))
                }
            })?;
        let stdout = String::from_utf8_lossy(&path.stdout);
        stdout
            .lines()
            .map(str::trim)
            .filter(|line| !line.is_empty())
            .map(std::path::PathBuf::from)
            .next()
            .ok_or_else(|| {
                std::io::Error::new(
                    std::io::ErrorKind::NotFound,
                    "where.exe returned no code.cmd path",
                )
            })
    }

    #[cfg(windows)]
    struct NotepadSmokeProcess {
        child: std::process::Child,
        closed: bool,
    }

    #[cfg(windows)]
    impl NotepadSmokeProcess {
        fn start() -> std::io::Result<Self> {
            let child = std::process::Command::new("notepad.exe").spawn()?;
            Ok(Self {
                child,
                closed: false,
            })
        }

        fn wait_for_window(
            &mut self,
            timeout: std::time::Duration,
        ) -> Option<windows::Win32::Foundation::HWND> {
            let deadline = std::time::Instant::now() + timeout;
            while std::time::Instant::now() < deadline {
                if let Some(hwnd) = find_top_level_window_for_process(self.child.id()) {
                    return Some(hwnd);
                }
                std::thread::sleep(std::time::Duration::from_millis(100));
            }
            None
        }

        fn close_without_prompt(&mut self, hwnd: windows::Win32::Foundation::HWND) {
            let _ = hwnd;
            let _ = self.child.kill();
            self.closed = true;
            let _ = self.child.wait();
        }
    }

    #[cfg(windows)]
    impl Drop for NotepadSmokeProcess {
        fn drop(&mut self) {
            if !self.closed {
                let _ = self.child.kill();
                let _ = self.child.wait();
            }
        }
    }

    #[cfg(windows)]
    fn find_top_level_window_for_process(
        process_id: u32,
    ) -> Option<windows::Win32::Foundation::HWND> {
        use windows::core::BOOL;
        use windows::Win32::Foundation::{HWND, LPARAM};
        use windows::Win32::UI::WindowsAndMessaging::{
            EnumWindows, GetWindowThreadProcessId, IsWindowVisible,
        };

        unsafe extern "system" fn enum_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
            let state = &mut *(lparam.0 as *mut FindWindowState);
            let mut window_process_id = 0u32;
            unsafe {
                GetWindowThreadProcessId(hwnd, Some(&mut window_process_id));
            }
            if window_process_id == state.process_id && unsafe { IsWindowVisible(hwnd) }.as_bool() {
                state.hwnd = Some(hwnd);
                return BOOL(0);
            }
            BOOL(1)
        }

        struct FindWindowState {
            process_id: u32,
            hwnd: Option<windows::Win32::Foundation::HWND>,
        }

        let mut state = FindWindowState {
            process_id,
            hwnd: None,
        };
        let _ = unsafe { EnumWindows(Some(enum_proc), LPARAM(&mut state as *mut _ as isize)) };
        state.hwnd
    }

    #[cfg(windows)]
    fn find_top_level_window_by_title_fragment(
        fragment: &str,
    ) -> Option<windows::Win32::Foundation::HWND> {
        use windows::core::BOOL;
        use windows::Win32::Foundation::{HWND, LPARAM};
        use windows::Win32::UI::WindowsAndMessaging::{
            EnumWindows, GetWindowTextLengthW, GetWindowTextW, IsWindowVisible,
        };

        unsafe extern "system" fn enum_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
            let state = &mut *(lparam.0 as *mut FindWindowByTitleState);
            if unsafe { IsWindowVisible(hwnd) }.as_bool() {
                let length = unsafe { GetWindowTextLengthW(hwnd) };
                if length > 0 {
                    let mut buffer = vec![0u16; length as usize + 1];
                    let copied = unsafe { GetWindowTextW(hwnd, &mut buffer) };
                    let title = String::from_utf16_lossy(&buffer[..copied as usize]);
                    if title.contains(&state.fragment) {
                        state.hwnd = Some(hwnd);
                        return BOOL(0);
                    }
                }
            }
            BOOL(1)
        }

        struct FindWindowByTitleState {
            fragment: String,
            hwnd: Option<windows::Win32::Foundation::HWND>,
        }

        let mut state = FindWindowByTitleState {
            fragment: fragment.to_string(),
            hwnd: None,
        };
        let _ = unsafe { EnumWindows(Some(enum_proc), LPARAM(&mut state as *mut _ as isize)) };
        state.hwnd
    }

    #[cfg(windows)]
    fn close_smoke_window(hwnd: windows::Win32::Foundation::HWND) {
        use windows::Win32::Foundation::{LPARAM, WPARAM};
        use windows::Win32::UI::WindowsAndMessaging::{PostMessageW, WM_CLOSE};

        unsafe {
            let _ = PostMessageW(Some(hwnd), WM_CLOSE, WPARAM(0), LPARAM(0));
        }
        std::thread::sleep(std::time::Duration::from_millis(300));
    }

    #[cfg(windows)]
    fn focus_smoke_window(hwnd: windows::Win32::Foundation::HWND) {
        use windows::Win32::UI::Input::KeyboardAndMouse::{SetActiveWindow, SetFocus};
        use windows::Win32::UI::WindowsAndMessaging::{BringWindowToTop, SetForegroundWindow};

        unsafe {
            let _ = BringWindowToTop(hwnd);
            let _ = SetForegroundWindow(hwnd);
            let _ = SetActiveWindow(hwnd);
            let _ = SetFocus(Some(hwnd));
        }
        std::thread::sleep(std::time::Duration::from_millis(250));
    }

    #[cfg(windows)]
    fn click_vscode_editor_first_line(hwnd: windows::Win32::Foundation::HWND) {
        use windows::Win32::Foundation::RECT;
        use windows::Win32::UI::Input::KeyboardAndMouse::{
            SendInput, INPUT, INPUT_0, INPUT_MOUSE, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP,
            MOUSEINPUT,
        };
        use windows::Win32::UI::WindowsAndMessaging::{GetWindowRect, SetCursorPos};

        let mut rect = RECT::default();
        let ok = unsafe { GetWindowRect(hwnd, &mut rect) }.is_ok();
        assert!(ok, "smoke window rect should be available");
        let width = rect.right - rect.left;
        let height = rect.bottom - rect.top;
        let x = rect.left + (width / 3).max(280);
        let y = rect.top + (height / 4).max(220);
        unsafe {
            let _ = SetCursorPos(x, y);
        }
        std::thread::sleep(std::time::Duration::from_millis(100));

        let inputs = [
            INPUT {
                r#type: INPUT_MOUSE,
                Anonymous: INPUT_0 {
                    mi: MOUSEINPUT {
                        dx: 0,
                        dy: 0,
                        mouseData: 0,
                        dwFlags: MOUSEEVENTF_LEFTDOWN,
                        time: 0,
                        dwExtraInfo: 0,
                    },
                },
            },
            INPUT {
                r#type: INPUT_MOUSE,
                Anonymous: INPUT_0 {
                    mi: MOUSEINPUT {
                        dx: 0,
                        dy: 0,
                        mouseData: 0,
                        dwFlags: MOUSEEVENTF_LEFTUP,
                        time: 0,
                        dwExtraInfo: 0,
                    },
                },
            },
        ];
        let sent = unsafe { SendInput(&inputs, std::mem::size_of::<INPUT>() as i32) };
        assert_eq!(
            sent,
            inputs.len() as u32,
            "smoke click should reach the editor"
        );
        std::thread::sleep(std::time::Duration::from_millis(250));
    }

    #[cfg(windows)]
    fn send_ctrl_a_to_smoke_window(hwnd: windows::Win32::Foundation::HWND) {
        send_ctrl_key_to_smoke_window(hwnd, 0x41);
    }

    #[cfg(windows)]
    fn send_ctrl_key_to_smoke_window(hwnd: windows::Win32::Foundation::HWND, virtual_key: u16) {
        use windows::Win32::UI::Input::KeyboardAndMouse::{
            KEYEVENTF_KEYUP, VIRTUAL_KEY, VK_CONTROL,
        };

        focus_smoke_window(hwnd);
        send_key_to_foreground(VK_CONTROL, 0);
        send_key_to_foreground(VIRTUAL_KEY(virtual_key), 0);
        send_key_to_foreground(VIRTUAL_KEY(virtual_key), KEYEVENTF_KEYUP.0);
        send_key_to_foreground(VIRTUAL_KEY(VK_CONTROL.0), KEYEVENTF_KEYUP.0);
    }

    #[cfg(windows)]
    fn send_ctrl_key_to_foreground(virtual_key: u16) {
        use windows::Win32::UI::Input::KeyboardAndMouse::{
            KEYEVENTF_KEYUP, VIRTUAL_KEY, VK_CONTROL,
        };

        send_key_to_foreground(VK_CONTROL, 0);
        send_key_to_foreground(VIRTUAL_KEY(virtual_key), 0);
        send_key_to_foreground(VIRTUAL_KEY(virtual_key), KEYEVENTF_KEYUP.0);
        send_key_to_foreground(VIRTUAL_KEY(VK_CONTROL.0), KEYEVENTF_KEYUP.0);
    }

    #[cfg(windows)]
    fn send_key_to_foreground(
        key: windows::Win32::UI::Input::KeyboardAndMouse::VIRTUAL_KEY,
        flags: u32,
    ) {
        use windows::Win32::UI::Input::KeyboardAndMouse::{
            SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT,
        };

        let input = INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT {
                    wVk: key,
                    wScan: 0,
                    dwFlags: windows::Win32::UI::Input::KeyboardAndMouse::KEYBD_EVENT_FLAGS(flags),
                    time: 0,
                    dwExtraInfo: 0,
                },
            },
        };
        unsafe {
            let _ = SendInput(&[input], std::mem::size_of::<INPUT>() as i32);
        }
    }
}

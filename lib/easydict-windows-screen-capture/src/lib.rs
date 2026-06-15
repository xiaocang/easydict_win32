#![cfg_attr(not(windows), forbid(unsafe_code))]

use std::fmt;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ScreenRect {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

impl ScreenRect {
    pub const fn new(x: i32, y: i32, width: u32, height: u32) -> Self {
        Self {
            x,
            y,
            width,
            height,
        }
    }

    pub const fn is_empty(self) -> bool {
        self.width == 0 || self.height == 0
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ScreenCaptureRequest {
    pub region: Option<ScreenRect>,
}

impl ScreenCaptureRequest {
    pub const fn virtual_desktop() -> Self {
        Self { region: None }
    }

    pub const fn region(region: ScreenRect) -> Self {
        Self {
            region: Some(region),
        }
    }
}

impl Default for ScreenCaptureRequest {
    fn default() -> Self {
        Self::virtual_desktop()
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScreenCaptureResult {
    pub pixel_data_path: String,
    pub pixel_width: u32,
    pub pixel_height: u32,
    pub screen_rect: ScreenRect,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScreenWindow {
    pub id: isize,
    pub parent_id: Option<isize>,
    pub rect: ScreenRect,
    pub class_name: String,
}

impl ScreenWindow {
    pub fn new(id: isize, parent_id: Option<isize>, rect: ScreenRect) -> Self {
        Self {
            id,
            parent_id,
            rect,
            class_name: String::new(),
        }
    }

    pub fn class_name(mut self, class_name: impl Into<String>) -> Self {
        self.class_name = class_name.into();
        self
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ScreenWindowSnapshotRequest {
    pub excluded_titles: Vec<String>,
}

impl ScreenWindowSnapshotRequest {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn exclude_title(mut self, title: impl Into<String>) -> Self {
        self.excluded_titles.push(title.into());
        self
    }
}

#[derive(Debug)]
pub enum WindowsScreenCaptureError {
    UnsupportedPlatform,
    InvalidCaptureRequest(&'static str),
    SizeOverflow,
    Io {
        operation: &'static str,
        message: String,
    },
    NativeCallFailed {
        operation: &'static str,
        code: i32,
    },
}

impl fmt::Display for WindowsScreenCaptureError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsupportedPlatform => {
                formatter.write_str("Windows screen capture is only available on Windows")
            }
            Self::InvalidCaptureRequest(reason) => {
                write!(formatter, "invalid screen capture request: {reason}")
            }
            Self::SizeOverflow => formatter.write_str("screen capture dimensions are too large"),
            Self::Io { operation, message } => write!(formatter, "{operation} failed: {message}"),
            Self::NativeCallFailed { operation, code } => {
                write!(formatter, "{operation} failed with native error {code}")
            }
        }
    }
}

impl std::error::Error for WindowsScreenCaptureError {}

pub fn capture_screen_region(
    request: ScreenCaptureRequest,
) -> Result<ScreenCaptureResult, WindowsScreenCaptureError> {
    platform::capture_screen_region(request)
}

pub fn capture_screen_windows(
    request: ScreenWindowSnapshotRequest,
) -> Result<Vec<ScreenWindow>, WindowsScreenCaptureError> {
    platform::capture_screen_windows(request)
}

#[cfg(windows)]
mod platform {
    use super::{
        ScreenCaptureRequest, ScreenCaptureResult, ScreenRect, ScreenWindow,
        ScreenWindowSnapshotRequest, WindowsScreenCaptureError,
    };
    use std::io::Write;
    use std::time::{SystemTime, UNIX_EPOCH};
    use windows::core::BOOL;
    use windows::Win32::Foundation::{GetLastError, HWND, LPARAM, RECT};
    use windows::Win32::Graphics::Gdi::{
        BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, GetDC,
        GetDIBits, ReleaseDC, SelectObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS,
        HBITMAP, HDC, HGDIOBJ, SRCCOPY,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        EnumChildWindows, EnumWindows, GetClassNameW, GetParent, GetSystemMetrics, GetWindowRect,
        GetWindowTextLengthW, GetWindowTextW, IsWindowVisible, SM_CXVIRTUALSCREEN,
        SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
    };

    struct ScreenDc(HDC);

    impl Drop for ScreenDc {
        fn drop(&mut self) {
            unsafe {
                ReleaseDC(None, self.0);
            }
        }
    }

    struct CompatibleDc(HDC);

    impl Drop for CompatibleDc {
        fn drop(&mut self) {
            unsafe {
                let _ = DeleteDC(self.0);
            }
        }
    }

    struct BitmapHandle(HBITMAP);

    impl Drop for BitmapHandle {
        fn drop(&mut self) {
            unsafe {
                let _ = DeleteObject(self.0.into());
            }
        }
    }

    struct SelectedObject {
        dc: HDC,
        previous: HGDIOBJ,
    }

    impl Drop for SelectedObject {
        fn drop(&mut self) {
            unsafe {
                let _ = SelectObject(self.dc, self.previous);
            }
        }
    }

    pub fn capture_screen_region(
        request: ScreenCaptureRequest,
    ) -> Result<ScreenCaptureResult, WindowsScreenCaptureError> {
        let (x, y, width, height) = screen_capture_rect(request)?;
        let buffer_len = capture_buffer_len(width, height)?;

        let screen_dc = unsafe { GetDC(None) };
        if screen_dc.is_invalid() {
            return Err(last_error("GetDC"));
        }
        let screen_dc = ScreenDc(screen_dc);

        let mem_dc = unsafe { CreateCompatibleDC(Some(screen_dc.0)) };
        if mem_dc.is_invalid() {
            return Err(last_error("CreateCompatibleDC"));
        }
        let mem_dc = CompatibleDc(mem_dc);

        let bitmap = unsafe { CreateCompatibleBitmap(screen_dc.0, width, height) };
        if bitmap.is_invalid() {
            return Err(last_error("CreateCompatibleBitmap"));
        }
        let bitmap = BitmapHandle(bitmap);

        let previous = unsafe { SelectObject(mem_dc.0, bitmap.0.into()) };
        if previous.is_invalid() {
            return Err(last_error("SelectObject"));
        }
        let _selected = SelectedObject {
            dc: mem_dc.0,
            previous,
        };

        unsafe {
            BitBlt(
                mem_dc.0,
                0,
                0,
                width,
                height,
                Some(screen_dc.0),
                x,
                y,
                SRCCOPY,
            )
        }
        .map_err(|error| native_error("BitBlt", error.code().0))?;

        let mut pixels = vec![0u8; buffer_len];
        let mut bitmap_info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: width,
                biHeight: -height,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                biSizeImage: buffer_len as u32,
                biXPelsPerMeter: 0,
                biYPelsPerMeter: 0,
                biClrUsed: 0,
                biClrImportant: 0,
            },
            bmiColors: [Default::default()],
        };

        let rows = unsafe {
            GetDIBits(
                mem_dc.0,
                bitmap.0,
                0,
                height as u32,
                Some(pixels.as_mut_ptr().cast()),
                &mut bitmap_info,
                DIB_RGB_COLORS,
            )
        };
        if rows == 0 {
            return Err(last_error("GetDIBits"));
        }

        let path = screen_capture_temp_path()?;
        let mut file =
            std::fs::File::create(&path).map_err(|error| WindowsScreenCaptureError::Io {
                operation: "CreateCaptureFile",
                message: error.to_string(),
            })?;
        file.write_all(&pixels)
            .map_err(|error| WindowsScreenCaptureError::Io {
                operation: "WriteCaptureFile",
                message: error.to_string(),
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
    ) -> Result<Vec<ScreenWindow>, WindowsScreenCaptureError> {
        let mut windows = Vec::new();
        let mut context = WindowSnapshotContext {
            request: &request,
            windows: &mut windows,
        };

        unsafe {
            EnumWindows(
                Some(enum_top_level_window_proc),
                LPARAM(&mut context as *mut WindowSnapshotContext<'_> as isize),
            )
        }
        .map_err(|error| native_error("EnumWindows", error.code().0))?;

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

    unsafe extern "system" fn enum_top_level_window_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
        let context = unsafe { &mut *(lparam.0 as *mut WindowSnapshotContext<'_>) };
        let Some(window) = screen_window_from_hwnd(hwnd, None, context.request, true) else {
            return BOOL(1);
        };

        context.windows.push(window);
        collect_direct_child_windows(hwnd, context.request, context.windows);
        BOOL(1)
    }

    unsafe extern "system" fn enum_child_window_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
        let context = unsafe { &mut *(lparam.0 as *mut ChildWindowSnapshotContext<'_>) };
        let Ok(parent) = (unsafe { GetParent(hwnd) }) else {
            return BOOL(1);
        };
        if parent != context.parent_hwnd {
            return BOOL(1);
        }

        let Some(window) =
            screen_window_from_hwnd(hwnd, Some(context.parent_id), context.request, false)
        else {
            return BOOL(1);
        };

        context.windows.push(window);
        collect_direct_child_windows(hwnd, context.request, context.windows);
        BOOL(1)
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
            parent_id: parent_hwnd.0 as isize,
        };

        unsafe {
            let _ = EnumChildWindows(
                Some(parent_hwnd),
                Some(enum_child_window_proc),
                LPARAM(&mut context as *mut ChildWindowSnapshotContext<'_> as isize),
            );
        }
    }

    fn screen_window_from_hwnd(
        hwnd: HWND,
        parent_id: Option<isize>,
        request: &ScreenWindowSnapshotRequest,
        apply_top_level_filters: bool,
    ) -> Option<ScreenWindow> {
        if hwnd.is_invalid() {
            return None;
        }

        if unsafe { IsWindowVisible(hwnd) }.as_bool() == false {
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

        let mut rect = RECT::default();
        if unsafe { GetWindowRect(hwnd, &mut rect) }.is_err() {
            return None;
        }

        let width = rect.right - rect.left;
        let height = rect.bottom - rect.top;
        if width <= 0 || height <= 0 {
            return None;
        }

        Some(
            ScreenWindow::new(
                hwnd.0 as isize,
                parent_id,
                ScreenRect::new(rect.left, rect.top, width as u32, height as u32),
            )
            .class_name(class_name),
        )
    }

    fn window_class_name(hwnd: HWND) -> String {
        let mut buffer = [0u16; 256];
        let len = unsafe { GetClassNameW(hwnd, &mut buffer) };
        if len <= 0 {
            return String::new();
        }

        String::from_utf16_lossy(&buffer[..len as usize])
    }

    fn window_title(hwnd: HWND) -> String {
        let len = unsafe { GetWindowTextLengthW(hwnd) };
        if len <= 0 {
            return String::new();
        }

        let mut buffer = vec![0u16; len as usize + 1];
        let copied = unsafe { GetWindowTextW(hwnd, &mut buffer) };
        if copied <= 0 {
            return String::new();
        }

        String::from_utf16_lossy(&buffer[..copied as usize])
    }

    fn screen_capture_rect(
        request: ScreenCaptureRequest,
    ) -> Result<(i32, i32, i32, i32), WindowsScreenCaptureError> {
        match request.region {
            Some(region) => {
                if region.is_empty() {
                    return Err(WindowsScreenCaptureError::InvalidCaptureRequest(
                        "empty region",
                    ));
                }

                let width = i32::try_from(region.width).map_err(|_| {
                    WindowsScreenCaptureError::InvalidCaptureRequest("region width overflow")
                })?;
                let height = i32::try_from(region.height).map_err(|_| {
                    WindowsScreenCaptureError::InvalidCaptureRequest("region height overflow")
                })?;
                Ok((region.x, region.y, width, height))
            }
            None => {
                let x = unsafe { GetSystemMetrics(SM_XVIRTUALSCREEN) };
                let y = unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) };
                let width = unsafe { GetSystemMetrics(SM_CXVIRTUALSCREEN) };
                let height = unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) };

                if width <= 0 || height <= 0 {
                    return Err(WindowsScreenCaptureError::NativeCallFailed {
                        operation: "GetSystemMetrics",
                        code: 0,
                    });
                }

                Ok((x, y, width, height))
            }
        }
    }

    fn capture_buffer_len(width: i32, height: i32) -> Result<usize, WindowsScreenCaptureError> {
        let width = usize::try_from(width).map_err(|_| WindowsScreenCaptureError::SizeOverflow)?;
        let height =
            usize::try_from(height).map_err(|_| WindowsScreenCaptureError::SizeOverflow)?;
        width
            .checked_mul(height)
            .and_then(|pixels| pixels.checked_mul(4))
            .ok_or(WindowsScreenCaptureError::SizeOverflow)
    }

    fn screen_capture_temp_path() -> Result<std::path::PathBuf, WindowsScreenCaptureError> {
        let mut directory = std::env::temp_dir();
        directory.push("Easydict");
        directory.push("screen-capture");
        std::fs::create_dir_all(&directory).map_err(|error| WindowsScreenCaptureError::Io {
            operation: "CreateCaptureDirectory",
            message: error.to_string(),
        })?;

        let timestamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or_default();
        directory.push(format!("capture-{}-{timestamp}.bgra", std::process::id()));
        Ok(directory)
    }

    fn last_error(operation: &'static str) -> WindowsScreenCaptureError {
        WindowsScreenCaptureError::NativeCallFailed {
            operation,
            code: unsafe { GetLastError().0 as i32 },
        }
    }

    fn native_error(operation: &'static str, code: i32) -> WindowsScreenCaptureError {
        WindowsScreenCaptureError::NativeCallFailed { operation, code }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn capture_buffer_len_uses_bgra_stride() {
            assert_eq!(capture_buffer_len(3, 2).unwrap(), 24);
        }

        #[test]
        fn region_request_rejects_empty_rect() {
            let request = ScreenCaptureRequest::region(ScreenRect::new(1, 2, 0, 5));

            assert!(matches!(
                screen_capture_rect(request),
                Err(WindowsScreenCaptureError::InvalidCaptureRequest(
                    "empty region"
                ))
            ));
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use super::{
        ScreenCaptureRequest, ScreenCaptureResult, ScreenWindow, ScreenWindowSnapshotRequest,
        WindowsScreenCaptureError,
    };

    pub fn capture_screen_region(
        _request: ScreenCaptureRequest,
    ) -> Result<ScreenCaptureResult, WindowsScreenCaptureError> {
        Err(WindowsScreenCaptureError::UnsupportedPlatform)
    }

    pub fn capture_screen_windows(
        _request: ScreenWindowSnapshotRequest,
    ) -> Result<Vec<ScreenWindow>, WindowsScreenCaptureError> {
        Err(WindowsScreenCaptureError::UnsupportedPlatform)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn request_region_preserves_rect() {
        let rect = ScreenRect::new(-10, 20, 300, 200);

        assert_eq!(ScreenCaptureRequest::region(rect).region, Some(rect));
    }

    #[test]
    fn window_snapshot_exclusions_are_accumulated() {
        let request = ScreenWindowSnapshotRequest::new()
            .exclude_title("Easydict Capture")
            .exclude_title("Settings");

        assert_eq!(
            request.excluded_titles,
            ["Easydict Capture".to_string(), "Settings".to_string()]
        );
    }
}

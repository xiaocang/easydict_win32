use easydict_windows_screen_capture as native;
use win_fluent::Task;

use crate::screen_capture::{ScreenWindowRect, ScreenWindowSnapshot};

pub fn capture_screen_region_task<Message>(
    request: native::ScreenCaptureRequest,
    map: impl FnOnce(Option<native::ScreenCaptureResult>) -> Message + Send + 'static,
) -> Task<Message>
where
    Message: Send + 'static,
{
    Task::perform(async move { capture_screen_region(request) }, map)
}

pub fn capture_screen_region_result_task<Message>(
    request: native::ScreenCaptureRequest,
    map: impl FnOnce(Result<native::ScreenCaptureResult, String>) -> Message + Send + 'static,
) -> Task<Message>
where
    Message: Send + 'static,
{
    Task::perform(async move { capture_screen_region_result(request) }, map)
}

pub fn capture_screen_windows_task<Message>(
    request: native::ScreenWindowSnapshotRequest,
    map: impl FnOnce(Vec<ScreenWindowSnapshot>) -> Message + Send + 'static,
) -> Task<Message>
where
    Message: Send + 'static,
{
    Task::perform(async move { capture_screen_windows(request) }, map)
}

pub fn capture_screen_windows_result_task<Message>(
    request: native::ScreenWindowSnapshotRequest,
    map: impl FnOnce(Result<Vec<ScreenWindowSnapshot>, String>) -> Message + Send + 'static,
) -> Task<Message>
where
    Message: Send + 'static,
{
    Task::perform(async move { capture_screen_windows_result(request) }, map)
}

pub fn capture_screen_region(
    request: native::ScreenCaptureRequest,
) -> Option<native::ScreenCaptureResult> {
    capture_screen_region_result(request).ok()
}

pub fn capture_screen_region_result(
    request: native::ScreenCaptureRequest,
) -> Result<native::ScreenCaptureResult, String> {
    native::capture_screen_region(request).map_err(|error| error.to_string())
}

pub fn capture_screen_windows(
    request: native::ScreenWindowSnapshotRequest,
) -> Vec<ScreenWindowSnapshot> {
    capture_screen_windows_result(request).unwrap_or_default()
}

pub fn capture_screen_windows_result(
    request: native::ScreenWindowSnapshotRequest,
) -> Result<Vec<ScreenWindowSnapshot>, String> {
    native::capture_screen_windows(request)
        .map(|windows| windows.into_iter().map(from_native_window).collect())
        .map_err(|error| error.to_string())
}

fn from_native_window(window: native::ScreenWindow) -> ScreenWindowSnapshot {
    ScreenWindowSnapshot::new(
        window.id,
        window.parent_id,
        from_native_window_rect(window.rect),
    )
    .class_name(window.class_name)
}

fn from_native_window_rect(rect: native::ScreenRect) -> ScreenWindowRect {
    ScreenWindowRect::new(rect.x, rect.y, rect.width, rect.height)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capture_region_uses_lib_owned_request_directly() {
        let request =
            native::ScreenCaptureRequest::region(native::ScreenRect::new(-10, 20, 300, 200));

        assert_eq!(
            request.region,
            Some(native::ScreenRect::new(-10, 20, 300, 200))
        );
    }

    #[test]
    fn capture_region_result_preserves_native_error_diagnostics() {
        let request = native::ScreenCaptureRequest::region(native::ScreenRect::new(1, 2, 0, 5));

        let error = capture_screen_region_result(request)
            .expect_err("invalid capture request should return diagnostics");

        assert!(
            error.contains("invalid screen capture request")
                || error.contains("only available on Windows"),
            "{error}"
        );
        assert_eq!(capture_screen_region(request), None);
    }

    #[test]
    fn native_window_conversion_preserves_tree_fields() {
        let window =
            native::ScreenWindow::new(42, Some(7), native::ScreenRect::new(1, 2, 300, 200))
                .class_name("Chrome_WidgetWin_1");

        let converted = from_native_window(window);

        assert_eq!(converted.id, 42);
        assert_eq!(converted.parent_id, Some(7));
        assert_eq!(converted.rect, ScreenWindowRect::new(1, 2, 300, 200));
        assert_eq!(converted.class_name, "Chrome_WidgetWin_1");
    }
}

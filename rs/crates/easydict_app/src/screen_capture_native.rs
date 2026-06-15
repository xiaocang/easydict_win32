use easydict_windows_screen_capture as native;
use win_fluent::prelude::{ScreenCaptureRequest, ScreenCaptureResult, ScreenRect, ScreenWindow};
use win_fluent::Task;

pub fn capture_screen_region_task<Message>(
    request: ScreenCaptureRequest,
    map: impl FnOnce(Option<ScreenCaptureResult>) -> Message + Send + 'static,
) -> Task<Message>
where
    Message: Send + 'static,
{
    Task::perform(async move { capture_screen_region(request) }, map)
}

pub fn capture_screen_windows_task<Message>(
    request: win_fluent::platform::ScreenWindowSnapshotRequest,
    map: impl FnOnce(Vec<ScreenWindow>) -> Message + Send + 'static,
) -> Task<Message>
where
    Message: Send + 'static,
{
    Task::perform(async move { capture_screen_windows(request) }, map)
}

pub fn capture_screen_region(request: ScreenCaptureRequest) -> Option<ScreenCaptureResult> {
    native::capture_screen_region(to_native_capture_request(request))
        .ok()
        .map(from_native_capture_result)
}

pub fn capture_screen_windows(
    request: win_fluent::platform::ScreenWindowSnapshotRequest,
) -> Vec<ScreenWindow> {
    native::capture_screen_windows(to_native_window_snapshot_request(request))
        .map(|windows| windows.into_iter().map(from_native_window).collect())
        .unwrap_or_default()
}

fn to_native_capture_request(request: ScreenCaptureRequest) -> native::ScreenCaptureRequest {
    native::ScreenCaptureRequest {
        region: request.region.map(to_native_rect),
    }
}

fn to_native_window_snapshot_request(
    request: win_fluent::platform::ScreenWindowSnapshotRequest,
) -> native::ScreenWindowSnapshotRequest {
    native::ScreenWindowSnapshotRequest {
        excluded_titles: request.excluded_titles,
    }
}

fn from_native_capture_result(result: native::ScreenCaptureResult) -> ScreenCaptureResult {
    ScreenCaptureResult {
        pixel_data_path: result.pixel_data_path,
        pixel_width: result.pixel_width,
        pixel_height: result.pixel_height,
        screen_rect: from_native_rect(result.screen_rect),
    }
}

fn from_native_window(window: native::ScreenWindow) -> ScreenWindow {
    ScreenWindow::new(window.id, window.parent_id, from_native_rect(window.rect))
        .class_name(window.class_name)
}

fn to_native_rect(rect: ScreenRect) -> native::ScreenRect {
    native::ScreenRect::new(rect.x, rect.y, rect.width, rect.height)
}

fn from_native_rect(rect: native::ScreenRect) -> ScreenRect {
    ScreenRect::new(rect.x, rect.y, rect.width, rect.height)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capture_request_conversion_preserves_region() {
        let request = ScreenCaptureRequest::region(ScreenRect::new(-10, 20, 300, 200));
        let converted = to_native_capture_request(request);

        assert_eq!(
            converted.region,
            Some(native::ScreenRect::new(-10, 20, 300, 200))
        );
    }

    #[test]
    fn native_window_conversion_preserves_tree_fields() {
        let window =
            native::ScreenWindow::new(42, Some(7), native::ScreenRect::new(1, 2, 300, 200))
                .class_name("Chrome_WidgetWin_1");

        let converted = from_native_window(window);

        assert_eq!(converted.id, 42);
        assert_eq!(converted.parent_id, Some(7));
        assert_eq!(converted.rect, ScreenRect::new(1, 2, 300, 200));
        assert_eq!(converted.class_name, "Chrome_WidgetWin_1");
    }
}

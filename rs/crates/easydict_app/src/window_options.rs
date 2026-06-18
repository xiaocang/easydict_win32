use crate::state::{CaptureBackground, SettingsState};
use win_fluent::prelude::*;

pub const MAIN_WINDOW_DEFAULT_WIDTH_DIPS: f32 = 419.0;
pub const MAIN_WINDOW_DEFAULT_HEIGHT_DIPS: f32 = 494.5;
pub const MAIN_WINDOW_MIN_WIDTH_DIPS: f32 = 400.0;
pub const MAIN_WINDOW_MIN_HEIGHT_DIPS: f32 = 494.5;
pub const SETTINGS_WINDOW_DEFAULT_WIDTH_DIPS: f32 = 846.0;
pub const SETTINGS_WINDOW_DEFAULT_HEIGHT_DIPS: f32 = 913.0;
/// Minimum settings window width. The tab grid reflows responsively, so this
/// can sit well below the single-row tab width without clipping the tabs.
pub const SETTINGS_WINDOW_MIN_WIDTH_DIPS: f32 = 480.0;
pub const SETTINGS_WINDOW_MIN_HEIGHT_DIPS: f32 = 620.0;

pub fn main_window_options() -> WindowOptions {
    WindowOptions::new("main", "Easydict")
        .size(
            MAIN_WINDOW_DEFAULT_WIDTH_DIPS,
            MAIN_WINDOW_DEFAULT_HEIGHT_DIPS,
        )
        .min_size(MAIN_WINDOW_MIN_WIDTH_DIPS, MAIN_WINDOW_MIN_HEIGHT_DIPS)
        .frame(WindowFrame::Borderless)
        .resize_mode(WindowResizeMode::CanResize)
        .placement(WindowPlacement::Center)
}

pub fn main_window_options_for_settings(settings: &SettingsState) -> WindowOptions {
    let options = main_window_options();
    if settings.minimize_to_tray && settings.start_minimized {
        options.hidden()
    } else {
        options
    }
}

pub fn settings_window_options() -> WindowOptions {
    WindowOptions::new("settings", "Easydict Settings")
        .size(
            SETTINGS_WINDOW_DEFAULT_WIDTH_DIPS,
            SETTINGS_WINDOW_DEFAULT_HEIGHT_DIPS,
        )
        // The tab grid reflows responsively (WinUI ItemsWrapGrid), so the
        // window may narrow well below the single-row tab width; the content
        // cards below are Fill-width and reflow with it.
        .min_size(
            SETTINGS_WINDOW_MIN_WIDTH_DIPS,
            SETTINGS_WINDOW_MIN_HEIGHT_DIPS,
        )
        .frame(WindowFrame::Borderless)
        .resize_mode(WindowResizeMode::CanResize)
        .placement(WindowPlacement::Center)
}

pub fn mini_window_options() -> WindowOptions {
    WindowOptions::new("mini", "Easydict Mini")
        .size(320.0, 200.0)
        .min_size(280.0, 200.0)
        .level(WindowLevel::TopMost)
        .frame(WindowFrame::Acrylic)
        .resize_mode(WindowResizeMode::CanResize)
        .placement(WindowPlacement::CursorOffset { x: 12.0, y: 12.0 })
        .skip_taskbar(true)
}

pub fn fixed_window_options() -> WindowOptions {
    WindowOptions::new("fixed", "Easydict Fixed")
        .size(320.0, 280.0)
        .min_size(280.0, 200.0)
        .level(WindowLevel::TopMost)
        .frame(WindowFrame::Acrylic)
        .resize_mode(WindowResizeMode::CanResize)
        .placement(WindowPlacement::Center)
        .skip_taskbar(true)
}

pub fn capture_overlay_window_options() -> WindowOptions {
    WindowOptions::new("capture-overlay", "Easydict Capture")
        .size(1920.0, 1080.0)
        .min_size(1.0, 1.0)
        .level(WindowLevel::TopMost)
        .frame(WindowFrame::Borderless)
        .resize_mode(WindowResizeMode::Fixed)
        .placement(WindowPlacement::Monitor)
        .skip_taskbar(true)
}

pub fn capture_overlay_window_options_for_background(
    background: Option<&CaptureBackground>,
) -> WindowOptions {
    let options = capture_overlay_window_options();
    let Some(background) = background else {
        return options;
    };
    if background.scale_factor <= f32::EPSILON {
        return options;
    }

    let x = background.screen_rect.x as f32 / background.scale_factor;
    let y = background.screen_rect.y as f32 / background.scale_factor;
    let width = (background.pixel_width as f32 / background.scale_factor).max(1.0);
    // Match monitor placement's one-DIP oversize for borderless overlays so
    // Windows does not promote the window into an exclusive fullscreen path.
    let height = (background.pixel_height as f32 / background.scale_factor).max(1.0) + 1.0;

    options
        .size(width, height)
        .placement(WindowPlacement::Explicit { x, y })
        .allow_offscreen()
}

pub fn pop_button_window_options() -> WindowOptions {
    WindowOptions::new("pop-button", "Easydict Selection")
        .size(30.0, 30.0)
        .min_size(30.0, 30.0)
        .level(WindowLevel::ToolWindow)
        .frame(WindowFrame::Borderless)
        .resize_mode(WindowResizeMode::Fixed)
        .placement(WindowPlacement::CursorOffset { x: 8.0, y: 8.0 })
        .skip_taskbar(true)
        .no_activate(true)
}

use crate::state::SettingsState;
use win_fluent::prelude::*;

pub const MAIN_WINDOW_DEFAULT_WIDTH_DIPS: f32 = 419.0;
pub const MAIN_WINDOW_DEFAULT_HEIGHT_DIPS: f32 = 494.5;
pub const MAIN_WINDOW_MIN_WIDTH_DIPS: f32 = 400.0;
pub const MAIN_WINDOW_MIN_HEIGHT_DIPS: f32 = 494.5;
pub const SETTINGS_WINDOW_DEFAULT_WIDTH_DIPS: f32 = 846.0;
pub const SETTINGS_WINDOW_DEFAULT_HEIGHT_DIPS: f32 = 913.0;

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
        .min_size(760.0, 620.0)
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

use win_fluent::prelude::*;

pub fn main_window_options() -> WindowOptions {
    WindowOptions::new("main", "Easydict")
        .size(940.0, 1220.0)
        .min_size(400.0, 500.0)
        .frame(WindowFrame::Borderless)
        .resize_mode(WindowResizeMode::CanResize)
        .placement(WindowPlacement::Center)
}

pub fn settings_window_options() -> WindowOptions {
    WindowOptions::new("settings", "Easydict Settings")
        .size(846.0, 913.0)
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

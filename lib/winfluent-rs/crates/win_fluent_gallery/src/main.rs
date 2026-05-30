use win_fluent::prelude::*;
use win_fluent_platform_win::WindowsPlatformAdapter;

#[allow(dead_code)]
#[derive(Clone, Debug)]
enum Msg {
    Navigate(String),
    InputChanged(String),
    ToggleChanged(bool),
    Run,
    Copy,
    Speak,
}

fn main() {
    let view = gallery_view();
    let mini_options = mini_window_options();
    println!("{}", win_fluent_testkit::view_snapshot(&view));
    println!("{}", win_fluent_testkit::layout_snapshot(&view));
    println!("{}", window_options_snapshot(&mini_options));
    println!("{}", windows_window_plan_snapshot(&mini_options));
    println!("{}", win_fluent_testkit::view_snapshot(&mini_window_view()));
    println!(
        "{}",
        win_fluent_testkit::theme_snapshot(&ThemeTokens::fluent_light())
    );
}

fn gallery_view() -> View<Msg> {
    page("Control Gallery")
        .content(
            navigation_view([
                NavigationItem::new("home", "Home").icon(icon::search()),
                NavigationItem::new("settings", "Settings").icon(icon::settings()),
            ])
            .selected("home")
            .content(scroll_view(
                column((
                    text("Inputs"),
                    text_editor("")
                        .id("gallery.input")
                        .placeholder("Type text")
                        .min_height(96)
                        .focused(true)
                        .on_input(Msg::InputChanged),
                    command_bar((
                        primary_button("Run")
                            .icon(icon::translate())
                            .on_press(Msg::Run),
                        button("Copy").icon(icon::copy()).on_press(Msg::Copy),
                        button("Speak")
                            .icon(icon::speaker())
                            .enabled(false)
                            .validation(ValidationState::info("Voice output is unavailable"))
                            .on_press(Msg::Speak),
                    )),
                    settings_row("Background service")
                        .description("Controls whether the service starts with the session")
                        .icon(icon::settings())
                        .trailing((toggle_switch("Enabled", true).on_toggle(Msg::ToggleChanged),)),
                    service_result_list([
                        ResultItem::new("one", "Provider A", "Ready"),
                        ResultItem::new("two", "Provider B", "Streaming")
                            .status(ResultStatus::Streaming),
                    ])
                    .on_copy(Msg::Copy)
                    .on_speak(Msg::Speak),
                ))
                .padding(24)
                .spacing(16),
            ))
            .on_select(Msg::Navigate),
        )
        .into_view()
}

fn mini_window_options() -> WindowOptions {
    WindowOptions::new("mini", "Mini Translate")
        .size(420.0, 360.0)
        .min_size(320.0, 220.0)
        .level(WindowLevel::TopMost)
        .frame(WindowFrame::Acrylic)
        .resize_mode(WindowResizeMode::CanResize)
        .placement(WindowPlacement::CursorOffset { x: 12.0, y: 12.0 })
        .skip_taskbar(true)
}

fn mini_window_view() -> View<Msg> {
    page("Mini Translate")
        .content(
            column((
                text_editor("Selected text")
                    .id("mini.input")
                    .placeholder("Text to translate")
                    .min_height(88)
                    .focused(true)
                    .on_input(Msg::InputChanged),
                command_bar((
                    primary_button("Translate")
                        .icon(icon::translate())
                        .on_press(Msg::Run),
                    button("Copy").icon(icon::copy()).on_press(Msg::Copy),
                    button("Speak").icon(icon::speaker()).on_press(Msg::Speak),
                ))
                .compact(true),
                service_result_list([
                    ResultItem::new("openai", "OpenAI", "Streaming result")
                        .status(ResultStatus::Streaming),
                    ResultItem::new("google", "Google", "Ready result"),
                ])
                .on_copy(Msg::Copy)
                .on_speak(Msg::Speak),
            ))
            .padding(16)
            .spacing(12),
        )
        .into_view()
}

fn window_options_snapshot(options: &WindowOptions) -> String {
    format!(
        "WindowOptions id={} title={:?} size={}x{} min={:?}x{:?} level={:?} frame={:?} resize={:?} placement={:?} skip_taskbar={}",
        options.id.as_str(),
        options.title,
        options.width,
        options.height,
        options.min_width,
        options.min_height,
        options.level,
        options.frame,
        options.resize_mode,
        options.placement,
        options.skip_taskbar
    )
}

fn windows_window_plan_snapshot(options: &WindowOptions) -> String {
    let plan = WindowsPlatformAdapter::plan_window_with_resolved_placement(options)
        .unwrap_or_else(|_| WindowsPlatformAdapter::plan_window(options));
    let placement = plan
        .placement
        .map(|placement| {
            format!(
                " placement={}x{}@{},{} work={}x{}@{},{}",
                placement.width,
                placement.height,
                placement.x,
                placement.y,
                placement.work_area.width(),
                placement.work_area.height(),
                placement.work_area.left,
                placement.work_area.top,
            )
        })
        .unwrap_or_else(|| " placement=unresolved".to_string());

    format!(
        "WindowsWindowPlan id={} style=0x{:08x} ex_style=0x{:08x} size={}x{} min={:?}x{:?} visible={} skip_taskbar={} acrylic={}{}",
        plan.id,
        plan.style,
        plan.ex_style,
        plan.width,
        plan.height,
        plan.min_width,
        plan.min_height,
        plan.visible_on_start,
        plan.skip_taskbar,
        plan.uses_acrylic,
        placement,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mini_window_reference_uses_windows_tool_window_shape() {
        let options = mini_window_options();
        let native_plan = WindowsPlatformAdapter::plan_window(&options);

        assert_eq!(options.id.as_str(), "mini");
        assert_eq!(options.level, WindowLevel::TopMost);
        assert_eq!(options.frame, WindowFrame::Acrylic);
        assert!(options.skip_taskbar);
        assert!(native_plan.uses_acrylic);
        assert_ne!(native_plan.ex_style, 0);
    }

    #[test]
    fn mini_window_reference_resolves_cursor_offset_with_current_monitor() {
        let options = mini_window_options();
        let placement = WindowsPlatformAdapter::resolve_window_placement_for(
            &options,
            win_fluent_platform_win::WindowsPoint { x: 1912, y: 1072 },
            win_fluent_platform_win::WindowsRect {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
        );

        assert_eq!(placement.width, 420);
        assert_eq!(placement.height, 360);
        assert_eq!(placement.x, 1500);
        assert_eq!(placement.y, 720);
    }

    #[test]
    fn mini_window_reference_has_stable_schema() {
        let snapshot = win_fluent_testkit::view_snapshot(&mini_window_view());

        assert!(snapshot.contains("ViewSchema version=1"));
        assert!(snapshot.contains("TextEditor"));
        assert!(snapshot.contains("ServiceResultList"));
        assert!(!snapshot.contains("iced"));
        assert!(!snapshot.contains("windows::"));
    }
}

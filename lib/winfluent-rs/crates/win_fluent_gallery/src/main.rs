use std::fmt::Write;

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
    println!("{}", win_fluent_testkit::accessibility_snapshot(&view));
    println!(
        "{}",
        win_fluent_testkit::accessibility_audit_snapshot(&view)
    );
    println!("{}", window_options_snapshot(&mini_options));
    println!("{}", windows_window_plan_snapshot(&mini_options));
    println!("{}", windows_uia_plan_snapshot(&view));
    println!("{}", win_fluent_testkit::view_snapshot(&main_window_view()));
    println!("{}", win_fluent_testkit::view_snapshot(&mini_window_view()));
    println!(
        "{}",
        win_fluent_testkit::view_snapshot(&fixed_window_view())
    );
    println!(
        "{}",
        win_fluent_testkit::view_snapshot(&settings_window_view())
    );
    println!("{}", win_fluent_testkit::view_snapshot(&ocr_overlay_view()));
    println!("{}", win_fluent_testkit::theme_matrix_snapshot());
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
                column(vec![
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
                    ))
                    .into_view(),
                    settings_row("Background service")
                        .description("Controls whether the service starts with the session")
                        .icon(icon::settings())
                        .trailing((toggle_switch("Enabled", true).on_toggle(Msg::ToggleChanged),))
                        .into_view(),
                    service_result_list([
                        ResultItem::new("one", "Provider A", "Ready"),
                        ResultItem::new("two", "Provider B", "Streaming")
                            .status(ResultStatus::Streaming),
                    ])
                    .on_copy(Msg::Copy)
                    .on_speak(Msg::Speak)
                    .into_view(),
                    progress_bar::<Msg>()
                        .id("gallery.progress")
                        .label("Progress")
                        .value(48.0)
                        .into_view(),
                    progress_ring::<Msg>()
                        .id("gallery.progress-ring")
                        .label("Background work")
                        .into_view(),
                    auto_suggest_box("pro")
                        .id("gallery.search")
                        .placeholder("Search")
                        .suggestions(["Provider A", "Provider B"])
                        .highlighted_index(Some(0))
                        .on_submit(Msg::Navigate),
                    slider(42.0)
                        .id("gallery.slider")
                        .range(0.0, 100.0)
                        .step(5.0)
                        .focused(true)
                        .on_change(|value| Msg::Navigate(format!("slider:{value:.0}"))),
                    busy_overlay(text("Panel content"))
                        .id("gallery.busy")
                        .active(true)
                        .blocks_input(true)
                        .label("Loading")
                        .into_view(),
                    overlay(text("Base surface"))
                        .id("gallery.overlay")
                        .layer(OverlayLayer::modal(text("Modal surface")))
                        .into_view(),
                    adaptive_switch(720, text("Wide surface"), text("Narrow surface"))
                        .id("gallery.adaptive")
                        .resolved_width(640.0)
                        .into_view(),
                    lazy("gallery.lazy.reference", text("Lazy reference"))
                        .id("gallery.lazy")
                        .into_view(),
                    pointer_region(text("Pointer surface"))
                        .id("gallery.pointer")
                        .width(Length::Fixed(240))
                        .height(Length::Fixed(120))
                        .on_move(|position| {
                            Msg::Navigate(format!("move:{},{}", position.x, position.y))
                        })
                        .on_wheel(|wheel| Msg::Navigate(format!("wheel:{}", wheel.delta)))
                        .on_escape(Msg::Run)
                        .into_view(),
                    capture_overlay(CaptureOverlayPhase::Detecting)
                        .id("gallery.capture")
                        .handles_visible(true)
                        .magnifier_visible(true)
                        .background(CaptureOverlayBackground::new("gallery.bgra", 800, 600))
                        .cursor(CaptureOverlayPoint::new(40, 48))
                        .into_view(),
                ])
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

fn main_window_view() -> View<Msg> {
    page("Translate")
        .content(
            navigation_view([
                NavigationItem::new("translate", "Translate").icon(icon::translate()),
                NavigationItem::new("history", "History").icon(icon::search()),
                NavigationItem::new("settings", "Settings").icon(icon::settings()),
            ])
            .selected("translate")
            .content(scroll_view(
                column((
                    row((
                        combo_box([
                            ComboBoxItem::new("auto", "Auto detect"),
                            ComboBoxItem::new("en", "English"),
                            ComboBoxItem::new("zh", "Chinese"),
                        ])
                        .label("Source")
                        .selected("auto")
                        .on_change(Msg::Navigate),
                        combo_box([
                            ComboBoxItem::new("en", "English"),
                            ComboBoxItem::new("zh", "Chinese"),
                            ComboBoxItem::new("ja", "Japanese"),
                        ])
                        .label("Target")
                        .selected("en")
                        .on_change(Msg::Navigate),
                    ))
                    .spacing(12),
                    text_editor("")
                        .id("main.input")
                        .placeholder("Text to translate")
                        .min_height(140)
                        .on_input(Msg::InputChanged),
                    command_bar((
                        primary_button("Translate")
                            .icon(icon::translate())
                            .on_press(Msg::Run),
                        button("Copy").icon(icon::copy()).on_press(Msg::Copy),
                        button("Speak").icon(icon::speaker()).on_press(Msg::Speak),
                    )),
                    service_result_list([
                        ResultItem::new("provider-a", "Provider A", "Ready result"),
                        ResultItem::new("provider-b", "Provider B", "Streaming result")
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
                    ResultItem::new("provider-a", "Provider A", "Streaming result")
                        .status(ResultStatus::Streaming),
                    ResultItem::new("provider-b", "Provider B", "Ready result"),
                ])
                .on_copy(Msg::Copy)
                .on_speak(Msg::Speak),
            ))
            .padding(16)
            .spacing(12),
        )
        .into_view()
}

fn fixed_window_view() -> View<Msg> {
    page("Fixed Translate")
        .content(
            column((
                command_bar((
                    primary_button("Translate")
                        .icon(icon::translate())
                        .on_press(Msg::Run),
                    button("Copy").icon(icon::copy()).on_press(Msg::Copy),
                    button("Speak").icon(icon::speaker()).on_press(Msg::Speak),
                ))
                .compact(true),
                text_editor("Pinned source text")
                    .id("fixed.input")
                    .placeholder("Pinned text")
                    .min_height(110)
                    .on_input(Msg::InputChanged),
                service_result_card(ResultItem::new(
                    "fixed-result",
                    "Pinned Result",
                    "Translation stays visible while working in other windows.",
                ))
                .on_copy(Msg::Copy)
                .on_speak(Msg::Speak),
            ))
            .padding(16)
            .spacing(12),
        )
        .into_view()
}

fn settings_window_view() -> View<Msg> {
    page("Settings")
        .content(scroll_view(
            column((
                settings_row("Background service")
                    .description("Start helper services with the session")
                    .icon(icon::settings())
                    .trailing((toggle_switch("Enabled", true).on_toggle(Msg::ToggleChanged),)),
                settings_row("Theme")
                    .description("Choose a visual mode")
                    .trailing((combo_box([
                        ComboBoxItem::new("system", "System"),
                        ComboBoxItem::new("light", "Light"),
                        ComboBoxItem::new("dark", "Dark"),
                        ComboBoxItem::new("contrast", "High contrast"),
                    ])
                    .label("Theme")
                    .selected("system")
                    .on_change(Msg::Navigate),)),
                settings_row("Capture shortcut")
                    .description("Keyboard shortcut used by capture overlay")
                    .trailing((button("Record").on_press(Msg::Run),)),
                settings_row("Translation providers")
                    .description("Select services for multi-result translation")
                    .content(service_result_list([
                        ResultItem::new("provider-a", "Provider A", "Configured"),
                        ResultItem::new("provider-b", "Provider B", "Needs attention")
                            .status(ResultStatus::Error),
                    ])),
            ))
            .padding(24)
            .spacing(12),
        ))
        .into_view()
}

fn ocr_overlay_view() -> View<Msg> {
    page("Capture Overlay")
        .content(
            column((
                text("Capture region"),
                text("Use the overlay controls to confirm or adjust the selected area."),
                command_bar((
                    primary_button("Confirm")
                        .icon(icon::translate())
                        .on_press(Msg::Run),
                    button("Copy").icon(icon::copy()).on_press(Msg::Copy),
                    button("Cancel").on_press(Msg::Run),
                ))
                .compact(true),
            ))
            .padding(12)
            .spacing(8),
        )
        .into_view()
}

fn window_options_snapshot(options: &WindowOptions) -> String {
    format!(
        "WindowOptions id={} title={:?} size={}x{} min={:?}x{:?} level={:?} frame={:?} resize={:?} placement={:?} screen_constraint={:?} skip_taskbar={}",
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
        options.screen_constraint,
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
                " placement={}x{}@{},{} dpi={} work={}x{}@{},{} physical_work={}x{}@{},{}",
                placement.width,
                placement.height,
                placement.x,
                placement.y,
                placement.dpi,
                placement.work_area.width(),
                placement.work_area.height(),
                placement.work_area.left,
                placement.work_area.top,
                placement.physical_work_area.width(),
                placement.physical_work_area.height(),
                placement.physical_work_area.left,
                placement.physical_work_area.top,
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

fn windows_uia_plan_snapshot<Message>(view: &View<Message>) -> String {
    let tree = win_fluent_testkit::accessibility_tree(view);
    let plan = WindowsPlatformAdapter::plan_uia_tree(&tree);
    let mut output = String::new();
    let _ = writeln!(output, "WindowsUiaTree");
    write_uia_node(&mut output, &plan.root, 0);
    output
}

fn write_uia_node(
    output: &mut String,
    node: &win_fluent_platform_win::WindowsUiaNodePlan,
    indent: usize,
) {
    let pad = " ".repeat(indent);
    let _ = writeln!(
        output,
        "{pad}{:?} name={:?} focusable={} children={}",
        node.control_type,
        node.name,
        node.focusable,
        node.children.len()
    );

    for child in &node.children {
        write_uia_node(output, child, indent + 2);
    }
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
        assert!(snapshot.contains("ResultList"));
        assert!(!snapshot.contains("iced"));
        assert!(!snapshot.contains("windows::"));
    }

    #[test]
    fn main_fixed_settings_and_ocr_references_have_stable_schema() {
        let snapshots = [
            win_fluent_testkit::view_snapshot(&main_window_view()),
            win_fluent_testkit::view_snapshot(&fixed_window_view()),
            win_fluent_testkit::view_snapshot(&settings_window_view()),
            win_fluent_testkit::view_snapshot(&ocr_overlay_view()),
        ];

        assert!(snapshots[0].contains("NavigationView"));
        assert!(snapshots[0].contains("ResultList"));
        assert!(snapshots[1].contains("ResultCard"));
        assert!(snapshots[2].contains("SettingsRow"));
        assert!(snapshots[3].contains("CommandBar"));

        for snapshot in snapshots {
            assert!(snapshot.contains("ViewSchema version=1"));
            assert!(!snapshot.contains("iced"));
            assert!(!snapshot.contains("windows::"));
        }
    }

    #[test]
    fn gallery_reference_covers_framework_controls_and_visual_diff_artifacts() {
        let snapshot = win_fluent_testkit::view_snapshot(&gallery_view());

        for expected in [
            "ProgressBar",
            "ProgressRing",
            "AutoSuggestBox",
            "Slider",
            "BusyOverlay",
            "Overlay",
            "AdaptiveSwitch",
            "Lazy",
            "PointerRegion",
            "CaptureOverlay",
        ] {
            assert!(
                snapshot.contains(expected),
                "gallery schema missing {expected}\n{snapshot}"
            );
        }
        assert!(!snapshot.contains("iced"));
        assert!(!snapshot.contains("windows::"));

        let before = win_fluent_testkit::VisualFrame::solid_rgba(2, 1, [0, 0, 0, 255]);
        let after =
            win_fluent_testkit::VisualFrame::from_rgba(2, 1, vec![0, 0, 0, 255, 1, 0, 0, 255])
                .expect("valid frame");
        let diff = before.diff(&after).expect("diff");

        assert!(diff.passes(win_fluent_testkit::VisualDiffTolerance {
            max_changed_pixels: 1,
            max_total_delta: 1,
            max_channel_delta: 1,
        }));
        assert!(!diff.passes(win_fluent_testkit::VisualDiffTolerance::EXACT));
        assert!(after.to_ppm_rgb().starts_with(b"P6\n2 1\n255\n"));
    }

    #[test]
    fn reference_views_pass_accessibility_audit_and_map_to_uia() {
        let views = [
            main_window_view(),
            mini_window_view(),
            fixed_window_view(),
            settings_window_view(),
            ocr_overlay_view(),
        ];

        for view in views {
            let audit = win_fluent_testkit::accessibility_audit(&view);
            assert!(audit.passed(), "{:?}", audit.issues);

            let snapshot = windows_uia_plan_snapshot(&view);
            assert!(snapshot.contains("WindowsUiaTree"));
            assert!(snapshot.contains("Window"));
            assert!(!snapshot.contains("iced::"));
            assert!(!snapshot.contains("windows::"));
        }
    }
}

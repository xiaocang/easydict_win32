use win_fluent::prelude::*;
use win_fluent::theme::ThemeTokens;
use win_fluent_backend_iced::IcedAdapter;

#[derive(Clone, Debug, PartialEq)]
enum Msg {
    Bool(bool),
    Number(f32),
    Selected(String),
}

#[test]
fn iced_backend_compiles_first_wave_winui_controls() {
    let view = page("Backend Contract")
        .content(column(vec![
            info_bar("Status", ValidationSeverity::Info).into_view(),
            grid()
                .columns([Length::Shrink, Length::Fill])
                .rows([Length::Shrink])
                .cell(0, 0, text("Query"))
                .cell(0, 1, text_editor("hello"))
                .into_view(),
            list_view([
                ListViewItem::new("a", text("Alpha")),
                ListViewItem::new("b", text("Beta")),
            ])
            .on_select(Msg::Selected),
            radio_group()
                .option("one", "One")
                .option("two", "Two")
                .selected("one")
                .on_select(Msg::Selected),
            image("assets/logo.png")
                .stretch(ImageStretch::Uniform)
                .into_view(),
            image_bgra_file("missing.raw", 8, 8)
                .width(Length::Fixed(16))
                .height(Length::Fixed(16))
                .into_view(),
            number_box(99.0)
                .range(0.0, 2.0)
                .step(0.0)
                .on_change(Msg::Number),
            auto_suggest_box("a")
                .suggestions(["Alpha"])
                .highlighted_index(Some(0))
                .on_submit(Msg::Selected),
            flyout(button("More"), text("Details"))
                .open(true)
                .focus_behavior(FlyoutFocusBehavior::MoveFocusToContent)
                .into_view(),
            text_runs([TextRun::plain("Read "), TextRun::link("docs", "docs")])
                .on_link(Msg::Selected),
            web_view_html("<p>content</p>").into_view(),
            toggle_button("Pin", false).on_toggle(Msg::Bool),
            split_button("Save")
                .items([
                    FlyoutMenuItem::command("legacy-save", "Legacy Save").enabled(false),
                    FlyoutMenuItem::command("save-as", "Save as"),
                ])
                .on_select(Msg::Selected),
            tab_view([TabItem::new("main", "Main", text("Main"))]).on_select(Msg::Selected),
            tree_view([TreeNode::leaf("leaf", "Leaf")]).on_select(Msg::Selected),
            border(viewbox(text("Scaled"))).into_view(),
        ]))
        .into_view();

    let _element = IcedAdapter::compile_view(&view);
}

#[test]
fn iced_backend_compiles_first_wave_controls_across_theme_and_disabled_states() {
    let disabled = ControlState {
        enabled: false,
        hovered: false,
        pressed: false,
        focused: false,
        selected: false,
        validation: ValidationState::default(),
    };

    let view = page("State Contract")
        .content(column(vec![
            radio_group()
                .option("system", "System")
                .option("dark", "Dark")
                .state(disabled.clone())
                .on_select(Msg::Selected),
            number_box(1.0)
                .state(disabled.clone())
                .range(0.0, 2.0)
                .on_change(Msg::Number),
            auto_suggest_box("a")
                .state(disabled.clone())
                .suggestions(["Alpha"])
                .on_submit(Msg::Selected),
            text_editor("secret")
                .secure(true)
                .read_only(true)
                .into_view(),
            checkbox("Mixed", false)
                .state(disabled)
                .indeterminate(true)
                .on_toggle(Msg::Bool),
        ]))
        .into_view();

    for theme in [
        ThemeTokens::fluent_light(),
        ThemeTokens::fluent_dark(),
        ThemeTokens::high_contrast(),
    ] {
        let _element = IcedAdapter::compile_view_with_theme(&view, &theme);
    }
}

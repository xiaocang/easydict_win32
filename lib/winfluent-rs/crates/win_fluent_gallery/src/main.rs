use win_fluent::prelude::*;

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
    println!("{}", win_fluent_testkit::view_snapshot(&view));
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
                        .on_input(Msg::InputChanged),
                    command_bar((
                        primary_button("Run")
                            .icon(icon::translate())
                            .on_press(Msg::Run),
                        button("Copy").icon(icon::copy()).on_press(Msg::Copy),
                        button("Speak").icon(icon::speaker()).on_press(Msg::Speak),
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

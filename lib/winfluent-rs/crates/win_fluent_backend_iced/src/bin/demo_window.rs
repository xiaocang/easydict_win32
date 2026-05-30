use iced::Element;
use win_fluent::prelude::*;
use win_fluent_backend_iced::{IcedAdapter, IcedTextEditorContent};

pub fn main() -> iced::Result {
    let options = demo_window_options();

    iced::application(DemoState::new, update, view)
        .title("win_fluent Iced Demo")
        .window(IcedAdapter::window_settings(&options))
        .run()
}

#[derive(Debug, Clone)]
enum DemoMessage {
    InputChanged(String),
    ToggleChanged(bool),
    ServiceChanged(String),
    Translate,
    Copy,
    Speak,
}

struct DemoState {
    input: String,
    enabled: bool,
    service: String,
    translated: bool,
    editor: IcedTextEditorContent,
    view: View<DemoMessage>,
}

impl DemoState {
    fn new() -> Self {
        let input = "Selected text".to_string();
        let enabled = true;
        let service = "openai".to_string();
        let translated = false;
        let editor = IcedTextEditorContent::with_text(&input);
        let view = build_view(&input, enabled, &service, translated);

        Self {
            input,
            enabled,
            service,
            translated,
            editor,
            view,
        }
    }

    fn rebuild_view(&mut self) {
        self.view = build_view(&self.input, self.enabled, &self.service, self.translated);
    }
}

fn update(state: &mut DemoState, message: DemoMessage) {
    match message {
        DemoMessage::InputChanged(value) => {
            state.input = value;
            state.editor = IcedTextEditorContent::with_text(&state.input);
            state.translated = false;
        }
        DemoMessage::ToggleChanged(value) => {
            state.enabled = value;
        }
        DemoMessage::ServiceChanged(value) => {
            state.service = value;
            state.translated = false;
        }
        DemoMessage::Translate => {
            state.translated = true;
        }
        DemoMessage::Copy | DemoMessage::Speak => {}
    }

    state.rebuild_view();
}

fn view(state: &DemoState) -> Element<'_, DemoMessage> {
    IcedAdapter::compile_view_with_text_editors(&state.view, |id| {
        (id == "demo.input").then_some(&state.editor)
    })
}

fn build_view(input: &str, enabled: bool, service: &str, translated: bool) -> View<DemoMessage> {
    let result_body = if translated {
        format!("{input}\n\nDemo translation via {service}.")
    } else {
        "Press Translate to render a demo result.".to_string()
    };

    page("Mini Translate Demo")
        .content(
            column((
                text_editor(input)
                    .id("demo.input")
                    .placeholder("Text to translate")
                    .min_height(88)
                    .focused(true)
                    .on_input(DemoMessage::InputChanged),
                command_bar((
                    combo_box([
                        ComboBoxItem::new("openai", "OpenAI"),
                        ComboBoxItem::new("google", "Google"),
                        ComboBoxItem::new("deepl", "DeepL"),
                    ])
                    .selected(service)
                    .on_change(DemoMessage::ServiceChanged),
                    primary_button("Translate")
                        .enabled(enabled)
                        .icon(icon::translate())
                        .on_press(DemoMessage::Translate),
                    button("Copy")
                        .icon(icon::copy())
                        .on_press(DemoMessage::Copy),
                    button("Speak")
                        .icon(icon::speaker())
                        .enabled(translated)
                        .on_press(DemoMessage::Speak),
                ))
                .compact(true),
                settings_row("Background service")
                    .description("Mapped through win_fluent tokens before Iced renders it")
                    .trailing((
                        toggle_switch("Enabled", enabled).on_toggle(DemoMessage::ToggleChanged),
                    )),
                service_result_list([ResultItem::new(
                    service,
                    service.to_uppercase(),
                    result_body,
                )
                .status(if translated {
                    ResultStatus::Ready
                } else {
                    ResultStatus::Loading
                })])
                .on_copy(DemoMessage::Copy)
                .on_speak(DemoMessage::Speak),
            ))
            .padding(16)
            .spacing(12),
        )
        .into_view()
}

fn demo_window_options() -> WindowOptions {
    WindowOptions::new("iced-demo", "win_fluent Iced Demo")
        .size(520.0, 460.0)
        .min_size(360.0, 300.0)
        .level(WindowLevel::TopMost)
        .frame(WindowFrame::Standard)
        .resize_mode(WindowResizeMode::CanResize)
}

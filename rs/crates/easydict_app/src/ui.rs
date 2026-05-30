use crate::i18n::{tr, tr_count};
use crate::state::{
    AppMode, EasydictUiState, FloatingWindowState, LongDocumentState, Message, SettingsSection,
    SettingsState, TranslationResultPreview,
};
use win_fluent::prelude::*;
use win_fluent::view::TextToken;

pub fn main_window_view(state: &EasydictUiState) -> View<Message> {
    let content = match state.mode {
        AppMode::QuickTranslate => quick_translate_content(state),
        AppMode::LongDocument => long_document_content(&state.long_document),
    };

    page("Easydict")
        .id("main.window")
        .content(
            column((
                title_bar(tr("app.name", "Easydict"))
                    .id("main.title_bar")
                    .subtitle(tr("app.beta", "beta"))
                    .icon(icon::translate())
                    .caption_controls(true)
                    .on_minimize(Message::MinimizeWindow)
                    .on_toggle_maximize(Message::ToggleMaximizeWindow)
                    .on_close(Message::CloseWindow),
                column((main_header(state), content))
                    .id("main.surface")
                    .tw("p-3 gap-3 w-full h-full"),
            ))
            .id("main.root")
            .tw("p-0 gap-0 w-full h-full"),
        )
        .into_view()
}

pub fn settings_view(state: &SettingsState) -> View<Message> {
    page("Settings")
        .id("settings.window")
        .content(scroll_view(
            column((
                row((
                    button("Back")
                        .id("settings.back")
                        .icon(icon::clear())
                        .icon_only()
                        .tooltip("Back")
                        .on_press(Message::Back),
                    text("Settings"),
                ))
                .id("settings.header")
                .spacing(16)
                .align(Alignment::Center),
                settings_category_bar(state.selected_section),
                settings_section_content(state),
            ))
            .id("settings.content")
            .padding(24)
            .spacing(24)
            .width(Length::Fill),
        ))
        .into_view()
}

pub fn mini_window_view(state: &FloatingWindowState) -> View<Message> {
    floating_translate_view("mini", state, true)
}

pub fn fixed_window_view(state: &FloatingWindowState) -> View<Message> {
    floating_translate_view("fixed", state, false)
}

pub fn capture_overlay_view() -> View<Message> {
    page("Capture Overlay")
        .id("capture.overlay")
        .content(
            column((
                text("Capture region"),
                text("Adjust the selected area before OCR or copy."),
                command_bar((
                    primary_button("Confirm")
                        .id("capture.confirm")
                        .icon(icon::translate())
                        .on_press(Message::ConfirmCapture),
                    button("Copy text")
                        .id("capture.copy")
                        .icon(icon::copy())
                        .on_press(Message::CopyResult),
                    button("Cancel")
                        .id("capture.cancel")
                        .icon(icon::clear())
                        .on_press(Message::CancelCapture),
                ))
                .id("capture.commands")
                .compact(true),
            ))
            .id("capture.panel")
            .padding(12)
            .spacing(8),
        )
        .into_view()
}

pub fn pop_button_view() -> View<Message> {
    page("Selection Translate")
        .id("pop-button.window")
        .content(
            primary_button("Translate selection")
                .id("pop-button.translate")
                .icon(icon::translate())
                .icon_only()
                .tooltip("Translate selection")
                .on_press(Message::TranslateSelection),
        )
        .into_view()
}

fn main_header(state: &EasydictUiState) -> View<Message> {
    row((
        row((
            styled_text("🌐", TextStyle::Title),
            row((
                styled_text(state.mode.title(), TextStyle::Subtitle),
                styled_text("⌄", TextStyle::Body),
            ))
            .id("main.title")
            .spacing(6)
            .align(Alignment::Center),
        ))
        .id("main.title_cluster")
        .spacing(10)
        .align(Alignment::Center),
        row((
            status_badge(state.status_text.clone(), ValidationSeverity::Success)
                .id("main.status")
                .into_view(),
            button(tr("main.settings", "Settings"))
                .id("main.settings")
                .icon(icon::settings())
                .icon_only()
                .tooltip(tr("main.settings", "Settings"))
                .on_press(Message::OpenSettings),
        ))
        .id("main.status_bar")
        .spacing(8)
        .align(Alignment::Center),
    ))
    .id("main.header")
    .tw("p-0 gap-3 w-full items-center justify-between")
    .into_view()
}

fn quick_translate_content(state: &EasydictUiState) -> View<Message> {
    scroll_view(
        column((
            source_text_card(state),
            translate_language_bar(
                "main.quick",
                &state.source_language,
                &state.target_language,
                Message::SourceLanguageChanged,
                Message::TargetLanguageChanged,
            ),
            card(tr("main.results", "Translation Results"))
                .id("main.quick.results_card")
                .content(results_list("main.quick.results", &state.results)),
            styled_text(
                tr_count(
                    "main.completed",
                    "{count} service(s) completed",
                    state.services_completed,
                ),
                TextStyle::Caption,
            ),
        ))
        .id("main.quick.content")
        .tw("p-0 gap-3 w-full"),
    )
    .id("main.quick.scroll")
    .into_view()
}

fn source_text_card(state: &EasydictUiState) -> View<Message> {
    let mut source_card = card(tr("main.source_text", "Source Text"))
        .id("main.quick.input_card")
        .trailing((button("Play source")
            .id("main.quick.play_source")
            .icon(icon::play())
            .icon_only()
            .tooltip("Play source text")
            .on_press(Message::SpeakResult),))
        .content(
            text_editor(state.source_text.clone())
                .id("main.quick.input")
                .placeholder("Enter or paste text to translate...")
                .min_height(150)
                .text_style(TextStyle::BodyLarge)
                .frameless()
                .on_input(Message::SourceTextChanged),
        );

    if let Some(detected_language) = &state.detected_language {
        source_card = source_card.description(detected_language.clone());
    }

    source_card.into_view()
}

fn long_document_content(state: &LongDocumentState) -> View<Message> {
    scroll_view(
        column((
            settings_row("Source Text")
                .id("main.long-doc.input_card")
                .description(state.selected_file.clone())
                .trailing((button("Browse...")
                    .id("main.long-doc.browse")
                    .icon(icon::add())
                    .on_press(Message::BrowseFile),))
                .content(
                    text_editor(state.source_text.clone())
                        .id("main.long-doc.source_text")
                        .placeholder("Paste long text, Markdown, or choose a PDF file.")
                        .min_height(160)
                        .on_input(Message::LongDocumentSourceTextChanged),
                ),
            long_document_control_bar(state),
            settings_row("Translation Result")
                .id("main.long-doc.output_card")
                .description(format!("Output folder: {}", state.output_folder))
                .trailing((button("Retry Failed")
                    .id("main.long-doc.retry")
                    .enabled(false)
                    .on_press(Message::RetryLongDocument),))
                .content(text("Output: {filename}_translated.pdf")),
            text(state.status_text.clone()),
            settings_row("History")
                .id("main.long-doc.history")
                .kind(SettingsRowKind::Expander)
                .trailing((button("Clear")
                    .id("main.long-doc.clear_history")
                    .on_press(Message::ClearHistory),))
                .content(results_list("main.long-doc.history_list", &state.history)),
        ))
        .id("main.long-doc.content")
        .padding(4)
        .spacing(12)
        .width(Length::Fill),
    )
    .id("main.long-doc.scroll")
    .into_view()
}

fn long_document_control_bar(state: &LongDocumentState) -> View<Message> {
    column((
        row((
            combo_box(language_items(true))
                .id("main.long-doc.source_language")
                .label("Source")
                .selected(state.source_language.clone())
                .on_change(Message::LongDocumentSourceLanguageChanged),
            combo_box(language_items(false))
                .id("main.long-doc.target_language")
                .label("Target")
                .selected(state.target_language.clone())
                .on_change(Message::LongDocumentTargetLanguageChanged),
            combo_box(service_items())
                .id("main.long-doc.service")
                .label("Service")
                .selected(state.service.clone())
                .on_change(Message::LongDocumentServiceChanged),
        ))
        .spacing(12)
        .width(Length::Fill),
        row((
            combo_box([
                ComboBoxItem::new("plaintext", "Text"),
                ComboBoxItem::new("markdown", "Markdown"),
                ComboBoxItem::new("pdf", "PDF"),
            ])
            .id("main.long-doc.input_mode")
            .label("Input")
            .selected(state.input_mode.clone())
            .on_change(Message::LongDocumentInputModeChanged),
            combo_box([
                ComboBoxItem::new("mono", "Mono"),
                ComboBoxItem::new("bilingual", "Bilingual"),
                ComboBoxItem::new("both", "Both"),
            ])
            .id("main.long-doc.output_mode")
            .label("Output")
            .selected(state.output_mode.clone())
            .on_change(Message::LongDocumentOutputModeChanged),
            text_editor(state.concurrency.clone())
                .id("main.long-doc.concurrency")
                .placeholder("Threads")
                .on_input(Message::LongDocumentConcurrencyChanged),
            text_editor(state.page_range.clone())
                .id("main.long-doc.page_range")
                .placeholder("1-3,5,7-10")
                .on_input(Message::LongDocumentPageRangeChanged),
        ))
        .spacing(12)
        .width(Length::Fill),
        row((
            toggle_switch("Use document context pass", state.two_pass_context)
                .id("main.long-doc.two_pass")
                .on_toggle(Message::ToggleTwoPassContext),
            primary_button("Translate")
                .id("main.long-doc.translate")
                .icon(icon::translate())
                .on_press(Message::Translate),
        ))
        .spacing(12)
        .align(Alignment::Center),
    ))
    .id("main.long-doc.control_bar")
    .spacing(8)
    .width(Length::Fill)
    .into_view()
}

fn floating_translate_view(
    id_prefix: &'static str,
    state: &FloatingWindowState,
    show_pin: bool,
) -> View<Message> {
    page(state.title.clone())
        .id(format!("{id_prefix}.window"))
        .content(
            column((
                floating_header(id_prefix, state, show_pin),
                settings_row("Source Text")
                    .id(format!("{id_prefix}.input_card"))
                    .content(
                        text_editor(state.text.clone())
                            .id(format!("{id_prefix}.input"))
                            .placeholder("Enter text...")
                            .min_height(56)
                            .max_height(120)
                            .focused(id_prefix == "mini")
                            .on_input(Message::FloatingTextChanged),
                    )
                    .trailing((button("Play source")
                        .id(format!("{id_prefix}.play_source"))
                        .icon(icon::speaker())
                        .icon_only()
                        .tooltip("Play source text")
                        .on_press(Message::SpeakResult),)),
                translate_language_bar(
                    id_prefix,
                    &state.source_language,
                    &state.target_language,
                    Message::SourceLanguageChanged,
                    Message::TargetLanguageChanged,
                ),
                text(state.detected_language.clone().unwrap_or_default()),
                results_list(&format!("{id_prefix}.results"), &state.results),
                text(state.status_text.clone()),
            ))
            .id(format!("{id_prefix}.content"))
            .padding(16)
            .spacing(8)
            .width(Length::Fill)
            .height(Length::Fill),
        )
        .into_view()
}

fn floating_header(
    id_prefix: &'static str,
    state: &FloatingWindowState,
    show_pin: bool,
) -> View<Message> {
    let pin = if show_pin {
        toggle_switch("Pin", state.pinned)
            .id(format!("{id_prefix}.pin"))
            .on_toggle(Message::TogglePin)
    } else {
        text("")
    };

    row((
        pin,
        text(state.title.clone()),
        button("Close")
            .id(format!("{id_prefix}.close"))
            .icon(icon::clear())
            .icon_only()
            .tooltip("Close")
            .on_press(Message::CloseWindow),
    ))
    .id(format!("{id_prefix}.header"))
    .spacing(12)
    .align(Alignment::Center)
    .space_between()
    .into_view()
}

fn translate_language_bar(
    id_prefix: &'static str,
    source_language: &str,
    target_language: &str,
    source_message: fn(String) -> Message,
    target_message: fn(String) -> Message,
) -> View<Message> {
    let is_main = id_prefix.starts_with("main");
    let source_width = if is_main {
        Length::Fixed(260)
    } else {
        Length::Fixed(96)
    };
    let target_width = if is_main {
        Length::Fixed(344)
    } else {
        Length::Fixed(96)
    };

    row((
        combo_box(language_items(true))
            .id(format!("{id_prefix}.source_language"))
            .label("Source Language")
            .selected(source_language.to_string())
            .width(source_width)
            .on_change(source_message),
        button("Swap languages")
            .id(format!("{id_prefix}.swap"))
            .icon(icon::swap())
            .icon_only()
            .tooltip("Swap languages")
            .on_press(Message::SwapLanguages),
        combo_box(language_items(false))
            .id(format!("{id_prefix}.target_language"))
            .label("Target Language")
            .selected(target_language.to_string())
            .width(target_width)
            .on_change(target_message),
        styled_text("?", TextStyle::Body),
        primary_button("")
            .id(format!("{id_prefix}.translate"))
            .icon(icon::translate())
            .tooltip(tr("main.translate", "Translate"))
            .a11y(A11yHint::named(tr("main.translate", "Translate")))
            .on_press(Message::Translate),
    ))
    .id(format!("{id_prefix}.language_bar"))
    .tw("gap-2 w-full items-center")
    .space_between()
    .into_view()
}

fn styled_text(value: impl Into<String>, style: TextStyle) -> View<Message> {
    View::new(ViewToken::Text(TextToken {
        id: None,
        value: value.into(),
        style,
        selectable: false,
        a11y: A11yHint::default(),
    }))
}

fn results_list(id: &str, results: &[TranslationResultPreview]) -> View<Message> {
    result_list(results.iter().map(TranslationResultPreview::to_result_item))
        .id(id)
        .on_copy(Message::CopyResult)
        .on_speak(Message::SpeakResult)
        .on_toggle(Message::ToggleResultExpanded)
        .into_view()
}

fn settings_category_bar(selected: SettingsSection) -> View<Message> {
    command_bar(SettingsSection::ALL.map(|section| settings_category_button(section, selected)))
        .id("settings.categories")
        .into_view()
}

fn settings_category_button(section: SettingsSection, selected: SettingsSection) -> View<Message> {
    button(section.label())
        .id(format!("settings.category.{}", section.id()))
        .icon(section.icon())
        .focused(section == selected)
        .on_press(Message::SettingsSectionChanged(section.id().to_string()))
}

fn settings_section_content(state: &SettingsState) -> View<Message> {
    match state.selected_section {
        SettingsSection::General => settings_general_content(state),
        SettingsSection::Services => settings_services_content(state),
        SettingsSection::Views => settings_views_content(state),
        SettingsSection::Hotkeys => settings_hotkeys_content(),
        SettingsSection::Advanced => settings_advanced_content(),
        SettingsSection::Language => settings_language_content(),
        SettingsSection::About => settings_about_content(),
    }
}

fn settings_general_content(state: &SettingsState) -> View<Message> {
    column((
        text("Behavior"),
        settings_row("App Theme")
            .id("settings.general.theme")
            .description("Choose how Easydict appears. Select System to follow Windows theme.")
            .trailing((combo_box([
                ComboBoxItem::new("system", "System"),
                ComboBoxItem::new("light", "Light"),
                ComboBoxItem::new("dark", "Dark"),
                ComboBoxItem::new("easydict", "Easydict Soft"),
                ComboBoxItem::new("high-contrast", "High Contrast"),
            ])
            .id("settings.general.theme_combo")
            .selected(theme_id(state.theme))
            .on_change(Message::ThemeChanged),)),
        settings_row("Minimize to system tray")
            .id("settings.general.minimize_to_tray")
            .trailing((toggle_switch("On", state.minimize_to_tray)
                .on_toggle(Message::ToggleMinimizeToTray),)),
        settings_row("Start minimized to tray")
            .id("settings.general.start_minimized")
            .trailing((
                toggle_switch("On", state.start_minimized).on_toggle(Message::ToggleStartMinimized),
            )),
        settings_row("Monitor clipboard for text")
            .id("settings.general.monitor_clipboard")
            .trailing((toggle_switch("On", state.monitor_clipboard)
                .on_toggle(Message::ToggleMonitorClipboard),)),
        settings_row("Mouse selection translate")
            .id("settings.general.mouse_selection")
            .description("Show the selection button after selecting text in another app.")
            .trailing((toggle_switch("On", state.mouse_selection_translate)
                .on_toggle(Message::ToggleMouseSelectionTranslate),)),
    ))
    .id("settings.general")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn settings_services_content(state: &SettingsState) -> View<Message> {
    column((
        text("Enabled Services"),
        settings_row("Translation services")
            .id("settings.services.enabled")
            .description(
                "Select which translation services to display in each window. Multiple services run in parallel.",
            )
            .content(results_list(
                "settings.services.enabled_list",
                &[
                    TranslationResultPreview::new("google", "Google Translate", "Enabled"),
                    TranslationResultPreview::new("bing", "Bing Translate", "Enabled"),
                    TranslationResultPreview::new("openai", "OpenAI", "Configured"),
                ],
            )),
        settings_row("MDX dictionaries")
            .id("settings.services.mdx")
            .description("No MDX dictionaries imported")
            .trailing((button("Import")
                .id("settings.services.import_mdx")
                .icon(icon::add())
                .on_press(Message::BrowseFile),)),
        settings_row("Enable International Services")
            .id("settings.services.international")
            .description("Some services require international network access.")
            .trailing((toggle_switch("On", state.enable_international_services)
                .on_toggle(Message::ToggleInternationalServices),)),
        text("Service Configuration"),
        settings_row("Local AI")
            .id("settings.services.local_ai")
            .description("Auto uses the first available local model provider. No cloud API key.")
            .trailing((combo_box([
                ComboBoxItem::new("auto", "Auto"),
                ComboBoxItem::new("phi-silica", "Phi Silica"),
                ComboBoxItem::new("foundry-local", "Foundry Local"),
                ComboBoxItem::new("openvino", "OpenVINO"),
            ])
            .selected("auto")
            .on_change(|_| Message::Noop),)),
        settings_row("OpenAI")
            .id("settings.services.openai")
            .description("Configure API key, endpoint, format, and model.")
            .trailing((button("Test")
                .id("settings.services.openai_test")
                .on_press(Message::Translate),)),
        settings_row("DeepL")
            .id("settings.services.deepl")
            .description("Configure optional API key and quality options.")
            .trailing((button("Test")
                .id("settings.services.deepl_test")
                .on_press(Message::Translate),)),
    ))
    .id("settings.services")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn settings_views_content(state: &SettingsState) -> View<Message> {
    column((
        text("Views"),
        settings_row("Main Window")
            .id("settings.views.main")
            .description("Choose services and ordering for the main result list.")
            .trailing((button("Reorder")
                .id("settings.views.main_reorder")
                .on_press(Message::Noop),)),
        settings_row("Mini Window")
            .id("settings.views.mini")
            .description("Compact floating translation surface.")
            .trailing((toggle_switch("Auto close", state.mini_auto_close)
                .on_toggle(Message::ToggleMiniAutoClose),)),
        settings_row("Fixed Window")
            .id("settings.views.fixed")
            .description("Persistent topmost translation surface.")
            .trailing((toggle_switch("Always on top", state.fixed_always_on_top)
                .on_toggle(Message::ToggleFixedAlwaysOnTop),)),
    ))
    .id("settings.views")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn settings_hotkeys_content() -> View<Message> {
    column((
        text("Hotkeys"),
        hotkey_row("Show or hide main window", "Ctrl+Alt+T"),
        hotkey_row("Translate clipboard", "Ctrl+Alt+D"),
        hotkey_row("OCR screenshot translate", "Ctrl+Alt+S"),
        hotkey_row("Silent OCR", "Ctrl+Alt+Shift+S"),
        hotkey_row("Show mini window with selection", "Ctrl+Alt+M"),
        hotkey_row("Show fixed window", "Ctrl+Alt+F"),
    ))
    .id("settings.hotkeys")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn hotkey_row(title: &'static str, shortcut: &'static str) -> View<Message> {
    settings_row(title)
        .description(shortcut)
        .trailing((button("Record").on_press(Message::Noop),))
        .into_view()
}

fn settings_advanced_content() -> View<Message> {
    column((
        text("Advanced"),
        settings_row("HTTP Proxy")
            .id("settings.advanced.proxy")
            .description("Configure a proxy server for translation requests.")
            .trailing((button("Configure").on_press(Message::Noop),)),
        settings_row("Shell context menu")
            .id("settings.advanced.shell")
            .description("Right-click files or desktop background to start OCR Translate.")
            .trailing((toggle_switch("Enabled", true).on_toggle(|_| Message::Noop),)),
        settings_row("Browser extension")
            .id("settings.advanced.browser")
            .description("Native messaging host used by Chrome and Firefox extensions.")
            .trailing((button("Install").on_press(Message::Noop),)),
        settings_row("Cache")
            .id("settings.advanced.cache")
            .description("Translation cache and local diagnostic data.")
            .trailing((button("Clear").on_press(Message::Noop),)),
    ))
    .id("settings.advanced")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn settings_language_content() -> View<Message> {
    column((
        text("Language"),
        settings_row("Display language")
            .id("settings.language.display")
            .description("Choose the language used by the app UI.")
            .trailing((combo_box([
                ComboBoxItem::new("en-US", "English"),
                ComboBoxItem::new("zh-CN", "Chinese (Simplified)"),
                ComboBoxItem::new("zh-TW", "Chinese (Traditional)"),
                ComboBoxItem::new("ja-JP", "Japanese"),
                ComboBoxItem::new("ko-KR", "Korean"),
            ])
            .selected("en-US")
            .on_change(|_| Message::Noop),)),
        settings_row("OCR language")
            .id("settings.language.ocr")
            .description("Auto uses installed Windows OCR languages.")
            .trailing((combo_box(language_items(true))
                .selected("auto")
                .on_change(|_| Message::Noop),)),
    ))
    .id("settings.language")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn settings_about_content() -> View<Message> {
    column((
        text("About"),
        settings_row("Easydict")
            .id("settings.about.app")
            .description("Free and open-source Windows translation app. GPL-3.0-or-later."),
        settings_row("Version")
            .id("settings.about.version")
            .description(env!("CARGO_PKG_VERSION")),
        settings_row("Source")
            .id("settings.about.source")
            .description("https://github.com/xiaocang/easydict_win32"),
    ))
    .id("settings.about")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn language_items(include_auto: bool) -> Vec<ComboBoxItem> {
    let mut items = Vec::new();
    if include_auto {
        items.push(ComboBoxItem::new(
            "auto",
            tr("main.auto_detect", "Auto Detect"),
        ));
    }
    items.extend([
        ComboBoxItem::new("en", "English"),
        ComboBoxItem::new("zh-Hans", tr("main.target_zh_hans", "Chinese (Simplified)")),
        ComboBoxItem::new("zh-Hant", "Chinese (Traditional)"),
        ComboBoxItem::new("ja", "Japanese"),
        ComboBoxItem::new("ko", "Korean"),
        ComboBoxItem::new("fr", "French"),
        ComboBoxItem::new("de", "German"),
        ComboBoxItem::new("es", "Spanish"),
    ]);
    items
}

fn service_items() -> [ComboBoxItem; 5] {
    [
        ComboBoxItem::new("openai", "OpenAI"),
        ComboBoxItem::new("google", "Google Translate"),
        ComboBoxItem::new("bing", "Bing Translate"),
        ComboBoxItem::new("deepl", "DeepL"),
        ComboBoxItem::new("local-ai", "Local AI"),
    ]
}

fn theme_id(theme: ThemeMode) -> &'static str {
    match theme {
        ThemeMode::System => "system",
        ThemeMode::Light => "light",
        ThemeMode::Dark => "dark",
        ThemeMode::Easydict => "easydict",
        ThemeMode::HighContrast => "high-contrast",
    }
}

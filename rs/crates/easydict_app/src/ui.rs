use crate::compat_protocol::local_ai_provider_modes;
use crate::i18n::{tr, tr_count};
use crate::quick_translate::QuickTranslateSurface;
use crate::state::{
    AppMode, EasydictUiState, FloatingWindowState, HotkeySetting, ImportedMdxDictionary,
    LongDocumentState, Message, ServiceProviderField, ServiceProviderSetting, SettingsLink,
    SettingsSection, SettingsState, TranslationResultPreview, WindowServiceSetting,
    TRANSLATION_LANGUAGE_IDS,
};
use crate::{
    HOTKEY_OCR_TRANSLATE, HOTKEY_SHOW_FIXED, HOTKEY_SHOW_MAIN, HOTKEY_SHOW_MINI, HOTKEY_SILENT_OCR,
    HOTKEY_TRANSLATE_CLIPBOARD,
};
use win_fluent::prelude::*;
use win_fluent::view::TextToken;

pub fn main_window_view(state: &EasydictUiState) -> View<Message> {
    let content = match state.mode {
        AppMode::QuickTranslate => quick_translate_content(state),
        AppMode::LongDocument => long_document_content(&state.long_document, &state.settings),
    };
    let surface = column((main_header(state), content))
        .id("main.surface")
        .tw("p-3 gap-3 w-full h-full");

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
                busy_overlay(surface)
                    .id("ModeSwitchOverlay")
                    .active(state.mode_overlay_active)
                    .opacity(0.86)
                    .label("Switching")
                    .into_view(),
            ))
            .id("main.root")
            .tw("p-0 gap-0 w-full h-full"),
        )
        .into_view()
}

pub fn settings_view(state: &SettingsState) -> View<Message> {
    let mut content_children = vec![
        settings_header(),
        row((
            settings_category_bar(state.selected_section),
            progress_ring()
                .id("SettingsTabSwitchRing")
                .active(false)
                .size(20),
        ))
        .id("settings.tabs_row")
        .spacing(12)
        .align(Alignment::Start)
        .width(Length::Fill)
        .into_view(),
        settings_section_content(state),
    ];

    // Reserve space so the last rows are never hidden behind the floating
    // save bar that is layered on top of the scroll content.
    content_children.push(
        spacer()
            .id("SettingsBottomSpacer")
            .height(Length::Fixed(80))
            .into_view(),
    );

    let scroll = scroll_view(
        column(content_children)
            .id("settings.content")
            .padding(24)
            .spacing(24)
            .width(Length::Fill)
            .tw("max-w-[1040px] mx-auto"),
    )
    .id("MainScrollViewer")
    // UIA automation hook mirroring WinUI `MainScrollViewer.HelpText`.
    .help_text(format!(
        "SelectedSettingsTab:{}",
        state.selected_section.id()
    ))
    .into_view();

    // The save bar floats over the content (bottom-right), and dialogs are true
    // centered modals with a scrim — both via the framework `overlay` primitive
    // rather than being stacked as scroll siblings.
    let mut surface = overlay(scroll).id("settings.root");
    if state.unsaved_changes {
        surface = surface
            .layer(OverlayLayer::new(settings_save_bar()).align(Alignment::End, Alignment::End));
    }
    if let Some(message) = state.save_error_message.as_deref() {
        surface = surface.layer(OverlayLayer::modal(settings_save_error_dialog(message)));
    } else if state.pending_mdx_delete_service_id.is_some() {
        surface = surface.layer(OverlayLayer::modal(settings_mdx_delete_dialog(state)));
    } else if state.show_unsaved_changes_dialog {
        surface = surface.layer(OverlayLayer::modal(settings_unsaved_changes_dialog()));
    }
    if state.settings_runtime.is_loading() {
        // Entry loading overlay (centered 32px ring) shown while the async
        // runtime-status check is in flight.
        surface = surface.layer(
            OverlayLayer::new(settings_loading_indicator())
                .scrim(0.3)
                .blocks_input(true),
        );
    }
    let content = surface.into_view();

    page("Settings")
        .id("settings.window")
        .content(
            column((
                title_bar(tr("app.name", "Easydict"))
                    .id("settings.title_bar")
                    .subtitle(tr("app.beta", "beta"))
                    .icon(icon::translate())
                    .caption_controls(true)
                    .on_minimize(Message::MinimizeWindow)
                    .on_toggle_maximize(Message::ToggleMaximizeWindow)
                    .on_close(Message::CloseWindow),
                content,
            ))
            .id("settings.root_with_title")
            .tw("p-0 gap-0 w-full h-full"),
        )
        .into_view()
}

fn settings_header() -> View<Message> {
    row((
        primary_button("")
            .id("BackButton")
            .icon(win_fluent::IconToken::with_glyph("back", '\u{E72B}'))
            .tooltip("Back")
            .a11y(A11yHint::named("Back"))
            .on_press(Message::Back),
        styled_text_id("SettingsHeaderText", "Settings", TextStyle::Title),
    ))
    .id("settings.header")
    .spacing(16)
    .align(Alignment::Center)
    .into_view()
}

fn settings_loading_indicator() -> View<Message> {
    column((
        progress_ring()
            .id("SettingsLoadingRing")
            .active(true)
            .size(32),
        styled_text_id(
            "SettingsLoadingText",
            "Loading settings…",
            TextStyle::Caption,
        ),
    ))
    .id("settings.loading_overlay")
    .spacing(12)
    .align(Alignment::Center)
    .into_view()
}

fn settings_save_bar() -> View<Message> {
    // Shrink-wrapped around the button and inset from the window edges; the
    // overlay layer positions it bottom-right.
    row((primary_button("Save Settings")
        .id("SaveButton")
        .icon(icon::check())
        .on_press(Message::SaveSettingsChanges),))
    .id("settings.save_floating_bar")
    .tw("shadow-lg m-6")
    .into_view()
}

fn settings_save_error_dialog(message: &str) -> View<Message> {
    dialog("Settings Error")
        .id("settings.error_dialog")
        .kind(DialogKind::Error)
        .content(
            column((
                text(message),
                row((primary_button("OK")
                    .id("settings.error.ok")
                    .on_press(Message::DismissSettingsError),))
                .id("settings.error.actions")
                .align(Alignment::Center),
            ))
            .spacing(12),
        )
        .into_view()
}

fn settings_mdx_delete_dialog(state: &SettingsState) -> View<Message> {
    let dictionary_name = state
        .pending_mdx_delete_service_id
        .as_deref()
        .and_then(|service_id| {
            state
                .imported_mdx_dictionaries
                .iter()
                .find(|dictionary| dictionary.service_id == service_id)
        })
        .map(|dictionary| dictionary.display_name.as_str())
        .unwrap_or("this dictionary");

    dialog("Delete MDX Dictionary")
        .id("MdxDeleteConfirmDialog")
        .kind(DialogKind::Confirmation)
        .content(
            column((
                text(format!("Delete {dictionary_name}?")),
                row((
                    primary_button("Delete")
                        .id("MdxDeleteConfirmButton")
                        .on_press(Message::ConfirmDeleteMdxDictionary),
                    button("Cancel")
                        .id("MdxDeleteCancelButton")
                        .on_press(Message::CancelDeleteMdxDictionary),
                ))
                .id("settings.mdx.delete.actions")
                .spacing(8)
                .align(Alignment::Center),
            ))
            .spacing(12),
        )
        .into_view()
}

fn settings_unsaved_changes_dialog() -> View<Message> {
    dialog("Unsaved Settings")
        .id("settings.unsaved_dialog")
        .kind(DialogKind::Confirmation)
        .content(
            column((
                text("Save your settings changes before leaving?"),
                row((
                    primary_button("Save")
                        .id("settings.unsaved.save")
                        .on_press(Message::SaveSettingsChanges),
                    button("Don't Save")
                        .id("settings.unsaved.discard")
                        .on_press(Message::DiscardSettingsChanges),
                    button("Cancel")
                        .id("settings.unsaved.cancel")
                        .on_press(Message::CancelSettingsChangesDialog),
                ))
                .id("settings.unsaved.actions")
                .spacing(8)
                .align(Alignment::Center),
            ))
            .spacing(12),
        )
        .into_view()
}

pub fn mini_window_view(state: &FloatingWindowState) -> View<Message> {
    mini_window_view_with_settings(state, &SettingsState::default())
}

pub fn fixed_window_view(state: &FloatingWindowState) -> View<Message> {
    fixed_window_view_with_settings(state, &SettingsState::default())
}

pub fn mini_window_view_with_settings(
    state: &FloatingWindowState,
    settings: &SettingsState,
) -> View<Message> {
    floating_translate_view("mini", QuickTranslateSurface::Mini, state, settings, true)
}

pub fn fixed_window_view_with_settings(
    state: &FloatingWindowState,
    settings: &SettingsState,
) -> View<Message> {
    floating_translate_view(
        "fixed",
        QuickTranslateSurface::Fixed,
        state,
        settings,
        false,
    )
}

pub fn capture_overlay_view() -> View<Message> {
    page("Capture Overlay")
        .id("capture.overlay")
        .content(
            column((
                pointer_region(
                    column((
                        text("Capture region"),
                        text("Adjust the selected area before OCR or copy."),
                    ))
                    .id("capture.pointer.content")
                    .padding(12)
                    .spacing(8),
                )
                .id("capture.pointer")
                .height(Length::Fill)
                .on_move(|position| Message::CaptureMouseMoved(capture_point(position)))
                .on_left_down(|position| Message::CaptureLeftButtonDown(capture_point(position)))
                .on_left_up(|position| Message::CaptureLeftButtonUp(capture_point(position)))
                .on_double_click(|position| Message::CaptureDoubleClick(capture_point(position)))
                .on_right_down(Message::CaptureRightButtonDown)
                .on_wheel(|wheel| Message::CaptureMouseWheel {
                    delta: wheel.delta,
                    point: capture_point(wheel.position),
                })
                .on_escape(Message::CaptureEscape),
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

fn capture_point(position: PointerPosition) -> crate::screen_capture::CapturePoint {
    crate::screen_capture::CapturePoint::new(position.x, position.y)
}

pub fn pop_button_view() -> View<Message> {
    page("Selection Translate")
        .id("pop-button.window")
        .content(
            primary_button("Translate selection")
                .id("pop-button.translate")
                .icon(icon::translate())
                .icon_only()
                .floating_action()
                .tooltip("Translate selection")
                .on_press(Message::TranslateSelection),
        )
        .into_view()
}

fn main_header(state: &EasydictUiState) -> View<Message> {
    let minimal = state.settings.theme == ThemeMode::Minimal;
    let mode_icon = match state.mode {
        AppMode::QuickTranslate => "🌐",
        AppMode::LongDocument => "📄",
    };
    let mode_name = match state.mode {
        AppMode::QuickTranslate => "Translate",
        AppMode::LongDocument => "Long Document",
    };
    let mut title_stack_children = vec![row((
        styled_text_id("ModeTitleText", "Easydict", TextStyle::Subtitle),
        flyout_button("")
            .id("ModeMenuButton")
            .selected(state.mode.id())
            .items([
                FlyoutMenuItem::radio(
                    AppMode::QuickTranslate.id(),
                    mode_menu_label(AppMode::QuickTranslate, minimal),
                    state.mode == AppMode::QuickTranslate,
                ),
                FlyoutMenuItem::radio(
                    AppMode::LongDocument.id(),
                    mode_menu_label(AppMode::LongDocument, minimal),
                    state.mode == AppMode::LongDocument,
                ),
            ])
            .a11y(A11yHint::named(format!("Mode: {mode_name}")))
            .on_select(Message::ModeChanged),
    ))
    .id("main.mode_title")
    .spacing(4)
    .align(Alignment::Center)
    .into_view()];
    if state.mode == AppMode::LongDocument {
        title_stack_children.push(styled_text("Long Document", TextStyle::Caption));
    }

    let mut title_cluster_children = Vec::new();
    if !minimal {
        title_cluster_children.push(styled_text(mode_icon, TextStyle::Title));
    }
    title_cluster_children.push(
        column(title_stack_children)
            .id("main.title_stack")
            .spacing(0)
            .into_view(),
    );

    row((
        row(title_cluster_children)
            .id("main.title_cluster")
            .spacing(10)
            .align(Alignment::Center),
        row((
            status_badge(
                state.status_text.clone(),
                state.connection_status.severity(),
            )
            .id("StatusIndicator")
            .into_view(),
            button(tr("main.settings", "Settings"))
                .id("SettingsButton")
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
    let mut content_children = vec![
        source_text_card(state),
        main_translate_action_bar(state),
        card(tr("main.results", "Translation Results"))
            .id("QuickOutputCard")
            .content(results_list(
                "main.quick.results",
                &state.results,
                |id| Message::ToggleResultExpandedIn(QuickTranslateSurface::Main, id),
                |id| Message::CopyResultIn(QuickTranslateSurface::Main, id),
                |id| Message::SpeakResultIn(QuickTranslateSurface::Main, id),
                |id| Message::ReplaceResultIn(QuickTranslateSurface::Main, id),
                |id| Message::RetryResultIn(QuickTranslateSurface::Main, id),
            ))
            .into_view(),
    ];
    if state.settings.theme != ThemeMode::Minimal {
        content_children.push(styled_text(
            tr_count(
                "main.completed",
                "{count} service(s) completed",
                state.services_completed,
            ),
            TextStyle::Caption,
        ));
    }

    scroll_view(
        column(content_children)
            .id("QuickTranslateContent.Content")
            .tw("p-0 gap-3 w-full"),
    )
    .id("QuickTranslateContent")
    .into_view()
}

fn source_text_card(state: &EasydictUiState) -> View<Message> {
    let minimal = state.settings.theme == ThemeMode::Minimal;
    let mut source_children: Vec<View<Message>> = Vec::new();

    if !state.local_dictionary_suggestions.is_empty()
        || state.local_dictionary_suggestion_error.is_some()
    {
        source_children.push(local_dictionary_suggestions_view(state));
    }

    let suggestions_available = !state.local_dictionary_suggestions.is_empty();
    let suggestion_popup_visible =
        suggestions_available || state.local_dictionary_suggestion_error.is_some();
    let mut source_editor = text_editor(state.source_text.clone())
        .id("InputTextBox")
        .placeholder("Enter or paste text to translate...")
        .min_height(150)
        .max_height(260)
        .text_style(TextStyle::BodyLarge)
        .frameless()
        .focused(state.source_text_focused)
        .on_key(
            TextEditorKey::Enter,
            TextEditorKeyModifiers::none(),
            Message::SourceTextSubmitted,
        );

    if suggestions_available {
        source_editor = source_editor
            .on_key(
                TextEditorKey::Tab,
                TextEditorKeyModifiers::none(),
                Message::FocusLocalDictionarySuggestions,
            )
            .on_key(
                TextEditorKey::Tab,
                TextEditorKeyModifiers::shift(),
                Message::ExitLocalDictionarySuggestions,
            )
            .on_key(
                TextEditorKey::ArrowDown,
                TextEditorKeyModifiers::none(),
                Message::MoveLocalDictionarySuggestion(1),
            )
            .on_key(
                TextEditorKey::ArrowUp,
                TextEditorKeyModifiers::none(),
                Message::MoveLocalDictionarySuggestion(-1),
            );
    }

    if suggestion_popup_visible {
        source_editor = source_editor.on_key(
            TextEditorKey::Escape,
            TextEditorKeyModifiers::none(),
            Message::DismissLocalDictionarySuggestions,
        );
    }

    source_children.push(
        source_editor
            .on_input(Message::SourceTextChanged)
            .into_view(),
    );

    let mut source_card = card(tr("main.source_text", "Source Text"))
        .id("QuickInputCard")
        .content(
            column(source_children)
                .id("main.quick.source_content")
                .spacing(8)
                .width(Length::Fill),
        );

    let mut trailing_children = local_dictionary_suggestion_header_buttons(state);
    if !minimal {
        trailing_children.push(
            button("Play source")
                .id("main.quick.play_source")
                .icon(icon::play())
                .icon_only()
                .tooltip("Play source text")
                .on_press(Message::SpeakResult)
                .into_view(),
        );
    }
    if !trailing_children.is_empty() {
        source_card = source_card.trailing(trailing_children);
    }

    if let Some(detected_language) = &state.detected_language {
        source_card = source_card.description(detected_language.clone());
    }

    source_card.into_view()
}

fn local_dictionary_suggestion_header_buttons(state: &EasydictUiState) -> Vec<View<Message>> {
    state
        .local_dictionary_suggestions
        .iter()
        .enumerate()
        .take(2)
        .map(|(index, suggestion)| {
            button(format!(
                "{} · {}",
                suggestion.key, suggestion.dictionary_name
            ))
            .id(format!("main.local_dictionary_suggestions.header.{index}"))
            .focused(state.local_dictionary_suggestion_active_index == Some(index))
            .on_press(Message::ApplyLocalDictionarySuggestion(
                suggestion.key.clone(),
            ))
            .into_view()
        })
        .collect()
}

fn local_dictionary_suggestions_view(state: &EasydictUiState) -> View<Message> {
    let mut children = vec![styled_text("Dictionary suggestions", TextStyle::Caption)];

    if let Some(error) = &state.local_dictionary_suggestion_error {
        children.push(styled_text_id(
            "main.local_dictionary_suggestions.error",
            error.clone(),
            TextStyle::Caption,
        ));
    }

    if !state.local_dictionary_suggestions.is_empty() {
        children.push(
            column(
                state
                    .local_dictionary_suggestions
                    .iter()
                    .enumerate()
                    .take(8)
                    .map(|(index, suggestion)| {
                        button(format!(
                            "{} · {}",
                            suggestion.key, suggestion.dictionary_name
                        ))
                        .id(format!("main.local_dictionary_suggestions.item.{index}"))
                        .focused(state.local_dictionary_suggestion_active_index == Some(index))
                        .on_press(
                            Message::ApplyLocalDictionarySuggestion(suggestion.key.clone()),
                        )
                    })
                    .collect::<Vec<_>>(),
            )
            .id("main.local_dictionary_suggestions.items")
            .spacing(6)
            .into_view(),
        );
    }

    column(children)
        .id("main.local_dictionary_suggestions")
        .spacing(6)
        .width(Length::Fill)
        .into_view()
}
fn long_document_content(state: &LongDocumentState, settings: &SettingsState) -> View<Message> {
    let can_edit = !state.is_translating;
    let theme = settings.theme;
    let output_text = state
        .last_output_path
        .as_deref()
        .map(|path| format!("Output: {path}"))
        .unwrap_or_else(|| {
            format!(
                "Output: {{filename}}_translated{}",
                long_document_output_extension(&state.input_mode)
            )
        });

    scroll_view(
        column((
            settings_row(semantic_header(theme, "📝", "Source Text"))
                .id("main.long-doc.input_card")
                .description(state.selected_file.clone())
                .trailing((button("Browse...")
                    .id("main.long-doc.browse")
                    .icon(icon::add())
                    .enabled(can_edit)
                    .on_press(Message::BrowseFile),))
                .content(
                    text_editor(state.source_text.clone())
                        .id("main.long-doc.source_text")
                        .placeholder("Paste long text, Markdown, or choose a PDF file.")
                        .min_height(160)
                        .enabled(can_edit)
                        .on_input(Message::LongDocumentSourceTextChanged),
                ),
            long_document_control_bar(state, settings),
            settings_row(semantic_header(theme, "⚡", "Translation Result"))
                .id("main.long-doc.output_card")
                .description(format!("Output folder: {}", state.output_folder))
                .trailing((button("Retry Failed")
                    .id("main.long-doc.retry")
                    .enabled(state.last_error.is_some() && !state.is_translating)
                    .on_press(Message::RetryLongDocument),))
                .content(long_document_output_content(state, output_text)),
            text(state.status_text.clone()),
            settings_row(semantic_header(theme, "📑", "History"))
                .id("main.long-doc.history")
                .kind(SettingsRowKind::Expander)
                .trailing((button("Clear")
                    .id("main.long-doc.clear_history")
                    .on_press(Message::ClearHistory),))
                .content(results_list(
                    "main.long-doc.history_list",
                    &state.history,
                    Message::ToggleResultExpanded,
                    |_| Message::Noop,
                    |_| Message::Noop,
                    |_| Message::Noop,
                    |_| Message::Noop,
                )),
        ))
        .id("main.long-doc.content")
        .padding(4)
        .spacing(12)
        .width(Length::Fill),
    )
    .id("main.long-doc.scroll")
    .into_view()
}

fn long_document_output_content(state: &LongDocumentState, output_text: String) -> View<Message> {
    let mut children = vec![text(output_text)];

    if let Some(percentage) = state.progress_percentage {
        children.push(text(format!("Progress: {:.0}%", percentage)));
    }

    if let Some(detail) = state.progress_detail.as_deref() {
        children.push(text(detail.to_string()));
    }

    if let Some(block) = state.last_translated_block.as_deref() {
        children.push(text(format!("Latest block: {block}")));
    }

    column(children)
        .id("main.long-doc.output_content")
        .spacing(6)
        .width(Length::Fill)
        .into_view()
}

fn long_document_output_extension(input_mode: &str) -> &'static str {
    match input_mode.trim().to_ascii_lowercase().as_str() {
        "markdown" | "md" => ".md",
        "plaintext" | "plain" | "text" | "txt" => ".txt",
        _ => ".pdf",
    }
}

fn long_document_control_bar(state: &LongDocumentState, settings: &SettingsState) -> View<Message> {
    let can_edit = !state.is_translating;

    column((
        row((
            combo_box(selected_language_items(true, settings))
                .id("main.long-doc.source_language")
                .label("Source")
                .selected(state.source_language.clone())
                .enabled(can_edit)
                .on_change(Message::LongDocumentSourceLanguageChanged),
            combo_box(selected_language_items(false, settings))
                .id("main.long-doc.target_language")
                .label("Target")
                .selected(state.target_language.clone())
                .enabled(can_edit)
                .on_change(Message::LongDocumentTargetLanguageChanged),
            combo_box(service_items())
                .id("main.long-doc.service")
                .label("Service")
                .selected(state.service.clone())
                .enabled(can_edit)
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
            .enabled(can_edit)
            .on_change(Message::LongDocumentInputModeChanged),
            combo_box([
                ComboBoxItem::new("mono", "Mono"),
                ComboBoxItem::new("bilingual", "Bilingual"),
                ComboBoxItem::new("both", "Both"),
            ])
            .id("main.long-doc.output_mode")
            .label("Output")
            .selected(state.output_mode.clone())
            .enabled(can_edit)
            .on_change(Message::LongDocumentOutputModeChanged),
            text_editor(state.concurrency.clone())
                .id("main.long-doc.concurrency")
                .placeholder("Threads")
                .enabled(can_edit)
                .on_input(Message::LongDocumentConcurrencyChanged),
            text_editor(state.page_range.clone())
                .id("main.long-doc.page_range")
                .placeholder("1-3,5,7-10")
                .enabled(can_edit)
                .on_input(Message::LongDocumentPageRangeChanged),
        ))
        .spacing(12)
        .width(Length::Fill),
        row((
            toggle_switch("Use document context pass", state.two_pass_context)
                .id("main.long-doc.two_pass")
                .enabled(can_edit)
                .on_toggle(Message::ToggleTwoPassContext),
            primary_button("Translate")
                .id("main.long-doc.translate")
                .icon(icon::translate())
                .enabled(!state.is_translating)
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
    surface: QuickTranslateSurface,
    state: &FloatingWindowState,
    settings: &SettingsState,
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
                            .on_input(move |value| {
                                Message::FloatingSurfaceTextChanged(surface, value)
                            }),
                    )
                    .trailing((button("Play source")
                        .id(format!("{id_prefix}.play_source"))
                        .icon(icon::speaker())
                        .icon_only()
                        .tooltip("Play source text")
                        .on_press(Message::SpeakResult),)),
                translate_language_bar(
                    id_prefix,
                    surface,
                    &state.source_language,
                    &state.target_language,
                    settings,
                    state.is_translating,
                ),
                text(state.detected_language.clone().unwrap_or_default()),
                results_list(
                    &format!("{id_prefix}.results"),
                    &state.results,
                    move |id| Message::ToggleResultExpandedIn(surface, id),
                    move |id| Message::CopyResultIn(surface, id),
                    move |id| Message::SpeakResultIn(surface, id),
                    move |id| Message::ReplaceResultIn(surface, id),
                    move |id| Message::RetryResultIn(surface, id),
                ),
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

fn main_translate_action_bar(state: &EasydictUiState) -> View<Message> {
    adaptive_switch(
        500,
        main_translate_action_bar_wide(state),
        main_translate_action_bar_narrow(state),
    )
    .id("main.quick.action_bar")
    .into_view()
}

fn main_translate_action_bar_wide(state: &EasydictUiState) -> View<Message> {
    let mut children = vec![
        combo_box(selected_language_items(true, &state.settings))
            .id("SourceLangCombo")
            .label("Source Language")
            .selected(state.source_language.clone())
            .width(Length::Fixed(200))
            .on_change(Message::SourceLanguageChanged),
        button("Swap languages")
            .id("SwapLanguageButton")
            .icon(icon::swap())
            .icon_only()
            .tooltip("Swap source and target languages")
            .on_press(Message::SwapLanguages),
        combo_box(selected_language_items(true, &state.settings))
            .id("TargetLangCombo")
            .label("Target Language")
            .selected(state.target_language.clone())
            .width(Length::Fixed(200))
            .on_change(Message::TargetLanguageChanged),
    ];
    if state.settings.theme != ThemeMode::Minimal {
        children.push(language_help_button());
    }
    children.push(main_translate_button(
        "TranslateButton",
        state.is_translating,
    ));

    row(children)
        .id("ActionBarWide")
        .tw("gap-2 w-full items-center")
        .space_between()
        .into_view()
}

fn main_translate_action_bar_narrow(state: &EasydictUiState) -> View<Message> {
    let mut language_row_children = vec![
        combo_box(selected_language_items(true, &state.settings))
            .id("SourceLangComboNarrow")
            .label("Source Language")
            .selected(state.source_language.clone())
            .width(Length::Fill)
            .on_change(Message::SourceLanguageChanged),
        button("Swap languages")
            .id("SwapLanguageButtonNarrow")
            .icon(icon::swap())
            .icon_only()
            .tooltip("Swap source and target languages")
            .on_press(Message::SwapLanguages),
        combo_box(selected_language_items(true, &state.settings))
            .id("TargetLangComboNarrow")
            .label("Target Language")
            .selected(state.target_language.clone())
            .width(Length::Fill)
            .on_change(Message::TargetLanguageChanged),
    ];
    if state.settings.theme != ThemeMode::Minimal {
        language_row_children.push(language_help_button());
    }

    column((
        row(language_row_children)
            .id("ActionBarNarrow.LanguageRow")
            .tw("gap-1 w-full items-center"),
        main_translate_button("TranslateButtonNarrow", state.is_translating),
    ))
    .id("ActionBarNarrow")
    .spacing(4)
    .align(Alignment::Center)
    .width(Length::Fill)
    .into_view()
}

fn main_translate_button(id: &'static str, is_loading: bool) -> View<Message> {
    if is_loading {
        progress_ring()
            .id(id)
            .size(20)
            .a11y(A11yHint::named("Translating"))
            .into_view()
    } else {
        primary_button("")
            .id(id)
            .icon(icon::translate())
            .tooltip(tr("main.translate", "Translate"))
            .a11y(A11yHint::named(tr("main.translate", "Translate")))
            .on_press(Message::QuickTranslate)
    }
}

fn language_help_button() -> View<Message> {
    button("Language help")
        .id("LanguageHelpButton")
        .icon(icon::help())
        .icon_only()
        .tooltip("Language help")
        .enabled(false)
        .into_view()
}

fn mode_menu_label(mode: AppMode, minimal: bool) -> &'static str {
    match (mode, minimal) {
        (AppMode::QuickTranslate, true) => "Translate",
        (AppMode::LongDocument, true) => "Long Document",
        (AppMode::QuickTranslate, false) => "🌐  Translate",
        (AppMode::LongDocument, false) => "📄  Long Document",
    }
}

fn semantic_header(theme: ThemeMode, icon: &'static str, label: &'static str) -> String {
    if theme == ThemeMode::Minimal {
        label.to_string()
    } else {
        format!("{icon} {label}")
    }
}

fn translate_language_bar(
    id_prefix: &'static str,
    surface: QuickTranslateSurface,
    source_language: &str,
    target_language: &str,
    settings: &SettingsState,
    is_translating: bool,
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
        combo_box(selected_language_items(true, settings))
            .id(format!("{id_prefix}.source_language"))
            .label("Source Language")
            .selected(source_language.to_string())
            .width(source_width)
            .on_change(move |value| Message::FloatingSourceLanguageChanged(surface, value)),
        button("Swap languages")
            .id(format!("{id_prefix}.swap"))
            .icon(icon::swap())
            .icon_only()
            .tooltip("Swap languages")
            .on_press(Message::SwapFloatingLanguages(surface)),
        combo_box(selected_language_items(true, settings))
            .id(format!("{id_prefix}.target_language"))
            .label("Target Language")
            .selected(target_language.to_string())
            .width(target_width)
            .on_change(move |value| Message::FloatingTargetLanguageChanged(surface, value)),
        styled_text("?", TextStyle::Body),
        floating_translate_button(format!("{id_prefix}.translate"), surface, is_translating),
    ))
    .id(format!("{id_prefix}.language_bar"))
    .tw("gap-2 w-full items-center")
    .space_between()
    .into_view()
}

fn floating_translate_button(
    id: String,
    surface: QuickTranslateSurface,
    is_loading: bool,
) -> View<Message> {
    if is_loading {
        progress_ring()
            .id(id)
            .size(20)
            .a11y(A11yHint::named("Translating"))
            .into_view()
    } else {
        primary_button("")
            .id(id)
            .icon(icon::translate())
            .tooltip(tr("main.translate", "Translate"))
            .a11y(A11yHint::named(tr("main.translate", "Translate")))
            .on_press(Message::QuickTranslateIn(surface))
    }
}

fn reveal_secret_button(id: impl Into<String>, label: &str) -> View<Message> {
    button("")
        .id(id)
        .icon(icon::search())
        .tooltip(label)
        .a11y(A11yHint::named(label))
        .on_press(Message::Noop)
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

fn styled_text_id(
    id: impl Into<String>,
    value: impl Into<String>,
    style: TextStyle,
) -> View<Message> {
    View::new(ViewToken::Text(TextToken {
        id: Some(id.into()),
        value: value.into(),
        style,
        selectable: false,
        a11y: A11yHint::default(),
    }))
}

fn results_list(
    id: &str,
    results: &[TranslationResultPreview],
    toggle_message: impl Fn(String) -> Message + Send + Sync + 'static,
    copy_message: impl Fn(String) -> Message + Send + Sync + 'static,
    speak_message: impl Fn(String) -> Message + Send + Sync + 'static,
    replace_message: impl Fn(String) -> Message + Send + Sync + 'static,
    retry_message: impl Fn(String) -> Message + Send + Sync + 'static,
) -> View<Message> {
    result_list(results.iter().map(TranslationResultPreview::to_result_item))
        .id(id)
        .on_copy_item(copy_message)
        .on_speak_item(speak_message)
        .on_replace_item(replace_message)
        .on_retry_item(retry_message)
        .on_toggle(toggle_message)
        .into_view()
}

fn settings_category_bar(selected: SettingsSection) -> View<Message> {
    // Wrap the tab tiles with a 7-column cap (WinUI `ItemsWrapGrid
    // MaximumRowsOrColumns=7`); the framework handles row wrapping instead of a
    // hand-rolled `[0..5]/[5..]` split.
    let buttons: Vec<View<Message>> = SettingsSection::ALL
        .iter()
        .copied()
        .map(|section| settings_category_button(section, selected))
        .collect();

    wrap(buttons)
        .id("settings.categories")
        .max_columns(7)
        .spacing(10)
        .into_view()
}

fn settings_category_button(section: SettingsSection, selected: SettingsSection) -> View<Message> {
    button(section.label())
        .id(format!("SettingsTab_{}", section.label()))
        .icon(section.icon())
        .tile()
        .tooltip(section.label())
        .selected(section == selected)
        .on_press(Message::SettingsSectionChanged(section.id().to_string()))
}

fn settings_section_content(state: &SettingsState) -> View<Message> {
    match state.selected_section {
        SettingsSection::General => settings_general_content(state),
        SettingsSection::Services => settings_services_content(state),
        SettingsSection::Views => settings_views_content(state),
        SettingsSection::Hotkeys => settings_hotkeys_content(state),
        SettingsSection::Advanced => settings_advanced_content(state),
        SettingsSection::Language => settings_language_content(state),
        SettingsSection::About => settings_about_content(),
    }
}

fn settings_general_content(state: &SettingsState) -> View<Message> {
    let mut children: Vec<View<Message>> = vec![
        styled_text_id(
            "SettingsGeneralBehaviorHeader",
            "Behavior",
            TextStyle::Subtitle,
        ),
        settings_row("App Theme")
            .id("settings.general.theme")
            .description("Choose how Easydict appears. Select System to follow Windows theme.")
            .trailing((combo_box([
                ComboBoxItem::new("system", "System"),
                ComboBoxItem::new("light", "Light"),
                ComboBoxItem::new("dark", "Dark"),
                ComboBoxItem::new("minimal", "Minimal"),
            ])
            .id("AppThemeCombo")
            .selected(theme_id(state.theme))
            .on_change(Message::ThemeChanged),))
            .into_view(),
        settings_row("Minimize to system tray")
            .id("settings.general.minimize_to_tray")
            .trailing((toggle_switch("On", state.minimize_to_tray)
                .on_toggle(Message::ToggleMinimizeToTray),))
            .into_view(),
        settings_row("Start minimized to tray")
            .id("settings.general.start_minimized")
            .trailing((
                toggle_switch("On", state.start_minimized).on_toggle(Message::ToggleStartMinimized),
            ))
            .into_view(),
        settings_row("Monitor clipboard for text")
            .id("settings.general.monitor_clipboard")
            .trailing((toggle_switch("On", state.monitor_clipboard)
                .on_toggle(Message::ToggleMonitorClipboard),))
            .into_view(),
        settings_row("Always on top")
            .id("settings.general.always_on_top")
            .trailing((toggle_switch("On", state.fixed_always_on_top)
                .on_toggle(Message::ToggleFixedAlwaysOnTop),))
            .into_view(),
        settings_row("Launch at Windows startup")
            .id("settings.general.launch_at_startup")
            .trailing((toggle_switch("On", state.launch_at_startup)
                .id("LaunchAtStartupToggle")
                .on_toggle(Message::ToggleLaunchAtStartup),))
            .into_view(),
        settings_row("Mouse selection translate")
            .id("settings.general.mouse_selection")
            .description("Show the selection button after selecting text in another app.")
            .trailing((toggle_switch("On", state.mouse_selection_translate)
                .id("MouseSelectionTranslateToggle")
                .on_toggle(Message::ToggleMouseSelectionTranslate),))
            .into_view(),
    ];

    if state.mouse_selection_translate {
        children.push(mouse_selection_excluded_apps_panel(state));
    }

    children.push(local_dictionary_suggestions_row(state));

    children.push(
        settings_row("Hide dictionaries with no result")
            .id("settings.general.hide_empty_service_results")
            .description("Collapse dictionary rows when a service reports no result.")
            .trailing((toggle_switch("On", state.hide_empty_service_results)
                .on_toggle(Message::ToggleHideEmptyServiceResults),))
            .into_view(),
    );

    children.extend([
        styled_text_id("SettingsGeneralTtsHeader", "TTS", TextStyle::Subtitle),
        settings_row("TTS speed")
            .id("settings.general.tts_speed")
            .description("Adjust speech rate for source and translation playback.")
            .content(
                row((
                    slider(tts_speed_value(&state.tts_speed))
                        .id("TtsSpeedSlider")
                        .range(0.5, 3.0)
                        .step(0.5)
                        .width(Length::Fixed(250))
                        .a11y(A11yHint::named("TTS speed"))
                        .on_change(|value| Message::TtsSpeedChanged(format_tts_speed(value))),
                    styled_text_id(
                        "TtsSpeedValueText",
                        format!("{}x", format_tts_speed(tts_speed_value(&state.tts_speed))),
                        TextStyle::Body,
                    ),
                ))
                .id("settings.general.tts_speed.control")
                .spacing(12)
                .align(Alignment::Center),
            )
            .into_view(),
        settings_row("Auto play translation")
            .id("settings.general.auto_play_translation")
            .description("Play translated text after a translation finishes.")
            .trailing((toggle_switch("On", state.auto_play_translation)
                .id("AutoPlayTranslationToggle")
                .on_toggle(Message::ToggleAutoPlayTranslation),))
            .into_view(),
    ]);

    column(children)
        .id("settings.general")
        .spacing(12)
        .width(Length::Fill)
        .into_view()
}

fn local_dictionary_suggestions_row(state: &SettingsState) -> View<Message> {
    settings_row("Enable custom dictionary input suggestions")
        .id("settings.general.local_dictionary_suggestions")
        .description(if state.imported_mdx_dictionaries.is_empty() {
            "Import an MDX dictionary to enable local input suggestions."
        } else {
            "Suggest local dictionary entries while typing."
        })
        .content(styled_text_id(
            "LocalDictionarySuggestionsExperimentalText",
            "Experimental",
            TextStyle::Caption,
        ))
        .trailing((toggle_switch("On", state.local_dictionary_suggestions)
            .id("EnableCustomDictionaryInputSuggestionsToggle")
            .enabled(!state.imported_mdx_dictionaries.is_empty())
            .on_toggle(Message::ToggleLocalDictionarySuggestions),))
        .into_view()
}

fn tts_speed_value(value: &str) -> f32 {
    snap_tts_speed(value.trim().parse::<f32>().unwrap_or(1.0))
}

fn format_tts_speed(value: f32) -> String {
    format!("{:.1}", snap_tts_speed(value))
}

fn snap_tts_speed(value: f32) -> f32 {
    if !value.is_finite() {
        return 1.0;
    }

    ((value.clamp(0.5, 3.0) * 2.0).round() / 2.0).clamp(0.5, 3.0)
}

fn mouse_selection_excluded_apps_panel(state: &SettingsState) -> View<Message> {
    column((
        text_editor(state.mouse_selection_excluded_apps.clone())
            .id("MouseSelectionExcludedAppsBox")
            .placeholder("code, slack, discord")
            .max_height(36)
            .on_input(Message::MouseSelectionExcludedAppsChanged),
        styled_text_id(
            "MouseSelectionExcludedAppsDescriptionText",
            "Process names to exclude, separated by commas.",
            TextStyle::Caption,
        ),
    ))
    .id("MouseSelectionExcludedAppsPanel")
    .padding(8)
    .spacing(4)
    .width(Length::Fill)
    .into_view()
}

fn settings_services_content(state: &SettingsState) -> View<Message> {
    let mdx_row = settings_row("MDX dictionaries")
        .id("settings.services.mdx")
        .description("Import custom MDX dictionaries for local lookup and suggestions.")
        .trailing((button("Import")
            .id("ImportMdxDictionaryButton")
            .icon(icon::add())
            .on_press(Message::ImportMdxDictionary),))
        .content(
            column((
                styled_text_id(
                    "ImportedMdxSummaryText",
                    mdx_dictionary_summary(state),
                    TextStyle::Caption,
                ),
                imported_mdx_config_panel(state),
            ))
            .id("settings.services.mdx.content")
            .spacing(8),
        );

    let mut children: Vec<View<Message>> = vec![
        text("Enabled Services").into_view(),
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
                |_| Message::Noop,
                |_| Message::Noop,
                |_| Message::Noop,
                |_| Message::Noop,
                |_| Message::Noop,
            ))
            .into_view(),
        mdx_row.into_view(),
        settings_row("Enable International Services")
            .id("settings.services.international")
            .description("Some services require international network access.")
            .content(styled_text_id(
                "EnableInternationalServicesDescriptionText",
                "Some services require international network access.",
                TextStyle::Caption,
            ))
            .trailing((
                styled_text_id(
                    "EnableInternationalServicesHeaderText",
                    "Enable International Services",
                    TextStyle::Caption,
                ),
                toggle_switch("On", state.enable_international_services)
                    .id("EnableInternationalServicesToggle")
                    .on_toggle(Message::ToggleInternationalServices),
            ))
            .into_view(),
        text("Service Configuration").into_view(),
        local_ai_service_expander(state),
        ollama_service_expander(state),
        open_ai_service_expander(state),
        deepl_service_expander(state),
    ];
    children.extend(
        llm_provider_descriptors()
            .iter()
            .map(|descriptor| llm_provider_service_expander(state, descriptor)),
    );
    children.extend(traditional_http_service_expanders(state));
    children.push(no_config_services_section());

    column(children)
        .id("settings.services")
        .spacing(12)
        .width(Length::Fill)
        .into_view()
}

fn imported_mdx_config_panel(state: &SettingsState) -> View<Message> {
    column(
        state
            .imported_mdx_dictionaries
            .iter()
            .map(imported_mdx_dictionary_expander)
            .collect::<Vec<_>>(),
    )
    .id("ImportedMdxConfigPanel")
    .spacing(8)
    .into_view()
}

fn imported_mdx_dictionary_expander(dictionary: &ImportedMdxDictionary) -> View<Message> {
    let service_id = dictionary.service_id.clone();
    let mut content = vec![
        styled_text_id(
            format!("MdxFilePathText.{service_id}"),
            format!("File: {}", dictionary.file_path),
            TextStyle::Caption,
        ),
        styled_text_id(
            format!("MdxMddPathsText.{service_id}"),
            mdx_mdd_summary(dictionary),
            TextStyle::Caption,
        ),
    ];

    if dictionary.is_encrypted {
        content.push(
            text_editor(dictionary.email.clone().unwrap_or_default())
                .id(format!("MdxEmailBox.{service_id}"))
                .placeholder("Email")
                .max_height(36)
                .on_input({
                    let service_id = service_id.clone();
                    move |value| Message::MdxDictionaryEmailChanged(service_id.clone(), value)
                })
                .into_view(),
        );
        content.push(
            text_editor(dictionary.regcode.clone().unwrap_or_default())
                .id(format!("MdxRegcodeBox.{service_id}"))
                .placeholder("Registration code")
                .max_height(36)
                .on_input({
                    let service_id = service_id.clone();
                    move |value| Message::MdxDictionaryRegcodeChanged(service_id.clone(), value)
                })
                .into_view(),
        );
        content.push(styled_text(
            "Encrypted dictionaries may require email and registration code.",
            TextStyle::Caption,
        ));
    }

    content.push(
        row((
            button("Rescan MDD")
                .id(format!("RescanMdxMddFilesButton.{service_id}"))
                .icon(icon::refresh())
                .on_press(Message::RescanMdxMddFiles(service_id.clone())),
            button("Delete")
                .id(format!("DeleteMdxDictionaryButton.{service_id}"))
                .icon(icon::delete())
                .on_press(Message::RequestDeleteMdxDictionary(service_id.clone())),
        ))
        .spacing(8)
        .into_view(),
    );

    settings_row(dictionary.display_name.clone())
        .id(format!("ImportedMdxDictionaryExpander.{service_id}"))
        .kind(SettingsRowKind::Expander)
        .description(dictionary.service_id.clone())
        .trailing((styled_text(
            if dictionary.is_encrypted {
                "Encrypted"
            } else {
                "Ready"
            },
            TextStyle::Caption,
        ),))
        .content(
            column(content)
                .id(format!("ImportedMdxDictionaryContent.{service_id}"))
                .spacing(8),
        )
        .into_view()
}

fn mdx_mdd_summary(dictionary: &ImportedMdxDictionary) -> String {
    match dictionary.mdd_file_paths.len() {
        0 => "MDD resources: none discovered".to_string(),
        1 => format!("MDD resources: {}", dictionary.mdd_file_paths[0]),
        count => format!("MDD resources: {count} files"),
    }
}

fn local_ai_service_expander(state: &SettingsState) -> View<Message> {
    settings_row("Windows Local AI")
        .id("WindowsLocalAIExpander")
        .kind(SettingsRowKind::Expander)
        .description(local_ai_provider_description(&state.local_ai_provider))
        .trailing((styled_text_id(
            "WindowsLocalAIStatusBadge",
            state.local_ai_status.clone(),
            TextStyle::Caption,
        ),))
        .content(
            column(vec![
                column(vec![
                    styled_text_id("LocalAIProviderLabelText", "Provider", TextStyle::Caption),
                    combo_box(local_ai_provider_items())
                        .id("LocalAIProviderCombo")
                        .label("Windows Local AI provider")
                        .width(Length::Fixed(520))
                        .selected(state.local_ai_provider.as_str())
                        .on_change(Message::LocalAiProviderChanged)
                        .into_view(),
                    local_ai_provider_rating_row(
                        "LocalAIProviderWindowsAIItem",
                        "LocalAIProviderWindowsAIRatingText",
                        "Phi Silica (Copilot+ PC)",
                        "5 stars",
                        "Best quality when available. Uses Phi Silica on supported Copilot+ PCs.",
                    ),
                    local_ai_provider_rating_row(
                        "LocalAIProviderFoundryLocalItem",
                        "LocalAIProviderFoundryLocalRatingText",
                        "Foundry Local",
                        "4 stars",
                        "Good local LLM fallback. Requires Foundry Local and a loaded model.",
                    ),
                    local_ai_provider_rating_row(
                        "LocalAIProviderOpenVINOItem",
                        "LocalAIProviderOpenVINORatingText",
                        "OpenVINO (NLLB-200)",
                        "2 stars",
                        "Basic offline translation fallback. Hardware acceleration is best effort.",
                    ),
                ])
                .id("LocalAIProviderPanel")
                .spacing(6)
                .into_view(),
                styled_text_id(
                    "WindowsLocalAIDescriptionText",
                    local_ai_provider_description(&state.local_ai_provider),
                    TextStyle::Caption,
                ),
                windows_ai_config_panel(state),
                foundry_local_config_panel(state),
                open_vino_config_panel(state),
            ])
            .id("settings.services.local_ai.content")
            .spacing(12),
        )
        .into_view()
}

fn local_ai_provider_rating_row(
    row_id: &'static str,
    rating_id: &'static str,
    label: &'static str,
    rating: &'static str,
    tooltip: &'static str,
) -> View<Message> {
    row((
        text(label).into_view(),
        styled_text_id(rating_id, rating, TextStyle::Caption),
        styled_text(tooltip, TextStyle::Caption).into_view(),
    ))
    .id(row_id)
    .spacing(8)
    .align(Alignment::Center)
    .into_view()
}

fn windows_ai_config_panel(state: &SettingsState) -> View<Message> {
    column(vec![
        row((
            styled_text_id(
                "WindowsLocalAISectionTitleText",
                "Phi Silica",
                TextStyle::BodyStrong,
            ),
            styled_text_id(
                "WindowsLocalAISectionRatingText",
                "5 stars",
                TextStyle::Caption,
            ),
        ))
        .spacing(8)
        .align(Alignment::Center)
        .into_view(),
        status_badge(
            state.local_ai_status.clone(),
            local_ai_status_severity(&state.local_ai_status),
        )
        .id("WindowsLocalAIStatusBar")
        .into_view(),
        button("Prepare model")
            .id("WindowsLocalAIPrepareButton")
            .icon(icon::refresh())
            .on_press(Message::PrepareLocalAiModel)
            .into_view(),
        column(vec![
            styled_text_id(
                "WindowsLocalAIPrepareProgressText",
                state.local_ai_prepare_progress.clone(),
                TextStyle::Caption,
            ),
            progress_ring()
                .id("WindowsLocalAIPrepareProgressBar")
                .active(state.local_ai_prepare_progress != "Idle")
                .size(16)
                .label(state.local_ai_prepare_progress.clone())
                .into_view(),
            button("Track download progress in Windows Update")
                .id("WindowsLocalAIWindowsUpdateLink")
                .icon(icon::settings())
                .on_press(Message::OpenWindowsAiUpdate)
                .into_view(),
        ])
        .id("WindowsLocalAIPrepareProgressPanel")
        .spacing(4)
        .into_view(),
    ])
    .id("WindowsLocalAIConfigPanel")
    .spacing(10)
    .into_view()
}

fn foundry_local_config_panel(state: &SettingsState) -> View<Message> {
    column(vec![
        row((
            styled_text_id("FoundryLocalTitleText", "Foundry Local", TextStyle::BodyStrong),
            styled_text_id("FoundryLocalRatingText", "4 stars", TextStyle::Caption),
        ))
        .spacing(8)
        .align(Alignment::Center)
        .into_view(),
        text_editor(state.foundry_local_endpoint.clone())
            .id("FoundryLocalEndpointBox")
            .placeholder("Auto-detect from foundry service status")
            .max_height(36)
            .on_input(Message::FoundryLocalEndpointChanged)
            .into_view(),
        text_editor(state.foundry_local_model.clone())
            .id("FoundryLocalModelBox")
            .placeholder("qwen2.5-0.5b")
            .max_height(36)
            .on_input(Message::FoundryLocalModelChanged)
            .into_view(),
        status_badge(
            state.foundry_local_status.clone(),
            ValidationSeverity::Info,
        )
        .id("FoundryLocalStatusBar")
        .into_view(),
        row((
            button("Start Foundry Local")
                .id("FoundryLocalStartButton")
                .icon(icon::play())
                .on_press(Message::StartFoundryLocal),
            button("Install Foundry Local")
                .id("FoundryLocalInstallLink")
                .icon(icon::add())
                .on_press(Message::InstallFoundryLocal),
            button("Install/use docs")
                .id("FoundryLocalDocsLink")
                .icon(icon::help())
                .on_press(Message::OpenFoundryLocalDocs),
        ))
        .spacing(8)
        .into_view(),
        styled_text_id(
            "FoundryLocalDescriptionText",
            "Leave endpoint empty to auto-detect it from foundry service status; set it manually if the local service uses a fixed OpenAI-compatible endpoint.",
            TextStyle::Caption,
        ),
    ])
    .id("FoundryLocalConfigPanel")
    .spacing(10)
    .into_view()
}

fn open_vino_config_panel(state: &SettingsState) -> View<Message> {
    column(vec![
        row((
            styled_text_id(
                "OpenVinoTitleText",
                "OpenVINO (local NLLB)",
                TextStyle::BodyStrong,
            ),
            styled_text_id("OpenVinoRatingText", "2 stars", TextStyle::Caption),
            styled_text_id(
                "OpenVinoStatusBadge",
                state.open_vino_status.clone(),
                TextStyle::Caption,
            ),
        ))
        .spacing(8)
        .align(Alignment::Center)
        .into_view(),
        combo_box(open_vino_device_items())
            .id("OpenVinoDeviceCombo")
            .label("Device")
            .selected(state.open_vino_device.as_str())
            .on_change(Message::OpenVinoDeviceChanged)
            .into_view(),
        status_badge(state.open_vino_status.clone(), ValidationSeverity::Info)
            .id("OpenVinoStatusBar")
            .into_view(),
        row((
            progress_ring()
                .id("OpenVinoDownloadProgress")
                .active(state.open_vino_download_progress != "Idle")
                .size(16)
                .label(state.open_vino_download_progress.clone()),
            styled_text_id(
                "OpenVinoDownloadProgressText",
                state.open_vino_download_progress.clone(),
                TextStyle::Caption,
            ),
        ))
        .spacing(8)
        .align(Alignment::Center)
        .into_view(),
        button("Download model")
            .id("OpenVinoDownloadButton")
            .icon(icon::refresh())
            .on_press(Message::DownloadOpenVinoModel)
            .into_view(),
        styled_text_id(
            "OpenVinoDescriptionText",
            "Runs NLLB-200 locally with ONNX Runtime + OpenVINO. Hardware acceleration is best effort and falls back to CPU when needed.",
            TextStyle::Caption,
        ),
    ])
    .id("OpenVinoConfigPanel")
    .spacing(10)
    .into_view()
}

fn local_ai_status_severity(status: &str) -> ValidationSeverity {
    if status.contains("requested") {
        ValidationSeverity::Info
    } else {
        ValidationSeverity::Success
    }
}

fn local_ai_provider_description(provider: &str) -> &'static str {
    match provider {
        local_ai_provider_modes::WINDOWS_AI => {
            "Uses Phi Silica on Copilot+ PCs through Windows AI APIs. No API key, no network."
        }
        local_ai_provider_modes::FOUNDRY_LOCAL => {
            "Runs local models through the Microsoft Foundry Local OpenAI-compatible endpoint. Requires Foundry Local installed and a local model available."
        }
        local_ai_provider_modes::OPENVINO => {
            "Runs NLLB-200 locally with ONNX Runtime + OpenVINO. Hardware acceleration is best effort; unsupported graph parts fall back to CPU. No API key, no network."
        }
        _ => {
            "Auto tries Phi Silica first, then Foundry Local, then OpenVINO/NLLB as the local translation fallback. No cloud API key."
        }
    }
}

fn local_ai_provider_items() -> [ComboBoxItem; 4] {
    [
        ComboBoxItem::new(
            local_ai_provider_modes::AUTO,
            "Auto (Phi Silica -> Foundry Local -> OpenVINO)",
        ),
        ComboBoxItem::new(
            local_ai_provider_modes::WINDOWS_AI,
            "Phi Silica (Copilot+ PC) - 5 stars",
        ),
        ComboBoxItem::new(
            local_ai_provider_modes::FOUNDRY_LOCAL,
            "Foundry Local - 4 stars",
        ),
        ComboBoxItem::new(
            local_ai_provider_modes::OPENVINO,
            "OpenVINO (NLLB-200, local) - 2 stars",
        ),
    ]
}

fn open_vino_device_items() -> [ComboBoxItem; 4] {
    [
        ComboBoxItem::new("Auto", "Auto"),
        ComboBoxItem::new("NPU", "NPU"),
        ComboBoxItem::new("GPU", "GPU"),
        ComboBoxItem::new("CPU", "CPU"),
    ]
}

fn ollama_service_expander(state: &SettingsState) -> View<Message> {
    settings_row("Ollama (Local LLM)")
        .id("OllamaServiceExpander")
        .kind(SettingsRowKind::Expander)
        .description("Local OpenAI-compatible endpoint")
        .trailing((styled_text_id(
            "OllamaStatusText",
            state.ollama_status.clone(),
            TextStyle::Caption,
        ),))
        .content(
            column((
                text_editor(state.ollama_endpoint.clone())
                    .id("OllamaEndpointBox")
                    .placeholder("http://localhost:11434/v1/chat/completions")
                    .max_height(36)
                    .on_input(Message::OllamaEndpointChanged),
                combo_box(ollama_model_items())
                    .id("OllamaModelCombo")
                    .label("Model")
                    .selected(state.ollama_model.as_str())
                    .on_change(Message::OllamaModelChanged),
                row((
                    button("Refresh")
                        .id("RefreshOllamaButton")
                        .icon(icon::refresh())
                        .on_press(Message::RefreshOllamaModels),
                    button("Test")
                        .id("TestOllamaButton")
                        .icon(icon::play())
                        .on_press(Message::TestOllama),
                ))
                .spacing(8),
                styled_text(
                    "Ollama must be running locally. Refresh records a model-list request for the runtime bridge.",
                    TextStyle::Caption,
                ),
            ))
            .id("settings.services.ollama.content")
            .spacing(8),
        )
        .into_view()
}

fn open_ai_service_expander(state: &SettingsState) -> View<Message> {
    settings_row("OpenAI")
        .id("OpenAIServiceExpander")
        .kind(SettingsRowKind::Expander)
        .description(open_ai_configuration_summary(state))
        .trailing((styled_text_id(
            "OpenAIStatusText",
            state.open_ai_test_status.clone(),
            TextStyle::Caption,
        ),))
        .content(
            column(vec![
                styled_text_id("OpenAIKeyHeaderText", "API Key", TextStyle::Caption),
                text_editor(state.open_ai_api_key.clone())
                    .id("OpenAIKeyBox")
                    .placeholder("sk-...")
                    .max_height(36)
                    .on_input(Message::OpenAIApiKeyChanged)
                    .into_view(),
                reveal_secret_button("OpenAIKeyRevealButton", "Reveal API key"),
                text_editor(state.open_ai_endpoint.clone())
                    .id("OpenAIEndpointBox")
                    .placeholder("https://api.openai.com/v1/responses")
                    .max_height(36)
                    .on_input(Message::OpenAIEndpointChanged)
                    .into_view(),
                combo_box(open_ai_api_format_items())
                    .id("OpenAIApiFormatCombo")
                    .label("API Format")
                    .selected(state.open_ai_api_format_override.as_str())
                    .on_change(Message::OpenAIApiFormatChanged)
                    .into_view(),
                styled_text_id(
                    "OpenAIDetectedFormatText",
                    open_ai_detected_format_text(state),
                    TextStyle::Caption,
                ),
                combo_box(open_ai_model_items())
                    .id("OpenAIModelCombo")
                    .label("Model")
                    .selected(state.open_ai_model.as_str())
                    .on_change(Message::OpenAIModelChanged)
                    .into_view(),
                styled_text(
                    "Auto-detect picks /responses for Responses API endpoints; otherwise it uses Chat Completions.",
                    TextStyle::Caption,
                ),
                button("Test")
                    .id("TestOpenAIButton")
                    .icon(icon::play())
                    .on_press(Message::TestOpenAI)
                    .into_view(),
            ])
            .id("settings.services.openai.content")
            .spacing(8),
        )
        .into_view()
}

struct LlmProviderDescriptor {
    service_id: &'static str,
    title: &'static str,
    expander_id: &'static str,
    status_id: &'static str,
    key_header_id: &'static str,
    key_box_id: &'static str,
    key_reveal_id: &'static str,
    key_label: &'static str,
    key_placeholder: &'static str,
    endpoint_box_id: Option<&'static str>,
    endpoint_placeholder: &'static str,
    model_box_id: &'static str,
    test_button_id: &'static str,
    description: &'static str,
    default_endpoint: &'static str,
    default_model: &'static str,
    model_options: &'static [&'static str],
}

fn llm_provider_service_expander(
    state: &SettingsState,
    descriptor: &LlmProviderDescriptor,
) -> View<Message> {
    let setting = service_provider_setting(state, descriptor);
    let mut content = vec![
        styled_text_id(
            descriptor.key_header_id,
            descriptor.key_label,
            TextStyle::Caption,
        ),
        text_editor(setting.api_key.clone())
            .id(descriptor.key_box_id)
            .placeholder(descriptor.key_placeholder)
            .max_height(36)
            .on_input({
                let service_id = descriptor.service_id.to_string();
                move |value| {
                    Message::ServiceProviderSettingChanged(
                        service_id.clone(),
                        ServiceProviderField::ApiKey,
                        value,
                    )
                }
            })
            .into_view(),
        reveal_secret_button(descriptor.key_reveal_id, "Reveal secret"),
    ];

    if let Some(endpoint_box_id) = descriptor.endpoint_box_id {
        content.push(
            text_editor(setting.endpoint.clone())
                .id(endpoint_box_id)
                .placeholder(descriptor.endpoint_placeholder)
                .max_height(36)
                .on_input({
                    let service_id = descriptor.service_id.to_string();
                    move |value| {
                        Message::ServiceProviderSettingChanged(
                            service_id.clone(),
                            ServiceProviderField::Endpoint,
                            value,
                        )
                    }
                })
                .into_view(),
        );
    }

    content.extend([
        combo_box(provider_model_items(descriptor))
            .id(descriptor.model_box_id)
            .label("Model")
            .selected(setting.model.as_str())
            .on_change({
                let service_id = descriptor.service_id.to_string();
                move |value| {
                    Message::ServiceProviderSettingChanged(
                        service_id.clone(),
                        ServiceProviderField::Model,
                        value,
                    )
                }
            })
            .into_view(),
        styled_text(descriptor.description, TextStyle::Caption),
        button("Test")
            .id(descriptor.test_button_id)
            .icon(icon::play())
            .on_press(Message::TestServiceProvider(
                descriptor.service_id.to_string(),
            ))
            .into_view(),
    ]);

    settings_row(descriptor.title)
        .id(descriptor.expander_id)
        .kind(SettingsRowKind::Expander)
        .description(format!("{} · {}", descriptor.default_model, setting.model))
        .trailing((styled_text_id(
            descriptor.status_id,
            setting.status,
            TextStyle::Caption,
        ),))
        .content(
            column(content)
                .id(format!(
                    "settings.services.{}.content",
                    descriptor.service_id
                ))
                .spacing(8),
        )
        .into_view()
}

fn service_provider_setting(
    state: &SettingsState,
    descriptor: &LlmProviderDescriptor,
) -> ServiceProviderSetting {
    state
        .service_provider_settings
        .iter()
        .find(|setting| setting.service_id == descriptor.service_id)
        .cloned()
        .unwrap_or_else(|| {
            ServiceProviderSetting::new(
                descriptor.service_id,
                descriptor.default_endpoint,
                descriptor.default_model,
            )
        })
}

fn provider_model_items(descriptor: &LlmProviderDescriptor) -> Vec<ComboBoxItem> {
    descriptor
        .model_options
        .iter()
        .map(|model| ComboBoxItem::new(*model, *model))
        .collect()
}

fn no_config_services_section() -> View<Message> {
    let mut service_rows = vec![
        no_config_service_row("FreeServiceGoogleTranslateRow", "Google Translate"),
        no_config_service_row("FreeServiceGoogleDictRow", "Google Dict"),
    ];

    service_rows.extend(linguee_no_config_service_rows());

    settings_row("Free Services")
        .id("settings.services.free_services")
        .description("No API key required")
        .content(
            column(vec![
                styled_text_id(
                    "FreeServicesHeaderText",
                    "Free Services (No Configuration Required)",
                    TextStyle::BodyStrong,
                ),
                row(service_rows)
                    .id("settings.services.free_services.rows")
                    .spacing(16)
                    .into_view(),
                styled_text_id(
                    "FreeServicesDescriptionText",
                    "Google Translate and Google Dict work out of the box without API keys.",
                    TextStyle::Caption,
                ),
            ])
            .id("settings.services.free_services.content")
            .spacing(8),
        )
        .into_view()
}

#[cfg(feature = "enable-linguee-service")]
fn linguee_no_config_service_rows() -> Vec<View<Message>> {
    vec![no_config_service_row(
        "LingueeFreeServicePanel",
        "Linguee Dictionary",
    )]
}

#[cfg(not(feature = "enable-linguee-service"))]
fn linguee_no_config_service_rows() -> Vec<View<Message>> {
    Vec::new()
}

fn no_config_service_row(id: &'static str, label: &'static str) -> View<Message> {
    row((
        status_badge("Ready", ValidationSeverity::Success)
            .id(format!("{id}.status"))
            .into_view(),
        text(label).into_view(),
    ))
    .id(id)
    .spacing(6)
    .align(Alignment::Center)
    .into_view()
}

fn traditional_http_service_expanders(state: &SettingsState) -> [View<Message>; 4] {
    [
        caiyun_service_expander(state),
        niu_trans_service_expander(state),
        youdao_service_expander(state),
        volcano_service_expander(state),
    ]
}

fn caiyun_service_expander(state: &SettingsState) -> View<Message> {
    settings_row("Caiyun")
        .id("CaiyunServiceExpander")
        .kind(SettingsRowKind::Expander)
        .description("API token required")
        .trailing((styled_text_id(
            "CaiyunStatusText",
            state.caiyun_status.clone(),
            TextStyle::Caption,
        ),))
        .content(
            column(vec![
                styled_text_id("CaiyunKeyHeaderText", "API Key", TextStyle::Caption),
                text_editor(state.caiyun_api_key.clone())
                    .id("CaiyunKeyBox")
                    .placeholder("Enter your Caiyun API key")
                    .max_height(36)
                    .on_input(Message::CaiyunApiKeyChanged)
                    .into_view(),
                reveal_secret_button("CaiyunKeyRevealButton", "Reveal API key"),
                styled_text(
                    "Get your API key from fanyi.caiyunapp.com.",
                    TextStyle::Caption,
                ),
                button("Test")
                    .id("TestCaiyunButton")
                    .icon(icon::play())
                    .on_press(Message::TestCaiyun)
                    .into_view(),
            ])
            .id("settings.services.caiyun.content")
            .spacing(8),
        )
        .into_view()
}

fn niu_trans_service_expander(state: &SettingsState) -> View<Message> {
    settings_row("NiuTrans")
        .id("NiuTransServiceExpander")
        .kind(SettingsRowKind::Expander)
        .description("API key required")
        .trailing((styled_text_id(
            "NiuTransStatusText",
            state.niu_trans_status.clone(),
            TextStyle::Caption,
        ),))
        .content(
            column(vec![
                styled_text_id("NiuTransKeyHeaderText", "API Key", TextStyle::Caption),
                text_editor(state.niu_trans_api_key.clone())
                    .id("NiuTransKeyBox")
                    .placeholder("Enter your NiuTrans API key")
                    .max_height(36)
                    .on_input(Message::NiuTransApiKeyChanged)
                    .into_view(),
                reveal_secret_button("NiuTransKeyRevealButton", "Reveal API key"),
                styled_text(
                    "NiuTrans supports 450+ language pairs. Get your API key from niutrans.com.",
                    TextStyle::Caption,
                ),
                button("Test")
                    .id("TestNiuTransButton")
                    .icon(icon::play())
                    .on_press(Message::TestNiuTrans)
                    .into_view(),
            ])
            .id("settings.services.niutrans.content")
            .spacing(8),
        )
        .into_view()
}

fn youdao_service_expander(state: &SettingsState) -> View<Message> {
    settings_row("Youdao")
        .id("YoudaoServiceExpander")
        .kind(SettingsRowKind::Expander)
        .description(if state.youdao_use_official_api {
            "Official API mode"
        } else {
            "Web dictionary mode"
        })
        .trailing((styled_text_id(
            "YoudaoStatusText",
            state.youdao_status.clone(),
            TextStyle::Caption,
        ),))
        .content(
            column(vec![
                styled_text_id("YoudaoAppKeyHeaderText", "App Key", TextStyle::Caption),
                text_editor(state.youdao_app_key.clone())
                    .id("YoudaoAppKeyBox")
                    .placeholder("Enter your Youdao App Key")
                    .max_height(36)
                    .on_input(Message::YoudaoAppKeyChanged)
                    .into_view(),
                reveal_secret_button("YoudaoAppKeyRevealButton", "Reveal app key"),
                styled_text_id("YoudaoAppSecretHeaderText", "App Secret", TextStyle::Caption),
                text_editor(state.youdao_app_secret.clone())
                    .id("YoudaoAppSecretBox")
                    .placeholder("Enter your Youdao App Secret")
                    .max_height(36)
                    .on_input(Message::YoudaoAppSecretChanged)
                    .into_view(),
                reveal_secret_button("YoudaoAppSecretRevealButton", "Reveal app secret"),
                toggle_switch("Use Official API", state.youdao_use_official_api)
                    .id("YoudaoUseOfficialApiToggle")
                    .on_toggle(Message::ToggleYoudaoUseOfficialApi)
                    .into_view(),
                styled_text(
                    "Without API keys, Youdao uses the free web dictionary. With keys, official API mode is available.",
                    TextStyle::Caption,
                ),
                button("Test")
                    .id("TestYoudaoButton")
                    .icon(icon::play())
                    .on_press(Message::TestYoudao)
                    .into_view(),
            ])
            .id("settings.services.youdao.content")
            .spacing(8),
        )
        .into_view()
}

fn volcano_service_expander(state: &SettingsState) -> View<Message> {
    settings_row("Volcano")
        .id("VolcanoServiceExpander")
        .kind(SettingsRowKind::Expander)
        .description("Access Key ID and Secret Access Key required")
        .trailing((styled_text_id(
            "VolcanoStatusText",
            state.volcano_status.clone(),
            TextStyle::Caption,
        ),))
        .content(
            column(vec![
                styled_text_id(
                    "VolcanoAccessKeyIdHeaderText",
                    "Access Key ID",
                    TextStyle::Caption,
                ),
                text_editor(state.volcano_access_key_id.clone())
                    .id("VolcanoAccessKeyIdBox")
                    .placeholder("Enter your Volcano Access Key ID")
                    .max_height(36)
                    .on_input(Message::VolcanoAccessKeyIdChanged)
                    .into_view(),
                reveal_secret_button("VolcanoAccessKeyIdRevealButton", "Reveal access key"),
                styled_text_id(
                    "VolcanoSecretAccessKeyHeaderText",
                    "Secret Access Key",
                    TextStyle::Caption,
                ),
                text_editor(state.volcano_secret_access_key.clone())
                    .id("VolcanoSecretAccessKeyBox")
                    .placeholder("Enter your Volcano Secret Access Key")
                    .max_height(36)
                    .on_input(Message::VolcanoSecretAccessKeyChanged)
                    .into_view(),
                reveal_secret_button("VolcanoSecretAccessKeyRevealButton", "Reveal secret key"),
                styled_text(
                    "Volcano translation uses signed OpenAPI requests from translate.volcengineapi.com.",
                    TextStyle::Caption,
                ),
                button("Test")
                    .id("TestVolcanoButton")
                    .icon(icon::play())
                    .on_press(Message::TestVolcano)
                    .into_view(),
            ])
            .id("settings.services.volcano.content")
            .spacing(8),
        )
        .into_view()
}

fn deepl_service_expander(state: &SettingsState) -> View<Message> {
    settings_row("DeepL")
        .id("DeepLServiceExpander")
        .kind(SettingsRowKind::Expander)
        .description(deepl_configuration_summary(state))
        .trailing((button("Test")
            .id("TestDeepLButton")
            .on_press(Message::Translate),))
        .content(
            column((
                text_editor(state.deepl_api_key.clone())
                    .id("DeepLKeyBox")
                    .placeholder("Enter your DeepL API key")
                    .max_height(36)
                    .on_input(Message::DeepLApiKeyChanged),
                button("")
                    .id("DeepLKeyRevealButton")
                    .icon(icon::search())
                    .tooltip("Reveal API key")
                    .a11y(A11yHint::named("Reveal API key"))
                    .on_press(Message::Noop),
                toggle_switch("Use Free API", state.deepl_use_free_api)
                    .id("DeepLFreeCheck")
                    .enabled(!state.deepl_use_quality_optimized)
                    .on_toggle(Message::ToggleDeepLUseFreeApi),
                toggle_switch(
                    "Use quality-optimized model",
                    state.deepl_use_quality_optimized,
                )
                .id("DeepLQualityCheck")
                .on_toggle(Message::ToggleDeepLUseQualityOptimized),
                styled_text_id(
                    "DeepLDescriptionText",
                    "Configure optional API key and quality options. Quality-optimized mode requires an API key.",
                    TextStyle::Caption,
                ),
            ))
            .id("settings.services.deepl.content")
            .spacing(8),
        )
        .into_view()
}

fn open_ai_configuration_summary(state: &SettingsState) -> String {
    format!(
        "{} · {}",
        state.open_ai_model,
        open_ai_detected_format_text(state)
    )
}

fn open_ai_detected_format_text(state: &SettingsState) -> &'static str {
    match state.open_ai_api_format_override.as_str() {
        "Responses" => "Pinned format: Responses API",
        "ChatCompletions" => "Pinned format: Chat Completions API",
        _ if state
            .open_ai_endpoint
            .trim()
            .trim_end_matches('/')
            .ends_with("/responses") =>
        {
            "Detected format: Responses API"
        }
        _ => "Detected format: Chat Completions API",
    }
}

fn deepl_configuration_summary(state: &SettingsState) -> String {
    if state.deepl_use_quality_optimized {
        "Quality-optimized mode".to_string()
    } else if state.deepl_use_free_api {
        "Free API mode".to_string()
    } else {
        "Standard API mode".to_string()
    }
}

fn mdx_dictionary_summary(state: &SettingsState) -> String {
    match state.imported_mdx_dictionaries.len() {
        0 => "No MDX dictionaries imported".to_string(),
        1 => "1 MDX dictionary imported".to_string(),
        count => format!("{count} MDX dictionaries imported"),
    }
}

fn settings_views_content(state: &SettingsState) -> View<Message> {
    column((
        styled_text_id("WindowResultsHeaderText", "Window Results", TextStyle::Subtitle),
        styled_text_id(
            "WindowResultsDescriptionText",
            "Choose which results appear in each window, and whether each result is queried automatically.",
            TextStyle::Caption,
        ),
        settings_panel(
            "WindowResultsSection",
            vec![
                settings_view_window_results_section(
                    "Main Window",
                    "settings.views.main",
                    "Choose services and ordering for the main result list.",
                    "MainWindowReorderModeButton",
                    "main",
                    QuickTranslateSurface::Main,
                    state.main_window_reorder_mode,
                    &state.main_window_services,
                ),
                settings_view_window_results_section(
                    "Mini Window",
                    "settings.views.mini",
                    "Choose services and ordering for the compact floating result list.",
                    "MiniWindowReorderModeButton",
                    "mini",
                    QuickTranslateSurface::Mini,
                    state.mini_window_reorder_mode,
                    &state.mini_window_services,
                ),
                settings_view_behavior_row(
                    "settings.views.mini.behavior",
                    "Mini Window behavior",
                    "Close the Mini window automatically after focus moves away.",
                    "Auto close",
                    state.mini_auto_close,
                    Message::ToggleMiniAutoClose,
                ),
                settings_view_window_results_section(
                    "Fixed Window",
                    "settings.views.fixed",
                    "Choose services and ordering for the persistent result list.",
                    "FixedWindowReorderModeButton",
                    "fixed",
                    QuickTranslateSurface::Fixed,
                    state.fixed_window_reorder_mode,
                    &state.fixed_window_services,
                ),
                settings_view_behavior_row(
                    "settings.views.fixed.behavior",
                    "Fixed Window behavior",
                    "Keep the Fixed window above other windows.",
                    "Always on top",
                    state.fixed_always_on_top,
                    Message::ToggleFixedAlwaysOnTop,
                ),
            ],
        ),
    ))
    .id("settings.views")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn settings_view_window_results_section(
    title: &'static str,
    section_id: &'static str,
    description: &'static str,
    reorder_button_id: &'static str,
    control_prefix: &'static str,
    surface: QuickTranslateSurface,
    reorder_mode: bool,
    services: &[WindowServiceSetting],
) -> View<Message> {
    column((
        row((
            column((
                styled_text(title, TextStyle::Subtitle),
                styled_text(description, TextStyle::Caption),
            ))
            .id(format!("{section_id}.header_text"))
            .spacing(4)
            .width(Length::Fill),
            button(if reorder_mode { "Done" } else { "Reorder" })
                .id(reorder_button_id)
                .on_press(Message::ToggleWindowReorderMode(surface)),
        ))
        .id(format!("{section_id}.header"))
        .spacing(12)
        .align(Alignment::Center)
        .width(Length::Fill),
        window_service_rows(control_prefix, surface, services, reorder_mode),
    ))
    .id(section_id)
    .spacing(10)
    .width(Length::Fill)
    .into_view()
}

fn settings_view_behavior_row(
    row_id: &'static str,
    title: &'static str,
    description: &'static str,
    toggle_label: &'static str,
    checked: bool,
    message: impl Fn(bool) -> Message + Send + Sync + 'static,
) -> View<Message> {
    row((
        column((
            styled_text(title, TextStyle::BodyStrong),
            styled_text(description, TextStyle::Caption),
        ))
        .id(format!("{row_id}.text"))
        .spacing(4)
        .width(Length::Fill),
        toggle_switch(toggle_label, checked).on_toggle(message),
    ))
    .id(row_id)
    .spacing(12)
    .align(Alignment::Center)
    .width(Length::Fill)
    .into_view()
}

fn window_service_rows(
    control_prefix: &'static str,
    surface: QuickTranslateSurface,
    services: &[WindowServiceSetting],
    reorder_mode: bool,
) -> View<Message> {
    let rows = services
        .iter()
        .enumerate()
        .map(|(index, service)| {
            window_service_row(
                control_prefix,
                surface,
                service,
                index,
                services.len(),
                reorder_mode,
            )
        })
        .collect::<Vec<_>>();

    column(rows)
        .id(format!("{control_prefix}.service_list"))
        .spacing(6)
        .into_view()
}

fn window_service_row(
    control_prefix: &'static str,
    surface: QuickTranslateSurface,
    service: &WindowServiceSetting,
    index: usize,
    service_count: usize,
    reorder_mode: bool,
) -> View<Message> {
    let control_id = service_control_id(&service.service_id);
    let mut trailing: Vec<View<Message>> = Vec::new();
    let service_id = service.service_id.clone();
    trailing.push(
        toggle_switch("Enabled", service.enabled)
            .id(format!("{control_prefix}.{control_id}.enabled"))
            .on_toggle(move |enabled| {
                Message::ToggleWindowService(surface, service_id.clone(), enabled)
            }),
    );

    if service.enabled {
        let service_id = service.service_id.clone();
        trailing.push(
            toggle_switch("EnabledQuery", service.enabled_query)
                .id(format!("{control_prefix}.{control_id}.enabled_query"))
                .on_toggle(move |enabled_query| {
                    Message::ToggleWindowServiceQuery(surface, service_id.clone(), enabled_query)
                }),
        );
    }

    if reorder_mode {
        trailing.push(
            button("")
                .id(format!("{control_prefix}.{control_id}.move_up"))
                .icon(win_fluent::IconToken::with_glyph("move-up", '\u{E70E}'))
                .tooltip("Move up")
                .icon_only()
                .enabled(index > 0)
                .on_press(Message::MoveWindowService(
                    surface,
                    service.service_id.clone(),
                    -1,
                )),
        );
        trailing.push(
            button("")
                .id(format!("{control_prefix}.{control_id}.move_down"))
                .icon(win_fluent::IconToken::with_glyph("move-down", '\u{E70D}'))
                .tooltip("Move down")
                .icon_only()
                .enabled(index + 1 < service_count)
                .on_press(Message::MoveWindowService(
                    surface,
                    service.service_id.clone(),
                    1,
                )),
        );
    }

    row((
        column((
            styled_text(service.display_name.clone(), TextStyle::BodyStrong),
            styled_text(
                if service.configured {
                    "Configured service"
                } else {
                    "Not configured"
                },
                TextStyle::Caption,
            ),
        ))
        .id(format!("{control_prefix}.service.{control_id}.text"))
        .spacing(2)
        .width(Length::Fill),
        row(trailing)
            .id(format!("{control_prefix}.service.{control_id}.controls"))
            .spacing(8)
            .align(Alignment::Center),
    ))
    .id(format!("{control_prefix}.service.{control_id}"))
    .spacing(12)
    .align(Alignment::Center)
    .width(Length::Fill)
    .into_view()
}

fn service_control_id(service_id: &str) -> String {
    service_id
        .chars()
        .map(|ch| {
            if ch.is_ascii_alphanumeric() {
                ch.to_ascii_lowercase()
            } else {
                '_'
            }
        })
        .collect()
}

fn settings_hotkeys_content(state: &SettingsState) -> View<Message> {
    column((
        text("Hotkeys"),
        hotkey_row(
            "Show Window",
            "settings.hotkeys.show_window",
            "ShowHotkeyBox",
            "ShowHotkeyEnabledToggle",
            "Ctrl+Alt+T",
            HOTKEY_SHOW_MAIN,
            &state.show_main_hotkey,
            false,
        ),
        hotkey_row(
            "Translate Clipboard",
            "settings.hotkeys.translate_clipboard",
            "TranslateClipboardHotkeyBox",
            "TranslateClipboardHotkeyEnabledToggle",
            "Ctrl+Alt+D",
            HOTKEY_TRANSLATE_CLIPBOARD,
            &state.translate_clipboard_hotkey,
            false,
        ),
        hotkey_row(
            "Show Mini Window",
            "settings.hotkeys.show_mini",
            "ShowMiniHotkeyBox",
            "ShowMiniHotkeyEnabledToggle",
            "Ctrl+Alt+M",
            HOTKEY_SHOW_MINI,
            &state.show_mini_hotkey,
            true,
        ),
        hotkey_row(
            "Show Fixed Window",
            "settings.hotkeys.show_fixed",
            "ShowFixedHotkeyBox",
            "ShowFixedHotkeyEnabledToggle",
            "Ctrl+Alt+F",
            HOTKEY_SHOW_FIXED,
            &state.show_fixed_hotkey,
            true,
        ),
        hotkey_row(
            "OCR Screenshot Translate",
            "settings.hotkeys.ocr_translate",
            "OcrTranslateHotkeyBox",
            "OcrTranslateHotkeyEnabledToggle",
            "Ctrl+Alt+S",
            HOTKEY_OCR_TRANSLATE,
            &state.ocr_translate_hotkey,
            false,
        ),
        hotkey_row(
            "Silent OCR",
            "settings.hotkeys.silent_ocr",
            "SilentOcrHotkeyBox",
            "SilentOcrHotkeyEnabledToggle",
            "Ctrl+Alt+Shift+S",
            HOTKEY_SILENT_OCR,
            &state.silent_ocr_hotkey,
            false,
        ),
    ))
    .id("settings.hotkeys")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn hotkey_row(
    title: &'static str,
    row_id: &'static str,
    box_id: &'static str,
    toggle_id: &'static str,
    placeholder: &'static str,
    hotkey_id: &'static str,
    setting: &HotkeySetting,
    derived_shift_toggle: bool,
) -> View<Message> {
    let toggle_hotkey_id = hotkey_id.to_string();
    let editor_hotkey_id = hotkey_id.to_string();

    settings_row(title)
        .id(row_id)
        .description(hotkey_row_description(setting, derived_shift_toggle))
        .trailing((
            toggle_switch("Enabled", setting.enabled)
                .id(toggle_id)
                .on_toggle(move |value| Message::ToggleHotkey(toggle_hotkey_id.clone(), value)),
            row((text_editor(setting.shortcut.clone())
                .id(box_id)
                .placeholder(placeholder)
                .max_height(36)
                .on_input(move |value| {
                    Message::HotkeyShortcutChanged(editor_hotkey_id.clone(), value)
                }),))
            .id(format!("{box_id}.field"))
            .width(Length::Fixed(580)),
        ))
        .into_view()
}

fn hotkey_row_description(setting: &HotkeySetting, derived_shift_toggle: bool) -> String {
    let shortcut = if setting.shortcut.trim().is_empty() {
        "Not set"
    } else {
        setting.shortcut.trim()
    };

    if derived_shift_toggle {
        format!("{shortcut} - toggle hotkey adds Shift after saving settings.")
    } else {
        format!("{shortcut} - applied after saving settings.")
    }
}

fn settings_panel(id: impl Into<String>, children: Vec<View<Message>>) -> View<Message> {
    column(children)
        .id(id)
        .tw("surface-card rounded-lg border p-6 gap-4 w-full")
        .into_view()
}

fn settings_form_field(
    id: impl Into<String>,
    label: impl Into<String>,
    description: Option<&str>,
    control: impl IntoView<Message>,
) -> View<Message> {
    let mut children = vec![styled_text(label, TextStyle::BodyLarge)];

    if let Some(description) = description {
        children.push(styled_text(description, TextStyle::Caption));
    }

    children.push(control.into_view());

    column(children)
        .id(id)
        .spacing(8)
        .width(Length::Fill)
        .into_view()
}

fn settings_advanced_content(state: &SettingsState) -> View<Message> {
    let mut ocr_fields: Vec<View<Message>> = vec![
        settings_form_field(
            "settings.advanced.ocr",
            "OCR Engine",
            Some("Choose the OCR provider used by screenshot translation."),
            combo_box(ocr_engine_items())
                .id("OcrEngineCombo")
                .selected(state.ocr_engine.clone())
                .width(Length::Fixed(300))
                .on_change(Message::OcrEngineChanged),
        ),
        settings_form_field(
            "settings.advanced.ocr.language",
            "OCR Language",
            Some("Auto uses installed Windows OCR languages."),
            combo_box(all_language_items(true))
                .id("OcrLanguageCombo")
                .selected(state.ocr_language.clone())
                .width(Length::Fixed(300))
                .on_change(Message::OcrLanguageChanged),
        ),
    ];

    if is_advanced_ocr_engine(&state.ocr_engine) {
        ocr_fields.push(settings_form_field(
            "settings.advanced.ocr.connection",
            "OCR Connection",
            Some("Advanced OCR engines use a local VLM or OpenAI-compatible API."),
            column((
                row((
                    text_editor(state.ocr_api_key.clone())
                        .id("OcrApiKeyBox")
                        .placeholder("API key")
                        .on_input(Message::OcrApiKeyChanged),
                    button("Reveal")
                        .id("OcrApiKeyRevealButton")
                        .on_press(Message::Noop),
                ))
                .spacing(8),
                text_editor(state.ocr_endpoint.clone())
                    .id("OcrEndpointBox")
                    .placeholder("Endpoint")
                    .on_input(Message::OcrEndpointChanged),
                text_editor(state.ocr_model.clone())
                    .id("OcrModelBox")
                    .placeholder("Model")
                    .on_input(Message::OcrModelChanged),
                text_editor(state.ocr_system_prompt.clone())
                    .id("OcrSystemPromptBox")
                    .placeholder("System prompt")
                    .min_height(80)
                    .on_input(Message::OcrSystemPromptChanged),
                row((
                    button("Test OCR Connection")
                        .id("TestOcrConnectionButton")
                        .on_press(Message::TestOcrConnection),
                    text_editor(state.ocr_test_result.clone())
                        .id("OcrTestResultBox")
                        .read_only(true),
                ))
                .spacing(8),
            ))
            .id("settings.advanced.ocr.connection")
            .spacing(8)
            .into_view(),
        ));
    }

    let mut children: Vec<View<Message>> = vec![
        styled_text_id(
            "AdvancedOcrHeaderText",
            "OCR (Text Recognition) Settings",
            TextStyle::Subtitle,
        ),
        settings_panel("settings.advanced.ocr.panel", ocr_fields),
    ];

    let mut layout_fields: Vec<View<Message>> = vec![settings_form_field(
        "settings.advanced.layout",
        "Detection Mode",
        Some("Choose how PDF page regions are detected before translation."),
        combo_box(layout_detection_items())
            .id("LayoutDetectionModeCombo")
            .selected(state.layout_detection_mode.clone())
            .width(Length::Fixed(300))
            .on_change(Message::LayoutDetectionModeChanged),
    )];

    if shows_layout_model_panel(&state.layout_detection_mode) {
        layout_fields.push(settings_form_field(
            "settings.advanced.layout.onnx",
            "Local ONNX Model",
            Some("DocLayout-YOLO model used by Auto and Local ONNX detection."),
            row((
                button("Download Model (~75MB)")
                    .id("DownloadLayoutModelButton")
                    .on_press(Message::DownloadLayoutModel),
                button("Delete")
                    .id("DeleteLayoutModelButton")
                    .on_press(Message::DeleteLayoutModel),
                progress_ring()
                    .id("LayoutModelProgressRing")
                    .active(state.layout_model_status.contains("queued"))
                    .size(20)
                    .label("Layout model"),
                styled_text_id(
                    "LayoutModelStatusText",
                    state.layout_model_status.clone(),
                    TextStyle::Caption,
                ),
            ))
            .id("settings.advanced.layout.onnx.panel")
            .spacing(8)
            .align(Alignment::Center)
            .into_view(),
        ));
    }

    if state.layout_detection_mode == "VisionLLM" {
        layout_fields.push(settings_form_field(
            "settings.advanced.layout.vision",
            "Vision LLM",
            Some("Use a vision-capable service for page layout detection."),
            combo_box(vision_layout_service_items())
                .id("VisionLayoutServiceCombo")
                .selected(state.vision_layout_service.clone())
                .width(Length::Fixed(300))
                .on_change(Message::VisionLayoutServiceChanged),
        ));
    }

    children.extend([
        styled_text_id(
            "AdvancedLayoutDetectionHeaderText",
            "Layout Detection",
            TextStyle::Subtitle,
        ),
        settings_panel("settings.advanced.layout.panel", layout_fields),
    ]);

    children.extend([
        styled_text_id("AdvancedCjkFontHeaderText", "CJK Font", TextStyle::Subtitle),
        settings_row("Noto Sans CJK")
            .id("settings.advanced.cjk_font")
            .description("Download Noto Sans CJK for PDF export without missing glyphs.")
            .content(
                row((
                    button("Download CJK Font")
                        .id("DownloadCjkFontButton")
                        .on_press(Message::DownloadCjkFont),
                    button("Delete")
                        .id("DeleteCjkFontButton")
                        .on_press(Message::DeleteCjkFont),
                    progress_ring()
                        .id("CjkFontProgressRing")
                        .active(state.cjk_font_status.contains("queued"))
                        .size(20)
                        .label("CJK font"),
                    styled_text_id(
                        "CjkFontStatusText",
                        state.cjk_font_status.clone(),
                        TextStyle::Caption,
                    ),
                ))
                .id("settings.advanced.cjk_font.panel")
                .spacing(8)
                .align(Alignment::Center),
            )
            .into_view(),
        styled_text_id(
            "AdvancedFormulaDetectionHeaderText",
            "Formula Detection",
            TextStyle::Subtitle,
        ),
        settings_row("Formula patterns")
            .id("settings.advanced.formula")
            .description("Empty values use the built-in formula protection rules.")
            .content(
                column((
                    text_editor(state.formula_font_pattern.clone())
                        .id("FormulaFontPatternBox")
                        .placeholder("Font Pattern regex")
                        .on_input(Message::FormulaFontPatternChanged),
                    text_editor(state.formula_char_pattern.clone())
                        .id("FormulaCharPatternBox")
                        .placeholder("Character Pattern regex")
                        .on_input(Message::FormulaCharPatternChanged),
                ))
                .id("settings.advanced.formula.patterns")
                .spacing(8),
            )
            .into_view(),
        styled_text_id(
            "AdvancedTranslationCacheHeaderText",
            "Translation Cache",
            TextStyle::Subtitle,
        ),
        settings_row("Enable Translation Cache")
            .id("settings.advanced.translation_cache")
            .description("Reuse short-text and long-document translation results when possible.")
            .content(styled_text_id(
                "TranslationCacheStatusText",
                state.translation_cache_status.clone(),
                TextStyle::Caption,
            ))
            .trailing((
                toggle_switch("Enable Translation Cache", state.translation_cache_enabled)
                    .id("TranslationCacheToggle")
                    .on_toggle(Message::ToggleTranslationCache),
                button("Clear Cache")
                    .id("ClearCacheButton")
                    .on_press(Message::ClearTranslationCache),
            ))
            .into_view(),
        styled_text_id(
            "AdvancedCustomPromptHeaderText",
            "Custom Translation Prompt",
            TextStyle::Subtitle,
        ),
        settings_row("Long document prompt")
            .id("settings.advanced.custom_prompt")
            .description("Additional instructions for long-document and LLM translation.")
            .content(
                text_editor(state.custom_translation_prompt.clone())
                    .id("CustomTranslationPromptBox")
                    .placeholder("Custom translation instructions")
                    .min_height(120)
                    .max_height(120)
                    .on_input(Message::CustomTranslationPromptChanged),
            )
            .into_view(),
        styled_text_id("AdvancedProxyHeaderText", "HTTP Proxy", TextStyle::Subtitle),
        settings_row("Use HTTP Proxy")
            .id("settings.advanced.proxy")
            .description("Proxy URL must be an absolute URI when enabled.")
            .content(
                column((
                    text_editor(state.proxy_url.clone())
                        .id("ProxyUriBox")
                        .placeholder("http://127.0.0.1:7890")
                        .enabled(state.proxy_enabled)
                        .on_input(Message::ProxyUrlChanged),
                    toggle_switch("Bypass proxy for localhost", state.proxy_bypass_local)
                        .id("ProxyBypassLocalToggle")
                        .on_toggle(Message::ToggleProxyBypassLocal),
                ))
                .id("settings.advanced.proxy.panel")
                .spacing(8),
            )
            .trailing((toggle_switch("Use HTTP Proxy", state.proxy_enabled)
                .id("ProxyEnabledToggle")
                .on_toggle(Message::ToggleProxyEnabled),))
            .into_view(),
        settings_row("Shell context menu")
            .id("settings.advanced.shell")
            .description("Right-click files or desktop background to start OCR Translate.")
            .trailing((toggle_switch("Enabled", state.shell_context_menu)
                .on_toggle(Message::ToggleShellContextMenu),))
            .into_view(),
        settings_row("Browser extension")
            .id("settings.advanced.browser")
            .description("Native messaging host used by Chrome and Firefox extensions.")
            .trailing((
                button("Install")
                    .id("settings.advanced.browser.install")
                    .on_press(Message::InstallBrowserSupport),
                button("Uninstall")
                    .id("settings.advanced.browser.uninstall")
                    .on_press(Message::UninstallBrowserSupport),
            ))
            .into_view(),
    ]);

    column(children)
        .id("settings.advanced")
        .spacing(12)
        .width(Length::Fill)
        .into_view()
}

fn is_advanced_ocr_engine(engine: &str) -> bool {
    matches!(engine, "Ollama" | "CustomApi")
}

fn shows_layout_model_panel(mode: &str) -> bool {
    matches!(mode, "Auto" | "OnnxLocal")
}

fn ocr_engine_items() -> [ComboBoxItem; 3] {
    [
        ComboBoxItem::new("WindowsNative", "Default / Windows Native"),
        ComboBoxItem::new("Ollama", "Ollama Local VLM"),
        ComboBoxItem::new("CustomApi", "Custom API"),
    ]
}

fn layout_detection_items() -> [ComboBoxItem; 4] {
    [
        ComboBoxItem::new("Auto", "Auto"),
        ComboBoxItem::new("OnnxLocal", "Local ONNX Model"),
        ComboBoxItem::new("VisionLLM", "Vision LLM"),
        ComboBoxItem::new("Heuristic", "Heuristic Only"),
    ]
}

fn vision_layout_service_items() -> [ComboBoxItem; 3] {
    [
        ComboBoxItem::new("openai", "OpenAI"),
        ComboBoxItem::new("gemini", "Gemini"),
        ComboBoxItem::new("custom-openai", "Custom OpenAI"),
    ]
}

fn settings_language_content(state: &SettingsState) -> View<Message> {
    let selected_count = state.selected_languages.len();
    let language_rows = TRANSLATION_LANGUAGE_IDS
        .into_iter()
        .map(|id| {
            let selected = state
                .selected_languages
                .iter()
                .any(|language| language == id);
            row((toggle_switch(language_label(id), selected)
                .id(format!("settings.language.selected.{id}.toggle"))
                .enabled(!selected || selected_count > 2)
                .on_toggle(move |value| Message::ToggleSelectedLanguage(id.to_string(), value)),))
            .id(format!("settings.language.selected.{id}"))
            .width(Length::Fixed(220))
            .into_view()
        })
        .collect::<Vec<_>>();

    column((
        text("Language"),
        settings_row("First Language")
            .id("settings.language.first")
            .description(
                "Preferred target language when detected source is not the first language.",
            )
            .trailing((combo_box(all_language_items(false))
                .id("FirstLanguageCombo")
                .selected(settings_language_selected(&state.first_language))
                .on_change(Message::FirstLanguageChanged),)),
        settings_row("Second Language")
            .id("settings.language.second")
            .description(
                "Fallback target language when detected source matches the first language.",
            )
            .trailing((combo_box(all_language_items(false))
                .id("SecondLanguageCombo")
                .selected(settings_language_selected(&state.second_language))
                .on_change(Message::SecondLanguageChanged),)),
        settings_row("Auto-select target language")
            .id("settings.language.auto_select_target")
            .description(
                "Use the first/second language rule until a target language is chosen manually.",
            )
            .trailing((toggle_switch(
                "Auto-select target language",
                state.auto_select_target_language,
            )
            .id("AutoSelectTargetToggle")
            .on_toggle(Message::ToggleAutoSelectTargetLanguage),)),
        settings_row("Display language")
            .id("settings.language.display")
            .description(
                "Choose the language used by the app UI. Restart required for full effect.",
            )
            .trailing((combo_box(ui_language_items())
                .id("UILanguageCombo")
                .selected(state.ui_language.clone())
                .on_change(Message::UiLanguageChanged),)),
        expander("Translation languages")
            .id("settings.language.translation_languages")
            .expanded(state.translation_languages_expanded)
            .on_toggle(Message::ToggleTranslationLanguagesExpanded)
            .description(
                "Choose which languages appear in Main, Mini, Fixed, and Long Document pickers.",
            )
            .content(
                column((
                    styled_text_id(
                        "AvailableLanguagesDescText",
                        "Select languages available in source/target pickers. At least 2 required.",
                        TextStyle::Caption,
                    ),
                    wrap(language_rows)
                        .id("settings.language.selected_languages")
                        .max_columns(4)
                        .spacing(8)
                        .run_spacing(6),
                ))
                .id("settings.language.selected_languages.content")
                .spacing(8),
            ),
    ))
    .id("settings.language")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn settings_about_content() -> View<Message> {
    column((
        styled_text_id("AboutHeaderText", "About", TextStyle::Subtitle),
        settings_row("Easydict")
            .id("settings.about.app")
            .description("Free and open-source Windows translation app. GPL-3.0-or-later.")
            .content(styled_text_id(
                "AboutAppNameText",
                "Easydict",
                TextStyle::BodyStrong,
            )),
        settings_row("Version")
            .id("settings.about.version")
            .description(env!("CARGO_PKG_VERSION")),
        settings_row("Links")
            .id("settings.about.links")
            .description("Project resources and feedback links.")
            .content(
                column((
                    settings_link_button(SettingsLink::GitHubRepository),
                    settings_link_button(SettingsLink::IssueFeedback),
                    settings_link_button(SettingsLink::EasydictForMacOS),
                ))
                .id("settings.about.links.list")
                .spacing(6),
            ),
    ))
    .id("settings.about")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn settings_link_button(link: SettingsLink) -> View<Message> {
    button(link.label())
        .id(link.id())
        .link()
        .tooltip(link.url())
        .on_press(Message::OpenSettingsLink(link))
}

fn selected_language_items(include_auto: bool, settings: &SettingsState) -> Vec<ComboBoxItem> {
    let mut items = Vec::new();
    if include_auto {
        items.push(ComboBoxItem::new(
            "auto",
            tr("main.auto_detect", "Auto Detect"),
        ));
    }
    items.extend(
        TRANSLATION_LANGUAGE_IDS
            .into_iter()
            .filter(|id| {
                settings
                    .selected_languages
                    .iter()
                    .any(|language| language == id)
            })
            .map(language_item),
    );
    items
}

fn all_language_items(include_auto: bool) -> Vec<ComboBoxItem> {
    let mut items = Vec::new();
    if include_auto {
        items.push(ComboBoxItem::new(
            "auto",
            tr("main.auto_detect", "Auto Detect"),
        ));
    }
    items.extend(TRANSLATION_LANGUAGE_IDS.into_iter().map(language_item));
    items
}

fn settings_language_selected(language_id: &str) -> String {
    match language_id.trim().to_ascii_lowercase().as_str() {
        "zh" | "zh-cn" => "zh-Hans".to_string(),
        "zh-tw" => "zh-Hant".to_string(),
        value => value.to_string(),
    }
}

fn ui_language_items() -> [ComboBoxItem; 15] {
    [
        ComboBoxItem::new("en-US", "English"),
        ComboBoxItem::new("zh-CN", "Chinese (Simplified)"),
        ComboBoxItem::new("zh-TW", "Chinese (Traditional)"),
        ComboBoxItem::new("ja-JP", "Japanese"),
        ComboBoxItem::new("ko-KR", "Korean"),
        ComboBoxItem::new("fr-FR", "French"),
        ComboBoxItem::new("de-DE", "German"),
        ComboBoxItem::new("vi-VN", "Vietnamese"),
        ComboBoxItem::new("th-TH", "Thai"),
        ComboBoxItem::new("ar-SA", "Arabic"),
        ComboBoxItem::new("id-ID", "Indonesian"),
        ComboBoxItem::new("it-IT", "Italian"),
        ComboBoxItem::new("ms-MY", "Malay"),
        ComboBoxItem::new("hi-IN", "Hindi"),
        ComboBoxItem::new("da-DK", "Danish"),
    ]
}

fn language_item(id: &'static str) -> ComboBoxItem {
    ComboBoxItem::new(id, language_label(id))
}

fn language_label(id: &str) -> String {
    match id {
        "ar" => "Arabic".to_string(),
        "bg" => "Bulgarian".to_string(),
        "bn" => "Bengali".to_string(),
        "cs" => "Czech".to_string(),
        "da" => "Danish".to_string(),
        "de" => "German".to_string(),
        "el" => "Greek".to_string(),
        "en" => "English".to_string(),
        "es" => "Spanish".to_string(),
        "et" => "Estonian".to_string(),
        "fa" => "Persian".to_string(),
        "fi" => "Finnish".to_string(),
        "fr" => "French".to_string(),
        "he" => "Hebrew".to_string(),
        "hi" => "Hindi".to_string(),
        "hu" => "Hungarian".to_string(),
        "id" => "Indonesian".to_string(),
        "it" => "Italian".to_string(),
        "ja" => "Japanese".to_string(),
        "ko" => "Korean".to_string(),
        "lt" => "Lithuanian".to_string(),
        "lv" => "Latvian".to_string(),
        "ms" => "Malay".to_string(),
        "nl" => "Dutch".to_string(),
        "no" => "Norwegian".to_string(),
        "pl" => "Polish".to_string(),
        "pt" => "Portuguese".to_string(),
        "ro" => "Romanian".to_string(),
        "ru" => "Russian".to_string(),
        "sk" => "Slovak".to_string(),
        "sl" => "Slovenian".to_string(),
        "sv" => "Swedish".to_string(),
        "ta" => "Tamil".to_string(),
        "te" => "Telugu".to_string(),
        "th" => "Thai".to_string(),
        "tl" => "Filipino".to_string(),
        "tr" => "Turkish".to_string(),
        "uk" => "Ukrainian".to_string(),
        "ur" => "Urdu".to_string(),
        "vi" => "Vietnamese".to_string(),
        "zh-Hans" => tr("main.target_zh_hans", "Chinese (Simplified)"),
        "zh-Hant" => "Chinese (Traditional)".to_string(),
        "zh-classical" => "Classical Chinese".to_string(),
        _ => id.to_string(),
    }
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

fn open_ai_api_format_items() -> [ComboBoxItem; 3] {
    [
        ComboBoxItem::new("Auto", "Auto-detect"),
        ComboBoxItem::new("Responses", "Responses API"),
        ComboBoxItem::new("ChatCompletions", "Chat Completions API"),
    ]
}

fn open_ai_model_items() -> [ComboBoxItem; 8] {
    [
        ComboBoxItem::new("gpt-5.4-mini", "gpt-5.4-mini"),
        ComboBoxItem::new("gpt-5.4-nano", "gpt-5.4-nano"),
        ComboBoxItem::new("gpt-5.4", "gpt-5.4"),
        ComboBoxItem::new("gpt-5-mini", "gpt-5-mini"),
        ComboBoxItem::new("gpt-5-nano", "gpt-5-nano"),
        ComboBoxItem::new("gpt-5", "gpt-5"),
        ComboBoxItem::new("gpt-4.1-mini", "gpt-4.1-mini"),
        ComboBoxItem::new("gpt-4o-mini", "gpt-4o-mini"),
    ]
}

fn ollama_model_items() -> [ComboBoxItem; 4] {
    [
        ComboBoxItem::new("llama3.2", "llama3.2"),
        ComboBoxItem::new("llama3.1", "llama3.1"),
        ComboBoxItem::new("qwen2.5", "qwen2.5"),
        ComboBoxItem::new("mistral", "mistral"),
    ]
}

fn llm_provider_descriptors() -> [LlmProviderDescriptor; 8] {
    [
        LlmProviderDescriptor {
            service_id: "deepseek",
            title: "DeepSeek",
            expander_id: "DeepSeekServiceExpander",
            status_id: "DeepSeekStatusText",
            key_header_id: "DeepSeekKeyHeaderText",
            key_box_id: "DeepSeekKeyBox",
            key_reveal_id: "DeepSeekKeyRevealButton",
            key_label: "API Key",
            key_placeholder: "sk-...",
            endpoint_box_id: None,
            endpoint_placeholder: "",
            model_box_id: "DeepSeekModelCombo",
            test_button_id: "TestDeepSeekButton",
            description: "Get your API key from platform.deepseek.com.",
            default_endpoint: "",
            default_model: "deepseek-chat",
            model_options: &["deepseek-chat", "deepseek-reasoner"],
        },
        LlmProviderDescriptor {
            service_id: "groq",
            title: "Groq",
            expander_id: "GroqServiceExpander",
            status_id: "GroqStatusText",
            key_header_id: "GroqKeyHeaderText",
            key_box_id: "GroqKeyBox",
            key_reveal_id: "GroqKeyRevealButton",
            key_label: "API Key",
            key_placeholder: "gsk_...",
            endpoint_box_id: None,
            endpoint_placeholder: "",
            model_box_id: "GroqModelCombo",
            test_button_id: "TestGroqButton",
            description: "Groq provides fast OpenAI-compatible inference.",
            default_endpoint: "",
            default_model: "llama-3.3-70b-versatile",
            model_options: &[
                "llama-3.3-70b-versatile",
                "llama-3.1-8b-instant",
                "qwen/qwen-3-32b",
            ],
        },
        LlmProviderDescriptor {
            service_id: "zhipu",
            title: "Zhipu (智谱)",
            expander_id: "ZhipuServiceExpander",
            status_id: "ZhipuStatusText",
            key_header_id: "ZhipuKeyHeaderText",
            key_box_id: "ZhipuKeyBox",
            key_reveal_id: "ZhipuKeyRevealButton",
            key_label: "API Key",
            key_placeholder: "Enter your Zhipu API key",
            endpoint_box_id: None,
            endpoint_placeholder: "",
            model_box_id: "ZhipuModelCombo",
            test_button_id: "TestZhipuButton",
            description: "Get your API key from open.bigmodel.cn.",
            default_endpoint: "",
            default_model: "glm-4.5-flash",
            model_options: &[
                "glm-4.5-flash",
                "glm-4-flash-250414",
                "glm-4.7",
                "glm-4.5-air",
            ],
        },
        LlmProviderDescriptor {
            service_id: "github",
            title: "GitHub Models",
            expander_id: "GitHubModelsServiceExpander",
            status_id: "GitHubModelsStatusText",
            key_header_id: "GitHubModelsTokenHeaderText",
            key_box_id: "GitHubModelsTokenBox",
            key_reveal_id: "GitHubModelsTokenRevealButton",
            key_label: "GitHub Token",
            key_placeholder: "ghp_...",
            endpoint_box_id: None,
            endpoint_placeholder: "",
            model_box_id: "GitHubModelsModelCombo",
            test_button_id: "TestGitHubModelsButton",
            description: "Use a GitHub personal access token for GitHub Models.",
            default_endpoint: "",
            default_model: "gpt-4.1",
            model_options: &[
                "gpt-4.1",
                "gpt-4.1-mini",
                "gpt-4.1-nano",
                "gpt-4o",
                "gpt-4o-mini",
                "deepseek-v3-0324",
            ],
        },
        LlmProviderDescriptor {
            service_id: "gemini",
            title: "Gemini",
            expander_id: "GeminiServiceExpander",
            status_id: "GeminiStatusText",
            key_header_id: "GeminiKeyHeaderText",
            key_box_id: "GeminiKeyBox",
            key_reveal_id: "GeminiKeyRevealButton",
            key_label: "API Key",
            key_placeholder: "Enter your Gemini API key",
            endpoint_box_id: None,
            endpoint_placeholder: "",
            model_box_id: "GeminiModelCombo",
            test_button_id: "TestGeminiButton",
            description: "Get your API key from aistudio.google.com.",
            default_endpoint: "",
            default_model: "gemini-2.5-flash",
            model_options: &[
                "gemini-2.5-flash",
                "gemini-2.5-flash-lite",
                "gemini-2.5-pro",
                "gemini-2.0-flash",
                "gemini-1.5-flash",
                "gemini-1.5-pro",
            ],
        },
        LlmProviderDescriptor {
            service_id: "custom-openai",
            title: "Custom OpenAI Compatible",
            expander_id: "CustomOpenAIServiceExpander",
            status_id: "CustomOpenAIStatusText",
            key_header_id: "CustomOpenAIKeyHeaderText",
            key_box_id: "CustomOpenAIKeyBox",
            key_reveal_id: "CustomOpenAIKeyRevealButton",
            key_label: "API Key (Optional)",
            key_placeholder: "Enter API key if required",
            endpoint_box_id: Some("CustomOpenAIEndpointBox"),
            endpoint_placeholder: "https://your-api.example.com/v1/chat/completions",
            model_box_id: "CustomOpenAIModelBox",
            test_button_id: "TestCustomOpenAIButton",
            description: "Configure any OpenAI-compatible API endpoint.",
            default_endpoint: "",
            default_model: "gpt-3.5-turbo",
            model_options: &["gpt-3.5-turbo", "gpt-4o-mini", "llama3.2", "qwen2.5"],
        },
        LlmProviderDescriptor {
            service_id: "builtin",
            title: "Built-in AI",
            expander_id: "BuiltInAIServiceExpander",
            status_id: "BuiltInStatusText",
            key_header_id: "BuiltInApiKeyHeaderText",
            key_box_id: "BuiltInApiKeyBox",
            key_reveal_id: "BuiltInApiKeyRevealButton",
            key_label: "API Key (Optional)",
            key_placeholder: "Leave empty to use built-in key",
            endpoint_box_id: None,
            endpoint_placeholder: "",
            model_box_id: "BuiltInModelCombo",
            test_button_id: "TestBuiltInButton",
            description: "Uses GLM or Groq free models; provide your own key for stable use.",
            default_endpoint: "",
            default_model: "glm-4-flash-250414",
            model_options: &[
                "glm-4-flash-250414",
                "glm-4-flash",
                "llama-3.3-70b-versatile",
                "llama-3.1-8b-instant",
            ],
        },
        LlmProviderDescriptor {
            service_id: "doubao",
            title: "Doubao (豆包)",
            expander_id: "DoubaoServiceExpander",
            status_id: "DoubaoStatusText",
            key_header_id: "DoubaoKeyHeaderText",
            key_box_id: "DoubaoKeyBox",
            key_reveal_id: "DoubaoKeyRevealButton",
            key_label: "API Key",
            key_placeholder: "Enter your Doubao API key",
            endpoint_box_id: Some("DoubaoEndpointBox"),
            endpoint_placeholder: "https://ark.cn-beijing.volces.com/api/v3/responses",
            model_box_id: "DoubaoModelBox",
            test_button_id: "TestDoubaoButton",
            description: "ByteDance Doubao translation service.",
            default_endpoint: "https://ark.cn-beijing.volces.com/api/v3/responses",
            default_model: "doubao-seed-translation-250915",
            model_options: &["doubao-seed-translation-250915"],
        },
    ]
}

fn theme_id(theme: ThemeMode) -> &'static str {
    match theme {
        ThemeMode::System => "system",
        ThemeMode::Light => "light",
        ThemeMode::Dark => "dark",
        ThemeMode::Minimal => "minimal",
        ThemeMode::HighContrast => "high-contrast",
    }
}

use crate::i18n::{tr, tr_count, tr_count_locale, tr_locale};
use crate::mdx_native::native_mdx_dictionary_can_route_natively;
use crate::protocol::local_ai_provider_modes;
use crate::quick_translate::QuickTranslateSurface;
use crate::screen_capture::{CaptureInteractionState, CapturePhase, CaptureRect};
use crate::state::{
    AppMode, EasydictUiState, FloatingWindowState, HotkeySetting, ImportedMdxDictionary,
    LongDocumentState, Message, ServiceProviderField, ServiceProviderSetting, SettingsLink,
    SettingsSection, SettingsState, TranslationResultPreview, WindowServiceSetting,
    TRANSLATION_LANGUAGE_IDS,
};
use crate::{
    default_translation_service_descriptors, TranslationServiceKind, HOTKEY_OCR_TRANSLATE,
    HOTKEY_SHOW_FIXED, HOTKEY_SHOW_MAIN, HOTKEY_SHOW_MINI, HOTKEY_SILENT_OCR,
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
                    .icon(icon::app())
                    .caption_controls(true)
                    .on_minimize(Message::MinimizeWindow)
                    .on_toggle_maximize(Message::ToggleMaximizeWindow)
                    .on_close(Message::CloseMainWindow),
                busy_overlay(surface)
                    .id("ModeSwitchOverlay")
                    .active(state.mode_overlay_active)
                    .opacity(0.86)
                    .fade_transition_ms(180)
                    .label("Switching")
                    .into_view(),
            ))
            .id("main.root")
            .tw("p-0 gap-0 w-full h-full"),
        )
        .into_view()
}

pub fn settings_view(state: &SettingsState) -> View<Message> {
    settings_view_with_close_message(state, Message::CloseWindow)
}

pub fn settings_view_for_main_window(state: &SettingsState) -> View<Message> {
    settings_view_with_close_message(state, Message::CloseMainWindow)
}

fn settings_view_with_close_message(
    state: &SettingsState,
    close_message: Message,
) -> View<Message> {
    let mut tabs_row_children = vec![settings_category_bar(state)];
    if state.tab_switching {
        tabs_row_children.push(
            progress_ring()
                .id("SettingsTabSwitchRing")
                .active(true)
                .size(20)
                .into_view(),
        );
    }

    let mut content_children = vec![
        settings_header(&state.ui_language),
        row(tabs_row_children)
            .id("settings.tabs_row")
            .spacing(12)
            .align(Alignment::Start)
            .width(Length::Fill)
            .margin(Edges {
                bottom: 10,
                ..Edges::ZERO
            })
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
    .scrollbars_visible(state.scrollbars_visible)
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
        surface = surface.layer(
            OverlayLayer::new(settings_save_bar(&state.ui_language))
                .align(Alignment::End, Alignment::End),
        );
    }
    if let Some(message) = state.save_error_message.as_deref() {
        surface = surface.layer(OverlayLayer::modal(settings_save_error_dialog(
            &state.ui_language,
            message,
        )));
    } else if state.pending_mdx_delete_service_id.is_some() {
        surface = surface.layer(OverlayLayer::modal(settings_mdx_delete_dialog(state)));
    } else if state.show_unsaved_changes_dialog {
        surface = surface.layer(OverlayLayer::modal(settings_unsaved_changes_dialog(
            &state.ui_language,
        )));
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
                    .icon(icon::app())
                    .caption_controls(true)
                    .on_minimize(Message::MinimizeWindow)
                    .on_toggle_maximize(Message::ToggleMaximizeWindow)
                    .on_close(close_message),
                content,
            ))
            .id("settings.root_with_title")
            .tw("p-0 gap-0 w-full h-full"),
        )
        .into_view()
}

fn settings_header(locale: &str) -> View<Message> {
    let back_label = tr_locale(locale, "settings.back", "Back");
    row((
        column((
            spacer().width(Length::Fixed(32)).height(Length::Fixed(5)),
            primary_button("")
                .id("BackButton")
                .icon(win_fluent::IconToken::with_glyph("back", '\u{E72B}'))
                .tooltip(back_label.clone())
                .width(Length::Fixed(32))
                .height(Length::Fixed(32))
                .a11y(A11yHint::named(back_label))
                .on_press(Message::Back),
        ))
        .id("BackButtonSlot")
        .width(Length::Fixed(32))
        .height(Length::Fixed(40))
        .margin(Edges {
            left: 1,
            ..Edges::ZERO
        }),
        styled_text_id(
            "SettingsHeaderText",
            tr_locale(locale, "settings.title", "Settings"),
            TextStyle::Title,
        ),
    ))
    .id("settings.header")
    .spacing(12)
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

fn settings_save_bar(locale: &str) -> View<Message> {
    // Shrink-wrapped around the button and inset from the window edges; the
    // overlay layer positions it bottom-right.
    row((
        primary_button(tr_locale(locale, "settings.save", "Save Settings"))
            .id("SaveButton")
            .icon(icon::check())
            .on_press(Message::SaveSettingsChanges),
    ))
    .id("settings.save_floating_bar")
    .tw("shadow-lg m-6")
    .into_view()
}

fn settings_save_error_dialog(locale: &str, message: &str) -> View<Message> {
    dialog(tr_locale(locale, "settings.error.title", "Settings Error"))
        .id("settings.error_dialog")
        .kind(DialogKind::Error)
        .content(
            column((
                text(message),
                row((primary_button(tr_locale(locale, "settings.ok", "OK"))
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

fn settings_unsaved_changes_dialog(locale: &str) -> View<Message> {
    dialog(tr_locale(
        locale,
        "settings.unsaved.title",
        "Unsaved Settings",
    ))
    .id("settings.unsaved_dialog")
    .kind(DialogKind::Confirmation)
    .content(
        column((
            text(tr_locale(
                locale,
                "settings.unsaved.message",
                "Save your settings changes before leaving?",
            )),
            row((
                primary_button(tr_locale(locale, "settings.unsaved.save", "Save"))
                    .id("settings.unsaved.save")
                    .on_press(Message::SaveSettingsChanges),
                button(tr_locale(locale, "settings.unsaved.discard", "Don't Save"))
                    .id("settings.unsaved.discard")
                    .on_press(Message::DiscardSettingsChanges),
                button(tr_locale(locale, "settings.unsaved.cancel", "Cancel"))
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
    capture_overlay_view_with_state(&CaptureInteractionState::new(), None, None)
}

pub fn capture_overlay_view_with_state(
    state: &CaptureInteractionState,
    selection_override: Option<CaptureRect>,
    background: Option<&crate::state::CaptureBackground>,
) -> View<Message> {
    let selection = selection_override
        .or(state.selection)
        .map(CaptureRect::normalized);
    let detected = state.detected_region.map(CaptureRect::normalized);
    // The base shows the frozen desktop under the dim mask like the WinUI
    // overlay; without a screenshot it stays a bare input-capture surface.
    let base_content = match background {
        Some(bg) => image_bgra_file(&bg.bgra_path, bg.pixel_width, bg.pixel_height)
            .id("capture.background")
            .width(Length::Fill)
            .height(Length::Fill)
            .into_view(),
        None => column((spacer().width(Length::Fill).height(Length::Fill),))
            .width(Length::Fill)
            .height(Length::Fill)
            .into_view(),
    };
    let base = pointer_region(
        column((base_content,))
            .id("capture.pointer.content")
            .width(Length::Fill)
            .height(Length::Fill),
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
    .on_escape(Message::CaptureEscape);

    // Match the WinUI overlay: while detecting/selecting only a centered tip
    // pill floats over the dim mask (~40% black, MaskAlpha 100); the command
    // panel with confirm/copy/nudge actions appears once a selection is being
    // adjusted.
    let top_layer = if state.phase == CapturePhase::Adjusting {
        OverlayLayer::new(capture_status_panel(state, selection))
            .align(Alignment::Start, Alignment::Start)
    } else {
        OverlayLayer::new(capture_tip_pill(state)).align(Alignment::Center, Alignment::Start)
    };
    let mut layers = overlay(base)
        .id("capture.overlay.layers")
        .layer(top_layer.scrim(0.40));

    if let Some(rect) = detected {
        layers = layers.layer(
            OverlayLayer::new(capture_region_token("capture.detected_region", state, rect))
                .align(Alignment::Center, Alignment::Center),
        );
    }

    if let Some(rect) = selection {
        layers = layers
            .layer(
                OverlayLayer::new(capture_selection_layer(rect, state))
                    .align(Alignment::Center, Alignment::Center),
            )
            .layer(
                OverlayLayer::new(capture_magnifier_layer(rect))
                    .align(Alignment::End, Alignment::Start),
            );
    }

    page("Capture Overlay")
        .id("capture.overlay")
        .content(layers)
        .into_view()
}

fn capture_tip_pill(state: &CaptureInteractionState) -> View<Message> {
    // Phase-specific hint matching the .NET overlay tip bar: full detection
    // guidance while detecting, a terse cancel hint once selecting.
    let tip = if state.phase == CapturePhase::Detecting {
        tr(
            "ocr.capture.instructions",
            "Drag to select region  |  Double-click to select window  |  Scroll to switch  |  Esc to exit",
        )
    } else {
        tr(
            "ocr.capture.instructions.selecting",
            "Right-click or Esc to cancel",
        )
    };

    column((text(tip),))
        .id("capture.tip")
        .padding(8)
        .margin(Edges {
            top: 20,
            right: 0,
            bottom: 0,
            left: 0,
        })
        .tw("capture-tip rounded-lg")
        .into_view()
}

fn capture_status_panel(
    state: &CaptureInteractionState,
    selection: Option<CaptureRect>,
) -> View<Message> {
    let can_confirm = state.phase == CapturePhase::Adjusting
        && selection.is_some_and(CaptureRect::is_confirmable);

    column((
        text(tr("ocr.capture.title", "OCR Capture")),
        command_bar((
            primary_button("Confirm")
                .id("capture.confirm")
                .icon(icon::translate())
                .enabled(can_confirm)
                .on_press(Message::ConfirmCapture),
            button("Copy text")
                .id("capture.copy")
                .icon(icon::copy())
                .enabled(can_confirm)
                .on_press(Message::CopyResult),
            button("Cancel")
                .id("capture.cancel")
                .icon(icon::clear())
                .on_press(Message::CancelCapture),
        ))
        .id("capture.commands")
        .compact(true),
        command_bar((
            capture_nudge_button(
                "capture.nudge.left",
                "Nudge left",
                "chevron-left",
                '\u{E76B}',
                -1,
                0,
                state.phase == CapturePhase::Adjusting,
            ),
            capture_nudge_button(
                "capture.nudge.up",
                "Nudge up",
                "chevron-up",
                '\u{E70E}',
                0,
                -1,
                state.phase == CapturePhase::Adjusting,
            ),
            capture_nudge_button(
                "capture.nudge.down",
                "Nudge down",
                "chevron-down",
                '\u{E70D}',
                0,
                1,
                state.phase == CapturePhase::Adjusting,
            ),
            capture_nudge_button(
                "capture.nudge.right",
                "Nudge right",
                "chevron-right",
                '\u{E76C}',
                1,
                0,
                state.phase == CapturePhase::Adjusting,
            ),
        ))
        .id("capture.nudge_commands")
        .compact(true),
    ))
    .id("capture.status_panel")
    .padding(10)
    .spacing(6)
    .width(Length::Fixed(460))
    .margin(Edges {
        top: 12,
        right: 0,
        bottom: 0,
        left: 12,
    })
    .tw("surface-card border rounded-lg")
    .into_view()
}

fn capture_nudge_button(
    id: &'static str,
    tooltip: &'static str,
    icon_name: &'static str,
    glyph: char,
    delta_x: i32,
    delta_y: i32,
    enabled: bool,
) -> View<Message> {
    button("")
        .id(id)
        .icon(win_fluent::IconToken::with_glyph(icon_name, glyph))
        .icon_only()
        .tooltip(tooltip)
        .a11y(A11yHint::named(tooltip))
        .enabled(enabled)
        .on_press(Message::CaptureNudgeSelection { delta_x, delta_y })
        .into_view()
}

fn capture_selection_layer(rect: CaptureRect, state: &CaptureInteractionState) -> View<Message> {
    column((capture_overlay_token(
        "capture.selection_rect",
        state,
        None,
        Some(rect),
        true,
        true,
    ),))
    .id("capture.selection_layer")
    .into_view()
}

fn capture_magnifier_layer(rect: CaptureRect) -> View<Message> {
    column((
        text("Magnifier"),
        text(format!(
            "{} x {}",
            rect.width().max(0),
            rect.height().max(0)
        )),
    ))
    .id("capture.magnifier")
    .padding(10)
    .spacing(4)
    .width(Length::Fixed(160))
    .height(Length::Fixed(96))
    .tw("surface-card border rounded-lg")
    .into_view()
}

fn capture_region_token(
    id: impl Into<String>,
    state: &CaptureInteractionState,
    rect: CaptureRect,
) -> View<Message> {
    capture_overlay_token(id, state, Some(rect), None, false, false)
}

fn capture_overlay_token(
    id: impl Into<String>,
    state: &CaptureInteractionState,
    detected_rect: Option<CaptureRect>,
    selection_rect: Option<CaptureRect>,
    handles_visible: bool,
    magnifier_visible: bool,
) -> View<Message> {
    let mut builder = capture_overlay(capture_overlay_phase(state))
        .id(id)
        .detection_depth(state.detection_depth)
        .dragging(state.is_drag_selecting())
        .handles_visible(handles_visible)
        .magnifier_visible(magnifier_visible);

    if let Some(rect) = detected_rect {
        builder = builder.detected_rect(capture_overlay_rect(rect));
    }

    if let Some(rect) = selection_rect {
        builder = builder.selection_rect(capture_overlay_rect(rect));
    }

    builder.into_view()
}

fn capture_overlay_phase(state: &CaptureInteractionState) -> CaptureOverlayPhase {
    match state.phase {
        CapturePhase::Detecting => CaptureOverlayPhase::Detecting,
        CapturePhase::Selecting => CaptureOverlayPhase::Selecting,
        CapturePhase::Adjusting => CaptureOverlayPhase::Adjusting,
    }
}

fn capture_overlay_rect(rect: CaptureRect) -> CaptureOverlayRect {
    let rect = rect.normalized();
    CaptureOverlayRect::new(rect.left, rect.top, rect.width(), rect.height())
}

fn capture_point(position: PointerPosition) -> crate::screen_capture::CapturePoint {
    crate::screen_capture::CapturePoint::new(position.x, position.y)
}

pub fn pop_button_view() -> View<Message> {
    pop_button_view_with_state(ControlState::default())
}

pub fn pop_button_view_with_state(state: ControlState) -> View<Message> {
    page("Selection Translate")
        .id("pop-button.window")
        .content(
            primary_button("Translate selection")
                .id("pop-button.translate")
                .icon(icon::translate())
                .icon_only()
                .floating_action()
                .width(Length::Fixed(30))
                .height(Length::Fixed(30))
                .tooltip("Translate selection")
                .state(state)
                .on_press(Message::PopButtonClicked),
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
        main_results_card(state),
    ];
    if state.settings.theme != ThemeMode::Minimal && state.services_completed > 0 {
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

fn main_results_card(state: &EasydictUiState) -> View<Message> {
    let results = results_list(
        "main.quick.results",
        &state.results,
        |id| Message::ToggleResultExpandedIn(QuickTranslateSurface::Main, id),
        |id| Message::CopyResultIn(QuickTranslateSurface::Main, id),
        |id| Message::SpeakResultIn(QuickTranslateSurface::Main, id),
        |id| Message::ReplaceResultIn(QuickTranslateSurface::Main, id),
        |id| Message::RetryResultIn(QuickTranslateSurface::Main, id),
    );

    let content = if main_results_should_hold_initial_height(&state.results) {
        // Must fill width: a Shrink-width wrapper collapses the Fill-width result
        // list to zero width, leaving the results card visually empty.
        column((results, spacer().height(Length::Fixed(44))))
            .id("main.quick.results.initial_frame")
            .spacing(0)
            .width(Length::Fill)
            .into_view()
    } else {
        results
    };

    card(tr("main.results", "Translation Results"))
        .id("QuickOutputCard")
        .kind(CardKind::Elevated)
        .content(content)
        .into_view()
}

fn main_results_should_hold_initial_height(results: &[TranslationResultPreview]) -> bool {
    !results.is_empty()
        && results
            .iter()
            .all(|result| !result.expanded && result.result_body().trim().is_empty())
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
    let source_text_state = state
        .source_text_state
        .clone()
        .focused(state.source_text_focused || state.source_text_state.focused);
    let mut source_editor = text_editor(state.source_text.clone())
        .id("InputTextBox")
        .placeholder(tr(
            "main.source_placeholder",
            "Enter or paste text to translate...",
        ))
        .min_height(80)
        .max_height(96)
        .text_style(TextStyle::BodyLarge)
        .state(source_text_state)
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
        .kind(CardKind::Elevated)
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
        .margin(Edges {
            right: 8,
            ..Edges::ZERO
        })
        .width(Length::Fill),
    )
    .id("main.long-doc.scroll")
    .into_view()
}

fn long_document_output_content(state: &LongDocumentState, output_text: String) -> View<Message> {
    let mut children = vec![
        row((
            column((
                styled_text_id(
                    "main.long-doc.output_folder_label",
                    "Output Folder",
                    TextStyle::Caption,
                ),
                styled_text_id(
                    "main.long-doc.output_folder",
                    state.output_folder.clone(),
                    TextStyle::Body,
                ),
            ))
            .id("main.long-doc.output_folder_text")
            .spacing(2)
            .width(Length::Fill)
            .into_view(),
            button("Browse...")
                .id("main.long-doc.output_browse")
                .enabled(!state.is_translating)
                .on_press(Message::BrowseOutputFolder),
        ))
        .id("main.long-doc.output_folder_row")
        .spacing(8)
        .align(Alignment::Center)
        .width(Length::Fill)
        .into_view(),
        styled_text_id(
            "main.long-doc.output_naming_hint",
            output_text,
            TextStyle::Caption,
        ),
    ];

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
            long_document_control_cell(
                "main.long-doc.source_language_cell",
                "🌐 Source",
                combo_box(selected_language_items(true, settings))
                    .id("main.long-doc.source_language")
                    .label("Source")
                    .selected(state.source_language.clone())
                    .width(Length::Fill)
                    .enabled(can_edit)
                    .on_change(Message::LongDocumentSourceLanguageChanged),
            ),
            long_document_control_cell(
                "main.long-doc.target_language_cell",
                "🎯 Target",
                combo_box(selected_language_items(false, settings))
                    .id("main.long-doc.target_language")
                    .label("Target")
                    .selected(state.target_language.clone())
                    .width(Length::Fill)
                    .enabled(can_edit)
                    .on_change(Message::LongDocumentTargetLanguageChanged),
            ),
            long_document_control_cell(
                "main.long-doc.service_cell",
                "🤖 Service",
                combo_box(service_items())
                    .id("main.long-doc.service")
                    .label("Service")
                    .selected(state.service.clone())
                    .state(state.service_combo_state.clone())
                    .width(Length::Fill)
                    .enabled(can_edit)
                    .on_change(Message::LongDocumentServiceChanged),
            ),
        ))
        .spacing(12)
        .width(Length::Fill),
        row((
            long_document_control_cell(
                "main.long-doc.input_mode_cell",
                "📄 Input",
                combo_box([
                    ComboBoxItem::new("plaintext", "Text"),
                    ComboBoxItem::new("markdown", "Markdown"),
                    ComboBoxItem::new("pdf", "PDF"),
                ])
                .id("main.long-doc.input_mode")
                .label("Input")
                .selected(state.input_mode.clone())
                .width(Length::Fill)
                .enabled(can_edit)
                .on_change(Message::LongDocumentInputModeChanged),
            ),
            long_document_control_cell(
                "main.long-doc.output_mode_cell",
                "📝 Output",
                combo_box([
                    ComboBoxItem::new("mono", "Mono"),
                    ComboBoxItem::new("bilingual", "Bilingual"),
                    ComboBoxItem::new("both", "Both"),
                ])
                .id("main.long-doc.output_mode")
                .label("Output")
                .selected(state.output_mode.clone())
                .width(Length::Fill)
                .enabled(can_edit)
                .on_change(Message::LongDocumentOutputModeChanged),
            ),
            long_document_control_cell(
                "main.long-doc.concurrency_cell",
                "⚡ Threads",
                text_editor(state.concurrency.clone())
                    .id("main.long-doc.concurrency")
                    .placeholder("Threads")
                    .max_height(36)
                    .enabled(can_edit)
                    .on_input(Message::LongDocumentConcurrencyChanged),
            ),
            long_document_control_cell(
                "main.long-doc.page_range_cell",
                "📑 Pages",
                text_editor(state.page_range.clone())
                    .id("main.long-doc.page_range")
                    .placeholder("1-3,5,7-10")
                    .max_height(36)
                    .enabled(can_edit)
                    .on_input(Message::LongDocumentPageRangeChanged),
            ),
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

fn long_document_control_cell(
    id: &'static str,
    label: &'static str,
    control: impl IntoView<Message>,
) -> View<Message> {
    column((
        styled_text_id(format!("{id}.label"), label, TextStyle::Caption),
        control,
    ))
    .id(id)
    .spacing(3)
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
                floating_input_surface(
                    id_prefix,
                    surface,
                    state,
                    id_prefix == "mini",
                    &settings.ui_language,
                ),
                translate_language_bar(
                    id_prefix,
                    surface,
                    &state.source_language,
                    &state.target_language,
                    settings,
                    state.is_translating,
                    state.translate_button_state.clone(),
                ),
                floating_detected_language_label(id_prefix, state),
                results_list(
                    &format!("{id_prefix}.results"),
                    &state.results,
                    move |id| Message::ToggleResultExpandedIn(surface, id),
                    move |id| Message::CopyResultIn(surface, id),
                    move |id| Message::SpeakResultIn(surface, id),
                    move |id| Message::ReplaceResultIn(surface, id),
                    move |id| Message::RetryResultIn(surface, id),
                ),
                floating_status_text(id_prefix, state, &settings.ui_language),
            ))
            .id(format!("{id_prefix}.content"))
            .padding(16)
            .spacing(if id_prefix == "mini" { 4 } else { 6 })
            .width(Length::Fill)
            .height(Length::Fill),
        )
        .into_view()
}

fn floating_detected_language_label(
    id_prefix: &'static str,
    state: &FloatingWindowState,
) -> View<Message> {
    match state
        .detected_language
        .as_deref()
        .filter(|value| !value.is_empty())
    {
        Some(value) => styled_text_id(
            format!("{id_prefix}.detected_language"),
            value.to_string(),
            TextStyle::Caption,
        ),
        None => spacer()
            .id(format!("{id_prefix}.detected_language_placeholder"))
            .into_view(),
    }
}

fn floating_status_text(
    id_prefix: &'static str,
    state: &FloatingWindowState,
    locale: &str,
) -> View<Message> {
    let value = if state.status_text.trim().is_empty() {
        tr_locale(locale, "status.ready", "Ready")
    } else {
        state.status_text.clone()
    };

    row((
        spacer().width(Length::Fill),
        styled_text_id(format!("{id_prefix}.status"), value, TextStyle::Caption),
    ))
    .id(format!("{id_prefix}.status_row"))
    .align(Alignment::Center)
    .width(Length::Fill)
    .into_view()
}

fn floating_input_surface(
    id_prefix: &'static str,
    surface: QuickTranslateSurface,
    state: &FloatingWindowState,
    show_source_play: bool,
    locale: &str,
) -> View<Message> {
    let placeholder = floating_input_placeholder(locale);
    let input = if id_prefix == "mini" {
        let value = if state.text.trim().is_empty() {
            placeholder.clone()
        } else {
            state.text.clone()
        };
        styled_text_id(format!("{id_prefix}.input"), value, TextStyle::Body)
    } else {
        text_editor(state.text.clone())
            .id(format!("{id_prefix}.input"))
            .placeholder(placeholder)
            .min_height(40)
            .max_height(120)
            .frameless()
            .on_input(move |value| Message::FloatingSurfaceTextChanged(surface, value))
    };

    let content = if show_source_play {
        row((
            input,
            button("Play source")
                .id(format!("{id_prefix}.play_source"))
                .icon(icon::play())
                .icon_only()
                .width(Length::Fixed(28))
                .height(Length::Fixed(28))
                .tooltip("Play source text")
                .on_press(Message::SpeakResult),
        ))
        .id(format!("{id_prefix}.input_content"))
        .spacing(4)
        .align(Alignment::Start)
        .width(Length::Fill)
        .into_view()
    } else {
        input
    };

    card("")
        .id(format!("{id_prefix}.input_card"))
        .kind(CardKind::FloatingInput)
        .content(content)
        .into_view()
}

fn floating_input_placeholder(locale: &str) -> String {
    tr_locale(
        locale,
        "main.source_placeholder",
        "Enter or paste text to translate...",
    )
}

fn floating_header(
    id_prefix: &'static str,
    state: &FloatingWindowState,
    show_pin: bool,
) -> View<Message> {
    let pin = if show_pin {
        button("Pin")
            .id(format!("{id_prefix}.pin"))
            .icon(icon::pin())
            .icon_only()
            .width(Length::Fixed(28))
            .height(Length::Fixed(28))
            .tooltip("Pin window (stay on top)")
            .on_press(Message::TogglePin(!state.pinned))
    } else {
        spacer().width(Length::Fixed(28)).into_view()
    };

    row((
        pin,
        styled_text_id(
            format!("{id_prefix}.title"),
            state.title.clone(),
            TextStyle::Caption,
        ),
        button("Close")
            .id(format!("{id_prefix}.close"))
            .icon(icon::clear())
            .icon_only()
            .width(Length::Fixed(28))
            .height(Length::Fixed(28))
            .tooltip("Close")
            .on_press(Message::CloseWindow),
    ))
    .id(format!("{id_prefix}.header"))
    .spacing(4)
    .align(Alignment::Center)
    .space_between()
    .into_view()
}

fn main_translate_action_bar(state: &EasydictUiState) -> View<Message> {
    // .NET switches to the stacked layout at a 500 DIP *window* width
    // (`AdaptiveTrigger MinWindowWidth="500"`). `adaptive_switch` measures the
    // inner container width instead, which is the window minus the surface
    // padding (~p-3) and scrollbar, so the breakpoint is offset to keep the wide
    // inline layout at the same window sizes .NET does. Otherwise the action bar
    // stacks and pushes the results card off-screen.
    // .NET switches to the stacked layout at a 500 DIP *window* width
    // (`AdaptiveTrigger MinWindowWidth="500"`). `adaptive_switch` measures the
    // inner container width instead (window minus the surface padding/scrollbar),
    // so the breakpoint is offset to keep the wide inline layout at the same
    // window sizes .NET does; otherwise the action bar stacks and the 文A button
    // wraps to its own row.
    adaptive_switch(
        360,
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
            .width(Length::Fixed(130))
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
            .width(Length::Fixed(130))
            .on_change(Message::TargetLanguageChanged),
    ];
    if state.settings.theme != ThemeMode::Minimal {
        children.push(language_help_button());
    }
    children.push(main_translate_button(
        "TranslateButton",
        state.is_translating,
        state.main_translate_button_state.clone(),
    ));

    row(children)
        .id("ActionBarWide")
        .tw("gap-2 w-full items-center")
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
        main_translate_button(
            "TranslateButtonNarrow",
            state.is_translating,
            state.main_translate_button_state.clone(),
        ),
    ))
    .id("ActionBarNarrow")
    .spacing(4)
    .align(Alignment::Center)
    .width(Length::Fill)
    .into_view()
}

fn main_translate_button(id: &'static str, is_loading: bool, state: ControlState) -> View<Message> {
    if is_loading {
        progress_ring()
            .id(id)
            .size(16)
            .a11y(A11yHint::named("Translating"))
            .into_view()
    } else {
        primary_button("")
            .id(id)
            .icon(icon::translate())
            .primary_round()
            .width(Length::Fixed(40))
            .height(Length::Fixed(40))
            .tooltip(tr("main.translate", "Translate"))
            .state(state)
            .a11y(A11yHint::named(tr("main.translate", "Translate")))
            .on_press(Message::QuickTranslate)
    }
}

fn language_help_button() -> View<Message> {
    styled_text_id("LanguageHelpButton", "?", TextStyle::Body)
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
    translate_button_state: ControlState,
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
        Length::Fill
    };
    let source_width = if is_main { source_width } else { Length::Fill };

    let language_items = if is_main {
        selected_language_items(true, settings)
    } else {
        selected_floating_language_items(true, settings)
    };

    let bar = row((
        combo_box(language_items.clone())
            .id(format!("{id_prefix}.source_language"))
            .label("Source Language")
            .selected(source_language.to_string())
            .width(source_width)
            .on_change(move |value| Message::FloatingSourceLanguageChanged(surface, value)),
        button("Swap languages")
            .id(format!("{id_prefix}.swap"))
            .icon(icon::swap())
            .icon_only()
            .width(Length::Fixed(28))
            .height(Length::Fixed(28))
            .tooltip("Swap languages")
            .on_press(Message::SwapFloatingLanguages(surface)),
        combo_box(language_items)
            .id(format!("{id_prefix}.target_language"))
            .label("Target Language")
            .selected(target_language.to_string())
            .width(target_width)
            .on_change(move |value| Message::FloatingTargetLanguageChanged(surface, value)),
        floating_translate_button(
            format!("{id_prefix}.translate"),
            surface,
            is_translating,
            translate_button_state,
        ),
    ))
    .id(format!("{id_prefix}.language_bar"))
    .tw(if is_main {
        "gap-2 w-full items-center"
    } else {
        "gap-1 w-full items-center"
    });

    if is_main {
        bar.space_between().into_view()
    } else {
        bar.into_view()
    }
}

fn floating_translate_button(
    id: String,
    surface: QuickTranslateSurface,
    is_loading: bool,
    state: ControlState,
) -> View<Message> {
    if is_loading {
        progress_ring()
            .id(id)
            .size(14)
            .a11y(A11yHint::named("Translating"))
            .into_view()
    } else {
        primary_button("")
            .id(id)
            .icon(icon::translate())
            .primary_round()
            .width(Length::Fixed(32))
            .height(Length::Fixed(32))
            .tooltip(tr("main.translate", "Translate"))
            .state(state)
            .a11y(A11yHint::named(tr("main.translate", "Translate")))
            .on_press(Message::QuickTranslateIn(surface))
    }
}

fn styled_text(value: impl Into<String>, style: TextStyle) -> View<Message> {
    View::new(ViewToken::Text(TextToken {
        id: None,
        value: value.into(),
        style,
        width: None,
        height: None,
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
        width: None,
        height: None,
        selectable: false,
        a11y: A11yHint::default(),
    }))
}

fn sized_styled_text_id(
    id: impl Into<String>,
    value: impl Into<String>,
    style: TextStyle,
    width: Length,
    height: Length,
) -> View<Message> {
    View::new(ViewToken::Text(TextToken {
        id: Some(id.into()),
        value: value.into(),
        style,
        width: Some(width),
        height: Some(height),
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

fn settings_category_bar(state: &SettingsState) -> View<Message> {
    // Wrap the tab tiles with a 7-column cap (WinUI `ItemsWrapGrid
    // MaximumRowsOrColumns=7`); the framework handles row wrapping instead of a
    // hand-rolled `[0..5]/[5..]` split.
    let buttons: Vec<View<Message>> = SettingsSection::ALL
        .iter()
        .copied()
        .map(|section| settings_category_button(section, state, &state.ui_language))
        .collect();

    wrap(buttons)
        .id("settings.categories")
        .max_columns(7)
        .spacing(10)
        .run_spacing(10)
        .into_view()
}

fn settings_category_button(
    section: SettingsSection,
    state: &SettingsState,
    locale: &str,
) -> View<Message> {
    let label = settings_section_label(section, locale);
    button(label.clone())
        .id(format!("SettingsTab_{}", section.label()))
        .icon(section.icon())
        .tile()
        .width(Length::Fixed(86))
        .height(Length::Fixed(76))
        .tooltip(label)
        .hovered(state.hovered_section == Some(section))
        .pressed(state.pressed_section == Some(section))
        .selected(section == state.selected_section)
        .on_press(Message::SettingsSectionChanged(section.id().to_string()))
}

fn settings_section_label(section: SettingsSection, locale: &str) -> String {
    match section {
        SettingsSection::General => tr_locale(locale, "settings.tab.general", "General"),
        SettingsSection::Services => tr_locale(locale, "settings.tab.services", "Services"),
        SettingsSection::Views => tr_locale(locale, "settings.tab.views", "Views"),
        SettingsSection::Hotkeys => tr_locale(locale, "settings.tab.hotkeys", "Hotkeys"),
        SettingsSection::Advanced => tr_locale(locale, "settings.tab.advanced", "Advanced"),
        SettingsSection::Language => tr_locale(locale, "settings.tab.language", "Language"),
        SettingsSection::About => tr_locale(locale, "settings.tab.about", "About"),
    }
}

fn settings_section_content(state: &SettingsState) -> View<Message> {
    match state.selected_section {
        SettingsSection::General => settings_general_content(state, &state.ui_language),
        SettingsSection::Services => settings_services_content(state, &state.ui_language),
        SettingsSection::Views => settings_views_content(state, &state.ui_language),
        SettingsSection::Hotkeys => settings_hotkeys_content(state, &state.ui_language),
        SettingsSection::Advanced => settings_advanced_content(state),
        SettingsSection::Language => settings_language_content(state, &state.ui_language),
        SettingsSection::About => settings_about_content(&state.ui_language),
    }
}

fn settings_general_content(state: &SettingsState, locale: &str) -> View<Message> {
    let mut behavior_controls: Vec<View<Message>> = vec![
        column((
            styled_text_id(
                "AppThemeLabelText",
                tr_locale(locale, "settings.general.app_theme", "App Theme"),
                TextStyle::Body,
            ),
            combo_box(theme_combo_items(locale))
                .id("AppThemeCombo")
                .width(Length::Fixed(250))
                .label(tr_locale(locale, "settings.general.app_theme", "App Theme"))
                .selected(theme_id(state.theme))
                .on_change(Message::ThemeChanged),
            styled_text_id(
                "AppThemeDescriptionText",
                tr_locale(
                    locale,
                    "settings.general.app_theme.description",
                    "Choose how Easydict appears. Select System to follow Windows theme.",
                ),
                TextStyle::Caption,
            ),
        ))
        .id("settings.general.theme")
        .spacing(12)
        .align(Alignment::Start)
        .width(Length::Fill)
        .into_view(),
        settings_behavior_toggle(
            "settings.general.minimize_to_tray",
            tr_locale(
                locale,
                "settings.general.minimize_to_tray",
                "Minimize to system tray",
            ),
            "MinimizeToTrayToggle",
            settings_toggle_on_label(locale),
            state.minimize_to_tray,
            Message::ToggleMinimizeToTray,
        ),
        settings_behavior_toggle(
            "settings.general.start_minimized",
            tr_locale(
                locale,
                "settings.general.start_minimized",
                "Start minimized to tray",
            ),
            "MinimizeToTrayOnStartupToggle",
            settings_toggle_on_label(locale),
            state.start_minimized,
            Message::ToggleStartMinimized,
        ),
        settings_behavior_toggle(
            "settings.general.monitor_clipboard",
            tr_locale(
                locale,
                "settings.general.monitor_clipboard",
                "Monitor clipboard for text",
            ),
            "ClipboardMonitorToggle",
            settings_toggle_on_label(locale),
            state.monitor_clipboard,
            Message::ToggleMonitorClipboard,
        ),
        settings_behavior_toggle(
            "settings.general.mouse_selection",
            tr_locale(
                locale,
                "settings.general.mouse_selection",
                "Mouse selection translate",
            ),
            "MouseSelectionTranslateToggle",
            settings_toggle_on_label(locale),
            state.mouse_selection_translate,
            Message::ToggleMouseSelectionTranslate,
        ),
    ];

    if state.mouse_selection_translate {
        behavior_controls.push(mouse_selection_excluded_apps_panel(state, locale));
    }

    behavior_controls.push(settings_behavior_toggle(
        "settings.general.always_on_top",
        tr_locale(locale, "settings.general.always_on_top", "Always on top"),
        "AlwaysOnTopToggle",
        settings_toggle_on_label(locale),
        state.fixed_always_on_top,
        Message::ToggleFixedAlwaysOnTop,
    ));
    behavior_controls.push(settings_behavior_toggle(
        "settings.general.launch_at_startup",
        tr_locale(
            locale,
            "settings.general.launch_at_startup",
            "Launch at Windows startup",
        ),
        "LaunchAtStartupToggle",
        settings_toggle_on_label(locale),
        state.launch_at_startup,
        Message::ToggleLaunchAtStartup,
    ));
    behavior_controls.push(settings_behavior_toggle(
        "settings.general.hide_empty_service_results",
        tr_locale(
            locale,
            "settings.general.hide_empty_service_results",
            "Hide dictionaries with no result",
        ),
        "HideEmptyServiceResultsToggle",
        settings_winui_default_toggle_on_label(),
        state.hide_empty_service_results,
        Message::ToggleHideEmptyServiceResults,
    ));
    behavior_controls.push(local_dictionary_suggestions_row(state, locale));

    let behavior_section = column((
        styled_text_id(
            "SettingsGeneralBehaviorHeader",
            tr_locale(locale, "settings.general.behavior", "Behavior"),
            TextStyle::Subtitle,
        ),
        column(behavior_controls)
            .id("BehaviorSectionCard")
            .padding(16)
            .spacing(12)
            .width(Length::Fill)
            .tw("surface-card rounded-lg border w-full"),
    ))
    .id("BehaviorSection")
    .spacing(12)
    .width(Length::Fill);

    let tts_speed_control = column((
        styled_text_id(
            "TtsSpeedLabelText",
            tr_locale(
                locale,
                "settings.general.tts.speed",
                "TTS Reading Speed (0.5x - 3.0x)",
            ),
            TextStyle::Body,
        ),
        slider(tts_speed_value(&state.tts_speed))
            .id("TtsSpeedSlider")
            .range(0.5, 3.0)
            .step(0.5)
            .width(Length::Fixed(250))
            .state(state.tts_speed_slider_state.clone())
            .a11y(A11yHint::named(tr_locale(
                locale,
                "settings.general.tts.speed.a11y",
                "TTS speed",
            )))
            .on_change(|value| Message::TtsSpeedChanged(format_tts_speed(value))),
    ))
    .id("settings.general.tts_speed")
    .spacing(4)
    .align(Alignment::Start)
    .width(Length::Fill)
    .into_view();

    let auto_play_toggle = toggle_switch(
        settings_winui_default_toggle_on_label(),
        state.auto_play_translation,
    )
    .id("AutoPlayTranslationToggle")
    .header(tr_locale(
        locale,
        "settings.general.auto_play_translation",
        "Auto play translation",
    ))
    .state(state.auto_play_translation_toggle_state.clone())
    .on_toggle(Message::ToggleAutoPlayTranslation);

    let tts_card = column((
        tts_speed_control,
        column((auto_play_toggle,))
            .id("settings.general.auto_play_translation")
            .height(Length::Fixed(63))
            .width(Length::Fill),
    ))
    .id("TtsSettingsCard")
    .padding(16)
    .spacing(16)
    .width(Length::Fill)
    .tw("surface-card rounded-lg border w-full");

    let tts_children: Vec<View<Message>> = vec![
        styled_text_id(
            "TtsSettingsHeaderText",
            tr_locale(locale, "settings.general.tts.header", "TTS Output Settings"),
            TextStyle::Subtitle,
        ),
        tts_card.into_view(),
    ];

    let general_tab_content = column((
        behavior_section,
        column(tts_children)
            .id("TtsSettingsSection")
            .spacing(12)
            .width(Length::Fill),
    ))
    .id("GeneralTabContent")
    .spacing(24)
    .width(Length::Fill);

    column((general_tab_content,))
        .id("settings.general")
        .spacing(0)
        .width(Length::Fill)
        .into_view()
}

fn settings_behavior_toggle(
    id: &'static str,
    title: impl Into<String>,
    toggle_id: &'static str,
    on_label: impl Into<String>,
    checked: bool,
    on_toggle: impl Fn(bool) -> Message + Send + Sync + 'static,
) -> View<Message> {
    let title = title.into();
    column((toggle_switch(on_label, checked)
        .id(toggle_id)
        .header(title)
        .on_toggle(on_toggle),))
    .id(id)
    .spacing(14)
    .align(Alignment::Start)
    .height(Length::Fixed(63))
    .width(Length::Fill)
    .into_view()
}

fn theme_combo_items(locale: &str) -> [ComboBoxItem; 4] {
    [
        ComboBoxItem::new(
            "system",
            tr_locale(locale, "settings.general.theme.system", "System"),
        ),
        ComboBoxItem::new(
            "light",
            tr_locale(locale, "settings.general.theme.light", "Light"),
        ),
        ComboBoxItem::new(
            "dark",
            tr_locale(locale, "settings.general.theme.dark", "Dark"),
        ),
        ComboBoxItem::new(
            "minimal",
            tr_locale(locale, "settings.general.theme.minimal", "Minimal"),
        ),
    ]
}

fn settings_toggle_on_label(locale: &str) -> String {
    tr_locale(locale, "settings.toggle.on", "On")
}

fn settings_winui_default_toggle_on_label() -> &'static str {
    "On"
}

fn local_dictionary_suggestions_row(state: &SettingsState, locale: &str) -> View<Message> {
    let show_hint = state.imported_mdx_dictionaries.is_empty();
    let mut children: Vec<View<Message>> = vec![
        row((
            styled_text_id(
                "EnableLocalDictionarySuggestionsLabelText",
                tr_locale(
                    locale,
                    "settings.general.local_dictionary_suggestions",
                    "Enable custom dictionary input suggestions",
                ),
                TextStyle::Body,
            ),
            styled_text_id(
                "ExperimentalLabelText",
                tr_locale(locale, "settings.general.experimental", "Experimental"),
                TextStyle::Caption,
            ),
        ))
        .id("EnableLocalDictionarySuggestionsHeader")
        .spacing(6)
        .align(Alignment::Center)
        .into_view(),
        toggle_switch(
            settings_winui_default_toggle_on_label(),
            state.local_dictionary_suggestions,
        )
        .id("EnableLocalDictionarySuggestionsToggle")
        .enabled(!state.imported_mdx_dictionaries.is_empty())
        .on_toggle(Message::ToggleLocalDictionarySuggestions),
    ];

    if show_hint {
        children.push(styled_text_id(
            "EnableLocalDictionarySuggestionsHintText",
            tr_locale(
                locale,
                "settings.general.local_dictionary_suggestions.empty",
                "Import an MDX dictionary to enable local input suggestions.",
            ),
            TextStyle::Caption,
        ));
    }

    let mut row = column(children)
        .id("settings.general.local_dictionary_suggestions")
        .spacing(6)
        .align(Alignment::Start)
        .width(Length::Fill);

    if !show_hint {
        row = row.height(Length::Fixed(63));
    }

    row.into_view()
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

fn mouse_selection_excluded_apps_panel(state: &SettingsState, locale: &str) -> View<Message> {
    column((
        styled_text_id(
            "MouseSelectionExcludedAppsHeaderText",
            tr_locale(locale, "settings.general.excluded_apps", "Excluded apps"),
            TextStyle::Body,
        ),
        text_editor(state.mouse_selection_excluded_apps.clone())
            .id("MouseSelectionExcludedAppsBox")
            .placeholder("code, slack, discord")
            .min_height(36)
            .max_height(36)
            .on_input(Message::MouseSelectionExcludedAppsChanged),
        styled_text_id(
            "MouseSelectionExcludedAppsDescriptionText",
            tr_locale(
                locale,
                "settings.general.excluded_apps.description",
                "Process names to exclude from mouse selection translate, separated by commas. Example: \"code\" for VS Code.",
            ),
            TextStyle::CaptionSmall,
        ),
    ))
    .id("MouseSelectionExcludedAppsPanel")
    .spacing(4)
    .width(Length::Fixed(350))
    .margin(Edges {
        left: 44,
        ..Edges::ZERO
    })
    .into_view()
}

fn settings_services_content(state: &SettingsState, locale: &str) -> View<Message> {
    let mut service_configuration_children: Vec<View<Message>> = vec![
        services_section_header(
            "ServiceConfigurationHeaderRow",
            "ServiceConfigurationHeaderText",
            "ServiceConfigHelpIcon",
            tr_locale(
                locale,
                "settings.services.configuration.title",
                "Service Configuration",
            ),
            tr_locale(
                locale,
                "settings.services.configuration.help",
                "Configure API keys, endpoints, and models for each translation service.",
            ),
        ),
        styled_text_id(
            "ServiceConfigurationDescriptionText",
            tr_locale(
                locale,
                "settings.services.configuration.description",
                "Configure API keys, endpoints, and models for each translation service.",
            ),
            TextStyle::Caption,
        ),
        deepl_service_expander(state),
        local_ai_service_expander(state),
        ollama_service_expander(state, locale),
        open_ai_service_expander(state),
    ];
    service_configuration_children.extend(llm_provider_descriptors().iter().map(|descriptor| {
        if descriptor.service_id == "builtin" {
            builtin_ai_service_expander(state, descriptor)
        } else {
            llm_provider_service_expander(state, descriptor)
        }
    }));
    service_configuration_children.extend(traditional_http_service_expanders(state));
    service_configuration_children.push(imported_mdx_config_panel(state));
    service_configuration_children.push(no_config_services_section());

    column((
        column((
            services_section_header(
                "EnabledServicesHeaderRow",
                "EnabledServicesHeaderText",
                "EnabledServicesHelpIcon",
                tr_locale(locale, "settings.services.enabled.title", "Enabled Services"),
                tr_locale(
                    locale,
                    "settings.services.enabled.help",
                    "Choose service visibility in the Views tab.",
                ),
            ),
            styled_text_id(
                "EnabledServicesDescriptionText",
                tr_locale(
                    locale,
                    "settings.services.enabled.description",
                    "Select which translation services to display in each window. Multiple services will run in parallel.",
                ),
                TextStyle::Caption,
            ),
            row((
                button(tr_locale(
                    locale,
                    "settings.services.mdx.import",
                    "Import MDX Dictionary",
                ))
                .id("ImportMdxDictionaryButton")
                .state(state.import_mdx_button_state.clone())
                .height(Length::Fixed(29))
                .on_press(Message::ImportMdxDictionary),
                styled_text_id(
                    "ImportedMdxSummaryText",
                    mdx_dictionary_summary_locale(state, locale),
                    TextStyle::Caption,
                ),
            ))
            .id("settings.services.mdx")
            .spacing(8)
            .align(Alignment::Center)
            .into_view(),
            services_international_panel(state, locale),
        ))
        .id("EnabledServicesSection")
        .spacing(12)
        .width(Length::Fill),
        column(service_configuration_children)
            .id("ServiceConfigurationSection")
            .spacing(12)
            .width(Length::Fill),
    ))
    .id("settings.services")
    .spacing(24)
    .width(Length::Fill)
    .into_view()
}

fn services_section_header(
    row_id: &'static str,
    title_id: &'static str,
    help_id: &'static str,
    title: String,
    help: String,
) -> View<Message> {
    row((
        styled_text_id(title_id, title, TextStyle::SectionTitle),
        button("")
            .id(help_id)
            .icon(icon::help())
            .icon_only()
            .width(Length::Fixed(20))
            .height(Length::Fixed(20))
            .tooltip(help.clone())
            .a11y(A11yHint::named(help))
            .on_press(Message::Noop),
    ))
    .id(row_id)
    .spacing(8)
    .align(Alignment::Center)
    .into_view()
}

fn services_international_panel(state: &SettingsState, locale: &str) -> View<Message> {
    column((
        row((
            styled_text_id(
                "EnableInternationalServicesHeaderText",
                tr_locale(
                    locale,
                    "settings.services.international.title",
                    "Enable International Services",
                ),
                TextStyle::Body,
            ),
            spacer().width(Length::Fill).into_view(),
            toggle_switch(
                tr_locale(locale, "settings.toggle.on", "On"),
                state.enable_international_services,
            )
            .id("EnableInternationalServicesToggle")
            .state(state.international_services_toggle_state.clone())
            .width(Length::Fixed(66))
            .height(Length::Fixed(40))
            .on_toggle(Message::ToggleInternationalServices),
        ))
        .id("settings.services.international.header")
        .spacing(8)
        .align(Alignment::Center)
        .width(Length::Fill)
        .into_view(),
        styled_text_id(
            "EnableInternationalServicesDescriptionText",
            tr_locale(
                locale,
                "settings.services.international.description",
                "Some services (Google, DeepL, OpenAI, Gemini, etc.) require international network access and may be unavailable in some regions.",
            ),
            TextStyle::CaptionSmall,
        ),
    ))
    .id("settings.services.international")
    .tw("surface-card border rounded-lg px-3 py-2 gap-1 w-full")
    .padding(12)
    .spacing(4)
    .height(Length::Fixed(76))
    .width(Length::Fill)
    .into_view()
}

fn service_expander(
    state: &SettingsState,
    service_id: &'static str,
    expanded: bool,
    id: impl Into<String>,
    title: impl Into<String>,
    status_id: impl Into<String>,
    status: impl Into<String>,
    content_id: impl Into<String>,
    content: Vec<View<Message>>,
) -> View<Message> {
    let service_id = service_id.to_string();
    let toggle_service_id = service_id.clone();
    let mut builder = expander(title)
        .id(id)
        .icon(service_configuration_icon(&service_id))
        .expanded(expanded)
        .header_state(settings_service_expander_header_state(state, &service_id))
        .on_toggle(move |expanded| {
            Message::ToggleServiceConfigurationExpanded(toggle_service_id.clone(), expanded)
        })
        .content(
            column(content)
                .id(content_id)
                .spacing(12)
                .width(Length::Fill),
        );

    if let Some(status) = service_header_status(&service_id, status.into()) {
        let status_style = match (service_id.as_str(), status.trim()) {
            ("windows-local-ai", "✓") => TextStyle::Success,
            ("windows-local-ai", "⚠") => TextStyle::Warning,
            _ => TextStyle::BodyStrong,
        };
        let status_view = if service_id == "windows-local-ai" {
            sized_styled_text_id(
                status_id,
                status.clone(),
                status_style,
                Length::Fixed(local_ai_header_status_width(&status)),
                Length::Fixed(19),
            )
        } else {
            styled_text_id(status_id, status, status_style)
        };
        builder = builder.trailing((status_view,));
    }

    builder.into_view()
}

fn settings_service_expander_header_state(state: &SettingsState, service_id: &str) -> ControlState {
    if let Some(control_state) = state.service_expander_states.get(service_id) {
        return control_state.clone();
    }

    match service_id {
        "deepl" => state.deepl_service_expander_state.clone(),
        _ => ControlState::default(),
    }
}

fn service_configuration_icon(service_id: &str) -> win_fluent::IconToken {
    match service_id {
        "google" => win_fluent::IconToken::with_image(
            "service-google",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Google.scale-100.png"
            ),
        ),
        "linguee" => win_fluent::IconToken::with_image(
            "service-linguee",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Linguee.scale-100.png"
            ),
        ),
        "deepl" => win_fluent::IconToken::with_image(
            "service-deepl",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/DeepL.scale-100.png"
            ),
        ),
        "windows-local-ai" => win_fluent::IconToken::with_image(
            "service-windows-local-ai",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/windows-local-ai.scale-100.png"
            ),
        ),
        "ollama" => win_fluent::IconToken::with_image(
            "service-ollama",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Ollama.scale-100.png"
            ),
        ),
        "openai" => win_fluent::IconToken::with_image(
            "service-openai",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/OpenAI.scale-100.png"
            ),
        ),
        "custom-openai" => win_fluent::IconToken::with_image(
            "service-custom-openai",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/CustomOpenAI.scale-100.png"
            ),
        ),
        "builtin" => win_fluent::IconToken::with_image(
            "service-builtin-ai",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/BuiltInAI.scale-100.png"
            ),
        ),
        "deepseek" => win_fluent::IconToken::with_image(
            "service-deepseek",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/DeepSeek.scale-100.png"
            ),
        ),
        "groq" => win_fluent::IconToken::with_image(
            "service-groq",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Groq.scale-100.png"
            ),
        ),
        "zhipu" => win_fluent::IconToken::with_image(
            "service-zhipu",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Zhipu.scale-100.png"
            ),
        ),
        "github" => win_fluent::IconToken::with_image(
            "service-github",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/GitHubOnLight.scale-100.png"
            ),
        ),
        "gemini" => win_fluent::IconToken::with_image(
            "service-gemini",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Gemini.scale-100.png"
            ),
        ),
        "doubao" => win_fluent::IconToken::with_image(
            "service-doubao",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Doubao.scale-100.png"
            ),
        ),
        "caiyun" => win_fluent::IconToken::with_image(
            "service-caiyun",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Caiyun.scale-100.png"
            ),
        ),
        "niutrans" => win_fluent::IconToken::with_image(
            "service-niutrans",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/NiuTrans.scale-100.png"
            ),
        ),
        "youdao" => win_fluent::IconToken::with_image(
            "service-youdao",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Youdao.scale-100.png"
            ),
        ),
        "volcano" => win_fluent::IconToken::with_image(
            "service-volcano",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Volcano.scale-100.png"
            ),
        ),
        _ => icon::translate(),
    }
}

fn service_header_status(service_id: &str, status: String) -> Option<String> {
    if service_id == "windows-local-ai" {
        return Some(if local_ai_header_status_is_ready(&status) {
            "✓".to_string()
        } else {
            "⚠".to_string()
        });
    }

    let trimmed = status.trim();
    if trimmed.is_empty()
        || matches!(
            trimmed,
            "Not tested"
                | "Not refreshed"
                | "Web dictionary mode"
                | "API token required"
                | "API key required"
        )
    {
        None
    } else {
        Some(trimmed.to_string())
    }
}

fn local_ai_header_status_is_ready(status: &str) -> bool {
    let normalized = status.trim().to_ascii_lowercase();
    if normalized.is_empty() || normalized.contains("not ready") {
        return false;
    }

    normalized == "ready"
        || normalized.contains(" is ready")
        || normalized.contains(" model ready")
        || normalized.contains("status_ready")
}

fn local_ai_header_status_width(status: &str) -> u16 {
    if status.trim() == "✓" {
        14
    } else {
        20
    }
}

fn settings_field_stack(
    id: impl Into<String>,
    width: u16,
    children: Vec<View<Message>>,
) -> View<Message> {
    column(children)
        .id(id)
        .spacing(4)
        .width(Length::Fixed(width))
        .into_view()
}

fn secret_field_stack(
    id: impl Into<String>,
    width: u16,
    header: View<Message>,
    editor: View<Message>,
    reveal_id: impl Into<String>,
    reveal_label: &'static str,
) -> View<Message> {
    let id = id.into();
    let row_id = format!("{id}.editor");
    let editor = text_editor_view_width_with_trailing_icon(editor, width, reveal_id, reveal_label);
    settings_field_stack(
        id,
        width,
        vec![
            header,
            row((editor,))
                .id(row_id)
                .spacing(0)
                .align(Alignment::Center)
                .width(Length::Fill)
                .height(Length::Fixed(36))
                .into_view(),
        ],
    )
}

fn text_editor_view_width_with_trailing_icon(
    view: View<Message>,
    width: u16,
    reveal_id: impl Into<String>,
    reveal_label: &'static str,
) -> View<Message> {
    match view.into_token() {
        ViewToken::TextEditor(mut token) => {
            token.width = Some(Length::Fixed(width));
            token.trailing_icon = Some(win_fluent::view::TextEditorTrailingIcon {
                id: reveal_id.into(),
                icon: win_fluent::IconToken::with_glyph("reveal-secret", '\u{E890}'),
                label: reveal_label.to_string(),
                width: 28,
                height: 28,
                spacing: 6,
            });
            View::new(ViewToken::TextEditor(token))
        }
        token => View::new(token),
    }
}

fn fixed_width_field(
    id: impl Into<String>,
    width: u16,
    child: impl IntoView<Message>,
) -> View<Message> {
    column((child,))
        .id(id)
        .width(Length::Fixed(width))
        .into_view()
}

fn settings_checkbox_row(id: impl Into<String>, checkbox: View<Message>) -> View<Message> {
    row((checkbox,))
        .id(id)
        .align(Alignment::Center)
        .height(Length::Fixed(32))
        .width(Length::Fill)
        .into_view()
}

fn settings_text_box_field(
    id: impl Into<String>,
    label_id: impl Into<String>,
    label: impl Into<String>,
    value: impl Into<String>,
    placeholder: impl Into<String>,
    width: u16,
    height: u16,
    on_input: impl Fn(String) -> Message + Send + Sync + 'static,
) -> View<Message> {
    let id = id.into();
    let value = value.into();
    let placeholder = placeholder.into();
    let field_id = format!("{id}Field");

    column((
        styled_text_id(label_id, label, TextStyle::Caption),
        text_editor(value)
            .id(id)
            .placeholder(placeholder)
            .width(Length::Fixed(width))
            .max_height(32)
            .on_input(on_input),
    ))
    .id(field_id)
    .spacing(6)
    .width(Length::Fixed(width))
    .height(Length::Fixed(height))
    .into_view()
}

fn service_configuration_expanded(state: &SettingsState, service_id: &str) -> bool {
    state
        .expanded_service_configurations
        .iter()
        .any(|id| id == service_id)
}

fn imported_mdx_config_panel(state: &SettingsState) -> View<Message> {
    column(
        state
            .imported_mdx_dictionaries
            .iter()
            .map(|dictionary| imported_mdx_dictionary_expander(state, dictionary))
            .collect::<Vec<_>>(),
    )
    .id("ImportedMdxConfigPanel")
    .spacing(8)
    .into_view()
}

fn imported_mdx_dictionary_expander(
    state: &SettingsState,
    dictionary: &ImportedMdxDictionary,
) -> View<Message> {
    let service_id = dictionary.service_id.clone();
    let toggle_service_id = service_id.clone();
    let requires_credentials = imported_mdx_dictionary_requires_credentials(dictionary);
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

    if requires_credentials {
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
            "Credential-encrypted dictionaries require email and registration code.",
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

    expander(dictionary.display_name.clone())
        .id(format!("ImportedMdxDictionaryExpander.{service_id}"))
        .icon(imported_mdx_dictionary_icon(requires_credentials))
        .expanded(service_configuration_expanded(state, &service_id))
        .on_toggle(move |expanded| {
            Message::ToggleServiceConfigurationExpanded(toggle_service_id.clone(), expanded)
        })
        .content(
            column(content)
                .id(format!("ImportedMdxDictionaryContent.{service_id}"))
                .spacing(12)
                .width(Length::Fill),
        )
        .into_view()
}

fn imported_mdx_dictionary_icon(requires_credentials: bool) -> win_fluent::IconToken {
    if requires_credentials {
        win_fluent::IconToken::with_glyph("service-mdx-encrypted", '\u{E72E}')
    } else {
        win_fluent::IconToken::with_glyph("service-mdx", '\u{E8D4}')
    }
}

fn imported_mdx_dictionary_requires_credentials(dictionary: &ImportedMdxDictionary) -> bool {
    dictionary.is_encrypted && !native_mdx_dictionary_can_route_natively(&dictionary.snapshot())
}

fn mdx_mdd_summary(dictionary: &ImportedMdxDictionary) -> String {
    match dictionary.mdd_file_paths.len() {
        0 => "MDD resources: none discovered".to_string(),
        1 => format!("MDD resources: {}", dictionary.mdd_file_paths[0]),
        count => format!("MDD resources: {count} files"),
    }
}

fn local_ai_service_expander(state: &SettingsState) -> View<Message> {
    let mut content = vec![
        column(vec![
            styled_text_id(
                "LocalAIProviderLabelText",
                tr("settings.services.local_ai.provider", "Provider"),
                TextStyle::Caption,
            ),
            combo_box(local_ai_provider_items())
                .id("LocalAIProviderCombo")
                .label(format!(
                    "Windows Local AI {}",
                    tr("settings.services.local_ai.provider", "Provider")
                ))
                .width(Length::Fixed(520))
                .height(Length::Fixed(40))
                .selected(state.local_ai_provider.as_str())
                .on_change(Message::LocalAiProviderChanged)
                .into_view(),
        ])
        .id("LocalAIProviderPanel")
        .spacing(6)
        .into_view(),
        styled_text_id(
            "WindowsLocalAIDescriptionText",
            local_ai_provider_description(&state.local_ai_provider),
            TextStyle::Caption,
        ),
    ];

    match state.local_ai_provider.as_str() {
        local_ai_provider_modes::WINDOWS_AI => content.push(windows_ai_config_panel(state)),
        local_ai_provider_modes::FOUNDRY_LOCAL => content.push(foundry_local_config_panel(state)),
        local_ai_provider_modes::OPENVINO => content.push(open_vino_config_panel(state)),
        _ => {
            content.push(windows_ai_config_panel(state));
            content.push(foundry_local_config_panel(state));
            content.push(open_vino_config_panel(state));
        }
    }

    service_expander(
        state,
        "windows-local-ai",
        service_configuration_expanded(state, "windows-local-ai"),
        "WindowsLocalAIExpander",
        "Windows Local AI",
        "WindowsLocalAIStatusBadge",
        state.local_ai_status.clone(),
        "settings.services.local_ai.content",
        content,
    )
}

fn windows_ai_config_panel(state: &SettingsState) -> View<Message> {
    let mut content = vec![
        row((
            styled_text_id(
                "WindowsLocalAISectionTitleText",
                "Phi Silica",
                TextStyle::BodyStrong,
            ),
            styled_text_id(
                "WindowsLocalAISectionRatingText",
                "★★★★★",
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
            .height(Length::Fixed(40))
            .on_press(Message::PrepareLocalAiModel)
            .into_view(),
    ];

    if state.local_ai_prepare_progress != "Idle" {
        content.push(
            column(vec![
                styled_text_id(
                    "WindowsLocalAIPrepareProgressText",
                    state.local_ai_prepare_progress.clone(),
                    TextStyle::Caption,
                ),
                progress_bar()
                    .id("WindowsLocalAIPrepareProgressBar")
                    .active(state.local_ai_prepare_progress != "Idle")
                    .height(4)
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
        );
    }

    column(content)
        .id("WindowsLocalAIConfigPanel")
        .spacing(10)
        .into_view()
}

fn foundry_local_config_panel(state: &SettingsState) -> View<Message> {
    let mut content = vec![
        row((
            styled_text_id(
                "FoundryLocalTitleText",
                "Foundry Local",
                TextStyle::BodyStrong,
            ),
            styled_text_id("FoundryLocalRatingText", "★★★★", TextStyle::Caption),
        ))
        .spacing(8)
        .align(Alignment::Center)
        .into_view(),
        settings_text_box_field(
            "FoundryLocalEndpointBox",
            "FoundryLocalEndpointLabelText",
            tr(
                "settings.services.local_ai.foundry.endpoint",
                "Endpoint (optional)",
            ),
            state.foundry_local_endpoint.clone(),
            tr(
                "settings.services.local_ai.foundry.endpoint_placeholder",
                "Auto-detect from foundry service status",
            ),
            762,
            59,
            Message::FoundryLocalEndpointChanged,
        ),
        settings_text_box_field(
            "FoundryLocalModelBox",
            "FoundryLocalModelLabelText",
            tr("settings.services.local_ai.foundry.model", "Model"),
            state.foundry_local_model.clone(),
            "qwen2.5-0.5b",
            762,
            56,
            Message::FoundryLocalModelChanged,
        ),
    ];

    if should_show_foundry_local_recovery(&state.foundry_local_status) {
        content.extend([
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
                    .height(Length::Fixed(40))
                    .on_press(Message::StartFoundryLocal),
                button("Install Foundry Local")
                    .id("FoundryLocalInstallLink")
                    .icon(icon::add())
                    .height(Length::Fixed(40))
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
                tr(
                    "settings.services.local_ai.foundry.config_description",
                    "Leave endpoint empty to auto-detect it from foundry service status; set it manually if the local service uses a fixed OpenAI-compatible endpoint.",
                ),
                TextStyle::Caption,
            ),
        ]);
    }

    column(content)
        .id("FoundryLocalConfigPanel")
        .spacing(10)
        .into_view()
}

fn should_show_foundry_local_recovery(status: &str) -> bool {
    let normalized = status.trim();
    !normalized.is_empty() && normalized != "Endpoint auto-detected at runtime"
}

fn open_vino_config_panel(state: &SettingsState) -> View<Message> {
    let mut content = vec![
        row((
            styled_text_id(
                "OpenVinoTitleText",
                "OpenVINO (local NLLB)",
                TextStyle::BodyStrong,
            ),
            styled_text_id("OpenVinoRatingText", "★★", TextStyle::Caption),
            styled_text_id(
                "OpenVinoStatusBadge",
                state.open_vino_status.clone(),
                TextStyle::Caption,
            ),
        ))
        .spacing(8)
        .align(Alignment::Center)
        .into_view(),
        status_badge(state.open_vino_status.clone(), ValidationSeverity::Info)
            .id("OpenVinoStatusBar")
            .into_view(),
    ];

    if state.open_vino_download_progress != "Idle" {
        content.push(
            progress_bar()
                .id("OpenVinoDownloadProgress")
                .active(state.open_vino_download_progress != "Idle")
                .height(4)
                .label(state.open_vino_download_progress.clone())
                .into_view(),
        );
    }

    content.extend([
        button("Download model")
            .id("OpenVinoDownloadButton")
            .icon(icon::refresh())
            .height(Length::Fixed(40))
            .on_press(Message::DownloadOpenVinoModel)
            .into_view(),
        styled_text_id(
            "OpenVinoDescriptionText",
            "Runs NLLB-200 locally with ONNX Runtime + OpenVINO. Hardware acceleration is best effort and falls back to CPU when needed.",
            TextStyle::Caption,
        ),
    ]);

    column(content)
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

fn local_ai_provider_description(provider: &str) -> String {
    match provider {
        local_ai_provider_modes::WINDOWS_AI => tr(
            "settings.services.local_ai.description.windows_ai",
            "Uses Phi Silica on Copilot+ PCs through Windows AI APIs. No API key, no network.",
        ),
        local_ai_provider_modes::FOUNDRY_LOCAL => tr(
            "settings.services.local_ai.description.foundry",
            "Runs local models through the Microsoft Foundry Local OpenAI-compatible endpoint. Requires Foundry Local installed and a local model available.",
        ),
        local_ai_provider_modes::OPENVINO => tr(
            "settings.services.local_ai.description.openvino",
            "Runs NLLB-200 locally with ONNX Runtime + OpenVINO. Hardware acceleration is best effort; unsupported graph parts fall back to CPU. No API key, no network.",
        ),
        _ => tr(
            "settings.services.local_ai.description.auto",
            "Auto tries Phi Silica first, then Foundry Local, then OpenVINO/NLLB as the local translation fallback. No cloud API key.",
        ),
    }
}

fn local_ai_provider_items() -> [ComboBoxItem; 4] {
    [
        ComboBoxItem::new(
            local_ai_provider_modes::AUTO,
            "Auto (Phi Silica → Foundry Local → OpenVINO)",
        ),
        ComboBoxItem::new(
            local_ai_provider_modes::WINDOWS_AI,
            "Phi Silica (Copilot+ PC)  ★★★★★",
        ),
        ComboBoxItem::new(
            local_ai_provider_modes::FOUNDRY_LOCAL,
            "Foundry Local  ★★★★",
        ),
        ComboBoxItem::new(
            local_ai_provider_modes::OPENVINO,
            "OpenVINO (NLLB-200, local)  ★★",
        ),
    ]
}

fn ollama_service_expander(state: &SettingsState, locale: &str) -> View<Message> {
    service_expander(
        state,
        "ollama",
        service_configuration_expanded(state, "ollama"),
        "OllamaServiceExpander",
        "Ollama (Local LLM)",
        "OllamaStatusText",
        state.ollama_status.clone(),
        "settings.services.ollama.content",
        vec![
            fixed_width_field(
                "OllamaEndpointField",
                450,
                column((
                    styled_text_id(
                        "OllamaEndpointHeaderText",
                        tr_locale(
                            locale,
                            "settings.services.ollama.endpoint_optional",
                            "Endpoint (Optional)",
                        ),
                        TextStyle::Body,
                    ),
                    text_editor(state.ollama_endpoint.clone())
                        .id("OllamaEndpointBox")
                        .placeholder("http://localhost:11434/v1/chat/completions")
                        .max_height(36)
                        .on_input(Message::OllamaEndpointChanged),
                ))
                .id("OllamaEndpointStack")
                .spacing(4)
                .width(Length::Fill),
            ),
            row((
                combo_box(ollama_model_items())
                    .id("OllamaModelCombo")
                    .label(tr_locale(locale, "settings.services.ollama.model", "Model"))
                    .width(Length::Fixed(200))
                    .selected(state.ollama_model.as_str())
                    .on_change(Message::OllamaModelChanged),
                button(tr_locale(
                    locale,
                    "settings.services.ollama.refresh",
                    "Refresh",
                ))
                .id("RefreshOllamaButton")
                .height(Length::Fixed(32))
                .on_press(Message::RefreshOllamaModels),
                button(tr_locale(locale, "settings.services.test", "Test"))
                    .id("TestOllamaButton")
                    .height(Length::Fixed(29))
                    .on_press(Message::TestOllama),
            ))
            .spacing(8)
            .align(Alignment::End)
            .into_view(),
            styled_text(
                "Ollama must be running locally. Click Refresh to load available models.",
                TextStyle::Caption,
            ),
        ],
    )
}

fn open_ai_service_expander(state: &SettingsState) -> View<Message> {
    service_expander(
        state,
        "openai",
        service_configuration_expanded(state, "openai"),
        "OpenAIServiceExpander",
        "OpenAI",
        "OpenAIStatusText",
        state.open_ai_test_status.clone(),
        "settings.services.openai.content",
        vec![
            secret_field_stack(
                "OpenAIKeyField",
                350,
                styled_text_id("OpenAIKeyHeaderText", "API Key", TextStyle::Body),
                text_editor(state.open_ai_api_key.clone())
                    .id("OpenAIKeyBox")
                    .placeholder("sk-...")
                    .max_height(36)
                    .on_input(Message::OpenAIApiKeyChanged)
                    .into_view(),
                "OpenAIKeyRevealButton",
                "Reveal API key",
            ),
            fixed_width_field(
                "OpenAIEndpointField",
                450,
                text_editor(state.open_ai_endpoint.clone())
                    .id("OpenAIEndpointBox")
                    .placeholder("https://api.openai.com/v1/responses")
                    .max_height(36)
                    .on_input(Message::OpenAIEndpointChanged),
            ),
                combo_box(open_ai_api_format_items())
                    .id("OpenAIApiFormatCombo")
                    .label("API Format")
                    .width(Length::Fixed(280))
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
                    .width(Length::Fixed(280))
                    .selected(state.open_ai_model.as_str())
                    .on_change(Message::OpenAIModelChanged)
                    .into_view(),
                styled_text(
                    "Auto-detect picks /responses for Responses API endpoints; otherwise it uses Chat Completions.",
                    TextStyle::Caption,
                ),
                button("Test")
                    .id("TestOpenAIButton")
                    .height(Length::Fixed(29))
                    .on_press(Message::TestOpenAI)
                    .into_view(),
        ],
    )
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
    let mut content = vec![secret_field_stack(
        format!("{}Field", descriptor.key_box_id),
        350,
        styled_text_id(
            descriptor.key_header_id,
            descriptor.key_label,
            TextStyle::Body,
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
        descriptor.key_reveal_id,
        "Reveal secret",
    )];

    if let Some(endpoint_box_id) = descriptor.endpoint_box_id {
        content.push(fixed_width_field(
            format!("{endpoint_box_id}Field"),
            450,
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
                }),
        ));
    }

    content.extend([
        combo_box(provider_model_items(descriptor))
            .id(descriptor.model_box_id)
            .label("Model")
            .width(Length::Fixed(provider_model_width(descriptor)))
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
            .height(Length::Fixed(29))
            .on_press(Message::TestServiceProvider(
                descriptor.service_id.to_string(),
            ))
            .into_view(),
    ]);

    service_expander(
        state,
        descriptor.service_id,
        service_configuration_expanded(state, descriptor.service_id),
        descriptor.expander_id,
        descriptor.title,
        descriptor.status_id,
        setting.status,
        format!("settings.services.{}.content", descriptor.service_id),
        content,
    )
}

fn builtin_ai_service_expander(
    state: &SettingsState,
    descriptor: &LlmProviderDescriptor,
) -> View<Message> {
    let setting = service_provider_setting(state, descriptor);
    let content = vec![
        builtin_ai_hint_bar(),
        combo_box(provider_model_items(descriptor))
            .id(descriptor.model_box_id)
            .label("Model")
            .width(Length::Fixed(provider_model_width(descriptor)))
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
        secret_field_stack(
            format!("{}Field", descriptor.key_box_id),
            350,
            styled_text_id(
                descriptor.key_header_id,
                descriptor.key_label,
                TextStyle::Body,
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
            descriptor.key_reveal_id,
            "Reveal secret",
        ),
        styled_text_id(
            "BuiltInDescriptionText",
            descriptor.description,
            TextStyle::Caption,
        ),
        button("Test")
            .id(descriptor.test_button_id)
            .height(Length::Fixed(29))
            .on_press(Message::TestServiceProvider(
                descriptor.service_id.to_string(),
            ))
            .into_view(),
    ];

    service_expander(
        state,
        descriptor.service_id,
        service_configuration_expanded(state, descriptor.service_id),
        descriptor.expander_id,
        descriptor.title,
        descriptor.status_id,
        setting.status,
        "settings.services.builtin.content",
        content,
    )
}

fn builtin_ai_hint_bar() -> View<Message> {
    row((
        styled_text_id("BuiltInAIHintIcon", "i", TextStyle::BodyStrong),
        column((
            styled_text_id("BuiltInAIHintTitleText", "Hint", TextStyle::BodyStrong),
            styled_text_id(
                "BuiltInAIHintMessageText",
                "The built-in key has limited free quota and is not guaranteed to always be available. For stable use, get your own free API key.",
                TextStyle::Caption,
            ),
        ))
        .id("BuiltInAIHintText")
        .spacing(2)
        .width(Length::Fill)
        .into_view()
    ))
    .id("BuiltInAIHintBar")
    .spacing(12)
    .align(Alignment::Center)
    .width(Length::Fill)
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

fn provider_model_width(descriptor: &LlmProviderDescriptor) -> u16 {
    match descriptor.service_id {
        "custom-openai" => 200,
        "doubao" => 300,
        _ => 280,
    }
}

fn no_config_services_section() -> View<Message> {
    let mut service_rows = vec![no_config_service_row(
        "FreeServiceGoogleTranslateRow",
        "google",
        "Google Translate",
    )];

    service_rows.extend(linguee_no_config_service_rows());

    card("")
        .id("settings.services.free_services")
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
                    "Google Translate works out of the box without API keys.",
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
        "linguee",
        "Linguee Dictionary",
    )]
}

#[cfg(not(feature = "enable-linguee-service"))]
fn linguee_no_config_service_rows() -> Vec<View<Message>> {
    Vec::new()
}

fn no_config_service_row(
    id: &'static str,
    service_id: &'static str,
    label: &'static str,
) -> View<Message> {
    row((
        button("")
            .id(format!("{id}.icon"))
            .icon(service_configuration_icon(service_id))
            .icon_only()
            .enabled(false)
            .width(Length::Fixed(18))
            .height(Length::Fixed(18))
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
    service_expander(
        state,
        "caiyun",
        service_configuration_expanded(state, "caiyun"),
        "CaiyunServiceExpander",
        "Caiyun",
        "CaiyunStatusText",
        state.caiyun_status.clone(),
        "settings.services.caiyun.content",
        vec![
            secret_field_stack(
                "CaiyunKeyField",
                350,
                styled_text_id("CaiyunKeyHeaderText", "API Key", TextStyle::Body),
                text_editor(state.caiyun_api_key.clone())
                    .id("CaiyunKeyBox")
                    .placeholder("Enter your Caiyun API key")
                    .max_height(36)
                    .on_input(Message::CaiyunApiKeyChanged)
                    .into_view(),
                "CaiyunKeyRevealButton",
                "Reveal API key",
            ),
            styled_text(
                "Get your API key from fanyi.caiyunapp.com.",
                TextStyle::Caption,
            ),
            button("Test")
                .id("TestCaiyunButton")
                .height(Length::Fixed(29))
                .on_press(Message::TestCaiyun)
                .into_view(),
        ],
    )
}

fn niu_trans_service_expander(state: &SettingsState) -> View<Message> {
    service_expander(
        state,
        "niutrans",
        service_configuration_expanded(state, "niutrans"),
        "NiuTransServiceExpander",
        "NiuTrans",
        "NiuTransStatusText",
        state.niu_trans_status.clone(),
        "settings.services.niutrans.content",
        vec![
            secret_field_stack(
                "NiuTransKeyField",
                350,
                styled_text_id("NiuTransKeyHeaderText", "API Key", TextStyle::Body),
                text_editor(state.niu_trans_api_key.clone())
                    .id("NiuTransKeyBox")
                    .placeholder("Enter your NiuTrans API key")
                    .max_height(36)
                    .on_input(Message::NiuTransApiKeyChanged)
                    .into_view(),
                "NiuTransKeyRevealButton",
                "Reveal API key",
            ),
            styled_text(
                "NiuTrans supports 450+ language pairs. Get your API key from niutrans.com.",
                TextStyle::Caption,
            ),
            button("Test")
                .id("TestNiuTransButton")
                .height(Length::Fixed(29))
                .on_press(Message::TestNiuTrans)
                .into_view(),
        ],
    )
}

fn youdao_service_expander(state: &SettingsState) -> View<Message> {
    service_expander(
        state,
        "youdao",
        service_configuration_expanded(state, "youdao"),
        "YoudaoServiceExpander",
        "Youdao",
        "YoudaoStatusText",
        state.youdao_status.clone(),
        "settings.services.youdao.content",
        vec![
            secret_field_stack(
                "YoudaoAppKeyField",
                350,
                styled_text_id("YoudaoAppKeyHeaderText", "App Key", TextStyle::Body),
                text_editor(state.youdao_app_key.clone())
                    .id("YoudaoAppKeyBox")
                    .placeholder("Enter your Youdao App Key")
                    .max_height(36)
                    .on_input(Message::YoudaoAppKeyChanged)
                    .into_view(),
                "YoudaoAppKeyRevealButton",
                "Reveal app key",
            ),
            secret_field_stack(
                "YoudaoAppSecretField",
                350,
                styled_text_id("YoudaoAppSecretHeaderText", "App Secret", TextStyle::Body),
                text_editor(state.youdao_app_secret.clone())
                    .id("YoudaoAppSecretBox")
                    .placeholder("Enter your Youdao App Secret")
                    .max_height(36)
                    .on_input(Message::YoudaoAppSecretChanged)
                    .into_view(),
                "YoudaoAppSecretRevealButton",
                "Reveal app secret",
            ),
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
                    .height(Length::Fixed(29))
                    .on_press(Message::TestYoudao)
                    .into_view(),
        ],
    )
}

fn volcano_service_expander(state: &SettingsState) -> View<Message> {
    service_expander(
        state,
        "volcano",
        service_configuration_expanded(state, "volcano"),
        "VolcanoServiceExpander",
        "Volcano",
        "VolcanoStatusText",
        state.volcano_status.clone(),
        "settings.services.volcano.content",
        vec![
            secret_field_stack(
                "VolcanoAccessKeyIdField",
                350,
                styled_text_id(
                    "VolcanoAccessKeyIdHeaderText",
                    "Access Key ID",
                    TextStyle::Body,
                ),
                text_editor(state.volcano_access_key_id.clone())
                    .id("VolcanoAccessKeyIdBox")
                    .placeholder("Enter your Volcano Access Key ID")
                    .max_height(36)
                    .on_input(Message::VolcanoAccessKeyIdChanged)
                    .into_view(),
                "VolcanoAccessKeyIdRevealButton",
                "Reveal access key",
            ),
            secret_field_stack(
                "VolcanoSecretAccessKeyField",
                350,
                styled_text_id(
                    "VolcanoSecretAccessKeyHeaderText",
                    "Secret Access Key",
                    TextStyle::Body,
                ),
                text_editor(state.volcano_secret_access_key.clone())
                    .id("VolcanoSecretAccessKeyBox")
                    .placeholder("Enter your Volcano Secret Access Key")
                    .max_height(36)
                    .on_input(Message::VolcanoSecretAccessKeyChanged)
                    .into_view(),
                "VolcanoSecretAccessKeyRevealButton",
                "Reveal secret key",
            ),
                styled_text(
                    "Volcano translation uses signed OpenAPI requests from translate.volcengineapi.com.",
                    TextStyle::Caption,
                ),
                button("Test")
                    .id("TestVolcanoButton")
                    .height(Length::Fixed(29))
                    .on_press(Message::TestVolcano)
                    .into_view(),
        ],
    )
}

fn deepl_service_expander(state: &SettingsState) -> View<Message> {
    service_expander(
        state,
        "deepl",
        service_configuration_expanded(state, "deepl"),
        "DeepLServiceExpander",
        "DeepL",
        "DeepLStatusText",
        "",
        "settings.services.deepl.content",
        vec![
            secret_field_stack(
                "DeepLKeyField",
                350,
                styled_text_id(
                    "DeepLKeyHeaderText",
                    tr("settings.services.deepl.api_key_optional", "API Key (Optional)"),
                    TextStyle::Body,
                ),
                text_editor(state.deepl_api_key.clone())
                    .id("DeepLKeyBox")
                    .placeholder("Enter your DeepL API key")
                    .max_height(36)
                    .on_input(Message::DeepLApiKeyChanged)
                    .into_view(),
                "DeepLKeyRevealButton",
                "Reveal API key",
            ),
            settings_checkbox_row(
                "DeepLFreeCheckRow",
                checkbox(
                    tr(
                        "settings.services.deepl.free_api",
                        "Use Free API (no API key required for web translation)",
                    ),
                    state.deepl_use_free_api,
                )
                .id("DeepLFreeCheck")
                .enabled(!state.deepl_use_quality_optimized)
                .on_toggle(Message::ToggleDeepLUseFreeApi),
            )
            .into_view(),
            settings_checkbox_row(
                "DeepLQualityCheckRow",
                checkbox(
                    tr(
                        "settings.services.deepl.quality_model",
                        "Use quality-optimized model (API only; slower, higher quality)",
                    ),
                    state.deepl_use_quality_optimized,
                )
                .id("DeepLQualityCheck")
                .on_toggle(Message::ToggleDeepLUseQualityOptimized),
            )
            .into_view(),
            styled_text_id(
                "DeepLDescriptionText",
                tr(
                    "settings.services.deepl.description",
                    "Leave the API key empty to use free web translation. Pro API keys have higher limits.",
                ),
                TextStyle::Caption,
            ),
            button(tr("settings.services.test", "Test"))
                .id("TestDeepLButton")
                .height(Length::Fixed(29))
                .on_press(Message::Translate)
                .into_view(),
        ],
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

fn mdx_dictionary_summary_locale(state: &SettingsState, locale: &str) -> String {
    match state.imported_mdx_dictionaries.len() {
        0 => tr_locale(
            locale,
            "settings.services.mdx.none",
            "No MDX dictionaries imported",
        ),
        1 => tr_locale(
            locale,
            "settings.services.mdx.one",
            "1 MDX dictionary imported",
        ),
        count => tr_count_locale(
            locale,
            "settings.services.mdx.many",
            "{count} MDX dictionaries imported",
            count,
        ),
    }
}

fn settings_views_content(state: &SettingsState, locale: &str) -> View<Message> {
    column((
        styled_text_id(
            "WindowResultsHeaderText",
            tr_locale(locale, "settings.views.title", "Window Results"),
            TextStyle::Subtitle,
        ),
        styled_text_id(
            "WindowResultsDescriptionText",
            tr_locale(
                locale,
                "settings.views.description",
                "Choose which results appear in each window and whether each result queries automatically.",
            ),
            TextStyle::Caption,
        ),
        settings_compact_panel(
            "WindowResultsSection",
            vec![
                settings_view_window_results_section(
                    tr_locale(locale, "settings.views.main_window", "Main Window"),
                    "settings.views.main",
                    "MainWindowReorderModeButton",
                    "main",
                    QuickTranslateSurface::Main,
                    state.main_window_reorder_mode,
                    &state.main_window_services,
                    locale,
                ),
                settings_divider("settings.views.main.divider"),
                settings_view_window_results_section(
                    tr_locale(locale, "settings.views.mini_window", "Mini Window"),
                    "settings.views.mini",
                    "MiniWindowReorderModeButton",
                    "mini",
                    QuickTranslateSurface::Mini,
                    state.mini_window_reorder_mode,
                    &state.mini_window_services,
                    locale,
                ),
                settings_divider("settings.views.mini.divider"),
                settings_view_window_results_section(
                    tr_locale(locale, "settings.views.fixed_window", "Fixed Window"),
                    "settings.views.fixed",
                    "FixedWindowReorderModeButton",
                    "fixed",
                    QuickTranslateSurface::Fixed,
                    state.fixed_window_reorder_mode,
                    &state.fixed_window_services,
                    locale,
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
    title: String,
    section_id: &'static str,
    reorder_button_id: &'static str,
    control_prefix: &'static str,
    surface: QuickTranslateSurface,
    reorder_mode: bool,
    services: &[WindowServiceSetting],
    locale: &str,
) -> View<Message> {
    column((
        row((
            column((styled_text(title, TextStyle::BodyStrong),))
                .id(format!("{section_id}.header_text"))
                .width(Length::Fill),
            button(if reorder_mode {
                tr_locale(locale, "settings.views.done", "Done")
            } else {
                tr_locale(locale, "settings.views.reorder", "Reorder")
            })
            .id(reorder_button_id)
            .on_press(Message::ToggleWindowReorderMode(surface)),
        ))
        .id(format!("{section_id}.header"))
        .spacing(12)
        .align(Alignment::Center)
        .width(Length::Fill),
        window_service_rows(control_prefix, surface, services, reorder_mode, locale),
    ))
    .id(section_id)
    .spacing(6)
    .width(Length::Fill)
    .into_view()
}

fn window_service_rows(
    control_prefix: &'static str,
    surface: QuickTranslateSurface,
    services: &[WindowServiceSetting],
    reorder_mode: bool,
    locale: &str,
) -> View<Message> {
    let rows = services
        .iter()
        .enumerate()
        .map(|(index, service)| {
            let next_service = services.get(index + 1);
            window_service_row(
                control_prefix,
                surface,
                service,
                next_service,
                index,
                services.len(),
                reorder_mode,
                locale,
            )
        })
        .collect::<Vec<_>>();

    column(rows)
        .id(format!("{control_prefix}.service_list"))
        .spacing(0)
        .width(Length::Fill)
        .into_view()
}

fn window_service_row(
    control_prefix: &'static str,
    surface: QuickTranslateSurface,
    service: &WindowServiceSetting,
    next_service: Option<&WindowServiceSetting>,
    index: usize,
    service_count: usize,
    reorder_mode: bool,
    locale: &str,
) -> View<Message> {
    let control_id = service_control_id(&service.service_id);
    let mut trailing: Vec<View<Message>> = Vec::new();
    let service_id = service.service_id.clone();
    let display_label = window_service_display_label(service);
    let enabled_checkbox = checkbox(display_label.clone(), service.enabled)
        .id(format!("{control_prefix}.{control_id}.enabled"))
        .label_italic(!service.configured)
        .a11y(A11yHint::named(format!(
            "{} {}",
            display_label,
            tr_locale(locale, "settings.views.enabled", "enabled")
        )))
        .on_toggle(move |enabled| {
            Message::ToggleWindowService(surface, service_id.clone(), enabled)
        });

    if service.enabled {
        let service_id = service.service_id.clone();
        trailing.push(
            toggle_switch(
                tr_locale(locale, "settings.views.auto", "Auto"),
                service.enabled_query,
            )
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

    let bottom_gap = service_row_bottom_gap(service, next_service);

    row((
        column((enabled_checkbox,))
            .id(format!("{control_prefix}.service.{control_id}.text"))
            .width(Length::Fill)
            .margin(Edges {
                left: 2,
                ..Edges::ZERO
            }),
        row(trailing)
            .id(format!("{control_prefix}.service.{control_id}.controls"))
            .spacing(8)
            .align(Alignment::Center),
    ))
    .id(format!("{control_prefix}.service.{control_id}"))
    .spacing(12)
    .align(Alignment::Center)
    .width(Length::Fill)
    .height(Length::Fixed(28))
    .margin(Edges {
        bottom: bottom_gap,
        ..Edges::ZERO
    })
    .into_view()
}

fn service_row_bottom_gap(
    service: &WindowServiceSetting,
    next_service: Option<&WindowServiceSetting>,
) -> u16 {
    let Some(next_service) = next_service else {
        return 0;
    };

    match (service.enabled, next_service.enabled) {
        (true, true) => 16,
        (false, false) => 4,
        _ => 10,
    }
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

fn settings_divider(id: impl Into<String>) -> View<Message> {
    column(Vec::<View<Message>>::new())
        .id(id)
        .tw("bg-border w-full h-[1px]")
        .into_view()
}

fn window_service_display_label(service: &WindowServiceSetting) -> String {
    if service.display_name.starts_with('\u{1f4d6}')
        || service.display_name.starts_with('\u{1f4da}')
    {
        return service.display_name.clone();
    }

    if service.service_id == "google_web" || service.service_id == "linguee" {
        format!("\u{1f4d6} {}", service.display_name)
    } else if service.service_id.starts_with("mdx::") {
        format!("\u{1f4da} {}", service.display_name)
    } else {
        service.display_name.clone()
    }
}

fn settings_hotkeys_content(state: &SettingsState, locale: &str) -> View<Message> {
    column((
        row((
            styled_text_id(
                "HotkeysHeaderText",
                tr_locale(locale, "settings.hotkeys.title", "Hotkeys"),
                TextStyle::Subtitle,
            ),
            button("")
                .id("HotkeysHelpIcon")
                .icon(icon::help())
                .icon_only()
                .width(Length::Fixed(20))
                .height(Length::Fixed(20))
                .tooltip(tr_locale(
                    locale,
                    "settings.hotkeys.help",
                    "Hotkey changes apply after restart",
                ))
                .a11y(A11yHint::named(tr_locale(
                    locale,
                    "settings.hotkeys.help",
                    "Hotkey changes apply after restart",
                ))),
        ))
        .id("settings.hotkeys.header")
        .spacing(8)
        .align(Alignment::Center),
        card("").id("settings.hotkeys.card").content(
            column((
                hotkey_row(
                    locale,
                    "settings.hotkeys.show_window.label",
                    "Show Window",
                    "settings.hotkeys.show_window",
                    "ShowHotkeyBox",
                    "ShowHotkeyEnabledToggle",
                    "Ctrl+Alt+T",
                    HOTKEY_SHOW_MAIN,
                    &state.show_main_hotkey,
                ),
                hotkey_row(
                    locale,
                    "settings.hotkeys.translate_selection.label",
                    "Translate Selection",
                    "settings.hotkeys.translate_clipboard",
                    "TranslateHotkeyBox",
                    "TranslateHotkeyEnabledToggle",
                    "Ctrl+Alt+D",
                    HOTKEY_TRANSLATE_CLIPBOARD,
                    &state.translate_clipboard_hotkey,
                ),
                hotkey_row(
                    locale,
                    "settings.hotkeys.show_mini.label",
                    "Show Mini Window",
                    "settings.hotkeys.show_mini",
                    "ShowMiniHotkeyBox",
                    "ShowMiniHotkeyEnabledToggle",
                    "Ctrl+Alt+M",
                    HOTKEY_SHOW_MINI,
                    &state.show_mini_hotkey,
                ),
                hotkey_row(
                    locale,
                    "settings.hotkeys.show_fixed.label",
                    "Show Fixed Window",
                    "settings.hotkeys.show_fixed",
                    "ShowFixedHotkeyBox",
                    "ShowFixedHotkeyEnabledToggle",
                    "Ctrl+Alt+F",
                    HOTKEY_SHOW_FIXED,
                    &state.show_fixed_hotkey,
                ),
                hotkey_row(
                    locale,
                    "settings.hotkeys.ocr_translate.label",
                    "OCR Screenshot Translate",
                    "settings.hotkeys.ocr_translate",
                    "OcrTranslateHotkeyBox",
                    "OcrTranslateHotkeyEnabledToggle",
                    "Ctrl+Alt+S",
                    HOTKEY_OCR_TRANSLATE,
                    &state.ocr_translate_hotkey,
                ),
                hotkey_row(
                    locale,
                    "settings.hotkeys.silent_ocr.label",
                    "Silent OCR",
                    "settings.hotkeys.silent_ocr",
                    "SilentOcrHotkeyBox",
                    "SilentOcrHotkeyEnabledToggle",
                    "Ctrl+Alt+Shift+S",
                    HOTKEY_SILENT_OCR,
                    &state.silent_ocr_hotkey,
                ),
                styled_text_id(
                    "HotkeysDescriptionText",
                    tr_locale(
                        locale,
                        "settings.hotkeys.note",
                        "Note: Restart app to apply hotkey changes. Toggle hotkeys use the same key with Shift added (e.g., Ctrl+Alt+Shift+M).",
                    ),
                    TextStyle::Caption,
                ),
            ))
            .id("settings.hotkeys.card.content")
            .spacing(24)
            .margin(Edges {
                top: 4,
                right: 4,
                bottom: 4,
                left: 4,
            }),
        ),
    ))
    .id("settings.hotkeys")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn hotkey_row(
    locale: &str,
    title_key: &'static str,
    fallback_title: &'static str,
    row_id: &'static str,
    box_id: &'static str,
    toggle_id: &'static str,
    placeholder: &'static str,
    hotkey_id: &'static str,
    setting: &HotkeySetting,
) -> View<Message> {
    let toggle_hotkey_id = hotkey_id.to_string();
    let editor_hotkey_id = hotkey_id.to_string();
    let label = tr_locale(locale, title_key, fallback_title);

    row((
        column((
            styled_text_id(format!("{box_id}.header"), label, TextStyle::Body),
            row((text_editor(setting.shortcut.clone())
                .id(box_id)
                .placeholder(placeholder)
                .max_height(36)
                .on_input(move |value| {
                    Message::HotkeyShortcutChanged(editor_hotkey_id.clone(), value)
                }),))
            .id(format!("{box_id}.field"))
            .width(Length::Fixed(200)),
        ))
        .id(format!("{box_id}.group"))
        .spacing(4)
        .width(Length::Fixed(200)),
        toggle_switch("", setting.enabled)
            .id(toggle_id)
            .on_toggle(move |value| Message::ToggleHotkey(toggle_hotkey_id.clone(), value)),
    ))
    .id(row_id)
    .spacing(12)
    .align(Alignment::End)
    .into_view()
}

fn settings_panel(id: impl Into<String>, children: Vec<View<Message>>) -> View<Message> {
    column(children)
        .id(id)
        .tw("surface-card rounded-lg border p-6 gap-4 w-full")
        .into_view()
}

fn settings_compact_panel(id: impl Into<String>, children: Vec<View<Message>>) -> View<Message> {
    column(children)
        .id(id)
        .tw("surface-card rounded-lg border p-3 gap-3 w-full")
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

fn settings_language_content(state: &SettingsState, locale: &str) -> View<Message> {
    let selected_count = state.selected_languages.len();
    let language_rows = TRANSLATION_LANGUAGE_IDS
        .into_iter()
        .map(|id| {
            let selected = state
                .selected_languages
                .iter()
                .any(|language| language == id)
                || id == "en";
            let enabled = id != "en" && (!selected || selected_count > 2);
            row((checkbox(settings_language_label(locale, id), selected)
                .id(format!("settings.language.selected.{id}.checkbox"))
                .enabled(enabled)
                .on_toggle(move |value| Message::ToggleSelectedLanguage(id.to_string(), value)),))
            .id(format!("settings.language.selected.{id}"))
            .width(Length::Fixed(180))
            .height(Length::Fixed(32))
            .align(Alignment::Center)
            .into_view()
        })
        .collect::<Vec<_>>();

    column((
        styled_text_id(
            "LanguagePreferencesHeaderText",
            tr_locale(
                locale,
                "settings.language.preferences",
                "Language Preferences",
            ),
            TextStyle::Subtitle,
        ),
        card("").id("settings.language.preferences.card").content(
            column((
                language_combo_field(
                    "settings.language.first",
                    "FirstLanguageLabelText",
                    tr_locale(locale, "settings.language.first", "First Language"),
                    combo_box(settings_language_items(locale, false))
                        .id("FirstLanguageCombo")
                        .selected(settings_language_selected(&state.first_language))
                        .width(Length::Fixed(250))
                        .on_change(Message::FirstLanguageChanged),
                ),
                language_combo_field(
                    "settings.language.second",
                    "SecondLanguageLabelText",
                    tr_locale(
                        locale,
                        "settings.language.second",
                        "Second Language",
                    ),
                    combo_box(settings_language_items(locale, false))
                        .id("SecondLanguageCombo")
                        .selected(settings_language_selected(&state.second_language))
                        .width(Length::Fixed(250))
                        .on_change(Message::SecondLanguageChanged),
                ),
                styled_text_id(
                    "LanguagePreferenceRuleDescriptionText",
                    tr_locale(
                        locale,
                        "settings.language.preference_rule.description",
                        "When detected language matches your First Language, translation target will be your Second Language, and vice versa.",
                    ),
                    TextStyle::Caption,
                ),
                column((
                    styled_text_id(
                        "AutoSelectTargetLabelText",
                        tr_locale(
                            locale,
                            "settings.language.auto_select_target.compact",
                            "Automatically select target language based on detected source",
                        ),
                        TextStyle::Body,
                    ),
                    toggle_switch(
                        tr_locale(locale, "settings.toggle.on", "On"),
                        state.auto_select_target_language,
                    )
                    .id("AutoSelectTargetToggle")
                    .on_toggle(Message::ToggleAutoSelectTargetLanguage),
                ))
                .id("settings.language.auto_select_target")
                .spacing(8)
                .align(Alignment::Start)
                .width(Length::Fill),
                language_combo_field(
                    "settings.language.display",
                    "UiLanguageLabelText",
                    tr_locale(locale, "settings.language.display", "Display language"),
                    combo_box(ui_language_items(locale))
                        .id("UILanguageCombo")
                        .selected(state.ui_language.clone())
                        .width(Length::Fixed(250))
                        .on_change(Message::UiLanguageChanged),
                ),
                styled_text_id(
                    "DisplayLanguageDescriptionText",
                    tr_locale(
                        locale,
                        "settings.language.display.description",
                        "Select the display language for the application interface. Restart required.",
                    ),
                    TextStyle::Caption,
                ),
            ))
            .id("settings.language.preferences.card.content")
            .spacing(16)
            .margin(Edges {
                top: 4,
                right: 4,
                bottom: 4,
                left: 4,
            }),
        ),
        expander(tr_locale(
            locale,
            "settings.language.translation_languages",
            "Available Languages",
        ))
        .id("settings.language.translation_languages")
        .expanded(state.translation_languages_expanded)
        .on_toggle(Message::ToggleTranslationLanguagesExpanded)
        .content(
            column((
                styled_text_id(
                    "AvailableLanguagesDescText",
                    tr_locale(
                        locale,
                        "settings.language.available.description",
                        "Select languages available in source/target pickers. At least 2 required.",
                    ),
                    TextStyle::Caption,
                ),
                wrap(language_rows)
                    .id("settings.language.selected_languages")
                    .max_columns(4)
                    .spacing(8)
                    .run_spacing(4),
                spacer()
                    .id("settings.language.selected_languages.bottom_spacer")
                    .height(Length::Fixed(10)),
            ))
            .id("settings.language.selected_languages.content")
            .spacing(8)
            .width(Length::Fill),
        ),
    ))
    .id("settings.language")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn language_combo_field(
    id: &'static str,
    label_id: &'static str,
    label: impl Into<String>,
    combo: impl IntoView<Message>,
) -> View<Message> {
    column((
        styled_text_id(label_id, label, TextStyle::Body),
        combo.into_view(),
    ))
    .id(id)
    .spacing(8)
    .align(Alignment::Start)
    .width(Length::Fill)
    .into_view()
}

fn settings_about_content(locale: &str) -> View<Message> {
    column((
        sized_styled_text_id(
            "AboutHeaderText",
            tr_locale(locale, "settings.about.title", "About"),
            TextStyle::Subtitle,
            Length::Fill,
            Length::Fixed(24),
        ),
        card("").id("settings.about.card").content(
            column((
                sized_styled_text_id(
                    "AboutAppNameText",
                    tr_locale(
                        locale,
                        "settings.about.app_name",
                        "Easydict for Windows ᵇᵉᵗᵃ",
                    ),
                    TextStyle::BodyStrong,
                    Length::Fill,
                    Length::Fixed(19),
                ),
                sized_styled_text_id(
                    "VersionText",
                    settings_version_text(locale),
                    TextStyle::Caption,
                    Length::Fill,
                    Length::Fixed(19),
                ),
                settings_link_button(SettingsLink::GitHubRepository, locale),
                settings_link_button(SettingsLink::IssueFeedback, locale),
                row((
                    sized_styled_text_id(
                        "AboutInspiredByText",
                        tr_locale(locale, "settings.about.inspired_by", "Inspired by"),
                        TextStyle::Caption,
                        Length::Shrink,
                        Length::Fixed(18),
                    ),
                    settings_link_button(SettingsLink::EasydictForMacOS, locale),
                ))
                .id("settings.about.inspired_by")
                .spacing(4)
                .align(Alignment::Center),
                sized_styled_text_id(
                    "LicenseText",
                    tr_locale(locale, "settings.about.license", "License: GPL-3.0"),
                    TextStyle::Caption,
                    Length::Shrink,
                    Length::Fixed(18),
                ),
            ))
            .id("settings.about.card.content")
            .spacing(8)
            .width(Length::Fill)
            .padding(4),
        ),
    ))
    .id("settings.about")
    .spacing(12)
    .width(Length::Fill)
    .into_view()
}

fn settings_version_text(locale: &str) -> String {
    tr_locale(locale, "settings.about.version", "Version {version}")
        .replace("{version}", &app_display_version())
}

fn app_display_version() -> String {
    ["EASYDICT_PREVIEW_APP_VERSION", "EASYDICT_APP_VERSION"]
        .into_iter()
        .find_map(|key| {
            std::env::var(key)
                .ok()
                .map(|value| value.trim().to_string())
                .filter(|value| !value.is_empty())
        })
        .unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_string())
}

fn settings_link_button(link: SettingsLink, locale: &str) -> View<Message> {
    let label = settings_link_label(link, locale);
    let (width, height) = settings_link_button_size(link, locale);
    let mut link_button = button(label)
        .id(link.id())
        .link()
        .tooltip(link.url())
        .width(Length::Fixed(width))
        .height(Length::Fixed(height));
    if link == SettingsLink::EasydictForMacOS {
        link_button = link_button.text_style(TextStyle::Caption);
    }
    link_button.on_press(Message::OpenSettingsLink(link))
}

fn settings_link_button_size(link: SettingsLink, locale: &str) -> (u16, u16) {
    match link {
        SettingsLink::GitHubRepository => (116, 21),
        SettingsLink::IssueFeedback if locale.eq_ignore_ascii_case("zh-CN") => (58, 21),
        SettingsLink::IssueFeedback => (94, 21),
        SettingsLink::EasydictForMacOS => (106, 18),
    }
}

fn settings_link_label(link: SettingsLink, locale: &str) -> String {
    match link {
        SettingsLink::GitHubRepository => tr_locale(
            locale,
            "settings.about.github",
            SettingsLink::GitHubRepository.label(),
        ),
        SettingsLink::IssueFeedback => tr_locale(
            locale,
            "settings.about.issue_feedback",
            SettingsLink::IssueFeedback.label(),
        ),
        SettingsLink::EasydictForMacOS => tr_locale(
            locale,
            "settings.about.mac",
            SettingsLink::EasydictForMacOS.label(),
        ),
    }
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

fn selected_floating_language_items(
    include_auto: bool,
    settings: &SettingsState,
) -> Vec<ComboBoxItem> {
    let mut items = Vec::new();
    if include_auto {
        items.push(ComboBoxItem::new(
            "auto",
            floating_language_label(&settings.ui_language, "auto"),
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
            .map(|id| ComboBoxItem::new(id, floating_language_label(&settings.ui_language, id))),
    );
    items
}

fn floating_language_label(locale: &str, id: &str) -> String {
    if id == "auto" {
        return tr_locale(locale, "main.auto_detect", "Auto Detect");
    }
    if !locale_is_zh(locale) {
        return language_label(id);
    }

    match id {
        "ar" => "阿拉伯语",
        "bg" => "保加利亚语",
        "bn" => "孟加拉语",
        "cs" => "捷克语",
        "da" => "丹麦语",
        "de" => "德语",
        "el" => "希腊语",
        "en" => "英语",
        "es" => "西班牙语",
        "et" => "爱沙尼亚语",
        "fa" => "波斯语",
        "fi" => "芬兰语",
        "fr" => "法语",
        "he" => "希伯来语",
        "hi" => "印地语",
        "hu" => "匈牙利语",
        "id" => "印尼语",
        "it" => "意大利语",
        "ja" => "日语",
        "ko" => "韩语",
        "lt" => "立陶宛语",
        "lv" => "拉脱维亚语",
        "ms" => "马来语",
        "nl" => "荷兰语",
        "no" => "挪威语",
        "pl" => "波兰语",
        "pt" => "葡萄牙语",
        "ro" => "罗马尼亚语",
        "ru" => "俄语",
        "sk" => "斯洛伐克语",
        "sl" => "斯洛文尼亚语",
        "sv" => "瑞典语",
        "ta" => "泰米尔语",
        "te" => "泰卢固语",
        "th" => "泰语",
        "tl" => "菲律宾语",
        "tr" => "土耳其语",
        "uk" => "乌克兰语",
        "ur" => "乌尔都语",
        "vi" => "越南语",
        "zh-Hans" => return tr_locale(locale, "main.target_zh_hans", "简体中文"),
        "zh-Hant" => "繁体中文",
        "zh-classical" => "文言文",
        _ => return language_label(id),
    }
    .to_string()
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

fn settings_language_items(locale: &str, include_auto: bool) -> Vec<ComboBoxItem> {
    let mut items = Vec::new();
    if include_auto {
        items.push(ComboBoxItem::new(
            "auto",
            tr_locale(locale, "main.auto_detect", "Auto Detect"),
        ));
    }
    items.extend(
        TRANSLATION_LANGUAGE_IDS
            .into_iter()
            .map(|id| ComboBoxItem::new(id, settings_language_label(locale, id))),
    );
    items
}

fn settings_language_selected(language_id: &str) -> String {
    match language_id.trim().to_ascii_lowercase().as_str() {
        "zh" | "zh-cn" => "zh-Hans".to_string(),
        "zh-tw" => "zh-Hant".to_string(),
        value => value.to_string(),
    }
}

fn ui_language_items(locale: &str) -> Vec<ComboBoxItem> {
    let labels = if locale_is_zh(locale) {
        [
            ("en-US", "English"),
            ("zh-CN", "简体中文"),
            ("zh-TW", "繁体中文"),
            ("ja-JP", "日语"),
            ("ko-KR", "韩语"),
            ("fr-FR", "法语"),
            ("de-DE", "德语"),
            ("vi-VN", "越南语"),
            ("th-TH", "泰语"),
            ("ar-SA", "阿拉伯语"),
            ("id-ID", "印尼语"),
            ("it-IT", "意大利语"),
            ("ms-MY", "马来语"),
            ("hi-IN", "印地语"),
            ("da-DK", "丹麦语"),
        ]
    } else {
        [
            ("en-US", "English"),
            ("zh-CN", "Chinese (Simplified)"),
            ("zh-TW", "Chinese (Traditional)"),
            ("ja-JP", "Japanese"),
            ("ko-KR", "Korean"),
            ("fr-FR", "French"),
            ("de-DE", "German"),
            ("vi-VN", "Vietnamese"),
            ("th-TH", "Thai"),
            ("ar-SA", "Arabic"),
            ("id-ID", "Indonesian"),
            ("it-IT", "Italian"),
            ("ms-MY", "Malay"),
            ("hi-IN", "Hindi"),
            ("da-DK", "Danish"),
        ]
    };

    labels
        .into_iter()
        .map(|(id, label)| ComboBoxItem::new(id, label))
        .collect()
}

fn language_item(id: &'static str) -> ComboBoxItem {
    ComboBoxItem::new(id, language_label(id))
}

fn settings_language_label(locale: &str, id: &str) -> String {
    if !locale_is_zh(locale) {
        return language_label(id);
    }

    match id {
        "ar" => "SA 阿拉伯语",
        "bg" => "BG 保加利亚语",
        "bn" => "BD 孟加拉语",
        "cs" => "CZ 捷克语",
        "da" => "DK 丹麦语",
        "de" => "DE 德语",
        "el" => "GR 希腊语",
        "en" => "US 英语",
        "es" => "ES 西班牙语",
        "et" => "EE 爱沙尼亚语",
        "fa" => "IR 波斯语",
        "fi" => "FI 芬兰语",
        "fr" => "FR 法语",
        "he" => "IL 希伯来语",
        "hi" => "IN 印地语",
        "hu" => "HU 匈牙利语",
        "id" => "ID 印尼语",
        "it" => "IT 意大利语",
        "ja" => "JP 日语",
        "ko" => "KR 韩语",
        "lt" => "LT 立陶宛语",
        "lv" => "LV 拉脱维亚语",
        "ms" => "MY 马来语",
        "nl" => "NL 荷兰语",
        "no" => "NO 挪威语",
        "pl" => "PL 波兰语",
        "pt" => "BR 葡萄牙语",
        "ro" => "RO 罗马尼亚语",
        "ru" => "RU 俄语",
        "sk" => "SK 斯洛伐克语",
        "sl" => "SI 斯洛文尼亚语",
        "sv" => "SE 瑞典语",
        "ta" => "IN 泰米尔语",
        "te" => "IN 泰卢固语",
        "th" => "TH 泰语",
        "tl" => "PH 菲律宾语",
        "tr" => "TR 土耳其语",
        "uk" => "UA 乌克兰语",
        "ur" => "PK 乌尔都语",
        "vi" => "VN 越南语",
        "zh-Hans" => "CN 简体中文",
        "zh-Hant" => "TW 繁体中文",
        "zh-classical" => "CN 文言文",
        _ => return language_label(id),
    }
    .to_string()
}

fn locale_is_zh(locale: &str) -> bool {
    locale
        .get(..2)
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case("zh"))
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

fn service_items() -> Vec<ComboBoxItem> {
    default_translation_service_descriptors()
        .into_iter()
        .filter(|descriptor| {
            !matches!(
                descriptor.kind,
                TranslationServiceKind::Dictionary | TranslationServiceKind::ImportedMdx
            )
        })
        .map(|descriptor| ComboBoxItem::new(descriptor.service_id, descriptor.display_name))
        .collect()
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
            description: "Uses GLM (Zhipu AI) or Groq free models. You can provide your own API key from open.bigmodel.cn (GLM) or console.groq.com (Groq).",
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
